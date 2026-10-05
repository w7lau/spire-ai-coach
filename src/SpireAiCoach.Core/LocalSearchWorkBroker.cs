using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

// Transport for the existing search-work rules. The parent owns the same queue
// in memory; messages contain proposals and identities, never native game state.
public sealed class LocalSearchWorkBroker : IDisposable
{
    internal sealed record Command(string Scope, int Owner, string Operation, string Kind = "expand",
        LocalAction[][]? Plans = null, bool InitializeRoot = false, string? Parent = null,
        bool Deeper = false, bool Focused = false, LocalWorkTask? Task = null, string? Terminal = null);
    internal sealed record Reply(LocalWorkStats Stats, LocalWorkTask? Task = null, string? Error = null);
    private readonly object _gate = new();
    private readonly LocalSearchWork _queue;
    private readonly Dictionary<string, (int Owner, LocalWorkTask Task)> _claims = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _pipes = new();
    private readonly List<Task> _sessions = [];
    private readonly Task _listener;
    private readonly string _scope;
    private readonly int _maximum;
    public string PipeName { get; } = "SpireAiCoach-search-" + Guid.NewGuid().ToString("N");
    public LocalWorkStats Stats => _queue.Stats();

    public LocalSearchWorkBroker(LocalSearchRequest request, int maximum, int capacity = 512)
    {
        if (maximum is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximum));
        _maximum = maximum; _scope = LocalTurnWork.Scope(request);
        _queue = new("", request with { SearchWorkPipe = null }, capacity, inMemory: true);
        _listener = Task.Run(Listen);
    }

    private async Task Listen()
    {
        try
        {
            NamedPipeServerStream CreateListener()
            {
                var listener = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _pipes.TryAdd(listener, 0);
                return listener;
            }
            var pipe = CreateListener();
            while (!_stop.IsCancellationRequested)
            {
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 232)
                {
                    // ERROR_NO_DATA: a peer closed before the accept completed.
                    var closed = pipe; pipe = CreateListener();
                    _pipes.TryRemove(closed, out _); closed.Dispose();
                    continue;
                }
                var connected = pipe;
                // Preserve the endpoint when a short-lived peer disconnects.
                pipe = CreateListener();
                _sessions.Add(Serve(connected));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException ||
            ex is IOException && _stop.IsCancellationRequested) { }
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
                var command = JsonSerializer.Deserialize<Command>(line) ?? throw new InvalidDataException("Empty search work command");
                Reply reply;
                lock (_gate)
                {
                    if (command.Scope != _scope || command.Owner < 0 || command.Owner >= _maximum ||
                        owner.HasValue && owner.Value != command.Owner)
                        reply = new(Stats, Error: "Search work request does not match its frozen owner");
                    else
                    {
                        owner = command.Owner;
                        try { reply = Apply(command); }
                        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
                        { reply = new(Stats, Error: ex.Message); }
                    }
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally
        {
            lock (_gate) if (owner.HasValue) _queue.Retire(owner.Value);
            _pipes.TryRemove(pipe, out _); pipe.Dispose();
        }
    }

    private Reply Apply(Command c)
    {
        LocalWorkTask? task = null;
        switch (c.Operation)
        {
            case "offer": _queue.Offer(c.Kind, c.Plans ?? [], c.InitializeRoot, c.Parent); break;
            case "take":
                task = _queue.Take(c.Kind, c.Owner, c.Deeper, c.Focused);
                if (task != null) _claims.Add(task.Key, (c.Owner, task));
                break;
            case "complete":
                var proposed = c.Task ?? throw new InvalidDataException("Missing owned search task");
                if (!_claims.TryGetValue(proposed.Key, out var claim) || claim.Owner != c.Owner ||
                    proposed.Kind != claim.Task.Kind || LocalSearchWork.Key(proposed.Kind, proposed.Plan) != proposed.Key)
                    throw new InvalidDataException("Search task does not belong to this worker");
                _queue.Complete(claim.Task); _claims.Remove(proposed.Key); break;
            case "terminal": _queue.RecordTerminalDigest(c.Terminal ?? ""); break;
            case "retire": _queue.Retire(c.Owner); break;
            case "stats": break;
            default: throw new InvalidDataException("Unknown search work operation");
        }
        return new(Stats, task);
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        foreach (var pipe in _pipes.Keys) pipe.Dispose();
        _listener.GetAwaiter().GetResult();
        Task.WhenAll(_sessions).GetAwaiter().GetResult();
        _queue.ReleasePlans(); _queue.Dispose(); _stop.Dispose();
    }
}

internal sealed class LocalSearchWorkClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly string _scope;
    private readonly int _owner;
    private bool _disposed;

    public LocalSearchWorkClient(LocalSearchRequest request)
    {
        _scope = LocalTurnWork.Scope(request); _owner = request.Partition;
        _pipe = new(".", request.SearchWorkPipe ?? throw new InvalidDataException("Missing search work pipe"),
            PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try { _pipe.Connect(5000); }
        catch { _pipe.Dispose(); throw; }
        _reader = new(_pipe, new UTF8Encoding(false), false, 4096, true);
        _writer = new(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    }

    private LocalSearchWorkBroker.Reply Send(LocalSearchWorkBroker.Command command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer.WriteLine(JsonSerializer.Serialize(command));
        var line = _reader.ReadLine() ?? throw new IOException("Search work broker closed");
        var reply = JsonSerializer.Deserialize<LocalSearchWorkBroker.Reply>(line) ?? throw new InvalidDataException("Empty search work response");
        if (reply.Error != null) throw new InvalidDataException(reply.Error);
        return reply;
    }
    public void Offer(string kind, LocalAction[][] plans, bool initializeRoot, string? parent) =>
        Send(new(_scope, _owner, "offer", kind, plans, initializeRoot, parent));
    public LocalWorkTask? Take(string kind, int owner, bool deeper, bool focused)
    {
        if (owner != _owner) throw new InvalidDataException("Search work owner changed");
        return Send(new(_scope, _owner, "take", kind, Deeper: deeper, Focused: focused)).Task;
    }
    public void Complete(LocalWorkTask task) => Send(new(_scope, _owner, "complete", Task: task));
    public LocalWorkStats Stats() => Send(new(_scope, _owner, "stats")).Stats;
    public void RecordTerminal(LocalCandidate candidate) =>
        Send(new(_scope, _owner, "terminal", Terminal: LocalSearchWork.Key("expand", candidate.Actions)));
    public void Retire(int owner)
    {
        if (owner != _owner) throw new InvalidDataException("Search work owner changed");
        Send(new(_scope, _owner, "retire"));
    }
    public void Dispose()
    {
        if (_disposed) return;
        try { Retire(_owner); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { }
        finally
        {
            _disposed = true; _pipe.Dispose();
            try { _writer.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            _reader.Dispose();
        }
    }
}
