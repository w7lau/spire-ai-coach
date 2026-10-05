using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Only started by an explicit Execute button. Never loads/replays a save or changes RNG.
public sealed class LocalPlanExecutor(SceneTree tree)
{
    public LocalExecutionReport? Report { get; private set; }

    public async Task<string> Execute(LocalContinuation plan, Func<string?> combatId,
        bool includePotions, Action<string> progress, CancellationToken token)
    {
        int expected = plan.CompletedActions;
        var source = plan.Original;
        int start = expected, total = source.Best?.Actions.Length ?? 0, dispatched = 0, settled = 0;
        var started = DateTimeOffset.UtcNow;
        var steps = new List<LocalExecutionStep>();
        LocalAction? next = null;
        string stage = "before-step";
        string? actualHash = null;
        LocalHistoryStamp? actualHistory = null, expectedHistory = null;
        Report = null;
        string Finish(string message, Exception? error = null)
        {
            Report = new(Guid.NewGuid().ToString("N"), started, DateTimeOffset.UtcNow,
                typeof(ModEntry).Assembly.GetName().Version!.ToString(3), source.Id, source.SnapshotId,
                start, total, dispatched, settled, source.Best?.Won,
                !CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding,
                stage, message, source.Best?.Hp, source.Best?.Actions ?? [], next, next?.BeforeHash, actualHash, expectedHistory, actualHistory,
                LocalExecutionState.Capture(), steps.ToArray(), error?.GetType().FullName, error?.ToString());
            return message;
        }
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var state = CombatManager.Instance.DebugOnlyGetState();
                if (state == null || CombatManager.Instance.IsOverOrEnding)
                    return Finish("战斗已结束，已停止执行。");
                if (state.Players.Count != 1 || RunManager.Instance.NetService.Type != NetGameType.Singleplayer)
                    throw new InvalidOperationException("自动执行仅支持单人战斗。");
                next = source.Best?.Actions.ElementAtOrDefault(expected);
                expectedHistory = source.Best?.Continuation?.ElementAtOrDefault(expected)?.History;
                stage = "before-step-settle";
                await WaitForSettlement(state, null, null, token);
                if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding)
                    return Finish("战斗已结束，已停止执行。");
                stage = "checkpoint";
                actualHash = LocalCapture.Fingerprint(); actualHistory = LocalCapture.History();
                var remaining = plan.Advance(combatId() ?? "", LocalCapture.LoadedMods(), actualHash, actualHistory);
                if (remaining?.Best is not { Dead: false, Actions.Length: > 0 } best || plan.CompletedActions != expected)
                    throw new InvalidOperationException("实际状态或操作与方案不一致，已停止执行，请重新计算。");
                var action = best.Actions[0];
                next = action; expectedHistory = best.Continuation?.FirstOrDefault()?.History;
                actualHash = LocalCapture.Fingerprint();
                if (action.BeforeHash != actualHash) throw new InvalidOperationException("战斗状态已变化，已停止执行。");
                var player = LocalContext.GetMe(state)!;
                var target = action.TargetId is null ? null : state.Creatures.SingleOrDefault(c => c.CombatId == action.TargetId);
                if (action.TargetId.HasValue && target == null) throw new InvalidOperationException("原目标已不存在，已停止执行。");
                stage = "validate-action";
                var before = LocalExecutionState.Capture();
                progress($"正在执行第 {expected + 1}/{total} 步：{LocalSearchPolicy.Describe(action)}");
                token.ThrowIfCancellationRequested();
                if (action.EndTurn) RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(
                    new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
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
                    if (card.Id.ToString() != action.ModelId ||
                        action.CombatCardIndex is { } instance && NetCombatCard.FromModel(card).CombatCardIndex != instance ||
                        !card.CanPlay() || !card.IsValidTarget(target))
                        throw new InvalidOperationException("这张牌或目标已不可用，已停止执行。");
                    RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(new PlayCardAction(card, target));
                }
                dispatched++;
                // Let the actual game process the command/animations; cancellation never undoes a committed action.
                var choices = new LocalChoices(action.Choices);
                stage = "action-settle";
                await Frame();
                await WaitForSettlement(state, action, choices, token);
                choices.Finish();
                settled++;
                steps.Add(new(expected + 1, action, actualHash, actualHistory, before, LocalExecutionState.Capture()));
                expected++;
                if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding)
                { stage = "battle-ended"; return Finish($"已执行 {settled} 步，战斗已结束。"); }
                if (best.Actions.Length == 1)
                {
                    stage = best.Won ? "victory-mismatch" : "partial-route";
                    next = null; expectedHistory = null;
                    if (best.Won)
                        throw new InvalidOperationException($"已执行完 {total} 步，但未达到预计的胜利结果；模拟与实际不一致，已停止执行。");
                    return Finish($"已执行部分路线，共 {settled} 步；战斗尚未结束，请继续计算。");
                }
            }
        }
        catch (Exception ex)
        {
            var message = total > 0 && stage is not "victory-mismatch" and not "partial-route"
                ? $"执行到第 {start + settled}/{total} 步。" + ex.Message : ex.Message;
            Finish(message, ex);
            if (ex is OperationCanceledException) throw;
            throw new InvalidOperationException(message, ex);
        }
    }

    private async Task WaitForSettlement(CombatState state, LocalAction? action, LocalChoices? choices, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            bool ending = !CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding;
            if (!ending && !ReferenceEquals(state, CombatManager.Instance.DebugOnlyGetState()))
                throw new InvalidOperationException("已进入另一场战斗，已停止执行。");
            choices?.Tick(token);
            if (LocalCapture.ExecutionSettled() && (ending || LocalCapture.Stable() &&
                (action?.EndTurn != true || state.RoundNumber > action.Round))) return;
            if (timer.Elapsed.TotalSeconds > 30)
                throw new InvalidOperationException("等待结算或额外选择超时，已停止执行，请手动处理。");
            await Frame();
        }
    }

    private async Task Frame() => await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
}
