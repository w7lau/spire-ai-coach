using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Saves.Runs;
using SpireAiCoach.Mod;

// CLR metadata checks only: no combat, save, RNG or game command is created.
internal static class SnapshotMetadataAudit
{
    private static int _constructorCalls, _getterCalls;
    // The audited native switch returns true for the zero-valued condition.
    private const SerializationCondition Always = (SerializationCondition)0;
    private sealed class Fixture
    {
        [SavedProperty(Always)]
        public int Value { get { _getterCalls++; return 42; } }
    }
    public static int Run(string destination)
    {
        var owner = typeof(LocalWorker).Assembly.GetType("SpireAiCoach.Mod.LocalWorkerSnapshotMetadata", true)!;
        var reuse = owner.GetField("_reuse", BindingFlags.NonPublic | BindingFlags.Static)!;
        var read = AccessTools.Method(owner, "ReadCondition");
        var keywords = AccessTools.Method(owner, "ReadKeywords");
        var property = typeof(Fixture).GetProperty(nameof(Fixture.Value))!;
        var constructor = AccessTools.Constructor(typeof(SavedPropertyAttribute), [typeof(SerializationCondition)]);
        var harmony = new Harmony("SpireAiCoach.metadata-audit.external-constructor");
        bool prior = (bool)reuse.GetValue(null)!;
        try
        {
            reuse.SetValue(null, true);
            for (int i = 0; i < 4; i++) Require((SerializationCondition)read.Invoke(null, [property])! == Always);
            Require(_getterCalls == 0);
            Require(((CardKeyword[])keywords.Invoke(null, null)!).SequenceEqual(Enum.GetValues<CardKeyword>()));
            harmony.Patch(constructor, postfix: new(AccessTools.Method(typeof(SnapshotMetadataAudit), nameof(ObserveConstructor))));
            for (int i = 0; i < 3; i++) Require((SerializationCondition)read.Invoke(null, [property])! == Always);
            Require(_constructorCalls == 3);
            harmony.UnpatchAll(harmony.Id);
            read.Invoke(null, [property]);
            Require(_constructorCalls == 3);
            Require(_getterCalls == 0);
            var keywordBody = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(NetFullCombatState.CardState), "From"));
            var transformed = Transform("KeywordTable", keywordBody);
            Require(transformed.Any(c => c.operand is MethodInfo m && m.Name == "ReadKeywords"));
            int table = keywordBody.FindIndex(c => c.operand is MethodInfo m && m.DeclaringType == typeof(Enum) && m.Name == "GetValues");
            var tableStore = keywordBody[table + 1];
            var extraLoad = new CodeInstruction(tableStore.opcode == OpCodes.Stloc_3 ? OpCodes.Ldloc_3 : OpCodes.Ldloc_S, tableStore.operand);
            var escaped = keywordBody.Select(c => new CodeInstruction(c)).ToList();
            escaped.InsertRange(table + 2, [extraLoad, new(OpCodes.Pop)]);
            Require(Rejects("KeywordTable", escaped));
            var savedBody = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(SavedProperties), "FromInternal"));
            Require(Transform("SavedCondition", savedBody).Any(c => c.operand is MethodInfo m && m.Name == "ReadCondition"));
            int attribute = savedBody.FindIndex(c => c.operand is MethodInfo m && m.DeclaringType == typeof(CustomAttributeExtensions) && m.Name == "GetCustomAttribute");
            var attributeLoad = new CodeInstruction(savedBody[attribute + 2]);
            var exposed = savedBody.Select(c => new CodeInstruction(c)).ToList();
            exposed.InsertRange(attribute + 2, [attributeLoad, new(OpCodes.Pop)]);
            Require(Rejects("SavedCondition", exposed));
            File.WriteAllText(destination, JsonSerializer.Serialize(new {
                passed = true, reflection_getter_calls = _getterCalls, preserved_foreign_constructor_calls = _constructorCalls,
                keyword_table_matches_native = true, altered_native_boundaries_rejected = 2,
                combat_or_save_created = false }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { harmony.UnpatchAll(harmony.Id); reuse.SetValue(null, prior); }

        List<CodeInstruction> Transform(string method, IEnumerable<CodeInstruction> body) =>
            ((IEnumerable<CodeInstruction>)AccessTools.Method(owner, method).Invoke(null, [body])!).ToList();
        bool Rejects(string method, IEnumerable<CodeInstruction> body)
        {
            try { Transform(method, body); return false; }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { return true; }
        }
    }
    private static void ObserveConstructor() => _constructorCalls++;
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Snapshot metadata audit failed"); }
}
