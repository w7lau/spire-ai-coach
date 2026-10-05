using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Installed only after worker ownership is verified. The original command / hook
// runs to completion. No preview executions, extra permission hooks, or RNG draws.
internal static class LocalDiscardObservation
{
    private sealed record Target(LocalDiscardLearning Learning, Player Player);
    private sealed record Snapshot(decimal Energy, int Stars, int Hp, int Block, int EnemyHp,
        HashSet<CardModel> Hand, int Buffs, int BuffAmount);
    private sealed record Pending(Target Target, string Card, bool Auto, Snapshot Before);
    private static Target? _current;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        var harmony = new Harmony("SpireAiCoach.owned-worker.discard-observation");
        try
        {
            var discarded = typeof(Hook).GetMethods().Single(m => m.Name == "AfterCardDiscarded");
            var auto = typeof(CardCmd).GetMethods().Single(m => m.Name == nameof(CardCmd.AutoPlay));
            harmony.Patch(discarded, prefix: new(AccessTools.Method(typeof(LocalDiscardObservation), nameof(BeforeDiscard))),
                postfix: new(AccessTools.Method(typeof(LocalDiscardObservation), nameof(After))));
            harmony.Patch(auto, prefix: new(AccessTools.Method(typeof(LocalDiscardObservation), nameof(BeforeAuto))),
                postfix: new(AccessTools.Method(typeof(LocalDiscardObservation), nameof(After))));
            _installed = true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id);
            Godot.GD.Print("[SpireAiCoach] Discard observation unavailable; retaining native choices: " + ex.Message);
        }
    }
    internal static IDisposable Use(LocalDiscardLearning? learning, Player player)
    {
        var prior = _current;
        _current = learning == null ? null : new(learning, player);
        return new Release(() => _current = prior);
    }
    private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
    private static Snapshot Capture(Player player)
    {
        var pcs = player.PlayerCombatState!;
        var powers = player.Creature.Powers.Where(p => p.TypeForCurrentAmount == PowerType.Buff).ToArray();
        return new(pcs.Energy, pcs.Stars, player.Creature.CurrentHp, player.Creature.Block,
            player.Creature.CombatState!.Enemies.Sum(e => Math.Max(0, e.CurrentHp)),
            new(pcs.Hand.Cards, ReferenceEqualityComparer.Instance), powers.Length, powers.Sum(p => Math.Max(0, p.Amount)));
    }
    private static Pending? Begin(CardModel card, bool auto)
    {
        if (_current is not { } target || !ReferenceEquals(card.Owner, target.Player)) return null;
        try
        {
            using var measured = LocalWorker.MeasureMethod("LocalDiscardObservation.Capture");
            return new(target, LocalDiscardLearning.Key(card), auto, Capture(target.Player));
        }
        catch { return null; }
    }
    private static void BeforeDiscard(CardModel card, out Pending? __state) => __state = Begin(card, false);
    private static void BeforeAuto(CardModel card, AutoPlayType type, out Pending? __state) =>
        __state = type == AutoPlayType.SlyDiscard ? Begin(card, true) : null;
    private static void After(ref Task __result, Pending? __state)
    {
        if (__state != null) __result = ObserveCompletion(__result, __state);
    }
    private static async Task ObserveCompletion(Task native, Pending pending)
    {
        await native; // Preserve every native exception and completion boundary.
        try
        {
            using var measured = LocalWorker.MeasureMethod("LocalDiscardObservation.Capture");
            var after = Capture(pending.Target.Player); var before = pending.Before;
            pending.Target.Learning.Observe(pending.Card, pending.Auto, new(
                Damage: Math.Max(0, before.EnemyHp - after.EnemyHp), Block: Math.Max(0, after.Block - before.Block),
                Energy: (double)(after.Energy - before.Energy), Stars: after.Stars - before.Stars,
                HandGain: after.Hand.Count(c => !before.Hand.Contains(c)),
                Healing: Math.Max(0, after.Hp - before.Hp), HpCost: Math.Max(0, before.Hp - after.Hp),
                Setup: Math.Min(40, Math.Max(0, after.Buffs - before.Buffs) * 20) +
                    Math.Min(40, Math.Sqrt(Math.Max(0, after.BuffAmount - before.BuffAmount)) * 8)));
        }
        catch { /* Unknown observed data changes ordering only, never native outcomes. */ }
    }
}
