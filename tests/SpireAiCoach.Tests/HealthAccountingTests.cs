using System.Text.Json;
using SpireAiCoach.Core;

internal static class HealthAccountingTests
{
    public static void Register(Action<string, Action> test)
    {
        test("health accounting includes battle and victory healing without replacing final HP scoring", () =>
        {
            var accounting = new LocalHealthAccounting(50, 80);
            accounting.Observe(36, 80); accounting.Observe(44, 80); accounting.Observe(50, 80);
            var health = accounting.Snapshot();
            Check(health.HpLost == 14 && health.HpGained == 14 && health.FullyObserved);
            var candidate = new LocalCandidate([], 50, health.HpLost, 0, 0, 80, true, false, false,
                StartingHp: 50, HealthChanges: health);
            Check(candidate.NetHpLoss == 0 && LocalSearchPolicy.CanStop(candidate, true));
        });
        test("nested Mod healing before notification is counted once despite duplicate callbacks", () =>
        {
            var accounting = new LocalHealthAccounting(50, 80);
            accounting.Observe(40, 80); // Low-level outer write, before any callback.
            accounting.Observe(45, 80); // A previously registered Mod callback heals.
            accounting.Observe(45, 80); // Nested notification/postfix.
            accounting.Observe(45, 80); // Outer notification/postfix reads current value.
            var health = accounting.Snapshot();
            Check(health.HpLost == 10 && health.HpGained == 5 && health.FinalHp == 45 && health.FullyObserved);
        });
        test("max HP increases are separate from healing and HP clipping remains a real loss", () =>
        {
            var accounting = new LocalHealthAccounting(80, 80);
            accounting.Observe(80, 85); accounting.Observe(85, 85);
            accounting.Observe(85, 75); accounting.Observe(75, 75);
            var health = accounting.Snapshot();
            Check(health.MaxHpGained == 5 && health.MaxHpLost == 10 && health.HpGained == 5 && health.HpLost == 10);
            Check(health.StartingHp + health.HpGained - health.HpLost == health.FinalHp);
        });
        test("unnotified field changes reconcile final values without claiming complete observation", () =>
        {
            var accounting = new LocalHealthAccounting(50, 80);
            accounting.Observe(40, 80);
            accounting.Observe(47, 83, checkpoint: true);
            accounting.Observe(47, 83, checkpoint: true);
            var health = accounting.Snapshot();
            Check(health.HpLost == 10 && health.HpGained == 7 && health.MaxHpGained == 3);
            Check(health.UnobservedChanges == 1 && !health.FullyObserved);
        });
        test("health ledger survives IPC with old candidates still readable", () =>
        {
            var candidate = new LocalCandidate([], 50, 14, 0, 0, 80, true, false, false, StartingHp: 50,
                HealthChanges: new(50, 80, 50, 80, 14, 14, 0, 0, 0));
            var copy = JsonSerializer.Deserialize<LocalCandidate>(JsonSerializer.Serialize(candidate))!;
            Check(copy.HealthChanges == candidate.HealthChanges && copy.NetHpLoss == 0);
            var old = JsonSerializer.Deserialize<LocalCandidate>(JsonSerializer.Serialize(candidate with { HealthChanges = null }))!;
            Check(old.HealthChanges == null && old.NetHpLoss == 0);
        });
        test("continuing a partially executed route rebases loss without reusing its old health ledger", () =>
        {
            var actions = new[] { new LocalAction(0, "one", null, "one", "", "root", 1),
                new LocalAction(0, "two", null, "two", "", "next", 1) };
            var health = new LocalHealthChanges(50, 80, 44, 80, 14, 8, 0, 0, 0);
            var points = new[] { new LocalContinuationPoint(0, "root", new(0, "h0"), 0, 50),
                new LocalContinuationPoint(1, "next", new(1, "h1"), 10, 40) };
            var candidate = new LocalCandidate(actions, 44, 14, 0, 0, 80, true, false, false,
                StartingHp: 50, Continuation: points, HealthChanges: health);
            var route = new LocalContinuation("battle", ["mod"], new("id", "snapshot", "done", "", 1, 0, 1, candidate));
            Check(route.Advance("battle", ["mod"], "root", new(0, "h0"))!.Best!.HealthChanges == health);
            var remaining = route.Advance("battle", ["mod"], "next", new(1, "h1"), requireProgress: true)!.Best!;
            Check(remaining.Actions.Length == 1 && remaining.StartingHp == 40 && remaining.HpLost == 4 &&
                remaining.HealthChanges == null && remaining.NetHpLoss == 0 && remaining.Continuation![0].HpLost == 0);
        });
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Health accounting assertion failed"); }
}
