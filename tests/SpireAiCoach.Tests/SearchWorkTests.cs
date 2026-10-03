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
        test("search work executed prefix audit preserves native choices and instances", () =>
        {
            var choice = new LocalCardChoice("offer", 0, "chosen", "display", [0], "pile");
            var observed = action with { Choices = [choice, choice with { OfferHash = "later" }] };
            Check(LocalSearchWork.MatchesPrefix([observed], [action]));
            Check(LocalSearchWork.MatchesPrefix([observed], [action with { Choices = [choice] }]));
            Check(!LocalSearchWork.MatchesPrefix([observed], [action with { CombatCardIndex = 7 }]));
            Check(!LocalSearchWork.MatchesPrefix([observed], [action with { BeforeHash = "different" }]));
            Check(!LocalSearchWork.MatchesPrefix([observed], [action with { Choices = [choice with { Index = 1 }] }]));
            Check(!LocalSearchWork.MatchesPrefix([observed], [observed, action]));
        });
        test("search work early consumers wait for atomic root publication", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request);
            Parallel.For(1, 4, i =>
            {
                var early = new LocalSearchWork(dir, request);
                Check(early.Take("expand", i) == null);
                Check(!early.Stats().RootReady);
            });
            work.Offer("expand", Enumerable.Range(0, 4).Select(i => new[] { action with { TargetId = (uint)i } }), initializeRoot: true);
            var keys = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 4, i =>
            {
                var consumer = new LocalSearchWork(dir, request);
                Check(consumer.Stats().RootReady);
                while (consumer.Take("expand", i) is { } task) { keys.Add(task.Key); consumer.Complete(task); }
            });
            Check(keys.Count == 4 && keys.Distinct().Count() == 4);
            Check(work.Stats() is { RootReady: true, Pending: 0, Active: 0, Completed: 4 });
        }));
        test("search work a failed root producer releases startup waiters", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request);
            Check(!work.Stats().RootReady);
            work.Retire(0);
            Check(work.Stats() is { RootReady: true, Active: 0, Pending: 0 });
        }));
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
        test("search work completed tasks release capacity without reopening identities", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request, 1);
            work.Offer("expand", [[action]]);
            var first = work.Take("expand", 0)!; work.Complete(first);
            var later = action with { BeforeHash = "later", CombatCardIndex = 7 };
            work.Offer("expand", [[action], [later]]);
            var next = work.Take("expand", 1)!;
            Check(next.Key != first.Key && next.Plan.Single() == later);
            Check(work.Take("expand", 2) == null);
            Check(work.Stats() is { Submitted: 2, Claimed: 2, Completed: 1, DuplicateOffers: 1 });
        }));
        test("search work promoting a pending job preserves focused and broad capacity", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request, 1);
            var later = action with { BeforeHash = "after", CombatCardIndex = 7 };
            var extra = action with { BeforeHash = "beyond", CombatCardIndex = 8 };
            work.Offer("expand", [[action]]);
            work.Offer("expand", [[action], [later]], parent: "measured-parent");
            Check(work.Stats().Submitted == 1);
            work.Offer("expand", [[later]]);
            work.Offer("expand", [[later], [extra]], parent: "measured-parent");
            Check(work.Stats().Submitted == 2);
            Check(work.Take("expand", 0, focused: true)!.Plan.Single() == action);
            Check(work.Take("expand", 1)!.Plan.Single() == later);
        }));
        test("search work focused descent survives interposed broad jobs and preserves siblings", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request, 2);
            work.Offer("expand", [[action], [action with { TargetId = 2 }]]);
            var root = work.Take("expand", 0, focused: true)!;
            var fork = action with { BeforeHash = "after-root", ModelId = "second", CombatCardIndex = 7 };
            work.Offer("expand", [[action, fork], [action, fork with { TargetId = 2 }]], parent: root.Key);
            work.Complete(root);
            var child = work.Take("expand", 0, focused: true)!;
            Check(child.Plan.Length == 2 && child.Plan.Last() == fork);
            var broad = work.Take("expand", 0)!;
            Check(broad.Plan.Length == 1 && broad.Plan[0].TargetId == 2);
            var deeper = fork with { BeforeHash = "after-second", ModelId = "third", CombatCardIndex = 9 };
            work.Offer("expand", [[..child.Plan, deeper]], parent: child.Key);
            work.Complete(child); work.Complete(broad);
            var grandchild = work.Take("expand", 0, focused: true)!;
            Check(grandchild.Plan.Length == 3 && grandchild.Plan.Last() == deeper);
            Check(work.Take("expand", 1)!.Plan.Last().TargetId == 2);
        }));
        test("search work new improvement generation gets attention while older plans remain reachable", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request);
            var shortOld = action with { ModelId = "old" };
            work.Offer("improve", [[shortOld]]);
            work.Offer("improve", [[action, action with { ModelId = "new" }]]);
            Check(work.Take("improve", 0)!.Plan.Last().ModelId == "new");
            Check(work.Take("improve", 1, deeper: true)!.Plan.Single() == shortOld);
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
        test("search work exposes backlog and stops waiting for retired owners", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request);
            work.Offer("expand", [new[] { action }, new[] { action with { TargetId = 2 } }]);
            Check(work.Stats() is { Pending: 2, Active: 0 });
            var job = work.Take("expand", 0)!;
            Check(work.Stats() is { Pending: 1, Active: 1 });
            work.Retire(0);
            Check(work.Stats() is { Pending: 1, Active: 0, Completed: 0 });
            work.Complete(job);
            Check(work.Stats().Completed == 1);
        }));
        test("search work skips queued and later exact terminal plans across job classes", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request);
            work.Offer("expand", [new[] { action }]);
            work.Offer("improve", [new[] { action }]);
            var candidate = new LocalCandidate([action], 40, 0, 0, 1, 40, true, false, false);
            work.RecordTerminal(candidate);
            Check(work.Take("expand", 0) == null && work.Take("improve", 1) == null && work.Stats().CoveredJobs == 2);
            work.Offer("expand", [new[] { action with { BeforeHash = "different" } }]);
            work.Offer("improve", [new[] { action with { CombatCardIndex = 7 } }]);
            Check(work.Take("expand", 0) != null && work.Take("improve", 1) != null);
            var fresh = new LocalSearchWork(dir, request with { Id = "other" });
            fresh.Offer("expand", [candidate.Actions]);
            Check(fresh.Take("expand", 0) != null);
        }));
        test("search work never closes unfinished horizons or different native selections", () => WithQueue(dir =>
        {
            var work = new LocalSearchWork(dir, request);
            var choice = new LocalCardChoice("offer", 0, "model", "same", [1, 2], "multi");
            var played = action with { Choices = [choice] };
            var candidate = new LocalCandidate([played], 40, 0, 0, 1, 40, false, false, false);
            work.RecordTerminal(candidate);
            work.Offer("expand", [candidate.Actions]);
            Check(work.Take("expand", 0) != null);
            work.RecordTerminal(candidate with { Won = true });
            var other = played with { Choices = [choice with { Indices = [1, 3] }] };
            work.Offer("expand", [new[] { other }]);
            Check(work.Take("expand", 1)!.Plan[0].Choices![0].Indices!.SequenceEqual([1, 3]));
        }));
    }
}
