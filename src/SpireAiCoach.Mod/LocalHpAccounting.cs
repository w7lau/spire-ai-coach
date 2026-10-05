using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Installed only by the owned worker. Preserve every native/Mod health effect;
// observe the two numeric writes before their callbacks can cause another write.
internal sealed class LocalHpAccounting : IDisposable
{
    private static readonly ConditionalWeakTable<Creature, LocalHpAccounting> Tracked = new();
    private static readonly AccessTools.FieldRef<Creature, int> CurrentHp = AccessTools.FieldRefAccess<Creature, int>("_currentHp");
    private static readonly AccessTools.FieldRef<Creature, int> MaxHp = AccessTools.FieldRefAccess<Creature, int>("_maxHp");
    private readonly Creature _creature;
    private readonly LocalHealthAccounting _accounting;
    private readonly object _gate = new();
    public static bool WritesHooked { get; private set; }

    public static void Install()
    {
        var harmony = new Harmony("SpireAiCoach.owned-worker.health");
        try
        {
            foreach (var property in new[] { nameof(Creature.CurrentHp), nameof(Creature.MaxHp) })
            {
                var setter = AccessTools.PropertySetter(typeof(Creature), property);
                if (setter == null) throw new MissingMethodException("Creature." + property);
                harmony.Patch(setter,
                    transpiler: new(AccessTools.Method(typeof(LocalHpAccounting), nameof(AfterWrite))),
                    postfix: new(AccessTools.Method(typeof(LocalHpAccounting), nameof(ObserveWrite))));
            }
            WritesHooked = true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id); WritesHooked = false;
            Godot.GD.Print("[SpireAiCoach] Health write observation unavailable: " + ex.Message);
        }
    }

    private static IEnumerable<CodeInstruction> AfterWrite(IEnumerable<CodeInstruction> source, MethodBase __originalMethod)
    {
        var instructions = source.ToList();
        string name = __originalMethod.Name == "set_CurrentHp" ? "_currentHp" : "_maxHp";
        var field = AccessTools.Field(typeof(Creature), name);
        if (field == null || !instructions.Any(i => i.opcode == OpCodes.Stfld && Equals(i.operand, field)))
            throw new InvalidOperationException("Health write boundary changed: " + __originalMethod.Name);
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.opcode != OpCodes.Stfld || !Equals(instruction.operand, field)) continue;
            yield return new(OpCodes.Ldarg_0);
            yield return new(OpCodes.Call, AccessTools.Method(typeof(LocalHpAccounting), nameof(ObserveWrite)));
        }
    }

    private static void ObserveWrite(Creature __instance)
    {
        if (Tracked.TryGetValue(__instance, out var accounting)) accounting.Observe();
    }

    public LocalHpAccounting(Player player)
    {
        _creature = player.Creature;
        _accounting = new(CurrentHp(_creature), MaxHp(_creature));
        Tracked.Add(_creature, this);
        _creature.CurrentHpChanged += Changed;
        _creature.MaxHpChanged += Changed;
    }

    private void Changed(int before, int after) => Observe();
    private void Observe(bool checkpoint = false)
    {
        // Read backing values directly: observations must not invoke another
        // Mod-patched getter or change rule state, and need no per-write reflection.
        lock (_gate) _accounting.Observe(CurrentHp(_creature), MaxHp(_creature), checkpoint);
    }

    public int HpLost { get { Observe(checkpoint: true); lock (_gate) return _accounting.HpLost; } }
    public LocalHealthChanges Snapshot()
    {
        Observe(checkpoint: true);
        lock (_gate) return _accounting.Snapshot();
    }

    // Explicit owned-worker diagnostic only; restore the full frozen root after
    // these fixtures. Never used to estimate a real card or heal effect.
    internal static LocalHealthChanges[] Audit(Player player)
    {
        if (!WritesHooked) throw new InvalidOperationException("Native HP writes were not hooked");
        var creature = player.Creature;
        int hp = creature.CurrentHp, max = creature.MaxHp;
        if (hp < 3) throw new InvalidOperationException("Health audit needs a root with at least 3 HP");
        var result = new List<LocalHealthChanges>();
        void ModHealing(int oldHp, int newHp)
        {
            if (newHp == hp - 2) creature.SetCurrentHpInternal(hp - 1);
        }
        creature.CurrentHpChanged += ModHealing;
        try
        {
            using var accounting = new LocalHpAccounting(player);
            creature.SetCurrentHpInternal(hp - 2);
            var health = accounting.Snapshot();
            if (health.HpLost != 2 || health.HpGained != 1 || !health.FullyObserved)
                throw new InvalidOperationException("Nested external HP callback was not captured exactly");
            result.Add(health);
        }
        finally { creature.CurrentHpChanged -= ModHealing; }
        creature.SetMaxHpInternal(max); creature.SetCurrentHpInternal(hp);
        using (var accounting = new LocalHpAccounting(player))
        {
            creature.SetMaxHpInternal(max + 2); creature.SetCurrentHpInternal(hp + 1);
            creature.SetMaxHpInternal(hp);
            var health = accounting.Snapshot();
            if (health.HpLost != 1 || health.HpGained != 1 || health.MaxHpGained != 2 ||
                health.MaxHpLost != max + 2 - hp || !health.FullyObserved)
                throw new InvalidOperationException("Maximum HP/clipping observation was not captured exactly");
            result.Add(health);
            AccessTools.Field(typeof(Creature), "_currentHp").SetValue(creature, hp - 1);
            health = accounting.Snapshot();
            if (health.UnobservedChanges != 1 || health.HpLost != 2 || health.FullyObserved)
                throw new InvalidOperationException("Unnotified HP write was not marked incomplete");
            result.Add(health);
        }
        return result.ToArray();
    }

    public void Dispose()
    {
        _creature.CurrentHpChanged -= Changed;
        _creature.MaxHpChanged -= Changed;
        Tracked.Remove(_creature);
    }
}
