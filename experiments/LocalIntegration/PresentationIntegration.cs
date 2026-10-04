using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

// Entry has already verified the executable and owner marker in a private
// installation/userdata. No live run, saved player state or API is used.
internal static class PresentationIntegration
{
    public static async Task Run(string root, SceneTree tree)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        if (NCombatRoom.Instance != null) throw new InvalidOperationException("Presentation smoke requires no combat scene");
        var mode = typeof(LocalWorkerPool).Assembly.GetType("SpireAiCoach.Mod.LocalWorkerDataMode", throwOnError: true)!;
        mode.GetMethod("Install")!.Invoke(null, [null]);
        if ((bool)mode.GetProperty("Available")!.GetValue(null)! != true)
            throw new InvalidOperationException("Native presentation patch installation failed");
        var active = mode.GetProperty("Active")!;
        var monster = ModelDb.Monster<SoulNexus>().ToMutable();
        var creature = new Creature(monster, CombatSide.Enemy, null);
        var death = typeof(SoulNexus).GetMethod("AfterDeath", fields | BindingFlags.DeclaredOnly)!;
        var handler = (Action<Creature>)death.CreateDelegate(typeof(Action<Creature>), monster);
        int notifications = 0;
        Action<Creature> sentinel = _ => notifications++;
        creature.Died += sentinel; creature.Died += handler;
        var eventField = typeof(Creature).GetField("Died", fields)!;
        bool nativeNeedsScene = false;
        try { death.Invoke(monster, [creature]); }
        catch (TargetInvocationException ex) when (ex.InnerException is NullReferenceException) { nativeNeedsScene = true; }
        if (!nativeNeedsScene) throw new InvalidOperationException("Regular callback's original scene dependency changed");
        creature.Died += handler;
        int hp = creature.CurrentHp;
        try
        {
            active.SetValue(null, true);
            death.Invoke(monster, [creature]);
            var listeners = ((Delegate?)eventField.GetValue(creature))?.GetInvocationList() ?? [];
            if (creature.CurrentHp != hp || notifications != 0 || listeners.Length != 1 || !listeners[0].Equals(sentinel))
                throw new InvalidOperationException("Native callback altered game data or failed to unsubscribe its own event");
        }
        finally { active.SetValue(null, false); creature.Died -= sentinel; }

        var overlay = new CoachOverlay(tree); overlay.Mount();
        var layer = tree.Root.GetNode<CanvasLayer>("SpireAiCoach");
        // Exercise the merged budget controls in this private userdata without
        // launching a search or substituting a route for a real combat.
        SpinBox Spin(string name) => layer.FindChild(name, true, false) as SpinBox
            ?? throw new InvalidOperationException("Missing merged product control: " + name);
        Spin("LocalWorkers").Value = 8;
        Spin("LocalMaxAttempts").Value = 257;
        Spin("LocalMaxRounds").Value = 130;
        Spin("LocalSearchSeconds").Value = 125;
        Spin("LocalTargetVictoryRounds").Value = 120;
        ((Button)layer.FindChild("LocalSaveSettings", true, false)!).EmitSignal(Button.SignalName.Pressed);
        var store = (SettingsStore)typeof(CoachOverlay).GetField("_store", fields)!.GetValue(overlay)!;
        var saved = store.Load().Settings;
        if (saved.LocalWorkers != 8 || saved.LocalMaxAttempts != 257 || saved.LocalMaxRounds != 130 ||
            saved.LocalSearchSeconds != 125 || saved.LocalTargetVictoryRounds != 120 || Spin("LocalTargetVictoryRounds").MaxValue != 130)
            throw new InvalidOperationException("Merged budget controls did not persist or update the horizon");
        var configure = typeof(CoachOverlay).GetMethod("ConfigureLocalRequest", fields)!;
        var pool = (LocalWorkerPool)typeof(CoachOverlay).GetField("_localPool", fields)!.GetValue(overlay)!;
        var count = typeof(LocalWorkerPool).GetMethod("Count", fields)!;
        var captured = new LocalSearchRequest("ui-only", "snapshot", [], "hash", 0, [], false);
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            var request = (LocalSearchRequest)configure.Invoke(overlay, [captured, order])!;
            if (request.SearchOrder != order || request.Workers != 8 || request.MaxNodes != 257 || request.MaxRounds != 130 ||
                request.BudgetSeconds != 125 || request.TargetVictoryRounds != 120 ||
                (int)count.Invoke(pool, [request.Workers, request.AdaptiveWorkers])! != 8)
                throw new InvalidOperationException("Merged controls no longer configure both algorithms or manual concurrency");
        }
        var panel = layer.GetNode<PanelContainer>("CoachPanel"); panel.Show();
        var layouts = new List<object>();
        foreach (var size in new[] { new Vector2I(1280, 720), new Vector2I(1920, 1080) })
        {
            tree.Root.ContentScaleSize = size;
            tree.Root.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
            typeof(CoachOverlay).GetMethod("Resize", fields)!.Invoke(overlay, null);
            ((Label)typeof(CoachOverlay).GetField("_battle", fields)!.GetValue(overlay)!).Text =
                "第 6 轮 · 出牌阶段\n生命 87/87 · 格挡 28 · 能量 3\n手牌 7 / 抽牌 12 / 弃牌 9 / 消耗 4";
            ((RichTextLabel)typeof(CoachOverlay).GetField("_advice", fields)!.GetValue(overlay)!).Text =
                "本地计算完成 · 战后生命 87/87\n第 1 回合\n1. 打出防御，获得格挡。\n2. 打出全身撞击，击败当前目标。\n第 2 回合\n3. 结束回合并等待结算。";
            for (int i = 0; i < 4; i++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            var rect = panel.GetGlobalRect();
            var viewport = tree.Root.GetVisibleRect();
            var controls = Descendants(panel).OfType<Control>().Where(c => c.IsVisibleInTree()).ToArray();
            var overflow = controls.Where(c => c.GetGlobalRect().Position.X < rect.Position.X - 1 ||
                c.GetGlobalRect().End.X > rect.End.X + 1).Select(c => c.GetPath().ToString()).ToArray();
            if (overflow.Length > 0) throw new InvalidOperationException("Coach panel horizontal overflow: " + string.Join(", ", overflow));
            if (rect.End.Y > viewport.End.Y + 1) throw new InvalidOperationException("Coach panel exceeds the viewport height");
            var search = panel.FindChild("LocalTurnSearch", recursive: true, owned: false) as Button;
            if (search == null || search.GetGlobalRect().Position.Y > rect.Position.Y + 250)
                throw new InvalidOperationException("Primary calculation button left the fixed action area");
            if (DisplayServer.GetName() != "headless")
            {
                await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                tree.Root.GetTexture().GetImage().SavePng(Path.Combine(root, $"coach-{size.X}x{size.Y}.png"));
            }
            var settings = (Control)typeof(CoachOverlay).GetField("_settingsPanel", fields)!.GetValue(overlay)!;
            var advanced = (Control)panel.FindChild("LocalAdvancedOptions", recursive: true, owned: false);
            var diagnostics = (Control)panel.FindChild("CoachDiagnostics", recursive: true, owned: false);
            foreach (var expanded in new[] { settings, advanced, diagnostics })
            {
                expanded.Show();
                for (int i = 0; i < 4; i++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                rect = panel.GetGlobalRect();
                if (rect.End.Y > tree.Root.GetVisibleRect().End.Y + 1 || Descendants(panel).OfType<Control>()
                    .Where(c => c.IsVisibleInTree()).Any(c => c.GetGlobalRect().Position.X < rect.Position.X - 1 || c.GetGlobalRect().End.X > rect.End.X + 1))
                    throw new InvalidOperationException("Expanded coach section exceeds the viewport");
                expanded.Hide();
            }
            ((ScrollContainer)typeof(CoachOverlay).GetField("_contentScroll", fields)!.GetValue(overlay)!).ScrollVertical = 0;
            layouts.Add(new { width = size.X, height = size.Y, measured_width = rect.Size.X, measured_height = rect.Size.Y,
                controls = controls.Length, horizontal_overflow = overflow.Length, primary_actions_fixed = true,
                expanded_sections_checked = 3 });
        }
        layer.QueueFree();
        LocalWire.Write(Path.Combine(root, "integration-presentation-summary.json"), new
        {
            passed = true, version = typeof(CoachOverlay).Assembly.GetName().Version?.ToString(3),
            regular_scene_dependency_preserved = nativeNeedsScene, data_callback_preserves_unsubscribe = true,
            creature_hp_unchanged = true, no_combat_scene = true, no_worker_started = true, layouts
            , merged_budget_controls_persisted = true, both_algorithm_requests_checked = true,
            manual_worker_limit_preserved = 8
        });
    }

    private static IEnumerable<Node> Descendants(Node parent)
    {
        foreach (var child in parent.GetChildren())
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
