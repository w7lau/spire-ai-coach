using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Test-only observation in the exact owned worker. Read identities and stored HP;
// never ask a gameplay permission/preview hook or alter a model/command result.
internal static class EnemyProbeObserver
{
    private static string _root = "";
    private static string? _id;
    private static readonly FieldInfo Request = typeof(LocalWorker).GetField("_activeRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
    public static void Install(string directory)
    {
        if (!File.Exists(Path.Combine(directory, ".coach-worker")) ||
            !string.Equals(OS.GetExecutablePath().Replace('/', '\\'), Path.Combine(directory, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enemy observation requires the exact owned worker executable");
        _root = directory;
        AuditGuards();
        var harmony = new Harmony("SpireLocalIntegration.enemy-observation");
        harmony.Patch(AccessTools.Method(typeof(CombatState), nameof(CombatState.CreateCreature)),
            prefix: new(AccessTools.Method(typeof(EnemyProbeObserver), nameof(Created))));
        harmony.Patch(AccessTools.Method(typeof(MonsterModel), nameof(MonsterModel.PerformMove)),
            prefix: new(AccessTools.Method(typeof(EnemyProbeObserver), nameof(Moved))));
        harmony.Patch(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Kill), [typeof(Creature), typeof(bool)]),
            prefix: new(AccessTools.Method(typeof(EnemyProbeObserver), nameof(KillRequested))));
        harmony.Patch(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Kill), [typeof(IReadOnlyCollection<Creature>), typeof(bool)]),
            prefix: new(AccessTools.Method(typeof(EnemyProbeObserver), nameof(KillsRequested))));
        harmony.Patch(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Escape)),
            prefix: new(AccessTools.Method(typeof(EnemyProbeObserver), nameof(EscapeRequested))));
    }
    private static void AuditGuards()
    {
        var owner = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalEnemyPresentation", true)!;
        var audit = owner.GetMethod("AuditPositionMethod", BindingFlags.Static | BindingFlags.NonPublic)!;
        var initializer = AccessTools.Method(typeof(SandpitPower), nameof(SandpitPower.AfterApplied));
        var original = LocalMethodBody.Read(initializer)!;
        bool Check(IEnumerable<LocalInstruction> code) => (bool)audit.Invoke(null, [code, initializer])!;
        if (!Check(original) ||
            Check(original.Append(new(9999, OpCodes.Call, AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.SetCurrentHp))))) ||
            Check(original.Append(new(9999, OpCodes.Stsfld, AccessTools.Field(typeof(EnemyProbeObserver), nameof(_id))))))
            throw new InvalidOperationException("Optional enemy geometry audit accepted a rule/global write or rejected the native initializer");
        LocalWire.Write(Path.Combine(_root, "enemy-guard-audit.json"), new {
            passed = true, nativeInitializerAccepted = true, addedGameplayCallRejected = true, addedGlobalWriteRejected = true });
    }
    private static void Created(MonsterModel __0) => Record(__0, "created", null);
    private static void Moved(MonsterModel __instance) => Record(__instance, "move", __instance.NextMove?.Id);
    private static void KillRequested(Creature __0) => Record(__0.ModelId.Entry, "kill-request", null, __0.CurrentHp);
    private static void KillsRequested(IReadOnlyCollection<Creature> __0) { foreach (var c in __0) KillRequested(c); }
    private static void EscapeRequested(Creature __0) { if (__0.Monster != null) Record(__0.Monster, "escape-request", null); }
    private static void Record(MonsterModel monster, string kind, string? move)
        => Record(monster.Id.Entry, kind, move, kind == "move" ? monster.Creature.CurrentHp : null);
    private static void Record(string model, string kind, string? move, int? hp)
    {
        if (Request.GetValue(null) is not LocalSearchRequest request) return;
        var path = Path.Combine(_root, "enemy-probe.jsonl");
        if (_id != request.Id) { _id = request.Id; File.WriteAllText(path, ""); }
        File.AppendAllText(path, JsonSerializer.Serialize(new { request = _id, model, kind, move, hp }) + "\n");
    }
}
