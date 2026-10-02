using System.Text.Json;

namespace SpireAiCoach.Core;

// One frozen call record; never receives headers or the endpoint URL.
public sealed class CallDiagnostics
{
    public string CallId { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public string PromptVersion { get; } = PromptBuilder.Version;
    public string? SnapshotId { get; set; }
    public string GuidanceScope => "current_turn";
    public string? RequestBody { get; set; }
    public int? HttpStatus { get; set; }
    public string? ResponseBody { get; set; }
    public string? AssistantContent { get; set; }
    public string? ProviderRequestId { get; set; }
    public string? FinishReason { get; set; }
    public string Outcome { get; set; } = "pending";
    public string? ErrorDetail { get; set; }
    public long ElapsedMs { get; set; }
    public int Retries => 0;

    public string RedactedJson(string key)
    {
        // Redact decoded string values, including JSON strings embedded in request/response text.
        string Redact(string value)
        {
            if (string.IsNullOrEmpty(key)) return value;
            var forms = new List<string> { key.Trim() };
            for (int i = 0; i < 3; i++) forms.Add(JsonSerializer.Serialize(forms[^1])[1..^1]);
            foreach (var form in forms.Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length))
                value = value.Replace(form, "[REDACTED]", StringComparison.Ordinal);
            return value;
        }
        var node = System.Text.Json.Nodes.JsonNode.Parse(Wire.Serialize(this))!.AsObject();
        foreach (var entry in node.ToArray())
            if (entry.Value is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text))
                node[entry.Key] = Redact(text);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
