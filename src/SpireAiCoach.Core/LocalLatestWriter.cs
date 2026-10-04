using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace SpireAiCoach.Core;

// Only immutable progress DTOs cross this boundary. One writer preserves ordering;
// a slow display consumes the newest update without blocking native rules.
public sealed class LocalLatestWriter<T> : IAsyncDisposable
{
    private readonly Channel<(long Sequence, T Value)> _pending = Channel.CreateBounded<(long Sequence, T Value)>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _writer;
    private ExceptionDispatchInfo? _failure;
    private readonly object _gate = new();
    private readonly List<(long Target, TaskCompletionSource Completion)> _flushes = [];
    private long _published, _finished;

    public LocalLatestWriter(Action<T> write) => _writer = Task.Run(async () =>
    {
        try
        {
            while (await _pending.Reader.WaitToReadAsync())
            {
                if (!_pending.Reader.TryRead(out var latest)) continue;
                while (_pending.Reader.TryRead(out var newer)) latest = newer;
                write(latest.Value);
                lock (_gate)
                {
                    _finished = latest.Sequence;
                    for (int i = _flushes.Count - 1; i >= 0; i--)
                        if (_flushes[i].Target <= _finished)
                        { _flushes[i].Completion.TrySetResult(); _flushes.RemoveAt(i); }
                }
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(ex));
            lock (_gate)
            {
                foreach (var (_, completion) in _flushes) completion.TrySetException(ex);
                _flushes.Clear();
            }
            _pending.Writer.TryComplete(ex);
            throw;
        }
    });

    public void Publish(T value)
    {
        Volatile.Read(ref _failure)?.Throw();
        lock (_gate)
        {
            var sequence = _published + 1;
            if (!_pending.Writer.TryWrite((sequence, value))) throw new InvalidOperationException("Progress writer is closed");
            _published = sequence;
        }
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _failure) is { } failure) return Task.FromException(failure.SourceException);
            if (_finished >= _published) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushes.Add((_published, completion));
            return completion.Task;
        }
    }

    // Drain before the worker can acknowledge idle or accept another request.
    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        await _writer;
    }
}
