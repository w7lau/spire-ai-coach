using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Read the player's effect-source closure once. Unrelated global Mods do not
// invalidate the chosen domain; unresolved calls inside a held source do.
internal sealed class LocalRecoveryEstimator(LocalSearchRequest request)
{
    private sealed record Proof(string? Unknown, int VictoryHeals, Type[] References, string? UnknownAt = null,
        int VictoryMaxHpGains = 0);
    private readonly Dictionary<Type, Proof> _proofs = [];
    private readonly Dictionary<MethodBase, LocalInstruction[]?> _bodies = [];
    private sealed record ContentEffects(bool ActiveRecovery, int VictoryHeals, int VictoryMaxHpGains,
        bool Uncertain, Type[] Generated, bool DynamicMaxHp = false, bool UsesCardPool = false, string? UncertainAt = null,
        bool MayChangeDeck = false, bool UsesCharacterPool = false);
    private readonly Dictionary<Type, ContentEffects> _content = [];
    private readonly Dictionary<Type, ContentEffects> _targetContent = [];
    private readonly Dictionary<Type, ContentEffects> _deckContent = [];
    private readonly Dictionary<(MethodBase Method, MethodInfo? Event, string Input), LocalIlFacts.Result> _eventFacts = [];
    private LocalRecoveryAllowance? _allowance;
    private Type[] _sourceTypes = [];
    private int? _nextPlayerTurnNumber;
    private bool _combatPileEventsOnly;
    private string? _deckCapabilityReason;
    public LocalHealthTarget? HealthTarget { get; private set; }
    public string? SharedDirectory { get; set; }
    public int TargetAnalysisCount { get; private set; }
    public double TargetAnalysisElapsedMs { get; private set; }
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

    // One closure per frozen search root. Fixed gains and repeatable healing
    // caps are reused throughout the branches, never rescanned after a move.
    public int AnalysisCount { get; private set; }
    public double AnalysisElapsedMs { get; private set; }
    public int MethodBodyReads => _bodies.Count;
    public bool ContentScoped => _allowance?.ContentScoped == true;
    public void ValidateObserved(LocalHealthChanges health)
    {
        if (_allowance is not { ContentScoped: true } allowance) return;
        bool outside = allowance.MaximumFinalHp is { } cap ? health.FinalHp > cap || health.FinalMaxHp > cap :
            allowance.MaximumFurtherHpGain is { } gain && (health.HpGained > gain || health.MaxHpGained > gain);
        if (outside) throw new CoachException("local_recovery_bound",
            "原生结算出现当前内容边界之外的回血或生命上限增加，未采用该边界的计算结果。");
    }
    public LocalRecoveryAllowance Estimate(Player player)
    {
        if (_allowance != null) return _allowance;
        long started = Stopwatch.GetTimestamp();
        AnalysisCount++;
        try { return _allowance = Analyze(player); }
        finally { AnalysisElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    private LocalRecoveryAllowance Analyze(Player player)
    {
        using var measuring = LocalWorker.MeasureMethod("LocalRecoveryEstimator.Estimate");
        if (Native.ManifestModule.ModuleVersionId.ToString() != CertifiedNativeModule)
            return new(null, "当前游戏版本的回复边界尚未认证");
        try
        {
            AbstractModel[]? models = player == null ? null : CurrentModels(player);
            // Search roots are already in the player's ready-to-play phase.
            // Turn-start callbacks can next run only on a later player turn.
            if (player?.PlayerCombatState?.TurnNumber is > 0 and < int.MaxValue)
                _nextPlayerTurnNumber = player.PlayerCombatState.TurnNumber + 1;
            if (player != null) _sourceTypes = PlayerSources(player, models!).Select(m => m.GetType()).Distinct().ToArray();
            if (player != null && request.StopOnZeroLoss && !request.StopOnFirstWin &&
                (!LocalSearchPolicy.HasSpecificGoal(request) || request.CardGoals is { Enabled: true, HpLossThreshold: null }))
            {
                LocalHealthTarget CalculateTarget()
                {
                    long started = Stopwatch.GetTimestamp(); TargetAnalysisCount++;
                    try
                    {
                        _combatPileEventsOnly = !CanChangeDeck(player, PlayerSources(player, models!));
                        return ContentTarget(player, models!);
                    }
                    finally { TargetAnalysisElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
                }
                HealthTarget = SharedDirectory == null ? CalculateTarget() :
                    LocalHealthTargetCache.Get(SharedDirectory, request, player.Creature.CurrentHp, CalculateTarget);
            }
            if (player == null) return new(null, "无法读取当前玩家内容");
            models ??= CurrentModels(player);
            var held = PlayerSources(player, models);
            var pending = new Queue<Type>(held.Select(m => m.GetType()).Distinct());
            var visited = new HashSet<Type>();
            decimal heal = 0, maxHp = 0;
            bool active = false, growth = false, poolRead = false;
            while (pending.TryDequeue(out var type))
            {
                if (!visited.Add(type)) continue;
                if (visited.Count > 256) return new(null, "当前内容生成的回复来源过多", ContentScoped: true);
                if (typeof(CardPoolModel).IsAssignableFrom(type))
                {
                    var pool = ModelDb.AllCardPools.FirstOrDefault(p => p.GetType() == type);
                    if (pool == null) return new(null, "当前内容引用的生成牌池无法读取", ContentScoped: true);
                    foreach (var card in pool.AllCards) pending.Enqueue(card.GetType());
                    continue;
                }
                var effect = DescribeContent(type);
                if (effect.Uncertain)
                {
                    // The current-content target is a user-selected estimate.
                    // An unknown ceiling cannot replace it with another target.
                    return new(null, "当前玩家效果的辅助调用尚未完整解析：" + (effect.UncertainAt ?? type.FullName), ContentScoped: true);
                }
                active |= effect.ActiveRecovery; growth |= effect.DynamicMaxHp;
                foreach (var reference in effect.Generated) pending.Enqueue(reference);
                _sourceTypes = _sourceTypes.Concat(effect.Generated).Distinct().ToArray();
                if (effect.UsesCardPool && !poolRead)
                {
                    poolRead = true;
                    // Superset of the character's unlocked offers. No selection,
                    // generation or RNG is executed while reading canonical data.
                    foreach (var card in player.Character.CardPool.AllCards) pending.Enqueue(card.GetType());
                }
                var owners = held.OfType<RelicModel>().Where(r => r.GetType() == type).ToArray();
                if (owners.Length == 0 && (effect.VictoryHeals > 0 || effect.VictoryMaxHpGains > 0))
                    return new(null, "后续可能生成新的战后回复来源", ContentScoped: true);
                foreach (var owner in owners)
                {
                    if (effect.VictoryHeals > 0) heal += Math.Max(0, owner.DynamicVars.Heal.BaseValue) * effect.VictoryHeals;
                    if (effect.VictoryMaxHpGains > 0) maxHp += Math.Max(0, owner.DynamicVars.MaxHp.BaseValue) * effect.VictoryMaxHpGains;
                }
            }
            if (growth) return new(null, "当前玩家内容存在可重复或动态生命上限增加", ContentScoped: true);
            if (heal + maxHp > long.MaxValue || player.Creature.MaxHp + maxHp > int.MaxValue)
                return new(null, "当前内容回复上界溢出", ContentScoped: true);
            // This is the user-selected player-content domain. Unrelated global
            // Mod rewrites do not disable it; results carry this narrower scope.
            return active ? new(null, "按当前玩家内容的生命上限判断重复回血", ContentScoped: true,
                MaximumFinalHp: (int)decimal.Ceiling(player.Creature.MaxHp + maxHp)) :
                new((long)decimal.Ceiling(heal + maxHp), "按当前玩家内容的固定回复判断", ContentScoped: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or OverflowException or
            ReflectionTypeLoadException or TypeLoadException or AmbiguousMatchException)
        { return new(null, "回复来源无法完整读取：" + ex.GetType().Name); }
    }

    private static AbstractModel[] CurrentModels(Player player) =>
        player.RunState.IterateHookListeners(CombatManager.Instance.DebugOnlyGetState()!)
            .Concat(new AbstractModel[] { player.Character })
            .Concat(player.PlayerCombatState!.AllPiles.SelectMany(p => p.Cards)).Concat(player.Potions)
            .OfType<AbstractModel>().Distinct<AbstractModel>(ReferenceEqualityComparer.Instance).ToArray();

    private static AbstractModel[] PlayerSources(Player player, IEnumerable<AbstractModel> models) =>
        models.Concat(player.PlayerCombatState!.AllPiles.SelectMany(p => p.Cards)
            .SelectMany(c => new AbstractModel?[] { c.Enchantment, c.Affliction }).OfType<AbstractModel>())
        .Where(m => m switch {
            CardModel card => card.Owner == player,
            RelicModel relic => relic.Owner == player,
            PotionModel potion => potion.Owner == player,
            PowerModel power => power.Owner == player.Creature,
            EnchantmentModel enchantment => enchantment.HasCard && enchantment.Card.Owner == player,
            AfflictionModel affliction => affliction.HasCard && affliction.Card.Owner == player,
            CharacterModel character => character == player.Character,
            _ => false }).Distinct<AbstractModel>(ReferenceEqualityComparer.Instance).ToArray();

    private LocalHealthTarget ContentTarget(Player player, AbstractModel[] models)
    {
        // The chosen policy estimates a goal from held content, even when global
        // Mod effects prevent a ceiling proof. Never feed this goal to Envelope.
        decimal heal = 0, maxHp = 0;
        bool limited = false;
        bool active = false, uncertain = request.LoadedMods.Any(m => !m.StartsWith("sts2:", StringComparison.Ordinal) &&
            !m.StartsWith("SpireAiCoach:", StringComparison.Ordinal));
        // Global modifiers also touch enemy HP. The user's policy is a goal
        // inferred from held PLAYER content; unresolved global effects remain
        // uncertain instead of masquerading as an owned healing source.
        var held = PlayerSources(player, models);
        var pending = new Queue<Type>(held.Select(m => m.GetType()).Distinct());
        var seen = new HashSet<Type>();
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type)) continue;
            // A bounded estimate can be incomplete without discovering a
            // healing effect. Keep the known goal and its uncertainty; only
            // actual HP calls may turn it into a full-health goal.
            if (seen.Count > 128) { limited = uncertain = true; break; }
            var effect = ReadContent(type, targetOnly: true);
            active |= effect.ActiveRecovery; uncertain |= effect.Uncertain;
            foreach (var generated in effect.Generated) pending.Enqueue(generated);
            var owners = held.OfType<RelicModel>().Where(r => r.GetType() == type).ToArray();
            if (owners.Length == 0 && (effect.VictoryHeals > 0 || effect.VictoryMaxHpGains > 0))
                active = true;
            foreach (var owner in owners)
            {
                if (effect.VictoryHeals > 0) heal += Math.Max(0, owner.DynamicVars.Heal.BaseValue) * effect.VictoryHeals;
                if (effect.VictoryMaxHpGains > 0) maxHp += Math.Max(0, owner.DynamicVars.MaxHp.BaseValue) * effect.VictoryMaxHpGains;
            }
        }
        int start = player.Creature.CurrentHp;
        decimal goal = Math.Min(player.Creature.MaxHp + maxHp, start + heal + maxHp);
        if (goal > int.MaxValue) { active = uncertain = true; goal = start; }
        return new(LocalMinimumLossProof.Scope(request), start, (int)decimal.Ceiling(goal), active,
            limited ? active ? "检测到生命恢复，目标为战后满血；内容分析触及数量上限" :
                "内容分析触及数量上限，按已识别生命效果估计返回目标" :
            active ? "检测到战中或动态生命恢复，目标为战后满血" : heal + maxHp > 0 ?
                "当前内容的固定战后生命增加" : "当前内容未检测到生命恢复调用，目标为不净损血", uncertain);
    }

    // Small current-content scan. Read only concrete model callbacks, their own
    // helpers and explicitly generated cards/powers. HasRelic/GetRelic references
    // do not activate absent relics. Unresolved calls remain an estimate, not a
    // claim that the player cannot possibly heal.
    private ContentEffects DescribeContent(Type type) => ReadContent(type, targetOnly: false);
    private ContentEffects ReadContent(Type type, bool targetOnly) => ReadEffects(type, targetOnly, combatDeckScan: false);
    private ContentEffects ReadEffects(Type type, bool targetOnly, bool combatDeckScan)
    {
        var cache = combatDeckScan ? _deckContent : targetOnly ? _targetContent : _content;
        if (cache.TryGetValue(type, out var cached)) return cached;
        bool active = false, uncertain = false, growth = false, pool = false, changesDeck = false, characterPool = false;
        string? uncertainAt = null;
        void Unknown(MethodBase method, MethodBase? caller = null)
        { uncertain = true; uncertainAt ??= (caller == null ? "" : caller.DeclaringType?.FullName + "." + caller.Name + " -> ") +
            method.DeclaringType?.FullName + "." + method.Name; }
        int heals = 0, gains = 0;
        var generated = new HashSet<Type>();
        var queue = new Queue<(MethodBase Method, bool Terminal, bool Direct, bool FutureTurnStart, MethodInfo? PileEvent, LocalIlFacts.Value[]? Input)>();
        var overrides = new HashSet<MethodInfo>();
        for (var owner = type; owner != null && owner != typeof(AbstractModel); owner = owner.BaseType)
        {
            if (owner == typeof(CardModel) || owner == typeof(RelicModel) || owner == typeof(PowerModel) ||
                owner == typeof(PotionModel) || owner == typeof(CharacterModel)) break;
            foreach (var method in owner.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                // Some powers expose a native effect entry point called by their
                // owning model instead of a Hook override (e.g. resurrection).
                if (!method.IsSpecialName && !method.IsVirtual && method.IsPublic)
                { queue.Enqueue((method, false, false, false, null, null)); continue; }
                if (!request.IncludePotions && typeof(PotionModel).IsAssignableFrom(type) && method.Name == "OnUse") continue;
                if (method.IsSpecialName || !method.IsVirtual || method.GetBaseDefinition() == method ||
                    !overrides.Add(method.GetBaseDefinition()) || method.Name is "AfterObtained" or "BeforeRemoved" or "AfterRemoved") continue;
                // Room-entry effects happen after this combat. Keep them in the
                // strict recovery closure; they cannot produce an in-combat pile event.
                if (combatDeckScan && method.DeclaringType?.Assembly == Native &&
                    method.GetBaseDefinition().DeclaringType == typeof(AbstractModel) && method.Name is "BeforeRoomEntered" or "AfterRoomEntered") continue;
                bool futureStart = method.DeclaringType?.Assembly == Native &&
                    method.GetBaseDefinition().DeclaringType == typeof(AbstractModel) &&
                    method.Name is "AfterPlayerTurnStart" or "AfterPlayerTurnStartEarly" or "AfterPlayerTurnStartLate";
                var pileEvent = method.GetBaseDefinition().DeclaringType == typeof(AbstractModel) &&
                    method.Name == "AfterCardChangedPiles" ? method : null;
                queue.Enqueue((method, method.Name is "AfterCombatVictory" or "AfterCombatEnd", true, futureStart, pileEvent, null));
            }
        }
        var seen = new HashSet<(MethodBase, bool, bool, bool, MethodInfo?, string)>();
        while (queue.TryDequeue(out var entry))
        {
            if (!seen.Add((entry.Method, entry.Terminal, entry.Direct, entry.FutureTurnStart, entry.PileEvent, InputKey(entry.Input)))) continue;
            var sourceOwner = entry.Method.DeclaringType;
            while (sourceOwner?.DeclaringType != null) sourceOwner = sourceOwner.DeclaringType;
            if (sourceOwner != null && typeof(RelicModel).IsAssignableFrom(sourceOwner) &&
                !_sourceTypes.Any(sourceOwner.IsAssignableFrom) && sourceOwner != type &&
                (entry.Method.Name.StartsWith("After", StringComparison.Ordinal) ||
                 entry.Method.Name.StartsWith("Before", StringComparison.Ordinal) ||
                 entry.Method.DeclaringType != sourceOwner)) continue;
            if (seen.Count > 512) { Unknown(entry.Method); break; }
            var (method, terminal, direct, futureTurnStart, pileEvent, input) = entry;
            var patches = Harmony.GetPatchInfo(method);
            if (patches != null)
                foreach (var patch in patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Finalizers).Concat(patches.Transpilers)
                    .Where(p => !OurPatch(p.owner)))
                { futureTurnStart = false; pileEvent = null; input = null; queue.Enqueue((patch.PatchMethod, false, false, false, null, null)); }
            var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType ??
                method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
            if (state?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } move)
                queue.Enqueue((move, terminal, direct, futureTurnStart, pileEvent, input));
            var code = Body(method);
            if (code == null) { Unknown(method); continue; }
            var reachable = futureTurnStart && method.DeclaringType?.Assembly == Native && _nextPlayerTurnNumber is { } nextTurn ?
                FutureTurnInstructions(method, code, nextTurn) : null;
            var facts = EventFacts(method, code, pileEvent, input);
            bool combatBatch = CombatTransformationBatch(method, code, facts);
            if (method is MethodInfo modifier && modifier.Name.StartsWith("ModifyCardPlayResult", StringComparison.Ordinal) &&
                (modifier.ReturnType == typeof(CardLocation) || modifier.ReturnType.IsGenericType &&
                 modifier.ReturnType.GetGenericTypeDefinition() == typeof(ValueTuple<,>) && modifier.ReturnType.GetGenericArguments()[0] == typeof(PileType)))
                changesDeck |= !facts.Complete || facts.Return.Tag is not ("play-result" or "pile-result") ||
                    facts.Return.Numbers == null || facts.Return.Numbers.Any(v => v == (int)PileType.Deck);
            if (targetOnly && _combatPileEventsOnly && pileEvent != null && method.DeclaringType?.Assembly == Native && facts.Complete)
                reachable = facts.Reachable;
            for (int i = 0; i < code.Length; i++)
            {
                if (reachable != null && !reachable[i]) continue;
                if (code[i].Operand is FieldInfo written && written.FieldType == typeof(PileType) &&
                    (code[i].Code == OpCodes.Stfld || code[i].Code == OpCodes.Stsfld) &&
                    written.DeclaringType?.IsDefined(typeof(CompilerGeneratedAttribute), false) != true)
                {
                    var values = facts.Complete && facts.Writes.TryGetValue(i, out var value) ? value.Numbers : null;
                    changesDeck |= values == null || values.Any(v => v == (int)PileType.Deck);
                }
                if (code[i].Code == OpCodes.Stobj && code[i].Operand is Type writtenType &&
                    writtenType.Assembly == Native && writtenType.Name == "CardLocation")
                {
                    var value = facts.Complete && facts.Writes.TryGetValue(i, out var writtenValue) ? writtenValue : LocalIlFacts.Value.Unknown;
                    changesDeck |= value.Tag != "play-result" || value.Numbers == null || value.Numbers.Any(v => v == (int)PileType.Deck);
                }
                // Relevant patch builders can name the HP API they insert.
                // Arbitrary display patch names do not invalidate every source.
                if (method.DeclaringType?.Assembly != Native && code[i].Operand is string api &&
                    (HpWrites.Contains(api) || api is "Heal" or "set_CurrentHp" or "set_MaxHp"))
                { active = true; growth |= api.Contains("MaxHp", StringComparison.Ordinal); }
                if (code[i].Operand is FieldInfo field && field.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Entities.Creatures.Creature" &&
                    field.Name is "_currentHp" or "_maxHp" && code[i].Code is var op &&
                    (op == System.Reflection.Emit.OpCodes.Stfld || op == System.Reflection.Emit.OpCodes.Ldflda))
                { active = true; growth |= field.Name == "_maxHp"; }
                if (code[i].Operand is not MethodBase called) continue;
                var declaring = called.DeclaringType!;
                var arguments = facts.Complete && facts.Calls.TryGetValue(i, out var atCall) ? atCall : null;
                changesDeck |= !(combatBatch && called.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Commands.CardCmd" &&
                    called.Name == "Transform" && arguments?.FirstOrDefault()?.Tag == "local-transformations") && CallCanChangeDeck(called, arguments);
                // Native command rewrites are global Mod behavior, outside the
                // selected held-player-source domain. Patches on the concrete
                // source callbacks/helpers are read when those methods are queued.
                if (declaring.FullName == "MegaCrit.Sts2.Core.Commands.CreatureCmd" && called.Name is "Heal" or "GainMaxHp")
                {
                    bool gain = called.Name == "GainMaxHp";
                    if (terminal && direct && typeof(RelicModel).IsAssignableFrom(type) && !LocalMethodBody.HasBackwardJump(code) &&
                        FixedVictoryValue(code, i, type, gain ? "get_MaxHp" : "get_Heal", !gain))
                    { if (gain) gains++; else heals++; }
                    else { active = true; growth |= gain; }
                    continue;
                }
                if (declaring.Namespace?.StartsWith("MegaCrit.Sts2.Core", StringComparison.Ordinal) == true &&
                    (HpWrites.Contains(called.Name) || called.Name is "set_CurrentHp" or "set_MaxHp"))
                { active = true; growth |= called.Name.Contains("MaxHp", StringComparison.Ordinal); }
                if (typeof(CardPoolModel).IsAssignableFrom(declaring) || called.Name == "get_CardPool") pool = true;
                if (called.Name == "get_CardPool" && typeof(CharacterModel).IsAssignableFrom(declaring)) characterPool = true;
                if (called.IsGenericMethod && (declaring == typeof(ModelDb) || declaring.FullName == "MegaCrit.Sts2.Core.Commands.PowerCmd" && called.Name == "Apply"))
                    foreach (var argument in called.GetGenericArguments())
                        if (!argument.IsAbstract && (typeof(CardModel).IsAssignableFrom(argument) || typeof(PowerModel).IsAssignableFrom(argument) ||
                            typeof(CardPoolModel).IsAssignableFrom(argument)))
                            generated.Add(argument);
                // Core commands/entities are boundaries; e.g. healing a summoned
                // ally does not create a new held player's recovery source.
                if (declaring.Assembly == Native && !typeof(AbstractModel).IsAssignableFrom(declaring)) continue;
                if (PureModelMethods.Contains(called.Name) && declaring.Assembly == Native) continue;
                if (declaring.Assembly == Native && (called.IsSpecialName || called.IsConstructor)) continue;
                // A queried but absent relic cannot receive an instance callback.
                // Static helpers remain part of the calling source's code closure.
                if (!called.IsStatic && typeof(RelicModel).IsAssignableFrom(declaring) &&
                    !_sourceTypes.Any(declaring.IsAssignableFrom)) continue;
                bool own = false;
                for (var parent = declaring; parent != null; parent = parent.DeclaringType)
                    if (parent == type || parent.IsAssignableFrom(type) && parent != typeof(object) && parent != typeof(AbstractModel) &&
                        parent != typeof(CardModel) && parent != typeof(RelicModel) && parent != typeof(PowerModel) &&
                        parent != typeof(PotionModel) && parent != typeof(CharacterModel)) { own = true; break; }
                bool framework = declaring.Assembly == typeof(object).Assembly ||
                    declaring.Assembly.GetName().Name is { } assemblyName &&
                    (assemblyName.StartsWith("System.", StringComparison.Ordinal) || assemblyName == "GodotSharp");
                if (declaring.Namespace?.StartsWith("System.Reflection", StringComparison.Ordinal) == true &&
                    (called.Name is "Invoke" or "SetValue" or "CreateDelegate" ||
                     called.Name == "GetValue" && !typeof(FieldInfo).IsAssignableFrom(declaring))) Unknown(called, method);
                if (own || declaring.Assembly != Native && !framework)
                {
                    if (called is MethodInfo virtualCall && virtualCall.IsVirtual && !virtualCall.IsFinal &&
                        (!own || virtualCall.IsAbstract))
                    {
                        // A held source calling its own abstract base dispatches
                        // to its concrete override. The abstract slot has no IL.
                        var candidates = own ? new[] { type } :
                            declaring.Namespace?.StartsWith("BaseLib.Utils.Patching", StringComparison.Ordinal) == true ?
                            declaring.Assembly.GetTypes().Where(t => !t.IsAbstract && declaring.IsAssignableFrom(t)) :
                            _sourceTypes.Where(declaring.IsAssignableFrom);
                        var implementations = candidates.SelectMany(t =>
                            declaring.IsInterface ? t.GetInterfaceMap(declaring).TargetMethods :
                            t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                .Where(m => m.GetBaseDefinition() == virtualCall.GetBaseDefinition())).Distinct().ToArray();
                        foreach (var implementation in implementations) queue.Enqueue((implementation, terminal, false, false, null, arguments));
                        if (implementations.Length == 0 && (own || !typeof(AbstractModel).IsAssignableFrom(declaring))) Unknown(called);
                        if (!called.IsAbstract) queue.Enqueue((called, terminal, false, false, null, arguments));
                    }
                    else queue.Enqueue((called, terminal, false, false, null, arguments));
                }
            }
        }
        return cache[type] = new(active, heals, gains, uncertain, generated.ToArray(), growth, pool, uncertainAt, changesDeck, characterPool);
    }

    private bool CanChangeDeck(Player player, AbstractModel[] held)
    {
        var pending = new Queue<Type>(held.Select(m => m.GetType()).Distinct());
        var seen = new HashSet<Type>(); bool characterPool = false;
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type)) continue;
            if (seen.Count > 256) return true;
            if (typeof(CardPoolModel).IsAssignableFrom(type))
            {
                var pool = ModelDb.AllCardPools.FirstOrDefault(p => p.GetType() == type);
                if (pool == null) return true;
                foreach (var card in pool.AllCards) pending.Enqueue(card.GetType());
                continue;
            }
            var effect = ReadEffects(type, targetOnly: false, combatDeckScan: true);
            if (effect.Uncertain || effect.MayChangeDeck)
            { _deckCapabilityReason = type.FullName + (effect.Uncertain ? ": " + effect.UncertainAt : ": permanent deck may change"); return true; }
            foreach (var generated in effect.Generated) pending.Enqueue(generated);
            if (effect.UsesCharacterPool && !characterPool)
            {
                characterPool = true;
                foreach (var card in player.Character.CardPool.AllCards) pending.Enqueue(card.GetType());
            }
        }
        return false;
    }

    private static string InputKey(LocalIlFacts.Value[]? input) => input == null ? "root" :
        string.Join(";", input.Select(v => (v.Tag ?? "?") + ":" + (v.Numbers == null ? "?" : string.Join(",", v.Numbers))));

    private LocalIlFacts.Result EventFacts(MethodBase method, LocalInstruction[] code, MethodInfo? pileEvent, LocalIlFacts.Value[]? input = null)
    {
        var key = (method, pileEvent, InputKey(input));
        if (_eventFacts.TryGetValue(key, out var cached)) return cached;
        var asyncOwner = method.DeclaringType?.DeclaringType?.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType == method.DeclaringType);
        bool NoForeignPatch(MethodBase inspected)
        {
            var patches = Harmony.GetPatchInfo(inspected);
            return patches == null || !patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers)
                .Concat(patches.Finalizers).Any(p => !OurPatch(p.owner));
        }
        var root = pileEvent ?? asyncOwner ?? method;
        var wrapper = asyncOwner == null ? null : Body(asyncOwner);
        var stateWrites = wrapper?.Select((i, n) => (i, n)).Where(p => p.i.Code == OpCodes.Stfld &&
            p.i.Operand is FieldInfo f && f.DeclaringType == method.DeclaringType && f.Name == "<>1__state").ToArray();
        bool completionPath = asyncOwner != null && NoForeignPatch(method) &&
            method.DeclaringType!.IsDefined(typeof(CompilerGeneratedAttribute), false) && stateWrites is { Length: 1 } &&
            stateWrites[0].n > 0 && LocalIlFacts.Integer(wrapper![stateWrites[0].n - 1]) == -1 &&
            wrapper.Any(i => i.Operand is MethodInfo m && m.DeclaringType?.Namespace == "System.Runtime.CompilerServices" &&
                m.DeclaringType.Name.StartsWith("AsyncTaskMethodBuilder", StringComparison.Ordinal) && m.Name == "Start" &&
                m.IsGenericMethod && m.GetGenericArguments().Contains(method.DeclaringType));
        bool nativePlay = completionPath && asyncOwner?.DeclaringType?.Assembly == Native && NoForeignPatch(asyncOwner);
        var parameters = root.GetParameters();
        bool pileModifier = root.Name.StartsWith("ModifyCardPlayResult", StringComparison.Ordinal) ||
            root.Name.StartsWith("AfterModifyingCardPlayResult", StringComparison.Ordinal);
        LocalIlFacts.Value Argument(int index)
        {
            if (!method.IsStatic && index == 0) return new(Tag: "frame");
            int n = index - (method.IsStatic ? 0 : 1);
            if (method != root || n < 0 || n >= parameters.Length) return LocalIlFacts.Value.Unknown;
            if (input != null && index < input.Length) return parameters[n].ParameterType.IsByRef ? LocalIlFacts.Value.Unknown : input[index];
            var parameterType = parameters[n].ParameterType.IsByRef ? parameters[n].ParameterType.GetElementType() : parameters[n].ParameterType;
            if (pileModifier && parameterType?.Assembly == Native && parameterType.Name == "CardLocation")
                return new([0, 1, 2, 3, 4, 5], "play-result");
            return parameters[n].ParameterType == typeof(CardModel) && pileEvent != null ? new(Tag: "event-card") :
                parameters[n].ParameterType == typeof(PileType) && (pileEvent != null || pileModifier) ? new([0, 1, 2, 3, 4, 5]) : LocalIlFacts.Value.Unknown;
        }
        LocalIlFacts.Value Field(FieldInfo member, LocalIlFacts.Value receiver)
        {
            if (completionPath && receiver.Tag == "frame" && member.DeclaringType == method.DeclaringType && member.Name == "<>1__state")
                return LocalIlFacts.Value.Number(-1);
            if (nativePlay && asyncOwner?.Name == "OnPlay" && receiver.Tag == "frame" && member.Name == "<>4__this" &&
                typeof(CardModel).IsAssignableFrom(member.FieldType)) return new(Tag: "combat-card");
            if (receiver.Tag == "frame" && member.DeclaringType == method.DeclaringType &&
                parameters.Any(p => p.Name == member.Name && p.ParameterType == member.FieldType))
            {
                int n = Array.FindIndex(parameters, p => p.Name == member.Name && p.ParameterType == member.FieldType) + (root.IsStatic ? 0 : 1);
                if (completionPath && input != null && n < input.Length) return input[n];
                if (member.FieldType == typeof(CardModel) && pileEvent != null) return new(Tag: "event-card");
                if (member.FieldType == typeof(PileType) && (pileEvent != null || pileModifier)) return new([0, 1, 2, 3, 4, 5]);
            }
            if (receiver.Tag is "pile-result" or "play-result" && member.FieldType == typeof(PileType))
                return new(receiver.Numbers);
            return LocalIlFacts.Value.Unknown;
        }
        LocalIlFacts.Value Call(MethodBase called, LocalIlFacts.Value[] args)
        {
            if (completionPath && called.DeclaringType?.Namespace == "System.Runtime.CompilerServices" &&
                called.DeclaringType.Name.StartsWith("TaskAwaiter", StringComparison.Ordinal) && called.Name == "get_IsCompleted")
                return LocalIlFacts.Value.Number(1); // Analyze the equivalent post-completion path; handlers remain independent.
            if (called.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Commands.CardSelectCmd" && called.Name == "FromHand")
                return new(Tag: "combat-selection-task");
            if (called.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Commands.CardSelectCmd" && called.Name == "FromCombatPile")
            {
                int n = Array.FindIndex(called.GetParameters(), p => p.ParameterType == typeof(CardPile));
                if (n >= 0 && n < args.Length && args[n].Tag == "combat-pile") return new(Tag: "combat-selection-task");
            }
            if (called.Name == "GetAwaiter" && args.FirstOrDefault()?.Tag == "combat-selection-task") return new(Tag: "combat-selection-await");
            if (called.Name == "GetResult" && args.FirstOrDefault()?.Tag == "combat-selection-await") return new(Tag: "combat-cards");
            if (called.DeclaringType == typeof(Enumerable) && called.Name is "First" or "FirstOrDefault" && args.FirstOrDefault()?.Tag == "combat-cards")
                return new(Tag: "combat-card");
            if (called.DeclaringType == typeof(Enumerable) && called.Name is "ToList" or "ToArray" && args.FirstOrDefault()?.Tag == "combat-cards")
                return new(Tag: "combat-cards");
            if (called.DeclaringType == typeof(Enumerable) && called.Name == "Where" && args.FirstOrDefault()?.Tag == "combat-cards")
                return new(Tag: "combat-cards");
            if (called.DeclaringType?.Namespace == "System.Collections.Generic" && called.Name == "GetEnumerator" &&
                args.FirstOrDefault()?.Tag == "combat-cards") return new(Tag: "combat-card-enumerator");
            if (called.DeclaringType?.Namespace == "System.Collections.Generic" && called.Name == "get_Current" &&
                args.FirstOrDefault()?.Tag == "combat-card-enumerator") return new(Tag: "combat-card");
            if (called.DeclaringType?.Namespace == "System.Collections.Generic" && called.Name == "get_Item" &&
                args.FirstOrDefault()?.Tag == "combat-cards") return new(Tag: "combat-card");
            if (called.IsConstructor && called.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Entities.Cards.CardTransformation" &&
                args.FirstOrDefault()?.Tag == "combat-card") return new(Tag: "combat-transformation");
            if (called.IsConstructor && called.GetParameters().Length == 0 && called.DeclaringType?.IsGenericType == true &&
                called.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                called.DeclaringType.GetGenericArguments()[0].FullName == "MegaCrit.Sts2.Core.Entities.Cards.CardTransformation")
                return new(Tag: "local-transformations");
            if (called.IsConstructor && called.DeclaringType?.IsGenericType == true &&
                called.DeclaringType.GetGenericTypeDefinition() == typeof(ValueTuple<,>) &&
                called.DeclaringType.GetGenericArguments()[0] == typeof(PileType))
                return new(args.FirstOrDefault()?.Numbers, "pile-result");
            if (called.IsConstructor && called.DeclaringType?.Assembly == Native && called.DeclaringType.Name == "CardLocation")
            {
                int n = Array.FindIndex(called.GetParameters(), p => p.ParameterType == typeof(PileType));
                return new(n >= 0 && n < args.Length ? args[n].Numbers : null, "play-result");
            }
            if (called is MethodInfo modifier && modifier.Name.StartsWith("ModifyCardPlayResult", StringComparison.Ordinal) &&
                modifier.ReturnType.IsGenericType && modifier.ReturnType.GetGenericTypeDefinition() == typeof(ValueTuple<,>) &&
                modifier.ReturnType.GetGenericArguments()[0] == typeof(PileType))
            {
                var implementations = modifier.IsVirtual && !modifier.IsFinal ? _sourceTypes.Where(t => modifier.DeclaringType!.IsAssignableFrom(t))
                    .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(m => m.GetBaseDefinition() == modifier.GetBaseDefinition())).Distinct().ToArray() : [modifier];
                LocalIlFacts.Value? value = null;
                foreach (var implementation in implementations)
                {
                    var body = Body(implementation);
                    var result = body == null ? LocalIlFacts.Value.Unknown : EventFacts(implementation, body, null).Return;
                    value = value == null ? result : LocalIlFacts.Value.Join(value, result);
                }
                return value ?? LocalIlFacts.Value.Unknown;
            }
            if (called.DeclaringType == typeof(CardModel) && called.Name == "get_Pile" && args.FirstOrDefault()?.Tag == "event-card")
                return new(Tag: "event-pile");
            if (called.DeclaringType == typeof(CardModel) && called.Name == "get_Pile" && args.FirstOrDefault()?.Tag == "combat-card")
                return new(Tag: "combat-pile");
            if (called.DeclaringType == typeof(CardPile) && called.Name == "get_Type" && args.FirstOrDefault()?.Tag == "event-pile")
                return new([0, 1, 2, 3, 4, 5]);
            if (called.DeclaringType == typeof(CardPile) && called.Name == "get_Cards" && args.FirstOrDefault()?.Tag == "combat-pile")
                return new(Tag: "combat-cards");
            if (called.DeclaringType == typeof(PlayerCombatState) && called is MethodInfo info && info.ReturnType == typeof(CardPile))
                return new(Tag: "combat-pile");
            if (called.DeclaringType == typeof(PileTypeExtensions) && called.Name == "GetPile" &&
                args.FirstOrDefault()?.Numbers is { Length: > 0 } values && values.All(v => v >= 0 && v < (int)PileType.Deck))
                return new(Tag: "combat-pile");
            return LocalIlFacts.Value.Unknown;
        }
        var handlers = method.GetMethodBody()?.ExceptionHandlingClauses.SelectMany(c =>
            c.Flags == ExceptionHandlingClauseOptions.Filter ? new[] { (c.HandlerOffset, 1), (c.FilterOffset, 1) } :
            new[] { (c.HandlerOffset, c.Flags == ExceptionHandlingClauseOptions.Clause ? 1 : 0) });
        // A recursive modifier cannot supply its own return proof.
        _eventFacts[key] = new(Enumerable.Repeat(true, code.Length).ToArray(), [], [], LocalIlFacts.Value.Unknown, false);
        return _eventFacts[key] = LocalIlFacts.Read(code, Argument, Field, Call, handlers);
    }

    private static bool CombatTransformationBatch(MethodBase method, LocalInstruction[] code, LocalIlFacts.Result facts)
    {
        if (method.DeclaringType?.Assembly != Native || !facts.Complete) return false;
        foreach (var write in facts.Writes.Where(p => p.Value.Tag == "local-transformations"))
            if (code[write.Key].Code != OpCodes.Stfld || code[write.Key].Operand is not FieldInfo member ||
                member.DeclaringType != method.DeclaringType || !member.DeclaringType.IsDefined(typeof(CompilerGeneratedAttribute), false)) return false;
        foreach (var invocation in facts.Calls.Where(p => p.Value.Any(v => v.Tag == "local-transformations")))
        {
            var called = (MethodBase)code[invocation.Key].Operand!;
            if (called.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Commands.CardCmd" && called.Name == "Transform") continue;
            if (called.DeclaringType?.IsGenericType == true && called.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                called.Name == "Add" && invocation.Value.Length == 2 && invocation.Value[1].Tag == "combat-transformation") continue;
            return false; // An escaped list or unknown insertion cannot certify its old-card domain.
        }
        return true;
    }

    private static bool CallCanChangeDeck(MethodBase method, LocalIlFacts.Value[]? arguments)
    {
        var owner = method.DeclaringType;
        // The certified native generation commands reject non-combat piles.
        if (owner?.FullName == "MegaCrit.Sts2.Core.Commands.CardPileCmd" &&
            method.Name is "AddGeneratedCardToCombat" or "AddGeneratedCardsToCombat") return false;
        if (owner?.FullName == "MegaCrit.Sts2.Core.Commands.CardCmd" && method.Name.StartsWith("Transform", StringComparison.Ordinal) &&
            method.GetParameters().FirstOrDefault()?.ParameterType == typeof(CardModel) && arguments?.FirstOrDefault()?.Tag == "combat-card") return false;
        if (method is MethodInfo callback && callback.GetBaseDefinition().DeclaringType == typeof(AbstractModel) &&
            callback.Name == "AfterCardChangedPiles") return true; // Explicit dispatch can supply a deck card.
        if (owner?.FullName == "MegaCrit.Sts2.Core.Commands.CardCmd" &&
            (method.Name.StartsWith("Obtain", StringComparison.Ordinal) || method.Name.StartsWith("Transform", StringComparison.Ordinal) ||
             method.Name.StartsWith("Remove", StringComparison.Ordinal)) ||
            owner == typeof(CardPile) && method.Name is "AddInternal" or "RemoveInternal") return true;
        var parameters = method.GetParameters();
        for (int n = 0; n < parameters.Length; n++)
        {
            int index = n + (method.IsStatic || method.IsConstructor ? 0 : 1);
            if (parameters[n].ParameterType == typeof(PileType))
            {
                var values = arguments != null && index < arguments.Length ? arguments[index].Numbers : null;
                // Opaque forwarding is examined at its eventual native writer.
                // Tuple construction also writes a play-result destination.
                bool boundary = owner?.FullName == "MegaCrit.Sts2.Core.Commands.CardPileCmd" && method.Name == "Add" ||
                    method.IsConstructor && (owner == typeof(CardLocation) || owner?.IsGenericType == true &&
                        owner.GetGenericTypeDefinition() == typeof(ValueTuple<,>) && owner.GetGenericArguments()[0] == typeof(PileType));
                if (owner == typeof(PileTypeExtensions) && method.Name == "GetPile") continue; // A pile query is not a mutation.
                if (method.IsConstructor && !boundary) continue; // Nullable/comparison containers do not move a card.
                if (values?.Any(v => v == (int)PileType.Deck) == true || boundary && values == null) return true;
            }
            if (owner?.FullName == "MegaCrit.Sts2.Core.Commands.CardPileCmd" && method.Name == "Add" && parameters[n].ParameterType == typeof(CardPile) &&
                (arguments == null || index >= arguments.Length || arguments[index].Tag != "combat-pile")) return true;
        }
        return false;
    }

    private static bool[] FutureTurnInstructions(MethodBase method, LocalInstruction[] code, int nextTurn)
    {
        bool Call(int at, Type owner, string name) => at >= 0 &&
            code[at].Code is var op && (op == OpCodes.Call || op == OpCodes.Callvirt) &&
            code[at].Operand is MethodInfo called && called.DeclaringType == owner && called.Name == name;
        bool? Taken(int at)
        {
            // Exact native owner -> combat state -> turn number expression.
            // Other receivers, helpers and unresolved comparisons stay reachable.
            if (at < 4 || !Call(at - 2, typeof(PlayerCombatState), "get_TurnNumber") ||
                !Call(at - 3, typeof(Player), "get_PlayerCombatState") ||
                !(Call(at - 4, typeof(RelicModel), "get_Owner") || Call(at - 4, typeof(CardModel), "get_Owner"))) return null;
            // Only an initial read-only gate supplies this range. An earlier
            // command/helper/write might change the turn; never infer through it.
            for (int i = 0; i < at - 4; i++)
            {
                if (code[i].Code == OpCodes.Stfld || code[i].Code == OpCodes.Stsfld || code[i].Code == OpCodes.Calli) return null;
                if (code[i].Operand is MethodBase &&
                    !(Call(i, typeof(RelicModel), "get_Owner") || Call(i, typeof(CardModel), "get_Owner") ||
                      Call(i, typeof(Player), "get_PlayerCombatState") || Call(i, typeof(PlayerCombatState), "get_TurnNumber"))) return null;
            }
            int? limit = code[at - 1].Code.Value switch {
                var value when value == OpCodes.Ldc_I4_M1.Value => -1,
                var value when value >= OpCodes.Ldc_I4_0.Value && value <= OpCodes.Ldc_I4_8.Value => value - OpCodes.Ldc_I4_0.Value,
                var value when value == OpCodes.Ldc_I4.Value && code[at - 1].Operand is int number => number,
                var value when value == OpCodes.Ldc_I4_S.Value && code[at - 1].Operand is sbyte number => number,
                _ => null };
            if (limit == null || nextTurn <= limit.Value) return null;
            var branch = code[at].Code;
            if (branch == OpCodes.Ble || branch == OpCodes.Ble_S || branch == OpCodes.Blt || branch == OpCodes.Blt_S ||
                branch == OpCodes.Beq || branch == OpCodes.Beq_S) return false;
            if (branch == OpCodes.Bgt || branch == OpCodes.Bgt_S || branch == OpCodes.Bge || branch == OpCodes.Bge_S ||
                branch == OpCodes.Bne_Un || branch == OpCodes.Bne_Un_S) return true;
            return null;
        }
        var handlers = method.GetMethodBody()?.ExceptionHandlingClauses.SelectMany(c =>
            c.Flags == ExceptionHandlingClauseOptions.Filter ? new[] { c.HandlerOffset, c.FilterOffset } : new[] { c.HandlerOffset });
        return LocalMethodBody.Reachable(code, Taken, handlers);
    }

    private LocalInstruction[]? Body(MethodBase method)
    {
        if (!_bodies.TryGetValue(method, out var code)) _bodies[method] = code = LocalMethodBody.Read(method);
        return code;
    }

    private Proof Describe(Type type)
    {
        if (_proofs.TryGetValue(type, out var found)) return found;
        var roots = new List<(MethodBase Method, bool Victory, bool Direct)>();
        for (var owner = type; owner != null && owner != typeof(AbstractModel); owner = owner.BaseType)
        {
            if (owner == typeof(CardModel) || owner == typeof(RelicModel) || owner == typeof(PowerModel) ||
                owner == typeof(MonsterModel) || owner == typeof(PotionModel) || owner == typeof(CharacterModel)) break;
            foreach (var method in owner.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName && owner.Assembly == Native || !method.IsVirtual || method.GetBaseDefinition() == method ||
                    method.Name is "AfterObtained" or "BeforeRemoved" or "AfterRemoved") continue;
                roots.Add((method, method.Name is "AfterCombatVictory" or "AfterCombatEnd", true));
            }
            foreach (var constructor in owner.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                roots.Add((constructor, false, false));
            if (owner.TypeInitializer is { } initializer) roots.Add((initializer, false, false));
        }
        return _proofs[type] = ReadEffects(type, roots);
    }

    private static string Detail(Proof proof) => proof.Unknown +
        (proof.UnknownAt == null ? "" : "：" + proof.UnknownAt);

    // Used only by the owned root audit, never by the search hot path.
    internal object Audit(Player player)
    {
        var state = CombatManager.Instance.DebugOnlyGetState()!;
        return player.RunState.IterateHookListeners(state).Concat(new AbstractModel[] { player.Character })
            .Concat(player.PlayerCombatState!.AllPiles.SelectMany(p => p.Cards)).Concat(player.Potions)
            .Select(m => m.GetType()).Distinct().Select(t =>
            {
                var proof = Describe(t);
                var content = DescribeContent(t);
                return new { Type = t.FullName, proof.Unknown, proof.UnknownAt, proof.VictoryHeals,
                    proof.VictoryMaxHpGains, References = proof.References.Select(r => r.FullName).ToArray(),
                    Content = new { content.ActiveRecovery, content.VictoryHeals, content.VictoryMaxHpGains,
                        content.Uncertain, content.UncertainAt, content.MayChangeDeck,
                        Generated = content.Generated.Select(g => g.FullName).ToArray() },
                    Target = ReadContent(t, targetOnly: true).ActiveRecovery,
                    CombatPileEventsOnly = _combatPileEventsOnly,
                    DeckCapabilityReason = _deckCapabilityReason };
            }).ToArray();
    }

    // Inspect registered patch callbacks and initialization/event roots. The
    // presence of an unrelated Mod is no longer itself an unknown effect.
    // Reflection metadata is read; no getter, initializer or Mod hook is invoked.
    private Proof DescribeGlobals()
    {
        var foreign = request.LoadedMods.Where(m => !m.StartsWith("sts2:", StringComparison.Ordinal) &&
            !m.StartsWith("SpireAiCoach:", StringComparison.Ordinal)).ToArray();
        if (foreign.Length == 0) return new(null, 0, []);
        // A known opaque rewrite already makes the bound unknown. Do this cheap
        // check before enumerating every type/initializer in all Mod assemblies.
        var patched = Harmony.GetAllPatchedMethods().ToArray();
        foreach (var original in patched)
            if (Harmony.GetPatchInfo(original)!.Transpilers.Any(p => !OurPatch(p.owner)))
                return new("外部 IL 补丁的回复效果尚未证明", 0, [], original.DeclaringType?.FullName + "." + original.Name);
        var roots = new List<(MethodBase Method, bool Victory, bool Direct)>();
        var available = ModManager.GetLoadedMods().Where(m => m.manifest?.id != "SpireAiCoach").ToArray();
        foreach (var identity in foreign)
        {
            var mod = available.FirstOrDefault(m => identity.StartsWith(m.manifest!.id + ":", StringComparison.Ordinal));
            if (mod == null || mod.assemblies.Count == 0)
                return new("无法读取 Mod 的全局代码", 0, [], identity.Split(':')[0]);
            foreach (var assembly in mod.assemblies)
            {
                foreach (var module in assembly.GetModules())
                    if (module.GetType("<Module>")?.TypeInitializer is { } moduleInitializer)
                        roots.Add((moduleInitializer, false, true));
                foreach (var type in assembly.GetTypes())
                {
                    if (type.TypeInitializer is { } initializer) roots.Add((initializer, false, true));
                    roots.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Where(m => m.IsDefined(typeof(ModuleInitializerAttribute), false))
                        .Select(m => ((MethodBase)m, false, true)));
                    foreach (var attribute in type.GetCustomAttributesData().Where(a => a.AttributeType == typeof(ModInitializerAttribute)))
                    {
                        if (attribute.ConstructorArguments.FirstOrDefault().Value is not string entry ||
                            type.GetMethod(entry, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is not { } method)
                            return new("无法确定 Mod 初始化入口", 0, [], type.FullName);
                        roots.Add((method, false, true));
                    }
                }
            }
        }
        foreach (var original in patched)
        {
            var patches = Harmony.GetPatchInfo(original)!;
            roots.AddRange(patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Finalizers)
                .Where(p => !OurPatch(p.owner)).Select(p => ((MethodBase)p.PatchMethod, false, false)));
        }
        return ReadEffects(null, roots);
    }

    private Proof ReadEffects(Type? type, IEnumerable<(MethodBase Method, bool Victory, bool Direct)> roots)
    {
        string? unknown = null;
        string? boundary = null;
        int heals = 0, maxHpGains = 0;
        var references = new HashSet<Type>();
        var seen = new HashSet<(MethodBase Method, bool Victory, bool Direct)>();
        var queue = new Queue<(MethodBase Method, bool Victory, bool Direct)>();
        foreach (var root in roots) queue.Enqueue(root);
        while (unknown == null && queue.TryDequeue(out var entry))
        {
            var (method, victory, direct) = entry;
            if (!seen.Add(entry)) continue;
            if (seen.Count > 2048) { unknown = "模型效果调用图过大"; break; }
            boundary = method.DeclaringType?.FullName + "." + method.Name;
            var patchInfo = Harmony.GetPatchInfo(method);
            if (patchInfo?.Transpilers.Any(p => !OurPatch(p.owner)) == true)
            { unknown = "效果存在尚未证明的外部 IL 补丁"; break; }
            if (patchInfo != null) foreach (var patch in patchInfo.Prefixes.Concat(patchInfo.Postfixes).Concat(patchInfo.Finalizers)
                .Where(p => !OurPatch(p.owner))) queue.Enqueue((patch.PatchMethod, false, false));
            var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType ??
                method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
            if (stateMachine != null)
            {
                if (type == null && direct) { unknown = "异步全局初始化的回调范围未知"; break; }
                var move = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (move == null) { unknown = "无法读取异步模型效果"; break; }
                queue.Enqueue((move, victory, direct));
            }
            boundary = method.DeclaringType?.FullName + "." + method.Name;
            var code = Body(method);
            if (code == null) { unknown = "无法证明模型效果的完整调用"; break; }
            for (int i = 0; i < code.Length && unknown == null; i++)
            {
                var instruction = code[i];
                if (type != null && instruction.Operand is Type tokenType) Refer(tokenType);
                if (method.DeclaringType?.Assembly != Native &&
                    (instruction.Code.Name?.StartsWith("stind", StringComparison.Ordinal) == true ||
                     instruction.Code == System.Reflection.Emit.OpCodes.Stobj || instruction.Code == System.Reflection.Emit.OpCodes.Cpobj))
                { unknown = "外部效果包含间接写入"; break; }
                if (method.DeclaringType?.Assembly != Native && instruction.Operand is FieldInfo field && field.DeclaringType?.Assembly == Native &&
                    (instruction.Code == System.Reflection.Emit.OpCodes.Stfld || instruction.Code == System.Reflection.Emit.OpCodes.Stsfld ||
                     instruction.Code == System.Reflection.Emit.OpCodes.Ldflda || instruction.Code == System.Reflection.Emit.OpCodes.Ldsflda))
                { unknown = "外部代码直接修改原生状态"; break; }
                if (!victory && instruction.Operand is string literal && literal is "Heal" or "MaxHp")
                { unknown = "回复变量可能在后续改变"; break; }
                if (instruction.Operand is not MethodBase called) continue;
                if (instruction.Code == System.Reflection.Emit.OpCodes.Ldvirtftn && called is MethodInfo { IsVirtual: true, IsFinal: false } &&
                    called.DeclaringType?.IsSealed != true)
                { unknown = "动态虚方法回调的目标未知"; break; }
                if (instruction.Code == System.Reflection.Emit.OpCodes.Ldftn || instruction.Code == System.Reflection.Emit.OpCodes.Ldvirtftn)
                { queue.Enqueue((called, false, false)); continue; }
                var declaring = called.DeclaringType!;
                boundary = declaring.FullName + "." + called.Name;
                string ns = declaring.Namespace ?? "";
                if (method.DeclaringType?.Assembly != Native && ns == "MegaCrit.Sts2.Core.Localization.DynamicVars" &&
                    called.Name.StartsWith("set_", StringComparison.Ordinal))
                { unknown = "外部效果可能改变后续回复变量"; break; }
                if (method.DeclaringType?.Assembly != Native && instruction.Code == System.Reflection.Emit.OpCodes.Callvirt &&
                    called is MethodInfo { IsVirtual: true, IsFinal: false } &&
                    declaring.IsSealed != true && !typeof(AbstractModel).IsAssignableFrom(declaring) &&
                    !ns.StartsWith("System", StringComparison.Ordinal))
                { unknown = "动态虚方法效果的目标未知"; break; }
                if (called.IsGenericMethod) foreach (var argument in called.GetGenericArguments()) Refer(argument);
                if (typeof(AbstractModel).IsAssignableFrom(declaring) && !declaring.IsAbstract) Refer(declaring);
                var calledPatches = Harmony.GetPatchInfo(called);
                if (calledPatches?.Transpilers.Any(p => !OurPatch(p.owner)) == true)
                { unknown = "效果命令存在尚未证明的外部 IL 补丁"; break; }
                if (calledPatches != null) foreach (var patch in calledPatches.Prefixes.Concat(calledPatches.Postfixes).Concat(calledPatches.Finalizers)
                    .Where(p => !OurPatch(p.owner))) queue.Enqueue((patch.PatchMethod, false, false));
                if (declaring.FullName == "MegaCrit.Sts2.Core.Commands.CreatureCmd" && called.Name == "GainMaxHp" &&
                    victory && direct && type?.Assembly == Native && typeof(RelicModel).IsAssignableFrom(type) &&
                    !LocalMethodBody.HasBackwardJump(code) && FixedVictoryValue(code, i, type, "get_MaxHp", false))
                { maxHpGains++; continue; }
                if (HpWrites.Contains(called.Name)) { unknown = "存在回复或生命上限变化"; break; }
                if (!victory && called.Name is "get_Heal" or "get_MaxHp" && ns == "MegaCrit.Sts2.Core.Localization.DynamicVars")
                { unknown = "回复变量可能在战中使用或改变"; break; }
                if (declaring.FullName == "MegaCrit.Sts2.Core.Commands.CreatureCmd" && called.Name == "Heal")
                {
                    // A fixed native victory callback executes once. Sum all its
                    // possible calls, ignoring conditions (an optimistic ceiling).
                    // Helpers/loops/other hooks cannot inherit that multiplicity.
                    if (!victory || !direct || type == null || type.Assembly != Native || !typeof(RelicModel).IsAssignableFrom(type) ||
                        LocalMethodBody.HasBackwardJump(code) || !FixedVictoryValue(code, i, type, "get_Heal", true))
                    { unknown = "战中、重复或动态回复的上界未知"; break; }
                    heals++; continue;
                }
                if (ns.StartsWith("System.Reflection", StringComparison.Ordinal) ||
                    ns.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal) || called.Name == "Invoke" ||
                    called.Name == "DynamicInvoke" || called.IsDefined(typeof(System.Runtime.InteropServices.DllImportAttribute), false))
                { unknown = "存在动态或外部效果调用"; break; }
                if (ns.StartsWith("HarmonyLib", StringComparison.Ordinal))
                {
                    if (type == null && direct) continue;
                    unknown = "战斗回调可能动态改变补丁"; break;
                }
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
                    if (declaring.Assembly == Native && called.IsSpecialName && called.Name.StartsWith("get_", StringComparison.Ordinal) ||
                        declaring.Assembly == Native && PureModelMethods.Contains(called.Name) ||
                        declaring.Assembly == Native && called.IsConstructor) continue;
                    if (called.Name is "AfterCombatVictory" or "AfterCombatEnd") { unknown = "战后回复回调可能重复调用"; break; }
                }
                // Read-only entity access and native hook dispatch retain the
                // model callbacks already in the certificate's closed model set.
                if (called.IsSpecialName && called.Name.StartsWith("get_", StringComparison.Ordinal) &&
                    (ns.StartsWith("MegaCrit.Sts2.Core.Entities", StringComparison.Ordinal) ||
                     ns.StartsWith("MegaCrit.Sts2.Core.Combat", StringComparison.Ordinal) ||
                     ns.StartsWith("MegaCrit.Sts2.Core.Runs", StringComparison.Ordinal)) ||
                    declaring.FullName == "MegaCrit.Sts2.Core.Hooks.Hook" ||
                    declaring.Assembly == Native && PureModelMethods.Contains(called.Name) ||
                    ns.StartsWith("MegaCrit.Sts2.Core.MonsterMoves", StringComparison.Ordinal) && called.IsConstructor) continue;
                queue.Enqueue((called, victory, type == null && direct));
            }
        }
        return new(unknown, heals, references.ToArray(), unknown == null ? null : boundary, maxHpGains);

        void Refer(Type target)
        {
            if (typeof(AbstractModel).IsAssignableFrom(target) && !target.IsAbstract) references.Add(target);
        }
    }

    private static bool OurPatch(string owner) => owner.StartsWith("SpireAiCoach", StringComparison.Ordinal);
    private static bool ForeignPatch(MethodBase method) => Harmony.GetPatchInfo(method)?.Owners.Any(o => !OurPatch(o)) == true;

    private static bool FixedVictoryValue(LocalInstruction[] code, int call, Type owner, string variable, bool hasBool)
    {
        // Require this.DynamicVars.Heal/MaxHp.BaseValue in native code.
        // No arbitrary getter evaluation, card IDs or localized descriptions.
        if (hasBool && (call == 0 || code[call - 1].Code != System.Reflection.Emit.OpCodes.Ldc_I4_1)) return false;
        int at = call - (hasBool ? 1 : 0);
        if (at < 4 || code[at - 1].Operand is not MethodInfo value || value.Name != "get_BaseValue" ||
            code[at - 2].Operand is not MethodInfo heal || heal.Name != variable ||
            code[at - 3].Operand is not MethodInfo vars || vars.Name != "get_DynamicVars" ||
            ForeignPatch(value) || ForeignPatch(heal) || ForeignPatch(vars)) return false;
        var source = code[at - 4];
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
