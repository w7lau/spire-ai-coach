using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// A conservative code certificate, NOT an observed-healing estimate or card-name
// table. Only native gameplay callbacks with understood boundaries can establish
// no healing. Generation, arbitrary setters, external code and repeated healing
// leave the ceiling unknown; the native simulator still explores those branches.
internal sealed class LocalRecoveryEstimator(LocalSearchRequest request)
{
    private sealed record Proof(string? Unknown, int VictoryHeals, Type[] References, string? UnknownAt = null);
    private readonly Dictionary<Type, Proof> _proofs = [];
    private static readonly Assembly Native = typeof(AbstractModel).Assembly;
    // Audited command boundaries for the installed v0.111.0 binary. An update
    // must not silently inherit a no-healing certificate from different code.
    private const string CertifiedNativeModule = "73b63ee0-6c0a-47bb-b0d1-b21f6d94222e";
    private static readonly HashSet<string> PureModelMethods = new(StringComparer.Ordinal)
        { "Flash", "HasTag", "AssertMutable", "AssertCanonical", "ApplyUpgrade", "GetPowerAmount", "HasPower", "GetPower" };
    private static readonly HashSet<string> GenerationCommands = new(StringComparer.Ordinal)
        { "Transform", "TransformToRandom", "Generate", "GenerateCard", "GenerateCards", "Obtain", "Replace" };
    private static readonly HashSet<string> HpWrites = new(StringComparer.Ordinal)
        { "HealInternal", "SetCurrentHpInternal", "SetMaxHpInternal", "GainMaxHp", "SetMaxHp", "SetCurrentHp", "SetMaxAndCurrentHp" };
    private static readonly Dictionary<string, HashSet<string>> CommandLeaves = new(StringComparer.Ordinal)
    {
        ["CreatureCmd"] = ["GainBlock", "LoseBlock", "Damage", "Kill", "TriggerAnim"],
        ["DamageCmd"] = ["Attack"],
        ["AttackCommand"] = ["FromCard", "FromCreature", "FromMonster", "Targeting", "TargetingAllOpponents", "WithHitFx",
            "WithHitVfx", "WithHitCount", "WithHits", "WithDamage", "Execute", "WithValueProp", "WithAttackerAnim",
            "WithAttackAnim", "WithSfx", "get_Results"],
        ["CardPileCmd"] = ["Add", "Draw", "Exhaust", "Discard", "Shuffle", "AutoMoveToPlayPile", "MoveToPlayPile",
            "AddGeneratedCardToCombat", "AddGeneratedCardsToCombat"],
        ["PowerCmd"] = ["Apply", "Remove", "Decrement", "DecrementOrRemove", "ModifyAmount", "SetAmount"],
        ["PlayerCmd"] = ["GainEnergy", "LoseEnergy", "GainStars", "LoseStars", "GainGold", "LoseGold", "EndTurn"],
        ["CardCmd"] = ["Upgrade", "Exhaust", "Discard"],
        ["PotionCmd"] = ["Discard", "TryToProcure"]
    };

    public LocalRecoveryAllowance Estimate(Player player)
    {
        using var measuring = LocalWorker.MeasureMethod("LocalRecoveryEstimator.Estimate");
        if (Native.ManifestModule.ModuleVersionId.ToString() != CertifiedNativeModule)
            return new(null, "当前游戏版本的回复边界尚未认证");
        // A Mod can install static/event callbacks outside the model hook list.
        // An unpatched current model alone cannot certify those external effects.
        if (request.LoadedMods.Any(m => !m.StartsWith("sts2:", StringComparison.Ordinal) &&
            !m.StartsWith("SpireAiCoach:", StringComparison.Ordinal)))
            return new(null, "第三方 Mod 的全局效果尚无回复上界证明");
        try
        {
            var state = CombatManager.Instance.DebugOnlyGetState()!;
            var models = player.RunState.IterateHookListeners(state)
                .Concat(new AbstractModel[] { player.Character })
                .Concat(player.PlayerCombatState!.AllPiles.SelectMany(p => p.Cards))
                .Concat(player.Potions).OfType<AbstractModel>().Distinct<AbstractModel>(ReferenceEqualityComparer.Instance).ToArray();
            var pending = new Queue<Type>(models.Select(m => m.GetType()).Distinct());
            var visited = new HashSet<Type>();
            long total = 0;
            while (pending.TryDequeue(out var type))
            {
                if (!visited.Add(type)) continue;
                var proof = Describe(type);
                if (proof.Unknown != null) return new(null, proof.Unknown);
                foreach (var reference in proof.References) pending.Enqueue(reference);
                if (proof.VictoryHeals == 0) continue;
                // Referenced future healing models, rather than current relic
                // instances, have no fixed current variable value to certify.
                var owners = models.OfType<RelicModel>().Where(r => r.GetType() == type).ToArray();
                if (owners.Length == 0) return new(null, "后续可能生成新的回复来源");
                foreach (var owner in owners)
                {
                    decimal gain = Math.Max(0, owner.DynamicVars.Heal.BaseValue) * proof.VictoryHeals;
                    if (gain > long.MaxValue - total) return new(null, "回复上界溢出");
                    total += (long)decimal.Ceiling(gain);
                }
            }
            return new(total);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or OverflowException)
        { return new(null, "回复来源无法完整读取：" + ex.GetType().Name); }
    }

    private Proof Describe(Type type)
    {
        if (_proofs.TryGetValue(type, out var found)) return found;
        string? unknown = null;
        string? boundary = null;
        int heals = 0;
        var references = new HashSet<Type>();
        var seen = new HashSet<(MethodBase Method, bool Victory, bool Direct)>();
        var queue = new Queue<(MethodBase Method, bool Victory, bool Direct)>();
        if (type.Assembly != Native) return _proofs[type] = new("非原生模型的回复上界未知", 0, []);
        for (var owner = type; owner != null && owner.Assembly == Native && owner != typeof(AbstractModel); owner = owner.BaseType)
        {
            if (owner == typeof(CardModel) || owner == typeof(RelicModel) || owner == typeof(PowerModel) ||
                owner == typeof(MonsterModel) || owner == typeof(PotionModel) || owner == typeof(CharacterModel)) break;
            foreach (var method in owner.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName || !method.IsVirtual || method.GetBaseDefinition() == method ||
                    method.Name is "AfterObtained" or "BeforeRemoved" or "AfterRemoved") continue;
                queue.Enqueue((method, method.Name == "AfterCombatVictory", true));
            }
            foreach (var constructor in owner.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                queue.Enqueue((constructor, false, false));
        }
        while (unknown == null && queue.TryDequeue(out var entry))
        {
            var (method, victory, direct) = entry;
            if (!seen.Add(entry)) continue;
            if (seen.Count > 2048) { unknown = "模型效果调用图过大"; break; }
            if (ForeignPatch(method)) { unknown = "模型效果存在外部补丁"; break; }
            var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType ??
                method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
            if (stateMachine != null)
            {
                var move = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (move == null) { unknown = "无法读取异步模型效果"; break; }
                queue.Enqueue((move, victory, direct));
            }
            boundary = method.DeclaringType?.FullName + "." + method.Name;
            var code = LocalMethodBody.Read(method);
            if (code == null) { unknown = "无法证明模型效果的完整调用"; break; }
            for (int i = 0; i < code.Length && unknown == null; i++)
            {
                var instruction = code[i];
                if (instruction.Operand is Type tokenType) Refer(tokenType);
                if (!victory && instruction.Operand is string literal && literal == "Heal")
                { unknown = "回复变量可能在后续改变"; break; }
                if (instruction.Operand is not MethodBase called) continue;
                var declaring = called.DeclaringType!;
                boundary = declaring.FullName + "." + called.Name;
                string ns = declaring.Namespace ?? "";
                if (called.IsGenericMethod) foreach (var argument in called.GetGenericArguments()) Refer(argument);
                if (typeof(AbstractModel).IsAssignableFrom(declaring) && !declaring.IsAbstract) Refer(declaring);
                if (ForeignPatch(called)) { unknown = "效果边界存在外部补丁"; break; }
                if (HpWrites.Contains(called.Name)) { unknown = "存在回复或生命上限变化"; break; }
                if (!victory && called.Name == "get_Heal" && ns == "MegaCrit.Sts2.Core.Localization.DynamicVars")
                { unknown = "回复变量可能在战中使用或改变"; break; }
                if (declaring.FullName == "MegaCrit.Sts2.Core.Commands.CreatureCmd" && called.Name == "Heal")
                {
                    // A fixed native victory callback executes once. Sum all its
                    // possible calls, ignoring conditions (an optimistic ceiling).
                    // Helpers/loops/other hooks cannot inherit that multiplicity.
                    if (!victory || !direct || !typeof(RelicModel).IsAssignableFrom(type) ||
                        LocalMethodBody.HasBackwardJump(code) || !FixedVictoryHeal(code, i, type))
                    { unknown = "战中、重复或动态回复的上界未知"; break; }
                    heals++; continue;
                }
                if (ns.StartsWith("System.Reflection", StringComparison.Ordinal) ||
                    ns.StartsWith("System.IO", StringComparison.Ordinal) || ns == "System.Diagnostics" || called.Name == "Invoke")
                { unknown = "存在动态或外部效果调用"; break; }
                if (declaring == typeof(ModelDb))
                {
                    if (!called.IsGenericMethod || called.GetGenericArguments().Any(t => !typeof(AbstractModel).IsAssignableFrom(t) ||
                        typeof(RelicModel).IsAssignableFrom(t))) unknown = "存在动态模型生成";
                    continue;
                }
                if (declaring.Name.Contains("Pool", StringComparison.Ordinal) &&
                    typeof(AbstractModel).IsAssignableFrom(declaring))
                { unknown = "可能从模型池生成新的回复效果"; break; }
                if (ns.StartsWith("MegaCrit.Sts2.Core.Commands", StringComparison.Ordinal))
                {
                    if (GenerationCommands.Contains(called.Name) || declaring.Name is "RelicCmd" or "CardFactory")
                        unknown = "后续模型生成的回复上界未知";
                    else if (declaring.Name is "SfxCmd" or "VfxCmd" or "CardSelectCmd" ||
                        CommandLeaves.TryGetValue(declaring.Name, out var allowed) && allowed.Contains(called.Name)) continue;
                    else { unknown = "原生效果边界的回复上界尚未认证"; }
                    break;
                }
                if (ns == "System.Runtime.CompilerServices" && declaring.Name is ("Unsafe" or "RuntimeHelpers"))
                { unknown = "存在无法追踪的内存或初始化效果"; break; }
                if (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal)) continue;
                // Native variable/energy-cost setters notify card presentation;
                // upgrade hooks are independently included in the model closure.
                // A reference to healing variables outside the fixed callback is
                // never covered by this scalar-only boundary.
                if (ns == "MegaCrit.Sts2.Core.Localization.DynamicVars" ||
                    declaring.FullName == "MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost") continue;
                if (typeof(AbstractModel).IsAssignableFrom(declaring))
                {
                    if (called.IsSpecialName && called.Name.StartsWith("get_", StringComparison.Ordinal) ||
                        PureModelMethods.Contains(called.Name) || called.IsConstructor) continue;
                    if (called.Name == "AfterCombatVictory") { unknown = "胜利回复回调可能重复调用"; break; }
                }
                if (declaring.Assembly != Native) { unknown = "非原生效果调用"; break; }
                // Read-only entity access and native hook dispatch retain the
                // model callbacks already in the certificate's closed model set.
                if (called.IsSpecialName && called.Name.StartsWith("get_", StringComparison.Ordinal) &&
                    (ns.StartsWith("MegaCrit.Sts2.Core.Entities", StringComparison.Ordinal) ||
                     ns.StartsWith("MegaCrit.Sts2.Core.Combat", StringComparison.Ordinal) ||
                     ns.StartsWith("MegaCrit.Sts2.Core.Runs", StringComparison.Ordinal)) ||
                    declaring.FullName == "MegaCrit.Sts2.Core.Hooks.Hook" ||
                    PureModelMethods.Contains(called.Name) ||
                    ns.StartsWith("MegaCrit.Sts2.Core.MonsterMoves", StringComparison.Ordinal) && called.IsConstructor) continue;
                queue.Enqueue((called, victory, false));
            }
        }
        return _proofs[type] = new(unknown, heals, references.ToArray(), unknown == null ? null : boundary);

        void Refer(Type target)
        {
            if (typeof(AbstractModel).IsAssignableFrom(target) && !target.IsAbstract) references.Add(target);
        }
    }

    private static bool ForeignPatch(MethodBase method) => Harmony.GetPatchInfo(method)?.Owners.Any(o =>
        !o.StartsWith("SpireAiCoach", StringComparison.Ordinal)) == true;

    private static bool FixedVictoryHeal(LocalInstruction[] code, int call, Type owner)
    {
        // Require the exact native expression this.DynamicVars.Heal.BaseValue.
        // No arbitrary getter evaluation, card IDs or localized descriptions.
        if (call < 5 || code[call - 1].Code != System.Reflection.Emit.OpCodes.Ldc_I4_1) return false;
        if (code[call - 2].Operand is not MethodInfo value || value.Name != "get_BaseValue" ||
            code[call - 3].Operand is not MethodInfo heal || heal.Name != "get_Heal" ||
            code[call - 4].Operand is not MethodInfo vars || vars.Name != "get_DynamicVars") return false;
        var source = code[call - 5];
        if (source.Operand is FieldInfo field && field.FieldType == owner ||
            source.Code == System.Reflection.Emit.OpCodes.Ldarg_0) return true;
        int? local = LocalIndex(source, "ldloc");
        if (local == null) return false;
        var assignments = Enumerable.Range(1, code.Length - 1)
            .Where(i => LocalIndex(code[i], "stloc") == local).ToArray();
        return assignments.Length == 1 && code[assignments[0] - 1].Operand is FieldInfo captured &&
            captured.FieldType == owner && captured.Name == "<>4__this";
    }

    private static int? LocalIndex(LocalInstruction instruction, string prefix)
    {
        string? name = instruction.Code.Name;
        if (name == prefix || name == prefix + ".s") return Convert.ToInt32(instruction.Operand);
        return name != null && name.StartsWith(prefix + ".", StringComparison.Ordinal) &&
            int.TryParse(name[(prefix.Length + 1)..], out int index) ? index : null;
    }
}
