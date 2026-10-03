using System.Text.Json;
using SpireAiCoach.Core;

static class TimelineTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        test("timeline aligns concurrent process clocks without summing elapsed time", () =>
        {
            long ticks = 1000;
            var parent = new LocalTimeline(1000, clock: () => ticks, frequency: 1000);
            var first = new LocalTimeline(1000, clock: () => ticks, frequency: 1000);
            var second = new LocalTimeline(1020, clock: () => ticks, frequency: 1000);
            var a = first.Measure(0, "prepare", "engine");
            ticks = 1020;
            var b = second.Measure(1, "prepare", "engine");
            ticks = 1100; a.Dispose(); b.Dispose();
            parent.Import(first.Snapshot()); parent.Import(second.Snapshot());
            var spans = parent.Snapshot().Spans;
            Check(spans[0].StartMs == 0 && spans[1].StartMs == 20, "Global origins must align exactly");
            Check(spans.Sum(s => s.DurationMs) == 180 && parent.ElapsedMs == 100, "Parallel CPU/resource totals are not user wall time");
        });
        test("timeline clips prewarming to the actual time this request waited", () =>
        {
            var parent = new LocalTimeline(2000, frequency: 1000);
            parent.Import(new(1000, 1000, [new(0,"prepare","engine","",900,300), new(1,"prepare","files","",500,100)]), 0, 150);
            var span = parent.Snapshot().Spans.Single();
            Check(span.StartMs == 0 && span.DurationMs == 150, "Only an overlapping wait may appear after the click");
            try { parent.Import(new(2000, 1, [])); throw new Exception("Accepted incompatible clock"); }
            catch (InvalidDataException) { }
        });
        test("timeline bounds diagnostics and closes an exception scope only once", () =>
        {
            long ticks = 2000;
            var trace = new LocalTimeline(2000, 1, () => ticks, 1000);
            var scope = trace.Measure(0,"search","card", new string('x', 400));
            ticks += 25; scope.Dispose(); ticks += 20; scope.Dispose();
            trace.Add(new(0,"search","card","",50,10));
            var snapshot = trace.Snapshot();
            Check(snapshot.Spans.Single().DurationMs == 25 && snapshot.Spans[0].Detail.Length == 160 && snapshot.Dropped == 1,
                "Scope disposal is idempotent and trace memory is bounded");
            var roundtrip = JsonSerializer.Deserialize<LocalTrace>(JsonSerializer.Serialize(snapshot))!;
            Check(roundtrip.Spans.SequenceEqual(snapshot.Spans) && roundtrip.Dropped == 1, "IPC must preserve timestamps and truncation");
        });
        test("timeline nests native waits without changing action state and tolerates old results", () =>
        {
            var legacy = new LocalSearchResult("id","state","done","",0,0,100,null);
            Check(LocalTimeline.Format(legacy) == "暂无耗时记录。", "Old results remain readable");
            var trace = new LocalTrace(1000,1000,[new(0,"search","session","",0,100),new(0,"search","card","打击",10,80,1,1,1),
                new(0,"search","action_queue","",20,60,1,1,2)]);
            var text = LocalTimeline.Format(legacy with { Trace = trace });
            Check(text.Contains("0.08s") && !text.Contains("0.06s"), "Nested waits must not be added again to parent action totals");
        });
        test("timeline partial routes never describe incomplete combat as final HP settlement", () =>
        {
            var candidate = new LocalCandidate([],60,20,30,0,80,false,false,false,StartingHp:80);
            var text = LocalSearchPolicy.Format(new("id","state","partial","",1,0,100,candidate));
            Check(text.Contains("已模拟到的生命") && text.Contains("战斗尚未完成") && !text.Contains("预测战后生命"),
                "An unfinished horizon has no final combat/victory healing result");
        });
        test("method timings retain nesting, skipped calls and latest snapshots across processes", () =>
        {
            long ticks = 1000;
            var worker = new LocalTimeline(1000, clock: () => ticks, frequency: 1000);
            var outer = worker.MeasureMethod(0, "search", "checksum");
            ticks += 2;
            var inner = worker.MeasureMethod(0, "search", "snapshot");
            ticks += 3; inner.Dispose(); ticks += 4; outer.Dispose(); outer.Dispose();
            worker.SkipMethod(0, "search", "sound");
            var first = worker.Snapshot();
            var again = worker.MeasureMethod(0, "search", "checksum");
            ticks += 5; again.Dispose();
            var parent = new LocalTimeline(1000, clock: () => ticks, frequency: 1000);
            parent.Import(first); parent.Import(worker.Snapshot()); parent.Import(first);
            var independent = new LocalTimeline(1000, clock: () => ticks, frequency: 1000);
            var other = independent.MeasureMethod(0, "search", "checksum");
            ticks += 7; other.Dispose(); parent.Import(independent.Snapshot());
            var trace = JsonSerializer.Deserialize<LocalTrace>(JsonSerializer.Serialize(parent.Snapshot()))!;
            var methods = trace.Methods!;
            var sums = methods.Where(m => m.Method == "checksum").ToArray();
            Check(sums.Sum(m => m.Calls) == 3 && sums.Sum(m => m.TotalMs) == 21 && sums.Max(m => m.MaxMs) == 9,
                "Repeated snapshots must replace, older snapshots cannot regress, independent requests must add");
            Check(methods.Single(m => m.Method == "snapshot").TotalMs == 3 && trace.Spans.Length == 0,
                "Nested method time is inclusive and never added as another top-level timeline interval");
            Check(methods.Single(m => m.Method == "sound") is { Calls: 1, Skipped: 1, TotalMs: 0 },
                "Skipped native bodies have a count, not an invented saved duration");
            var text = LocalTimeline.Format(new("id", "state", "done", "", 1, 0, 21, null, Trace: trace));
            Check(text.Contains("不能相加") && text.Contains("调用 3 次") && text.Contains("跳过 1 次"),
                "UI must separate method totals and explain that nested costs overlap");
        });
        test("method profiling is bounded and old trace JSON remains readable", () =>
        {
            var legacy = JsonSerializer.Deserialize<LocalTrace>("{\"OriginTimestamp\":1,\"Frequency\":1000,\"Spans\":[],\"Dropped\":0}")!;
            Check(legacy.Methods == null, "Legacy diagnostics need no fabricated method samples");
            var timer = new LocalTimeline();
            for (int i = 0; i < 4097; i++) timer.SkipMethod(0, "search", "unknown-" + i);
            Check(timer.Snapshot().Methods!.Length == 4096 && timer.Snapshot().Dropped == 1,
                "Method aggregation cannot grow without bound for unexpected Mod names");
            var old = JsonSerializer.Deserialize<LocalSearchRequest>("{\"Id\":\"id\",\"SnapshotId\":\"s\",\"Replay\":\"\",\"NativeHash\":\"h\",\"ModelHash\":0,\"LoadedMods\":[],\"ContinueOptimization\":false}")!;
            Check(old.TrimWorkerOverhead, "Older requests must receive the product default for the owned worker");
            Check(old.FastVerification, "Older requests use presentation-only final verification acceleration");
            var ordinary = System.Text.Json.JsonSerializer.Deserialize<LocalSearchRequest>(
                "{\"Id\":\"ordinary\",\"SnapshotId\":\"snapshot\",\"Replay\":\"\",\"NativeHash\":\"native\",\"ModelHash\":0,\"LoadedMods\":[],\"ContinueOptimization\":false,\"FastVerification\":false}")!;
            Check(!ordinary.FastVerification, "Ordinary final verification remains available for comparison and fallback");
        });
    }
}
