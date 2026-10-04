using System.Text.Json;
using SpireAiCoach.Core;

static class FirstWinTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static LocalSearchRequest Request() => new("first", "battle", [], "root", 1, [], false,
        IncludePotions: true, StopOnZeroLoss: false, StopOnFirstWin: true,
        TargetVictoryRounds: 6, TargetPotionUses: 0, RequireKnownZeroEnemyDamage: true);
    private static LocalCandidate Win() => new([new(0, "potion", null, "potion", "", "root", 10, PotionSlot: 0)],
        8, 42, 0, 0, 50, true, false, false, Rounds: 10, StartingHp: 50,
        DamageSources: new(42, 0, 0, 0, true));

    public static void Register(Action<string, Action> test)
    {
        test("first-win settings and request default off and both buttons freeze explicit choice", () =>
        {
            Check(!new CoachSettings().LocalStopOnFirstWin, "Existing settings changed their stopping mode");
            Check(JsonSerializer.Deserialize<CoachSettings>(Wire.Serialize(new CoachSettings { LocalStopOnFirstWin = true }), Wire.Json)!.LocalStopOnFirstWin,
                "Saving AI settings lost the first-win preference");
            var old = JsonSerializer.Deserialize<LocalSearchRequest>("""{"Id":"old","SnapshotId":"battle","Replay":"","NativeHash":"root","ModelHash":1,"LoadedMods":[],"ContinueOptimization":false}""")!;
            Check(!old.StopOnFirstWin, "Legacy requests must default off");
            foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            {
                var enabled = LocalCalculation.Configure(old, order, 8, true, true, true, 6, 257, 130, 125, stopOnFirstWin: true);
                Check(enabled.StopOnFirstWin && enabled.StopOnZeroLoss && enabled.SkipFinalVerification &&
                    enabled.TargetVictoryRounds == null && enabled.TargetPotionUses == null && !enabled.RequireKnownZeroEnemyDamage,
                    "First-win mode did not override the stricter optional return goal");
                Check(enabled.MaxNodes == 257 && enabled.MaxRounds == 130 && enabled.BudgetSeconds == 125 && enabled.Workers == 8 &&
                    enabled.Replay == old.Replay && enabled.NativeHash == old.NativeHash && enabled.IncludePotions,
                    "Stopping mode changed budgets or the frozen root");
                Check(JsonSerializer.Deserialize<LocalSearchRequest>(JsonSerializer.Serialize(enabled))!.StopOnFirstWin,
                    "Worker transport lost the option");
                var disabled = LocalCalculation.Configure(old, order, 8, true, true, true, 6);
                Check(!disabled.StopOnFirstWin && disabled.TargetVictoryRounds == 6 && disabled.TargetPotionUses == 0 &&
                    disabled.RequireKnownZeroEnemyDamage, "Disabling first-win changed the existing target");
            }
        });
        test("first-win accepts costly complete victories but rejects probes death and stale roots", () =>
        {
            var r = Request(); var win = Win();
            Check(LocalSearchPolicy.CanStopAfterVictory(win, r) && !LocalSearchPolicy.HasSpecificGoal(r),
                "Any win must accept damage, a potion and later rounds");
            Check(!LocalSearchPolicy.CanStopAfterVictory(win, r with { StopOnFirstWin = false }) &&
                !LocalSearchPolicy.CanStopAfterVictory(win, r with { StopOnFirstWin = false, StopOnZeroLoss = true }),
                "Normal loss optimization was disabled");
            foreach (var bad in new[] { win with { Won = false }, win with { Dead = true }, win with { Hp = 0 },
                win with { Actions = [] }, win with { Actions = [win.Actions[0] with { BeforeHash = "stale" }], Hp = 50 } })
                Check(!LocalSearchPolicy.CanStopAfterVictory(bad, r with { StopOnZeroLoss = true }),
                    "A probe or invalid root bypassed first-win checks through the zero-loss fallback");
            Check(!LocalSearchPolicy.CanStopAfterVictory(win, r with { VerifyCandidate = win }), "Search stop interrupted verification");
        });
        test("first-win stop reaches peers with minimum stop disabled and preserves verification cancellation", () =>
        {
            var r = Request(); var stop = new LocalSearchStop(r.Id, r.SnapshotId, r.NativeHash);
            Check(stop.Matches(r) && !stop.Matches(r with { StopOnFirstWin = false }), "Peer ignored a first-win stop");
            Check(!stop.Matches(r with { VerifyCandidate = Win() }) && !stop.Matches(r with { NativeHash = "stale" }),
                "A goal stop reached verification or another root");
            Check((stop with { Cancel = true }).Matches(r with { VerifyCandidate = Win(), StopOnFirstWin = false }),
                "User cancellation stopped reaching verification");
        });
        test("first-win advice labels the chosen return mode without claiming a minimum", () =>
        {
            var result = new LocalSearchResult("first", "battle", "done", "", 1, 0, 1, Win(),
                StoppedEarly: true, StoppedOnFirstWin: true);
            Check(LocalSearchPolicy.FormatAdvice(result).Contains("找到获胜路线即返回") &&
                LocalSearchPolicy.Format(result).Contains("未继续优化损失或用药"), "Advice lost the return mode");
            var proof = new LocalMinimumLossStatus(Certificate: new("root", 50, 42, 1), Confirmed: true);
            Check(!LocalSearchPolicy.HasMinimumProof(result with { MinimumLoss = proof }),
                "First-win mode was advertised as a proven minimum");
        });
    }
}
