using System.Collections.Concurrent;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

public static class FeatureIntegration
{
    public static async Task Run(string root, SceneTree tree, LocalWorkerPool pool, StateCapture capture, Player player)
    {
        async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var records = new List<object>();
        var panel = new LocalProgressPanel();
        tree.Root.AddChild(panel.View);
        var sourceAudio = SaveManager.Instance.SettingsSave.VolumeMaster;
        var installation = LocalCapture.Installation();
        var original = LocalCapture.Fingerprint();
        var captured = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), true) with { Workers = 2, MaxNodes = 4, BudgetSeconds = 45 };
        var previews = new List<LocalProgress>();
        var baseline = await Task.Run(() => pool.Analyze(captured with { IncludePotions = true, MaxNodes = 1 }, installation, _ => { }, CancellationToken.None));
        bool baselineUnchanged = original == LocalCapture.Fingerprint();
        records.Add(new { stage = "enabled_preserve_baseline", unchanged = baselineUnchanged, result = baseline });
        if (!baselineUnchanged || baseline.Best?.Won != true || baseline.Best.Actions.Any(a => a.PotionSlot.HasValue))
            throw new InvalidOperationException("Enabled search failed to retain its no-potion baseline");
        foreach (bool enabled in new[] { false, true })
        {
            var request = captured with { Id = Guid.NewGuid().ToString("N"), IncludePotions = enabled };
            panel.Begin(request);
            var pending = new ConcurrentQueue<LocalProgress>();
            var task = Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None, p => pending.Enqueue(p)));
            while (!task.IsCompleted)
            {
                while (pending.TryDequeue(out var p)) { previews.Add(p); panel.Accept(p); }
                await Frame();
            }
            var result = await task;
            while (pending.TryDequeue(out var p)) { previews.Add(p); panel.Accept(p); }
            panel.Finish("completed", false);
            var unchanged = LocalCapture.Fingerprint() == original && player.PotionSlots.Count(p => p != null) == 3;
            records.Add(new { stage = enabled ? "potions_on" : "potions_off", unchanged, result });
            LocalWire.Write(Path.Combine(root, "integration-features.json"), records);
            if (!unchanged || result.Best?.Won != true) throw new InvalidOperationException("Potion test changed host or failed to find combat victory");
            if (!enabled && result.Best.Actions.Any(a => a.PotionSlot.HasValue)) throw new InvalidOperationException("Disabled potion search suggested drinking a potion");
            if (result.Best.Actions.Any(a => a.PotionSlot == 2)) throw new InvalidOperationException("Automatic Fairy potion was manually used");
            if (enabled && !result.Best.Actions.Any(a => a.PotionSlot.HasValue)) throw new InvalidOperationException("Enabled fixture did not find a potion route");
        }
        // Native history now includes a self-targeted potion and a targeted enemy potion, leaving empty slots.
        var block = player.GetPotionAtSlotIndex(1)!;
        block.EnqueueManualUse(player.Creature);
        await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
        while (capture.Capture(true)?.CanAdvise != true) await Frame();
        var target = CombatManager.Instance.DebugOnlyGetState()!.Enemies.First(e => e.IsAlive);
        player.GetPotionAtSlotIndex(0)!.EnqueueManualUse(target);
        await Frame(); await RunManager.Instance.ActionQueueSet.BecameEmpty();
        while (capture.Capture(true)?.CanAdvise != true) await Frame();
        var historicalHash = LocalCapture.Fingerprint();
        var historical = LocalCapture.Capture(capture.Capture(true)!.Fingerprint(), true) with { Workers = 2, MaxNodes = 1, IncludePotions = false };
        var replay = await Task.Run(() => pool.Analyze(historical, installation, _ => { }, CancellationToken.None, p => { lock (previews) previews.Add(p); }));
        bool historyUnchanged = historicalHash == LocalCapture.Fingerprint();
        records.Add(new { stage = "after_native_potions", unchanged = historyUnchanged, remaining_slots = player.PotionSlots.Select(p => p?.Id.ToString()).ToArray(), result = replay });
        if (!historyUnchanged || replay.Best?.Won != true || replay.Best.Actions.Any(a => a.PotionSlot.HasValue))
            throw new InvalidOperationException("Potion history did not restore faithfully with manual potion search disabled");
        if (!previews.Any(p => p.State != null && p.Events.Any()) || previews.Where(p => p.State != null).Select(p => p.Worker).Distinct().Count() != 2 ||
            !previews.Any(p => p.Events.Any(e => e.Action.Contains("药水")))) throw new InvalidOperationException("Expected live multi-worker potion telemetry");
        records.Add(new { stage = "progress", frames = previews.Count, workers = previews.Select(p => p.Worker).Distinct().ToArray(),
            phases = previews.Select(p => p.Phase).Distinct().ToArray(), max_events = previews.Max(p => p.Events.Length),
            examples = previews.Where(p => p.Events.Any(e => e.Action.Contains("药水"))).Take(4).ToArray() });
        using (var cancel = new CancellationTokenSource(300))
        {
            try
            {
                await Task.Run(() => pool.Analyze(historical with { Id = Guid.NewGuid().ToString("N"), MaxNodes = 128 }, installation, _ => { }, cancel.Token));
                throw new InvalidOperationException("Cancellation did not propagate");
            }
            catch (OperationCanceledException) { panel.Finish("cancelled", true); }
        }
        bool finalUnchanged = historicalHash == LocalCapture.Fingerprint();
        bool audioUnchanged = SaveManager.Instance.SettingsSave.VolumeMaster == sourceAudio;
        if (!finalUnchanged || !audioUnchanged) throw new InvalidOperationException("Cancellation or worker muting modified host");
        records.Add(new { stage = "cancel_and_host_audio", unchanged = finalUnchanged, source_audio_unchanged = audioUnchanged });
        LocalWire.Write(Path.Combine(root, "integration-features.json"), records);
        panel.View.QueueFree();
    }
}
