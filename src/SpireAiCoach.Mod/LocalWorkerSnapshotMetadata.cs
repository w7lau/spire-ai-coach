using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace SpireAiCoach.Mod;

// Immutable metadata only. Native property getters, serialization conditions,
// card costs and Mod hooks still run for every fresh combat snapshot.
internal static class LocalWorkerSnapshotMetadata
{
    private const string NativeModule = "73b63ee0-6c0a-47bb-b0d1-b21f6d94222e";
    // Native snapshots belong to one thread. Cache only loaded CLR metadata,
    // with a fixed cap; do not retain any model, combat or serialized value.
    [ThreadStatic] private static Dictionary<MemberInfo, SerializationCondition>? _conditions;
    [ThreadStatic] private static bool _reuse;
    private const int MaximumConditions = 4096;
    private static readonly CardKeyword[] Keywords = Enum.GetValues<CardKeyword>();
    private static readonly MethodInfo KeywordValues = typeof(Enum).GetMethods()
        .Single(m => m.Name == nameof(Enum.GetValues) && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(CardKeyword));
    private static readonly MethodInfo AttributeReader = typeof(CustomAttributeExtensions).GetMethods()
        .Single(m => m.Name == nameof(CustomAttributeExtensions.GetCustomAttribute) && m.IsGenericMethodDefinition &&
            m.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(MemberInfo))
        .MakeGenericMethod(typeof(SavedPropertyAttribute));
    private static readonly ConstructorInfo? AttributeConstructor = AccessTools.Constructor(typeof(SavedPropertyAttribute), [typeof(SerializationCondition)]);
    private static readonly List<string> Boundaries = [];
    private static readonly List<string> Failures = [];
    private static long _conditionLoads, _conditionHits, _keywordCopies, _keywordReuses;
    // Evaluate the request/verification gate once per full native snapshot,
    // rather than repeatedly for every property and keyword in every pile.
    internal static bool Enter() { bool previous = _reuse; _reuse = LocalWorker.ReuseSnapshotMetadata; return previous; }
    internal static void Exit(bool previous) => _reuse = previous;

    public static void ResetCounters() => _conditionLoads = _conditionHits = _keywordCopies = _keywordReuses = 0;
    public static object Status() => new { enabled = LocalWorker.ReuseSnapshotMetadata,
        condition_loads = _conditionLoads, condition_hits = _conditionHits,
        keyword_copies = _keywordCopies, keyword_reuses = _keywordReuses,
        boundaries = Boundaries.ToArray(), failures = Failures.ToArray() };

    public static void Install()
    {
        if (typeof(SavedProperties).Assembly.ManifestModule.ModuleVersionId.ToString() != NativeModule)
        { Failures.Add("Native snapshot metadata has not been audited for this game version"); return; }
        Patch(AccessTools.Method(typeof(SavedProperties), "FromInternal"), nameof(SavedCondition));
        Patch(AccessTools.Method(typeof(NetFullCombatState.CardState), "From"), nameof(KeywordTable));
    }

    private static void Patch(MethodInfo? method, string transpiler)
    {
        string name = method?.DeclaringType?.Name + "." + method?.Name;
        var harmony = new Harmony("SpireAiCoach.owned-worker.snapshot-metadata." + transpiler);
        try
        {
            if (method == null) throw new MissingMethodException("Native metadata method unavailable");
            harmony.Patch(method, transpiler: new(AccessTools.Method(typeof(LocalWorkerSnapshotMetadata), transpiler)));
            Boundaries.Add(name);
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id);
            Failures.Add(name + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static SerializationCondition ReadCondition(MemberInfo member)
    {
        // Attribute constructors and reflection readers can themselves be
        // patched. Preserve such callbacks, including patches installed later.
        bool cache = _reuse && AttributeConstructor != null && Harmony.GetPatchInfo(AttributeConstructor) == null &&
            Harmony.GetPatchInfo(AttributeReader) == null;
        if (cache && _conditions?.TryGetValue(member, out var cached) == true)
        { _conditionHits++; return cached; }
        _conditionLoads++;
        var attribute = member.GetCustomAttribute<SavedPropertyAttribute>()!;
        var value = attribute.defaultBehaviour;
        // A derived attribute may execute its own constructor. Preserve those
        // calls instead of assuming that arbitrary Mod attribute code is pure.
        if (cache && attribute.GetType() == typeof(SavedPropertyAttribute))
        {
            _conditions ??= new(ReferenceEqualityComparer.Instance);
            if (_conditions.Count < MaximumConditions) _conditions[member] = value;
        }
        return value;
    }

    private static CardKeyword[] ReadKeywords()
    {
        if (_reuse && Harmony.GetPatchInfo(KeywordValues) == null) { _keywordReuses++; return Keywords; }
        _keywordCopies++;
        return Enum.GetValues<CardKeyword>();
    }

    private static IEnumerable<CodeInstruction> SavedCondition(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var field = AccessTools.Field(typeof(SavedPropertyAttribute), nameof(SavedPropertyAttribute.defaultBehaviour));
        var sites = Enumerable.Range(0, Math.Max(0, code.Count - 3)).Where(i =>
            code[i].operand is MethodInfo method && method.DeclaringType == typeof(CustomAttributeExtensions) &&
            method.Name == nameof(CustomAttributeExtensions.GetCustomAttribute) && method.IsGenericMethod &&
            method.GetGenericArguments().SequenceEqual(new[] { typeof(SavedPropertyAttribute) }) &&
            method.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(MemberInfo)).ToArray();
        if (sites.Length != 1) throw new InvalidOperationException("Native saved-property attribute boundary changed");
        int start = sites[0];
        if (!IsStore(code[start + 1]) || !IsLoad(code[start + 2]) ||
            Local(code[start + 1]) != Local(code[start + 2]) ||
            code[start + 3].opcode != OpCodes.Ldfld || !Equals(code[start + 3].operand, field) ||
            code.Skip(start + 1).Take(3).Any(c => c.labels.Count != 0 || c.blocks.Count != 0) ||
            code.Where((_, i) => i != start + 1 && i != start + 2)
                .Any(c => (IsLoad(c) || IsStore(c)) && Local(c) == Local(code[start + 1])))
            throw new InvalidOperationException("Native saved attribute is no longer consumed only as its fixed condition");
        var replacement = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocalWorkerSnapshotMetadata), nameof(ReadCondition)));
        replacement.labels.AddRange(code[start].labels); replacement.blocks.AddRange(code[start].blocks);
        code.RemoveRange(start, 4); code.Insert(start, replacement);
        return code;
    }

    private static IEnumerable<CodeInstruction> KeywordTable(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var sites = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(KeywordValues)).ToArray();
        if (sites.Length != 1 || sites[0] + 1 >= code.Count || !IsStore(code[sites[0] + 1]))
            throw new InvalidOperationException("Native keyword enumeration boundary changed");
        int local = Local(code[sites[0] + 1]);
        var loads = Enumerable.Range(0, code.Count).Where(i => IsLoad(code[i]) && Local(code[i]) == local).ToArray();
        // The private table may only be indexed or have its length read. Never
        // expose it to a Mod callback or a native path that could mutate it.
        if (loads.Length != 2 || code.Where((_, i) => i != sites[0] + 1).Any(c => IsStore(c) && Local(c) == local) ||
            code.Any(c => (c.opcode == OpCodes.Ldloca || c.opcode == OpCodes.Ldloca_S) && Local(c) == local) ||
            code[loads[0] + 2].opcode != OpCodes.Ldelem_I4 || !IsLoad(code[loads[0] + 1]) ||
            code[loads[1] + 1].opcode != OpCodes.Ldlen)
            throw new InvalidOperationException("Native keyword table is no longer read-only");
        code[sites[0]].operand = AccessTools.Method(typeof(LocalWorkerSnapshotMetadata), nameof(ReadKeywords));
        return code;
    }

    private static bool IsStore(CodeInstruction code) => code.opcode == OpCodes.Stloc || code.opcode == OpCodes.Stloc_S ||
        code.opcode == OpCodes.Stloc_0 || code.opcode == OpCodes.Stloc_1 || code.opcode == OpCodes.Stloc_2 || code.opcode == OpCodes.Stloc_3;
    private static bool IsLoad(CodeInstruction code) => code.opcode == OpCodes.Ldloc || code.opcode == OpCodes.Ldloc_S ||
        code.opcode == OpCodes.Ldloc_0 || code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Ldloc_3;
    private static int Local(CodeInstruction code)
    {
        if (code.opcode == OpCodes.Ldloc_0 || code.opcode == OpCodes.Stloc_0) return 0;
        if (code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Stloc_1) return 1;
        if (code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Stloc_2) return 2;
        if (code.opcode == OpCodes.Ldloc_3 || code.opcode == OpCodes.Stloc_3) return 3;
        return code.operand switch { LocalBuilder b => b.LocalIndex, LocalVariableInfo v => v.LocalIndex,
            int i => i, byte i => i, _ => -1 };
    }
}
