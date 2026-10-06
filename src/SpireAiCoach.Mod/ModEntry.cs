using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireAiCoach.Mod;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    private static CoachOverlay? _overlay;
    public static void Initialize()
    {
        LocalModReplay.Install();
        if (LocalWorker.TryStart()) return;
        if (System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_INTEGRATION") != null) return;
        if (_overlay != null) return;
        Callable.From(() =>
        {
            try
            {
                if (Engine.GetMainLoop() is not SceneTree tree) throw new InvalidOperationException("SceneTree unavailable");
                _overlay = new CoachOverlay(tree);
                _overlay.Mount();
                GD.Print($"[SpireAiCoach] {typeof(ModEntry).Assembly.GetName().Version} loaded. F8 opens the coach panel.");
            }
            catch (Exception ex) { GD.PrintErr($"[SpireAiCoach] Initialization failed: {ex.GetType().Name}"); }
        }).CallDeferred();
    }
}
