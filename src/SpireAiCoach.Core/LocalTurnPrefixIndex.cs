using System.Text.Json;

namespace SpireAiCoach.Core;

// Exact ordered native history identities, never a cache of game states or outcomes.
internal sealed class LocalTurnPrefixIndex
{
    private readonly List<Dictionary<StepKey, int>> _nodes = [new()];
    private readonly record struct StepKey(string Before, int Round, bool End, int? Potion, uint? Target,
        string Model, uint? Card, int? Hand, string Choices);
    public int Count => _nodes.Count;
    public void Clear() { _nodes.Clear(); _nodes.Add(new()); }
    public int GetId(IReadOnlyList<LocalAction> actions, bool create)
    {
        // Intern exact action identities under their exact parents. This shares
        // immutable history structure, never native states, RNG or Mod objects.
        // Typed fields avoid serializing and storing the whole prefix per fork.
        int parent = 0;
        foreach (var action in actions)
        {
            string choices = action.Choices is not { Length: > 0 } ? "" : JsonSerializer.Serialize(
                action.Choices.Select(c => new { c.OfferHash, c.Kind, c.Index, c.ModelId, Indices = c.Indices ?? [] }));
            var step = new StepKey(action.BeforeHash, action.Round, action.EndTurn, action.PotionSlot,
                action.TargetId, action.ModelId, action.CombatCardIndex,
                action.CombatCardIndex.HasValue ? null : action.HandIndex, choices);
            var children = _nodes[parent];
            if (!children.TryGetValue(step, out int next))
            {
                if (!create) return -1;
                next = _nodes.Count; children.Add(step, next); _nodes.Add(new());
            }
            parent = next;
        }
        return parent;
    }

}
