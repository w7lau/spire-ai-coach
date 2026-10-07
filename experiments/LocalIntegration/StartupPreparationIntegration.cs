using System.Diagnostics;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Entry verifies the owned executable, directory lock and private APPDATA.
internal static class StartupPreparationIntegration
{
    public static async Task Run(string root, SceneTree tree)
    {
        if (RunManager.Instance.IsInProgress || CombatManager.Instance.DebugOnlyGetState() != null)
            throw new InvalidOperationException("Startup preparation must start in the idle menu");
        var flag = System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_INTEGRATION");
        var overlay = new CoachOverlay(tree);
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(CoachOverlay).GetField(name, fields)!.GetValue(overlay)!;
        var timer = Stopwatch.StartNew();
        try
        {
            // Only this exact owned host may enter the product startup warmup.
            System.Environment.SetEnvironmentVariable("SPIRE_LOCAL_INTEGRATION", null);
            overlay.Mount();
            Field<SpinBox>("_localWorkers").Value = 2;
            var pool = Field<LocalWorkerPool>("_localPool");
            while (pool.Resources().Ready < 2)
            {
                if (timer.Elapsed.TotalSeconds > 120) throw new TimeoutException("Idle native startup did not prewarm two lanes");
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            var warmed = pool.Resources();
            for (int i = 0; i < 8; i++)
            {
                typeof(CoachOverlay).GetMethod("PrepareLocalResources", fields)!.Invoke(overlay, [null]);
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            var installation = LocalCapture.Installation();
            await Task.Run(() => pool.Prepare(installation, 2, CancellationToken.None));
            var reused = pool.Resources();
            if (warmed.Starts != 2 || reused.Starts != warmed.Starts || reused.Ready != 2 ||
                RunManager.Instance.IsInProgress || CombatManager.Instance.DebugOnlyGetState() != null)
                throw new InvalidOperationException("Idle preparation restarted resources or changed the native game state");

            var panel = new LocalProgressPanel();
            tree.Root.AddChild(panel.View);
            var request = new LocalSearchRequest("ui-phase", "snapshot", [], "hash", 0, [], false);
            panel.Begin(request);
            var first = new LocalProgress(request.Id, request.SnapshotId, 0, 2, 4, 5, 4, 64, 4, 9000, 60,
                "试走", null, [], Best: new(2, 74, 75, 74, 3, 0));
            panel.Accept(first);
            var next = first with { Pass = 1, Sequence = 4_000_001, Victories = 0, Best = null, Phase = "兼容模式 · 准备计算" };
            panel.Accept(next);
            T Part<T>(string name) => (T)typeof(LocalProgressPanel).GetField(name, fields)!.GetValue(panel)!;
            if (Part<Label>("_caption").Text != next.Phase || Part<Label>("_health").Text != next.Phase ||
                Part<Label>("_best").Text != "") throw new InvalidOperationException("Compatibility restart still shows stale victory or waiting for cards");
            panel.Accept(first with { Sequence = 99 });
            if (Part<Label>("_caption").Text != next.Phase) throw new InvalidOperationException("Late old-pass progress replaced compatibility status");
            panel.Accept(next with { Sequence = next.Sequence + 1, Status = "searched", Phase = "搜索完成 · 正在汇总路线" });
            panel.Finish("计算完成", false);
            panel.Accept(next with { Sequence = next.Sequence + 2 });
            if (Part<Label>("_caption").Text != "计算完成") throw new InvalidOperationException("Finished exploration returned to waiting");
            panel.View.QueueFree();
            LocalWire.Write(Path.Combine(root, "integration-startup-preparation-summary.json"), new {
                version = typeof(ModEntry).Assembly.GetName().Version!.ToString(3),
                module = typeof(ModEntry).Assembly.ManifestModule.ModuleVersionId,
                idleMenu = true, warmupMs = timer.ElapsedMilliseconds, warmed, reused,
                repeatedPolls = 8, phaseRestartClearsOldVictory = true, lateProgressRejected = true,
                finishPreserved = true, nativeStateUnchanged = true });
        }
        finally
        {
            typeof(CoachOverlay).GetMethod("Dispose", fields)!.Invoke(overlay, null);
            System.Environment.SetEnvironmentVariable("SPIRE_LOCAL_INTEGRATION", flag);
        }
    }
}
