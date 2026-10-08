using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Diagnostic metadata only; reads the estimate's already-filled type cache.
internal static class RecoveryTargetProbe
{
    private static string _root = "";
    public static void Install(string root)
    {
        if (!File.Exists(Path.Combine(root, ".coach-worker")) ||
            !string.Equals(OS.GetExecutablePath().Replace('/', '\\'), Path.Combine(root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recovery metadata observation requires the owned worker executable");
        _root = root;
        var type = typeof(ModEntry).Assembly.GetType("SpireAiCoach.Mod.LocalRecoveryEstimator")!;
        new Harmony("SpireLocalIntegration.owned-recovery-target").Patch(AccessTools.Method(type, "Audit"),
            postfix: new(AccessTools.Method(typeof(RecoveryTargetProbe), nameof(AfterAudit))));
    }
    private static void AfterAudit(object __instance)
    {
        var current = (LocalSearchRequest)AccessTools.Field(typeof(LocalWorker), "_activeRequest").GetValue(null)!;
        var cache = (IDictionary)AccessTools.Field(__instance.GetType(), "_targetContent").GetValue(__instance)!;
        object? Get(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
        var entries = cache.Keys.Cast<Type>().Select(t => new {
            Type = t.FullName,
            ActiveRecovery = Get(cache[t]!, "ActiveRecovery"),
            DynamicMaxHp = Get(cache[t]!, "DynamicMaxHp"),
            Uncertain = Get(cache[t]!, "Uncertain"),
            UncertainAt = Get(cache[t]!, "UncertainAt"),
            VictoryHeals = Get(cache[t]!, "VictoryHeals"),
            VictoryMaxHpGains = Get(cache[t]!, "VictoryMaxHpGains"),
            Generated = ((Type[])Get(cache[t]!, "Generated")!).Select(g => g.FullName).ToArray()
        }).ToArray();
        LocalWire.Write(Path.Combine(_root, "target-sources-audit.json"), new { current.Id, entries });
    }
}
