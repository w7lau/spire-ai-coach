namespace SpireAiCoach.Core;

// Native escape is a settled result, but it does not defeat the escaped enemy.
// Keep native identities after the battlefield list has removed that creature.
public sealed record LocalEscapedEnemy(uint? CombatId, string ModelId, int Hp);
public sealed record LocalCombatOutcome(bool Settled, bool NativeVictory, LocalEscapedEnemy[] EscapedEnemies)
{
    public bool Escaped => EscapedEnemies.Length > 0;
    public bool Won(bool dead) => Settled && NativeVictory && !dead && !Escaped;
    public bool Matches(LocalCombatOutcome? expected) => expected != null &&
        Settled == expected.Settled && NativeVictory == expected.NativeVictory &&
        EscapedEnemies.SequenceEqual(expected.EscapedEnemies);
}
