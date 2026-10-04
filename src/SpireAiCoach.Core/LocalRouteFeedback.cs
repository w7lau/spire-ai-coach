namespace SpireAiCoach.Core;

// Measured native losses are diagnostic evidence. They do not prove a branch
// avoidable, simulate a card effect, or change the completed-candidate objective.
public static class LocalRouteFeedback
{
    public static IReadOnlyDictionary<int, double> RoundLosses(LocalCandidate candidate)
    {
        var decisions = (candidate.Decisions ?? []).GroupBy(d => d.BeforeStep)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var points = (candidate.Continuation ?? []).Where(p => p.ActionIndex >= 0 &&
            p.ActionIndex < candidate.Actions.Length && p.NativeHash == candidate.Actions[p.ActionIndex].BeforeHash)
            .GroupBy(p => p.ActionIndex).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var losses = new Dictionary<int, double>();
        for (int i = 0; i < candidate.Actions.Length; i++)
        {
            decisions.TryGetValue(i, out var decision);
            points.TryGetValue(i, out var beforePoint);
            points.TryGetValue(i + 1, out var afterPoint);
            int? before = decision?.HpBefore ?? beforePoint?.Hp;
            int? after = i == candidate.Actions.Length - 1 ? candidate.Hp : decision?.HpAfter ?? afterPoint?.Hp;
            if (before is not >= 0 || after is not >= 0) continue;
            int round = candidate.Actions[i].Round;
            losses[round] = losses.GetValueOrDefault(round) + (double)before.Value - after.Value;
        }
        // Healing in the measured interval offsets its costs, including native
        // victory settlement on the last action. Missing legacy data stays neutral.
        return losses.ToDictionary(p => p.Key, p => Math.Max(0, p.Value));
    }
}
