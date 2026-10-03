namespace SpireAiCoach.Core;

public sealed record CoachSettings
{
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public string Model { get; init; } = "";
    public bool RevealDrawOrder { get; init; } = true;
    public bool RememberKey { get; init; }
    public bool IncludeStreamUsage { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 120;
    public int LocalWorkers { get; init; } = 0;
    public bool LocalIncludePotions { get; init; }
    public bool LocalStopOnZeroLoss { get; init; } = true;

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
