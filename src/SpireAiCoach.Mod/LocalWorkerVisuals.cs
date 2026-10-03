using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace SpireAiCoach.Mod;

// Installed only after the owned worker executable and marker have been checked.
// Card/monster effects, hooks and action synchronization still run normally.
internal static class LocalWorkerVisuals
{
    private static readonly ConditionalWeakTable<Tween, object> PileTweens = new();
    private static MethodInfo _swooshCompletionSetter = null!;
    public static bool Active { get; set; }
    public static bool FastCardPresentation { get; set; }
    public static void Install(Harmony harmony)
    {
        HarmonyMethod Patch(string name) => new(typeof(LocalWorkerVisuals).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!);
        harmony.Patch(typeof(CreatureCmd).GetMethod(nameof(CreatureCmd.TriggerAnim))!, prefix: Patch(nameof(Animation)));
        harmony.Patch(typeof(CardPileCmd).GetMethod(nameof(CardPileCmd.GetTweenForCardsChangingPiles),
            [typeof(IEnumerable<CardPileAddResult>), typeof(bool)])!, postfix: Patch(nameof(TrackPileTween)));
        harmony.Patch(typeof(CardPileCmd).GetMethod("AppendPlayPileLerpTween", BindingFlags.NonPublic | BindingFlags.Static)!,
            postfix: Patch(nameof(TrackPlayPileTween)));
        harmony.Patch(typeof(TweenHelper).GetMethod(nameof(TweenHelper.AwaitFinished), [typeof(Tween), typeof(Node)])!,
            prefix: Patch(nameof(FinishPileTweenWithOwner)));
        harmony.Patch(typeof(TweenHelper).GetMethod(nameof(TweenHelper.AwaitFinished), [typeof(Tween), typeof(CancellationToken)])!,
            prefix: Patch(nameof(FinishPileTweenWithCancellation)));
        _swooshCompletionSetter = AccessTools.PropertySetter(typeof(NCardFlyVfx), nameof(NCardFlyVfx.SwooshAwayCompletion));
        harmony.Patch(typeof(NCardFlyVfx).GetMethod("PlayAnim", BindingFlags.NonPublic | BindingFlags.Instance)!,
            prefix: Patch(nameof(FinishCardFly)));
        harmony.Patch(typeof(NCardFlyShuffleVfx).GetMethod("PlayAnim", BindingFlags.NonPublic | BindingFlags.Instance)!,
            prefix: Patch(nameof(FinishShuffleFly)));
        harmony.Patch(typeof(NCardFlyPowerVfx).GetMethod(nameof(NCardFlyPowerVfx.PlayAnim))!,
            prefix: Patch(nameof(FinishPowerFly)));
        harmony.Patch(typeof(NCardFlyPowerVfx).GetMethod(nameof(NCardFlyPowerVfx.GetDuration))!,
            prefix: Patch(nameof(PowerFlyDuration)));
        harmony.Patch(typeof(NCardTrail).GetMethod(nameof(NCardTrail._Process))!, prefix: Patch(nameof(Trail)));
        harmony.Patch(AccessTools.Method(typeof(NDecimillipedeRocksVfx), "Play"),
            prefix: Patch(nameof(FinishAmbientRocks)));
        harmony.Patch(AccessTools.Method(typeof(NCard), "UpdateTypePlaqueSizeAndPosition"),
            prefix: Patch(nameof(TypePlaque)));
        harmony.Patch(typeof(NRewardsScreen).GetMethod("UpdateScreenState", BindingFlags.NonPublic | BindingFlags.Instance)!,
            prefix: Patch(nameof(UpdateCurrentRewardsScreen)));
    }

    public static void Reset()
    {
        Active = FastCardPresentation = false;
        PileTweens.Clear();
    }

    private static bool Animation(ref Task __result)
    {
        if (!Active) return true;
        __result = Task.CompletedTask;
        return false;
    }

    // This detached rock animation uses wall-clock delays and presentation RNG.
    // It can wake after the combat scene was replaced and dereference disposed
    // children. Do not start it in an owned worker; retain its node cleanup.
    private static bool FinishAmbientRocks(NDecimillipedeRocksVfx __instance, ref Task __result)
    {
        if (!Active) return true;
        __instance.QueueFreeSafely();
        __result = Task.CompletedTask;
        return false;
    }

    // Only sizes/positions the card type banner. Hidden accelerated scenes do
    // not need its deferred geometry, which can divide by a zero scale.
    private static bool TypePlaque() => !FastCardPresentation;

    // Keep the native pile command, its events, node cleanup and return value unchanged.
    // Only tweens returned by this exact presentation boundary can be completed early.
    private static void TrackPileTween((Tween?, bool) __result)
    {
        if (FastCardPresentation && __result.Item1 is { } tween) PileTweens.GetOrCreateValue(tween);
    }
    private static void TrackPlayPileTween(Tween? tween)
    {
        if (FastCardPresentation && tween != null) PileTweens.GetOrCreateValue(tween);
    }

    private static bool IsPileTween(Tween tween) => FastCardPresentation && PileTweens.TryGetValue(tween, out _);
    private static bool Complete(Tween tween)
    {
        // Bounded steps execute callbacks without an enormous synthetic delta. Unknown long/
        // looping tweens keep their original wait; never claim completion while they still run.
        for (int step = 0; step < 64 && tween.IsValid() && tween.IsRunning(); step++) tween.CustomStep(0.25);
        return !tween.IsValid() || !tween.IsRunning();
    }

    private static bool FinishPileTweenWithOwner(Tween tween, Node owner, ref Task<bool> __result)
    {
        if (!IsPileTween(tween)) return true;
        if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree()) return true;
        if (!Complete(tween)) return true;
        __result = Task.FromResult(GodotObject.IsInstanceValid(owner) && owner.IsInsideTree());
        return false;
    }

    private static bool FinishPileTweenWithCancellation(Tween tween, CancellationToken ct, ref Task __result)
    {
        if (!IsPileTween(tween)) return true;
        if (ct.IsCancellationRequested) { __result = Task.FromCanceled(ct); return false; }
        if (!Complete(tween)) return true;
        __result = Task.CompletedTask;
        return false;
    }

    // Native fly animations own completion/cleanup as well as visuals. Preserve those
    // responsibilities when omitting their frame-by-frame curve and trail sampling.
    private static bool FinishCardFly(NCardFlyVfx __instance, NCard ____card, bool ____isAddingToPile, NCardTrailVfx? ____vfx, ref Task __result)
    {
        if (!FastCardPresentation) return true;
        var model = ____card.Model ?? throw new InvalidOperationException("Missing card model in native fly animation");
        if (model.Pile is { } pile) SfxCmd.PlayCardSwooshSfx(pile);
        var completion = new TaskCompletionSource();
        _swooshCompletionSetter.Invoke(__instance, [completion]);
        if (____isAddingToPile) model.Pile?.InvokeCardAddFinished();
        completion.TrySetResult();
        RetireTrail(____vfx);
        ____card.QueueFreeSafely(); // Native TreeExited handler retires the fly/trail nodes.
        __result = Task.CompletedTask;
        return false;
    }

    private static bool FinishShuffleFly(NCardFlyShuffleVfx __instance, CardPile ____targetPile, NCardTrailVfx? ____vfx, ref Task __result)
    {
        if (!FastCardPresentation) return true;
        ____targetPile.InvokeCardAddFinished();
        // Native PlayAnim fades this separate sibling out before freeing its source.
        // Omitting the animation must also retire it; otherwise _Process dereferences
        // the disposed source forever, including while the worker is idle.
        RetireTrail(____vfx);
        __instance.QueueFreeSafely();
        __result = Task.CompletedTask;
        return false;
    }

    private static void RetireTrail(NCardTrailVfx? trail)
    {
        if (trail == null || !GodotObject.IsInstanceValid(trail)) return;
        trail.SetProcess(false);
        trail.QueueFreeSafely(); // Native _ExitTree kills its own Tween.
    }

    // Native power fly animation is presentation and node ownership only. Its curve
    // sampling can overshoot the baked path at accelerated frame deltas. Keep the
    // same card/trail/fly cleanup without starting that detached animation task.
    private static bool FinishPowerFly(NCardFlyPowerVfx __instance, NCardTrailVfx? ____vfx, ref Task __result)
    {
        if (!FastCardPresentation) return true;
        SfxCmd.Play("event:/sfx/ui/cards/card_movement_B_power");
        RetireTrail(____vfx);
        __instance.CardNode.QueueFreeSafely();
        __instance.QueueFreeSafely();
        __result = Task.CompletedTask;
        return false;
    }
    private static bool PowerFlyDuration(ref float __result)
    {
        if (!FastCardPresentation) return true;
        __result = 0;
        return false;
    }

    // A deferred UI refresh can outlive the restored run that created it. Retire only
    // that stale callback; reward generation, synchronization, completion and healing
    // still execute for the current run. This patch exists only in owned workers.
    private static bool UpdateCurrentRewardsScreen(NRewardsScreen __instance, IRunState ____runState)
    {
        var manager = RunManager.Instance;
        return GodotObject.IsInstanceValid(__instance) && !__instance.IsQueuedForDeletion() && __instance.IsInsideTree() &&
            !manager.IsCleaningUp && ReferenceEquals(____runState, manager.DebugOnlyGetState()) && manager.RewardsSetSynchronizer != null;
    }

    // Pure Line2D geometry; no model, history, RNG or completion side effects.
    private static bool Trail() => !FastCardPresentation;
}
