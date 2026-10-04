using System.Diagnostics;

namespace SpireAiCoach.Core;

// This is published only after native cleanup, broker retirement and mode reset.
// The generation identifies one owned process, even across a recycled OS PID.
public sealed record LocalWorkerIdle(string Id, string SnapshotId, string NativeHash, int Worker, string Generation)
{
    public bool Matches(LocalSearchRequest request, string generation) =>
        generation.Length > 0 && Generation == generation && Id == request.Id &&
        SnapshotId == request.SnapshotId && NativeHash == request.NativeHash && Worker == request.Partition;
}

public sealed class LocalWorkerCancelledException : OperationCanceledException;

public static class LocalWorkerSession
{
    public static bool Cancelled(string root, LocalSearchRequest request)
    {
        var path = Path.Combine(root, "stop-search.json");
        return File.Exists(path) && LocalWire.Read<LocalSearchStop>(path) is { Cancel: true } stop && stop.Matches(request);
    }

    // Call only at a settled native boundary, never inside an unfinished command.
    public static void ThrowIfCancelled(string root, LocalSearchRequest request)
    {
        if (Cancelled(root, request)) throw new LocalWorkerCancelledException();
    }

    public static async Task<bool> WaitForIdle(string root, LocalSearchRequest request, string generation,
        Func<bool> alive, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        var path = Path.Combine(root, "idle.json");
        while (alive() && timer.Elapsed < timeout)
        {
            if (File.Exists(path) && LocalWire.Read<LocalWorkerIdle>(path).Matches(request, generation)) return true;
            await Task.Delay(25);
        }
        return false;
    }
}
