namespace SpireAiCoach.Core;

// Actual changes, including healing above the starting HP and HP clipped when
// maximum HP falls. Observing no gain is not a bound on future healing.
public sealed record LocalHealthChanges(int StartingHp, int StartingMaxHp, int FinalHp, int FinalMaxHp,
    int HpLost, int HpGained, int MaxHpGained, int MaxHpLost, int UnobservedChanges)
{
    public bool FullyObserved => UnobservedChanges == 0;
}

public sealed class LocalHealthAccounting(int hp, int maxHp)
{
    private readonly int _startingHp = hp, _startingMaxHp = maxHp;
    private int _hp = hp, _maxHp = maxHp, _hpLost, _hpGained, _maxHpGained, _maxHpLost, _unobserved;
    public int HpLost => _hpLost;

    // Setters, notifications and their postfixes may report the same state.
    // Count transitions once; sample the live value so reentrant notifications
    // cannot replay an older state after a Mod has already healed the player.
    public void Observe(int currentHp, int currentMaxHp, bool checkpoint = false)
    {
        if (checkpoint && (currentHp != _hp || currentMaxHp != _maxHp)) _unobserved++;
        _hpLost = checked(_hpLost + Math.Max(0, _hp - currentHp));
        _hpGained = checked(_hpGained + Math.Max(0, currentHp - _hp));
        _maxHpGained = checked(_maxHpGained + Math.Max(0, currentMaxHp - _maxHp));
        _maxHpLost = checked(_maxHpLost + Math.Max(0, _maxHp - currentMaxHp));
        _hp = currentHp; _maxHp = currentMaxHp;
    }

    public LocalHealthChanges Snapshot() => new(_startingHp, _startingMaxHp, _hp, _maxHp,
        _hpLost, _hpGained, _maxHpGained, _maxHpLost, _unobserved);
}
