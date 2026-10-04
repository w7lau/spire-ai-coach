using System.Reflection;
using System.Threading.Tasks;
using SpireAiCoach.Core;

static class HealthBoundTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static LocalCandidate Win(int hp = 30, int potions = 0) => new(
        [new(0, "card", null, "card", "", "native", PotionSlot: potions > 0 ? 0 : null)],
        hp, 50 - hp, 0, 0, 50, true, false, false, StartingHp: 50);
    private static async Task AsyncFixture() { await Task.Yield(); FixtureTarget(); }
    private static void FixtureTarget() { }

    public static void Register(Action<string, Action> test)
    {
        test("health bound includes victory healing and preserves equal outcomes", () =>
        {
            var bound = LocalWinningBound.From("root", Win(35));
            Check(!LocalHealthBound.CannotImprove(new("root", 50, 29, 0, 6), bound), "29+6 ties and cannot be discarded");
            Check(LocalHealthBound.CannotImprove(new("root", 50, 28, 0, 6), bound), "28+6 cannot beat a completed 35-HP victory");
            var sum = LocalRecoveryAllowance.Sum([new(6), new(12), new(0)]);
            Check(sum.MaximumFurtherHpGain == 18, "Multiple recovery sources must add");
            Check(LocalRecoveryAllowance.Sum([new(6), new(null, "unknown effect")]).MaximumFurtherHpGain == null,
                "A known relic cannot hide unknown future recovery");
            Check(LocalRecoveryAllowance.Sum([new(long.MaxValue), new(1)]).MaximumFurtherHpGain == null, "Overflow cannot produce a false bound");
        });
        test("health bound pending native prefix uses its certified recovery allowance", () =>
        {
            var search = new LocalTurnSearch(1, "root");
            search.Offer([new(0, "card", null, "card", "", "native")], 2, new(28, 50, 40, 100, MaximumFurtherHpGain: 6));
            search.Offer([new(1, "card", null, "card", "", "native")], 2, new(29, 50, 40, 100, MaximumFurtherHpGain: 6));
            search.Offer([new(2, "card", null, "card", "", "native")], 2, new(1, 50, 40, 100));
            Check(search.DiscardProvenExpenses(LocalWinningBound.From("root", Win(35))) == 1 && search.Count == 2,
                "Only the proven inferior prefix can be removed; equal/unknown recovery must survive");
        });
        test("health bound sharing rejects uncompleted invalid stale and different-mod results", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "spire-bound-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var request = new LocalSearchRequest("job", "snapshot", [], "native", 1, ["model"], false);
                var writer = new LocalSharedHealthBound(directory, request);
                var finished = new LocalSearchResult("job", "snapshot", "searched", "", 1, 0, 1, Win());
                foreach (var invalid in new[] { finished with { Status = "running" }, finished with { Status = "failed" },
                    finished with { Status = "partial" }, finished with { Id = "stale" }, finished with { SnapshotId = "different" },
                    finished with { Best = Win() with { Won = false } },
                    finished with { Best = Win() with { Actions = [Win().Actions[0] with { BeforeHash = "different" }] } } })
                    Check(!writer.PublishFinished(invalid), "An untrusted candidate became a shared pruning certificate");
                Check(writer.PublishFinished(finished), "A finished native victory should be shareable");
                Check(new LocalSharedHealthBound(directory, request).Read()?.NetHpLoss == 20, "Peer did not receive the completed bound");
                Check(new LocalSharedHealthBound(directory, request with { LoadedMods = ["other"] }).Read() == null,
                    "Different Mod code must not receive the certificate");
                Check(new LocalSharedHealthBound(directory, request with { TargetVictoryRounds = 6 }).Read() == null,
                    "Different objectives must not receive the certificate");
                Check(new LocalSharedHealthBound(directory, request with { NumericalExecution = false }).Read() == null,
                    "Different native execution paths must not receive the certificate");
                Check(!writer.PublishFinished(finished with { Best = Win(29) }), "A worse finished victory weakened the bound");
                Check(writer.PublishFinished(finished with { Best = Win(35) }), "A better victory did not improve the bound");
                Check(new LocalSharedHealthBound(directory, request).Read()?.NetHpLoss == 15, "Improved certificate was lost");
                File.WriteAllText(Directory.GetFiles(directory, "health-bound-*.json").Single(), "{");
                Check(new LocalSharedHealthBound(directory, request).Read() == null,
                    "An unreadable optional certificate must not break native exploration");
            }
            finally { Directory.Delete(directory, true); }
        });
        test("health bound sharing respects unfinished specific goals and starting HP", () =>
        {
            var better = LocalWinningBound.From("root", Win(35));
            Check(LocalHealthBound.Better(better, LocalWinningBound.From("root", Win(40) with { StartingHp = 60 })) == better,
                "Starting HP changes invalidate the comparison");
            var directory = Path.Combine(Path.GetTempPath(), "spire-goal-bound-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var request = new LocalSearchRequest("job", "snapshot", [], "native", 1, [], false, TargetVictoryRounds: 6);
                Check(!new LocalSharedHealthBound(directory, request).PublishFinished(
                    new("job", "snapshot", "searched", "", 1, 0, 1, Win() with { Rounds = 5 })),
                    "An unmet zero-loss goal must not delete possible target-achieving routes");
            }
            finally { Directory.Delete(directory, true); }
        });
        test("health bound exact tree closure preserves alternative orders", () =>
        {
            var search = new LocalSearchTree(1);
            var first = Win().Actions[0]; var alternate = first with { CombatCardIndex = 2, HandIndex = 2 };
            var trial = search.Begin(); search.Select(trial, [first, alternate], first);
            search.Complete(trial, Win() with { Won = false, EnemyHp = 40 }, 100, closeExactPrefix: true);
            var next = search.Begin();
            Check(search.TrySelect(next, [first, alternate], out var open) && open == alternate,
                "A proven inferior order must not close its sibling");
            Check(!search.TrySelect(search.Begin(), [first], out _), "A certified prefix was reopened");
            var potion = alternate with { PotionSlot = 0 };
            Check(search.TrySelect(search.Begin(), [first, potion], out _) && !search.TrySelect(search.Begin(), [first], out _),
                "After pruning the only open potion, the filtered tree must report ordinary exhaustion");
        });
        test("health bound metadata reader resolves lowered asynchronous calls", () =>
        {
            var fixture = typeof(HealthBoundTests).GetMethod(nameof(AsyncFixture), BindingFlags.Static | BindingFlags.NonPublic)!;
            var state = fixture.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()!.StateMachineType;
            var move = state.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var code = LocalMethodBody.Read(move);
            Check(code != null && code.Any(i => i.Operand is MethodInfo m && m.Name == nameof(FixtureTarget)),
                "Async effect analysis missed the actual continuation body");
        });
    }
}
