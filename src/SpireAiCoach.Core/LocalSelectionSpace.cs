using System.Numerics;

namespace SpireAiCoach.Core;

// Exact lazy native selection domain. A page is an exploration window, never a
// declaration that the rest of a large ordered/multi-select offer is illegal.
public sealed class LocalSelectionSpace
{
    public int CardCount { get; }
    public bool Ordered { get; }
    private readonly int[] _sizes;
    private readonly BigInteger[] _counts;
    public BigInteger Count { get; }
    public LocalSelectionSpace(int count, int minimum, int maximum, bool ordered = true, int[]? sizes = null)
    {
        minimum = Math.Max(0, minimum); maximum = Math.Min(count, maximum);
        if (count < 0 || minimum > maximum) throw new InvalidOperationException("Invalid native selection limits");
        CardCount = count; Ordered = ordered;
        _sizes = (sizes ?? Enumerable.Range(minimum, maximum - minimum + 1).ToArray()).Distinct().Order().ToArray();
        if (_sizes.Length == 0 || _sizes.Any(s => s < minimum || s > maximum))
            throw new InvalidOperationException("Invalid native selection sizes");
        _counts = _sizes.Select(s => Ways(count, s, ordered)).ToArray();
        Count = _counts.Aggregate(BigInteger.Zero, (sum, c) => sum + c);
    }
    private static BigInteger Ways(int n, int k, bool ordered)
    {
        if (k < 0 || k > n) return 0;
        BigInteger result = 1;
        if (!ordered) k = Math.Min(k, n - k);
        for (int i = 0; i < k; i++) { result *= n - i; if (!ordered) result /= i + 1; }
        return result;
    }
    private BigInteger BeforeRound(BigInteger round) => _counts.Aggregate(BigInteger.Zero,
        (sum, c) => sum + BigInteger.Min(c, round));
    public int[] At(BigInteger rank)
    {
        if (rank < 0 || rank >= Count) throw new ArgumentOutOfRangeException(nameof(rank));
        // Interleave sizes fairly without materializing every preceding combination.
        BigInteger lo = 0, hi = _counts.Max();
        while (lo + 1 < hi) { var mid = (lo + hi) / 2; if (BeforeRound(mid) <= rank) lo = mid; else hi = mid; }
        var offset = rank - BeforeRound(lo);
        int group = 0;
        for (; group < _sizes.Length; group++)
            if (_counts[group] > lo && offset-- == 0) break;
        var within = lo;
        int size = _sizes[group];
        var available = Enumerable.Range(0, CardCount).ToList();
        var chosen = new int[size];
        int start = 0;
        for (int i = 0; i < size; i++)
        {
            if (Ordered)
            {
                var block = Ways(CardCount - i - 1, size - i - 1, true);
                int index = (int)(within / block); within %= block;
                chosen[i] = available[index]; available.RemoveAt(index);
            }
            else
                for (int index = start; index < CardCount; index++)
                {
                    var block = Ways(CardCount - index - 1, size - i - 1, false);
                    if (within >= block) { within -= block; continue; }
                    chosen[i] = index; start = index + 1; break;
                }
        }
        return chosen;
    }
    public BigInteger Rank(IReadOnlyList<int> indices)
    {
        int group = Array.IndexOf(_sizes, indices.Count);
        if (group < 0 || indices.Any(i => i < 0 || i >= CardCount) || indices.Distinct().Count() != indices.Count ||
            !Ordered && !indices.SequenceEqual(indices.Order())) throw new InvalidOperationException("Invalid native selection");
        var available = Enumerable.Range(0, CardCount).ToList();
        BigInteger within = 0;
        int start = 0;
        for (int i = 0; i < indices.Count; i++)
            if (Ordered)
            {
                int index = available.IndexOf(indices[i]);
                within += index * Ways(CardCount - i - 1, indices.Count - i - 1, true);
                available.RemoveAt(index);
            }
            else
            {
                for (int index = start; index < indices[i]; index++)
                    within += Ways(CardCount - index - 1, indices.Count - i - 1, false);
                start = indices[i] + 1;
            }
        return BeforeRound(within) + _counts.Take(group).Count(c => c > within);
    }
}

// One request owns this cursor. Repeated exact offers advance rather than hiding
// everything after the first 64 combinations. Full replay validates indices directly.
public sealed class LocalSelectionCursor
{
    private readonly Dictionary<string, BigInteger> _next = new(StringComparer.Ordinal);
    public BigInteger[] Page(string offer, LocalSelectionSpace space, int limit = 128)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (space.Count <= limit) return Enumerable.Range(0, (int)space.Count).Select(i => new BigInteger(i)).ToArray();
        var start = _next.GetValueOrDefault(offer);
        var ranks = Enumerable.Range(0, limit).Select(i => (start + i) % space.Count).ToArray();
        _next[offer] = (start + limit) % space.Count;
        return ranks;
    }
}
