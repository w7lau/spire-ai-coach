using System.Reflection;
using HarmonyLib;
using SpireAiCoach.Core;

// This experiment lives only in the console's parent scheduler. It is never
// copied into a game worker, installed in the player game or part of the mod.
// Reorder existing exact tasks; do not invent, merge, remove or reopen a branch.
static class LossRoundStudy
{
    private static readonly FieldInfo Winner = typeof(LocalTurnSearch).GetField("_winner", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Outcome = typeof(LocalTurnSearch).GetField("_winningOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static bool _enabled;
    public static int Reorders { get; private set; }
    public static void Begin(bool enabled) { _enabled = enabled; Reorders = 0; }
    public static void Install() => new Harmony("spire.route.study.parent.only").Patch(
        typeof(LocalTurnSearch).GetMethod(nameof(LocalTurnSearch.ObserveOutcome))!,
        postfix: new HarmonyMethod(typeof(LossRoundStudy).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic)!));

    private static void After(LocalTurnSearch __instance, LocalCandidate candidate)
    {
        if (!_enabled || !ReferenceEquals(Outcome.GetValue(__instance), candidate) ||
            candidate.Actions.Any(a => a.PotionSlot.HasValue)) return; // Preserve the current potion-saving precedence.
        var losses = (candidate.Decisions ?? []).Where(d => d.BeforeStep >= 0 && d.BeforeStep < candidate.Actions.Length &&
            d.HpBefore.HasValue && d.HpAfter.HasValue)
            .GroupBy(d => candidate.Actions[d.BeforeStep].Round)
            .ToDictionary(g => g.Key, g => g.Sum(d => Math.Max(0L, (long)d.HpBefore!.Value - d.HpAfter!.Value)));
        if (!losses.Values.Any(v => v > 0)) return;
        var queue = (Queue<(int Id, LocalAction[] Tail)>)Winner.GetValue(__instance)!;
        var original = queue.ToArray();
        var ordered = original.OrderByDescending(item => item.Tail.Length == 0 ? 0 : losses.GetValueOrDefault(item.Tail[0].Round)).ToArray();
        if (original.Select(x => x.Id).SequenceEqual(ordered.Select(x => x.Id))) return;
        if (!original.Select(x => x.Id).Order().SequenceEqual(ordered.Select(x => x.Id).Order()))
            throw new InvalidOperationException("Study changed the pending task multiset");
        queue.Clear();
        foreach (var item in ordered) queue.Enqueue(item);
        Reorders++;
    }
}
