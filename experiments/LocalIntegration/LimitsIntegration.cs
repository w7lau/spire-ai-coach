using Godot;
using System.Diagnostics;
using System.Reflection;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Product controls and protocol acceptance; not an eight-process throughput benchmark.
internal static class LimitsIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest captured,
        LocalInstallation installation, string seedFile)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var overlay = new CoachOverlay(tree);
        overlay.Mount();
        var layer = tree.Root.GetNode<CanvasLayer>("SpireAiCoach");
        SpinBox Spin(string name) => layer.FindChild(name, true, false) as SpinBox
            ?? throw new InvalidOperationException("Missing product control: " + name);
        Spin("LocalWorkers").Value = 8;
        Spin("LocalMaxAttempts").Value = 257;
        Spin("LocalMaxRounds").Value = 130;
        Spin("LocalSearchSeconds").Value = 125;
        Spin("LocalTargetVictoryRounds").Value = 120;
        ((Button)layer.FindChild("LocalSaveSettings", true, false)!).EmitSignal(Button.SignalName.Pressed);
        var store = (SettingsStore)typeof(CoachOverlay).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(overlay)!;
        var saved = store.Load().Settings;
        if (saved.LocalWorkers != 8 || saved.LocalMaxAttempts != 257 || saved.LocalMaxRounds != 130 ||
            saved.LocalSearchSeconds != 125 || saved.LocalTargetVictoryRounds != 120 || Spin("LocalTargetVictoryRounds").MaxValue != 130)
            throw new InvalidOperationException("Product controls did not save or update the horizon");
        var configure = typeof(CoachOverlay).GetMethod("ConfigureLocalRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var count = typeof(LocalWorkerPool).GetMethod("Count", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var seed = LocalWire.Read<LocalSearchResult>(seedFile).Best ?? throw new InvalidOperationException("Missing native route");
        if (seed.Actions.Length == 0 || seed.Actions[0].BeforeHash != captured.NativeHash)
            throw new InvalidOperationException("The fixed route must match the frozen native root");
        await Task.Run(() => pool.Prepare(installation, 8, CancellationToken.None));
        var workers = (Array)typeof(LocalWorkerPool).GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!;
        var worker = workers.GetValue(0)!;
        var workerRoot = (string)worker.GetType().GetProperty("Root")!.GetValue(worker)!;
        if (!File.Exists(Path.Combine(workerRoot, ".coach-worker"))) throw new InvalidOperationException("Not an owned worker");
        var records = new List<object>();
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            var request = (LocalSearchRequest)configure.Invoke(overlay, [captured, order])!;
            int ceiling = (int)count.Invoke(pool, [request.Workers, request.AdaptiveWorkers])!;
            if (ceiling != 8 || request.SearchOrder != order || request.MaxNodes != 257 || request.MaxRounds != 130 ||
                request.BudgetSeconds != 125 || request.Workers != 8 || request.TargetVictoryRounds != 120 ||
                !request.NumericalExecution || !request.DataOnlyCombat || !request.DataOnlyRun)
                throw new InvalidOperationException("Configured limits did not reach the common product request/pool");
            var command = request with { Id = Guid.NewGuid().ToString("N"), Partition = 0, Partitions = 1,
                VerifyCandidate = seed, InitialPlan = null, RecordedReplayProbe = null, ShareSearchWork = false,
                TurnWorkPipe = null, DeferVerification = false };
            LocalWire.Write(Path.Combine(workerRoot, "request.json"), command);
            var timer = Stopwatch.StartNew();
            LocalSearchResult? result = null;
            while (timer.Elapsed.TotalSeconds < 120)
            {
                await Task.Delay(100);
                var file = Path.Combine(workerRoot, "result.json");
                if (!File.Exists(file)) continue;
                var sample = LocalWire.Read<LocalSearchResult>(file);
                if (sample.Id == command.Id && sample.Status != "running") { result = sample; break; }
            }
            LocalWire.Write(Path.Combine(root, $"integration-limits-{order}-private.json"), result);
            if (result?.Status != "done" || result.Best is not { } best || best.Continuation?.Length != seed.Actions.Length ||
                best.Hp != seed.Hp || best.HpLost != seed.HpLost || best.Gold != seed.Gold || best.EnemyHp != seed.EnemyHp ||
                best.MaxHp != seed.MaxHp || best.Won != seed.Won || best.Dead != seed.Dead ||
                best.Actions.Length != seed.Actions.Length)
                throw new InvalidOperationException("Native protocol rejected enlarged limits or changed the fixed outcome: " + result?.Message);
            records.Add(new { order, configured_workers = request.Workers, worker_limit = ceiling, request.MaxNodes,
                request.MaxRounds, request.BudgetSeconds, request.TargetVictoryRounds, result.Status,
                verified_steps = best.Continuation.Length, best.Hp, best.Won, ui_persisted = true,
                actual_native_workers = 1, eight_process_throughput_measured = false });
        }
        layer.QueueFree();
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        LocalWire.Write(Path.Combine(root, "integration-limits-summary.json"), new
        {
            version = typeof(ModEntry).Assembly.GetName().Version!.ToString(3), records
        });
    }
}
