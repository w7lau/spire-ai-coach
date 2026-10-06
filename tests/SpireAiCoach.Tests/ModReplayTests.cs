using System.Text.Json;
using SpireAiCoach.Core;

internal static class ModReplayTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Mod replay assertion failed"); }
        var field = new LocalRelicReplayField(1, 3, "RELIC.RUNE", "Mod.Rune", "module", "ordinal", 1);
        var state = new LocalModReplayCheckpoint([field], [field with { Value = 2 }]);
        var request = new LocalSearchRequest("r", "snapshot", [1], "native", 42, ["mod:1"], false, ModReplay: state);
        test("supplemental Mod state distinguishes equal native states and preserves the native-only format", () =>
        {
            Check(LocalModReplayCheckpoint.Fingerprint("native", []) == "native");
            Check(LocalModReplayCheckpoint.Fingerprint("native", state.Initial) !=
                LocalModReplayCheckpoint.Fingerprint("native", state.Current));
            Check(LocalModReplayCheckpoint.Fingerprint("native", state.Current) !=
                LocalModReplayCheckpoint.Fingerprint("changed", state.Current));
        });
        test("local IPC retains independent initial and current Mod state without defaulting legacy requests", () =>
        {
            var restored = JsonSerializer.Deserialize<LocalSearchRequest>(JsonSerializer.Serialize(request))!;
            Check(restored.ModReplay!.Initial.SequenceEqual(state.Initial));
            Check(restored.ModReplay.Current.SequenceEqual(state.Current));
            var legacy = JsonSerializer.Serialize(request with { ModReplay = null });
            Check(JsonSerializer.Deserialize<LocalSearchRequest>(legacy)!.ModReplay == null);
        });
        test("retained search rejects changes in initial and current Mod state or receiver identity", () =>
        {
            using var session = new LocalSearchSession(request);
            Check(session.Matches(request with { Id = "next", Workers = 8 }));
            foreach (var changed in new[] {
                state with { Initial = [field with { Value = 0 }] },
                state with { Current = [field with { Value = 3 }] },
                state with { Initial = [field with { RelicIndex = 4 }] },
                state with { Initial = [field with { ModuleId = "changed" }] } })
                Check(!session.Matches(request with { ModReplay = changed }));
        });
        test("missing supplemental Mod replay data does not restart an ordinary compatibility search", () =>
        {
            var failure = LocalSimulationFailure.Capture(new CoachException("local_mod_replay", "Missing root"), 0, "search");
            var result = new LocalSearchResult("r", "snapshot", "failed", "Missing root", 0, 0, 0, null, Failure: failure);
            Check(LocalSearchRecovery.AbortPass(request, result));
            Check(!LocalSearchRecovery.NeedsCompatibilityPass(request, [result]));
        });
    }
}
