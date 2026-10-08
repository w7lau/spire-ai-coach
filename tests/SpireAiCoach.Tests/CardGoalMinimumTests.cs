using SpireAiCoach.Core;

static class CardGoalMinimumTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static LocalSearchRequest Request() => new("goal-minimum", "snapshot", [], "root", 1, ["sts2:fixture"], false,
        CardGoals: new("royalty"));
    static LocalCandidate Win() => new([new(0, "royalty", null, "", "", "root", 1)],
        49, 1, 0, 0, 75, true, false, false, Rounds: 3, StartingHp: 50,
        DamageSources: new(1, 0, 0, 0, true),
        CardGoalOutcome: new("royalty", null, 1, 0, [new(1, 0)], new(new(1, 1, 1), null)));
    static LocalMinimumLossCertificate Certificate(LocalSearchRequest request, int loss = 1, int potions = 0) =>
        new(LocalMinimumLossProof.Scope(request), 50, loss, potions, 50 - loss, ContentScoped: true);

    public static void Register(Action<string, Action> test)
    {
        test("completed card goal returns at a positive certified loss with contributor confirmation", () =>
        {
            var request = Request(); var win = Win(); var proof = Certificate(request);
            Check(LocalSearchPolicy.ShouldTrackMinimum(request), "Selected play goal disabled native proof");
            Check(LocalSearchPolicy.CanStopAtMinimum(win, request, proof), "Completed goal could not return at its certified loss");
            Check(LocalSearchPolicy.CanStopAfterVictory(win, request, proof), "Combined return policy ignored the proof");
            Check(LocalSearchPolicy.RequiresMinimumConfirmation(win, request, proof), "Card goal bypassed contributor confirmation");
            Check(!LocalSearchPolicy.CanStopAfterVictory(win, request), "A sampled one-loss victory became its own certificate");
        });
        test("minimum proof cannot replace missing successful goal copies or kills", () =>
        {
            var request = Request(); var win = Win(); var proof = Certificate(request);
            foreach (var outcome in new LocalCardGoalOutcome?[] { null,
                win.CardGoalOutcome! with { Plays = 0 },
                win.CardGoalOutcome! with { PlayModelId = "another" },
                win.CardGoalOutcome! with { ConsumableGoals = null },
                win.CardGoalOutcome! with { ConsumableGoals = new(new(1, 0, 1), null) } })
                Check(!LocalSearchPolicy.CanStopAtMinimum(win with { CardGoalOutcome = outcome }, request, proof), "Incomplete or mismatched goal qualified");
            var finishing = request with { CardGoals = new(FinisherModelId: "finisher") };
            var finished = win with { CardGoalOutcome = new(null, "finisher", 0, 1, [new(0, 1)], new(null, new(1, 1, 1))) };
            Check(LocalSearchPolicy.CanStopAtMinimum(finished, finishing, Certificate(finishing)), "Successful finite finisher could not use the bound");
            Check(!LocalSearchPolicy.CanStopAtMinimum(finished with { CardGoalOutcome = finished.CardGoalOutcome! with { Kills = 0 } },
                finishing, Certificate(finishing)), "Consuming a finisher without a kill qualified");
        });
        test("card-goal minimum keeps loss round potion damage and verification constraints", () =>
        {
            var request = Request(); var win = Win();
            foreach (var restricted in new[] { request with { StopOnZeroLoss = false }, request with { StopOnFirstWin = true },
                request with { CardGoals = new("royalty", HpLossThreshold: 1) },
                request with { TargetVictoryRounds = 2 }, request with { RequireKnownZeroEnemyDamage = true },
                request with { VerifyCandidate = win }, request with { ExcludedModels = ["unsupported"] } })
                Check(!LocalSearchPolicy.CanStopAtMinimum(win, restricted, Certificate(restricted)), "A requested constraint was weakened");
            var potionRequest = request with { IncludePotions = true, TargetPotionUses = 0 };
            var usedPotion = win with { Actions = [win.Actions[0], new(-1, "potion", null, "", "", "later", PotionSlot: 0)] };
            Check(!LocalSearchPolicy.CanStopAtMinimum(usedPotion, potionRequest, Certificate(potionRequest, potions: 1)), "Proof ignored the potion reserve");
            var allowed = request with { TargetVictoryRounds = 3, TargetPotionUses = 0 };
            Check(LocalSearchPolicy.CanStopAtMinimum(win, allowed, Certificate(allowed)), "Satisfied extra constraints blocked the proof");
            Check(!LocalSearchPolicy.ShouldTrackMinimum(request with { CardGoals = null, TargetVictoryRounds = 6 }), "Stand-alone zero-loss benchmark was broadened");
        });
        test("confirmed goal minimum is displayed as proof only when its goal is complete", () =>
        {
            var request = Request(); var win = Win();
            var result = new LocalSearchResult(request.Id, request.SnapshotId, "done", "", 1, 0, 1, win,
                CardGoals: request.CardGoals, StoppedEarly: true, StoppedOnMinimum: true,
                MinimumLoss: new(Certificate: Certificate(request), Confirmed: true));
            Check(LocalSearchPolicy.HasMinimumProof(result), "Confirmed goal-aware result lost its proof");
            foreach (var text in new[] { LocalSearchPolicy.Format(result), LocalSearchPolicy.FormatAdvice(result) })
                Check(text.Contains("当前一次性出牌及补刀目标已完成") && !text.Contains("可选目标尚未证明最优"), "Goal proof was reported as incomplete");
            Check(!LocalSearchPolicy.HasMinimumProof(result with { MinimumLoss = result.MinimumLoss! with { Confirmed = false } }), "Unconfirmed contributors supplied a displayed proof");
            Check(!LocalSearchPolicy.HasMinimumProof(result with { Best = win with { CardGoalOutcome = null } }), "Missing goal evidence supplied a displayed proof");
        });
        test("incomplete exact replay offers schedule native expansion even when observed children are solved", () =>
        {
            var request = Request(); var proof = new LocalMinimumLossProof(request);
            var root = Win().Actions[0]; var next = root with { BeforeHash = "next", CombatCardIndex = 1 };
            var other = next with { CombatCardIndex = 2 };
            var hint = new LocalTurnHint(50, 50, 100, 100, MaximumFurtherHpGain: 0);
            LocalHealthEnvelope Bound(int hp) => new(request.SnapshotId + ":" + request.NativeHash, 50, hp, 0, 0);
            proof.Observe(new(50, [new(root, [root], [], Bound(50), BeforeHint: hint, AfterHint: hint, AfterRound: 1),
                new(next, [next], [], Bound(49), CompleteLegal: false, BeforeHint: hint,
                    AfterHint: hint with { Hp = 49 }, AfterRound: 1)], 49, Won: true));
            Check(proof.Status.Focus?.Any(f => f.Prefix.Length == 1 && f.Prefix[0] == root) == true,
                "An incomplete offer with solved observed children lost all proof work");
            Check(!LocalSearchPolicy.CanStopAtMinimum(Win(), request, proof.Status.Certificate), "Observed subset became a full native offer");
            proof.Observe(new(50, [new(root, [root], [], Bound(50), BeforeHint: hint, AfterHint: hint, AfterRound: 1),
                new(other, [next, other], [], Bound(49), BeforeHint: hint, AfterHint: hint with { Hp = 49 }, AfterRound: 1)], 49));
            Check(proof.Status.Certificate is { MinimumNetHpLoss: 1, MaximumFinalHp: 49 } &&
                LocalSearchPolicy.CanStopAtMinimum(Win(), request, proof.Status.Certificate), "Full offer plus partial settled sibling failed to close the proof");
        });
        test("bounded proof scan visits open descendants beyond hundreds of resolved siblings", () =>
        {
            var request = Request(); var proof = new LocalMinimumLossProof(request);
            var roots = Enumerable.Range(0, 600).Select(i => new LocalAction(0, "native:root", null, "", "", "root", 1, CombatCardIndex: (uint)i)).ToArray();
            var hint = new LocalTurnHint(50, 50, 100, 100, MaximumFurtherHpGain: 0);
            LocalHealthEnvelope Bound(int hp) => new(request.SnapshotId + ":" + request.NativeHash, 50, hp, 0, 0);
            for (int n = 0; n < roots.Length - 1; n++)
                proof.Observe(new(50, [new(roots[n], roots, [], Bound(49), BeforeHint: hint,
                    AfterHint: hint with { Hp = 49 }, AfterRound: 1)], 49, Won: n == 0));
            var next = new[] { roots[0] with { BeforeHash = "after-last", CombatCardIndex = 700 },
                roots[0] with { BeforeHash = "after-last", CombatCardIndex = 701 } };
            proof.Observe(new(50, [new(roots[^1], roots, [], Bound(50), BeforeHint: hint, AfterHint: hint, AfterRound: 1),
                new(next[0], next, [], Bound(49), BeforeHint: hint, AfterHint: hint with { Hp = 49 }, AfterRound: 1)], 49));
            var focus = proof.Status.Focus;
            Check(focus?.Any(f => f.Prefix.Length == 2 && f.Prefix[0].CombatCardIndex == 599 && f.Prefix[1].CombatCardIndex == 701) == true,
                "Closed siblings consumed the scan budget and hid the missing native child");
            Check(!LocalSearchPolicy.CanStopAtMinimum(Win(), request, proof.Status.Certificate), "Missing sibling was treated as already proven");
            proof.Observe(new(50, [new(roots[^1], roots, [], Bound(50), BeforeHint: hint, AfterHint: hint, AfterRound: 1),
                new(next[1], next, [], Bound(49), BeforeHint: hint, AfterHint: hint with { Hp = 49 }, AfterRound: 1)], 49));
            Check(proof.Status.Certificate is { MinimumNetHpLoss: 1, MaximumFinalHp: 49 } &&
                LocalSearchPolicy.CanStopAtMinimum(Win(), request, proof.Status.Certificate), "Partial settled child failed to close the bound");
        });
        test("unconstrained zero-loss alternative cannot certify a one-loss goal winner", () =>
        {
            var request = Request(); var win = Win();
            var root = new[] { win.Actions[0], win.Actions[0] with { CombatCardIndex = 2 } };
            var proof = new LocalMinimumLossProof(request);
            LocalHealthEnvelope Bound(int hp) => new(request.SnapshotId + ":" + request.NativeHash, 50, hp, 0, 0);
            proof.Observe(new(50, [new(root[0], root, [], Bound(49))], 49, Won: true));
            proof.Observe(new(50, [new(root[1], root, [], Bound(50))], 50, Won: true));
            Check(proof.Status.Certificate is { MinimumNetHpLoss: 0, MaximumFinalHp: 50 }, "Full-domain bound silently excluded a goal-free legal winner");
            Check(!LocalSearchPolicy.CanStopAtMinimum(win, request, proof.Status.Certificate), "One-loss goal winner matched a zero-loss global bound");
            var unfinished = win with { Hp = 50, HpLost = 0, CardGoalOutcome = null };
            Check(!LocalSearchPolicy.CanStopAtMinimum(unfinished, request, proof.Status.Certificate), "Goal-free optimum bypassed selected goals");
        });
    }
}
