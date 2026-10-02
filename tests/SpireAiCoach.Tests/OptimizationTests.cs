using SpireAiCoach.Core;

static class OptimizationTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
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
        LocalSearchResult Result()
        {
            var actions = Enumerable.Range(0, 3).Select(i => new LocalAction(i, "card", 1, "card", "", "state" + i, 1)).ToArray();
            var points = Enumerable.Range(0, 3).Select(i => new LocalContinuationPoint(i, "state" + i, new(10 + i, "history" + i), i * 2)).ToArray();
            return new("id", "snapshot", "done", "", 1, 0, 100, new(actions, 76, 4, 0, 99, 80, true, false, false, Continuation: points));
        }
        test("optimization continuation reuses exact forward history and adjusts remaining damage", () =>
        {
            var plan = new LocalContinuation("fight", ["mod"], Result());
            Check(plan.Advance("fight", ["mod"], "state0", new(10, "history0"))?.Best?.Actions.Length == 3, "Root");
            var next = plan.Advance("fight", ["mod"], "state2", new(12, "history2"));
            Check(next?.Best?.Actions.Length == 1 && next.Best.HpLost == 0 && plan.CompletedActions == 2, "Skip observed intermediate frames safely");
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
