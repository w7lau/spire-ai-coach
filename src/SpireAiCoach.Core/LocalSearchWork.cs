using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpireAiCoach.Core;

public sealed record LocalWorkTask(string Key, string Kind, LocalAction[] Plan);
public sealed record LocalWorkStats(int Submitted, int Claimed, int Completed, int DuplicateOffers);

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
    }
    private sealed class Index
    {
        public List<Entry> Entries { get; set; } = [];
        public int DuplicateOffers { get; set; }
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
            Choices = (a.Choices ?? []).Select(c => new { c.Kind, c.OfferHash, c.Index, c.ModelId, c.Indices })
        })));

    public void Offer(string kind, IEnumerable<LocalAction[]> plans)
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
            // Frontier expansion must not fill the space reserved for improvements of a
            // later, better candidate. Each class has its own bounded capacity.
            if (count >= _capacityPerKind) break;
            // Every queue access holds the same gate. Immutable jobs are fully written
            // before publishing the index, without acquiring an extra IPC mutex per job.
            File.WriteAllText(Path.Combine(_directory, key + ".json"), JsonSerializer.Serialize(new LocalWorkTask(key, kind, plan)));
            index.Entries.Add(new() { Key = key, Kind = kind, Depth = plan.Length, Order = index.Entries.Count });
            count++;
        }
        Save(index);
    }

    public LocalWorkTask? Take(string kind, int owner, bool deeper = false)
    {
        using var gate = Lock();
        var index = Read();
        var candidates = index.Entries.Where(e => e.Kind == kind && !e.Owner.HasValue);
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
            index.Entries.Count(e => e.Completed), index.DuplicateOffers);
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
