using System.Diagnostics;

namespace SpireAiCoach.Core;

// All owned processes run on this Windows host: QueryPerformanceCounter gives a shared
// monotonic clock. Durations are wall time, including awaited native work, not CPU time.
public sealed record LocalTraceSpan(int Worker, string Stage, string Phase, string Detail,
    double StartMs, double DurationMs, int Route = 0, int Step = 0, int Depth = 0);
public sealed record LocalTrace(long OriginTimestamp, long Frequency, LocalTraceSpan[] Spans, int Dropped = 0);

public sealed class LocalTimeline
{
    private readonly object _gate = new();
    private readonly List<LocalTraceSpan> _spans = [];
    private readonly Func<long> _clock;
    private readonly int _capacity;
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
    }

    public double ElapsedMs => (_clock() - Origin) * 1000d / Frequency;
    public IDisposable Measure(int worker, string stage, string phase, string detail = "",
        int route = 0, int step = 0, int depth = 0) => new Scope(this, worker, stage, phase, detail, route, step, depth);

    public void Add(LocalTraceSpan span)
    {
        if (!double.IsFinite(span.StartMs) || !double.IsFinite(span.DurationMs) || span.DurationMs < 0)
            throw new ArgumentException("Invalid timeline span");
        lock (_gate)
        {
            if (_spans.Count < _capacity) _spans.Add(span with { Detail = span.Detail.Length <= 160 ? span.Detail : span.Detail[..160] });
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
        lock (_gate) _dropped += trace.Dropped;
    }

    public LocalTrace Snapshot()
    {
        lock (_gate) return new(Origin, Frequency, _spans.OrderBy(s => s.StartMs).ThenBy(s => s.Depth).ToArray(), _dropped);
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
        if (trace.Dropped > 0) lines.Add($"细节达到记录上限，省略 {trace.Dropped} 条；总用时不受影响。");
        return string.Join("\n", lines);
    }
}
