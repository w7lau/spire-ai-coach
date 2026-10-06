using System.ComponentModel;
using SpireAiCoach.Core;

internal static class WorkerDirectoryTests
{
    public static void Register(Action<string, Action> test)
    {
        if (!OperatingSystem.IsWindows()) return;
        test("saturated NTFS files reuse an owned idle launch tree across configurations", () => WithRoot(root =>
        {
            var legacy = Cached(root, "1111111111111111", 0);
            var source = Path.Combine(root, "sdk.bin");
            File.WriteAllText(source, "native-sdk");
            File.Delete(Path.Combine(legacy, "game", "SlayTheSpire2.exe"));
            File.Delete(Path.Combine(legacy, "game", "SlayTheSpire2.pck"));
            foreach (var name in new[] { "SlayTheSpire2.exe", "SlayTheSpire2.pck" })
                LocalWorkerFileSharing.Share(source, Path.Combine(legacy, "game", name), CancellationToken.None);
            for (var i = 0; i < 1021; i++)
                LocalWorkerFileSharing.Share(source, Path.Combine(root, "alias-" + i), CancellationToken.None);
            var missing = Path.Combine(root, "one-too-many");
            try { LocalWorkerFileSharing.Share(source, missing, CancellationToken.None); throw new Exception("Saturation was not exercised"); }
            catch (LocalWorkerResourceException ex)
            {
                Check(ex.InnerException is Win32Exception { NativeErrorCode: 1142 } && ex.Message.Contains("1142") &&
                    ex.Message.Contains("sdk.bin") && LocalSimulationFailure.Capture(ex).Category == "local_resource");
            }
            using (File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                foreach (var group in new[] { "2222222222222222", "3333333333333333" })
                {
                    var preferred = Path.Combine(root, group, "worker-0");
                    using var lease = LocalWorkerDirectory.Acquire(root, preferred, CancellationToken.None);
                    Check(lease.Root == legacy && lease.Reused && !Directory.Exists(preferred));
                    LocalWorkerFileSharing.Share(source, Path.Combine(lease.Root, "game", "SlayTheSpire2.exe"), CancellationToken.None);
                }
            Check(File.ReadAllText(source) == "native-sdk" && !File.Exists(missing));
        }));
        test("resource leases skip active owners and retain their private data", () => WithRoot(root =>
        {
            var first = Cached(root, "1111111111111111", 0);
            var second = Cached(root, "2222222222222222", 1);
            File.WriteAllText(Path.Combine(first, "game.log"), "prior diagnostic");
            using var active = LocalWorkerDirectory.Acquire(root, first, CancellationToken.None);
            using var peer = LocalWorkerDirectory.Acquire(root, first, CancellationToken.None);
            Check(peer.Root == second && active.Root != peer.Root);
            try { using var conflict = LocalWorkerDirectory.Acquire(root, first, CancellationToken.None); throw new Exception("Active directory was reused"); }
            catch (LocalWorkerResourceException) { }
            Check(File.ReadAllText(Path.Combine(first, "game.log")) == "prior diagnostic");
        }));
        test("resource storage refuses unowned destinations", () => WithRoot(root =>
        {
            var preferred = Path.Combine(root, "1111111111111111", "worker-0");
            Directory.CreateDirectory(preferred);
            File.WriteAllText(Path.Combine(preferred, "foreign.txt"), "keep");
            try { using var lease = LocalWorkerDirectory.Acquire(root, preferred, CancellationToken.None); throw new Exception("Unowned directory was accepted"); }
            catch (LocalWorkerResourceException) { }
            Check(File.ReadAllText(Path.Combine(preferred, "foreign.txt")) == "keep" &&
                !File.Exists(Path.Combine(preferred, ".coach-worker")));
        }));
        test("cancelled resource allocation creates no launch tree", () => WithRoot(root =>
        {
            var preferred = Path.Combine(root, "1111111111111111", "worker-0");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { using var lease = LocalWorkerDirectory.Acquire(root, preferred, cancelled.Token); throw new Exception("Cancellation was ignored"); }
            catch (OperationCanceledException) { }
            Check(!Directory.Exists(preferred));
        }));
        test("resource allocation rejects paths outside the known owned layout", () => WithRoot(root =>
        {
            foreach (var target in new[] { Path.Combine(root, "..", "foreign", "worker-0"),
                Path.Combine(root, "1111111111111111", "worker-16"), Path.Combine(root, "not-a-group", "worker-0") })
            {
                try { using var lease = LocalWorkerDirectory.Acquire(root, target, CancellationToken.None); throw new Exception("Unsafe path accepted"); }
                catch (LocalWorkerResourceException) { }
                Check(!Directory.Exists(target));
            }
        }));
        test("disk resource failures never repeat native compatibility searches", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "native", 1, [], false);
            var failure = LocalSimulationFailure.Capture(new LocalWorkerResourceException("links exhausted"));
            var failed = new LocalSearchResult(request.Id, request.SnapshotId, "failed", failure.Message, 0, 0, 1, null, Failure: failure);
            Check(failure.Category == "local_resource" && !LocalSearchRecovery.AbortPass(request, failed) &&
                !LocalSearchRecovery.NeedsCompatibilityPass(request, [failed]) && LocalSearchRecovery.OnlyLocalFailures([failure]) &&
                !LocalSearchPolicy.HasExecutionPoints(failed));
            var native = failed with { Failure = failure with { Category = "local_replay_mismatch" } };
            Check(LocalSearchRecovery.NeedsCompatibilityPass(request, [failed, native]) && LocalSearchRecovery.AbortPass(request, native));
        });
    }

    private static string Cached(string root, string group, int index)
    {
        var path = Path.Combine(root, group, "worker-" + index);
        Directory.CreateDirectory(Path.Combine(path, "game"));
        File.WriteAllText(Path.Combine(path, ".coach-worker"), LocalWorkerDirectory.Ownership);
        File.WriteAllText(Path.Combine(path, "game", "SlayTheSpire2.exe"), "exe");
        File.WriteAllText(Path.Combine(path, "game", "SlayTheSpire2.pck"), "pck");
        return path;
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("Resource reuse assertion failed"); }
    private static void WithRoot(Action<string> action)
    {
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(parent, "spire-slots-" + Guid.NewGuid().ToString("N")));
        if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture directory");
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
