using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.ValueProps;
using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Observe real native effect completion and Damage results, including the final
// hit that never reaches combat history after victory. No rule is repeated or changed. Installed lazily only
// by owned worker/integration accounting; the player's overlay never creates one.
internal sealed class LocalCardGoalAccounting : IDisposable
{
    private static LocalCardGoalAccounting? _current;
    private static bool _installed;
    private readonly CombatHistory _history = CombatManager.Instance.History;
    private readonly Player _player;
    private readonly LocalCardGoals? _goals;
    private int _plays, _kills, _stepPlays, _stepKills;
    private bool _won;
    private readonly List<LocalCardGoalStep> _steps = [];
    private readonly HashSet<CardModel>? _playCopies, _finisherCopies;
    private readonly HashSet<CardModel>? _playGoalCopies, _finisherGoalCopies;
    private readonly int _eligibleLivingEnemies;
    private readonly HashSet<CardModel> _playedCopies = new(ReferenceEqualityComparer.Instance),
        _killingCopies = new(ReferenceEqualityComparer.Instance), _exhaustedCopies = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Creature Receiver, CardModel Source)> _pendingKills = [];

    public LocalCardGoalAccounting(Player player, LocalCardGoals? goals)
    {
        _player = player; _goals = goals;
        _eligibleLivingEnemies = string.IsNullOrEmpty(goals?.FinisherModelId) ? 0 :
            player.Creature.CombatState?.Enemies.Count(e => e.IsAlive && LocalFinisherEligibility.AllowsFatal(e)) ?? 0;
        _playCopies = CurrentConsumableCopies(goals?.PlayModelId);
        _finisherCopies = CurrentConsumableCopies(goals?.FinisherModelId);
        _playGoalCopies = CurrentGoalCopies(goals?.PlayModelId);
        _finisherGoalCopies = CurrentGoalCopies(goals?.FinisherModelId);
        if (goals?.Enabled == true)
        {
            if (_current != null) throw new InvalidOperationException("Native goal accounting already active");
            Install(); _current = this;
            CombatManager.Instance.CombatWon += Won;
        }
    }

    private static void Install()
    {
        if (_installed) return;
        var harmony = new Harmony("SpireAiCoach.owned-goal-accounting");
        try
        {
            var play = AccessTools.Method(typeof(CardModel), nameof(CardModel.OnPlayWrapper));
            var machine = play.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
            harmony.Patch(AccessTools.Method(machine, "MoveNext"),
                transpiler: new(AccessTools.Method(typeof(LocalCardGoalAccounting), nameof(ObservePlays))));
            harmony.Patch(AccessTools.Method(typeof(CombatHistory), nameof(CombatHistory.CardExhausted)),
                postfix: new(AccessTools.Method(typeof(LocalCardGoalAccounting), nameof(Exhausted))));
            harmony.Patch(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
                [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp),
                    typeof(Creature), typeof(CardModel), typeof(CardPlay)]),
                prefix: new(AccessTools.Method(typeof(LocalCardGoalAccounting), nameof(BeforeDamage))),
                postfix: new(AccessTools.Method(typeof(LocalCardGoalAccounting), nameof(Damaged))));
            _installed = true;
        }
        catch { harmony.UnpatchAll(harmony.Id); throw; }
    }

    private void Won(object _) => _won = true;
    private static IEnumerable<CodeInstruction> ObservePlays(IEnumerable<CodeInstruction> instructions)
    {
        var native = AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.InvokeExecutionFinished));
        var observer = AccessTools.Method(typeof(LocalCardGoalAccounting), nameof(Played));
        var output = new List<CodeInstruction>(); int sites = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(native))
            {
                // Keep the receiver once for our read-only callback; preserve
                // the native call, branch destinations and exception regions.
                output.Add(new CodeInstruction(OpCodes.Dup).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction));
                output.Add(instruction); output.Add(new(OpCodes.Call, observer)); sites++;
            }
            else output.Add(instruction);
        }
        if (sites != 3) throw new InvalidOperationException("Native card play completion layout changed");
        return output;
    }
    private static void Played(AbstractModel __instance)
    {
        if (_current is not { } ledger || __instance is not CardModel card ||
            string.IsNullOrEmpty(ledger._goals!.PlayModelId) ||
            !ReferenceEquals(card.Owner, ledger._player) || card.Id.ToString() != ledger._goals!.PlayModelId) return;
        ledger._plays++; ledger._playedCopies.Add(card);
    }
    private static void Exhausted(CombatHistory __instance, CardModel card)
    {
        if (_current is { } ledger && ReferenceEquals(ledger._history, __instance)) ledger._exhaustedCopies.Add(card);
    }
    private static void BeforeDamage(IEnumerable<Creature> __1, CardModel? cardSource,
        out HashSet<Creature>? __state)
    {
        __state = null;
        if (_current is not { } ledger || cardSource == null || !ReferenceEquals(cardSource.Owner, ledger._player) ||
            cardSource.Id.ToString() != ledger._goals!.FinisherModelId) return;
        // Death can remove a Mod power denying Fatal rewards. Observe its rule
        // before damage, not after cleanup. This never changes native effects.
        __state = new(__1.Where(target => target.IsAlive && LocalFinisherEligibility.AllowsFatal(target)),
            ReferenceEqualityComparer.Instance);
    }
    private static void Damaged(CardModel? cardSource, HashSet<Creature>? __state,
        ref Task<IEnumerable<DamageResult>> __result)
    {
        if (_current is not { } ledger || cardSource == null || __state == null || __state.Count == 0) return;
        if (__result.IsCompletedSuccessfully) ledger.ObserveDamage(__result.Result, cardSource, __state);
        else __result = ledger.AfterDamage(__result, cardSource, __state);
    }
    private async Task<IEnumerable<DamageResult>> AfterDamage(Task<IEnumerable<DamageResult>> native, CardModel source,
        HashSet<Creature> eligible)
    {
        var results = await native;
        if (ReferenceEquals(_current, this)) ObserveDamage(results, source, eligible);
        return results;
    }
    private void ObserveDamage(IEnumerable<DamageResult> results, CardModel source, HashSet<Creature> eligible)
    {
        foreach (var result in results)
            if (result.WasTargetKilled && eligible.Contains(result.Receiver)) _pendingKills.Add((result.Receiver, source));
    }

    public void CompleteStep()
    {
        if (_goals?.Enabled != true) return;
        // Damage estimates and pre-hook lethal flags cannot supply credit. Wait
        // for this action's native effects/death prevention/revival to settle.
        foreach (var kill in _pendingKills.Where(k => k.Receiver.IsDead))
        { _kills++; _killingCopies.Add(kill.Source); }
        _pendingKills.Clear();
        _steps.Add(new(_plays - _stepPlays, _kills - _stepKills));
        _stepPlays = _plays; _stepKills = _kills;
    }

    public LocalCardGoalOutcome? Snapshot() => _goals?.Enabled == true ?
        new(_goals.PlayModelId, _goals.FinisherModelId, _plays, _kills, _steps.ToArray(),
            new(Progress(_playCopies, _playedCopies), Progress(_finisherCopies, _killingCopies, _eligibleLivingEnemies))) : null;

    public LocalGoalOpportunity? Opportunity()
    {
        if (_goals?.Enabled != true) return null;
        bool terminal = _won || _player.Creature.IsDead;
        if (_player.PlayerCombatState == null && !terminal) return null;
        // Current membership matters: exhausted cards may be recovered. Historical
        // exhaust events alone must not make a returned copy permanently unavailable.
        var available = new HashSet<CardModel>(terminal ? [] : _player.PlayerCombatState!.AllPiles
            .Where(p => p.Type != PileType.Exhaust).SelectMany(p => p.Cards), ReferenceEqualityComparer.Instance);
        int living = terminal ? 0 : _player.Creature.CombatState?.Enemies.Count(e => e.IsAlive && LocalFinisherEligibility.AllowsFatal(e)) ?? 0;
        LocalGoalStock? Stock(HashSet<CardModel>? copies, HashSet<CardModel> completed, string? model, int? enemies = null) =>
            copies == null ? null : new(enemies.HasValue ? Math.Min(copies.Count, enemies.Value) : copies.Count,
                copies.Count(completed.Contains), Math.Min(enemies.HasValue ? living : int.MaxValue,
                    copies.Count(c => !completed.Contains(c) && available.Contains(c) && c.Id.ToString() == model)));
        return new(_goals.PlayModelId, _goals.FinisherModelId,
            Stock(_playGoalCopies, _playedCopies, _goals.PlayModelId),
            Stock(_finisherGoalCopies, _killingCopies, _goals.FinisherModelId, _eligibleLivingEnemies));
    }

    private HashSet<CardModel>? CurrentGoalCopies(string? model) => string.IsNullOrEmpty(model) || _player.PlayerCombatState == null ? null :
        new(_player.PlayerCombatState!.AllPiles.Where(p => p.Type != PileType.Exhaust).SelectMany(p => p.Cards)
            .Where(c => c.Id.ToString() == model), ReferenceEqualityComparer.Instance);

    internal static int ExhaustSelectionPenalty(CardModel card) => _current is { } ledger &&
        (ledger._playGoalCopies?.Contains(card) == true && !ledger._playedCopies.Contains(card) &&
             ledger._playGoalCopies.Count(ledger._playedCopies.Contains) < ledger._playGoalCopies.Count ||
         ledger._finisherGoalCopies?.Contains(card) == true && !ledger._killingCopies.Contains(card) &&
             ledger._finisherGoalCopies.Count(ledger._killingCopies.Contains) < Math.Min(ledger._finisherGoalCopies.Count, ledger._eligibleLivingEnemies)) ? 180 : 0;

    private HashSet<CardModel>? CurrentConsumableCopies(string? model)
    {
        if (string.IsNullOrEmpty(model) || _player.PlayerCombatState == null) return null;
        var copies = _player.PlayerCombatState.AllPiles.Where(p => p.Type != PileType.Exhaust)
            .SelectMany(p => p.Cards).Where(c => c.Id.ToString() == model).Distinct<CardModel>(ReferenceEqualityComparer.Instance).ToArray();
        return copies.Length > 0 && copies.All(c => c.Keywords.Contains(CardKeyword.Exhaust)) ?
            new(copies, ReferenceEqualityComparer.Instance) : null;
    }

    private LocalConsumableGoalProgress? Progress(HashSet<CardModel>? copies, HashSet<CardModel> completed, int? enemies = null) =>
        copies == null ? null : new(copies.Count, copies.Count(c => completed.Contains(c) &&
            (_won && !_player.Creature.IsDead || _exhaustedCopies.Contains(c))),
            copies.Count(_exhaustedCopies.Contains), enemies, BattleEnded: _won && !_player.Creature.IsDead);

    public bool Matches(LocalCardGoalOutcome? expected) => expected == null ? _goals?.Enabled != true :
        Snapshot() is { } actual && actual.PlayModelId == expected.PlayModelId &&
        actual.FinisherModelId == expected.FinisherModelId && actual.Plays == expected.Plays &&
        actual.Kills == expected.Kills && actual.Steps.SequenceEqual(expected.Steps) &&
        actual.ConsumableGoals == expected.ConsumableGoals;

    public void Dispose()
    {
        if (_goals?.Enabled == true)
        {
            CombatManager.Instance.CombatWon -= Won;
            if (ReferenceEquals(_current, this)) _current = null;
        }
    }
}
