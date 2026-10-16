using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Read a frozen one-action incident; reconstruct its exact earlier root only
// inside the owned integration host. No player process or save is touched.
internal static class ReflectReplayIntegration
{
    public static async Task Run(string root, LocalSearchRequest frozen, string resultPath)
    {
        var result = LocalWire.Read<LocalSearchResult>(resultPath);
        var best = result.Best ?? throw new InvalidDataException("Missing frozen route");
        var reader = new PacketReader(); reader.Reset(frozen.Replay);
        var replay = reader.Read<CombatReplay>();
        if (frozen.History?.Count != 1 || best.Actions[0].ModelId != "CARD.REFLECT")
            throw new InvalidDataException("Expected one-action Reflect incident");
        replay.events.Clear();
        var packet = new PacketWriter(); replay.Serialize(packet);
        var before = frozen with { Id = result.Id, SnapshotId = result.SnapshotId,
            NativeHash = best.Actions[0].BeforeHash,
            Replay = packet.Buffer.AsSpan(0, (packet.BitPosition + 7) / 8).ToArray(),
            History = new(0, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]))) };
        LocalWire.Write(Path.Combine(root, "reconstructed-root.json"), before);
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_REFLECT_VALIDATE") == "1")
        {
            await ModReplayIntegration.Run(root, (Godot.SceneTree)Godot.Engine.GetMainLoop(), before, resultPath,
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_REFLECT_REPLAY")!, circleOfDeath: true);
            return;
        }
        System.Environment.SetEnvironmentVariable("SPIRE_LOCAL_EXECUTION_DIAGNOSE", "1");
        await ExecutionReplayIntegration.Run(root, before, resultPath);
        var player = CombatManager.Instance.DebugOnlyGetState()!.Players.Single();
        var relics = player.Relics.Select(r => new { type = r.GetType().FullName, id = r.Id.ToString(),
            fields = r.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsEnum)
                .Select(f => new { name = f.Name, value = f.GetValue(r)?.ToString() }).ToArray() }).ToArray();
        LocalWire.Write(Path.Combine(root, "reflect-relics-private.json"), relics);
    }
}
