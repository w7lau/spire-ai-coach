using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Read-only diagnostic, enabled explicitly and only inside an owned native host.
internal static class RuleStallProbe
{
    private static string _root = "";
    private static string _key = "";
    private static int _frames;
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo Request = AccessTools.Field(typeof(LocalWorker), "_activeRequest");
    private static readonly FieldInfo Route = AccessTools.Field(typeof(LocalWorker), "_traceRoute");
    private static readonly FieldInfo Step = AccessTools.Field(typeof(LocalWorker), "_traceStep");

    public static void Install(string root)
    {
        if (!File.Exists(Path.Combine(root, ".coach-worker")) ||
            !string.Equals(OS.GetExecutablePath().Replace('/', '\\'), Path.Combine(root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Rule stall observation requires the owned worker executable");
        _root = root;
        new Harmony("SpireLocalIntegration.owned-rule-stall").Patch(AccessTools.Method(typeof(LocalWorker), "Frame"),
            prefix: new(AccessTools.Method(typeof(RuleStallProbe), nameof(BeforeFrame))));
    }

    private static void BeforeFrame()
    {
        if (Request.GetValue(null) is not LocalSearchRequest request) return;
        string key = $"{request.Id}-{Route.GetValue(null)}-{Step.GetValue(null)}";
        if (key != _key) { _key = key; _frames = 0; }
        if (++_frames != 120) return;
        try
        {
            var manager = CombatManager.Instance;
            var state = manager.DebugOnlyGetState();
            var player = state == null ? null : LocalContext.GetMe(state);
            var tasks = new List<object>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Read(object? value, string path, int depth)
            {
                if (value == null || depth > 20 || tasks.Count >= 160 || !seen.Add(value)) return;
                var type = value.GetType();
                if (value is Task task) tasks.Add(new { path, type = type.FullName, task.Status,
                    exception = task.IsFaulted ? task.Exception?.ToString() : null });
                else tasks.Add(new { path, type = type.FullName,
                    state = type.GetField("<>1__state", Fields)?.GetValue(value) });
                if (value is Delegate callback) { Read(callback.Target, path + ".Target", depth + 1); return; }
                foreach (var field in type.GetFields(Fields))
                {
                    if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string)) continue;
                    var child = field.GetValue(value);
                    if (child is Task || child is Delegate && value is Task ||
                        field.FieldType.Name.Contains("TaskCompletionSource", StringComparison.Ordinal) ||
                        field.FieldType.Name.Contains("Awaiter", StringComparison.Ordinal) ||
                        field.FieldType.Name.Contains("AsyncTaskMethodBuilder", StringComparison.Ordinal) ||
                        field.Name.Contains("awaiter", StringComparison.OrdinalIgnoreCase) ||
                        field.Name.Contains("StateMachine", StringComparison.OrdinalIgnoreCase) || field.Name == "m_continuationObject")
                        Read(child, path + "." + field.Name, depth + 1);
                }
            }
            Read(manager, "combat", 0);
            Read(manager.StateTracker, "tracker", 0);
            Read(RunManager.Instance.ActionExecutor, "executor", 0);
            LocalWire.Write(Path.Combine(_root, "rule-stall-observation.json"), new { request.Id, key,
                round = state?.RoundNumber, playerPhase = player?.PlayerCombatState?.Phase.ToString(),
                manager.IsStarting, manager.IsOverOrEnding, manager.PlayerActionsDisabled,
                queueEmpty = RunManager.Instance.ActionQueueSet.BecameEmpty().Status,
                executorFinished = RunManager.Instance.ActionExecutor.FinishedExecutingActions().Status, tasks });
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(_root, "rule-stall-observation-error.txt"), ex.ToString()); }
    }
}
