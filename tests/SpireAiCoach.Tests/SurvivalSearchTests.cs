using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAiCoach.Core;

static class SurvivalSearchTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        LocalCandidate Partial(int hp, int enemy, double risk = 0) =>
            new([], hp, 80 - hp, enemy, 0, 80, false, false, false,
                StartingHp: 80, InitialEnemyHp: 100, EndTurnHpLossHint: risk);

        test("survival search values effective defense over capped damage without removing attacks", () =>
        {
            var capped = new LocalTacticalFeatures(Damage: 1, EnemyHp: 100, Incoming: 10,
                Known: true, ResourceCost: 1);
            var defense = new LocalTacticalFeatures(Block: 5, Incoming: 10, Known: true, ResourceCost: 1);
            Check(LocalTactics.Priority(defense) > LocalTactics.Priority(capped), "Avoidable loss must outweigh a capped hit");
            Check(LocalTactics.Priority(capped with { EnemyHp = 1, TargetThreat = 10 }) > LocalTactics.Priority(defense),
                "A real present kill remains valuable even under a damage cap");
            Check(LocalTactics.Priority(capped with { Weak = 1, TargetThreat = 10, EnergyGain = 1 }) >
                LocalTactics.Priority(capped), "A capped attack can still provide useful native side effects");
        });

        test("survival search and fallback both retain health rather than only reducing enemy HP", () =>
        {
            var defended = Partial(80, 100);
            var attacked = Partial(60, 90);
            Check(LocalSearchPolicy.Better(defended, attacked) && !LocalSearchPolicy.Better(attacked, defended),
                "The unfinished incumbent must not prioritize a small enemy HP reduction over major loss");
            Check(LocalSearchTree.Reward(defended, 100) > LocalSearchTree.Reward(attacked, 100),
                "Monte Carlo feedback must agree with the unfinished incumbent");
            Check(LocalSearchPolicy.Better(Partial(80, 80), defended), "Safe kill progress must still improve a route");
            Check(LocalSearchPolicy.Better(Partial(70, 50), defended), "Meaningful kill progress can justify a measured health trade");
        });

        test("survival search distinguishes imminent preview risk and preserves completed victory priority", () =>
        {
            var safe = Partial(80, 100);
            var exposed = Partial(80, 50, 30);
            Check(LocalSearchPolicy.Better(safe, exposed) && LocalSearchTree.Reward(safe, 100) >
                LocalSearchTree.Reward(exposed, 100), "Still unspent block or incoming risk must not look like safe HP");
            var win = Partial(1, 0) with { Won = true, EndTurnHpLossHint = null };
            Check(LocalSearchPolicy.Better(win, safe) && LocalSearchTree.Reward(win, 100) >
                LocalSearchTree.Reward(safe, 100), "An unfinished defense is never a victory");
            Check(LocalSearchPolicy.Better(win with { Hp = 80, HpLost = 14 }, win with { Hp = 79, HpLost = 1 }),
                "Completed fights still use net HP after native healing");
            Check(!LocalSearchPolicy.Better(win with { EndTurnHpLossHint = 100 }, win),
                "Speculative risk must not affect completed native victories");
        });

        test("survival search learns partial defense outcomes and still tries unknown alternatives", () =>
        {
            var tree = new LocalSearchTree(123);
            var defense = new LocalAction(0, "opaque-defense", null, "", "", "root");
            var attack = defense with { HandIndex = 1, ModelId = "opaque-attack" };
            var unknown = defense with { HandIndex = 2, ModelId = "unknown-mod-effect", Preference = -40 };
            var counts = new Dictionary<string, int>();
            for (int i = 0; i < 200; i++)
            {
                var trial = tree.Begin();
                var action = tree.Select(trial, [defense, attack, unknown]);
                counts[action.ModelId] = counts.GetValueOrDefault(action.ModelId) + 1;
                var result = action == defense ? Partial(80, 100, 5) :
                    action == attack ? Partial(25, 10, 10) : Partial(10, 100) with { Dead = true };
                tree.Complete(trial, result with { Actions = [action] }, 100);
            }
            Check(counts.Count == 3, "A poor/unknown preview must not delete a legal branch");
            Check(counts[defense.ModelId] > counts[attack.ModelId] * 2, "Measured survival feedback must direct actual search effort");
            Check(tree.CompletedTrials == 200, "Only actual supplied outcomes count as evaluated trials");
        });

        test("survival search preserves optional hints across IPC and legacy results remain readable", () =>
        {
            var candidate = Partial(75, 90, 3);
            var copy = JsonSerializer.Deserialize<LocalCandidate>(JsonSerializer.Serialize(candidate))!;
            Check(copy.InitialEnemyHp == 100 && copy.EndTurnHpLossHint == 3 && copy.StartingHp == 80,
                "The parent and workers must use the same frozen-root hint");
            var legacy = JsonSerializer.SerializeToNode(candidate)!.AsObject();
            legacy.Remove(nameof(LocalCandidate.InitialEnemyHp));
            legacy.Remove(nameof(LocalCandidate.EndTurnHpLossHint));
            var old = legacy.Deserialize<LocalCandidate>()!;
            Check(old.InitialEnemyHp == null && old.EndTurnHpLossHint == null && old.Hp == 75,
                "Legacy results cannot require fabricated threat metadata");
            Check(LocalSearchPolicy.Better(old with { Hp = 80, EnemyHp = 100 }, old with { Hp = 60, EnemyHp = 99 }),
                "A common legacy scale must still balance survival and progress");
            Check(LocalSearchPolicy.Better(old with { Hp = 80, EnemyHp = 80 }, old with { Hp = 80, EnemyHp = 100 }),
                "Legacy safe progress must remain valuable");
        });
    }
}
