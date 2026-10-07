using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Metadata probes never invoke a callback. Native stopping is separately checked
// by replaying the frozen real incident and independently verifying every step.
internal static class HealthTargetReturnIntegration
{
    private abstract class AbstractSource<T> : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
        public void Entry(Creature creature) => Apply(creature);
        protected abstract void Apply(Creature creature);
    }
    private sealed class NoRecoverySource : AbstractSource<int>
    { protected override void Apply(Creature creature) { } }
    private sealed class RecoverySource : AbstractSource<int>
    { protected override void Apply(Creature creature) => creature.SetCurrentHpInternal(10); }
    private sealed class ExternalTurnSource : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
        public override Task AfterPlayerTurnStartLate(PlayerChoiceContext context, Player player) =>
            Owner.PlayerCombatState!.TurnNumber > 1 ? Task.CompletedTask : CreatureCmd.Heal(Owner.Creature, 1, true);
    }

    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest frozen, LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best ?? throw new InvalidOperationException("Frozen native win missing");
        if (!LocalSearchPolicy.WinningRouteFrom(seed, frozen) || seed.HpChange != 1 || seed.Hp >= seed.MaxHp)
            throw new InvalidOperationException("This native fixture requires a same-root, non-full, plus-one victory");
        var estimatorType = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalRecoveryEstimator", true)!;
        var metadata = Activator.CreateInstance(estimatorType, frozen)!;
        var describe = estimatorType.GetMethod("DescribeContent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var probes = new List<object>();
        foreach (var type in new[] { typeof(NoRecoverySource), typeof(RecoverySource) })
        {
            var effect = describe.Invoke(metadata, [type])!;
            bool Flag(string name) => (bool)effect.GetType().GetProperty(name)!.GetValue(effect)!;
            bool recovery = type == typeof(RecoverySource);
            if (Flag("Uncertain") || Flag("ActiveRecovery") != recovery)
                throw new InvalidOperationException("Abstract dispatch metadata did not resolve the concrete callback: " + type.Name + ": " +
                    effect.GetType().GetProperty("UncertainAt")!.GetValue(effect));
            probes.Add(new { kind = type.Name, activeRecovery = Flag("ActiveRecovery"), uncertain = Flag("Uncertain"), callbackExecuted = false });
        }
        // Use the installed native async body. A missing future-turn context must
        // keep the heal, while ready-to-play roots cannot repeat its first-turn heal.
        var bloodVial = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Relics.BloodVial", true)!;
        foreach (int? nextTurn in new int?[] { null, 1, 2, 3 })
        {
            var instance = Activator.CreateInstance(estimatorType, frozen)!;
            estimatorType.GetField("_nextPlayerTurnNumber", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, nextTurn);
            var effect = describe.Invoke(instance, [bloodVial])!;
            bool active = (bool)effect.GetType().GetProperty("ActiveRecovery")!.GetValue(effect)!;
            if (active != (nextTurn is null or <= 1) || (bool)effect.GetType().GetProperty("Uncertain")!.GetValue(effect)!)
                throw new InvalidOperationException("Native first-turn callback was classified outside its reachable phase");
            probes.Add(new { kind = bloodVial.Name, nextTurn, activeRecovery = active, callbackExecuted = false });
        }
        var external = Activator.CreateInstance(estimatorType, frozen)!;
        estimatorType.GetField("_nextPlayerTurnNumber", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(external, 2);
        var externalEffect = describe.Invoke(external, [typeof(ExternalTurnSource)])!;
        if (!(bool)externalEffect.GetType().GetProperty("ActiveRecovery")!.GetValue(externalEffect)!)
            throw new InvalidOperationException("External callback was incorrectly given a native phase proof");
        probes.Add(new { kind = nameof(ExternalTurnSource), activeRecovery = true, externalCallbackRetained = true, callbackExecuted = false });
        foreach (var type in typeof(AbstractModel).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(PowerModel).IsAssignableFrom(t)))
        {
            var instance = Activator.CreateInstance(estimatorType, frozen)!;
            var effect = describe.Invoke(instance, [type])!;
            if (!(bool)effect.GetType().GetProperty("ActiveRecovery")!.GetValue(effect)!) continue;
            var future = Activator.CreateInstance(estimatorType, frozen)!;
            estimatorType.GetField("_nextPlayerTurnNumber", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(future, 2);
            var futureEffect = describe.Invoke(future, [type])!;
            if (!(bool)futureEffect.GetType().GetProperty("ActiveRecovery")!.GetValue(futureEffect)!)
                throw new InvalidOperationException("Future turn context hid an active native power: " + type.FullName);
            probes.Add(new { kind = type.Name, activeRecovery = true, futureRecoveryPreserved = true, callbackExecuted = false });
        }
        var samples = new List<object>();
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            var request = frozen with { Id = Guid.NewGuid().ToString("N"), SearchOrder = order, Workers = 1,
                MaxNodes = 2, BudgetSeconds = 10, InitialPlan = seed.Actions, StopOnZeroLoss = true,
                StopOnFirstWin = false, ShareSearchWork = false, SkipFinalVerification = false,
                MaxRounds = frozen.MaxRounds, VerifyCandidate = null, InitialTrace = null, TimelineOrigin = 0 };
            var result = await Task.Run(() => pool.Analyze(request, installation, _ => { }, CancellationToken.None));
            LocalWire.Write(Path.Combine(root, "integration-health-target-return-" + order + "-private.json"), result);
            if (result.Status != "done" || !result.StoppedEarly || !result.StoppedOnHealthTarget || result.StoppedOnMinimum ||
                result.StoppedOnManualVictory || result.Evaluated != 1 || result.Best is not { Won: true, Dead: false } best ||
                best.Hp != seed.Hp || best.MaxHp != seed.MaxHp || result.HealthTarget is not { FullHealth: false } target ||
                target.TargetHp != seed.Hp || target.StartingHp != seed.StartingHp || best.Hp >= best.MaxHp ||
                !LocalSearchPolicy.CanStopAtHealthTarget(best, request, target) ||
                result.Timing?.Verifications != 1 || !LocalSearchPolicy.HasExecutionPoints(result) ||
                result.RecoveredFailures is { Length: > 0 } || result.HealthBounds?.TargetAnalyses != 1 ||
                result.HealthBounds.UnknownReason?.Contains("IsAvailableForCharacter", StringComparison.Ordinal) == true)
                throw new InvalidOperationException("Frozen plus-one victory did not return at its original content goal: " +
                    System.Text.Json.JsonSerializer.Serialize(new { result.Status, result.Message, result.Evaluated,
                        result.StoppedOnHealthTarget, result.HealthTarget, result.HealthBounds, result.Best?.Hp }));
            samples.Add(new { algorithm = order.ToString(), result.Evaluated, result.ElapsedMs, result.StoppedOnHealthTarget,
                best.StartingHp, best.Hp, best.MaxHp, best.HpChange, target.TargetHp, target.FullHealth, target.Basis, target.Uncertain,
                targetAnalyses = result.HealthBounds.TargetAnalyses, targetAnalysisMs = result.HealthBounds.TargetAnalysisMs,
                result.HealthBounds.UnknownReason, checkpoints = best.Actions.Length, verifications = result.Timing.Verifications,
                seeded = true, nativeRootAndStateRngHistoryMatched = true });
        }
        LocalWire.Write(Path.Combine(root, "integration-health-target-return-summary.json"), new
        {
            version = typeof(LocalWorker).Assembly.GetName().Version!.ToString(3), passed = true, probes, samples,
            scope = "Read-only callback dispatch and future-turn metadata probes plus seeded exact frozen native plus-one victory in both algorithms with ordinary independent replay. No unseeded speed or global optimality claim."
        });
    }
}
