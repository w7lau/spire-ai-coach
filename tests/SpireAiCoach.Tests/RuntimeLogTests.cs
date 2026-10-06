using System.Text;
using SpireAiCoach.Core;

internal static class RuntimeLogTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Runtime log assertion failed"); }
        test("native log monitoring preserves split UTF8 lines and the original error stack", () =>
        {
            using var stream = new MemoryStream(); var log = new LocalRuntimeLog();
            var bytes = Encoding.UTF8.GetBytes("[WARN] 原生警告\r\n[ERROR] 选择上下文不一致\r\n   at Native.Hook.AfterShuffle()\r\n   at Native.Card.Draw()\r\n[INFO] later\n");
            foreach (byte value in bytes)
            {
                stream.Position = stream.Length; stream.WriteByte(value);
                _ = log.Read(stream, 3, "search");
            }
            var failure = log.Read(stream, 3, "search")!;
            Check(failure.Category == "local_runtime" && failure.Worker == 3 && failure.Stage == "search");
            Check(failure.Message == "[ERROR] 选择上下文不一致" && failure.Stack.Contains("Native.Hook.AfterShuffle()") &&
                failure.Stack.Contains("Native.Card.Draw()") && !failure.Stack.Contains("later") && !failure.Stack.Contains('\ufffd'));
            Check(log.Read(stream, 3, "verify")!.Stage == "verify");
        });
        test("native runtime errors stay failed while warnings and ordinary engine exit reports retain their existing policy", () =>
        {
            foreach (string text in new[] { "[WARN] warning\n", "ERROR: 5 resources leaked at exit\n", "[INFO] completed\n" })
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                Check(new LocalRuntimeLog().Read(stream, 0, "search") == null);
            }
            foreach (string text in new[] { "[ERROR] native hook\n", "[ERROR] native hook without newline", "System.InvalidOperationException: native error\n", "ERROR: FATAL: native failure\n" })
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                var failure = new LocalRuntimeLog().Read(stream, 0, "search")!;
                var request = new LocalSearchRequest("request", "snapshot", [], "native", 1, [], false);
                var result = new LocalSearchResult(request.Id, request.SnapshotId, "failed", failure.Message, 0, 0, 0, null, Failure: failure);
                Check(!LocalSearchRecovery.AbortPass(request, result) && !LocalSearchRecovery.NeedsCompatibilityPass(request, [result]) &&
                    LocalSearchRecovery.OnlyLocalFailures([failure]) && !LocalSearchPolicy.HasExecutionPoints(result));
            }
            Check(!LocalSearchRecovery.OnlyLocalFailures([]) &&
                !LocalSearchRecovery.OnlyLocalFailures([new("native", "mismatch", null, "", Category: "local_replay_mismatch")]));
        });
        test("incremental native log scanning bounds stack retention and resets on a replaced short file", () =>
        {
            using var stream = new MemoryStream(); var log = new LocalRuntimeLog();
            byte[] bytes = Encoding.UTF8.GetBytes("[ERROR] first\n" + string.Concat(Enumerable.Repeat("   at Native.Step()\n", 10000)));
            stream.Write(bytes); var failure = log.Read(stream, 0, "search")!;
            Check(failure.Message == "[ERROR] first" && failure.Stack.Length <= 64 * 1024);
            stream.SetLength(0); stream.Write(Encoding.UTF8.GetBytes("[INFO] new process log\n"));
            Check(log.Read(stream, 0, "search") == null);
        });
    }
}
