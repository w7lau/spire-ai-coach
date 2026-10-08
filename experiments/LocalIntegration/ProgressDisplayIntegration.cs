using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Godot;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Observe real worker telemetry in an owned host and feed the product display on
// the native main thread. This bounded display probe is not a search benchmark.
internal static class ProgressDisplayIntegration
{
    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest request,
        LocalInstallation installation)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var panel = new LocalProgressPanel();
        tree.Root.AddChild(panel.View);
        panel.View.Size = new Vector2(760, 1000);
        request = request with { Id = Guid.NewGuid().ToString("N"), Workers = 8, Partitions = 8, Partition = 0,
            MaxNodes = 2, BudgetSeconds = 20, StopOnZeroLoss = false, StopOnFirstWin = false,
            SkipFinalVerification = true, InitialPlan = null, VerifyCandidate = null, RecordedReplayProbe = null,
            TurnWorkPipe = null, SearchWorkPipe = null, ProgressPipe = null, MinimumLossPipe = null,
            TimelineOrigin = 0, InitialTrace = null };
        panel.Begin(request);
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        var map = typeof(LocalProgressPanel).GetField("_map", fields)!.GetValue(panel)!;
        var rowsMethod = map.GetType().GetMethod("Rows", fields)!;
        object Read(object row, string name) => row.GetType().GetProperty(name)!.GetValue(row)!;
        object[] Rows() => ((IEnumerable)rowsMethod.Invoke(map, null)!).Cast<object>().ToArray();
        // Reproduce the misleading finish display: six lanes ended and two are
        // still searching. A dead current trial remains a running worker.
        for (int worker = 0; worker < 8; worker++) panel.Accept(new(request.Id, request.SnapshotId,
            worker, 8, 1, 1, 2, 64, 2, 1000, 60, worker < 6 ? "搜索完成 · 正在汇总路线" : "试走路线",
            worker == 6 ? new(1, 0, 75, 0, 0, "", [], 0, []) : null,
            [new(1, 1, "打击", "伤害")], worker < 6 ? "searched" : "running", RootBranches: 13));
        var finishRows = Rows();
        var caption = ((Label)typeof(LocalProgressPanel).GetField("_caption", fields)!.GetValue(panel)!).Text;
        bool finishLifecycle = finishRows.Length == 8 && finishRows.Count(r => !(bool)Read(r, "Stopped")) == 2 &&
            finishRows.Take(6).All(r => (string)Read(r, "LaneState") == "已结束") &&
            caption.Contains("搜索 2 路 · 准备 0 路 · 已结束 6 路") && caption.EndsWith("试走路线");
        if (!finishLifecycle) throw new InvalidOperationException("The route map hides lanes or mistakes trial HP for worker completion");
        panel.Begin(request);
        var pending = new ConcurrentDictionary<int, LocalProgress>();
        var updates = new ConcurrentDictionary<int, int>();
        var received = new ConcurrentDictionary<int, LocalProgress>();
        var checkpoints = new List<object>();
        var timer = Stopwatch.StartNew(); long last = -500;
        var calculation = Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None,
            p => { pending[p.Worker] = p; received[p.Worker] = p;
                updates.AddOrUpdate(p.Worker, 1, (_, count) => count + 1); }));
        try
        {
            while (!calculation.IsCompleted)
            {
                foreach (var worker in pending.Keys)
                    if (pending.TryRemove(worker, out var progress)) panel.Accept(progress);
                if (timer.ElapsedMilliseconds - last >= 500)
                {
                    last = timer.ElapsedMilliseconds;
                    var rows = Rows();
                    checkpoints.Add(new { ms = last,
                        visible = rows.Where(r => !(bool)Read(r, "Victory")).Select(r => (int)Read(r, "Worker")).ToArray(),
                        moving = rows.Where(r => !(bool)Read(r, "Victory") && !(bool)Read(r, "Stopped"))
                            .Select(r => (int)Read(r, "Worker")).ToArray(),
                        latest = received.OrderBy(p => p.Key).Select(p => new { worker = p.Key,
                            p.Value.Sequence, p.Value.Phase, p.Value.Status, hp = p.Value.State?.Hp,
                            events = p.Value.Events.Length }).ToArray() });
                }
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            var result = await calculation;
            LocalWire.Write(Path.Combine(root, "integration-progress-display-private.json"), result);
            LocalWire.Write(Path.Combine(root, "integration-progress-display-summary.json"), new {
                version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3),
                request.SearchOrder, requestedWorkers = request.Workers, result.Workers,
                result.Status, result.Evaluated, result.Victories, result.ElapsedMs,
                updates = updates.OrderBy(p => p.Key).Select(p => new { worker = p.Key, count = p.Value }).ToArray(),
                checkpoints, finishLifecycle, allEightVisible = checkpoints.Any(c =>
                    ((int[])c.GetType().GetProperty("visible")!.GetValue(c)!).Length == 8), searchBenchmark = false });
            if (result.Workers != 8 || result.Failure != null || result.RecoveredFailures is { Length: > 0 } ||
                received.Count != 8 || received.Values.Any(p => p.Sequence <= 0))
                throw new InvalidOperationException("Eight native lanes did not deliver progress: " + result.Message);
        }
        finally { panel.Finish("probe complete", true); panel.View.QueueFree(); }
    }
}
