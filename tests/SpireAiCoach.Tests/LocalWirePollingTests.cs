using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpireAiCoach.Core;

internal static class LocalWirePollingTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("busy IPC polling does not consume the five second publication timeout", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "spire-wire-busy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); var path = Path.Combine(directory, "result.json");
            var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(); Task? writer = null;
            try
            {
                LocalWire.Write(path, new[] { 1, 1 });
                writer = Task.Run(() => LocalWire.Write(path, new[] { 2, 2 }, operation =>
                {
                    if (operation == "Write")
                    {
                        locked.TrySetResult();
                        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Writer control timed out");
                    }
                    return null;
                }));
                await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 20; i++) Check(!LocalWire.TryRead<int[]>(path, out _), "Busy publication yielded a stale value");
                Check(timer.Elapsed < TimeSpan.FromSeconds(1), "Polling waited on native publication");
                release.Set(); await writer;
                Check(LocalWire.TryRead<int[]>(path, out var next) && next.SequenceEqual(new[] { 2, 2 }), "Completed publication was lost");
            }
            finally { release.Set(); if (writer != null) await writer; Directory.Delete(directory, true); }
        });
        test("IPC polling preserves corruption errors and treats missing documents as pending", () => WithDirectory(path =>
        {
            Check(!LocalWire.TryRead<int[]>(path, out _), "Missing file became a value");
            File.WriteAllText(path, "{broken");
            try { LocalWire.TryRead<int[]>(path, out _); throw new Exception("Corruption was treated as pending"); }
            catch (JsonException) { }
        }));
        asyncTest("large IPC deserialization releases publication ownership before parsing", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "spire-wire-decode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); var path = Path.Combine(directory, "result.json");
            var decoding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resume = new ManualResetEventSlim();
            ControlledConverter.Decoding = decoding; ControlledConverter.Resume = resume;
            Task<ControlledValue>? reader = null;
            try
            {
                File.WriteAllText(path, "1");
                reader = Task.Run(() => LocalWire.Read<ControlledValue>(path));
                await decoding.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // The first document is frozen even if its object graph is slow.
                // Publication of the next complete document must remain possible.
                var writer = Task.Run(() => LocalWire.Write(path, 2));
                await writer.WaitAsync(TimeSpan.FromSeconds(2));
                Check(LocalWire.Read<int>(path) == 2, "New snapshot was not published while old decoding waited");
                resume.Set();
                Check((await reader).Value == 1, "Decode combined different document generations");
            }
            finally
            {
                resume.Set(); if (reader != null) await reader;
                ControlledConverter.Decoding = null; ControlledConverter.Resume = null;
                Directory.Delete(directory, true);
            }
        });
        test("IPC pressure is not a reason to abandon peers or rebuild in another native mode", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "native", 1, [], false);
            var failure = LocalSimulationFailure.Capture(new LocalIpcBusyException("result.json"));
            var busy = new LocalSearchResult(request.Id, request.SnapshotId, "failed", failure.Message, 0, 0, 1, null, Failure: failure);
            Check(failure.Category == "local_ipc" && !LocalSearchRecovery.AbortPass(request, busy) &&
                !LocalSearchRecovery.NeedsCompatibilityPass(request, [busy]), "Communication pressure became a rule-compatibility failure");
            var native = busy with { Failure = failure with { Category = "local_data_unavailable" } };
            Check(LocalSearchRecovery.AbortPass(request, native) && LocalSearchRecovery.NeedsCompatibilityPass(request, [busy, native]),
                "A genuine native failure lost its compatibility path");
        });
    }
    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "spire-wire-poll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, "result.json")); }
        finally { Directory.Delete(directory, true); }
    }
    [JsonConverter(typeof(ControlledConverter))]
    public sealed record ControlledValue(int Value);
    public sealed class ControlledConverter : JsonConverter<ControlledValue>
    {
        public static TaskCompletionSource? Decoding;
        public static ManualResetEventSlim? Resume;
        public override ControlledValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            Decoding?.TrySetResult();
            if (Resume?.Wait(TimeSpan.FromSeconds(10)) == false) throw new TimeoutException("Decoder control timed out");
            return new(reader.GetInt32());
        }
        public override void Write(Utf8JsonWriter writer, ControlledValue value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }
}
