using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.GameActions;
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
    private static CombatReplay Replay() => typeof(CombatReplayWriter).GetField("_replay", BindingFlags.Instance | BindingFlags.NonPublic)
        ?.GetValue(RunManager.Instance.CombatReplayWriter) as CombatReplay ?? throw new InvalidOperationException("No combat replay");

    // The native network action encodes the combat-card instance, unlike its current hand index.
    // Normalize only readiness/resume bookkeeping. Keep the exact ordered native choice results.
    public static LocalHistoryStamp History()
    {
        var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState()!)!;
        var entries = new List<string>();
        foreach (var item in Replay().events)
        {
            if (item.eventType is CombatReplayEventType.HookAction or CombatReplayEventType.ResumeAction) continue;
            if (item.eventType == CombatReplayEventType.PlayerChoice)
            {
                if (item.playerChoiceResult is not { } choice || item.choiceId == null)
                    throw new InvalidOperationException("Incomplete native choice history");
                var packet = new PacketWriter(); choice.Serialize(packet);
                entries.Add($"{item.playerId}:choice:{item.choiceId}:{Convert.ToHexString(packet.Buffer.AsSpan(0, (packet.BitPosition + 7) / 8))}");
                continue;
            }
            if (item.eventType != CombatReplayEventType.GameAction || item.action == null)
                throw new InvalidOperationException("Unsupported replay choice for continuation");
            var action = item.action.ToGameAction(player);
            string? entry = action switch
            {
                PlayCardAction card => $"card:{card.NetCombatCard.CombatCardIndex}:{card.CardModelId}:{card.TargetId}",
                UsePotionAction potion => $"potion:{potion.PotionIndex}:{potion.TargetId}",
                EndPlayerTurnAction => "end",
                ReadyToBeginEnemyTurnAction => null,
                _ => throw new InvalidOperationException("Unsupported replay action for continuation")
            };
            if (entry != null) entries.Add($"{item.playerId}:{entry}");
        }
        return new(entries.Count, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries)))));
    }

    public static bool Stable()
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        var pcs = state == null ? null : LocalContext.GetMe(state)?.PlayerCombatState;
        return pcs?.Phase == PlayerTurnPhase.Play && pcs.PlayPile.IsEmpty && !CombatManager.Instance.PlayerActionsDisabled &&
            RunManager.Instance.ActionQueueSet.BecameEmpty().IsCompletedSuccessfully;
    }

    public static string Fingerprint()
    {
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("No combat");
        var writer = new PacketWriter();
        NetFullCombatState.FromRun(state.RunState, null).Serialize(writer);
        var extra = Encoding.UTF8.GetBytes($"|{state.RoundNumber}|" + string.Join(";",
            state.Enemies.Select(e => $"{e.CombatId}:{e.Monster?.NextMove?.Id}")) + "|potions|" +
            string.Join(";", state.Players.SelectMany(p => p.PotionSlots.Select((potion, slot) =>
                $"{p.NetId}:{slot}:{potion?.Id}:{potion?.IsQueued}"))));
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
        var replay = Replay();
        if (replay == null)
            throw new CoachException("local_replay", "这场战斗没有可用的原生重放记录，请在下一场战斗重试。");
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
                .ToDictionary(e => e.Id, e => e.Label), History: History());
    }

    public static LocalInstallation Installation() => new(
        Path.GetDirectoryName(OS.GetExecutablePath())!, ModManager.GetLoadedMods().Select(m => m.path).ToArray());

}
