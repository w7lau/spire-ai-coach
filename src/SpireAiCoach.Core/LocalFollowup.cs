namespace SpireAiCoach.Core;

// A selection-order hint, not an effect simulator or a bound for pruning.
// Values/costs come from native previews in the selection's current state.
public readonly record struct LocalFollowupCard(int Value, int EnergyCost, int StarCost = 0);

public static class LocalFollowup
{
    public static int Value(IEnumerable<LocalFollowupCard> cards, int energy, int stars)
    {
        if (energy < 0 || stars < 0) return 0;
        var reachable = new Dictionary<(int Energy, int Stars), int> { [(0, 0)] = 0 };
        foreach (var card in cards)
        {
            if (card.Value <= 0 || card.EnergyCost < 0 || card.StarCost < 0 ||
                card.EnergyCost > energy || card.StarCost > stars) continue;
            // Each returned instance can contribute once. The actual next play
            // re-enumerates native legality, costs and hooks, including loops.
            foreach (var state in reachable.ToArray())
            {
                if (card.EnergyCost > energy - state.Key.Energy || card.StarCost > stars - state.Key.Stars) continue;
                var spent = (state.Key.Energy + card.EnergyCost, state.Key.Stars + card.StarCost);
                int value = (int)Math.Min(1_000_000L, (long)state.Value + card.Value);
                if (value > reachable.GetValueOrDefault(spent)) reachable[spent] = value;
            }
        }
        return reachable.Values.Max();
    }
}
