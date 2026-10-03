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
        test("final verification skipped advice cannot become an executable continuation", () =>
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
            Check(text.Contains("未复核") && text.Contains("手动对照") && text.Contains("自动执行与路线续用不可用"),
                "Advice must expose the omitted independent check");
            Check(!JsonSerializer.Deserialize<LocalSearchResult>(JsonSerializer.Serialize(verified))!.VerificationSkipped,
                "Existing result metadata remains compatible");
        });
    }
}
