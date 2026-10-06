using SpireAiCoach.Core;

static class WorkerLayoutTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Fixture(Action<string> test)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SpireAiCoach-layout-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally
        {
            Check(root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), "Fixture escaped temp directory");
            Directory.Delete(root, true);
        }
    }
    static string Legacy(string shared, string scope)
    {
        var assembly = typeof(LocalWorkerLayout).Assembly;
        var root = Path.Combine(shared, LocalWorkerLayout.Key(assembly.ManifestModule.ModuleVersionId + "|" + scope), "worker-0");
        var mod = Path.Combine(root, "game", "mods", "loaded-0");
        Directory.CreateDirectory(mod);
        File.Copy(assembly.Location, Path.Combine(mod, "SpireAiCoach.dll"));
        File.WriteAllText(Path.Combine(root, ".coach-worker"), "SpireAiCoach shared local worker v2");
        File.WriteAllText(Path.Combine(root, "history-private.json"), "preserved");
        return root;
    }
    public static void Register(Action<string, Action> test)
    {
        test("worker layouts survive code generations while preserving cache files", () => Fixture(shared =>
        {
            const string scope = "game|owned pool|native mod paths|False|True";
            var old = Legacy(shared, scope);
            var first = LocalWorkerLayout.Acquire(shared, scope, 0, CancellationToken.None);
            Check(first.Root == old, "The exact legacy scope was not reused"); first.Lock.Dispose();
            // The binding remains valid after the next code generation replaces
            // this DLL; adoption must not depend on the old module remaining.
            File.WriteAllText(Path.Combine(old, "game", "mods", "loaded-0", "SpireAiCoach.dll"), "new code generation");
            var next = LocalWorkerLayout.Acquire(shared, scope, 0, CancellationToken.None);
            using (next.Lock)
                Check(next.Root == old && File.ReadAllText(Path.Combine(old, "history-private.json")) == "preserved" &&
                    Directory.GetDirectories(shared).Length == 1, "Version changes created another layout or lost cached files");
        }));
        test("worker layouts refuse unrelated identities and unowned folders", () => Fixture(shared =>
        {
            var unrelated = Legacy(shared, "another game|another pool|other mod paths|False|True");
            const string scope = "game|this pool|this mod path|False|True";
            var actual = LocalWorkerLayout.Acquire(shared, scope, 0, CancellationToken.None);
            using (actual.Lock) Check(actual.Root != unrelated, "An unrelated legacy context was adopted");
            Check(!File.Exists(Path.Combine(unrelated, ".coach-layout")) &&
                File.ReadAllText(Path.Combine(unrelated, "history-private.json")) == "preserved", "An unrelated cache was changed");
            const string unownedScope = "unowned-context";
            var unowned = Path.Combine(shared, LocalWorkerLayout.Key("layout-v1|" + unownedScope), "worker-0");
            Directory.CreateDirectory(unowned); File.WriteAllText(Path.Combine(unowned, "keep"), "untouched");
            try { var bad = LocalWorkerLayout.Acquire(shared, unownedScope, 0, CancellationToken.None); bad.Lock.Dispose(); throw new Exception("Unowned directory accepted"); }
            catch (IOException) { }
            Check(File.ReadAllText(Path.Combine(unowned, "keep")) == "untouched" &&
                !File.Exists(Path.Combine(unowned, ".coach-worker")), "Unowned files were overwritten");
        }));
        test("worker layout leases isolate simultaneous owners and cancellation", () => Fixture(shared =>
        {
            const string scope = "game|pool|mods|False|True";
            var legacy = Legacy(shared, scope);
            var first = LocalWorkerLayout.Acquire(shared, scope, 0, CancellationToken.None);
            using (first.Lock)
            {
                var second = LocalWorkerLayout.Acquire(shared, scope, 0, CancellationToken.None);
                using (second.Lock)
                {
                    Check(second.Root != first.Root, "Concurrent owners shared an OS resource root");
                    try { var bad = LocalWorkerLayout.Acquire(shared, scope, 0, CancellationToken.None); bad.Lock.Dispose(); throw new Exception("Busy roots accepted"); }
                    catch (IOException) { }
                    Check(File.ReadAllText(Path.Combine(legacy, "history-private.json")) == "preserved", "Active owner's files were replaced");
                }
            }
            var resumed = LocalWorkerLayout.Acquire(shared, scope, 0, CancellationToken.None); resumed.Lock.Dispose();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var count = Directory.GetDirectories(shared).Length;
            try { var bad = LocalWorkerLayout.Acquire(shared, "cancelled", 0, cancelled.Token); bad.Lock.Dispose(); throw new Exception("Cancelled preparation accepted"); }
            catch (OperationCanceledException) { }
            Check(Directory.GetDirectories(shared).Length == count, "Cancellation allocated a resource layout");
        }));
    }
}
