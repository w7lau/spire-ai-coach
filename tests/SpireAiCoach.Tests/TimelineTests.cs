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
    }
}
