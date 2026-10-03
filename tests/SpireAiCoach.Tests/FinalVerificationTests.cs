using System.Text.Json;
using SpireAiCoach.Core;

static class FinalVerificationTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        test("final verification defaults on for legacy requests and preserves explicit opt-out", () =>
        {
            var legacy = JsonSerializer.Deserialize<LocalSearchRequest>("{\"Id\":\"id\",\"SnapshotId\":\"s\",\"Replay\":\"\",\"NativeHash\":\"h\",\"ModelHash\":0,\"LoadedMods\":[],\"ContinueOptimization\":false}")!;
            Check(!legacy.SkipFinalVerification, "Omitting the new option must retain final verification");
            var copy = JsonSerializer.Deserialize<LocalSearchRequest>(JsonSerializer.Serialize(legacy with { SkipFinalVerification = true }))!;
            Check(copy.SkipFinalVerification && !copy.DeferVerification, "Product opt-out is distinct from per-worker deferred verification");
            foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            {
                var verified = LocalCalculation.Configure(copy, order, 8, true, true);
                var skipped = LocalCalculation.Configure(copy, order, 8, true, true, true);
                Check(!verified.SkipFinalVerification && skipped.SkipFinalVerification, "Both buttons must freeze the explicit UI option");
                Check(skipped == verified with { SkipFinalVerification = true } && skipped.MaxNodes == 64 && skipped.BudgetSeconds == 60,
                    "Skipping final verification cannot change simulation, search order or budget");
            }
        });
        test("final verification skipped legacy advice needs its own complete search checkpoints", () =>
        {
            var move = new LocalAction(0, "CARD", 1, "card", "enemy", "root", 1, CombatCardIndex: 7);
            var history = new LocalHistoryStamp(3, "history");
            var candidate = new LocalCandidate([move], 80, 10, 0, 0, 80, true, false, false,
                Continuation: [new(0, "root", history, 0)], StartingHp: 80);
            var verified = new LocalSearchResult("id", "state", "done", "", 1, 0, 100, candidate);
            Check(new LocalContinuation("combat", ["mod"], verified).Advance("combat", ["mod"], "root", history) != null,
                "Existing verified continuation remains usable");
            var skipped = JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(verified with { VerificationSkipped = true }))!;
            Check(new LocalContinuation("combat", ["mod"], skipped).Advance("combat", ["mod"], "root", history) == null,
                "Explicit unverified metadata rejects even accidentally retained execution points");
            var text = LocalSearchPolicy.Format(skipped);
            Check(text.Contains("跳过最终复核") && text.Contains("手动查看") && text.Contains("暂不能自动执行"),
                "Advice must expose the omitted independent check");
            Check(!JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(verified))!.VerificationSkipped,
                "Existing result metadata remains compatible");
        });
        test("final verification skipped search checkpoints permit guarded execution and suffix reuse", () =>
        {
            var first = new LocalAction(0, "CARD", 1, "card", "enemy", "root", 1, CombatCardIndex: 7);
            var last = new LocalAction(-1, "", null, "", "", "next", 1, EndTurn: true);
            var points = new LocalContinuationPoint[] { new(0, "root", new(3, "before"), 0, 80), new(1, "next", new(5, "after-choice"), 2, 78) };
            var best = new LocalCandidate([first, last], 80, 2, 0, 0, 80, true, false, false,
                Continuation: points, StartingHp: 80, ContinuationFromSearch: true);
            var result = JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(
                new LocalSearchResult("id", "state", "done", "", 1, 0, 100, best, VerificationSkipped: true)))!;
            Check(LocalSearchPolicy.HasExecutionPoints(result), "Complete first-pass checkpoints need no independent replay");
            var plan = new LocalContinuation("combat", ["mod"], result);
            Check(plan.Advance("combat", ["mod"], "root", points[0].History) != null, "Root execution guard failed");
            var suffix = plan.Advance("combat", ["mod"], "next", points[1].History, requireProgress: true)!;
            Check(plan.CompletedActions == 1 && suffix.Best!.Actions.Length == 1 && suffix.Best.StartingHp == 78 &&
                suffix.Best.HpLost == 0 && LocalSearchPolicy.HasExecutionPoints(suffix), "Suffix checkpoints must remain aligned with remaining actions");
            Check(LocalSearchPolicy.Format(suffix).Contains("可点击执行方案"), "Manual progress cannot turn an executable suffix into a manual-only claim");
            Check(new LocalContinuation("combat", ["mod"], result).Advance("combat", ["mod"], "root", new(3, "wrong")) == null,
                "Matching visible native state cannot replace matching action and choice history");
            Check(new LocalContinuation("combat", ["mod"], result).Advance("other", ["mod"], "root", points[0].History) == null &&
                new LocalContinuation("combat", ["mod"], result).Advance("combat", ["changed"], "root", points[0].History) == null,
                "Skipping final replay cannot bypass combat or loaded-Mod guards");
        });
        test("final verification skipped execution rejects incomplete shifted or stagnant histories", () =>
        {
            var actions = new[] { new LocalAction(0, "CARD", 1, "card", "enemy", "a", 1),
                new LocalAction(-1, "", null, "", "", "b", 1, EndTurn: true) };
            var points = new[] { new LocalContinuationPoint(0, "a", new(0, "h0"), 0), new LocalContinuationPoint(1, "b", new(1, "h1"), 0) };
            var best = new LocalCandidate(actions, 80, 0, 0, 0, 80, true, false, false, Continuation: points, ContinuationFromSearch: true);
            var result = new LocalSearchResult("id", "state", "done", "", 1, 0, 100, best, VerificationSkipped: true);
            foreach (var bad in new[] { best with { Continuation = [points[0]] }, best with { Continuation = [points[0], points[1] with { ActionIndex = 0 }] },
                best with { Continuation = [points[0], points[1] with { NativeHash = "different" }] },
                best with { Continuation = [points[0], points[1] with { History = points[0].History }] }, best with { Dead = true } })
            {
                var invalid = result with { Best = bad };
                Check(!LocalSearchPolicy.HasExecutionPoints(invalid) &&
                    new LocalContinuation("combat", [], invalid).Advance("combat", [], "a", points[0].History) == null,
                    "Every step needs its own matching native state and forward history");
            }
        });
    }
}
