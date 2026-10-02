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
        CancellationToken cancellationToken = default, CallDiagnostics? diagnostics = null)
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
        var requestBody = Wire.Serialize(new
        {
            model = settings.Model.Trim(),
            messages = new[]
            {
                new { role = "system", content = PromptBuilder.SystemPrompt },
                new { role = "user", content = PromptBuilder.UserPrompt(snapshot) }
            },
            stream = false
        });
        diagnostics ??= new();
        diagnostics.SnapshotId = snapshot.Fingerprint();
        diagnostics.RequestBody = requestBody;
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
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
                    $"AI 服务返回 HTTP {status}。请检查地址、模型、额度及密钥；本次未自动重试。");
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new CoachException("response_size", "AI 回复超过 2 MiB，未显示。");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (memory.Length + count > MaxResponseBytes) throw new CoachException("response_size", "AI 回复超过 2 MiB，未显示。");
                memory.Write(buffer, 0, count);
            }
            JsonDocument document;
            diagnostics.ResponseBody = Encoding.UTF8.GetString(memory.ToArray());
            try { document = JsonDocument.Parse(memory.ToArray()); }
            catch (JsonException) { throw new CoachException("provider_json", "服务返回的内容不是有效的接口 JSON。"); }
            using (document)
            {
                var root = document.RootElement;
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
                if (string.IsNullOrWhiteSpace(content)) throw new CoachException("empty_response", "AI 没有返回可显示的建议。");
                var advice = AdviceContract.Parse(content, snapshot);
                diagnostics.Outcome = "validated";
                return new(advice, GetString(root, "model") ?? settings.Model, GetString(root, "id"), finish,
                    timer.ElapsedMilliseconds, root.TryGetProperty("usage", out var usage) ? usage.GetRawText() : null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostics.Outcome = "timeout";
            throw new CoachException("timeout", "AI 请求超时；可重新分析，未自动重试。");
        }
        catch (HttpRequestException)
        {
            diagnostics.Outcome = "transport";
            throw new CoachException("transport", "无法连接 AI 服务，请检查网络和 API 地址。");
        }
        catch (OperationCanceledException) { diagnostics.Outcome = "cancelled"; throw; }
        catch (CoachException ex) { diagnostics.Outcome = ex.Category; diagnostics.ErrorDetail = ex.Message; throw; }
        finally { diagnostics.ElapsedMs = timer.ElapsedMilliseconds; }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    private static string SafeTag(string value) => new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(32).ToArray());
    public void Dispose() => _http.Dispose();
}
