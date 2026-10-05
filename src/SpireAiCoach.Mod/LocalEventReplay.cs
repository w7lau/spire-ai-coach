using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

internal static class LocalEventReplay
{
    // Native event choices are already recorded on the player's map history.
    // Read them without choosing anything, changing the recorder, or using RNG.
    public static LocalEventEntry? Capture(Player player)
    {
        if (player.RunState.CurrentRoom is not CombatRoom { ParentEventId: { } eventId }) return null;
        var choices = player.RunState.CurrentMapPointHistoryEntry?.GetEntry(player.NetId).EventChoices;
        if (choices == null || choices.Count == 0)
            throw new CoachException("local_event_entry", "这场事件战斗缺少进入战斗的原生选项记录。");
        return new(eventId.ToString(), choices.Select(c => new LocalEventOption(c.Title.LocTable, c.Title.LocEntryKey)).ToArray());
    }

    public static async Task Restore(LocalEventEntry? entry, RunState run)
    {
        var manager = RunManager.Instance;
        if (run.CurrentRoom is not EventRoom)
        {
            if (entry != null) throw new CoachException("local_event_entry", "事件战斗入口没有恢复到对应事件房间。");
            return;
        }
        if (entry == null || entry.Choices.Length == 0)
            throw new CoachException("local_event_entry", "战斗重放停在事件页且缺少入口记录；已停止，避免等待事件选择超时。");
        var sync = manager.EventSynchronizer;
        for (int step = 0; step < entry.Choices.Length; step++)
        {
            if (run.CurrentRoom is not EventRoom || CombatManager.Instance.IsInProgress)
                throw new CoachException("local_event_entry", "事件入口在全部选项重放前发生了变化。");
            var model = sync.GetLocalEvent();
            int index = entry.Resolve(model.Id.ToString(), step, model.CurrentOptions.Select(o =>
                new LocalEventOption(o.HistoryName.LocTable, o.HistoryName.LocEntryKey, o.IsLocked || o.WasChosen)).ToArray());
            // The original synchronizer performs shared/non-shared selection,
            // all event effects, RNG, nested room entry and Mod callbacks.
            sync.ChooseLocalOption(index);
            await sync.AwaitPendingOptionTasks();
        }
        // The final native option may start its room transition asynchronously.
        // StableOrTerminal still checks native combat settlement immediately after this.
    }
}
