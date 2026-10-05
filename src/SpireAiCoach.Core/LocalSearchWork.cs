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
public sealed class LocalSearchWork : IDisposable
{
    private readonly string _directory;
    private readonly string _domainKey;
    private readonly int _capacityPerKind;
    private readonly object _memoryGate = new();
    private Index? _memory;
    private readonly Dictionary<string, LocalWorkTask> _plans = new(StringComparer.Ordinal);
    private readonly LocalSearchWorkClient? _remote;
    public LocalSearchWork(string parent, LocalSearchRequest request, int capacity = 512, bool inMemory = false)
    {
        _domainKey = LocalSearchSession.Key(request);
        var scope = Hash(request.Id + "\n" + request.SnapshotId + "\n" + request.NativeHash + "\n" +
            request.ModelHash + "\n" + string.Join("\n", request.LoadedMods));
        _directory = Path.Combine(parent, "search-work", scope);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacityPerKind = capacity;
        if (request.SearchWorkPipe != null) _remote = new(request);
        else if (inMemory) _memory = new();
        else Directory.CreateDirectory(_directory);
    }

    public bool Matches(LocalSearchRequest request) => _domainKey == LocalSearchSession.Key(request);

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
        public string? Parent { get; set; }
        public int Generation { get; set; }
    }
    private sealed class Index
    {
        public bool RootInitialized { get; set; }
        public List<Entry> Entries { get; set; } = [];
        public int DuplicateOffers { get; set; }
        public int CoveredJobs { get; set; }
        public HashSet<string> TerminalHistories { get; set; } = new(StringComparer.Ordinal);
        public HashSet<int> RetiredOwners { get; set; } = [];
        public Dictionary<int, string> Descents { get; set; } = [];
        public int Generation { get; set; }
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

    public void Offer(string kind, IEnumerable<LocalAction[]> plans, bool initializeRoot = false, string? parent = null)
    {
        if (kind is not ("expand" or "improve")) throw new ArgumentException("Unknown search job kind", nameof(kind));
        var batch = plans.ToArray();
        if (batch.Length == 0 && !initializeRoot) return;
        if (_remote != null) { _remote.Offer(kind, batch, initializeRoot, parent); return; }
        using var gate = Lock();
        var index = Read();
        var known = index.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        // Finished/claimed work must not permanently consume the frontier capacity.
        // The index retains identities and counters so this never reopens a job.
        var count = index.Entries.Count(e => e.Kind == kind && !e.Owner.HasValue && !e.Covered && e.Parent == null);
        var focusedCount = index.Entries.Count(e => e.Kind == kind && !e.Owner.HasValue && !e.Covered && e.Parent != null);
        int generation = ++index.Generation;
        foreach (var plan in batch)
        {
            if (plan.Length == 0) continue;
            var key = Key(kind, plan);
            if (known.TryGetValue(key, out var existing))
            {
                index.DuplicateOffers++;
                if (parent != null && !existing.Owner.HasValue && !existing.Covered)
                {
                    if (existing.Parent == null)
                    {
                        if (focusedCount >= _capacityPerKind) continue;
                        count--; focusedCount++;
                    }
                    existing.Parent = parent; existing.Generation = generation;
                }
                continue;
            }
            var history = kind == "expand" ? key : Key("expand", plan);
            if (index.TerminalHistories.Contains(history)) { index.CoveredJobs++; continue; }
            // Frontier expansion must not fill the space reserved for improvements of a
            // later, better candidate. Each class has its own bounded capacity.
            if (parent == null ? count >= _capacityPerKind : focusedCount >= _capacityPerKind) break;
            // Every queue access holds the same gate. Immutable jobs are fully written
            // before publishing the index, without acquiring an extra IPC mutex per job.
            var task = new LocalWorkTask(key, kind, plan);
            if (_memory != null) _plans.Add(key, task);
            else File.WriteAllText(Path.Combine(_directory, key + ".json"), JsonSerializer.Serialize(task));
            var entry = new Entry { Key = key, Kind = kind, Depth = plan.Length, Order = index.Entries.Count,
                HistoryKey = history, Parent = parent, Generation = generation };
            index.Entries.Add(entry); known.Add(key, entry);
            if (parent == null) count++; else focusedCount++;
        }
        // Publish readiness under the same gate as the first root jobs. A worker
        // reaching this queue first must not confuse startup with exhaustion.
        if (initializeRoot) index.RootInitialized = true;
        Save(index);
    }

    public LocalWorkTask? Take(string kind, int owner, bool deeper = false, bool focused = false)
    {
        if (_remote != null) return _remote.Take(kind, owner, deeper, focused);
        using var gate = Lock();
        var index = Read();
        var candidates = index.Entries.Where(e => e.Kind == kind && !e.Owner.HasValue && !e.Covered);
        // Interleave shallow and deeper frontier work, rather than starving late-battle forks.
        Entry? entry = null;
        if (focused && kind == "expand")
        {
            var descent = index.Descents.GetValueOrDefault(owner);
            entry = candidates.Where(e => e.Parent != null && e.Parent == descent).OrderBy(e => e.Order).FirstOrDefault()
                ?? candidates.Where(e => e.Parent != null).OrderByDescending(e => e.Generation).ThenBy(e => e.Order).FirstOrDefault();
        }
        if (kind == "improve" && !deeper)
            entry = candidates.OrderByDescending(e => e.Generation).ThenBy(e => e.Depth).ThenBy(e => e.Order).FirstOrDefault();
        entry ??= deeper ? candidates.OrderByDescending(e => e.Depth).ThenBy(e => e.Order).FirstOrDefault()
            : candidates.OrderBy(e => e.Depth).ThenBy(e => e.Order).FirstOrDefault();
        if (entry == null) return null;
        var task = _memory != null ? _plans[entry.Key] : Read<LocalWorkTask>(Path.Combine(_directory, entry.Key + ".json"));
        if (task.Key != entry.Key || task.Kind != kind || Key(kind, task.Plan) != entry.Key)
            throw new InvalidDataException("Shared search job identity changed");
        entry.Owner = owner;
        if (focused && kind == "expand") index.Descents[owner] = entry.Key;
        Save(index);
        return task;
    }

    public void Complete(LocalWorkTask task)
    {
        if (_remote != null) { _remote.Complete(task); return; }
        using var gate = Lock();
        var index = Read();
        var entry = index.Entries.Single(e => e.Key == task.Key);
        if (!entry.Owner.HasValue) throw new InvalidOperationException("Search job was not claimed");
        entry.Completed = true;
        Save(index);
    }

    public LocalWorkStats Stats()
    {
        if (_remote != null) return _remote.Stats();
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
        if (_remote != null) { _remote.RecordTerminal(candidate); return; }
        var history = Key("expand", candidate.Actions);
        RecordTerminalDigest(history);
    }
    internal void RecordTerminalDigest(string history)
    {
        if (history.Length != 64 || !history.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid terminal history digest");
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
        if (_remote != null) { _remote.Retire(owner); return; }
        using var gate = Lock();
        var index = Read();
        if (index.RetiredOwners.Add(owner)) Save(index);
    }

    // Called only after the old broker and its clients have fully stopped.
    // Completed identities stay closed; interrupted claims become pending again.
    public void Resume()
    {
        if (_remote != null || _memory == null) throw new InvalidOperationException("Only a retained parent queue resumes");
        using var gate = Lock();
        foreach (var entry in _memory.Entries)
        {
            if (!entry.Completed && !entry.Covered) entry.Owner = null;
            else _plans.Remove(entry.Key);
        }
        _memory.RetiredOwners.Clear(); _memory.Descents.Clear();
    }

    // Only the pool calls this after all search/verification lanes have finished.
    // Retain the small index for diagnostics; do not retain unused action plans forever.
    public void ReleasePlans()
    {
        if (_remote != null) throw new InvalidOperationException("Only the parent releases shared plans");
        using var gate = Lock();
        if (_memory != null) { _plans.Clear(); return; }
        foreach (var entry in Read().Entries)
        {
            if (entry.Key.Length != 64 || !entry.Key.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid shared search job filename");
            File.Delete(Path.Combine(_directory, entry.Key + ".json"));
        }
    }

    private IDisposable Lock()
    {
        if (_memory != null) { Monitor.Enter(_memoryGate); return new MemoryLease(_memoryGate); }
        var started = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(Path.Combine(_directory, ".gate"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.Elapsed.TotalSeconds < 2) { Thread.Sleep(2); }
        }
    }
    private sealed class MemoryLease(object gate) : IDisposable { public void Dispose() => Monitor.Exit(gate); }
    private Index Read() => _memory ?? (File.Exists(Path.Combine(_directory, "index.json"))
        ? Read<Index>(Path.Combine(_directory, "index.json")) : new());
    private static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Shared search data is empty");
    }
    private void Save(Index index)
    {
        if (_memory != null) return;
        var path = Path.Combine(_directory, "index.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(index));
        File.Move(path + ".tmp", path, true);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() => _remote?.Dispose();
}
