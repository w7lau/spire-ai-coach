namespace SpireAiCoach.Core;

public sealed record LocalWorkerDemand(int PendingJobs, int StartingWorkers, int RootBranches, int AllowedWorkers);

// Worker count is a ceiling. Queue ownership and native state remain in their
// existing owners; this scheduler changes admission, never card rules or budgets.
public static class LocalConcurrency
{
    public static int PrewarmCount(int configured) => configured > 0 ? Math.Clamp(configured, 1, 16) : 1;
    public static bool AdaptiveAdmission(int configured, bool adaptive) => adaptive && configured <= 0;

    public static int Limit(int processors, ulong availableMemory, int configured)
    {
        int requested = LocalSearchPolicy.WorkerCount(processors, availableMemory, configured);
        // A positive value is the player's explicit ceiling. Only automatic
        // selection uses the historical CPU/RAM estimate; admission remains lazy.
        if (configured > 0) return requested;
        int memory = (int)Math.Clamp(((double)availableMemory / (1024 * 1024 * 1024) - 3) / 1.5, 1, 16);
        return Math.Min(requested, Math.Min(Math.Max(1, processors), memory));
    }

    // Systematic search keeps a fixed, identical partition on every worker.
    // With one root move it runs on one worker and can still explore later forks.
    public static int Partitions(int maximum, int rootBranches, bool adaptive) =>
        adaptive ? Math.Min(maximum, Math.Max(1, rootBranches)) : maximum;

    public static int Desired(int maximum, int launched, LocalWorkerDemand demand, bool shared)
    {
        int allowed = Math.Min(maximum, Math.Max(launched, demand.AllowedWorkers));
        if (launched >= allowed || demand.RootBranches <= 0) return launched;
        if (shared)
        {
            // Each not-yet-ready worker already reserves one waiting job. Do not
            // create another cold instance for the same short-lived queue entry.
            return demand.PendingJobs > demand.StartingWorkers ? launched + 1 : launched;
        }
        return Math.Min(launched + 1, Math.Min(allowed, demand.RootBranches));
    }

    public static async Task<T[]> Run<T>(int maximum, bool adaptive, bool shared,
        Func<int, Task<T>> launch, Func<int, LocalWorkerDemand> demand, Func<bool> stopped,
        CancellationToken cancellation, int pollMs = 250, bool waitForRoot = false)
    {
        if (maximum is < 1 or > 16 || pollMs < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (!adaptive)
        {
            var admitted = new List<Task<T>>();
            try
            {
                for (int index = 0; index < maximum && !stopped(); index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (index == 1 && waitForRoot)
                    {
                        // Validate one native root before admitting its peers.
                        // Once it is valid, preserve the chosen manual count and
                        // launch the remaining lanes together, with their own budgets.
                        while (!admitted[0].IsCompleted && !stopped() && demand(1).RootBranches <= 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            await Task.WhenAny(admitted[0], Task.Delay(pollMs, cancellation));
                        }
                        cancellation.ThrowIfCancellationRequested();
                        if (stopped() || admitted[0].IsFaulted || admitted[0].IsCanceled || demand(1).RootBranches <= 0) break;
                    }
                    admitted.Add(launch(index));
                }
            }
            finally { await Task.WhenAll(admitted); }
            return admitted.Select(r => r.Result).ToArray();
        }
        var runs = new List<Task<T>> { launch(0) };
        bool admissionClosed = false;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                // Observe failures before admitting any more work. The finally
                // block joins all admitted workers, including cancellation cleanup.
                if (runs.Any(r => r.IsFaulted || r.IsCanceled)) break;
                // A shared search ending releases memory. That must not reopen
                // admission and start a fresh full-budget worker after its peers
                // have finished. Fixed partitions still require every root owner.
                if (shared && runs.Any(r => r.IsCompleted)) admissionClosed = true;
                bool added = !admissionClosed && !stopped() &&
                    Desired(maximum, runs.Count, demand(runs.Count), shared) > runs.Count;
                if (added) runs.Add(launch(runs.Count));
                if (!added && runs.All(r => r.IsCompleted)) break;
                await Task.WhenAny(Task.WhenAll(runs), Task.Delay(pollMs, cancellation));
            }
        }
        finally { await Task.WhenAll(runs); }
        return runs.Select(r => r.Result).ToArray();
    }

    // A compatibility pass becomes unusable as soon as one lane rejects it.
    // Notify admitted peers immediately, including peers still preparing, then
    // join their cleanup before the caller changes execution mode or ownership.
    public static async Task<T[]> Run<T>(int maximum, bool adaptive, bool shared,
        Func<int, CancellationToken, Task<T>> launch, Func<int, LocalWorkerDemand> demand,
        Func<bool> stopped, Func<T, bool> failed, CancellationToken cancellation, int pollMs = 250,
        bool waitForRoot = false)
    {
        using var failure = new CancellationTokenSource();
        async Task<T> Observe(int index)
        {
            try
            {
                var result = await launch(index, failure.Token);
                if (failed(result)) failure.Cancel();
                return result;
            }
            catch
            {
                failure.Cancel();
                throw;
            }
        }
        return await Run(maximum, adaptive, shared, Observe, demand,
            () => failure.IsCancellationRequested || stopped(), cancellation, pollMs, waitForRoot);
    }
}
