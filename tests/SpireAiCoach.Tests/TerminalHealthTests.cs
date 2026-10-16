using System.Reflection;
using SpireAiCoach.Core;
using V = SpireAiCoach.Core.LocalScalarProgram.Value;

static class TerminalHealthTests
{
    public static void Register(Action<string, Action> test)
    {
        void Check(bool ok) { if (!ok) throw new Exception("Conditional terminal recovery regression"); }
        int[] Map(Func<int, int> effect) => LocalTerminalHealth.Envelope(Enumerable.Range(0, 67).Select(effect).ToArray());
        test("conditional terminal recovery preserves high HP and improves low HP", () =>
        {
            var map = Map(h => h <= 33 ? Math.Min(66, h + 12) : h);
            Check(LocalTerminalHealth.Compose(52, [map]) == 52);
            Check(LocalTerminalHealth.Compose(30, [map]) == 42);
            Check(LocalTerminalHealth.Compose(33, [map]) == 45);
            // A candidate may lose HP before the terminal callback to enable it.
            Check(LocalTerminalHealth.Compose(40, [map]) == 45);
            Check(LocalTerminalHealth.Compose(18, [map]) == 30);
            // A revival to 26 can improve that target after conditional healing.
            Check(LocalTerminalHealth.Compose(26, [map]) == 38);
        });
        test("terminal recovery composes thresholds and fixed gains without assuming callback order", () =>
        {
            var conditional = Map(h => h <= 33 ? Math.Min(66, h + 12) : h);
            var fixedGain = Map(h => Math.Min(66, h + 6));
            Check(LocalTerminalHealth.Compose(30, [conditional, fixedGain]) == 48);
            Check(LocalTerminalHealth.Compose(30, [fixedGain, conditional]) == 48);
            Check(LocalTerminalHealth.Compose(52, [conditional, fixedGain]) == 58);
            Check(LocalTerminalHealth.Compose(52, Enumerable.Repeat(conditional, 9).ToArray()) == null);
        });
        test("scalar metadata handles decimal thresholds without executing inspected code", () =>
        {
            var method = typeof(TerminalHealthTests).GetMethod(nameof(Transfer), BindingFlags.Static | BindingFlags.NonPublic)!;
            V? Read(int hp, int max, decimal threshold, decimal amount) => LocalScalarProgram.Read(LocalMethodBody.Read(method)!,
                [V.N(hp), V.N(max), V.N(threshold), V.N(amount)], (_, _) => null, LocalScalarProgram.Arithmetic);
            Check(Read(52, 66, 50, 12)?.Number == 52);
            Check(Read(30, 66, 50, 12)?.Number == 42);
            Check(Read(33, 67, 50, 12)?.Number == 45);
            Check(Read(34, 67, 50, 12)?.Number == 34);
            Check(Read(20, 80, 25, 5)?.Number == 25);
        });
        test("scalar metadata rejects unknown calls and mutable loops", () =>
        {
            foreach (var name in new[] { nameof(Unknown), nameof(Loop) })
            {
                var m = typeof(TerminalHealthTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
                Check(LocalScalarProgram.Read(LocalMethodBody.Read(m)!, [V.N(20)], (_, _) => null, (_, _) => null) == null);
            }
        });
    }
    private static int Transfer(int hp, int max, decimal threshold, decimal amount) =>
        hp <= (int)(max * (threshold / 100m)) ? Math.Min(max, hp + (int)amount) : hp;
    private static int Unknown(int hp) => hp + Environment.TickCount;
    private static int Loop(int hp) { while (hp < 33) hp++; return hp; }
}
