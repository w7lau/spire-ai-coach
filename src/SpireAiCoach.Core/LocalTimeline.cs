using System.Diagnostics;

namespace SpireAiCoach.Core;

// All owned processes run on this Windows host: QueryPerformanceCounter gives a shared
// monotonic clock. Durations are wall time, including awaited native work, not CPU time.
public sealed record LocalTraceSpan(int Worker, string Stage, string Phase, string Detail,
    double StartMs, double DurationMs, int Route = 0, int Step = 0, int Depth = 0);
// Method totals include nested calls. Source identifies cumulative snapshots from
// one process/request so importing a newer snapshot does not count it twice.
public sealed record LocalMethodTiming(string Source, int Worker, string Stage, string Method,
    long Calls, long Skipped, double TotalMs, double MaxMs);
public sealed record LocalTrace(long OriginTimestamp, long Frequency, LocalTraceSpan[] Spans, int Dropped = 0,
    LocalMethodTiming[]? Methods = null);

public sealed class LocalTimeline
{
    private readonly object _gate = new();
    private readonly List<LocalTraceSpan> _spans = [];
    private readonly Queue<LocalTraceSpan> _overview = new();
    private readonly Func<long> _clock;
    private readonly int _capacity;
    private readonly int _overviewCapacity;
    private readonly string _methodSource = Guid.NewGuid().ToString("N");
    private readonly Dictionary<(int Worker, string Stage, string Method), MethodCounter> _methodCounters = [];
    private readonly Dictionary<(string Source, int Worker, string Stage, string Method), LocalMethodTiming> _methods = [];
    private int _dropped;
    public long Origin { get; }
    public long Frequency { get; }
    public static long Timestamp => Stopwatch.GetTimestamp();

    public LocalTimeline(long origin = 0, int capacity = 16384, Func<long>? clock = null, long frequency = 0)
    {
        _clock = clock ?? Stopwatch.GetTimestamp;
        Frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
        Origin = origin > 0 ? origin : _clock();
        _capacity = Math.Max(1, capacity);
        // Reserve bounded room for late stages even when per-action details fill up.
        _overviewCapacity = _capacity >= 512 ? 128 : 0;
    }

    public double ElapsedMs => (_clock() - Origin) * 1000d / Frequency;
    public IDisposable Measure(int worker, string stage, string phase, string detail = "",
        int route = 0, int step = 0, int depth = 0) => new Scope(this, worker, stage, phase, detail, route, step, depth);

    // Synchronous boundaries only. Aggregate in memory instead of creating a
    // timeline span or writing a file for every native call.
    public MethodScope MeasureMethod(int worker, string stage, string method) => new(this, worker, stage, method);
    public void SkipMethod(int worker, string stage, string method) => RecordMethod(worker, stage, method, 0, true);
    private void RecordMethod(int worker, string stage, string method, double durationMs, bool skipped)
    {
        var key = (worker, stage, method);
        lock (_gate)
        {
            if (!_methodCounters.TryGetValue(key, out var counter))
            {
                if (_methodCounters.Count >= 4096) { _dropped++; return; }
                _methodCounters.Add(key, counter = new());
            }
            counter.Calls++; if (skipped) counter.Skipped++;
            counter.TotalMs += durationMs; counter.MaxMs = Math.Max(counter.MaxMs, durationMs);
        }
    }

    private sealed class MethodCounter
    {
        public long Calls, Skipped;
        public double TotalMs, MaxMs;
    }

    public struct MethodScope : IDisposable
    {
        private readonly LocalTimeline? _timeline;
        private readonly int _worker;
        private readonly string _stage, _method;
        private readonly long _started;
        private bool _disposed;
        internal MethodScope(LocalTimeline timeline, int worker, string stage, string method)
        {
            _timeline = timeline; _worker = worker; _stage = stage; _method = method;
            _started = timeline._clock(); _disposed = false;
        }
        public void Dispose()
        {
            if (_timeline == null || _disposed) return;
            _disposed = true;
            _timeline.RecordMethod(_worker, _stage, _method,
                (_timeline._clock() - _started) * 1000d / _timeline.Frequency, false);
        }
    }

    public void Add(LocalTraceSpan span)
    {
        if (!double.IsFinite(span.StartMs) || !double.IsFinite(span.DurationMs) || span.DurationMs < 0)
            throw new ArgumentException("Invalid timeline span");
        lock (_gate)
        {
            span = span with { Detail = span.Detail.Length <= 160 ? span.Detail : span.Detail[..160] };
            if (_overviewCapacity > 0 && (span.Depth == 0 || span.Phase is "result_transfer" or "receive" or "stop_search" or "await_idle" or "cancel_request"))
            {
                if (_overview.Count == _overviewCapacity) { _overview.Dequeue(); _dropped++; }
                _overview.Enqueue(span);
            }
            else if (_spans.Count < _capacity - _overviewCapacity) _spans.Add(span);
            else _dropped++;
        }
    }

    public void Import(LocalTrace? trace, double fromMs = 0, double toMs = double.PositiveInfinity)
    {
        if (trace == null) return;
        if (trace.Frequency != Frequency) throw new InvalidDataException("Timeline clocks differ");
        double offset = (trace.OriginTimestamp - Origin) * 1000d / Frequency;
        foreach (var span in trace.Spans)
        {
            var start = Math.Max(fromMs, span.StartMs + offset);
            var end = Math.Min(toMs, span.StartMs + offset + span.DurationMs);
            if (end >= start) Add(span with { StartMs = start, DurationMs = end - start });
        }
        // Method aggregates cover the completed worker request, not a clipped
        // prewarming interval. Preparation traces do not contain method samples.
        lock (_gate)
        {
            foreach (var method in trace.Methods ?? [])
            {
                var key = (method.Source, method.Worker, method.Stage, method.Method);
                if (_methods.TryGetValue(key, out var previous))
                { if (method.Calls >= previous.Calls) _methods[key] = method; }
                else if (_methods.Count < 4096) _methods.Add(key, method);
                else _dropped++;
            }
            _dropped += trace.Dropped;
        }
    }

    public LocalTrace Snapshot()
    {
        lock (_gate) return new(Origin, Frequency, _spans.Concat(_overview).OrderBy(s => s.StartMs).ThenBy(s => s.Depth).ToArray(), _dropped,
            _methods.Values.Concat(_methodCounters.Select(p => new LocalMethodTiming(_methodSource, p.Key.Worker, p.Key.Stage,
                p.Key.Method, p.Value.Calls, p.Value.Skipped, p.Value.TotalMs, p.Value.MaxMs)))
                .OrderBy(m => m.Worker).ThenBy(m => m.Stage).ThenBy(m => m.Method).ToArray());
    }

    private sealed class Scope(LocalTimeline timeline, int worker, string stage, string phase,
        string detail, int route, int step, int depth) : IDisposable
    {
        private readonly long _start = timeline._clock();
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            timeline.Add(new(worker, stage, phase, detail, (_start - timeline.Origin) * 1000d / timeline.Frequency,
                (timeline._clock() - _start) * 1000d / timeline.Frequency, route, step, depth));
        }
    }

    public static string Label(string phase) => phase switch
    {
        "capture" => "读取当前战斗", "queue" => "等待准备或计算队列", "prepare" => "准备计算",
        "files" => "共享资源、加载 Mod 配置", "launch" => "创建计算进程", "engine" => "引擎与 Mod 启动",
        "reuse" => "复用已启动进程", "session" => "搜索", "restore" => "恢复路线起点",
        "cleanup" => "清理上次模拟", "decode" => "解码战斗记录", "setup" => "建立运行状态",
        "assets" => "准备角色与地图资源", "scene" => "初始化战斗", "history" => "恢复已完成操作",
        "fingerprint" => "核对状态", "decision" => "枚举与选择动作", "refine" => "生成改进路线",
        "schedule" => "分配搜索分支",
        "card" => "出牌并结算", "potion" => "药水并结算", "end_turn" => "敌方行动与下回合",
        "action_queue" => "等待原生动作结算", "settle" => "等待状态与胜利结算",
        "executor" => "等待原生执行完成", "asset_gc" => "资源准备后的内存回收",
        "logic_frame" => "等待事件",
        "stop_search" => "停止其余搜索",
        "await_ready" => "等待已启动实例就绪", "await_idle" => "等待实例安全清理", "cancel_request" => "取消并清理本次计算",
        "observe" => "读取过程预览", "publish" => "写入进度与候选", "ipc" => "传递计算请求",
        "receive" => "接收候选与检查运行日志", "dispatch" => "等待计算进程接收请求",
        "result_transfer" => "结果传递与轮询等待", "display_wait" => "等待界面显示",
        "verify" => "独立复核最终路线", "display" => "核对并显示结果", "result" => "结果就绪",
        "fallback" => "切换兼容执行",
        _ => phase
    };

    public static string Format(LocalSearchResult result)
    {
        if (result.Trace == null) return "暂无耗时记录。";
        var trace = result.Trace;
        var lines = new List<string> { $"从开始到结果就绪：{result.ElapsedMs / 1000d:F2} 秒。",
            "各路使用同一时间轴；下列并发用时不能相加作为总等待。动作耗时含原生结算等待，并非纯 CPU 用时。" };
        foreach (var span in trace.Spans.Where(s => s.Depth == 0))
            lines.Add($"{span.StartMs / 1000:F2}–{(span.StartMs + span.DurationMs) / 1000:F2}s　" +
                (span.Worker < 0 ? "主流程" : $"计算 {span.Worker + 1}") + "　" + Label(span.Phase));
        foreach (var worker in trace.Spans.Where(s => s.Worker >= 0).GroupBy(s => s.Worker).OrderBy(g => g.Key))
        {
            lines.Add($"—— 计算 {worker.Key + 1}：各阶段内部累计 ——");
            foreach (var group in worker.Where(s => s.Depth == 1).GroupBy(s => (s.Stage, s.Phase)))
                lines.Add($"{(group.Key.Stage == "verify" ? "复核 / " : "")}{Label(group.Key.Phase)}：{group.Sum(s => s.DurationMs) / 1000:F2}s，{group.Count()} 次");
        }
        if (trace.Methods is { Length: > 0 })
        {
            lines.Add("—— 方法累计（含内部调用，不能相加作为总耗时）——");
            foreach (var group in trace.Methods.GroupBy(m => (m.Worker, m.Stage, m.Method))
                .OrderBy(g => g.Key.Worker).ThenBy(g => g.Key.Stage).ThenByDescending(g => g.Sum(m => m.TotalMs)))
                lines.Add($"计算 {group.Key.Worker + 1} / {(group.Key.Stage == "verify" ? "复核" : "搜索")} / {group.Key.Method}：" +
                    $"{group.Sum(m => m.TotalMs):F2}ms，调用 {group.Sum(m => m.Calls)} 次，跳过 {group.Sum(m => m.Skipped)} 次，最长 {group.Max(m => m.MaxMs):F2}ms");
        }
        if (trace.Dropped > 0) lines.Add($"细节达到记录上限，省略 {trace.Dropped} 条；总用时不受影响。");
        return string.Join("\n", lines);
    }
}
