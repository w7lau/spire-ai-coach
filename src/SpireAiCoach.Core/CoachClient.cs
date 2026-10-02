using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

public sealed record CallResult(Advice Advice, string Model, string? RequestId, string FinishReason,
    long ElapsedMs, string? Usage);

public sealed class CoachClient : IDisposable
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _http;
    public CoachClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<CallResult> AnalyzeAsync(CoachSettings settings, string key, CombatSnapshot snapshot,
        CancellationToken cancellationToken = default, CallDiagnostics? diagnostics = null, Action<string>? onContent = null)
    {
        settings.Validate();
        if (!snapshot.CanAdvise) throw new CoachException("phase", "请在自己的出牌阶段分析。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint());
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (key.Contains('\r') || key.Contains('\n')) throw new CoachException("configuration", "API Key 不能包含换行。");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        }
        var body = new Dictionary<string, object>
        {
            ["model"] = settings.Model.Trim(),
            ["messages"] = new[]
            {
                new { role = "system", content = PromptBuilder.SystemPrompt },
                new { role = "user", content = PromptBuilder.ContextPrompt(snapshot) },
                new { role = "user", content = PromptBuilder.UserPrompt(snapshot) }
            },
            ["stream"] = true
        };
        if (settings.IncludeStreamUsage) body["stream_options"] = new { include_usage = true };
        var requestBody = Wire.Serialize(body);
        diagnostics ??= new();
        diagnostics.SnapshotId = snapshot.Fingerprint();
        diagnostics.RequestBody = requestBody;
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            diagnostics.HttpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // Never display the remote body: proxies sometimes echo request headers and secrets.
                throw new CoachException(status is 401 or 403 ? "authentication" : "http",
                    $"AI 服务返回 HTTP {status}。请检查地址、模型、额度及密钥；本次未自动重试。" +
                    (status == 400 && settings.IncludeStreamUsage ? "若接口不支持流式用量统计，可在设置中关闭该项。" : ""));
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new CoachException("response_size", "AI 回复超过 2 MiB，未显示。");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var captured = new CapturedResponseStream(stream, MaxResponseBytes);
            try
            {
                ProviderReply reply;
                if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.ResponseFormat = "sse";
                    reply = await StreamingResponse.ReadAsync(captured, diagnostics, onContent, timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    // Some compatible gateways ignore stream=true. Consume this same response once;
                    // never send a fallback request or pretend it arrived incrementally.
                    diagnostics.ResponseFormat = "json";
                    using var memory = new MemoryStream();
                    await captured.CopyToAsync(memory, timeout.Token).ConfigureAwait(false);
                    reply = ReadJson(memory.ToArray(), diagnostics);
                    onContent?.Invoke(reply.Content);
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(reply.Content)) throw new CoachException("empty_response", "AI 没有返回可显示的建议。");
                var advice = AdviceContract.Parse(reply.Content, snapshot);
                diagnostics.Outcome = "validated";
                return new(advice, reply.Model ?? settings.Model, reply.Id, reply.Finish, timer.ElapsedMilliseconds, reply.Usage);
            }
            finally { diagnostics.ResponseBody = captured.Text; }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostics.Outcome = "timeout";
            diagnostics.ErrorDetail = "AI 请求超时；可重新分析，未自动重试。";
            throw new CoachException("timeout", diagnostics.ErrorDetail);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            diagnostics.Outcome = "transport";
            diagnostics.ErrorDetail = "AI 服务连接失败或传输中断；已保留收到的内容，未自动重试。";
            throw new CoachException("transport", diagnostics.ErrorDetail);
        }
        catch (DecoderFallbackException)
        {
            diagnostics.Outcome = "provider_encoding";
            diagnostics.ErrorDetail = "AI 流式回复不是有效 UTF-8，已停止接收。";
            throw new CoachException("provider_encoding", diagnostics.ErrorDetail);
        }
        catch (OperationCanceledException) { diagnostics.Outcome = "cancelled"; throw; }
        catch (CoachException ex) { diagnostics.Outcome = ex.Category; diagnostics.ErrorDetail = ex.Message; throw; }
        finally { diagnostics.ElapsedMs = timer.ElapsedMilliseconds; }
    }

    private static ProviderReply ReadJson(byte[] bytes, CallDiagnostics diagnostics)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new CoachException("provider_json", "服务返回的内容不是有效的接口 JSON。"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usageValue))
                diagnostics.UsageJson = usageValue.GetRawText();
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new CoachException("provider_schema", "接口回复缺少 choices，当前仅支持 Chat Completions 兼容接口。");
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object) throw new CoachException("provider_schema", "接口回复 choices 类型有误。");
            var finish = GetString(choice, "finish_reason") ?? "unknown";
            var content = choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                ? GetString(message, "content") : null;
            diagnostics.ProviderRequestId = GetString(root, "id");
            diagnostics.FinishReason = finish;
            diagnostics.AssistantContent = content;
            if (finish == "length") throw new CoachException("truncated", "AI 回复被服务端截断，请调整服务端设置后重新分析。");
            if (finish != "stop") throw new CoachException("finish_reason", $"AI 未正常完成回复（{SafeTag(finish)}）。");
            return new(content ?? "", GetString(root, "model"), GetString(root, "id"), finish,
                root.TryGetProperty("usage", out var usage) ? usage.GetRawText() : null);
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    private static string SafeTag(string value) => new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(32).ToArray());
    public void Dispose() => _http.Dispose();
}
