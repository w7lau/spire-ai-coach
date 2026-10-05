using MegaCrit.Sts2.Core.Entities.Creatures;

namespace SpireAiCoach.Mod;

// The game's Fatal rewards use this native power hook, rather than a model/name
// allowlist. It includes MinionPower and Mod powers implementing the same rule.
internal static class LocalFinisherEligibility
{
    public static bool AllowsFatal(Creature? target) => target?.IsEnemy == true &&
        target.Powers.All(power => power.ShouldOwnerDeathTriggerFatal());
}
