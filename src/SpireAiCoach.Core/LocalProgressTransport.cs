using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace SpireAiCoach.Core;

// Display telemetry only. Completed candidates, cancellation, native checkpoints
// and idle acknowledgement keep their existing reliable channels and guards.
public sealed class LocalProgressTransport : IDisposable
{
    internal const string Prefix = "SpireAiCoach-progress-";
    internal const int MaximumBytes = 2 * 1024 * 1024;
    internal sealed record Envelope(string Scope, LocalProgress Progress);
    private readonly LocalSearchRequest _request;
    private readonly string _scope;
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _receiver;
    private LocalProgress? _latest;
    private int _disposed;
    public string PipeName { get; } = Prefix + Guid.NewGuid().ToString("N");
    public LocalProgress? Latest => Volatile.Read(ref _latest);
    public string? Failure { get; private set; }

    public LocalProgressTransport(LocalSearchRequest request)
    {
        if (request.Partitions is < 1 or > 16 || request.Partition < 0 || request.Partition >= request.Partitions)
            throw new ArgumentOutOfRangeException(nameof(request));
        _request = request; _scope = LocalTurnWork.Scope(request);
        _pipe = new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _receiver = Task.Run(Receive);
    }

    private async Task Receive()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token);
            var header = new byte[4];
            while (!_stop.IsCancellationRequested)
            {
                await _pipe.ReadExactlyAsync(header, _stop.Token);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 1 or > MaximumBytes) throw new InvalidDataException("Invalid local progress length");
                var bytes = new byte[length];
                await _pipe.ReadExactlyAsync(bytes, _stop.Token);
                var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidDataException("Empty local progress");
                var update = envelope.Progress ?? throw new InvalidDataException("Missing local progress");
                if (envelope.Scope != _scope || update.Id != _request.Id || update.SnapshotId != _request.SnapshotId ||
                    update.Worker != _request.Partition || update.Workers != _request.Partitions || update.Sequence <= 0)
                    throw new InvalidDataException("Local progress does not match its frozen worker");
                if (!_stop.IsCancellationRequested && update.Sequence > (Latest?.Sequence ?? 0))
                    Volatile.Write(ref _latest, update);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested && ex is not EndOfStreamException) Failure = ex.GetType().Name + ": " + ex.Message; }
        finally { _pipe.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _pipe.Dispose();
        _receiver.GetAwaiter().GetResult(); _stop.Dispose();
    }
}

public sealed class LocalProgressSender : IDisposable
{
    private readonly string _name, _scope;
    private NamedPipeClientStream? _pipe;
    private bool _disabled;
    public bool Connected { get; private set; }
    public string? Fallback { get; private set; }

    public LocalProgressSender(LocalSearchRequest request)
    {
        _name = request.ProgressPipe ?? ""; _scope = LocalTurnWork.Scope(request);
        if (!_name.StartsWith(LocalProgressTransport.Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(_name[LocalProgressTransport.Prefix.Length..], "N", out _))
        { _disabled = true; Fallback = "No owned progress endpoint"; }
    }

    // Called only by the dedicated latest-value writer, never with live models.
    // A missing/broken listener falls back to the existing atomic-file protocol.
    public bool TrySend(LocalProgress progress, Func<string, IDisposable?>? measure = null)
    {
        if (_disabled) return false;
        try
        {
            if (_pipe == null)
            {
                using var connecting = measure?.Invoke("PipeConnect");
                _pipe = new(".", _name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _pipe.Connect(500); Connected = true;
            }
            byte[] bytes;
            using (measure?.Invoke("Serialize"))
                bytes = JsonSerializer.SerializeToUtf8Bytes(new LocalProgressTransport.Envelope(_scope, progress));
            if (bytes.Length > LocalProgressTransport.MaximumBytes) throw new InvalidDataException("Local progress is too large");
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            using var writing = measure?.Invoke("PipeWrite");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _pipe.WriteAsync(header, timeout.Token).AsTask().GetAwaiter().GetResult();
            _pipe.WriteAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            Fallback = ex.GetType().Name + ": " + ex.Message; _disabled = true; Connected = false;
            _pipe?.Dispose(); _pipe = null; return false;
        }
    }

    public void Dispose() { _pipe?.Dispose(); _pipe = null; _disabled = true; }
}
