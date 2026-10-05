using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Reconstruct the earlier start only inside the owned incident host. The source
// replay, actual game, and captured search result are never modified.
internal static class ReplayConsistencyIntegration
{
    public static async Task Run(string root, LocalSearchRequest frozen, string resultPath)
    {
        var result = LocalWire.Read<LocalSearchResult>(resultPath);
        var best = result.Best ?? throw new InvalidDataException("Missing frozen first-pass candidate");
        var reader = new PacketReader(); reader.Reset(frozen.Replay);
        var replay = reader.Read<CombatReplay>();
        if (frozen.History?.Count != 3 || best.Actions.Length < 4 || best.Actions.Take(3).Any(a => a.Choices is { Length: > 0 }))
            throw new InvalidDataException("Expected the captured three-action incident");
        int originalEvents = replay.events.Count;
        replay.events.Clear();
        var packet = new PacketWriter(); replay.Serialize(packet);
        var before = frozen with
        {
            Id = result.Id, SnapshotId = result.SnapshotId, NativeHash = best.Actions[0].BeforeHash,
            Replay = packet.Buffer.AsSpan(0, (packet.BitPosition + 7) / 8).ToArray(),
            History = new(0, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]))),
        };
        LocalWire.Write(Path.Combine(root, "integration-reconstructed-root-private.json"), before);
        System.Environment.SetEnvironmentVariable("SPIRE_LOCAL_EXECUTION_DIAGNOSE", "1");
        var samples = new List<object>();
        foreach (bool learning in new[] { false, true })
        {
            System.Environment.SetEnvironmentVariable("SPIRE_LOCAL_EXECUTION_LEARN", learning ? "1" : null);
            await ExecutionReplayIntegration.Run(root, before, resultPath);
            var actual = LocalWire.Read<LocalExecutionReport>(Path.Combine(root, "integration-execution-replay-private.json"));
            var rune = CombatManager.Instance.DebugOnlyGetState()!.Players.Single().Relics
                .Single(r => r.GetType().FullName == "HextechRunes.HiddenGemUpgradeRune");
            var ordinalField = rune.GetType().GetField("_localUpgradedPlayOrdinal", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int afterNativePlay = (int)ordinalField.GetValue(rune)!;
            var serialized = rune.ToSerializable();
            var restoredRune = RelicModel.FromSerializable(serialized);
            int afterNativeRestore = (int)ordinalField.GetValue(restoredRune)!;
            if (afterNativePlay <= 0 || afterNativeRestore != 0)
                throw new InvalidOperationException("Mod proc-state persistence gap was not reproduced");
            File.Copy(Path.Combine(root, "integration-execution-draw-private.json"),
                Path.Combine(root, $"integration-draw-{learning}-private.json"), true);
            File.Copy(Path.Combine(root, "integration-execution-replay-private.json"),
                Path.Combine(root, $"integration-execution-{learning}-private.json"), true);
            samples.Add(new { learning, actual.Stage, actual.SettledActions, actual.PlannedActions,
                actual.ActualHash, actual.ExpectedHash, actual.ActualHistory, actual.Message,
                mod_proc_ordinal_after_native_play = afterNativePlay,
                mod_proc_ordinal_after_native_restore = afterNativeRestore,
                mod_proc_ordinal_preserved_by_native_serialization = false,
                matched_live_stop = actual.ActualHash == frozen.NativeHash && actual.ActualHistory == frozen.History });
        }
        LocalWire.Write(Path.Combine(root, "integration-replay-consistency-summary.json"), new
        {
            reconstructed_prior_root = true, original_events = originalEvents, samples,
            searches = 0, source_replay_preserved = true,
        });
    }
}
