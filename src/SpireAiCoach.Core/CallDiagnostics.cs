using System.Text.Json;

namespace SpireAiCoach.Core;

// One frozen call record; never receives headers or the endpoint URL.
public sealed class CallDiagnostics
{
    public string CallId { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public string PromptVersion { get; } = PromptBuilder.Version;
    public string SystemPromptHash { get; } = PromptBuilder.SystemPromptHash;
    public string? SnapshotId { get; set; }
    public string GuidanceScope => "current_turn";
    public string? RequestBody { get; set; }
    public int? HttpStatus { get; set; }
    public string? ResponseFormat { get; set; }
    public bool StreamCompleted { get; set; }
    public string? ResponseBody { get; set; }
    public string? AssistantContent { get; set; }
    public string? ProviderRequestId { get; set; }
    public string? FinishReason { get; set; }
    public string? UsageJson { get; set; }
    public TokenUsage TokenUsage => TokenUsage.Read(UsageJson);
    public string Outcome { get; set; } = "pending";
    public string? ErrorDetail { get; set; }
    public long ElapsedMs { get; set; }
    public int Retries => 0;

    public string RedactedJson(string key)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Wire.Serialize(this))!.AsObject();
        foreach (var entry in node.ToArray())
            if (entry.Value is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text))
                node[entry.Key] = SecretRedactor.Redact(text, key, entry.Key == "assistant_content");
        // A key echoed across SSE chunks is reconstructable from raw events. Suppress that raw
        // stream when assembled content needs redaction; the redacted assistant body is retained.
        if (ResponseFormat == "sse" && AssistantContent != null && SecretRedactor.Redact(AssistantContent, key, true) != AssistantContent)
            node["response_body"] = "[OMITTED: configured key or its trailing prefix appeared in streamed content]";
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

public static class SecretRedactor
{
    public static string Redact(string value, string key, bool trailingPartial = false)
    {
        if (string.IsNullOrWhiteSpace(key)) return value;
        var forms = new List<string> { key.Trim() };
        for (int i = 0; i < 3; i++) forms.Add(JsonSerializer.Serialize(forms[^1])[1..^1]);
        foreach (var form in forms.Distinct().OrderByDescending(s => s.Length))
        {
            value = value.Replace(form, "[REDACTED]", StringComparison.Ordinal);
            if (!trailingPartial) continue;
            for (int length = Math.Min(form.Length - 1, value.Length); length > 0; length--)
                if (value.EndsWith(form[..length], StringComparison.Ordinal))
                { value = value[..^length] + "[REDACTED]"; break; }
        }
        return value;
    }
}

public sealed class DiagnosticStore(string directory)
{
    private readonly object _gate = new();
    public string Save(CallDiagnostics trace, string redactedJson)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"call-{trace.StartedUtc:yyyyMMddTHHmmssfffffff}-{trace.CallId}.json");
            File.WriteAllText(path, redactedJson);
            // Retain only this component's well-formed call records; no recursive operations.
            foreach (var file in Directory.EnumerateFiles(directory, "call-*.json")
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^call-\d{8}T\d{13}-[a-f0-9]{32}\.json$"))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(20)) File.Delete(file);
            return path;
        }
    }
}
