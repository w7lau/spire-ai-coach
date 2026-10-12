using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Test-only observer. It never changes models, commands, RNG, state or callbacks.
public static class TaskCallableProbe
{
    private static string _root = "";
    public static void Install(string root)
    {
        _root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(_root, ".coach-worker")) ||
            !string.Equals(OS.GetExecutablePath().Replace('/', '\\'), Path.Combine(_root, "game", "SlayTheSpire2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Task callable probe needs an owned native worker");
        foreach (var name in new[] { "task-callable-observed.jsonl", "task-callable-component.json", "task-callable-component-error.txt" })
        {
            var path = Path.Combine(_root, name);
            if (File.Exists(path)) File.Delete(path);
        }
        var compatibility = typeof(LocalWorkerPool).Assembly.GetType("SpireAiCoach.Mod.LocalTaskCallableCompatibility", true)!;
        var patched = (IEnumerable<string>)compatibility.GetField("Patched", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var names = patched.ToArray();
        if (names.Length != 5) throw new InvalidOperationException("Actual VFX registration coverage differs");
        File.WriteAllText(Path.Combine(_root, "task-callable-sites.json"), JsonSerializer.Serialize(names));
        var native = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechRunes")
            .GetType("HextechRunes.HextechCombatVfx", true)!;
        var harmony = new Harmony("SpireLocalIntegration.task-callable-observer");
        foreach (var method in native.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => names.Contains(m.Name)))
        {
            harmony.Patch(method, prefix: new(AccessTools.Method(typeof(TaskCallableProbe), nameof(Record))));
            if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_TASK_CALLABLE_BASELINE") == "1")
                harmony.Unpatch(method, HarmonyPatchType.Transpiler, "SpireAiCoach.owned-worker.task-callable." + method.Name);
        }
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_TASK_CALLABLE_BASELINE") == "1") return;
        VerifyComponent(compatibility);
    }

    private static void Record(MethodBase __originalMethod) => File.AppendAllText(Path.Combine(_root, "task-callable-observed.jsonl"),
        JsonSerializer.Serialize(new { method = __originalMethod.Name }) + "\n");

    private static async void VerifyComponent(Type compatibility)
    {
        try
        {
            int starts = 0, completions = 0;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<Task> callback = async () => { starts++; await Task.Yield(); completions++; done.SetResult(); };
            var factory = compatibility.GetMethod("FromTask", BindingFlags.NonPublic | BindingFlags.Static)!;
            var callable = (Callable)factory.Invoke(null, [callback])!;
            using var value = callable.Call();
            if (value.VariantType != Variant.Type.Nil) throw new InvalidOperationException("Void callable did not return nil");
            await done.Task;
            if (starts != 1 || completions != 1) throw new InvalidOperationException("Task callback did not run exactly once");
            using var integer = Callable.From(() => 7).Call();
            if (integer.AsInt32() != 7) throw new InvalidOperationException("Unrelated integer callable changed");
            File.WriteAllText(Path.Combine(_root, "task-callable-component.json"), JsonSerializer.Serialize(new {
                callbackStarts = starts, callbackCompletions = completions, nilReturn = true, integerCallbackPreserved = true }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(_root, "task-callable-component-error.txt"), ex.ToString()); }
    }
}
