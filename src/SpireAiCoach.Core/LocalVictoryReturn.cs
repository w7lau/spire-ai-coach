namespace SpireAiCoach.Core;

// One UI choice for one frozen calculation. This stops search admissions, not
// final verification or the player's game. Running winners remain provisional
// until the pool has checked their owner's cleanup and runtime errors.
public sealed class LocalVictoryReturn(LocalSearchRequest request)
{
    private readonly object _gate = new();
    private bool _searching = true;
    private bool _hasVictory;
    private bool _requested;

    public bool Matches(LocalSearchRequest other) => other.Id == request.Id &&
        other.SnapshotId == request.SnapshotId && other.NativeHash == request.NativeHash;
    public bool MatchesRoot(string? snapshotId, string nativeHash) =>
        snapshotId == request.SnapshotId && nativeHash == request.NativeHash;
    public bool CanRequest { get { lock (_gate) return _searching && _hasVictory && !_requested; } }
    public bool Requested { get { lock (_gate) return _requested; } }

    public void Observe(LocalSearchResult result)
    {
        if (result.Id != request.Id || result.SnapshotId != request.SnapshotId ||
            result.Status is not ("running" or "searched" or "done") ||
            !LocalSearchPolicy.WinningRouteFrom(result.Best, request)) return;
        lock (_gate) { if (_searching) _hasVictory = true; }
    }

    public bool TryRequest()
    {
        lock (_gate)
        {
            if (!_searching || !_hasVictory || _requested) return false;
            _requested = true;
            return true;
        }
    }

    public void CloseSearch() { lock (_gate) _searching = false; }
}
