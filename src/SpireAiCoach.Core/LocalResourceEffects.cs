namespace SpireAiCoach.Core;

// Observations from native execution, not a card-effect implementation. Only
// the same surviving hand instances can establish an observed cost reduction.
// A newly generated/drawn card, a removed card or the played card is not credit.
public static class LocalResourceEffects
{
    public static int HandCostSavings(IReadOnlyDictionary<uint, int> before,
        IReadOnlyDictionary<uint, int> after, uint played)
    {
        long savings = 0;
        foreach (var (id, cost) in before)
            if (id != played && cost >= 0 && after.TryGetValue(id, out var now) && now >= 0)
                savings += Math.Max(0L, (long)cost - now);
        return (int)Math.Min(int.MaxValue, savings);
    }
}
