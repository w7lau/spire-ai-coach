using System.Reflection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace SpireAiCoach.Mod;

// Finite installed-version contract. This checks native API coverage, not that
// every card/Mod has been run or every gameplay hook is presentation-free.
public static class LocalSelectionCoverage
{
    private static readonly Dictionary<string, int[]> Entries = new(StringComparer.Ordinal)
    {
        ["FromChooseACardScreen"] = [4], ["FromChooseABundleScreen"] = [2],
        ["FromSimpleGrid"] = [4], ["FromSimpleGridForRewards"] = [4],
        ["FromCombatPile"] = [4, 5], ["FromHand"] = [5], ["FromHandForDiscard"] = [5],
        ["FromHandForUpgrade"] = [3], ["FromDeckForUpgrade"] = [2],
        ["FromDeckForTransformation"] = [3], ["FromDeckForEnchantment"] = [4, 4, 5],
        ["FromDeckForRemoval"] = [3], ["FromDeckGeneric"] = [4]
    };
    public static string[] Audit()
    {
        var failures = new List<string>();
        var actual = typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("From", StringComparison.Ordinal)).ToArray();
        foreach (var entry in actual)
            if (!Entries.TryGetValue(entry.Name, out var counts) || !counts.Contains(entry.GetParameters().Length))
                failures.Add("Uncovered native choice command: " + entry);
        foreach (var expected in Entries)
            if (!actual.Where(m => m.Name == expected.Key).Select(m => m.GetParameters().Length).Order()
                .SequenceEqual(expected.Value.Order())) failures.Add("Native choice signature changed: " + expected.Key);
        foreach (var screen in typeof(CardSelectCmd).Assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsNested &&
            t.Namespace == typeof(NCardGridSelectionScreen).Namespace && typeof(NCardGridSelectionScreen).IsAssignableFrom(t)))
            if (!LocalChoices.SupportedGrid(screen)) failures.Add("Uncovered native card grid: " + screen.Name);
        foreach (var type in new[] { typeof(NPlayerHand), typeof(NChooseACardSelectionScreen),
            typeof(NChooseABundleSelectionScreen), typeof(NCardGridSelectionScreen) })
            if (type.GetMethods(BindingFlags.Instance | BindingFlags.Public).All(m =>
                m.Name != (type == typeof(NPlayerHand) ? "SelectCards" : "CardsSelected")))
                failures.Add("Native selection completion changed: " + type.Name);
        return failures.ToArray();
    }
}
