namespace SpireAiCoach.Core;

// A single-use fingerprint of one settled, same-thread decision, never a cache
// across effects, frames, state notifications, restores, or verification.
public sealed class LocalDecisionFingerprint
{
    private object? _state;
    private string? _hash;
    private long _revision, _frames;
    private int _thread;

    public void Remember(object state, string hash, long revision, long frames)
    {
        _state = state; _hash = hash; _revision = revision; _frames = frames;
        _thread = Environment.CurrentManagedThreadId;
    }

    public bool Consume(object state, string expectedHash, long revision, long frames, bool settled)
    {
        bool matches = settled && ReferenceEquals(state, _state) && _hash == expectedHash &&
            _revision == revision && _frames == frames && _thread == Environment.CurrentManagedThreadId;
        Clear();
        return matches;
    }

    public void Clear() { _state = null; _hash = null; }
}
