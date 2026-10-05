using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

// In-process search proposals only. No native objects, saves, RNG or transferable proofs.
public sealed class LocalSearchSession : IDisposable
{
    private readonly string _key;
    public LocalTurnWorkState? Turns { get; }
    public LocalSearchWork? Work { get; }
    public LocalSearchResult? Baseline { get; private set; }
    public int Batches { get; private set; }
    public int TotalEvaluated { get; private set; }
    public int TotalVictories { get; private set; }
    public string? LastId { get; private set; }
    public int Pending => Turns?.Pending ?? Work?.Stats().Pending ?? 0;

    public static bool Supported(LocalSearchRequest r) => r.ShareSearchWork &&
        (r.SearchOrder == LocalSearchOrder.TurnFrontier || r.SearchOrder == LocalSearchOrder.MonteCarlo && r.MemorySearchWork);

    public LocalSearchSession(LocalSearchRequest request)
    {
        if (!Supported(request)) throw new ArgumentException("Search order has no retained parent frontier");
        _key = Key(request);
        if (request.SearchOrder == LocalSearchOrder.TurnFrontier) Turns = new(request);
        else Work = new("", request with { SearchWorkPipe = null }, inMemory: true);
    }

    public bool Matches(LocalSearchRequest request) => Supported(request) && _key == Key(request);
    public void Complete(LocalSearchResult result)
    {
        Batches++; TotalEvaluated += result.Evaluated; TotalVictories += result.Victories; LastId = result.Id;
        // Only a surviving candidate can be carried as a proposal. Final verification
        // and complete search checkpoints still belong to the pool/execution owners.
        Baseline = result.Best is { Dead: false } ? result : null;
    }
    public LocalSearchProgress Progress(bool resumed, int evaluated) =>
        new(Batches, evaluated, TotalEvaluated, TotalVictories, Pending, resumed, Pending > 0);

    internal static string Key(LocalSearchRequest request)
    {
        // Budget/admission and display transport do not change the legal search domain.
        // Include the full replay, native root/history, models, Mod versions, choices,
        // horizons and goals. A superficially identical hand is never enough.
        var stable = request with { Id = "", ContinueOptimization = false, ResumingFrontier = false,
            Partition = 0, Partitions = 1, Workers = 0, MaxNodes = 0, BudgetSeconds = 0,
            InitialPlan = null, VerifyCandidate = null, DeferVerification = false, TargetLabels = null,
            TimelineOrigin = 0, InitialTrace = null, TurnWorkPipe = null, SearchWorkPipe = null,
            MinimumLossPipe = null, ProgressPipe = null, StopOnZeroLoss = false, StopOnFirstWin = false,
            SkipFinalVerification = false, FastVerification = false };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stable))));
    }
    public void Dispose() { Work?.ReleasePlans(); Work?.Dispose(); }
}

public sealed record LocalSearchProgress(int Batch, int BatchEvaluated, int TotalEvaluated,
    int TotalVictories, int Pending, bool Resumed, bool CanContinue);

public sealed class LocalTurnWorkState
{
    internal readonly string Key;
    internal readonly LocalTurnSearch Frontier;
    internal readonly HashSet<string> Terminals = new(StringComparer.Ordinal);
    internal int Repeated, Taken, Rollouts;
    internal bool RootReady;
    public int Pending => Frontier.Count;
    public LocalTurnWorkState(LocalSearchRequest request)
    {
        Key = LocalSearchSession.Key(request);
        Frontier = new(1729, request.SnapshotId + ":" + request.NativeHash, request.CardGoals);
    }
}
