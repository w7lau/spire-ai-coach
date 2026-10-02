using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

// Accept only the already-redacted persisted record. Decode known envelopes, never unescape
// arbitrary text with replacements (which would corrupt literal slashes, quotes and newlines).
public static class DiagnosticDisplay
{
    public static string Format(string redactedJson)
    {
        using var document = JsonDocument.Parse(redactedJson);
        var root = document.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        var output = new StringBuilder();
        output.AppendLine("AI 原始正文（已隐藏本次密钥）：");
        output.AppendLine(Text("assistant_content") ?? "未取得 AI 正文。");
        output.AppendLine("\n发送给模型的消息内容（已解开 HTTP 和日志外层 JSON）：");
        var requestBody = Text("request_body");
        if (requestBody != null)
        {
            try
            {
                using var request = JsonDocument.Parse(requestBody);
                if (request.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                    foreach (var message in messages.EnumerateArray())
                    {
                        output.AppendLine("\n[" + message.GetProperty("role").GetString() + "]");
                        output.AppendLine(message.GetProperty("content").GetString());
                    }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            { output.AppendLine("请求记录格式无法展开，请查看完整诊断文件。"); }
        }
        else output.AppendLine("尚未生成请求。");
        output.AppendLine("\n用量：" + TokenUsage.Read(Text("usage_json")).Display());
        output.AppendLine("结束原因：" + (Text("finish_reason") ?? "未知"));
        if (Text("error_detail") is { } error) output.AppendLine("错误详情：" + error);
        output.AppendLine("\n复制完整诊断或打开文件可查看原始 HTTP / SSE 记录；日志外层转义不是额外的模型输入。");
        return output.ToString();
    }
}
