namespace SpireAiCoach.Core;

// Progress has its own 100 ms channel. Keep complete candidate/checkpoint packets
// fresh when they change, with a slower heartbeat for unchanged running results.
public sealed class LocalResultPublication
{
    private long _last = -1000;
    private object? _candidate, _certificate;
    public bool ShouldPublish(long now, string status, object? candidate, object? certificate,
        bool trimOverhead, bool throttleUnchanged)
    {
        bool unchanged = ReferenceEquals(candidate, _candidate) && Equals(certificate, _certificate);
        int interval = throttleUnchanged && unchanged ? 1000 : 250;
        if (status == "running" && (trimOverhead || ReferenceEquals(candidate, _candidate)) && now - _last < interval)
            return false;
        _last = now; _candidate = candidate; _certificate = certificate;
        return true;
    }
}
