using SpireAiCoach.Core;

internal static class FollowupTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool condition) { if (!condition) throw new Exception("Follow-up resource regression"); }
        test("retrieval considers remaining energy and free repeat plays", () =>
        {
            var expensive = new LocalFollowupCard(60, 2);
            var free = new LocalFollowupCard(18, 0);
            Check(LocalFollowup.Value([expensive], 1, 0) == 0);
            Check(LocalFollowup.Value([free], 0, 0) == 18);
            Check(LocalFollowup.Value([expensive, free], 2, 0) == 78);
            Check(LocalFollowup.Value([free, free], 0, 0) == 36);
        });
        test("returned bundles share energy and stars rather than summing impossible plays", () =>
        {
            var cards = new[] { new LocalFollowupCard(40, 2), new(35, 1, 1), new(30, 1, 1), new(8, 0) };
            Check(LocalFollowup.Value(cards, 2, 0) == 48);
            Check(LocalFollowup.Value(cards, 2, 1) == 48);
            Check(LocalFollowup.Value(cards, 2, 2) == 73);
        });
        test("follow-up resource allocation matches an independent exhaustive subset oracle", () =>
        {
            var random = new Random(374);
            for (int sample = 0; sample < 120; sample++)
            {
                var cards = Enumerable.Range(0, 7).Select(_ => new LocalFollowupCard(random.Next(-8, 81),
                    random.Next(0, 4), random.Next(0, 3))).ToArray();
                int energy = random.Next(0, 7), stars = random.Next(0, 5), best = 0;
                for (int mask = 0; mask < 1 << cards.Length; mask++)
                {
                    var chosen = cards.Where((_, i) => (mask & 1 << i) != 0).ToArray();
                    if (chosen.Sum(c => c.EnergyCost) <= energy && chosen.Sum(c => c.StarCost) <= stars)
                        best = Math.Max(best, chosen.Sum(c => c.Value));
                }
                Check(LocalFollowup.Value(cards, energy, stars) == best);
            }
        });
        test("opaque or unaffordable followups remain neutral without invalid free costs", () =>
        {
            Check(LocalFollowup.Value([new(100, -1), new(100, 0, -1), new(-30, 0), new(0, 0)], 3, 3) == 0);
            Check(LocalFollowup.Value([new(10, 0)], -1, 0) == 0);
            Check(LocalFollowup.Value([new(10, 0)], 0, -1) == 0);
        });
    }
}
