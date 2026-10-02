using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Only started by an explicit Execute button. Never loads/replays a save or changes RNG.
public sealed class LocalPlanExecutor(SceneTree tree)
{
    public async Task<string> Execute(LocalContinuation plan, Func<string?> combatId,
        bool includePotions, Action<string> progress, CancellationToken token)
    {
        int expected = plan.CompletedActions;
        int count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null || CombatManager.Instance.IsOverOrEnding)
                return "战斗已结束，已停止执行。";
            if (state.Players.Count != 1 || RunManager.Instance.NetService.Type != NetGameType.Singleplayer)
                throw new InvalidOperationException("自动执行仅支持单人战斗。");
            if (!LocalCapture.Stable()) throw new InvalidOperationException("战斗尚未结算完成，已停止执行。");
            var remaining = plan.Advance(combatId() ?? "", LocalCapture.LoadedMods(), LocalCapture.Fingerprint(), LocalCapture.History());
            if (remaining?.Best is not { Dead: false, Actions.Length: > 0 } best || plan.CompletedActions != expected)
                throw new InvalidOperationException("实际状态或操作与方案不一致，已停止执行，请重新计算。");
            var action = best.Actions[0];
            if (action.BeforeHash != LocalCapture.Fingerprint()) throw new InvalidOperationException("战斗状态已变化，已停止执行。");
            var player = LocalContext.GetMe(state)!;
            var target = action.TargetId is null ? null : state.Creatures.SingleOrDefault(c => c.CombatId == action.TargetId);
            if (action.TargetId.HasValue && target == null) throw new InvalidOperationException("原目标已不存在，已停止执行。");
            progress($"正在执行第 {++count} 步：{LocalSearchPolicy.Describe(action)}");
            token.ThrowIfCancellationRequested();
            if (action.EndTurn) PlayerCmd.EndTurn(player, canBackOut: false);
            else if (action.PotionSlot is { } slot)
            {
                var potion = slot >= 0 && slot < player.PotionSlots.Count ? player.GetPotionAtSlotIndex(slot) : null;
                if (!includePotions || !remaining.IncludePotions || potion == null || potion.Id.ToString() != action.ModelId ||
                    potion.Usage is not (PotionUsage.CombatOnly or PotionUsage.AnyTime) ||
                    !potion.PassesCustomUsabilityCheck || potion.IsQueued || potion.HasBeenRemovedFromState || !potion.IsValidTarget(target))
                    throw new InvalidOperationException("药水或目标已变化，已停止执行。");
                potion.EnqueueManualUse(target);
            }
            else
            {
                var hand = player.PlayerCombatState!.Hand.Cards;
                if (action.HandIndex < 0 || action.HandIndex >= hand.Count) throw new InvalidOperationException("手牌已变化，已停止执行。");
                var card = hand[action.HandIndex];
                if (card.Id.ToString() != action.ModelId || !card.CanPlay() || !card.IsValidTarget(target))
                    throw new InvalidOperationException("这张牌或目标已不可用，已停止执行。");
                RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, target));
            }
            // Let the actual game process the command/animations; cancellation never undoes a committed action.
            var timer = Stopwatch.StartNew();
            await Frame(); await Frame();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding) return "战斗已结束，已停止执行。";
                if (!ReferenceEquals(state, CombatManager.Instance.DebugOnlyGetState()))
                    throw new InvalidOperationException("已进入另一场战斗，已停止执行。");
                if (LocalCapture.Stable() && (!action.EndTurn || CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > action.Round)) break;
                if (timer.Elapsed.TotalSeconds > 30) throw new InvalidOperationException("等待结算或额外选择超时，已停止执行，请手动处理。");
                await Frame();
            }
            expected++;
            if (best.Actions.Length == 1) return "已执行完已验证的路线；战斗未结束时请重新计算。";
        }
    }

    private async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
}
