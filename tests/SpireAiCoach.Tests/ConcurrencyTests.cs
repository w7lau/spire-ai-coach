using SpireAiCoach.Core;

static class ConcurrencyTests
{
    static void Check(bool value) { if (!value) throw new Exception("Adaptive concurrency assertion failed"); }

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("manual concurrency prepares its chosen count and automatic mode remains lazy", () =>
        {
            Check(LocalConcurrency.PrewarmCount(0) == 1 && LocalConcurrency.PrewarmCount(8) == 8 &&
                LocalConcurrency.PrewarmCount(100) == 16);
            Check(!LocalConcurrency.AdaptiveAdmission(8, true) && LocalConcurrency.AdaptiveAdmission(0, true) &&
                !LocalConcurrency.AdaptiveAdmission(0, false));
        });
        asyncTest("manual concurrency starts every lane before the first root becomes ready", async () =>
        {
            var lanes = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource<int>()).ToArray();
            var launched = new List<int>();
            var request = LocalCalculation.Configure(new("id", "snap", [], "native", 1, [], true),
                LocalSearchOrder.TurnFrontier, 8, true, false);
            var running = LocalConcurrency.Run(8, LocalConcurrency.AdaptiveAdmission(request.Workers, request.AdaptiveWorkers), true,
                i => { launched.Add(i); return lanes[i].Task; },
                _ => throw new Exception("Manual admission waited for root work"), () => false, CancellationToken.None);
            Check(launched.SequenceEqual(Enumerable.Range(0, 8)) && !running.IsCompleted);
            for (int i = 0; i < 8; i++) lanes[i].SetResult(i);
            Check((await running).SequenceEqual(Enumerable.Range(0, 8)) && request.MaxNodes == 64 &&
                request.MaxRounds == 64 && request.BudgetSeconds == 60);
        });
        asyncTest("manual concurrency stops admission after an immediate goal", async () =>
        {
            bool stopped = false;
            int launched = 0;
            var result = await LocalConcurrency.Run(8, false, true,
                i => { launched++; stopped = true; return Task.FromResult(i); },
                _ => throw new Exception("Manual admission consulted pending work"), () => stopped, CancellationToken.None);
            Check(launched == 1 && result.SequenceEqual([0]));
        });
        test("adaptive concurrency honors manual ceilings and estimates resources only in automatic mode", () =>
        {
            const ulong gb = 1024UL * 1024 * 1024;
            Check(LocalConcurrency.Limit(32, 64 * gb, 8) == 8);
            Check(LocalConcurrency.Limit(32, 4 * gb, 8) == 8);
            Check(LocalConcurrency.Limit(2, 64 * gb, 8) == 8);
            Check(LocalConcurrency.Limit(16, 9 * gb, 8) == 8);
            Check(LocalConcurrency.Limit(16, 9 * gb, 0) == 4);
            Check(LocalConcurrency.Limit(32, 64 * gb, 0) == 8);
            Check(LocalConcurrency.Limit(32, 0, 16) == 16);
            Check(LocalConcurrency.Limit(32, 0, 0) == 1);
            Check(LocalConcurrency.Limit(32, 64 * gb, 100) == 16);
        });
        test("adaptive concurrency admits only waiting independent jobs", () =>
        {
            Check(LocalConcurrency.Desired(8, 1, new(10, 0, 0, 8), true) == 1);
            Check(LocalConcurrency.Desired(8, 1, new(1, 0, 4, 8), true) == 2);
            Check(LocalConcurrency.Desired(8, 3, new(1, 1, 4, 8), true) == 3);
            Check(LocalConcurrency.Desired(8, 3, new(2, 1, 4, 8), true) == 4);
            Check(LocalConcurrency.Desired(8, 3, new(20, 0, 4, 3), true) == 3);
            Check(LocalConcurrency.Desired(8, 3, new(0, 0, 4, 8), true) == 3);
            Check(LocalConcurrency.Partitions(8, 1, true) == 1);
            Check(LocalConcurrency.Partitions(8, 2, true) == 2);
            Check(LocalConcurrency.Partitions(8, 2, false) == 8);
        });
        asyncTest("adaptive concurrency starts one worker and grows after a root is ready", async () =>
        {
            var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var launches = new List<int>();
            int polls = 0;
            bool stopped = false;
            var result = await LocalConcurrency.Run(8, true, true, i =>
            {
                launches.Add(i);
                if (i == 0) return first.Task;
                stopped = true; first.SetResult(0); return Task.FromResult(i);
            }, admitted =>
            {
                if (++polls <= 2) { Check(admitted == 1); return new(10, 1, 0, 8); }
                return new(1, 0, 4, 8);
            }, () => stopped, CancellationToken.None, 1);
            Check(result.SequenceEqual([0, 1]) && launches.SequenceEqual([0, 1]) && polls >= 3);
        });
        asyncTest("adaptive concurrency never starts spare workers after a goal", async () =>
        {
            bool stopped = false;
            int launched = 0;
            var result = await LocalConcurrency.Run(8, true, true, i =>
            { launched++; stopped = true; return Task.FromResult(i); },
                _ => new(100, 0, 16, 8), () => stopped, CancellationToken.None, 1);
            Check(result.Length == 1 && launched == 1);
        });
        asyncTest("adaptive concurrency preserves every admitted fixed partition even for fast workers", async () =>
        {
            var launched = new List<int>();
            var result = await LocalConcurrency.Run(8, true, false, i =>
            { launched.Add(i); return Task.FromResult(i); },
                _ => new(0, 0, 3, 8), () => false, CancellationToken.None, 1);
            Check(launched.SequenceEqual([0, 1, 2]) && result.SequenceEqual([0, 1, 2]));
            result = await LocalConcurrency.Run(8, true, false, i => Task.FromResult(i),
                _ => new(0, 0, 1, 8), () => false, CancellationToken.None, 1);
            Check(result.SequenceEqual([0]));
        });
        asyncTest("adaptive concurrency can grow to the ceiling without reducing search limits", async () =>
        {
            var request = LocalCalculation.Configure(new("id", "snap", [], "native", 1, [], true),
                LocalSearchOrder.MonteCarlo, 8, true, false);
            var workers = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource<int>()).ToArray();
            var result = await LocalConcurrency.Run(8, true, true, i =>
            {
                if (i == 7) for (int j = 0; j < 8; j++) workers[j].SetResult(j);
                return workers[i].Task;
            },
                _ => new(100, 0, 20, 8), () => false, CancellationToken.None, 1);
            Check(result.SequenceEqual(Enumerable.Range(0, 8)) && request.AdaptiveWorkers && request.Workers == 8 &&
                request.MaxNodes == 64 && request.BudgetSeconds == 60 && request.MaxRounds == 64 &&
                request.IncludePotions && !request.StopOnZeroLoss);
        });
        asyncTest("shared concurrency does not start new searches after all admitted workers finish", async () =>
        {
            int launched = 0;
            var result = await LocalConcurrency.Run(8, true, true, i =>
            { launched++; return Task.FromResult(i); },
                _ => new(100, 0, 20, 8), () => false, CancellationToken.None, 1);
            Check(result.SequenceEqual([0]) && launched == 1);
        });
        asyncTest("shared concurrency does not reopen admission when a finished worker releases memory", async () =>
        {
            var first = new TaskCompletionSource<int>();
            var second = new TaskCompletionSource<int>();
            var launches = new List<int>();
            int pollsWithTwo = 0;
            var result = await LocalConcurrency.Run(8, true, true, i =>
            { launches.Add(i); return i == 0 ? first.Task : second.Task; }, n =>
            {
                if (n == 2 && ++pollsWithTwo == 2)
                {
                    first.SetResult(0);
                    _ = Task.Run(async () => { await Task.Delay(20); second.SetResult(1); });
                }
                return new(100, 0, 20, pollsWithTwo > 2 ? 8 : 2);
            }, () => false, CancellationToken.None, 1);
            Check(result.SequenceEqual([0, 1]) && launches.SequenceEqual([0, 1]));
        });
        asyncTest("adaptive concurrency cancellation joins every admitted worker cleanup", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            int launched = 0, cleaned = 0;
            async Task<int> Launch(int i)
            {
                launched++;
                try { await Task.Delay(Timeout.Infinite, cancellation.Token); return i; }
                finally { Interlocked.Increment(ref cleaned); }
            }
            try
            {
                await LocalConcurrency.Run(8, true, true, Launch, n =>
                {
                    if (n == 2) cancellation.Cancel();
                    return new(1, n == 2 ? 1 : 0, 4, 8);
                }, () => cancellation.IsCancellationRequested, cancellation.Token, 1);
                throw new Exception("Cancellation was swallowed");
            }
            catch (OperationCanceledException) { }
            Check(launched == 2 && cleaned == 2);
        });
        asyncTest("a rejected compatibility pass cancels preparing peers and joins cleanup", async () =>
        {
            var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int launches = 0, cleaned = 0;
            async Task<string> Launch(int index, CancellationToken failure)
            {
                Interlocked.Increment(ref launches);
                try
                {
                    if (index == 0) return await first.Task;
                    first.TrySetResult("original native error");
                    try { await Task.Delay(Timeout.Infinite, failure); throw new Exception("Peer preparation continued"); }
                    catch (OperationCanceledException) when (failure.IsCancellationRequested)
                    { canceled.TrySetResult(); await cleanup.Task; return "peer stopped"; }
                }
                finally { Interlocked.Increment(ref cleaned); }
            }
            var running = LocalConcurrency.Run(8, true, true, Launch,
                n => new(100, n - 1, 8, 2), () => false, r => r == "original native error", CancellationToken.None, 1);
            try
            {
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!running.IsCompleted && launches == 2 && cleaned == 1);
            }
            finally { cleanup.TrySetResult(); }
            var results = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Check(results.SequenceEqual(["original native error", "peer stopped"]) && launches == cleaned);
        });
        asyncTest("compatibility cancellation preserves caller cancellation instead of a successful fallback", async () =>
        {
            using var caller = new CancellationTokenSource();
            int cleaned = 0;
            async Task<int> Launch(int index, CancellationToken failure)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, failure);
                try { caller.Cancel(); await Task.Delay(Timeout.Infinite, linked.Token); return index; }
                finally { Interlocked.Increment(ref cleaned); }
            }
            try
            {
                await LocalConcurrency.Run(8, true, true, Launch, _ => new(100, 0, 8, 8),
                    () => false, _ => false, caller.Token, 1);
                throw new Exception("Caller cancellation was returned as a result");
            }
            catch (OperationCanceledException) { Check(cleaned == 1); }
        });
    }
}
