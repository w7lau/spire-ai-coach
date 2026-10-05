using System.Text.Json;
using System.Text.Json.Nodes;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

static class CardGoalTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static LocalCardGoals Goals(int? limit = null) => new("mod:play", "mod:finish", limit, "多打牌", "补刀牌");
    static LocalCandidate Win(int hp, int plays, int kills, int potions = 0) => new(
        Enumerable.Range(0, potions).Select(i => new LocalAction(-1, "potion", null, "药水", "", "root", PotionSlot: i)).ToArray(),
        hp, Math.Max(0, 50 - hp), 0, 0, 80, true, false, false, StartingHp: 50,
        CardGoalOutcome: new("mod:play", "mod:finish", plays, kills, [new(plays, kills)]));
    static LocalCandidate Fulfilled(int hp = 50) => Win(hp, 2, 1) with
    {
        Actions = [new(0, "mod:finish", null, "", "", "root", 1)],
        CardGoalOutcome = new("mod:play", "mod:finish", 2, 1, [new(2, 1)], new(new(1, 1, 1), new(1, 1, 1)))
    };

    public static void Register(Action<string, Action> test)
    {
        test("card goals preserve health first and prefer native goal counts before potion ties", () =>
        {
            Check(!LocalSearchPolicy.Better(Win(49, 100, 5), Win(50, 0, 0), Goals()), "Sacrificed HP without permission");
            Check(LocalSearchPolicy.Better(Win(50, 3, 1, 1), Win(50, 100, 0), Goals()), "Goal tie ignored or finisher priority reversed");
            Check(LocalSearchPolicy.Better(Win(50, 4, 1), Win(50, 3, 1), Goals()), "Repeated plays did not break the kill tie");
            Check(!LocalSearchPolicy.Better(Win(50, 3, 1, 1), Win(50, 100, 0)), "Default potion policy changed");
            Check(LocalSearchPolicy.Better(Win(55, 0, 0), Win(50, 100, 5), Goals()), "Healing above the root became equivalent HP");
        });
        test("card goals use a strict net loss threshold and health fallback outside it", () =>
        {
            var goals = Goals(5);
            Check(LocalSearchPolicy.Better(Win(46, 3, 1), Win(50, 2, 0), goals), "Allowed goal tradeoff was rejected");
            Check(!LocalSearchPolicy.Better(Win(45, 100, 5), Win(46, 0, 0), goals), "Threshold boundary was made inclusive");
            Check(!LocalSearchPolicy.Better(Win(40, 100, 5), Win(44, 0, 0), goals), "No qualifying route failed health fallback");
            Check(!LocalSearchPolicy.Better(Win(46, 3, 1), Win(50, 3, 1), goals), "Equal goals ignored better health");
            Check(goals.WithinThreshold(Win(50, 0, 0) with { HpLost = 20 }), "Gross damage displaced post-combat healing");
            Check(!goals.WithinThreshold(Win(49, 3, 1) with { StartingHp = null }), "Missing root invented an HP allowance");
        });
        test("card goals never trade victory or survival for goal counts", () =>
        {
            Check(LocalSearchPolicy.Better(Win(1, 0, 0), Win(50, 99, 9) with { Won = false }, Goals(5)), "Unfinished route displaced victory");
            Check(!LocalSearchPolicy.Better(Win(0, 99, 9) with { Won = false, Dead = true },
                Win(1, 0, 0) with { Won = false }, Goals(5)), "Death became a reward route");
        });
        test("card goals single selections ignore unrelated and mismatched recorded identities", () =>
        {
            Check(LocalSearchPolicy.Better(Win(50, 10, 0), Win(50, 9, 3), new("mod:play", null)), "Disabled finisher was compared");
            var onlyPlayA = Win(50, 10, 0) with { CardGoalOutcome = new("mod:play", null, 10, 0, [new(10)]) };
            var onlyPlayB = onlyPlayA with { CardGoalOutcome = new("mod:play", null, 9, 0, [new(9)]) };
            Check(LocalSearchPolicy.Better(onlyPlayA, onlyPlayB, new("mod:play", null)), "Single play goal ignored");
            Check(Goals().Counts(Win(50, 99, 9) with { CardGoalOutcome = new("other", "other", 99, 9, []) }) == (0, 0),
                "A different model supplied goal credit");
        });
        test("card goals postpone zero loss stopping and preserve explicit cancellation", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false, CardGoals: Goals());
            Check(!LocalSearchPolicy.CanStop(Win(50, 100, 9), request), "Stopped without any goal upper bound");
            Check(!new LocalSearchStop("request", "snapshot", "root").Matches(request), "Peer zero-loss stop ended goal search");
            Check(new LocalSearchStop("request", "snapshot", "root", true).Matches(request), "Explicit cancellation ignored");
            Check(LocalSearchPolicy.CanStop(Win(50, 0, 0), request with { CardGoals = null }), "Ordinary no-loss stop changed");
        });
        test("card goals keep searching after a certified minimum health loss", () =>
        {
            var ordinary = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false);
            var selected = ordinary with { CardGoals = Goals(20) };
            var candidate = Win(38, 3, 1) with { Actions = [new(0, "mod:finish", null, "", "", "root", 1)] };
            LocalMinimumLossCertificate Certificate(LocalSearchRequest r) => new(LocalMinimumLossProof.Scope(r), 50, 12, 0);
            Check(LocalSearchPolicy.CanStopAtMinimum(candidate, ordinary, Certificate(ordinary)), "Ordinary certified stop regressed");
            Check(!LocalSearchPolicy.CanStopAtMinimum(candidate, selected, Certificate(selected)), "Health proof ended optional-goal search");
            Check(LocalMinimumLossProof.Scope(ordinary) != LocalMinimumLossProof.Scope(selected), "Goal identity leaked across proof scopes");
            var proven = new LocalSearchResult("request", "snapshot", "done", "", 1, 0, 1, candidate,
                MinimumLoss: new(Certificate: Certificate(ordinary), Confirmed: true));
            Check(LocalSearchPolicy.HasMinimumProof(proven), "Ordinary proof display regressed");
            Check(!LocalSearchPolicy.HasMinimumProof(proven with { CardGoals = selected.CardGoals }), "Card-goal route claimed proved optimality");
            var disabled = ordinary with { CardGoals = new(null, null) };
            Check(LocalSearchPolicy.CanStopAtMinimum(candidate, disabled, Certificate(disabled)), "Disabled selections prevented certified stopping");
        });
        test("consumable card goals stop only after native victory with settled health and all targets fulfilled", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false, CardGoals: Goals());
            var good = Fulfilled();
            Check(LocalSearchPolicy.CanStopAfterVictory(good, request), "Completed consumable targets kept searching");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good with { Won = false }, request), "Spent cards ended an ongoing battle");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good with { Dead = true }, request), "Dead player supplied a goal stop");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good with { Hp = 49 }, request), "HP sacrificed without an allowance");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good with { StartingHp = null }, request), "Unknown final loss passed");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good with { Actions = [good.Actions[0] with { BeforeHash = "other" }] }, request), "Other frozen battle stopped this search");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good, request with { StopOnZeroLoss = false }), "Disabled stop option ignored");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good, request with { VerifyCandidate = good }), "Verification interrupted by a goal");
        });
        test("consumable card goals preserve strict affordable losses even after a zero loss incumbent", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false, CardGoals: Goals(5));
            var zeroWithoutKills = Fulfilled() with { CardGoalOutcome = Fulfilled().CardGoalOutcome! with { Kills = 0,
                Steps = [new(2)], ConsumableGoals = new(new(1, 1, 1), new(1, 0, 1)) } };
            Check(!LocalSearchPolicy.CanStopAfterVictory(zeroWithoutKills, request), "No-loss incumbent stopped before the goal");
            Check(LocalSearchPolicy.BetterForGoal(Fulfilled(46), zeroWithoutKills, request), "Allowed goal improvement rejected");
            Check(LocalSearchPolicy.CanStopAfterVictory(Fulfilled(46), request), "Loss four did not qualify under five");
            Check(!LocalSearchPolicy.CanStopAfterVictory(Fulfilled(45), request), "Loss five qualified under five");
            Check(!LocalHealthBound.CannotImprove(new("root", 50, 46, 0, 0), new("root", 50, 0, 0), request.CardGoals),
                "Zero-loss route pruned a permissible goal world line");
        });
        test("consumable targets require successful distinct source copies and real exhaustion", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false, CardGoals: Goals(5));
            var good = Fulfilled();
            bool Stop(LocalConsumableCardGoals? evidence, int kills = 1) => LocalSearchPolicy.CanStopAfterVictory(good with
                { CardGoalOutcome = good.CardGoalOutcome! with { ConsumableGoals = evidence, Kills = kills } }, request);
            Check(!Stop(new(new(1, 1, 1), new(1, 0, 1))), "Exhaust without a finishing blow counted as success");
            Check(!Stop(new(new(1, 1, 1), new(1, 1, 0))), "Unspent target card assumed exhausted");
            Check(!Stop(new(new(1, 1, 1), new(2, 1, 1)), 20), "Repeated kills by one copy covered another unused copy");
            Check(!Stop(new(null, new(1, 1, 1))), "Repeatable play goal treated as finitely complete");
            Check(!Stop(new(new(0, 0, 0), new(1, 1, 1))), "Absent root card invented a completion bound");
            Check(!Stop(new(new(1, 1, 1), new(1, 1, 1), "other-scope")), "Other stopping scope accepted");
            Check(!Stop(null), "Legacy counts certified consumable completion");
            Check(!Stop(new(new(1, 1, 1), new(1, 1, 1)), 0), "Consumable metadata displaced real native kills");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good with { CardGoalOutcome = good.CardGoalOutcome! with { FinisherModelId = "other" } }, request),
                "Different selected model completed this goal");
        });
        test("consumable card goals preserve explicit round potion and enemy damage gates", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false, CardGoals: Goals(5),
                TargetVictoryRounds: 2, TargetPotionUses: 0, RequireKnownZeroEnemyDamage: true);
            var good = Fulfilled() with { Rounds = 2, DamageSources = new(0, 0, 0, 0, true) };
            Check(LocalSearchPolicy.CanStopAfterVictory(good, request), "Combined valid gates did not stop");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good with { Rounds = 3 }, request), "Round gate ignored");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good with { Actions = [good.Actions[0] with { PotionSlot = 0 }] }, request), "Potion gate ignored");
            Check(!LocalSearchPolicy.CanStopAfterVictory(good with { DamageSources = new(1, 0, 0, 0, true) }, request), "Enemy damage gate ignored");
        });
        test("consumable peer stops are typed frozen scoped and never interrupt verification", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false, CardGoals: Goals(5));
            var stop = new LocalSearchStop("request", "snapshot", "root", CardGoalsCompleted: true);
            Check(stop.Matches(request), "Goal-qualified stop not delivered to peers");
            Check(!new LocalSearchStop("request", "snapshot", "root").Matches(request), "Generic zero-loss stop interrupted goals");
            Check(!stop.Matches(request with { StopOnZeroLoss = false }), "Disabled goal stop accepted");
            Check(!stop.Matches(request with { VerifyCandidate = Fulfilled() }), "Goal signal stopped verification");
            Check(!stop.Matches(request with { Id = "other" }) && !stop.Matches(request with { SnapshotId = "other" }) &&
                !stop.Matches(request with { NativeHash = "other" }), "Old request supplied a peer stop");
            Check(!stop.Matches(request with { CardGoals = null }), "Card-goal signal stopped ordinary search");
            var restored = JsonSerializer.Deserialize<LocalSearchStop>(JsonSerializer.Serialize(stop));
            Check(restored!.Matches(request), "IPC lost the goal stop type");
        });
        test("consumable goal display and continuation do not claim arbitrary mod maximums or carry old root evidence", () =>
        {
            var good = Fulfilled(46);
            var result = new LocalSearchResult("request", "snapshot", "done", "", 1, 0, 1, good,
                CardGoals: Goals(5), StoppedEarly: true, StoppedOnCardGoals: true);
            var advice = LocalSearchPolicy.FormatAdvice(result);
            Check(advice.Contains("已停止搜索") && advice.Contains("回收、复制") && !LocalSearchPolicy.HasMinimumProof(result),
                "Scoped goal stop claimed a global loss or arbitrary-Mod proof");
            Check(good.CardGoalOutcome!.Remaining(0).ConsumableGoals != null && good.CardGoalOutcome.Remaining(1).ConsumableGoals == null,
                "Original root completion evidence survived an advanced root");
            Check(JsonSerializer.Deserialize<LocalCandidate>(JsonSerializer.Serialize(good))?.CardGoalOutcome?.ConsumableGoals ==
                good.CardGoalOutcome.ConsumableGoals, "IPC lost distinct current copy counters");
        });
        test("consumable finishers cannot demand more finishing blows than the current living enemy count", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false,
                CardGoals: new(null, "mod:finish", 5));
            var winner = Fulfilled() with { CardGoalOutcome = new(null, "mod:finish", 0, 1, [new(0, 1)],
                new(null, new(2, 1, 1, LivingEnemies: 1))) };
            Check(LocalSearchPolicy.CanStopAfterVictory(winner, request), "One defeated enemy required two finishing blows");
            Check(!LocalSearchPolicy.CanStopAfterVictory(winner with { Won = false }, request), "Enemy-count cap ended a continuing battle");
            Check(!LocalSearchPolicy.CanStopAfterVictory(winner with { CardGoalOutcome = winner.CardGoalOutcome! with
                { ConsumableGoals = new(null, new(2, 1, 1, LivingEnemies: 2)) } }, request), "Another available enemy was ignored");
            Check(!LocalSearchPolicy.CanStopAfterVictory(winner with { CardGoalOutcome = winner.CardGoalOutcome! with
                { ConsumableGoals = new(null, new(2, 1, 1, LivingEnemies: 0)) } }, request), "No living root enemies invented a finishing goal");
            var play = Fulfilled() with { CardGoalOutcome = Fulfilled().CardGoalOutcome! with
                { ConsumableGoals = new(new(2, 1, 1, LivingEnemies: 1), new(1, 1, 1)) } };
            Check(!LocalSearchPolicy.CanStopAfterVictory(play, request with { CardGoals = Goals(5) }), "Enemy count capped the unrelated play goal");
        });
        test("native victory closes a successful final card even when post combat cleanup omits exhaustion", () =>
        {
            var request = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false,
                CardGoals: new(null, "mod:finish", 5));
            var winner = Fulfilled() with { CardGoalOutcome = new(null, "mod:finish", 0, 1, [new(0, 1)],
                new(null, new(2, 1, 0, LivingEnemies: 1, BattleEnded: true))) };
            Check(LocalSearchPolicy.CanStopAfterVictory(winner, request), "Successful final card required a discarded terminal history event");
            Check(!LocalSearchPolicy.CanStopAfterVictory(winner with { Won = false }, request), "Completion flag invented a native victory");
            Check(!LocalSearchPolicy.CanStopAfterVictory(winner with { CardGoalOutcome = winner.CardGoalOutcome! with
                { Kills = 0, ConsumableGoals = new(null, new(2, 0, 0, LivingEnemies: 1, BattleEnded: true)) } }, request),
                "Victory substituted for a real finishing blow");
        });
        test("card goals preserve affordable goal branches in both health bound modes", () =>
        {
            var best = new LocalWinningBound("root", 50, 0, 0);
            var paid = new LocalHealthEnvelope("root", 50, 47, 1, 0);
            Check(LocalHealthBound.CannotImprove(paid, best), "Baseline HP bound changed");
            Check(!LocalHealthBound.CannotImprove(paid, best, Goals(5)), "Allowed HP tradeoff pruned");
            Check(!LocalHealthBound.CannotImprove(paid with { Hp = 50 }, best, Goals()), "Equal-HP potion goal branch pruned");
            Check(LocalHealthBound.CannotImprove(paid with { Hp = 45 }, best, Goals(5)), "Known loss outside allowance was not bounded");
            Check(!LocalHealthBound.CannotImprove(paid with { Hp = 40, MaximumFurtherHpGain = null }, best, Goals(5)), "Unknown recovery was assumed absent");
            var search = new LocalTurnSearch(1, "root", Goals(5));
            search.Offer([new(0, "card", null, "", "", "root", 1)], 1, new(47, 50, 20, 20, MaximumFurtherHpGain: 0));
            Check(search.DiscardProvenExpenses(best) == 0 && search.Count == 1, "Turn frontier dropped a goal prefix");
        });
        test("card goals feed measured outcomes to the search reward", () =>
        {
            Check(LocalSearchTree.Reward(Win(46, 5, 1), 20, Goals(5)) >
                LocalSearchTree.Reward(Win(50, 0, 0), 20, Goals(5)), "HP allowance not fed back to search");
            Check(LocalSearchTree.Reward(Win(46, 5, 1), 20, Goals(5)) >
                LocalSearchTree.Reward(Win(45, 99, 9), 20, Goals(5)), "Search rewards exceed allowed HP tier");
        });
        test("card goals continuation subtracts native counts of completed actions", () =>
        {
            var actions = new[] { new LocalAction(0, "mod:play", null, "", "", "root", 1), new LocalAction(0, "mod:finish", null, "", "", "after", 1) };
            var best = Win(50, 2, 1) with { Actions = actions, CardGoalOutcome = new("mod:play", "mod:finish", 2, 1, [new(2), new(0, 1)]),
                Continuation = [new(0, "root", new(0, "history0"), 0, 50), new(1, "after", new(1, "history1"), 0, 50)], ContinuationFromSearch = true };
            var original = new LocalSearchResult("request", "snapshot", "done", "", 1, 0, 1, best, VerificationSkipped: true, CardGoals: Goals());
            var remaining = new LocalContinuation("combat", ["mod"], original).Advance("combat", ["mod"], "after", new(1, "history1"));
            Check(remaining?.Best?.CardGoalOutcome is { Plays: 0, Kills: 1, Steps.Length: 1 }, "Old completed plays leaked into remaining plan");
        });
        test("card goals settings persist without AI credentials and reject invalid threshold before write", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "spire-card-goals-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "config.json"); var key = Path.Combine(dir, "api-key.dpapi");
                File.WriteAllText(path, """{"model":"existing","extra":17}"""); File.WriteAllBytes(key, [7, 8, 9]);
                var store = new SettingsStore(dir);
                Check(!store.Load().Settings.LocalCardGoalThresholdEnabled && store.Load().Settings.LocalPlayCardModelId == "", "Legacy config enables goals");
                store.SaveLocalOptions(2, null, playCardModelId: "mod:play", finisherCardModelId: "mod:finish", cardGoalThresholdEnabled: true, cardGoalHpLossThreshold: 5);
                var loaded = store.Load().Settings;
                Check(loaded.LocalPlayCardModelId == "mod:play" && loaded.LocalFinisherCardModelId == "mod:finish" &&
                    loaded.LocalCardGoalThresholdEnabled && loaded.LocalCardGoalHpLossThreshold == 5, "Goals were not saved");
                var before = File.ReadAllText(path);
                try { store.SaveLocalOptions(2, null, cardGoalHpLossThreshold: 0); throw new Exception("Invalid threshold accepted"); }
                catch (ArgumentOutOfRangeException) { }
                Check(File.ReadAllText(path) == before && File.ReadAllBytes(key).SequenceEqual(new byte[] { 7, 8, 9 }), "Rejected input changed config or key");
                store.SaveLocalOptions(3, false);
                Check(store.Load().Settings.LocalFinisherCardModelId == "mod:finish" && JsonNode.Parse(File.ReadAllText(path))!["extra"]!.GetValue<int>() == 17, "Unrelated save erased goals");
                store.SaveLocalOptions(3, null, playCardModelId: "", finisherCardModelId: "", cardGoalThresholdEnabled: false);
                Check(store.Load().Settings.LocalPlayCardModelId == "", "Goals could not be disabled");
            }
            finally { foreach (var file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir); }
        });
        test("card goals freeze in both algorithm requests and survive IPC with legacy defaults", () =>
        {
            var old = new LocalSearchRequest("request", "snapshot", [], "root", 1, [], false);
            foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            {
                var request = LocalCalculation.Configure(old, order, 2, false, true, cardGoals: Goals(5));
                Check(request.CardGoals == Goals(5) && request.MaxNodes == 64 && request.BudgetSeconds == 60 && request.MaxRounds == 64,
                    "Options changed budget or missed an algorithm");
                Check(JsonSerializer.Deserialize<LocalSearchRequest>(JsonSerializer.Serialize(request))?.CardGoals == Goals(5), "IPC lost frozen goals");
            }
            Check(JsonSerializer.Deserialize<LocalSearchRequest>(JsonSerializer.Serialize(old))!.CardGoals == null, "Legacy request activated goals");
        });
    }
}
