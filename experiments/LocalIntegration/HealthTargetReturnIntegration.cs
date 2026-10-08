using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Cards;
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
    private abstract class PileSource : RelicModel
    {
        public override RelicRarity Rarity => RelicRarity.Common;
        protected static async Task TransformCard(CardModel card)
        { await Task.Yield(); await CardCmd.Transform(card, card, default); }
    }
    private sealed class ExternalAsyncCombatTransform : PileSource
    {
        public async Task Entry(PlayerChoiceContext context, Player player)
        {
            var cards = (await CardSelectCmd.FromHand(context, player, default, null, this)).ToList();
            for (int i = 0; i < cards.Count; i++) await TransformCard(cards[i]);
        }
    }
    private sealed class ExternalAsyncUnknownTransform : PileSource
    { public async Task Entry(CardModel card) { await TransformCard(card); } }
    private sealed class CombatPileSource : PileSource
    { public void Entry(CardModel card) { _ = CardPileCmd.Add(card, PileType.Hand, CardPilePosition.Top, this, false); } }
    private sealed class DeckPileSource : PileSource
    { public void Entry(CardModel card) { _ = CardPileCmd.Add(card, PileType.Deck, CardPilePosition.Top, this, false); } }
    private sealed class UnknownPileSource : PileSource
    { public void Entry(CardModel card, PileType pile) { _ = CardPileCmd.Add(card, pile, CardPilePosition.Top, this, false); } }
    private sealed class DeckLocationSource : PileSource
    { public CardLocation Entry() => new(Owner, PileType.Deck, CardPilePosition.Top); }
    private sealed class UnknownLocationSource : PileSource
    {
        private readonly CardLocation _location;
        public UnknownLocationSource() { _location = default; }
        public CardLocation ModifyCardPlayResultLocationFixture(CardLocation original) => _location;
    }
    private sealed class ExternalPileRecovery : PileSource
    {
        public override Task AfterCardChangedPiles(CardModel card, PileType oldPile, AbstractModel? clonedBy) =>
            card.Pile?.Type == PileType.Deck ? CreatureCmd.Heal(Owner.Creature, 1, true) : Task.CompletedTask;
    }

    private static void MetadataPrefix() { }

    public static async Task Run(string root, LocalWorkerPool pool, LocalSearchRequest frozen, LocalInstallation installation, string seedPath)
    {
        var seed = LocalWire.Read<LocalSearchResult>(seedPath).Best ?? throw new InvalidOperationException("Frozen native win missing");
        if (!LocalSearchPolicy.WinningRouteFrom(seed, frozen) || seed.HpChange is not (0 or 1) || seed.Hp >= seed.MaxHp)
            throw new InvalidOperationException("This native fixture requires a same-root, non-full, zero-loss or plus-one victory");
        var estimatorType = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalRecoveryEstimator", true)!;
        var metadata = Activator.CreateInstance(estimatorType, frozen)!;
        var describe = estimatorType.GetMethod("DescribeContent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var readContent = estimatorType.GetMethod("ReadContent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var probes = new List<object>();
        // Exceed the real method traversal budget without executing effects.
        // Unknown tail code must not manufacture healing; a known earlier HP
        // write must remain visible even when a later helper chain is truncated.
        foreach (bool recoveryBeforeLimit in new[] { false, true })
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OwnedRecoveryBudget" + recoveryBeforeLimit), AssemblyBuilderAccess.Run);
            var builder = assembly.DefineDynamicModule("Probe").DefineType("BudgetSource", TypeAttributes.Public | TypeAttributes.Abstract, typeof(RelicModel));
            var helpers = Enumerable.Range(0, 514).Select(i => builder.DefineMethod(i == 0 ? "Entry" : "Helper" + i,
                i == 0 ? MethodAttributes.Public : MethodAttributes.Private | MethodAttributes.Static,
                typeof(void), [typeof(Creature)])).ToArray();
            for (int i = 0; i < helpers.Length; i++)
            {
                var il = helpers[i].GetILGenerator();
                if (i == 0 && recoveryBeforeLimit)
                {
                    il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Callvirt, typeof(Creature).GetMethod(nameof(Creature.SetCurrentHpInternal))!);
                }
                if (i + 1 < helpers.Length) { il.Emit(i == 0 ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0); il.Emit(OpCodes.Call, helpers[i + 1]); }
                il.Emit(OpCodes.Ret);
            }
            var type = builder.CreateType()!;
            var instance = Activator.CreateInstance(estimatorType, frozen)!;
            var effect = readContent.Invoke(instance, [type, true])!;
            bool active = (bool)effect.GetType().GetProperty("ActiveRecovery")!.GetValue(effect)!;
            bool uncertain = (bool)effect.GetType().GetProperty("Uncertain")!.GetValue(effect)!;
            if (active != recoveryBeforeLimit || !uncertain)
                throw new InvalidOperationException("Method traversal limit changed known healing or invented an absent HP write: " +
                    System.Text.Json.JsonSerializer.Serialize(new { recoveryBeforeLimit, active, uncertain,
                        unknownAt = effect.GetType().GetProperty("UncertainAt")!.GetValue(effect) }));
            probes.Add(new { kind = "BoundedMethodClosure", recoveryBeforeLimit, activeRecovery = active, uncertain, callbackExecuted = false });
        }
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
        var periapt = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Relics.DarkstonePeriapt", true)!;
        foreach (bool combatOnly in new[] { false, true })
        {
            var instance = Activator.CreateInstance(estimatorType, frozen)!;
            estimatorType.GetField("_combatPileEventsOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, combatOnly);
            var effect = readContent.Invoke(instance, [periapt, true])!;
            bool active = (bool)effect.GetType().GetProperty("ActiveRecovery")!.GetValue(effect)!;
            if (active == combatOnly) throw new InvalidOperationException("Native permanent-deck condition ignored its producer domain");
            probes.Add(new { kind = periapt.Name, combatOnly, activeRecovery = active, callbackExecuted = false });
        }
        foreach (var type in new[] { typeof(CombatPileSource), typeof(DeckPileSource), typeof(UnknownPileSource), typeof(DeckLocationSource), typeof(UnknownLocationSource) })
        {
            var instance = Activator.CreateInstance(estimatorType, frozen)!;
            var effect = describe.Invoke(instance, [type])!;
            bool changesDeck = (bool)effect.GetType().GetProperty("MayChangeDeck")!.GetValue(effect)!;
            if (changesDeck != (type != typeof(CombatPileSource)))
                throw new InvalidOperationException("Permanent-deck event producer was classified incorrectly: " + type.Name);
            probes.Add(new { kind = type.Name, changesDeck, callbackExecuted = false });
        }
        foreach (string name in new[] { "Begone", "Charge", "Compact", "Guards", "Seance" })
        {
            var type = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Cards." + name, true)!;
            var instance = Activator.CreateInstance(estimatorType, frozen)!;
            var effect = describe.Invoke(instance, [type])!;
            if ((bool)effect.GetType().GetProperty("MayChangeDeck")!.GetValue(effect)!)
                throw new InvalidOperationException("Native combat-card transformation was mistaken for a permanent-deck producer: " + name);
            probes.Add(new { kind = name, changesDeck = false, callbackExecuted = false });
        }
        var helperMetadata = Activator.CreateInstance(estimatorType, frozen)!;
        foreach (var type in new[] { typeof(ExternalAsyncCombatTransform), typeof(ExternalAsyncUnknownTransform) })
        {
            var effect = describe.Invoke(helperMetadata, [type])!;
            bool changesDeck = (bool)effect.GetType().GetProperty("MayChangeDeck")!.GetValue(effect)!;
            if (changesDeck != (type == typeof(ExternalAsyncUnknownTransform)))
                throw new InvalidOperationException("Async helper lost or borrowed its caller's card domain: " + type.Name);
            probes.Add(new { kind = type.Name, changesDeck, callbackExecuted = false });
        }
        var externalPile = Activator.CreateInstance(estimatorType, frozen)!;
        estimatorType.GetField("_combatPileEventsOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(externalPile, true);
        var externalPileEffect = readContent.Invoke(externalPile, [typeof(ExternalPileRecovery), true])!;
        if (!(bool)externalPileEffect.GetType().GetProperty("ActiveRecovery")!.GetValue(externalPileEffect)!)
            throw new InvalidOperationException("External pile callback was assigned a native event proof");
        probes.Add(new { kind = nameof(ExternalPileRecovery), activeRecovery = true, callbackExecuted = false });
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
        var planisphere = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Relics.Planisphere", true)!;
        var phaseInstance = Activator.CreateInstance(estimatorType, frozen)!;
        var allPhases = describe.Invoke(phaseInstance, [planisphere])!;
        var futurePhases = readContent.Invoke(phaseInstance, [planisphere, true])!;
        if (!(bool)allPhases.GetType().GetProperty("ActiveRecovery")!.GetValue(allPhases)! ||
            (bool)futurePhases.GetType().GetProperty("ActiveRecovery")!.GetValue(futurePhases)!)
            throw new InvalidOperationException("Settled native room-entry healing still changes the combat return goal");
        probes.Add(new { kind = "NativeRoomPhase", roomRecoveryPresent = true, futureRecovery = false, strictClosureRetained = true, callbackExecuted = false });

        // An external override stays uncertain rather than receiving the native
        // room phase rule. The generated callback is metadata only, never called.
        var roomHook = typeof(AbstractModel).GetMethod("AfterRoomEntered")!;
        var externalAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OwnedExternalRoomRecovery"), AssemblyBuilderAccess.Run);
        var externalBuilder = externalAssembly.DefineDynamicModule("Probe").DefineType("ExternalRoomRecovery", TypeAttributes.Public | TypeAttributes.Abstract, typeof(RelicModel));
        var externalMethod = externalBuilder.DefineMethod(roomHook.Name, MethodAttributes.Public | MethodAttributes.Virtual,
            roomHook.ReturnType, roomHook.GetParameters().Select(p => p.ParameterType).ToArray());
        var roomIl = externalMethod.GetILGenerator();
        roomIl.Emit(OpCodes.Ldnull); roomIl.Emit(OpCodes.Ldc_I4_1);
        roomIl.Emit(OpCodes.Call, typeof(decimal).GetMethod("op_Implicit", [typeof(int)])!);
        roomIl.Emit(OpCodes.Ldc_I4_1);
        roomIl.Emit(OpCodes.Call, typeof(CreatureCmd).GetMethods().Single(m => m.Name == "Heal" && m.GetParameters().Length == 3));
        roomIl.Emit(OpCodes.Ret); externalBuilder.DefineMethodOverride(externalMethod, roomHook);
        var externalRoom = readContent.Invoke(Activator.CreateInstance(estimatorType, frozen)!, [externalBuilder.CreateType()!, true])!;
        if (!(bool)externalRoom.GetType().GetProperty("ActiveRecovery")!.GetValue(externalRoom)!)
            throw new InvalidOperationException("An external room callback was excluded using native phase semantics");
        probes.Add(new { kind = "ExternalRoomPhase", futureRecovery = true, callbackExecuted = false });

        var fairy = typeof(AbstractModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Potions.FairyInABottle", true)!;
        var revivalType = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalRevivalEstimate", true)!;
        var readRevival = revivalType.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
        var revival = readRevival.Invoke(null, [fairy]) ?? throw new InvalidOperationException("The native automatic death-recovery formula was not recognized");
        var maximum = revivalType.GetMethod("MaximumHp")!;
        var improves = revivalType.GetMethod("CanImprove", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if ((decimal)maximum.Invoke(revival, [75m])! != 23m ||
            (bool)improves.Invoke(revival, [74m, 75m, 0m, false])! ||
            !(bool)improves.Invoke(revival, [10m, 75m, 0m, false])! ||
            !(bool)improves.Invoke(revival, [74m, 75m, 0m, true])! ||
            !(bool)improves.Invoke(revival, [74m, 75m, 52m, false])!)
            throw new InvalidOperationException("Revival goal ignored starting HP, later recovery or dynamic max HP");
        var passiveEffect = readContent.Invoke(Activator.CreateInstance(estimatorType, frozen with { IncludePotions = false })!, [fairy, true])!;
        if (!(bool)passiveEffect.GetType().GetProperty("ActiveRecovery")!.GetValue(passiveEffect)!)
            throw new InvalidOperationException("Disabling manual potions removed passive death recovery");
        var harmony = new Harmony("SpireLocalIntegration.owned-revival-metadata");
        var onUse = fairy.GetMethod("OnUse", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        try
        {
            harmony.Patch(onUse, prefix: new(typeof(HealthTargetReturnIntegration).GetMethod(nameof(MetadataPrefix), BindingFlags.Static | BindingFlags.NonPublic)!));
            if (readRevival.Invoke(null, [fairy]) != null) throw new InvalidOperationException("Patched recovery received the original native revival cap");
        }
        finally { harmony.Unpatch(onUse, HarmonyPatchType.Prefix, harmony.Id); }
        if (readRevival.Invoke(null, [fairy]) == null) throw new InvalidOperationException("Native recovery metadata was not restored");
        probes.Add(new { kind = "NativeAutomaticRevival", maxHp = 75, conservativeRevivalCap = 23,
            cannotImprove74 = true, canImprove10 = true, dynamicMaxHpRetained = true,
            laterHealingRetained = true, passiveWithoutManualPotions = true, foreignPatchRetained = true, callbackExecuted = false });
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
                throw new InvalidOperationException("Frozen victory did not return at its original content goal: " +
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
            scope = "Read-only callback, turn and pile-event metadata probes plus seeded exact frozen native zero-loss/plus-one victory in both algorithms with ordinary independent replay. No unseeded speed or global optimality claim."
        });
    }
}
