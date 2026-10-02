using System.Text.Json;

namespace SpireAiCoach.Core;

public sealed record TokenUsage(long? InputTokens, long? CachedInputTokens, long? OutputTokens)
{
    // Optional provider telemetry: absent or invalid numbers stay unknown, never zero.
    public static TokenUsage Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(null, null, null);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            long? cached = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("prompt_tokens_details", out var details))
                cached = Number(details, "cached_tokens");
            return new(Number(root, "prompt_tokens"), cached, Number(root, "completion_tokens"));
        }
        catch (JsonException) { return new(null, null, null); }
    }
    private static long? Number(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    public string Display() => $"输入 {InputTokens?.ToString() ?? "未知"} · 缓存命中 {CachedInputTokens?.ToString() ?? "未返回"} · 输出 {OutputTokens?.ToString() ?? "未知"} tokens";
}
