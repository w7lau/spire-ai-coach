using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Relics;
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
            scope = "Read-only abstract-dispatch metadata probes plus seeded exact frozen native plus-one victory in both algorithms with ordinary independent replay. No unseeded speed or global optimality claim."
        });
    }
}
