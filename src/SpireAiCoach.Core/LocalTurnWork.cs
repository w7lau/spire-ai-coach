using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

// One parent-owned in-memory queue for exact proposals. Native game objects,
// RNG and outcomes stay in the owned game processes. Pipes avoid rewriting a
// growing JSON frontier or creating a file for every short-lived proposal.
public sealed class LocalTurnWork : IDisposable
{
    internal sealed record Command(string Scope, int Owner, string Operation,
        LocalTurnOffer[]? Offers = null, LocalTurnTask? Task = null,
        LocalAction[]? Actions = null, LocalDecision[]? Decisions = null,
        LocalTurnHint? Hint = null, LocalWinningBound? Bound = null, string? TerminalDigest = null);
    internal sealed record Reply(LocalTurnTask? Task, int Pending, int Active,
        int Offered, int Duplicates, int Affected = 0, string? Error = null);

    private readonly object _gate = new();
    private readonly LocalTurnSearch _frontier;
    private readonly Dictionary<int, LocalTurnTask> _active = new();
    private readonly HashSet<string> _terminals = new(StringComparer.Ordinal);
    private int _repeated;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _pipes = new();
    private readonly List<Task> _sessions = [];
    private readonly Task _listener;
    private readonly string _scope;
    private readonly int _maximum;
    private int _taken;
    public string PipeName { get; } = "SpireAiCoach-turn-" + Guid.NewGuid().ToString("N");
    public int Pending { get { lock (_gate) return _frontier.Count; } }
    public int Offered { get { lock (_gate) return _frontier.Offered; } }
    public int DuplicateOffers { get { lock (_gate) return _frontier.DuplicateOffers; } }
    public int CompletedHistories { get { lock (_gate) return _terminals.Count + _repeated; } }
    public int RepeatedHistories { get { lock (_gate) return _repeated; } }

    public LocalTurnWork(LocalSearchRequest request, int maximum)
    {
        if (maximum is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximum));
        _maximum = maximum; _scope = Scope(request);
        _frontier = new(1729, request.SnapshotId + ":" + request.NativeHash);
        _listener = Task.Run(Listen);
    }

    internal static string Scope(LocalSearchRequest request) => request.Id + "\n" + request.SnapshotId + "\n" +
        request.NativeHash + "\n" + request.ModelHash + "\n" + string.Join("\n", request.LoadedMods);

    private async Task Listen()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // Keep room for the next listener while all sixteen owners are connected.
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _pipes.TryAdd(pipe, 0);
                await pipe.WaitForConnectionAsync(_stop.Token);
                _sessions.Add(Serve(pipe));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException && _stop.IsCancellationRequested) { }
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
                var command = JsonSerializer.Deserialize<Command>(line) ?? throw new InvalidDataException("Empty turn task command");
                Reply reply;
                lock (_gate)
                {
                    if (command.Scope != _scope || command.Owner < 0 || command.Owner >= _maximum ||
                        owner.HasValue && owner.Value != command.Owner)
                        reply = Snapshot(error: "Turn task request does not match its frozen owner");
                    else
                    {
                        owner = command.Owner;
                        try { reply = Apply(command); if (command.Operation == "retire") owner = null; }
                        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
                        { reply = Snapshot(error: ex.Message); }
                    }
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_gate) if (owner.HasValue) Retire(owner.Value);
            _pipes.TryRemove(pipe, out _); pipe.Dispose();
        }
    }

    private Reply Snapshot(LocalTurnTask? task = null, int affected = 0, string? error = null) =>
        new(task, _frontier.Count, _active.Count, _frontier.Offered, _frontier.DuplicateOffers, affected, error);

    private Reply Apply(Command command)
    {
        foreach (var offer in command.Offers ?? []) _frontier.Offer(offer.Prefix, offer.SearchRound, offer.Hint);
        switch (command.Operation)
        {
            case "offer": return Snapshot();
            case "take":
                if (_active.ContainsKey(command.Owner)) throw new InvalidOperationException("Worker already owns a turn task");
                if (!_frontier.TryTake(out var task)) return Snapshot();
                task = task with { FullRollout = LocalTurnSearch.IsFullRollout(_taken++),
                    Lane = _frontier.LastLane, Focused = _frontier.LastFocused };
                _active.Add(command.Owner, task); return Snapshot(task);
            case "focus":
                var focused = RequireOwner(command);
                _frontier.FocusNext(focused, command.Actions ?? [], command.Decisions ?? [], focused.Focused);
                return Snapshot();
            case "finish":
                RequireOwner(command); _active.Remove(command.Owner);
                if (command.TerminalDigest is { } digest && !_terminals.Add(digest)) _repeated++;
                return Snapshot();
            case "return":
                var interrupted = RequireOwner(command); _active.Remove(command.Owner);
                _frontier.ReturnInterrupted(interrupted, command.Hint ?? throw new InvalidDataException("Missing turn hint"));
                return Snapshot();
            case "discard": return Snapshot(affected: _frontier.DiscardDescendants(command.Actions ?? []));
            case "bound": return Snapshot(affected: _frontier.DiscardProvenExpenses(command.Bound));
            case "retire": Retire(command.Owner); return Snapshot();
            default: throw new InvalidDataException("Unknown turn task command");
        }
    }

    private LocalTurnTask RequireOwner(Command command)
    {
        if (!_active.TryGetValue(command.Owner, out var active) || command.Task?.Id != active.Id)
            throw new InvalidOperationException("Turn task is not owned by this worker");
        return active;
    }

    private void Retire(int owner)
    {
        if (!_active.Remove(owner, out var task)) return;
        // A disconnected/interrupted process does not close a native subtree.
        _frontier.ReturnInterrupted(task, task.Hint ?? throw new InvalidDataException("Turn task lost its observed hint"));
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var pipe in _pipes.Keys) pipe.Dispose();
        _listener.GetAwaiter().GetResult();
        // Cancellation can race with adding the listener's final waiting pipe.
        foreach (var pipe in _pipes.Keys) pipe.Dispose();
        Task.WhenAll(_sessions).GetAwaiter().GetResult();
        _stop.Dispose();
    }
}

public sealed class LocalTurnWorkClient : ILocalTurnFrontier, IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly string _scope;
    private readonly int _owner;
    private readonly LocalTurnSearch _choices;
    private readonly List<LocalTurnOffer> _offers = [];
    private LocalTurnWork.Reply _last = new(null, 0, 0, 0, 0);
    private LocalTurnTask? _owned;
    private long _lastFlush = Environment.TickCount64;
    public int Count => _last.Pending + _offers.Count;
    public int Active => _last.Active;
    public int Offered => _last.Offered;
    public int DuplicateOffers => _last.Duplicates;
    public int LastLane { get; private set; }

    public LocalTurnWorkClient(LocalSearchRequest request)
    {
        _scope = LocalTurnWork.Scope(request); _owner = request.Partition;
        _choices = new(1729 + _owner);
        _pipe = new(".", request.TurnWorkPipe ?? throw new ArgumentException("Missing turn pipe"), PipeDirection.InOut);
        _pipe.Connect(10000);
        _reader = new(_pipe, new UTF8Encoding(false), false, 4096, true);
        _writer = new(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    }

    private LocalTurnWork.Reply Exchange(string operation, LocalTurnTask? task = null,
        LocalAction[]? actions = null, LocalDecision[]? decisions = null,
        LocalTurnHint? hint = null, LocalWinningBound? bound = null, string? terminalDigest = null)
    {
        var command = new LocalTurnWork.Command(_scope, _owner, operation, _offers.ToArray(), task, actions, decisions, hint, bound, terminalDigest);
        _writer.WriteLine(JsonSerializer.Serialize(command));
        var reply = JsonSerializer.Deserialize<LocalTurnWork.Reply>(_reader.ReadLine() ?? throw new IOException("Turn task broker closed"))
            ?? throw new InvalidDataException("Empty turn task reply");
        if (reply.Error != null) throw new InvalidOperationException(reply.Error);
        _offers.Clear(); _lastFlush = Environment.TickCount64; _last = reply; return reply;
    }

    public void Offer(LocalAction[] prefix, int searchRound, LocalTurnHint hint)
    {
        _offers.Add(new(prefix.ToArray(), searchRound, hint));
        if (_offers.Count >= 32 || Environment.TickCount64 - _lastFlush >= 100) Exchange("offer");
    }

    public bool TryTake(out LocalTurnTask task)
    {
        var reply = Exchange("take"); task = reply.Task!;
        if (task == null) return false;
        _owned = task; LastLane = task.Lane; return true;
    }

    public void FocusNext(LocalTurnTask task, IReadOnlyList<LocalAction> actions, IReadOnlyList<LocalDecision> decisions) =>
        Exchange("focus", task, actions.Take(task.Prefix.Length + 1).ToArray(),
            decisions.Where(d => d.BeforeStep == task.Prefix.Length).ToArray());
    public void ReturnInterrupted(LocalTurnTask task, LocalTurnHint hint)
    { Exchange("return", task, hint: hint); _owned = null; }
    public void Finish(LocalTurnTask task, string? terminalDigest = null)
    { if (_owned == null) return; Exchange("finish", task, terminalDigest: terminalDigest); _owned = null; }
    public int DiscardDescendants(IReadOnlyList<LocalAction> prefix) => Exchange("discard", actions: prefix.ToArray()).Affected;
    public int DiscardProvenExpenses(LocalWinningBound? incumbent) =>
        incumbent == null || incumbent.NetHpLoss != 0 ? 0 : Exchange("bound", bound: incumbent).Affected;
    public LocalAction Choose(IReadOnlyList<LocalAction> legal, bool coherent = true) => _choices.Choose(legal, coherent);
    public void OfferAlternatives(IReadOnlyList<LocalAction> actions, LocalDecision decision, LocalTurnHint before,
        LocalTurnTask? owner = null)
    {
        foreach (var offer in LocalTurnSearch.Alternatives(actions, decision, before, owner))
            Offer(offer.Prefix, offer.SearchRound, offer.Hint);
    }

    public void Dispose()
    {
        try { Exchange("retire"); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { _reader.Dispose(); _writer.Dispose(); _pipe.Dispose(); }
    }
}
