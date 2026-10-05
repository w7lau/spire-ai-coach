using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

// The parent alone publishes FINISHED, valid native results. A running peer's
// seed can later be invalidated by replay divergence and cannot certify a cut.
public sealed class LocalSharedHealthBound(string directory, LocalSearchRequest request)
{
    private sealed record Published(string Scope, LocalWinningBound Bound);
    private readonly string _scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        request.Id, request.SnapshotId, request.NativeHash, request.ModelHash, request.LoadedMods,
        request.MaxRounds, request.IncludePotions, request.TargetVictoryRounds, request.TargetPotionUses,
        request.StopOnZeroLoss, request.StopOnFirstWin,
        request.RequireKnownZeroEnemyDamage, request.DataOnlyCombat, request.DataOnlyRun,
        request.NumericalExecution, request.FastNativeWaits, request.FastStateSettling, request.CardGoals,
        HealthObjective = "final-hp-v2"
    }))));
    private readonly string _file = Path.Combine(directory, "health-bound-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Id))) + ".json");
    private readonly object _gate = new();
    private LocalWinningBound? _published;
    private LocalWinningBound? _cached;
    private long _nextRead;
    public string Root => request.SnapshotId + ":" + request.NativeHash;

    public bool PublishFinished(LocalSearchResult result)
    {
        if (result.Id != request.Id || result.SnapshotId != request.SnapshotId ||
            result.Status is not ("searched" or "done") || result.Best is not { Actions.Length: > 0 } candidate ||
            candidate.Actions[0].BeforeHash != request.NativeHash ||
            request.CardGoals?.Enabled != true && LocalSearchPolicy.HasSpecificGoal(request) &&
            !LocalSearchPolicy.MeetsGoal(candidate, request)) return false;
        var bound = LocalWinningBound.From(Root, candidate, LocalSearchPolicy.PrefersFullHealth(request));
        if (bound == null) return false;
        lock (_gate)
        {
            var next = LocalHealthBound.Better(_published, bound);
            if (Equals(next, _published)) return false;
            try { LocalWire.Write(_file, new Published(_scope, next!)); }
            catch (IOException) { return false; } // Optional sharing must not invalidate a finished victory.
            _published = next;
            return true;
        }
    }

    public LocalWinningBound? Read()
    {
        long now = Environment.TickCount64;
        if (now < _nextRead) return _cached;
        _nextRead = now + 100;
        if (!File.Exists(_file)) return _cached;
        try
        {
            // The publisher atomically replaces this tiny document. A read needs
            // no IPC mutex or its five-second wait: a busy file can be skipped.
            using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var value = JsonSerializer.Deserialize<Published>(stream);
            if (value?.Scope == _scope && value.Bound is { } bound && bound.Root == Root &&
                bound.NetHpLoss >= 0 && bound.PotionsUsed >= 0)
                _cached = LocalHealthBound.Better(_cached, bound);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        { /* A busy or unreadable optional bound cannot stop native exploration. */ }
        return _cached;
    }
}
