using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

internal sealed record ProviderReply(string Content, string? Model, string? Id, string Finish, string? Usage);

// StreamReader handles UTF-8 across byte boundaries and all SSE line endings. The underlying
// capture enforces a byte limit even for a malicious/unbounded line without a newline.
internal static class StreamingResponse
{
    public static async Task<ProviderReply> ReadAsync(Stream stream, CallDiagnostics trace,
        Action<string>? onContent, CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var data = new StringBuilder();
        var content = new StringBuilder();
        var publishClock = System.Diagnostics.Stopwatch.StartNew();
        int publishedLength = 0;
        string? model = null, usage = null;
        string eventType = "";
        bool done = false;
        bool firstLine = true;
        try
        {
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } receivedLine)
            {
                var line = firstLine ? receivedLine.TrimStart('\uFEFF') : receivedLine;
                firstLine = false;
                token.ThrowIfCancellationRequested();
                if (line.Length != 0)
                {
                    if (line[0] == ':') continue; // Heartbeat/comment.
                    var colon = line.IndexOf(':');
                    var field = colon < 0 ? line : line[..colon];
                    var value = colon < 0 ? "" : line[(colon + 1)..];
                    if (value.StartsWith(' ')) value = value[1..];
                    if (field == "data") data.Append(value).Append('\n');
                    else if (field == "event") eventType = value;
                    continue;
                }
                if (eventType == "error") throw new CoachException("provider_error", "AI 服务在流式传输中返回错误，已保留接收记录。");
                eventType = "";
                if (data.Length == 0) continue;
                var payload = data.ToString(0, data.Length - 1);
                data.Clear();
                if (payload.Trim() == "[DONE]") { done = true; break; }
                JsonDocument document;
                try { document = JsonDocument.Parse(payload); }
                catch (JsonException) { throw new CoachException("provider_json", "AI 流式事件不是有效 JSON，已保留接收记录。"); }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw Schema();
                    if (root.TryGetProperty("error", out _))
                        throw new CoachException("provider_error", "AI 服务在流式传输中返回错误，已保留接收记录。");
                    trace.ProviderRequestId ??= Text(root, "id");
                    model = Text(root, "model") ?? model;
                    if (root.TryGetProperty("usage", out var usageValue) && usageValue.ValueKind != JsonValueKind.Null)
                        usage = usageValue.GetRawText();
                    if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw Schema();
                    bool seen = false;
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.ValueKind != JsonValueKind.Object) throw Schema();
                        if (choice.TryGetProperty("index", out var index))
                        {
                            if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var number)) throw Schema();
                            if (number != 0) continue;
                        }
                        else if (choices.GetArrayLength() != 1) throw Schema();
                        if (seen) throw Schema();
                        seen = true;
                        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) throw Schema();
                        if (delta.TryGetProperty("content", out var fragment) && fragment.ValueKind != JsonValueKind.Null)
                        {
                            if (fragment.ValueKind != JsonValueKind.String) throw Schema();
                            var text = fragment.GetString()!;
                            if (text.Length > 0)
                            {
                                if (trace.FinishReason != null) throw Schema();
                                content.Append(text);
                                if (onContent != null && (publishedLength == 0 || publishClock.ElapsedMilliseconds >= 100))
                                {
                                    onContent(content.ToString());
                                    publishedLength = content.Length;
                                    publishClock.Restart();
                                }
                            }
                        }
                        var finish = Text(choice, "finish_reason");
                        if (finish != null)
                        {
                            if (trace.FinishReason != null && trace.FinishReason != finish) throw Schema();
                            trace.FinishReason = finish;
                            if (finish == "length") throw new CoachException("truncated", "AI 回复被服务端截断，已保留接收到的正文。");
                            if (finish != "stop") throw new CoachException("finish_reason", "AI 未正常完成流式回复，详情见诊断记录。");
                        }
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            // A fully framed finish=stop plus clean EOF is supported for gateways omitting [DONE].
            // EOF in an unfinished event or without a finish reason is never a successful response.
            if (trace.FinishReason != "stop" || (!done && data.Length != 0))
                throw new CoachException("stream_incomplete", "AI 流式回复未完整结束，已保留部分内容；请重新分析。");
            trace.StreamCompleted = true;
            if (onContent != null && publishedLength != content.Length) onContent(content.ToString());
            return new(content.ToString(), model, trace.ProviderRequestId, trace.FinishReason, usage);
        }
        finally
        {
            trace.AssistantContent = content.Length == 0 ? null : content.ToString();
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static CoachException Schema() => new("provider_schema", "AI 流式事件结构有误，当前仅支持 Chat Completions 兼容接口。");
}

internal sealed class CapturedResponseStream(Stream source, int limit) : Stream
{
    private readonly MemoryStream _capture = new();
    public string Text => Encoding.UTF8.GetString(_capture.ToArray());
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        int count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
        if (_capture.Length + count > limit) throw new CoachException("response_size", "AI 回复超过 2 MiB，已停止接收。");
        _capture.Write(buffer.Span[..count]);
        return count;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _capture.Dispose(); base.Dispose(disposing); }
}
