using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

public sealed record LocalInstallation(string GameDirectory, string[] ModDirectories);

public static class LocalCapture
{
    public static string Fingerprint()
    {
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("No combat");
        var writer = new PacketWriter();
        NetFullCombatState.FromRun(state.RunState, null).Serialize(writer);
        var extra = Encoding.UTF8.GetBytes($"|{state.RoundNumber}|" + string.Join(";",
            state.Enemies.Select(e => $"{e.CombatId}:{e.Monster?.NextMove?.Id}")));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(writer.Buffer.AsSpan(0, (writer.BitPosition + 7) / 8));
        hash.AppendData(extra);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string[] LoadedMods() => ModManager.GetLoadedMods()
        .Select(m => m.manifest!.id + ":" + m.manifest.version + ":" +
            string.Join(",", m.assemblies.Select(a => a.ManifestModule.ModuleVersionId.ToString()).Order(StringComparer.Ordinal)))
        .Append("sts2:" + typeof(RunManager).Assembly.ManifestModule.ModuleVersionId)
        .Order(StringComparer.Ordinal).ToArray();

    // Called on the live main thread. Serialize the already-recorded replay without modifying it.
    public static LocalSearchRequest Capture(string snapshotId, bool continueOptimization)
    {
        var manager = RunManager.Instance;
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null || state.Players.Count != 1 || manager.NetService.Type != NetGameType.Singleplayer)
            throw new CoachException("local_unsupported", "本地模式目前仅支持单人战斗。");
        var player = LocalContext.GetMe(state)!;
        if (!manager.ActionQueueSet.BecameEmpty().IsCompletedSuccessfully ||
            player.PlayerCombatState?.PlayPile.IsEmpty != true || CombatManager.Instance.PlayerActionsDisabled)
            throw new CoachException("local_busy", "请等所有出牌和选择结算完成后再计算。");
        var replay = typeof(CombatReplayWriter).GetField("_replay", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(manager.CombatReplayWriter) as CombatReplay;
        if (replay == null)
            throw new CoachException("local_replay", "这场战斗没有可用的原生重放记录，请在下一场战斗重试。");
        if (replay.events.Any(e => e.eventType is CombatReplayEventType.PlayerChoice or CombatReplayEventType.ResumeAction))
            throw new CoachException("local_choice", "当前战斗记录包含额外选牌或选择流程，本地重放暂不支持；可使用 AI 指导。");
        var before = Fingerprint();
        var packet = new PacketWriter();
        replay.Serialize(packet);
        if (before != Fingerprint()) throw new CoachException("local_changed", "采集期间战斗发生变化，请重试。");
        return new(Guid.NewGuid().ToString("N"), snapshotId,
            packet.Buffer.AsSpan(0, (packet.BitPosition + 7) / 8).ToArray(), before,
            ModelIdSerializationCache.Hash, LoadedMods(), continueOptimization,
            TargetLabels: state.Enemies.Where(e => e.IsAlive && e.CombatId.HasValue)
                .OrderBy(e => e.GetCreatureNode()?.GlobalPosition.X ?? float.MaxValue)
                .Select((e, index) => new { Id = e.CombatId!.Value, Label = $"从左到右第 {index + 1} 个敌人「{e.Name}」" })
                .ToDictionary(e => e.Id, e => e.Label));
    }

    public static LocalInstallation Installation() => new(
        Path.GetDirectoryName(OS.GetExecutablePath())!, ModManager.GetLoadedMods().Select(m => m.path).ToArray());
}
