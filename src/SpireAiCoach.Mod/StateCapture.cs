using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Called only on Godot's main thread. No game actions, RNG calls or state-machine evaluation.
public sealed class StateCapture : IDisposable
{
    private long _revision;
    public StateCapture() => CombatManager.Instance.StateTracker.CombatStateChanged += Changed;
    private void Changed(CombatState _) => _revision++;
    public void Dispose() => CombatManager.Instance.StateTracker.CombatStateChanged -= Changed;
    private ConditionalWeakTable<object, Identity> _ids = new();
    private object? _combat;
    private string _combatId = "";
    private int _sequence;
    private readonly SortedSet<string> _warnings = new(StringComparer.Ordinal);
    private sealed record Identity(string Value);
    private string Id(object value, string prefix) => _ids.GetValue(value, _ => new(prefix + ++_sequence)).Value;

    public CombatSnapshot? Capture(bool revealOrder)
    {
        var manager = CombatManager.Instance;
        var state = manager.DebugOnlyGetState();
        if (state == null || !manager.IsInProgress || manager.IsOverOrEnding) return null;
        var me = LocalContext.GetMe(state);
        if (me?.PlayerCombatState == null) return null;
        if (!ReferenceEquals(_combat, state))
        {
            _combat = state;
            _combatId = Guid.NewGuid().ToString("N");
            _ids = new();
            _sequence = 0;
        }
        _warnings.Clear();
        _warnings.Add("仅依据当前描述与可读状态推断；隐藏的 Mod 逻辑未经过模拟验证。");
        _warnings.Add("后续敌人节点仅为静态连接，不代表分支结果；未调用游戏随机数或推进状态机。");
        var pcs = me.PlayerCombatState;
        var targets = state.Enemies.Concat(state.PlayerCreatures).Concat(pcs.Pets).Distinct().ToArray();
        foreach (var target in targets) _ = Id(target, "unit-");

        // Assign instance IDs independently of the actual draw order, including when reveal is off.
        foreach (var card in pcs.AllCards.OrderBy(c => c.Id.ToString(), StringComparer.Ordinal)) _ = Id(card, "card-");
        var hand = Pile(pcs.Hand, targets);
        var draw = Pile(pcs.DrawPile, targets);
        var drawOrder = revealOrder ? draw.Cards.Select(c => c.InstanceId).ToArray() : [];
        // Unordered inventory is sorted even when a separate explicit order is enabled.
        draw = draw with { Cards = draw.Cards.OrderBy(c => c.ModelId, StringComparer.Ordinal)
            .ThenBy(c => c.InstanceId, StringComparer.Ordinal).ToArray() };
        var discard = Pile(pcs.DiscardPile, targets);
        var exhaust = Pile(pcs.ExhaustPile, targets);
        var play = Pile(pcs.PlayPile, targets);
        var enemies = state.Enemies.Select(c => Creature(c, me.Creature)).ToArray();
        var allies = state.PlayerCreatures.Where(c => c != me.Creature).Select(c => Creature(c, me.Creature)).ToArray();
        if (state.Players.Count > 1) _warnings.Add("联机快照只包含本人的手牌和牌堆；队友同时操作会使建议过期。未读取队友私有手牌。");
        var player = new PlayerInfo(Id(me.Creature, "unit-"), me.Character.Title.GetFormattedText(),
            me.Creature.CurrentHp, me.Creature.MaxHp, me.Creature.Block, pcs.Energy, pcs.MaxEnergy,
            pcs.Stars, Powers(me.Creature),
            me.Relics.Select(r => new EffectInfo(r.Id.ToString(), Source(r), r.Title.GetFormattedText(),
                Read(() => Clean(r.DynamicDescription.GetFormattedText()), r.Id + ".description"),
                r.DisplayAmount, Variables(r.DynamicVars), r.IsUsedUp, r.StackCount)).ToArray(),
            me.PotionSlots.Where(p => p != null).Select(p => new PotionInfo(Id(p!, "potion-"),
                p!.Title.GetFormattedText(), Read(() => Clean(p.DynamicDescription.GetFormattedText()), p.Id + ".description"),
                p.TargetType.ToString(), Usable(p),
                targets.Where(t => p.IsValidTarget(t) && Usable(p))
                    .Select(t => Id(t, "unit-")).ToArray())).ToArray(),
            pcs.OrbQueue.Orbs.Select(o => $"{o.Title.GetFormattedText()}：被动 {o.PassiveVal}，激发 {o.EvokeVal}").ToArray(),
            pcs.Pets.Select(c => Creature(c, me.Creature)).ToArray());
        var canAdvise = pcs.Phase == PlayerTurnPhase.Play && !manager.PlayerActionsDisabled &&
            !manager.IsPlayerReadyToEndTurn(me) && pcs.PlayPile.IsEmpty && me.Creature.IsAlive;
        return new(_combatId, state.RoundNumber, canAdvise, $"{pcs.Phase} / 本人第 {pcs.TurnNumber} 回合",
            state.RunState.TotalFloor, state.RunState.AscensionLevel, player, hand, draw, discard,
            exhaust, play, enemies, allies, revealOrder, drawOrder, _warnings.ToArray(), _revision);
    }

    private PileInfo Pile(CardPile pile, Creature[] targets) => new(pile.Cards.Count,
        pile.Cards.Select(card => new CardInfo(Id(card, "card-"), card.Id.ToString(), Source(card),
            card.Title, Read(() => Clean(card.GetDescriptionForPile(pile.Type)), card.Id + ".description"),
            Read<int?>(() => card.EnergyCost.GetAmountToSpend(), card.Id + ".cost"), card.EnergyCost.CostsX,
            card.GetStarCostWithModifiers(), card.Type.ToString(), card.TargetType.ToString(), card.CurrentUpgradeLevel,
            pile.Type == PileType.Hand ? Read<bool?>(() => card.CanPlay(), card.Id + ".playable") : null,
            targets.Where(card.IsValidTarget).Select(t => Id(t, "unit-")).ToArray(),
            card.Keywords.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToArray(), Variables(card.DynamicVars),
            pile.Type == PileType.Hand ? targets.Where(card.IsValidTarget).ToDictionary(t => Id(t, "unit-"),
                t => Read(() => Preview(card, t), card.Id + ".target_preview")) : new Dictionary<string, IReadOnlyDictionary<string, decimal>?>()
        )).ToArray());

    private CreatureInfo Creature(Creature creature, Creature localPlayer)
    {
        var monster = creature.Monster;
        var next = monster?.NextMove;
        var intents = next?.Intents.Select(intent => new IntentInfo(intent.IntentType.ToString(),
            Read(() => Clean(intent.GetHoverTip([localPlayer], creature).Description), "intent.description"),
            intent is AttackIntent attack ? Read<decimal?>(() => attack.GetSingleDamage([localPlayer], creature), "intent.damage") : null,
            intent is AttackIntent atk ? atk.Repeats : null)).ToArray() ?? [];
        var follow = next?.FollowUpState?.Id ?? next?.FollowUpStateId;
        return new(Id(creature, "unit-"), creature.Player?.Character.Title.GetFormattedText() ??
            monster?.Title.GetFormattedText() ?? creature.Name, creature.CurrentHp, creature.MaxHp,
            creature.Block, Powers(creature), intents, next?.Id, follow == null ? [] : [follow], FixedMoves(monster));
    }

    private EffectInfo[] Powers(Creature creature) => creature.Powers.Select(power => new EffectInfo(
        power.Id.ToString(), Source(power), power.Title.GetFormattedText(),
        Read(() => Clean(string.Join("\n", power.HoverTips.OfType<HoverTip>().Select(t => t.Description))), power.Id + ".description"),
        power.Amount, Variables(power.DynamicVars))).ToArray();

    private IReadOnlyDictionary<string, decimal> Variables(DynamicVarSet variables)
    {
        return variables.ToDictionary(pair => pair.Key, pair => pair.Value.BaseValue);
    }
    private static IReadOnlyDictionary<string, decimal> Preview(CardModel card, Creature target)
    {
        // The preview mutates only a cloned variable set, never the live card's values.
        var variables = card.DynamicVars.Clone(card);
        card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, variables);
        return variables.ToDictionary(pair => pair.Key, pair => pair.Value.PreviewValue);
    }
    private static IReadOnlyList<FutureMoveInfo> FixedMoves(MonsterModel? monster)
    {
        var result = new List<FutureMoveInfo>();
        var move = monster?.NextMove;
        var states = monster?.MoveStateMachine?.States;
        // Resolve only literal links of the built-in MoveState. Never call GetNextState,
        // branch delegates, roll RNG, or temporarily alter the real monster's history.
        while (move?.GetType() == typeof(MoveState) && result.Count < 3)
        {
            var next = move.FollowUpState;
            if (next == null && move.FollowUpStateId != null && states != null)
                states.TryGetValue(move.FollowUpStateId, out next);
            if (next?.GetType() != typeof(MoveState)) break;
            move = (MoveState)next;
            result.Add(new(move.Id, move.Intents.Select(i => i.IntentType.ToString()).ToArray()));
        }
        return result;
    }
    private T? Read<T>(Func<T> reader, string field)
    {
        try
        {
            var value = reader();
            if (value is string text && string.IsNullOrWhiteSpace(text))
            {
                _warnings.Add($"{field} 的描述为空，效果语义未知。");
                return default;
            }
            return value;
        }
        catch (Exception ex)
        {
            _warnings.Add($"无法读取 {field}（{ex.GetType().Name}），该字段未知，不按零处理。");
            return default;
        }
    }
    private static string Source(object model) => model.GetType().Assembly.GetName().Name ?? "unknown";
    private static bool Usable(PotionModel potion) =>
        potion.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime && potion.PassesCustomUsabilityCheck && !potion.IsQueued;
    private static string Clean(string text) => Regex.Replace(text, @"\[[^\]\r\n]+\]", "");
}
