using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

// One batch per settled trial, not one IPC call per card. Both search algorithms
// share this proof; their queues and heuristics are deliberately independent.
public sealed class LocalMinimumLossBroker : IDisposable
{
    internal sealed record Command(string Scope, int Owner, LocalLossProofTrial? Trial = null, bool ClaimFocus = false);
    internal sealed record Reply(LocalMinimumLossStatus Status, string? Error = null, LocalLossProofFocus? Focus = null);
    private readonly object _gate = new();
    private readonly LocalMinimumLossProof _proof;
    private readonly string _scope;
    private readonly int _maximum;
    private readonly HashSet<int> _submitted = [], _confirmed = [];
    private readonly Dictionary<int, string> _claims = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _pipes = new();
    private readonly List<Task> _sessions = [];
    private readonly Task _listener;
    public string PipeName { get; } = "SpireAiCoach-loss-" + Guid.NewGuid().ToString("N");
    public LocalMinimumLossBroker(LocalSearchRequest request, int maximum)
    {
        if (maximum is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximum));
        _maximum = maximum; _scope = LocalMinimumLossProof.Scope(request); _proof = new(request);
        _listener = Task.Run(Listen);
    }
    public LocalMinimumLossStatus Status
    {
        get { lock (_gate) return _proof.Status with { Confirmed = _submitted.Count > 0 && _submitted.IsSubsetOf(_confirmed) }; }
    }
    // A worker may propose a stop using settled trials. The final global claim
    // additionally requires all contributors to pass cleanup and game-error gates.
    public void ConfirmOwner(int owner) { lock (_gate) _confirmed.Add(owner); }
    public void RejectOwner(int owner)
    {
        lock (_gate) if (_submitted.Contains(owner)) _proof.Invalidate("A contributing worker did not finish valid native execution");
    }
    private async Task Listen()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _pipes.TryAdd(pipe, 0); await pipe.WaitForConnectionAsync(_stop.Token); _sessions.Add(Serve(pipe));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || ex is IOException && _stop.IsCancellationRequested) { }
    }
    private async Task Serve(NamedPipeServerStream pipe)
    {
        int? owner = null;
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var c = JsonSerializer.Deserialize<Command>(line) ?? throw new InvalidDataException("Empty loss proof command");
                Reply reply;
                lock (_gate)
                {
                    if (c.Scope != _scope || c.Owner < 0 || c.Owner >= _maximum || owner.HasValue && c.Owner != owner.Value)
                        reply = new(Status, "Loss proof request does not match its frozen owner");
                    else
                    {
                        owner = c.Owner;
                        if (c.Trial != null)
                        { _claims.Remove(c.Owner); _submitted.Add(c.Owner); _proof.Observe(c.Trial); }
                        LocalLossProofFocus? focus = null;
                        if (c.ClaimFocus && !_claims.ContainsKey(c.Owner))
                        {
                            focus = (_proof.Status.Focus ?? []).FirstOrDefault(f =>
                                !_claims.ContainsValue(LocalTurnSearch.HistoryKey(f.Prefix)));
                            if (focus != null) _claims.Add(c.Owner, LocalTurnSearch.HistoryKey(focus.Prefix));
                        }
                        reply = new(Status, Focus: focus);
                    }
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally
        {
            lock (_gate) if (owner.HasValue) _claims.Remove(owner.Value);
            _pipes.TryRemove(pipe, out _); pipe.Dispose();
        }
    }
    public void Dispose()
    {
        _stop.Cancel(); foreach (var pipe in _pipes.Keys) pipe.Dispose();
        _listener.GetAwaiter().GetResult(); foreach (var pipe in _pipes.Keys) pipe.Dispose();
        Task.WhenAll(_sessions).GetAwaiter().GetResult(); _stop.Dispose();
    }
}

public sealed class LocalMinimumLossClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly string _scope;
    private readonly int _owner;
    public LocalMinimumLossStatus Status { get; private set; } = new();
    public LocalMinimumLossClient(LocalSearchRequest request)
    {
        _scope = LocalMinimumLossProof.Scope(request); _owner = request.Partition;
        _pipe = new(".", request.MinimumLossPipe ?? throw new InvalidDataException("Missing loss proof pipe"),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { _pipe.Connect(1000); } catch { _pipe.Dispose(); throw; }
        _reader = new(_pipe, new UTF8Encoding(false), false, 4096, true);
        _writer = new(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    }
    public LocalMinimumLossStatus Observe(LocalLossProofTrial? trial)
    {
        return Send(trial).Status;
    }
    public LocalLossProofFocus? TakeFocus() => Send(claimFocus: true).Focus;
    private LocalMinimumLossBroker.Reply Send(LocalLossProofTrial? trial = null, bool claimFocus = false)
    {
        var command = JsonSerializer.Serialize(new LocalMinimumLossBroker.Command(_scope, _owner, trial, claimFocus));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _writer.WriteLineAsync(command.AsMemory(), deadline.Token).GetAwaiter().GetResult();
        var response = _reader.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult() ?? throw new IOException("Loss proof broker closed");
        var reply = JsonSerializer.Deserialize<LocalMinimumLossBroker.Reply>(response) ?? throw new InvalidDataException("Empty loss proof reply");
        if (reply.Error != null) throw new InvalidDataException(reply.Error);
        Status = reply.Status; return reply;
    }
    public void Dispose()
    {
        _pipe.Dispose();
        try { _writer.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _reader.Dispose();
    }
}
