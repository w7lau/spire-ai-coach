using SpireAiCoach.Core;

static class SearchAlgorithmTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Register(Action<string, Action> test)
    {
        test("search tactics distinguish in-hand turn-end loss from a play payment", () =>
        {
            var payment = LocalTactics.Priority(new(Known: true, HpCost: 6));
            var removeHeldLoss = LocalTactics.Priority(new(Known: true, HandEndHpLoss: 6));
            var emptyDefense = LocalTactics.Priority(new(Known: true, Block: 5));
            var protectedEnd = LocalTactics.Priority(new(Known: true, EndTurn: true,
                Incoming: 10, CurrentBlock: 20, Hp: 40, HandEndHpLoss: 6));
            Check(payment < 0, "An actual play payment must remain a cost");
            Check(removeHeldLoss > emptyDefense && removeHeldLoss > protectedEnd,
                "Explore removing the harmful hand effect before wasting defense or ending");
            Check(protectedEnd < 0, "Attack block must not erase a separate turn-end HP loss");
            Check(LocalTactics.Priority(new(Known: true, EndTurn: true, Hp: 6, HandEndHpLoss: 6)) <= -40,
                "Do not treat a potentially fatal in-hand effect as a safe end turn");
        });
        test("search choice proposals preserve unique model intent after a changed prefix", () =>
        {
            var old = new LocalCardChoice("old", 1, "CARD.SETUP", "old display", [1], "pile");
            var current = old with { OfferHash = "new", Index = 3, Indices = [3], Name = "new display" };
            Check(LocalRouteRefiner.ProposeChoice(old, [current]) == current,
                "Propose the unique native legal model, recording its new offer and coordinates");
            Check(LocalRouteRefiner.ProposeChoice(old, [old, current]) == old, "Exact offers take precedence");
            Check(LocalRouteRefiner.ProposeChoice(old, [current, current with { Index = 4, Indices = [4] }]) == null,
                "Duplicate instances need fresh exploration, not a guessed identity");
            Check(LocalRouteRefiner.ProposeChoice(old, [current with { ModelId = "CARD.OTHER" }]) == null,
                "Display names cannot substitute a different model");
            Check(LocalRouteRefiner.ProposeChoice(old with { Indices = [1, 2] }, [current]) == null,
                "A changed multi-select offer is a new decision");
            Check(LocalRouteRefiner.ProposeChoice(old, [current with { Kind = "hand" }]) == null,
                "Do not carry pile choice intent to a different selection context");
        });
        test("search choice proposals continue a combo when a changed offer removes an earlier intent", () =>
        {
            var earlier = new LocalCardChoice("old", 0, "CARD.DEFENSE", "defense", [0], "pile");
            var later = earlier with { Index = 3, Indices = [3], ModelId = "CARD.FINISHER" };
            var available = later with { OfferHash = "new", Index = 1, Indices = [1] };
            LocalCardChoice[] legal = [earlier with { OfferHash = "new" },
                earlier with { OfferHash = "new", Index = 2, Indices = [2] }, available];
            int next = 0;
            Check(LocalRouteRefiner.ProposeNextChoice([earlier, later], legal, ref next) == available && next == 2,
                "Ambiguous old instances must not hide a later unique legal combination proposal");
            Check(LocalRouteRefiner.ProposeNextChoice([earlier, later], legal, ref next) == null,
                "Do not reuse the same planned choice for extra offers");
            next = 0;
            Check(LocalRouteRefiner.ProposeNextChoice([earlier, later], [earlier, later], ref next) == earlier && next == 1,
                "An unchanged native offer keeps its exact first choice");
        });
        test("search refinement proposes coupled end-turn plays and changed repeated choice order", () =>
        {
            var end1 = Move("end") with { EndTurn = true, ModelId = "", CombatCardIndex = null };
            var end2 = end1 with { Round = 2 };
            var costlessDefense = Move("empty-defense") with { CombatCardIndex = 1, Preference = -12 };
            var held1 = Move("held-risk") with { CombatCardIndex = 2, Preference = 24 };
            var held2 = held1 with { Round = 2, CombatCardIndex = 3 };
            var early = new LocalCardChoice("one", 0, "CARD.DEFENSE", "", [0], "pile");
            var late = new LocalCardChoice("two", 1, "CARD.FINISHER", "", [1], "pile");
            var pull = Move("retrieve") with { Round = 3, CombatCardIndex = 4, Choices = [early, late] };
            var refiner = new LocalRouteRefiner();
            refiner.Offer(new([end1, end2, pull], 16, 24, 0, 0, 40, true, false, false,
                Decisions: [new(0, [costlessDefense, held1, end1]), new(1, [held2, end2])], StartingHp: 40));
            var proposals = new List<LocalAction[]>();
            while (refiner.TryTake(out var plan)) proposals.Add(plan);
            Check(proposals.Any(p => p.Count(a => a.ModelId == "held-risk") == 2),
                "Two turn-end improvements must not require either alone to improve the incumbent");
            Check(proposals.Any(p => p.Count(a => a.ModelId == "held-risk") == 2 &&
                p.Single(a => a.ModelId == "retrieve").Choices![0] == late),
                "A changed repeated offer needs a candidate retaining the finisher intent");
            Check(proposals.All(p => p.Count(a => a.EndTurn) == 2), "Do not move or remove native turn boundaries");
        });
        test("search feedback finds strength vulnerability upgrades and energy before attacks", () =>
        {
            foreach (var setup in new[] { "strength", "vulnerable", "upgrade", "energy" })
            {
                var scenario = new Scenario(["hit", "hit", setup], setup == "energy" ? 12 : 18, 0, 8, setup == "energy" ? 1 : 3);
                var old = Run(scenario, actions => actions.OrderByDescending(a => a.Preference).First());
                Check(!old.Won, $"Fixture must distinguish old attack-first behavior: {setup}");
                var (best, _) = Search(scenario, 32);
                Check(best.Won && best.Actions[0].ModelId == setup, $"Did not discover setup first: {setup}");
                Check(best.HpLost == 0, "Winning setup should prevent enemy damage");
            }
        });
        test("search feedback omits empty defense but keeps needed defense and shield-breaking chains", () =>
        {
            var (idle, _) = Search(new(["defend", "defend", "hit"], 50, 20, 0, 3), 32);
            Check(idle.Actions.Length == 1 && idle.Actions[0].EndTurn, "Prefer skipping defense and attacks with no payoff");
            var (defended, _) = Search(new(["defend", "defend"], 50, 0, 10, 2), 32);
            Check(defended.HpLost == 0 && defended.Actions.Count(a => a.ModelId == "defend") == 2, "Do not discard needed defense");
            var (broken, _) = Search(new(["hit", "hit"], 2, 10, 8, 2), 32);
            Check(broken.Won && broken.Actions.Count(a => a.ModelId == "hit") == 2, "First hit into block enables the second hit");
        });
        test("search feedback learns only measured branches while retaining exploration", () =>
        {
            var tree = new LocalSearchTree(123);
            var choices = new[] { Move("bad"), Move("good"), Move("unknown") };
            var counts = choices.ToDictionary(a => a.ModelId, _ => 0);
            for (int i = 0; i < 120; i++)
            {
                var trial = tree.Begin(); var action = tree.Select(trial, choices); counts[action.ModelId]++;
                var won = action.ModelId == "good";
                tree.Complete(trial, new([action], won ? 80 : 10, won ? 0 : 70, won ? 0 : 30, 0, 80, won, false, false), 30);
            }
            Check(counts.Values.All(n => n > 0), "Every root alternative needs an opportunity");
            Check(counts["good"] > counts["bad"] * 3, "Measured wins should get more search effort");
            Check(tree.CompletedTrials == 120, "Backpropagation must count each trial once");
        });
        test("search feedback separates exact instances targets and action history", () =>
        {
            var tree = new LocalSearchTree(0);
            var a = Move("same"); var choices = new[] { a, a with { HandIndex = 1 }, a with { TargetId = 2 }, a with { PotionSlot = 0 } };
            var seen = new HashSet<LocalAction>();
            for (int i = 0; i < choices.Length; i++)
            {
                var trial = tree.Begin(); seen.Add(tree.Select(trial, choices));
                tree.Select(trial, [Move("tail")]);
                tree.Complete(trial, new([], 80, 0, 0, 0, 80, true, false, false), 10);
            }
            Check(seen.Count == 4 && tree.Nodes == 9, "Distinct prefix histories must never share child statistics");
        });
        test("search feedback bounded memory and terminal reward ordering", () =>
        {
            var tree = new LocalSearchTree(0, 3);
            var trial = tree.Begin();
            for (int i = 0; i < 100; i++) tree.Select(trial, [Move(i.ToString())]);
            tree.Complete(trial, new([], 80, 0, 0, 0, 80, true, false, false), 10);
            Check(tree.Nodes == 3 && tree.CompletedTrials == 1, "Capacity limits statistics, not execution");
            var lowWin = new LocalCandidate([], 1, 200, 0, 0, 80, true, false, false);
            var unfinished = lowWin with { Hp = 80, HpLost = 0, Won = false, EnemyHp = 1 };
            Check(LocalSearchTree.Reward(lowWin, 100) > LocalSearchTree.Reward(unfinished, 100), "Unfinished must not beat a victory");
            var healed = lowWin with { Hp = 80, HpLost = 10 };
            Check(!LocalSearchPolicy.Better(healed with { HpLost = 0 }, healed), "Equal final HP must not reject a route solely for already healed costs");
        });
        test("search feedback never resimulates a closed exact terminal history", () =>
        {
            var tree = new LocalSearchTree(5);
            var seen = new HashSet<string>();
            int trials = 0;
            while (!tree.Exhausted && trials < 20)
            {
                var trial = tree.Begin();
                var first = tree.Select(trial, [Move("a"), Move("b")]);
                var second = tree.Select(trial, [Move("c"), Move("d")]);
                Check(seen.Add(first.ModelId + second.ModelId), "Exact terminal history repeated");
                tree.Complete(trial, new([first, second], 80, 0, 0, 0, 80, true, false, false), 10, closeExactPrefix: true);
                trials++;
            }
            Check(trials == 4 && tree.Exhausted, "All four paths should close the root");
        });
    }

    private static LocalAction Move(string id) => new(0, id, 1, id, "enemy", "state", 1);
    private sealed record Scenario(string[] Hand, int EnemyHp, int EnemyBlock, int Incoming, int Energy);

    private static (LocalCandidate Best, LocalSearchTree Tree) Search(Scenario scenario, int trials)
    {
        var tree = new LocalSearchTree(1729);
        LocalCandidate? best = null;
        for (int i = 0; i < trials && !tree.Exhausted; i++)
        {
            var trial = tree.Begin();
            var result = Run(scenario, legal => tree.Select(trial, legal));
            tree.Complete(trial, result, scenario.EnemyHp, closeExactPrefix: true);
            if (LocalSearchPolicy.Better(result, best)) best = result;
        }
        return (best!, tree);
    }

    // Deliberately tiny deterministic combat, not a replacement for native-game verification.
    // The first list positions are attacks: ordering by hand index or type fails these fixtures.
    private static LocalCandidate Run(Scenario scenario, Func<LocalAction[], LocalAction> choose)
    {
        var hand = scenario.Hand.ToList();
        int enemy = scenario.EnemyHp, shield = scenario.EnemyBlock, energy = scenario.Energy, strength = 0, block = 0, hp = 80;
        bool vulnerable = false, upgraded = false;
        var actions = new List<LocalAction>();
        while (enemy > 0)
        {
            var hash = $"{enemy}:{shield}:{energy}:{strength}:{block}:{vulnerable}:{upgraded}:{string.Join(',', hand)}";
            var legal = energy > 0 ? hand.Select((id, i) => new LocalAction(i, id, 1, id, "enemy", hash, 1,
                Preference: id == "hit" ? 30 : 10)).ToList() : [];
            legal.Add(new(-1, "", null, "", "", hash, 1, EndTurn: true));
            var action = choose(legal.ToArray()); actions.Add(action);
            if (action.EndTurn) { hp -= Math.Max(0, scenario.Incoming - block); break; }
            hand.RemoveAt(action.HandIndex); energy--;
            switch (action.ModelId)
            {
                case "strength": strength += 3; break;
                case "vulnerable": vulnerable = true; break;
                case "upgrade": upgraded = true; break;
                case "energy": energy += 2; break;
                case "defend": block += 5; break;
                case "hit":
                    int damage = (int)(((upgraded ? 9 : 6) + strength) * (vulnerable ? 1.5 : 1));
                    int absorbed = Math.Min(shield, damage); shield -= absorbed; enemy = Math.Max(0, enemy - damage + absorbed); break;
            }
        }
        return new(actions.ToArray(), hp, 80 - hp, enemy, 0, 80, enemy == 0, hp <= 0, false, 1, "synthetic");
    }
}
