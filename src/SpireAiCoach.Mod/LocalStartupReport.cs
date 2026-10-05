using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

internal sealed record LocalStartupReport(string Generation, Guid ModMvid, string Phase, LocalTrace Trace,
    LocalStartupRuntime? Runtime);
internal sealed record LocalStartupRuntime(bool ServerGc, Dictionary<string, object> GcConfiguration, int Threads,
    long PrivateBytes, double CpuMs, double GcPauseMs, int GodotMaxThreads);
