using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Vfx;

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
        harmony.Patch(typeof(NCardTrail).GetMethod(nameof(NCardTrail._Process))!, prefix: Patch(nameof(Trail)));
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
    private static bool FinishCardFly(NCardFlyVfx __instance, NCard ____card, bool ____isAddingToPile, ref Task __result)
    {
        if (!FastCardPresentation) return true;
        var model = ____card.Model ?? throw new InvalidOperationException("Missing card model in native fly animation");
        if (model.Pile is { } pile) SfxCmd.PlayCardSwooshSfx(pile);
        var completion = new TaskCompletionSource();
        _swooshCompletionSetter.Invoke(__instance, [completion]);
        if (____isAddingToPile) model.Pile?.InvokeCardAddFinished();
        completion.TrySetResult();
        ____card.QueueFreeSafely(); // Native TreeExited handler retires the fly/trail nodes.
        __result = Task.CompletedTask;
        return false;
    }

    private static bool FinishShuffleFly(NCardFlyShuffleVfx __instance, CardPile ____targetPile, ref Task __result)
    {
        if (!FastCardPresentation) return true;
        ____targetPile.InvokeCardAddFinished();
        __instance.QueueFreeSafely();
        __result = Task.CompletedTask;
        return false;
    }

    // Pure Line2D geometry; no model, history, RNG or completion side effects.
    private static bool Trail() => !FastCardPresentation;
}
