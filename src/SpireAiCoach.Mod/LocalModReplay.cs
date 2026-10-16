using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// The audited Hextech fields affect real selections but are absent from
// native relic serialization. Observe it in the live game; restore only in owned
// workers, before room initialization and the original recorded history.
internal static class LocalModReplay
{
    private sealed record SupportedField(string TypeName, string ModuleId, string FieldName);
    private static readonly SupportedField[] Supported =
    [
        new("HextechRunes.HiddenGemUpgradeRune", "730cd0af-a1c5-4fe0-b932-add4bf4809ef", "_localUpgradedPlayOrdinal"),
        new("HextechRunes.CircleOfDeathRune", "81593f41-70fb-41de-9a5e-2103436f255d", "_localTargetRollOrdinal")
    ];
    private static readonly FieldInfo ReplayField = typeof(CombatReplayWriter)
        .GetField("_replay", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private sealed record Root(LocalRelicReplayField[] Fields, string? Error = null);
    private static readonly ConditionalWeakTable<CombatReplay, Root> Roots = new();
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        var method = AccessTools.Method(typeof(CombatReplayWriter), nameof(CombatReplayWriter.RecordInitialState));
        if (ReplayField == null || method == null) return; // Capture rejects a missing root.
        new Harmony("SpireAiCoach.read-only-mod-replay").Patch(method,
            postfix: new HarmonyMethod(typeof(LocalModReplay), nameof(ObserveRoot)));
        _installed = true;
    }

    private static void ObserveRoot(CombatReplayWriter __instance)
    {
        if (!__instance.IsEnabled || ReplayField.GetValue(__instance) is not CombatReplay replay) return;
        Root root;
        try { root = new(Read(RunManager.Instance.DebugOnlyGetState()!.Players)); }
        catch (Exception ex) { root = new([], ex.Message); }
        Roots.Remove(replay); Roots.Add(replay, root);
    }

    public static LocalModReplayCheckpoint? Capture(CombatReplay replay, IEnumerable<Player> players)
    {
        var current = Read(players);
        if (!Roots.TryGetValue(replay, out var root))
        {
            if (current.Length == 0) return null;
            throw Missing("缺少这场战斗开始时的符文状态，请进入下一场战斗后再计算。");
        }
        if (root.Error != null) throw Missing(root.Error);
        return root.Fields.Length == 0 && current.Length == 0 ? null : new(root.Fields, current);
    }

    public static LocalRelicReplayField[] Read(IEnumerable<Player> players)
    {
        List<LocalRelicReplayField>? values = null;
        foreach (var player in players.OrderBy(p => p.NetId))
        {
            for (int slot = 0; slot < player.Relics.Count; slot++)
            {
                var relic = player.Relics[slot];
                if (!Supported.Any(s => s.TypeName == relic.GetType().FullName)) continue;
                var field = Field(relic);
                values ??= [];
                values.Add(new(player.NetId, slot, relic.Id.ToString(), relic.GetType().FullName!,
                    relic.GetType().Assembly.ManifestModule.ModuleVersionId.ToString(), field.Name, (int)field.GetValue(relic)!));
            }
        }
        return values?.ToArray() ?? [];
    }

    public static void Restore(LocalModReplayCheckpoint? checkpoint, RunState run)
    {
        if (!LocalWorker.InOwnedRestore) throw new InvalidOperationException("Mod replay restoration requires an owned worker.");
        var actual = Read(run.Players);
        if (checkpoint == null)
        {
            if (actual.Length != 0) throw Missing("这份旧计算缺少符文起点状态，请重新计算。");
            return;
        }
        // Validate every receiver before writing any field. Never import a static
        // variable, another rune, a changed module, or an assumed default value.
        if (checkpoint.Initial == null || checkpoint.Current == null || actual.Length != checkpoint.Initial.Length)
            throw Missing("符文起点字段不完整。");
        var writes = new List<(RelicModel Relic, FieldInfo Field, int Value)>();
        for (int i = 0; i < actual.Length; i++)
        {
            var expected = checkpoint.Initial[i];
            if (expected != (actual[i] with { Value = expected.Value })) throw Missing("符文起点身份不一致。");
            var player = run.Players.Single(p => p.NetId == expected.PlayerId);
            var relic = player.Relics[expected.RelicIndex];
            writes.Add((relic, Field(relic), expected.Value));
        }
        foreach (var write in writes) write.Field.SetValue(write.Relic, write.Value);
    }

    public static void ValidateCurrent(LocalModReplayCheckpoint? checkpoint, IEnumerable<Player> players)
    {
        var actual = Read(players);
        if (!(checkpoint?.Current ?? []).SequenceEqual(actual))
            throw Missing("后台重放后的符文状态与实际战斗不一致，未发布本地建议。");
    }

    private static FieldInfo Field(RelicModel relic)
    {
        var type = relic.GetType();
        var supported = Supported.SingleOrDefault(s => s.TypeName == type.FullName &&
            s.ModuleId == type.Assembly.ManifestModule.ModuleVersionId.ToString());
        if (supported == null) throw Missing("当前符文版本尚未验证状态还原：" + relic.Id);
        var field = type.GetField(supported.FieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.FieldType != typeof(int) || field.IsStatic || field.IsInitOnly || field.DeclaringType != type)
            throw Missing("符文状态字段已变化：" + relic.Id);
        return field;
    }

    private static CoachException Missing(string detail) => new("local_mod_replay", detail);
}
