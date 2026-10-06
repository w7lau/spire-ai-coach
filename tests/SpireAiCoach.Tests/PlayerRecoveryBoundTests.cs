using System.Text.Json;
using SpireAiCoach.Core;

static class PlayerRecoveryBoundTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Register(Action<string, Action> test)
    {
        test("player-content loss proof closes native siblings and retains its conditional scope", () =>
        {
            var r = new LocalSearchRequest("content", "snapshot", [], "native", 1, ["unrelated-mod:1"], false);
            var a = new LocalAction(0, "first", null, "first", "", "native", 1);
            var b = a with { ModelId = "second" };
            var proof = new LocalMinimumLossProof(r);
            foreach (var action in new[] { a, b })
                proof.Observe(new(50, [new(action, [a, b], [], new("snapshot:native", 50, 41, 0, 0, ContentScoped: true))],
                    41, Won: true));
            var certificate = proof.Status.Certificate!;
            Check(certificate is { MinimumNetHpLoss: 9, MaximumFinalHp: 41, ContentScoped: true }, "Content bound did not propagate");
            var candidate = new LocalCandidate([a], 41, 9, 0, 0, 75, true, false, false, StartingHp: 50);
            Check(LocalSearchPolicy.CanStopAtMinimum(candidate, r, certificate), "A reached conditional minimum could not stop");
            var result = new LocalSearchResult(r.Id, r.SnapshotId, "done", "", 2, 0, 1, candidate,
                MinimumLoss: proof.Status with { Confirmed = true }, StoppedOnMinimum: true);
            var text = LocalSearchPolicy.FormatAdvice(result);
            Check(text.Contains("按当前玩家内容") && text.Contains("最低净损失 9") && !text.Contains("已证明最低净损失"),
                "Conditional content bounds were displayed as a global certificate");
            Check(JsonSerializer.Deserialize<LocalMinimumLossCertificate>(JsonSerializer.Serialize(certificate))!.ContentScoped,
                "Worker transport lost the content domain");
        });
        test("content recovery preserves possible healing above the current winner", () =>
        {
            var winner = new LocalWinningBound("root", 50, 9, 0, 41);
            Check(LocalHealthBound.CannotImprove(new("root", 50, 40, 0, 0, true), winner), "No-recovery worse branch remained open");
            Check(!LocalHealthBound.CannotImprove(new("root", 50, 40, 0, 10, true), winner), "Fixed future healing was discarded");
            Check(!LocalHealthBound.CannotImprove(new("root", 50, 40, 0, 35, true), winner), "Repeatable healing cap was discarded");
            Check(!LocalHealthBound.CannotImprove(new("root", 50, 40, 0, null, true), winner), "Dynamic cap growth became zero recovery");
        });
        test("legacy bounds stay unscoped and source aggregation retains player-content scope", () =>
        {
            var legacy = JsonSerializer.Deserialize<LocalMinimumLossCertificate>("""{"Scope":"root","StartingHp":50,"MinimumNetHpLoss":9,"MinimumPotionsUsed":0}""")!;
            Check(!legacy.ContentScoped, "Old certificates changed domain");
            Check(LocalRecoveryAllowance.Sum([new(6), new(2, ContentScoped: true)]) is { MaximumFurtherHpGain: 8, ContentScoped: true },
                "Aggregated healing forgot its assumptions");
        });
    }
}
