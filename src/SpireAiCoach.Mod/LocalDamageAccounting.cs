using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Players;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Observe the game's own damage entries, including effects from Mods. This does
// not replace damage commands, HP hooks, or the independent HP-change counter.
internal sealed class LocalDamageAccounting : IDisposable
{
    private readonly CombatHistory _history = CombatManager.Instance.History;
    private readonly Player _player;
    private CombatHistoryEntry? _last;
    private int _enemy, _self, _unknown;

    public LocalDamageAccounting(Player player)
    {
        _player = player;
        _last = _history.Entries.LastOrDefault();
        _history.Changed += Changed;
    }

    private void Changed()
    {
        var entry = _history.Entries.LastOrDefault();
        if (ReferenceEquals(entry, _last)) return;
        _last = entry;
        if (entry is not DamageReceivedEntry damage || !ReferenceEquals(damage.Receiver, _player.Creature)) return;
        int loss = Math.Max(0, damage.Result.UnblockedDamage);
        if (damage.Dealer?.IsEnemy == true) _enemy += loss;
        else if (ReferenceEquals(damage.Dealer, _player.Creature)) _self += loss;
        else _unknown += loss;
    }

    public LocalDamageSources Snapshot(int grossHpLost)
    {
        int recorded = _enemy + _self + _unknown;
        return new(_enemy, _self, _unknown, Math.Max(0, grossHpLost - recorded), recorded <= grossHpLost);
    }

    public void Dispose() => _history.Changed -= Changed;
}
