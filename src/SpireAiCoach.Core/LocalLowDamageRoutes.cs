using System.Text.Json;

namespace SpireAiCoach.Core;

// A bounded proposal archive, not a dominance relation over native states.
// Equal HP/damage can hide different cards spent and different future draws.
public sealed class LocalLowDamageRoutes
{
    private sealed record Seed(string Cards, LocalCandidate Candidate, LocalRouteRefiner Refiner);
    private readonly List<Seed> _seeds = [];
    private int _next;
    public int Count => _seeds.Sum(s => s.Refiner.Count);

    public void Offer(LocalCandidate candidate)
    {
        if (!LocalSequenceTactics.BetterLowDamageSeed(candidate, null) || candidate.Actions.Length == 0) return;
        int lowest = _seeds.Count == 0 ? int.MaxValue : _seeds[0].Candidate.HpLost;
        if (candidate.HpLost > lowest) return;
        if (candidate.HpLost < lowest) { _seeds.Clear(); _next = 0; }
        // Targets and ordering are explored by the refiner. Keep independent
        // seeds for different first-turn card/resource combinations as well.
        string cards = JsonSerializer.Serialize(candidate.Actions.Where(a => a.Round == candidate.Actions[0].Round && !a.EndTurn)
            .Select(a => (a.CombatCardIndex, a.ModelId, a.PotionSlot))
            .OrderBy(a => a.CombatCardIndex).ThenBy(a => a.ModelId, StringComparer.Ordinal).ThenBy(a => a.PotionSlot)
            .Select(a => new { a.CombatCardIndex, a.ModelId, a.PotionSlot }));
        int existing = _seeds.FindIndex(s => s.Cards == cards);
        if (existing >= 0)
        {
            var seed = _seeds[existing];
            if (!LocalSequenceTactics.BetterLowDamageSeed(candidate, seed.Candidate)) return;
            seed.Refiner.Offer(candidate, preserveSequence: true);
            _seeds[existing] = seed with { Candidate = candidate };
        }
        else if (_seeds.Count < 4)
        {
            var refiner = new LocalRouteRefiner(); refiner.Offer(candidate, preserveSequence: true);
            _seeds.Add(new(cards, candidate, refiner));
        }
    }

    public bool TryTake(out LocalAction[] proposal, out int round, out LocalRolloutStyle style)
    {
        for (int i = 0; i < _seeds.Count; i++)
        {
            var seed = _seeds[_next++ % _seeds.Count];
            if (!seed.Refiner.TryTake(out proposal!)) continue;
            round = seed.Candidate.Actions[0].Round; style = seed.Candidate.RolloutStyle; return true;
        }
        proposal = []; round = 0; style = default; return false;
    }
}
