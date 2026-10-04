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
