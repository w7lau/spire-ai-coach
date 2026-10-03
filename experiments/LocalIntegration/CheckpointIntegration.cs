using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Deliberately probe native save-as-checkpoint in the owned synthetic host. A failure
// is evidence against adopting that shortcut, never a reason to relax native checks.
public static class CheckpointIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, StateCapture capture, Player player)
    {
        for (int i = 0; i < 2; i++)
        {
            var card = player.PlayerCombatState!.Hand.Cards.First(c => c.Id.Entry == "DEFEND_IRONCLAD");
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, null));
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await RunManager.Instance.ActionQueueSet.BecameEmpty();
            while (!LocalCapture.Stable()) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        var prefix = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), true);
        var packet = new PacketReader(); packet.Reset(prefix.Replay);
        var replay = packet.Read<CombatReplay>();
        replay.serializableRun = RunManager.Instance.ToSave(null);
        replay.events.Clear();
        var writer = new PacketWriter(); replay.Serialize(writer);
        var request = prefix with { Replay = writer.Buffer.AsSpan(0, (writer.BitPosition + 7) / 8).ToArray(),
            Workers = 1, MaxNodes = 1, MaxRounds = 1, BudgetSeconds = 25 };
        bool rejectedForRootMismatch = false;
        try { await Task.Run(() => pool.Analyze(request, LocalCapture.Installation(), _ => { }, CancellationToken.None)); }
        catch (CoachException ex) when (ex.Category == "local_failed" && ex.Message.Contains("后台重放与当前战斗状态不一致"))
        { rejectedForRootMismatch = true; }
        if (!rejectedForRootMismatch) throw new InvalidOperationException("Checkpoint probe needs an explicit native root mismatch");
        if (LocalCapture.Fingerprint() != prefix.NativeHash) throw new InvalidOperationException("Checkpoint probe modified its source host");
        LocalWire.Write(Path.Combine(root, "integration-checkpoint.json"), new
        {
            prefix_steps = 2, source_energy = player.PlayerCombatState!.Energy, source_block = player.Creature.Block,
            native_save_restores_exact_combat_prefix = false, mismatch_guard_rejected = true,
            source_host_unchanged = true, adopted = false,
            reason = "Native SerializableRun is not a complete mid-combat checkpoint. Retain native replay restoration."
        });
    }
}
