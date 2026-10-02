using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace SpireAiCoach.Mod;

// Installed only after LocalWorker's ownership/executable checks. No live-game patch.
// Completed choices are native data, not UI clicks or an index-only approximation.
internal sealed class LocalReplayChoices : IDisposable
{
    private static LocalReplayChoices? _active;
    private readonly Queue<CombatReplayEvent> _pending;
    public LocalReplayChoices(IEnumerable<CombatReplayEvent> events)
    {
        if (_active != null) throw new InvalidOperationException("Nested historical choice restoration");
        _pending = new(events.Where(e => e.eventType == CombatReplayEventType.PlayerChoice)); _active = this;
    }
    public static void Install(Harmony harmony)
    {
        harmony.Patch(typeof(CardSelectCmd).GetMethod("ShouldSelectLocalCard", BindingFlags.Static | BindingFlags.NonPublic)!,
            prefix: new(typeof(LocalReplayChoices).GetMethod(nameof(SelectRemote), BindingFlags.Static | BindingFlags.NonPublic)!));
        harmony.Patch(typeof(PlayerChoiceSynchronizer).GetMethod(nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice))!,
            prefix: new(typeof(LocalReplayChoices).GetMethod(nameof(ReceiveRecorded), BindingFlags.Static | BindingFlags.NonPublic)!));
        harmony.Patch(typeof(PlayerChoiceSynchronizer).GetMethod(nameof(PlayerChoiceSynchronizer.SyncLocalChoice))!,
            prefix: new(typeof(LocalReplayChoices).GetMethod(nameof(CheckRecorded), BindingFlags.Static | BindingFlags.NonPublic)!));
    }
    private static bool SelectRemote(ref bool __result)
    { if (_active == null) return true; __result = false; return false; }
    private static bool ReceiveRecorded(Player player, uint choiceId, ref Task<PlayerChoiceResult> __result)
    {
        if (_active == null) return true;
        var recorded = _active.Expected(player, choiceId);
        var result = PlayerChoiceResult.FromNetData(player, RunManager.Instance.DebugOnlyGetState()!, recorded.playerChoiceResult!.Value);
        // Keep the game's recording, IDs and pause/resume protocol intact.
        RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, choiceId, result);
        __result = Task.FromResult(result); return false;
    }
    private CombatReplayEvent Expected(Player player, uint choiceId)
    {
        if (!_pending.TryPeek(out var item) || item.playerId != player.NetId || item.choiceId != choiceId || item.playerChoiceResult == null)
            throw new InvalidOperationException("历史选择的原生身份不一致，停止恢复。");
        return item;
    }
    private static byte[] Bytes(NetPlayerChoiceResult result)
    { var writer = new PacketWriter(); result.Serialize(writer); return writer.Buffer.AsSpan(0, (writer.BitPosition + 7) / 8).ToArray(); }
    private static void CheckRecorded(Player player, uint choiceId, PlayerChoiceResult result)
    {
        if (_active == null) return;
        var item = _active.Expected(player, choiceId);
        if (!Bytes(item.playerChoiceResult!.Value).SequenceEqual(Bytes(result.ToNetData())))
            throw new InvalidOperationException("历史选择的原生结果不一致，停止恢复。");
        _active._pending.Dequeue();
    }
    public void Finish()
    { if (_pending.Count != 0) throw new InvalidOperationException("历史选择未完整恢复。"); }
    public void Dispose() { if (ReferenceEquals(_active, this)) _active = null; }
}
