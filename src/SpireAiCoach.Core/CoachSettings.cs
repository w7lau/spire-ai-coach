namespace SpireAiCoach.Core;

public static class GuidanceScopes
{
    public const string CurrentTurn = "current_turn";
    public const string Combat = "combat";
    public const int MaxRounds = 10;
}

public sealed record CoachSettings
{
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public string Model { get; init; } = "";
    public bool RevealDrawOrder { get; init; } = true;
    public bool RememberKey { get; init; }
    public int TimeoutSeconds { get; init; } = 120;
    public string GuidanceScope { get; init; } = GuidanceScopes.CurrentTurn;

    public Uri Endpoint()
    {
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new CoachException("configuration", "请输入 HTTPS API 地址（本机服务可用 HTTP），不要在 URL 中填写密钥或查询参数。");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.Ordinal))
            path += (path.Length == 0 ? "/v1" : "") + "/chat/completions";
        return new UriBuilder(uri) { Path = path }.Uri;
    }

    public void Validate()
    {
        _ = Endpoint();
        if (GuidanceScope is not (GuidanceScopes.CurrentTurn or GuidanceScopes.Combat))
            throw new CoachException("configuration", "指导范围无效，请重新选择。");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200)
            throw new CoachException("configuration", "请填写模型名称。");
        if (TimeoutSeconds is < 10 or > 600)
            throw new CoachException("configuration", "超时应为 10 至 600 秒。");
    }
}

public sealed class CoachException(string category, string message) : Exception(message)
{
    public string Category { get; } = category;
}
