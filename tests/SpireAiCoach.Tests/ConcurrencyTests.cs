using SpireAiCoach.Core;

static class ConcurrencyTests
{
    static void Check(bool value) { if (!value) throw new Exception("Adaptive concurrency assertion failed"); }

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("adaptive concurrency treats configured workers as a resource ceiling", () =>
        {
            const ulong gb = 1024UL * 1024 * 1024;
            Check(LocalConcurrency.Limit(32, 64 * gb, 8) == 8);
            Check(LocalConcurrency.Limit(32, 4 * gb, 8) == 1);
            Check(LocalConcurrency.Limit(2, 64 * gb, 8) == 2);
            Check(LocalConcurrency.Limit(32, 64 * gb, 0) == 8);
            Check(LocalConcurrency.Limit(32, 0, 16) == 1);
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
            var result = await LocalConcurrency.Run(8, true, true, i => Task.FromResult(i),
                _ => new(100, 0, 20, 8), () => false, CancellationToken.None, 1);
            Check(result.SequenceEqual(Enumerable.Range(0, 8)) && request.AdaptiveWorkers && request.Workers == 8 &&
                request.MaxNodes == 64 && request.BudgetSeconds == 60 && request.MaxRounds == 64 &&
                request.IncludePotions && !request.StopOnZeroLoss);
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
    }
}
