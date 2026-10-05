using SpireAiCoach.Core;

internal static class DiscardEffectsTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool condition) { if (!condition) throw new Exception("Discard effect regression"); }
        var safe = new LocalTacticalFeatures(EnemyHp: 100, Hp: 50);
        test("discard free plays are valued by effect rather than printed resource costs", () => {
            Check(LocalDiscardEffects.Value([new(Damage: 12)], safe) > LocalDiscardEffects.Value([default], safe));
            Check(LocalDiscardEffects.Value([new(HandGain: 2)], safe) > 0);
            Check(LocalDiscardEffects.Value([new(Energy: 2)], safe) > 0);
            Check(LocalDiscardEffects.Value([new(Stars: 2)], safe) > 0);
        });
        test("discard choices respond to current incoming damage", () => {
            var attack = new LocalDiscardYield(Damage: 11); var defense = new LocalDiscardYield(Block: 7);
            Check(LocalDiscardEffects.Value([attack], safe) > LocalDiscardEffects.Value([defense], safe));
            var threatened = safe with { Incoming = 12 };
            Check(LocalDiscardEffects.Value([defense], threatened) > LocalDiscardEffects.Value([attack], threatened));
        });
        test("multiple discard block triggers share one threat instead of saving the same HP twice", () => {
            var threatened = safe with { Incoming = 10 };
            int combined = LocalDiscardEffects.Value([new(Block: 8), new(Block: 8)], threatened);
            Check(combined == LocalDiscardEffects.Value([new(Block: 10)], threatened));
            Check(combined < 2 * LocalDiscardEffects.Value([new(Block: 8)], threatened));
            Check(LocalDiscardEffects.Value([new(Block: 8), new(Block: 8)], threatened with { RetainsBlock = true }) > combined);
        });
        test("discard learned healing and self harm affect order without certifying a route", () => {
            Check(LocalDiscardEffects.Value([new(Healing: 4)], safe) > 0);
            Check(LocalDiscardEffects.Value([new(HpCost: 4)], safe) < 0);
            Check(LocalDiscardEffects.Value([new(HandEndHpLoss: 2)], safe) > LocalDiscardEffects.Value([default], safe));
        });
        test("optional discard preserves valuable payable cards and empty choices", () => {
            Check(LocalDiscardEffects.Value([], safe, LocalRolloutStyle.Preparation) == 0);
            Check(LocalDiscardEffects.Value([new(KeepValue: 15)], safe) < 0);
            Check(LocalDiscardEffects.Value([new(Energy: 2, KeepValue: 5)], safe) > 0);
        });
        test("discard observations average vectors so current context remains authoritative", () => {
            var average = new LocalDiscardYield(Block: 6, HandGain: 2).Add(new(Block: 10)).Scale(.5);
            Check(average.Block == 8 && average.HandGain == 1);
            Check(LocalDiscardEffects.Value([average], safe with { Incoming = 10 }) > LocalDiscardEffects.Value([average], safe));
        });
        test("discard hooks consuming energy or stars are not mistaken for free benefits", () => {
            Check(LocalDiscardEffects.Value([new(Energy: -2)], safe) < 0);
            Check(LocalDiscardEffects.Value([new(Stars: -2)], safe) < 0);
        });
    }
}
