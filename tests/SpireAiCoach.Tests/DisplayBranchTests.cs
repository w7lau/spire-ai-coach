using System.Reflection;
using System.Runtime.CompilerServices;
using SpireAiCoach.Core;

internal static class DisplayBranchTests
{
    private sealed class Visual { public void Render() { } }
    private sealed class State { public int? Value = 1; }
    private static bool Enabled => throw new Exception("Metadata inspection executed a getter");
    private static int _effects;
    private static void Effect() => _effects++;
    private static void Render() { }
    private static async Task Valid() { Effect(); if (Enabled) { var v = new Visual(); v.Render(); await Task.Delay(1); v.Render(); } Effect(); }
    private static async Task Mixed() { if (Enabled) { Render(); await Task.Delay(1); Effect(); } }
    private static async Task Writes() { if (Enabled) { Render(); await Task.Delay(1); _effects = 0; } }
    private static async Task AddressWrites(State state) { if (Enabled) { Render(); state.Value = null; await Task.Delay(1); } }
    private static async Task Leaks() { Visual? v = null; if (Enabled) { v = new Visual(); await Task.Delay(1); v.Render(); } v?.Render(); }
    private static void Sync() { Effect(); if (Enabled) Render(); Effect(); }
    private static void Return() { if (Enabled) { Render(); return; } Effect(); }
    private static void Unguarded() => Render();
    private static async Task Deferred() { await Task.Delay(1); if (Enabled) Render(); Effect(); }
    private static void Multiple() { if (Enabled) Render(); Effect(); if (Enabled) Render(); }
    private static bool Match(string name)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var method = typeof(DisplayBranchTests).GetMethod(name, flags)!;
        var machine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        if (machine != null) method = machine.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return LocalDisplayBranch.Find(method, typeof(DisplayBranchTests).GetProperty(nameof(Enabled), flags)!.GetMethod!,
            m => m.DeclaringType == typeof(Visual) || m.DeclaringType == typeof(Task) && m.Name == nameof(Task.Delay) ||
                m.DeclaringType == typeof(DisplayBranchTests) && m.Name == nameof(Render), t => t == typeof(Visual)).Length > 0;
    }
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        void Check(bool b) { if (!b) throw new Exception("Optional display guard assertion failed"); }
        test("optional display audit handles native async visual awaits without executing getters", () => { int before = _effects; Check(Match(nameof(Valid))); Check(_effects == before); });
        test("optional display audit preserves synchronous effects around a display block", () => Check(Match(nameof(Sync))));
        test("optional display audit rejects an effect injected inside the display guard", () => Check(!Match(nameof(Mixed))));
        test("optional display audit rejects state writes inside the display guard", () => Check(!Match(nameof(Writes))));
        test("optional display audit rejects numeric field writes through managed addresses", () => Check(!Match(nameof(AddressWrites))));
        test("optional display audit rejects a visual temporary used outside the guard", () => Check(!Match(nameof(Leaks))));
        test("optional display audit rejects a guard controlling an effect return", () => Check(!Match(nameof(Return))));
        test("optional display audit rejects unguarded display operations", () => Check(!Match(nameof(Unguarded))));
        test("optional display audit handles a static deferred visual after an earlier await", () => Check(Match(nameof(Deferred))));
        test("optional display audit recognizes separate visual blocks without removing intervening effects", () => Check(Match(nameof(Multiple))));

        var request = new LocalSearchRequest("recovery", "snapshot", [], "root", 0, [], false);
        var failed = new LocalSearchResult(request.Id, request.SnapshotId, "failed", "route error", 2, 0, 4, null, RootBranches: 6);
        var candidate = new LocalCandidate([], 40, 0, 0, 0, 40, true, false, true, 1, "won");
        var usable = failed with { Status = "done", Best = candidate };
        test("route failures do not cancel healthy search lanes", () => Check(!LocalSearchRecovery.AbortPass(request, failed)));
        test("root bootstrap failures still stop the incompatible pass", () => Check(LocalSearchRecovery.AbortPass(request, failed with { Evaluated = 0, RootBranches = 0 })));
        test("a completed candidate prevents a full-budget compatibility restart", () => Check(!LocalSearchRecovery.NeedsCompatibilityPass(request, [failed, usable])));
        test("all failed lanes still require compatibility and produce no fabricated route", () => Check(LocalSearchRecovery.NeedsCompatibilityPass(request, [failed, failed])));
        test("budget interruption is never counted as a completed full rollout", () =>
            Check(!LocalSearchRecovery.CompleteTrial(false, false, false, true, false, true)));
        test("normal terminal and full rollouts still complete while probes remain incomplete", () => {
            Check(LocalSearchRecovery.CompleteTrial(false, false, false, true, false, false));
            Check(LocalSearchRecovery.CompleteTrial(false, false, false, false, true, false));
            Check(!LocalSearchRecovery.CompleteTrial(true, false, false, true, true, false));
        });
        asyncTest("an isolated route failure lets admitted peers finish without failure cancellation", async () =>
        {
            bool canceled = false;
            var results = await LocalConcurrency.Run(2, false, true, async (index, token) => {
                if (index == 0) return failed;
                token.Register(() => canceled = true);
                await Task.Delay(10, token);
                return usable;
            }, _ => new(1, 0, 6, 2), () => false, r => LocalSearchRecovery.AbortPass(request, r), CancellationToken.None, 1);
            Check(!canceled && results.Length == 2 && results[1].Best == candidate);
        });
    }
}
