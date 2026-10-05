namespace SpireAiCoach.Mod;

public sealed record LocalInstallation(string GameDirectory, string[] ModDirectories, bool MinimalWorkerBootstrap = true,
    bool LimitRuntimeThreads = true);
