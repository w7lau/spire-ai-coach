using SpireAiCoach.Core;

static class OptimizationTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        test("low damage archive keeps bounded equal-cost resource diversity and rotates proposals", () =>
        {
            LocalCandidate Seed(uint index, int loss = 14) {
                var hit = new LocalAction(0,"opaque",1,"","","root",CombatCardIndex:index);
                return new([hit],32-loss,loss,0,0,72,true,false,false,StartingHp:31,
                    Decisions:[new(0,[hit,hit with {TargetId=2},hit with {TargetId=3}])],
                    HealthChanges:new(31,71,32-loss,72,loss,1,1,0,0),CombatOutcome:new(true,true,[]));
            }
            var archive = new LocalLowDamageRoutes();
            for(uint i=1;i<=5;i++) archive.Offer(Seed(i));
            var first = new List<uint?>();
            for(int i=0;i<4;i++) {
                Check(archive.TryTake(out var p,out _,out _) && p[0].TargetId==2,"Missing first target proposal");
                first.Add(p[0].CombatCardIndex);
            }
            Check(first.SequenceEqual(new uint?[]{1,2,3,4}),"Equal-cost seed diversity must be bounded and fair");
            for(int i=0;i<4;i++) Check(archive.TryTake(out var p,out _,out _) && p[0].TargetId==3,
                "Second target was buried behind unrelated neighborhoods");
            archive.Offer(Seed(6,13));
            Check(archive.TryTake(out var improved,out _,out _) && improved[0].CombatCardIndex==6 && archive.Count<=63,
                "A strictly lower damage seed must replace higher-damage archive entries");
        });
        test("low damage seed preserves observed survival without changing final HP selection", () =>
        {
            var low = new LocalCandidate([], 18, 14, 0, 0, 72, true, false, false, StartingHp: 31,
                HealthChanges: new(31,71,18,72,14,1,1,0,0), CombatOutcome: new(true,true,[]));
            var healed = low with { Hp = 22, HpLost = 31, HealthChanges = new(31,71,22,72,31,22,1,0,0) };
            Check(LocalSequenceTactics.BetterLowDamageSeed(low, healed), "Retain lower gross damage independently");
            Check(!LocalSequenceTactics.BetterLowDamageSeed(healed, low), "Healing must not clear the low damage seed");
            Check(LocalSearchPolicy.Better(healed, low), "Exploration must not invert the final HP objective");
            Check(!LocalSequenceTactics.BetterLowDamageSeed(low with { Dead = true }, null) &&
                !LocalSequenceTactics.BetterLowDamageSeed(low with { CombatOutcome = new(true,false,[]) }, null) &&
                !LocalSequenceTactics.BetterLowDamageSeed(low with { HealthChanges = low.HealthChanges! with { UnobservedChanges = 1 } }, null),
                "Only observed native victories can seed this lane");
        });
        test("soft end defense reconsideration leaves exact replay and unknown threat untouched", () =>
        {
            var end = new LocalAction(-1,"",null,"","","state",EndTurn:true);
            var block = new LocalAction(0,"opaque",null,"","","state",CombatCardIndex:12);
            var potion = block with { PotionSlot = 0 };
            var legal = new[] { (block, 6), (potion, 20) };
            Check(LocalSequenceTactics.ReconsiderEnd(end,true,10,legal) == block, "Current legal defense should be proposed");
            Check(LocalSequenceTactics.ReconsiderEnd(end,false,10,legal) == end &&
                LocalSequenceTactics.ReconsiderEnd(end,true,0,legal) == end &&
                LocalSequenceTactics.ReconsiderEnd(end,true,double.NaN,legal) == end &&
                LocalSequenceTactics.ReconsiderEnd(block,true,10,legal) == block,
                "Only eligible soft end proposals with a positive known gap may change");
        });
        test("sequence hint shares defensive resources and caps overlapping block", () =>
        {
            LocalFollowupCard[] blocks = [new(8, 1), new(6, 1), new(9, 2), new(3, 0, 1)];
            Check(LocalSequenceTactics.Defense(blocks, 1, 0, 20) == 8, "Cannot spend one energy twice");
            Check(LocalSequenceTactics.Defense(blocks, 1, 1, 20) == 11, "Both resources constrain the combination");
            Check(LocalSequenceTactics.Defense(blocks, 2, 1, 10) == 10, "Do not count excess block as saved HP");
            Check(LocalSequenceTactics.Defense(blocks, -1, 1, 20) == 0, "No negative budget");
            Check(LocalSequenceTactics.LostDefense(8, 8) == 0 && LocalSequenceTactics.LostDefense(8, 3) > 0,
                "Only lost affordable protection is a cost");
        });
        test("delayed hint values marginal poison without inventing lethal credit", () =>
        {
            Check(LocalSequenceTactics.DelayedDamage(3, 0, 20) > 0, "Delayed damage has exploration value");
            Check(LocalSequenceTactics.DelayedDamage(3, 20, 10) == 0, "Do not repeatedly reward already covered HP");
            Check(LocalSequenceTactics.DelayedDamage(double.NaN, 0, 20) == 0, "Unknown preview stays neutral");
            Check(LocalSequenceTactics.DelayedDamage(100, 0, 1) == 2, "Low HP caps the hint");
        });
        test("optimization tests unused legal resources before end turn and removes wasted actions", () =>
        {
            var hit = new LocalAction(0, "unknown-hit", 1, "", "", "a", 1, CombatCardIndex: 1);
            var block = new LocalAction(1, "unknown-carry-mechanism", null, "", "", "b", 1, CombatCardIndex: 2);
            var end = new LocalAction(-1, "", null, "", "", "b", 1, EndTurn: true);
            var nextEnd = end with { Round = 2, BeforeHash = "c" };
            var candidate = new LocalCandidate([hit,end,nextEnd], 70, 10, 0, 0, 80, true, false, false,
                Decisions: [new(1,[block,end])]);
            var refiner = new LocalRouteRefiner(); refiner.Offer(candidate);
            var proposals = new List<LocalAction[]>();
            while (refiner.TryTake(out var plan)) proposals.Add(plan);
            Check(proposals.Any(p => p.SequenceEqual(new[] { hit,block,end,nextEnd })), "Unused defense/setup must be tested with the whole suffix");
            Check(proposals.Any(p => p.SequenceEqual(new[] { end,nextEnd })), "Removing an action must also be tested");
            Check(proposals.All(p => p.Count(a => a.EndTurn) == 2), "Insertion/removal must keep enemy turns");
        });
        test("optimization final net HP loss includes healing and accepts recovered HP costs", () =>
        {
            var less = new LocalCandidate([], 60, 0, 0, 0, 80, true, false, false, StartingHp: 80);
            var healed = less with { Hp = 80, HpLost = 20 };
            Check(LocalSearchPolicy.Better(healed, less) && !LocalSearchPolicy.Better(less, healed), "Net loss includes native healing");
            Check(LocalSearchTree.Reward(healed, 100) > LocalSearchTree.Reward(less, 100), "Exploration must favor the final outcome");
            Check(!LocalSearchPolicy.Better(healed with { HpLost = 40 }, healed), "Gross healed costs must not disqualify equal final outcomes");
            Check(!LocalSearchPolicy.Better(less with { Won = false }, healed), "A partial no-loss route is not a victory");
        });
        test("optimization healing above starting HP outranks potion reserve", () =>
        {
            var potion = new LocalAction(-1, "unknown-potion", null, "", "", "", PotionSlot: 0);
            var kept = new LocalCandidate([], 70, 20, 0, 0, 80, true, false, false, StartingHp: 60);
            var used = kept with { Hp = 80, Actions = [potion] };
            Check(LocalSearchPolicy.Better(used, kept), "Additional final HP must survive the zero net-loss clamp");
            Check(LocalSearchTree.Reward(used, 100) > LocalSearchTree.Reward(kept, 100), "Tree flattened native healing above starting HP");
            Check(LocalSearchPolicy.Better(kept with { Hp = 80 }, used), "Equal final HP must preserve the potion");
            Check(LocalSearchPolicy.Better(used, kept with { Hp = 59 }), "Potion preventing final HP loss is valid");
            var result = new LocalSearchResult("", "", "done", "", 1, 0, 1, kept);
            Check(LocalSearchPolicy.Format(result).Contains("生命净变化 +10") && LocalSearchPolicy.Format(result).Contains("累计扣血 20"), "Display separates net change and gross damage");
        });
        test("optimization varies native selection targets without per-card adapters", () =>
        {
            var c = new LocalCardChoice("offer", 0, "playable", "", [0], "hand");
            var status = c with { Index = 1, ModelId = "unknown-status", Indices = [1] };
            var a = new LocalAction(0, "unknown-select-exhaust", null, "", "", "", 1, Choices: [c], CombatCardIndex: 10);
            var end = new LocalAction(-1, "", null, "", "", "", 1, EndTurn: true);
            var seed = new LocalCandidate([a,end], 80, 0, 0, 0, 80, true, false, false,
                Decisions: [new(0,[a,end],[new(0,[c,status])])]);
            var refine = new LocalRouteRefiner(); refine.Offer(seed);
            var found = false;
            while (refine.TryTake(out var plan))
                found |= plan.Length == 2 && plan[0].Choices?.Single().ModelId == "unknown-status";
            Check(found, "Changing discard/exhaust selection must be offered with native offer identity");
        });
        test("optimization reorders native card instances without name type or hand-position rules", () =>
        {
            var a = new LocalAction(0, "mod-attack", 1, "same-name", "", "root", 1, CombatCardIndex: 10);
            var b = a with { HandIndex = 1, CombatCardIndex = 11 };
            var c = a with { HandIndex = 2, ModelId = "unknown-mod-effect", CombatCardIndex = 12, TargetId = null };
            var end = new LocalAction(-1, "", null, "", "", "after", 1, EndTurn: true);
            var refiner = new LocalRouteRefiner();
            refiner.Offer(new([a,b,c,end], 60, 20, 50, 0, 80, false, false, false));
            Check(refiner.TryTake(out var proposed) && proposed[0] == c && proposed[1] == a && proposed[2] == b,
                "Delayed effect should be tested before both earlier cards");
            var legal = new[] { b with { HandIndex = 0, BeforeHash = "changed" }, a with { HandIndex = 3, BeforeHash = "changed" } };
            Check(LocalRouteRefiner.Resolve(a, legal)?.HandIndex == 3, "Two same-name copies must retain native identity");
            Check(LocalRouteRefiner.Resolve(c, legal) == null, "Unavailable proposal must not guess another instance");
            Check(LocalRouteRefiner.Resolve(a, legal.Select(x => x with { Round = 2 }).ToArray()) == null, "Round must match");
        });
        test("optimization selection branches cover empty single and multiple choices within native limits", () =>
        {
            var choices = LocalSelectionBranches.Generate(4, 0, 2);
            Check(choices.Length == 11 && choices.Any(c => c.Length == 0) && choices.Count(c => c.Length == 2) == 6, "All combinations");
            Check(choices.All(c => c.Distinct().Count() == c.Length && c.All(i => i >= 0 && i < 4)), "Valid instances");
            var bounded = LocalSelectionBranches.Generate(100, 0, 100, 64);
            Check(bounded.Length == 64 && bounded.Any(c => c.Length > 1), "Large branching must be bounded without enumerating its powerset");
            Check(new LocalSearchRequest("", "", [], "", 0, [], false).MaxRounds == 64, "Ten-round cutoff must be removed");
        });
        test("optimization tactical priors depend on lethal threat and followup rather than card type", () =>
        {
            var hit = new LocalTacticalFeatures(Damage: 6, EnemyHp: 30, Known: true);
            Check(LocalTactics.Priority(hit with { EnemyHp = 6 }) > LocalTactics.Priority(new(Strength: 3, FollowupAttacks: 2, Known: true)), "Lethal first");
            Check(LocalTactics.Priority(new(Strength: 3, FollowupAttacks: 2, Known: true)) > LocalTactics.Priority(hit), "Setup before nonlethal attack");
            Check(LocalTactics.Priority(new(Strength: 3, FollowupAttacks: 0, Known: true)) < LocalTactics.Priority(hit), "No imaginary current-turn combo");
            Check(LocalTactics.Priority(new(Block: 5, Incoming: 0, Known: true)) < LocalTactics.Priority(new(EndTurn: true, Known: true)), "Unused block may wait");
            Check(LocalTactics.Priority(new(Block: 8, Incoming: 10, Hp: 5, Known: true)) > LocalTactics.Priority(hit), "Survival block first");
            Check(LocalTactics.Priority(new(Known: false)) == 0, "Unknown remains neutral");
        });
        test("optimization two-hit targets outrank split damage without inventing a followup", () =>
        {
            var hit = new LocalTacticalFeatures(Damage: 15, EnemyHp: 20, FollowupAttacks: 1,
                FollowupDamage: 6, Known: true, ResourceCost: 2, FollowupKillHint: true);
            Check(LocalTactics.Priority(hit with { FollowupKillHint = false }) == 10,
                "Ordinary combat must retain its existing attack score");
            Check(LocalTactics.Priority(hit) > LocalTactics.Priority(hit with { EnemyHp = 30 }),
                "Equal immediate damage hid the target that a following hit can finish");
            Check(LocalTactics.Priority(hit with { FollowupAttacks = 0 }) ==
                LocalTactics.Priority(hit with { FollowupDamage = 0 }), "Absent followup must remain neutral");
            Check(LocalTactics.Priority(hit with { FollowupDamage = double.NaN }) ==
                LocalTactics.Priority(hit with { FollowupDamage = 0 }), "Unknown damage must not invent a combo");
            Check(LocalTactics.Priority(hit with { EnemyHp = 15 }) > LocalTactics.Priority(hit),
                "A possible combo must not outrank the same attack's immediate kill");
            Check(LocalTactics.Priority(hit with { Known = false }) == 0, "Unknown native preview stays neutral");
        });
        test("optimization followup kill hint is confined to explicit play-only goals", () =>
        {
            Check(LocalCardGoalTactics.UseFollowupKills(new(PlayModelId: "selected")), "Play-only goal missing");
            Check(!LocalCardGoalTactics.UseFollowupKills(null) && !LocalCardGoalTactics.UseFollowupKills(new()),
                "Ordinary combat changed");
            Check(!LocalCardGoalTactics.UseFollowupKills(new(FinisherModelId: "selected")) &&
                !LocalCardGoalTactics.UseFollowupKills(new(PlayModelId: "play", FinisherModelId: "finish")),
                "Unvalidated finishing-blow or combined goals changed");
        });
        test("optimization rollout portfolios explore native persistent setup without changing the default", () =>
        {
            var hit = new LocalTacticalFeatures(Damage: 6, EnemyHp: 100, Known: true);
            var unknownPower = new LocalTacticalFeatures(PersistentSetup: true);
            Check(LocalTactics.Priority(hit) == 12 && LocalTactics.Priority(unknownPower) == 0,
                "The existing balanced preview changed");
            Check(LocalTactics.Priority(unknownPower, LocalRolloutStyle.Preparation) >
                LocalTactics.Priority(hit, LocalRolloutStyle.Preparation), "Persistent setup was never tested early");
            Check(LocalTactics.Priority(hit, LocalRolloutStyle.Attack) > LocalTactics.Priority(hit),
                "The attack portfolio does not explore a different ordering");
            var spareFreeBlock = new LocalTacticalFeatures(Block: 8, Incoming: 0, Known: true, EnergyCost: 0);
            Check(LocalTactics.Priority(spareFreeBlock) < 0 &&
                LocalTactics.Priority(spareFreeBlock, LocalRolloutStyle.Preparation) > 0,
                "A separate native rollout must test opaque triggers from an otherwise unused free play");
            Check(LocalTactics.Priority(spareFreeBlock with { EnergyCost = 1 }, LocalRolloutStyle.Preparation) < 0,
                "The free-play portfolio must not silently assume every paid block is worthwhile");
            Check(LocalTactics.Priority(new(EndTurn: true, Known: true, Hp: 5, Incoming: 10),
                LocalRolloutStyle.Preparation) == -40, "A setup prior must not overwrite lethal end-turn risk");
        });

        test("optimization previews retain marginal gains of strong attacks", () =>
        {
            var hit = new LocalTacticalFeatures(Damage: 80, EnemyHp: 500, Known: true);
            Check(LocalTactics.Priority(hit with { Damage = 90 }) > LocalTactics.Priority(hit),
                "A score ceiling hid additional block, strength or upgrade damage");
            Check(LocalTactics.Priority(hit with { EnemyBlock = 20 }) < LocalTactics.Priority(hit),
                "Blocked damage must retain its native marginal cost");
        });

        test("optimization tactical previews never exclude unknown or low priority actions", () =>
        {
            var tree = new LocalSearchTree(1);
            var high = new LocalAction(0, "hit", 1, "hit", "", "root", Preference: 100);
            var low = high with { HandIndex = 1, ModelId = "unknown", Preference = -40 };
            var seen = new HashSet<string>();
            for (int i = 0; i < 2; i++)
            {
                var trial = tree.Begin(); var action = tree.Select(trial, [high, low]); seen.Add(action.ModelId);
                tree.Complete(trial, new([action], 80, 0, 0, 0, 80, true, false, false), 10, true);
            }
            Check(seen.Count == 2, "Prior accidentally pruned a legal action");
        });
        test("optimization tactical rollout keeps carried block valuable and still explores unknown branches", () =>
        {
            var block = new LocalTacticalFeatures(Block: 16, Incoming: 0, Known: true);
            Check(LocalTactics.Priority(block with { RetainsBlock = true }) > LocalTactics.Priority(new(EndTurn: true, Known: true)),
                "Carried block must remain useful when this turn has no incoming attack");
            Check(LocalTactics.Priority(block) < 0, "Ordinary excess block is not assumed to carry");
            var a = new LocalAction(0, "native-defense", null, "", "", "root", Preference: 32);
            var b = a with { HandIndex = 1, ModelId = "unknown-mod-mechanism", Preference = 0 };
            var tree = new LocalSearchTree(2);
            var first = tree.Begin();
            Check(tree.Select(first, [a,b], greedy: true) == a, "First coherent rollout must use the current tactical prior");
            tree.Complete(first, new([a], 80, 0, 0, 0, 80, true, false, false), 10, true);
            var second = tree.Begin();
            Check(tree.Select(second, [a,b], greedy: true) == b, "Coherent rollouts must not delete unknown mechanics");
        });
        LocalSearchResult Result()
        {
            var actions = Enumerable.Range(0, 3).Select(i => new LocalAction(i, "card", 1, "card", "", "state" + i, 1)).ToArray();
            var points = Enumerable.Range(0, 3).Select(i => new LocalContinuationPoint(i, "state" + i, new(10 + i, "history" + i), i * 2, 80 - i * 2)).ToArray();
            return new("id", "snapshot", "done", "", 1, 0, 100, new(actions, 76, 4, 0, 99, 80, true, false, false, Continuation: points, StartingHp: 80));
        }
        test("optimization continuation reuses exact forward history and adjusts remaining damage", () =>
        {
            var plan = new LocalContinuation("fight", ["mod"], Result());
            Check(plan.Advance("fight", ["mod"], "state0", new(10, "history0"))?.Best?.Actions.Length == 3, "Root");
            var next = plan.Advance("fight", ["mod"], "state2", new(12, "history2"));
            Check(next?.Best?.Actions.Length == 1 && next.Best.HpLost == 0 && plan.CompletedActions == 2, "Skip observed intermediate frames safely");
            Check(next?.Best?.StartingHp == 76 && next.Best.NetHpLoss == 0, "Remaining cost uses HP at this exact continuation point");
            Check(plan.Advance("fight", ["mod"], "state1", new(11, "history1")) == null && plan.Invalid, "Cannot move backward");
            var noProgress = new LocalContinuation("fight", ["mod"], Result());
            Check(noProgress.Advance("fight", ["mod"], "state0", new(10, "history0"), requireProgress: true) == null,
                "An observed state-change event without a verified action must invalidate even if visible values return");
        });
        test("optimization continuation rejects other fights mods histories and repeated visible states", () =>
        {
            foreach (var input in new[] {
                ("other", new[] { "mod" }, "state1", new LocalHistoryStamp(11, "history1")),
                ("fight", new[] { "changed" }, "state1", new LocalHistoryStamp(11, "history1")),
                ("fight", new[] { "mod" }, "state1", new LocalHistoryStamp(11, "other-action")),
                ("fight", new[] { "mod" }, "state1", new LocalHistoryStamp(12, "history1")),
                ("fight", new[] { "mod" }, "changed-state", new LocalHistoryStamp(11, "history1")) })
            {
                var plan = new LocalContinuation("fight", ["mod"], Result());
                Check(plan.Advance(input.Item1, input.Item2, input.Item3, input.Item4) == null, "Mismatch reused");
                Check(plan.Advance("fight", ["mod"], "state0", new(10, "history0")) == null, "Invalidation is sticky");
            }
        });
    }
}
