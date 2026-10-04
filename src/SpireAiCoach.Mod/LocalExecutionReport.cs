using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Plain diagnostic values only: no live models, replay bytes, provider settings or credentials.
public sealed record LocalExecutionCard(uint Instance, string ModelId, string Name);
public sealed record LocalExecutionEnemy(uint? Instance, int Hp, int Block);
public sealed record LocalExecutionState(int Round, int Hp, int MaxHp, int Block, int Energy, int Stars,
    LocalExecutionCard[] Hand, LocalExecutionEnemy[] Enemies)
{
    internal static LocalExecutionState? Capture()
    {
        try
        {
            var state = CombatManager.Instance.DebugOnlyGetState();
            var player = state == null ? null : LocalContext.GetMe(state);
            if (player?.PlayerCombatState is not { } pcs) return null;
            return new(state!.RoundNumber, player.Creature.CurrentHp, player.Creature.MaxHp, player.Creature.Block,
                pcs.Energy, pcs.Stars, pcs.Hand.Cards.Select(c =>
                    new LocalExecutionCard(NetCombatCard.FromModel(c).CombatCardIndex, c.Id.ToString(), c.Title)).ToArray(),
                state.Enemies.Select(e => new LocalExecutionEnemy(e.CombatId, e.CurrentHp, e.Block)).ToArray());
        }
        catch { return null; } // A diagnostic failure must not replace the execution's original outcome.
    }
}

public sealed record LocalExecutionStep(int Index, LocalAction Action, string ActualHash,
    LocalHistoryStamp ActualHistory, LocalExecutionState? Before, LocalExecutionState? After);
public sealed record LocalExecutionReport(string Id, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    string Version, string? RequestId, string? SnapshotId, int StartingActionIndex, int PlannedActions,
    int DispatchedActions, int SettledActions, bool? ExpectedVictory, bool BattleEnded, string Stage,
    string Message, int? ExpectedHp, LocalAction[] Plan, LocalAction? NextAction, string? ExpectedHash, string? ActualHash,
    LocalHistoryStamp? ExpectedHistory, LocalHistoryStamp? ActualHistory, LocalExecutionState? ActualState,
    LocalExecutionStep[] Steps, string? ExceptionType, string? ExceptionStack);
