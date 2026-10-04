using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text.Json;
using SpireAiCoach.Core;

public static class ProgressTransportTests
{
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        void Check(bool condition) { if (!condition) throw new Exception("Progress transport assertion failed"); }
        var request = new LocalSearchRequest(Guid.NewGuid().ToString("N"), "frozen", [], "native", 42, ["mod"], false,
            Partition: 3, Partitions: 8);
        LocalProgress Progress(LocalSearchRequest r, long sequence) => new(r.Id, r.SnapshotId, r.Partition, r.Partitions,
            sequence, 1, 1, 64, 0, 10, 60, "出牌", null, [], "running");
        async Task Wait(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!ready()) await Task.Delay(10, timeout.Token);
        }
        asyncTest("progress pipe preserves native identity ordering and final update without files", async () =>
        {
            using var listener = new LocalProgressTransport(request);
            using var sender = new LocalProgressSender(request with { ProgressPipe = listener.PipeName });
            Check(sender.TrySend(Progress(request, 2))); await Wait(() => listener.Latest?.Sequence == 2);
            Check(sender.TrySend(Progress(request, 1)));
            var final = Progress(request, 3) with { Status = "searched", Phase = "完成" };
            Check(sender.TrySend(final)); await Wait(() => listener.Latest?.Sequence == 3);
            Check(JsonSerializer.Serialize(listener.Latest) == JsonSerializer.Serialize(final) && listener.Failure == null && sender.Connected);
            sender.Dispose(); await Task.Delay(20); Check(listener.Latest?.Sequence == final.Sequence);
            listener.Dispose(); listener.Dispose();
        });
        asyncTest("progress pipe rejects stale frozen scope request worker and partition count", async () =>
        {
            foreach (var changed in new[] { request with { NativeHash = "other" }, request with { LoadedMods = ["other"] },
                request with { ModelHash = 43 }, request with { Id = "other" }, request with { SnapshotId = "other" },
                request with { Partition = 2 }, request with { Partitions = 7 } })
            {
                using var listener = new LocalProgressTransport(request);
                using var sender = new LocalProgressSender(changed with { ProgressPipe = listener.PipeName });
                sender.TrySend(Progress(changed, 1));
                await Wait(() => listener.Failure != null); Check(listener.Latest == null);
            }
        });
        asyncTest("progress pipe bounds frames and rejects null telemetry without faulting cleanup", async () =>
        {
            foreach (var payload in new[] { "null", "{\"Scope\":\"native\",\"Progress\":null}", "{" })
            {
                using var listener = new LocalProgressTransport(request);
                using var client = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(1000);
                var bytes = System.Text.Encoding.UTF8.GetBytes(payload); var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                await client.WriteAsync(header); await client.WriteAsync(bytes);
                await Wait(() => listener.Failure != null); Check(listener.Latest == null);
            }
            foreach (int length in new[] { 0, -1, 2 * 1024 * 1024 + 1 })
            {
                using var listener = new LocalProgressTransport(request);
                using var client = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.Out);
                client.Connect(1000); var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
                client.Write(header); await Wait(() => listener.Failure != null); Check(listener.Latest == null);
            }
        });
        test("missing progress listener falls back once and does not accept foreign endpoints", () =>
        {
            using var sender = new LocalProgressSender(request with { ProgressPipe = "SpireAiCoach-progress-" + Guid.NewGuid().ToString("N") });
            Check(!sender.TrySend(Progress(request, 1)) && sender.Fallback != null);
            Check(!sender.TrySend(Progress(request, 2)) && !sender.Connected);
            using var foreign = new LocalProgressSender(request with { ProgressPipe = "other-service" });
            Check(!foreign.TrySend(Progress(request, 1)) && foreign.Fallback != null);
            using var waiting = new LocalProgressTransport(request); waiting.Dispose(); waiting.Dispose();
        });
        test("timing archive retains both algorithms and latest compressed records", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "coach-timing-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var first = new { algorithm = "MonteCarlo", elapsed_ms = 700, trials = 1 };
                var second = new { algorithm = "TurnFrontier", elapsed_ms = 600, trials = 1 };
                string a = LocalTimingArchive.Write(directory, Guid.NewGuid().ToString("N"), first);
                string b = LocalTimingArchive.Write(directory, Guid.NewGuid().ToString("N"), second);
                string Read(string path) { using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
                    using var text = new StreamReader(gzip); return text.ReadToEnd(); }
                Check(a != b && Directory.GetFiles(directory, "*.gz").Length == 2);
                Check(Read(a) == JsonSerializer.Serialize(first) && Read(b) == JsonSerializer.Serialize(second));
                Check(File.ReadAllText(Path.Combine(directory, "local-timing-latest.json")) == Read(b));
                try { LocalTimingArchive.Write(directory, "../outside", second); throw new Exception("Unsafe identity accepted"); }
                catch (ArgumentException) { }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        test("simulation failure retains underlying native cause and wrapper stack", () =>
        {
            Exception root;
            try { NativeFailure(); throw new Exception("Expected native error"); }
            catch (InvalidOperationException ex) { root = ex; }
            var wrapped = new AggregateException(new System.Reflection.TargetInvocationException(root));
            var failure = LocalSimulationFailure.Capture(wrapped, 3, "action");
            Check(failure.ExceptionType == typeof(InvalidOperationException).FullName && failure.Message == "native rule failure");
            Check(failure.Method!.EndsWith(".NativeFailure", StringComparison.Ordinal) && failure.Stack.Contains("AggregateException"));
            var result = new LocalSearchResult(request.Id, request.SnapshotId, "failed", "失败", 0, 0, 1, null, Failure: failure);
            var roundtrip = JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(result))!;
            Check(roundtrip.Failure == failure && roundtrip.Best == null);
            var recovered = roundtrip with { Status = "done", Failure = null, RecoveredFailures = [failure] };
            Check(JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(recovered))!.RecoveredFailures!.Single() == failure);
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void NativeFailure() => throw new InvalidOperationException("native rule failure");
}
