using SpireAiCoach.Core;

static class FullHealthReturnTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static LocalCandidate Win(int hp, int maxHp) => new(
        [new(0, "mod:card", null, "card", "", "root")], hp, 0, 0, 0, maxHp,
        true, false, false, StartingHp: 100);

    public static void Register(Action<string, Action> test)
    {
        test("full-health return uses the actual cap after victory instead of clamped net loss", () =>
        {
            foreach (var candidate in new[] { Win(100, 200), Win(100, 250), Win(150, 250), Win(249, 250) })
                Check(candidate.NetHpLoss == 0 && !LocalSearchPolicy.CanStop(candidate, true),
                    "A zero-loss but unfilled final HP cap ended healing exploration");
            var full = Win(250, 250) with { HpLost = 70 };
            Check(LocalSearchPolicy.CanStop(full, true), "Native healing to the increased final cap should qualify");
            Check(!LocalSearchPolicy.CanStop(full, false), "Disabled full-health stop ignored");
            foreach (var candidate in new[] { full with { Won = false }, full with { Dead = true },
                full with { MaxHp = 0 }, full with { Hp = 251 }, full with { StartingHp = null } })
                Check(!LocalSearchPolicy.CanStop(candidate, true), "Incomplete or invalid final HP qualified");
        });
        test("full-health return ranks the stopping goal consistently across concurrent workers", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false);
            var partial = Win(100, 250);
            var full = Win(250, 250) with { Actions = [partial.Actions[0] with { PotionSlot = 0 }] };
            Check(LocalSearchPolicy.BetterForGoal(full, partial, request) &&
                !LocalSearchPolicy.BetterForGoal(partial, full, request), "A non-full peer displaced the achieved return goal");
            Check(LocalSearchPolicy.BetterForGoal(partial, full, request with { StopOnZeroLoss = false }),
                "Disabling the return goal must preserve the saved potion-reserve policy");
            Check(LocalSearchPolicy.BetterForGoal(Win(150, 250), partial, request), "Extra native healing was ignored");
            Check(LocalSearchPolicy.BetterForGoal(Win(100, 300), partial, request), "Cap growth was ignored while health was tied");
        });
        test("full-health return keeps recovery branches open after a non-full zero-loss incumbent", () =>
        {
            var partial = Win(100, 250);
            var bound = LocalWinningBound.From("root", partial, requireFullHealthForZeroLoss: true);
            Check(bound == null && !LocalHealthBound.CannotImprove(new("root", 100, 100, 1), bound),
                "An unfilled cap pruned a later recovery-potion continuation");
            Check(LocalWinningBound.From("root", partial) != null,
                "Ordinary potion-reserve bounds changed when the full-health goal is disabled");
            Check(LocalWinningBound.From("root", Win(250, 250), true) != null &&
                LocalWinningBound.From("root", Win(90, 250), true) != null,
                "Completed full-health and positive-loss bounds remain usable");
        });
        test("full-health return publishes only compatible completed recovery bounds", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "spire-full-health-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false);
                var writer = new LocalSharedHealthBound(directory, request);
                LocalSearchResult Result(LocalCandidate c) => new("request", "snapshot", "searched", "", 1, 0, 1, c);
                Check(!writer.PublishFinished(Result(Win(100, 250))), "Non-full result became a peer's zero-loss pruning bound");
                Check(writer.PublishFinished(Result(Win(250, 250))), "Completed full-health native bound was lost");
                Check(new LocalSharedHealthBound(directory, request).Read()?.NetHpLoss == 0,
                    "Compatible peer did not receive the bound");
                Check(new LocalSharedHealthBound(directory, request with { StopOnZeroLoss = false }).Read() == null &&
                    new LocalSharedHealthBound(directory, request with { StopOnFirstWin = true }).Read() == null,
                    "A changed return objective reused an incompatible bound");
            }
            finally { Directory.Delete(directory, true); }
        });
        test("full-health return describes a healed but incomplete cap honestly", () =>
        {
            var partial = new LocalSearchResult("request", "snapshot", "done", "", 1, 0, 1, Win(100, 250));
            var text = LocalSearchPolicy.Format(partial);
            Check(text.Contains("仍差 150 点生命才满血") && !text.Contains("已达到战后满血目标"),
                "Zero clamped loss was displayed as completed full health");
            Check(LocalSearchPolicy.Format(partial with { Best = Win(250, 250) }).Contains("已达到战后满血目标"),
                "Full-health result was not identified");
        });
    }
}
