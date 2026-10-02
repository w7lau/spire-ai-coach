using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireAiCoach.Mod;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    private static CoachOverlay? _overlay;
    public static void Initialize()
    {
        if (_overlay != null) return;
        Callable.From(() =>
        {
            try
            {
                if (Engine.GetMainLoop() is not SceneTree tree) throw new InvalidOperationException("SceneTree unavailable");
                _overlay = new CoachOverlay(tree);
                _overlay.Mount();
                GD.Print("[SpireAiCoach] 0.1.0 loaded. F8 panel / F9 analyze / F10 settings.");
            }
            catch (Exception ex) { GD.PrintErr($"[SpireAiCoach] Initialization failed: {ex.GetType().Name}"); }
        }).CallDeferred();
    }
}
