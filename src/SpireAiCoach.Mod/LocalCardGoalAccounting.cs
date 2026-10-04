using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Players;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Read native history only. Auto/repeated plays and card-source finishing blows
// use the same event boundary as ordinary plays, including third-party cards.
internal sealed class LocalCardGoalAccounting : IDisposable
{
    private readonly CombatHistory _history = CombatManager.Instance.History;
    private readonly Player _player;
    private readonly LocalCardGoals? _goals;
    private CombatHistoryEntry? _last;
    private int _plays, _kills, _stepPlays, _stepKills;
    private readonly List<LocalCardGoalStep> _steps = [];

    public LocalCardGoalAccounting(Player player, LocalCardGoals? goals)
    {
        _player = player; _goals = goals;
        _last = goals?.Enabled == true ? _history.Entries.LastOrDefault() : null;
        if (goals?.Enabled == true) _history.Changed += Changed;
    }

    private void Changed()
    {
        var entry = _history.Entries.LastOrDefault();
        if (ReferenceEquals(entry, _last)) return;
        _last = entry;
        if (entry is CardPlayFinishedEntry play && ReferenceEquals(play.CardPlay.Card.Owner, _player) &&
            play.CardPlay.Card.Id.ToString() == _goals!.PlayModelId) _plays++;
        if (entry is DamageReceivedEntry damage && damage.Receiver.IsEnemy && damage.Result.WasTargetKilled &&
            damage.CardSource is { } source && ReferenceEquals(source.Owner, _player) &&
            source.Id.ToString() == _goals!.FinisherModelId) _kills++;
    }

    public void CompleteStep()
    {
        if (_goals?.Enabled != true) return;
        _steps.Add(new(_plays - _stepPlays, _kills - _stepKills));
        _stepPlays = _plays; _stepKills = _kills;
    }

    public LocalCardGoalOutcome? Snapshot() => _goals?.Enabled == true ?
        new(_goals.PlayModelId, _goals.FinisherModelId, _plays, _kills, _steps.ToArray()) : null;

    public bool Matches(LocalCardGoalOutcome? expected) => expected == null ? _goals?.Enabled != true :
        Snapshot() is { } actual && actual.PlayModelId == expected.PlayModelId &&
        actual.FinisherModelId == expected.FinisherModelId && actual.Plays == expected.Plays &&
        actual.Kills == expected.Kills && actual.Steps.SequenceEqual(expected.Steps);

    public void Dispose()
    {
        if (_goals?.Enabled == true) _history.Changed -= Changed;
    }
}
