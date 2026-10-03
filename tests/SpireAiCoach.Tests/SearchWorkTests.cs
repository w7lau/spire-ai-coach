using SpireAiCoach.Core;

public static class SearchWorkTests
{
    public static void Register(Action<string, Action> test)
    {
        var action = new LocalAction(0, "card", 1, "name", "enemy", "native", 1, CombatCardIndex: 4);
        var request = new LocalSearchRequest("job", "snapshot", [], "root", 42, ["mod"], true);
        void WithQueue(Action<string> body)
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var directory = Path.GetFullPath(Path.Combine(temp, "spire-search-work-test-" + Guid.NewGuid().ToString("N")));
            if (!directory.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its temporary root");
            Directory.CreateDirectory(directory);
            try { body(directory); }
            finally { Directory.Delete(directory, recursive: true); }
        }
        void Check(bool condition) { if (!condition) throw new Exception("Shared work assertion failed"); }
        test("search work concurrent producers deduplicate exact jobs", () => WithQueue(dir =>
        {
            Parallel.For(0, 8, _ => new LocalSearchWork(dir, request).Offer("expand", [new[] { action }]));
            var stats = new LocalSearchWork(dir, request).Stats();
            Check(stats.Submitted == 1 && stats.DuplicateOffers == 7);
        }));
        test("search work concurrent consumers claim each job once", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request);
            work.Offer("expand", Enumerable.Range(0, 32).Select(i => new[] { action with { TargetId = (uint)i } }));
            var keys = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 8, i =>
            {
                var consumer = new LocalSearchWork(dir, request);
                while (consumer.Take("expand", i) is { } job) { keys.Add(job.Key); consumer.Complete(job); }
            });
            Check(keys.Count == 32 && keys.Distinct().Count() == 32);
            Check(work.Stats() is { Submitted: 32, Claimed: 32, Completed: 32 });
        }));
        test("search work preserves history choice and job class identity", () =>
        {
            string Key(LocalAction a, string kind = "expand") => LocalSearchWork.Key(kind, [a]);
            Check(Key(action) == Key(action with { CardName = "other", HandIndex = 7, Preference = -90 }));
            Check(Key(action) != Key(action with { BeforeHash = "other-state" }));
            Check(Key(action) != Key(action with { CombatCardIndex = 5 }));
            Check(Key(action) != Key(action, "improve"));
            var choice = new LocalCardChoice("offer", 0, "card", "name", [0, 1], "multi");
            Check(Key(action with { Choices = [choice] }) != Key(action with { Choices = [choice with { Indices = [1, 2] }] }));
        });
        test("search work isolates requests roots and loaded mods", () => WithQueue(dir =>
        {
            new LocalSearchWork(dir, request).Offer("expand", [new[] { action }]);
            foreach (var other in new[] { request with { Id = "other" }, request with { NativeHash = "other" }, request with { LoadedMods = ["other"] } })
                Check(new LocalSearchWork(dir, other).Take("expand", 0) == null);
        }));
        test("search work capacity and shallow deeper fairness", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request, 2);
            work.Offer("expand", [new[] { action }, new[] { action, action }, new[] { action, action, action }]);
            Check(work.Stats().Submitted == 2);
            Check(work.Take("expand", 0, true)!.Plan.Length == 2);
            Check(work.Take("expand", 1)!.Plan.Length == 1);
        }));
        test("search work expansion cannot starve later route improvements", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request, 2);
            var plans = Enumerable.Range(0, 3).Select(i => new[] { action with { TargetId = (uint)i } }).ToArray();
            work.Offer("expand", plans);
            work.Offer("improve", plans);
            Check(work.Stats().Submitted == 4);
            Check(work.Take("improve", 0) != null && work.Take("improve", 1) != null && work.Take("improve", 2) == null);
        }));
        test("search work completed request releases plans but preserves counters", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request);
            work.Offer("expand", [new[] { action }]);
            var job = work.Take("expand", 0)!; work.Complete(job);
            var stats = work.Stats(); work.ReleasePlans();
            Check(work.Stats() == stats);
            Check(!Directory.EnumerateFiles(dir, job.Key + ".json", SearchOption.AllDirectories).Any());
        }));
    }
}
