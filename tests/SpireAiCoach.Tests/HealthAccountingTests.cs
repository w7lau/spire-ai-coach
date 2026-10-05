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
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Health accounting assertion failed"); }
}
