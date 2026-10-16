namespace SpireAiCoach.Core;

// Estimated return goal only. Each monotone envelope includes every lower HP
// entering the callback, so losing HP to activate a threshold remains possible.
public static class LocalTerminalHealth
{
    public static int[] Envelope(int[] outcomes)
    {
        var envelope = new int[outcomes.Length];
        for (int hp = 1; hp < outcomes.Length; hp++)
        {
            if (outcomes[hp] < hp || outcomes[hp] >= outcomes.Length) throw new ArgumentException("Invalid health transfer");
            envelope[hp] = Math.Max(hp, Math.Max(envelope[hp - 1], outcomes[hp]));
        }
        return envelope;
    }

    // Actual callback order may interleave different owners. Consider every
    // order, each callback once, rather than summing conditional heal amounts.
    public static int? Compose(int start, IReadOnlyList<int[]> effects)
    {
        if (effects.Count > 8 || start <= 0 || effects.Any(e => start >= e.Length)) return null;
        var bounds = new int[1 << effects.Count]; bounds[0] = start;
        for (int mask = 0; mask < bounds.Length; mask++)
            for (int n = 0; n < effects.Count; n++)
                if ((mask & (1 << n)) == 0)
                {
                    if (bounds[mask] >= effects[n].Length) return null;
                    int next = mask | (1 << n);
                    bounds[next] = Math.Max(bounds[next], effects[n][bounds[mask]]);
                }
        return bounds[^1];
    }
}
