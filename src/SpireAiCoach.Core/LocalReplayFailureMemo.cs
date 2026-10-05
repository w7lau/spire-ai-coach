namespace SpireAiCoach.Core;

// A short-lived negative result for one complete frozen input, not a route or
// a general Mod ban. Timeouts, startup failures and route exceptions are not cached.
public sealed class LocalReplayFailureMemo
{
    private readonly object _gate = new();
    private string? _key;
    private DateTimeOffset _expires;
    private LocalSimulationFailure[] _failures = [];
    public bool Remember(LocalSearchRequest request, string installation, LocalSimulationFailure[] first,
        LocalSearchResult regularProbe, DateTimeOffset now)
    {
        if (!request.DataOnlyCombat || !LocalSearchRecovery.NeedsReplayValidation(first) ||
            regularProbe is not { Status: "failed", Evaluated: 0, RootBranches: 0, Best: null,
                Failure: { Category: "local_replay_mismatch" } }) return false;
        lock (_gate)
        {
            _key = installation + "\n" + LocalSearchSession.Key(request);
            _expires = now.AddMinutes(2);
            _failures = first.Append(regularProbe.Failure).ToArray();
        }
        return true;
    }
    public LocalSimulationFailure[]? Get(LocalSearchRequest request, string installation, DateTimeOffset now)
    {
        lock (_gate)
            return now < _expires && _key == installation + "\n" + LocalSearchSession.Key(request)
                ? _failures.ToArray() : null;
    }
}
