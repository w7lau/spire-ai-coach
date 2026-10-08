using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using SpireAiCoach.Core;

namespace SpireLocalIntegration;

// Owned parent only. Observe an existing proof tree; never replay or mutate it.
internal static class ProofTreeProbe
{
    private static string _root = "";
    public static void Install(string root)
    {
        if (!File.Exists(Path.Combine(root, ".spire-native-probe-owner")) ||
            !string.Equals(OS.GetExecutablePath().Replace('/', '\\'), Path.Combine(root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Proof observation requires the owned parent executable");
        _root = root;
        new Harmony("SpireLocalIntegration.owned-proof-tree").Patch(typeof(LocalMinimumLossBroker).GetMethod("Dispose")!,
            prefix: new(typeof(ProofTreeProbe).GetMethod(nameof(BeforeDispose), BindingFlags.Static | BindingFlags.NonPublic)!));
    }
    private static void BeforeDispose(object __instance)
    {
        object? Field(object o, string n) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(o);
        int Number(object o, string n) => (int)o.GetType().GetProperty(n)!.GetValue(o)!;
        var proof = Field(__instance, "_proof")!;
        var root = Field(proof, "_root")!;
        var goal = Field(proof, "_goal");
        int? target = goal == null ? null : Number(goal, "Loss");
        var queue = new Queue<object>(); queue.Enqueue(root);
        int total = 0, open = 0, withoutHint = 0, missing = 0, incomplete = 0;
        var examples = new List<object>();
        var prefix = proof.GetType().GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
        while (queue.TryDequeue(out var node))
        {
            total++;
            var children = (IDictionary)Field(node, "Children")!;
            foreach (var value in children.Values) queue.Enqueue(value!);
            if ((bool)Field(node, "Terminal")! || target == null || Number(Field(node, "Minimum")!, "Loss") >= target) continue;
            open++;
            var legal = ((IEnumerable)Field(node, "Legal")!).Cast<string>().ToArray();
            int absent = legal.Count(k => !children.Contains(k));
            bool noHint = Field(node, "Hint") == null;
            bool complete = (bool)Field(node, "Complete")!;
            if (noHint) withoutHint++;
            missing += absent;
            if (!complete) incomplete++;
            if (examples.Count < 16 && (absent > 0 || !complete || legal.Length == 0))
                examples.Add(new { minimum = Field(node, "Minimum")!.ToString(), own = Field(node, "Own")!.ToString(),
                    noHint, complete, round = Field(node, "Round"), legal = legal.Length, missing = absent,
                    prefix = prefix.Invoke(null, [node]) });
        }
        LocalWire.Write(Path.Combine(_root, "proof-tree-private.json"), new { total, open, withoutHint, missing,
            incomplete, knownRecovery = Field(proof, "_knownRecovery"), target, examples });
    }
}
