using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

public sealed record LocalWorkTask(string Key, string Kind, LocalAction[] Plan);
public sealed record LocalWorkStats(int Submitted, int Claimed, int Completed, int DuplicateOffers,
    int Pending = 0, int Active = 0, int CoveredJobs = 0, bool RootReady = false);

// A bounded shared frontier of exact action-history jobs. It stores proposals, never a
// substitute combat state or a transferable score. Every claimed job runs natively.
public sealed class LocalSearchWork
{
    private readonly string _directory;
    private readonly int _capacityPerKind;
    public LocalSearchWork(string parent, LocalSearchRequest request, int capacity = 512)
    {
        var scope = Hash(request.Id + "\n" + request.SnapshotId + "\n" + request.NativeHash + "\n" +
            request.ModelHash + "\n" + string.Join("\n", request.LoadedMods));
        _directory = Path.Combine(parent, "search-work", scope);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacityPerKind = capacity;
        Directory.CreateDirectory(_directory);
    }

    private sealed class Entry
    {
        public string Key { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Depth { get; set; }
        public int Order { get; set; }
        public int? Owner { get; set; }
        public bool Completed { get; set; }
        public bool Covered { get; set; }
        public string HistoryKey { get; set; } = "";
    }
    private sealed class Index
    {
        public bool RootInitialized { get; set; }
        public List<Entry> Entries { get; set; } = [];
        public int DuplicateOffers { get; set; }
        public int CoveredJobs { get; set; }
        public HashSet<string> TerminalHistories { get; set; } = new(StringComparer.Ordinal);
        public HashSet<int> RetiredOwners { get; set; } = [];
    }

    // Exclude display text and mutable hand positions when a native card instance exists.
    // Expansion jobs keep all before-state hashes: different histories are never merged
    // merely because their visible outcome happens to look the same.
    public static string Key(string kind, IReadOnlyList<LocalAction> plan) => Hash(kind + "\n" +
        JsonSerializer.Serialize(plan.Select(a => new
        {
            a.Round, a.EndTurn, a.PotionSlot, a.CombatCardIndex,
            Hand = a.CombatCardIndex.HasValue ? null : (int?)a.HandIndex,
            a.ModelId, a.TargetId, Before = kind == "expand" ? a.BeforeHash : null,
            Choices = (a.Choices ?? []).Select(c => new { c.Kind, c.OfferHash, c.Index, c.ModelId, Indices = c.Indices ?? [] })
        })));

    public static bool MatchesPrefix(IReadOnlyList<LocalAction> executed, IReadOnlyList<LocalAction> planned) =>
        executed.Count >= planned.Count && planned.Select((expected, i) => LocalTurnSearch.SameAction(expected, executed[i]) &&
            (expected.Choices ?? []).Select((choice, j) => j < (executed[i].Choices?.Length ?? 0) &&
                LocalTurnSearch.SameChoice(choice, executed[i].Choices![j])).All(match => match)).All(match => match);

    public void Offer(string kind, IEnumerable<LocalAction[]> plans, bool initializeRoot = false)
    {
        if (kind is not ("expand" or "improve")) throw new ArgumentException("Unknown search job kind", nameof(kind));
        using var gate = Lock();
        var index = Read();
        var known = index.Entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var count = index.Entries.Count(e => e.Kind == kind);
        foreach (var plan in plans)
        {
            if (plan.Length == 0) continue;
            var key = Key(kind, plan);
            if (!known.Add(key)) { index.DuplicateOffers++; continue; }
            var history = kind == "expand" ? key : Key("expand", plan);
            if (index.TerminalHistories.Contains(history)) { index.CoveredJobs++; continue; }
            // Frontier expansion must not fill the space reserved for improvements of a
            // later, better candidate. Each class has its own bounded capacity.
            if (count >= _capacityPerKind) break;
            // Every queue access holds the same gate. Immutable jobs are fully written
            // before publishing the index, without acquiring an extra IPC mutex per job.
            File.WriteAllText(Path.Combine(_directory, key + ".json"), JsonSerializer.Serialize(new LocalWorkTask(key, kind, plan)));
            index.Entries.Add(new() { Key = key, Kind = kind, Depth = plan.Length, Order = index.Entries.Count, HistoryKey = history });
            count++;
        }
        // Publish readiness under the same gate as the first root jobs. A worker
        // reaching this queue first must not confuse startup with exhaustion.
        if (initializeRoot) index.RootInitialized = true;
        Save(index);
    }

    public LocalWorkTask? Take(string kind, int owner, bool deeper = false)
    {
        using var gate = Lock();
        var index = Read();
        var candidates = index.Entries.Where(e => e.Kind == kind && !e.Owner.HasValue && !e.Covered);
        // Interleave shallow and deeper frontier work, rather than starving late-battle forks.
        var entry = deeper ? candidates.OrderByDescending(e => e.Depth).ThenBy(e => e.Order).FirstOrDefault()
            : candidates.OrderBy(e => e.Depth).ThenBy(e => e.Order).FirstOrDefault();
        if (entry == null) return null;
        var task = Read<LocalWorkTask>(Path.Combine(_directory, entry.Key + ".json"));
        if (task.Key != entry.Key || task.Kind != kind || Key(kind, task.Plan) != entry.Key)
            throw new InvalidDataException("Shared search job identity changed");
        entry.Owner = owner;
        Save(index);
        return task;
    }

    public void Complete(LocalWorkTask task)
    {
        using var gate = Lock();
        var index = Read();
        var entry = index.Entries.Single(e => e.Key == task.Key);
        if (!entry.Owner.HasValue) throw new InvalidOperationException("Search job was not claimed");
        entry.Completed = true;
        Save(index);
    }

    public LocalWorkStats Stats()
    {
        using var gate = Lock();
        var index = Read();
        return new(index.Entries.Count, index.Entries.Count(e => e.Owner.HasValue),
            index.Entries.Count(e => e.Completed), index.DuplicateOffers,
            index.Entries.Count(e => !e.Owner.HasValue && !e.Covered),
            index.Entries.Count(e => e.Owner.HasValue && !e.Completed && !index.RetiredOwners.Contains(e.Owner.Value)), index.CoveredJobs,
            index.RootInitialized || index.RetiredOwners.Contains(0));
    }

    // Close only the exact settled terminal history. No HP/block state merging,
    // transfer of a candidate score, or closure of interrupted/horizon trials.
    public void RecordTerminal(LocalCandidate candidate)
    {
        if ((!candidate.Won && !candidate.Dead) || candidate.Actions.Length == 0) return;
        var history = Key("expand", candidate.Actions);
        using var gate = Lock();
        var index = Read();
        if (!index.TerminalHistories.Add(history)) return;
        foreach (var entry in index.Entries.Where(e => !e.Owner.HasValue && !e.Covered && e.HistoryKey == history))
        { entry.Covered = true; index.CoveredJobs++; }
        Save(index);
    }

    // An interrupted job is not a completed route. It also must not make idle
    // peers wait for new proposals from a worker that has already returned.
    public void Retire(int owner)
    {
        using var gate = Lock();
        var index = Read();
        if (index.RetiredOwners.Add(owner)) Save(index);
    }

    // Only the pool calls this after all search/verification lanes have finished.
    // Retain the small index for diagnostics; do not retain unused action plans forever.
    public void ReleasePlans()
    {
        using var gate = Lock();
        foreach (var entry in Read().Entries)
        {
            if (entry.Key.Length != 64 || !entry.Key.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid shared search job filename");
            File.Delete(Path.Combine(_directory, entry.Key + ".json"));
        }
    }

    private FileStream Lock()
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            try { return new(Path.Combine(_directory, ".gate"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.Elapsed.TotalSeconds < 2) { Thread.Sleep(2); }
        }
    }
    private Index Read() => File.Exists(Path.Combine(_directory, "index.json"))
        ? Read<Index>(Path.Combine(_directory, "index.json")) : new();
    private static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Shared search data is empty");
    }
    private void Save(Index index)
    {
        var path = Path.Combine(_directory, "index.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(index));
        File.Move(path + ".tmp", path, true);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
