using SpireAiCoach.Core;

static class HealthRecoveryTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static LocalAction Move(int id, bool potion = false) =>
        new(id, "card-" + id, null, "card", "", "root", 1, PotionSlot: potion ? 0 : null,
            CombatCardIndex: (uint)id);
    static LocalCandidate Win(int hp, bool potion = false) =>
        new([Move(1, potion)], hp, 0, 0, 0, 100, true, false, false, StartingHp: 50);

    public static void Register(Action<string, Action> test)
    {
        test("healed native outcomes keep actual HP ahead of potion reserve", () =>
        {
            var more = Win(75, true) with { HpLost = 40 };
            var less = Win(60);
            Check(more.NetHpLoss == 0 && less.NetHpLoss == 0 && more.HpChange == 25,
                "Net loss and signed healing must remain distinct");
            Check(LocalSearchPolicy.Better(more, less) && !LocalSearchPolicy.Better(less, more),
                "A saved potion displaced 15 extra final HP");
            Check(LocalSearchPolicy.Better(Win(75), more), "Equal final HP must preserve the potion");
            Check(!LocalSearchPolicy.Better(more with { HpLost = 100 }, more),
                "Damage/healing churn cannot improve an unchanged final outcome");
            Check(LocalSearchTree.Reward(more, 20) > LocalSearchTree.Reward(less, 20),
                "Native rollout feedback flattened gains above starting HP");
            var progress = new LocalProgress("job", "battle", 0, 2, 1, 1, 1, 4, 1, 0, 15, "search", null, [],
                Best: new(1, less.Hp, 100, 50, 1, 0));
            Check(LocalProgressBook.BestVictory([progress, progress with { Worker = 1,
                Best = new(2, more.Hp, 100, 50, 1, 1) }])?.Best is { Hp: 75, HpChange: 25 },
                "Progress presentation disagreed with native final HP selection");
            var text = LocalSearchPolicy.FormatAdvice(new("job", "battle", "done", "", 1, 0, 1, more));
            Check(text.Contains("生命净变化 +25") && !text.Contains("净损失 0"),
                "Player advice hid the actual healing gain");
        });
        test("HP pruning preserves unknown and higher-recovery continuations", () =>
        {
            var bound = LocalWinningBound.From("battle", Win(70));
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 10, 1), bound),
                "Unknown healing was pruned by an already-zero loss");
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 65, 1, 15), bound),
                "A potion-assisted 80-HP continuation was removed");
            Check(LocalHealthBound.CannotImprove(new("battle", 50, 60, 0, 5), bound),
                "A certified 65-HP ceiling cannot improve a 70-HP win");
            Check(LocalHealthBound.CannotImprove(new("battle", 50, 65, 1, 5), bound) &&
                !LocalHealthBound.CannotImprove(new("battle", 50, 65, 0, 5), bound),
                "Only a strictly greater potion expense may close equal-HP ties");
            Check(LocalHealthBound.Better(bound, LocalWinningBound.From("battle", Win(75, true)))?.ActualHp == 75,
                "Shared native bounds kept a less healed zero-loss route");
            Check(!LocalHealthBound.CannotImprove(new("battle", 50, 60, 1, 0), new("battle", 50, 0, 0)),
                "A legacy zero-loss record fabricated an exact final HP");
            Check(LocalHealthBound.MaximumFinalHp(new("battle", 50, 80, 0, long.MaxValue)) == int.MaxValue,
                "Optimistic HP addition overflowed");
        });
        test("complete native offers prove the maximum healed HP and its potion floor", () =>
        {
            var r = MinimumLossTests.Request() with { IncludePotions = true };
            var root = r.SnapshotId + ":" + r.NativeHash;
            var actions = new[] { Move(1, true), Move(2) };
            var proof = new LocalMinimumLossProof(r);
            proof.Observe(new(50, [new(actions[0], actions, [], new(root, 50, 70, 1, 0))], 70, Won: true));
            Check(proof.Status.Certificate?.MaximumFinalHp == null &&
                !LocalSearchPolicy.CanStopAtMinimum(Win(70, true), r, proof.Status.Certificate),
                "An unvisited sibling could still heal more");
            proof.Observe(new(50, [new(actions[1], actions, [], new(root, 50, 65, 0, 5))], 65));
            Check(proof.Status.Certificate is { MaximumFinalHp: 70, MinimumNetHpLoss: 0, MinimumPotionsUsed: 0 } &&
                !LocalSearchPolicy.CanStopAtMinimum(Win(70, true), r, proof.Status.Certificate),
                "The equally healed no-potion sibling remains possible");
            proof.Observe(new(50, [new(actions[1], actions, [], new(root, 50, 65, 0, 0))], 65, Won: true));
            var best = Win(70, true);
            Check(proof.Status.Certificate is { MaximumFinalHp: 70, MinimumPotionsUsed: 1 } &&
                LocalSearchPolicy.CanStopAtMinimum(best, r, proof.Status.Certificate) &&
                LocalSearchPolicy.RequiresMinimumConfirmation(best, r, proof.Status.Certificate),
                "A completed maximally healed victory should return without requiring full HP");
            var result = new LocalSearchResult(r.Id, r.SnapshotId, "done", "", 2, 0, 1, best,
                MinimumLoss: proof.Status with { Confirmed = true }, StoppedOnMinimum: true);
            Check(LocalSearchPolicy.HasMinimumProof(result) &&
                LocalSearchPolicy.FormatAdvice(result).Contains("已证明最高战后生命为 70") &&
                !LocalSearchPolicy.HasMinimumProof(result with { MinimumLoss = proof.Status }),
                "Healed optimality requires confirmed native contributors");
        });
        test("unknown recovery and incomplete offers cannot certify maximum healing", () =>
        {
            var r = MinimumLossTests.Request(); var actions = new[] { Move(1), Move(2) };
            var root = r.SnapshotId + ":" + r.NativeHash;
            foreach (bool incomplete in new[] { false, true })
            {
                var proof = new LocalMinimumLossProof(r);
                proof.Observe(new(50, [new(actions[0], actions, [], new(root, 50, 70, 0, 0),
                    CompleteLegal: !incomplete)], 70, Won: true));
                proof.Observe(new(50, [new(actions[1], actions, [], new(root, 50, 65, 0, incomplete ? 0 : null),
                    CompleteLegal: !incomplete)], 65));
                Check(proof.Status.Certificate?.MaximumFinalHp == null &&
                    !LocalSearchPolicy.CanStopAtMinimum(Win(70), r, proof.Status.Certificate),
                    "An unknown continuation was declared maximally healed");
            }
            var legacy = new LocalMinimumLossCertificate(LocalMinimumLossProof.Scope(r), 50, 0, 0);
            Check(!LocalSearchPolicy.CanStopAtMinimum(Win(70), r, legacy),
                "Legacy zero-net-loss proof does not bound further HP gains");
        });
    }
}
