using System.Reflection;
using System.Runtime.CompilerServices;
using SpireAiCoach.Core;

internal static class SummonPresentationTests
{
    private sealed class Node;
    private sealed class Model { public Node Creature => new(); }
    private sealed class Room { public static Room Instance => new(); public Node Lookup(Node creature) => creature; }
    private static bool Enabled => true;
    private static int _effects;
    private static void Fall(Node node) { }
    private static void Effect() => _effects++;
    private static void ReadNode(Node node) { }
    private static void Valid(Model model) { Effect(); if (Enabled) { var node = Room.Instance.Lookup(model.Creature); Fall(node); } Effect(); }
    private static async Task AsyncValid(Model model) { await Task.Yield(); if (Enabled) { var node = Room.Instance.Lookup(model.Creature); Fall(node); } Effect(); }
    private static void Mixed(Model model) { if (Enabled) { var node = Room.Instance.Lookup(model.Creature); Fall(node); Effect(); } }
    private static void Writes(Model model) { if (Enabled) { var node = Room.Instance.Lookup(model.Creature); Fall(node); _effects = 0; } }
    private static void Leaks(Model model) { Node node = null!; if (Enabled) { node = Room.Instance.Lookup(model.Creature); Fall(node); } ReadNode(node); }
    private static void Unguarded(Model model) => Fall(Room.Instance.Lookup(model.Creature));
    private static bool Match(string name)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var method = typeof(SummonPresentationTests).GetMethod(name, flags)!;
        var machine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        if (machine != null) method = machine.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return LocalPresentationGuard.OptionalGuardedCalls(method,
            typeof(SummonPresentationTests).GetProperty(nameof(Enabled), flags)!.GetMethod!,
            typeof(Room).GetProperty(nameof(Room.Instance))!.GetMethod!, typeof(Model).GetProperty(nameof(Model.Creature))!.GetMethod!,
            typeof(Room).GetMethod(nameof(Room.Lookup))!, typeof(SummonPresentationTests).GetMethod(nameof(Fall), flags)!);
    }
    public static void Register(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Unsafe or missing optional summon boundary"); }
        test("summon display guard retains effects before and after an optional node block without executing them", () => { var before = _effects; Check(Match(nameof(Valid))); Check(_effects == before); });
        test("summon display guard recognizes a native async callback without removing its await or later effect", () => Check(Match(nameof(AsyncValid))));
        test("summon display guard rejects a rule call inside the visual block", () => Check(!Match(nameof(Mixed))));
        test("summon display guard rejects state writes inside the visual block", () => Check(!Match(nameof(Writes))));
        test("summon display guard rejects a local consumed after the skipped block", () => Check(!Match(nameof(Leaks))));
        test("summon display guard rejects unguarded node access", () => Check(!Match(nameof(Unguarded))));
    }
}
