using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx.Forms;
using MegaCrit.Sts2.Core.TestSupport;
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
        // Presentation factories have one owner after merging the native worker paths.
        typeof(LocalWorkerPool).Assembly.GetType("SpireAiCoach.Mod.LocalWorkerOverhead", true)!.GetMethod("Install")!.Invoke(null, null);
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

        if (TestMode.IsOn) throw new InvalidOperationException("Visual smoke must keep native TestMode off");
        var visualChecks = new List<object>();
        var visualFactories = (string[])mode.GetProperty("VisualFactories")!.GetValue(null)!;
        foreach (var type in typeof(NFormVfx).Assembly.GetTypes().Where(t => t != typeof(NFormVfx) && typeof(NFormVfx).IsAssignableFrom(t)))
        {
            var create = type.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (create == null) continue;
            if (!visualFactories.Contains(type.Name + ".Create")) throw new InvalidOperationException("Form factory was not discovered: " + type.Name);
            bool sceneRequired = false;
            try { create.Invoke(null, [creature]); }
            catch (TargetInvocationException ex) when (ex.InnerException is NullReferenceException) { sceneRequired = true; }
            if (!sceneRequired) throw new InvalidOperationException("Regular form visual behavior changed: " + type.Name);
            try
            {
                active.SetValue(null, true);
                if (create.Invoke(null, [creature]) != null || TestMode.IsOn || creature.CurrentHp != hp)
                    throw new InvalidOperationException("Scene-free form factory changed native rules: " + type.Name);
            }
            finally { active.SetValue(null, false); }
            visualChecks.Add(new { factory = type.Name, regular_requires_scene = sceneRequired, numerical_returns_null = true });
        }

        var overlay = new CoachOverlay(tree); overlay.Mount();
        var layer = tree.Root.GetNode<CanvasLayer>("SpireAiCoach");
        var panel = layer.GetNode<PanelContainer>("CoachPanel");
        var toggle = layer.GetNode<Button>("CoachToggle");
        CheckBox CheckBox(string name) => layer.FindChild(name, true, false) as CheckBox
            ?? throw new InvalidOperationException("Missing checkbox: " + name);
        var skip = CheckBox("LocalSkipVerification");
        if (!skip.ButtonPressed || ((Control)layer.FindChild("LocalAdvancedOptions", true, false)!).Visible ||
            ((Control)layer.FindChild("CoachDiagnostics", true, false)!).Visible ||
            ((Control)layer.FindChild("AiOptions", true, false)!).Visible ||
            ((Control)layer.FindChild("LocalResultDetails", true, false)!).Visible ||
            ((Control)layer.FindChild("LocalProgressDetails", true, false)!).Visible)
            throw new InvalidOperationException("Simplified UI defaults did not apply");
        var visibility = typeof(CoachOverlay).GetMethod("ApplyPanelContext", fields)!;
        var manual = typeof(CoachOverlay).GetField("_manualPanelVisibility", fields)!;
        var visibilityChecks = new List<object>();
        void CheckVisibility(string name, bool expected)
        {
            if (panel.Visible != expected) throw new InvalidOperationException("Panel visibility failed: " + name);
            visibilityChecks.Add(new { name, visible = panel.Visible });
        }
        CheckVisibility("outside combat initially hidden", false);
        if (toggle.Visible) throw new InvalidOperationException("Corner entry must default to hidden");
        async Task KeyStroke(Key key)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
            for (int i = 0; i < 3; i++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        await KeyStroke(Key.F9); await KeyStroke(Key.F10);
        CheckVisibility("former shortcuts do not open panel", false);
        if (typeof(CoachOverlay).GetField("_request", fields)!.GetValue(overlay) != null ||
            ((Control)typeof(CoachOverlay).GetField("_settingsPanel", fields)!.GetValue(overlay)!).Visible)
            throw new InvalidOperationException("Former shortcuts triggered AI or settings");
        await KeyStroke(Key.F8); CheckVisibility("single shortcut opens once while held", true);
        await KeyStroke(Key.F8); CheckVisibility("single shortcut closes panel", false);
        var entrySetting = CheckBox("ShowOverlayButton");
        entrySetting.ButtonPressed = true;
        if (!toggle.Visible) throw new InvalidOperationException("Entry opt-in did not apply");
        var interfaceStore = (SettingsStore)typeof(CoachOverlay).GetField("_store", fields)!.GetValue(overlay)!;
        if (!interfaceStore.Load().Settings.ShowOverlayButton) throw new InvalidOperationException("Entry preference was not saved");
        entrySetting.ButtonPressed = false;
        if (toggle.Visible || interfaceStore.Load().Settings.ShowOverlayButton)
            throw new InvalidOperationException("Hidden entry preference was not saved");
        if (layer.FindChild("LocalAlgorithmHeading", true, false) is not Label { Text: "本地整场计算 · 选择搜索算法" })
            throw new InvalidOperationException("Local algorithms have no shared whole-battle heading");
        foreach (var (node, order) in new[] {
            ("LocalBattleSearch", LocalSearchOrder.MonteCarlo),
            ("LocalTurnSearch", LocalSearchOrder.TurnFrontier) })
        {
            var button = (Button)layer.FindChild(node, true, false)!;
            if (!button.Text.StartsWith(LocalCalculation.Name(order) + "算法\n", StringComparison.Ordinal) ||
                button.Text.Split('\n').Length != 2 || string.IsNullOrWhiteSpace(button.TooltipText))
                throw new InvalidOperationException("Search button does not identify and explain its algorithm");
        }
        var algorithmButtons = new[] { "LocalBattleSearch", "LocalTurnSearch" }.Select(node =>
        {
            var button = (Button)layer.FindChild(node, true, false)!;
            return new { node, text = button.Text, tooltip = button.TooltipText };
        }).ToArray();
        toggle.EmitSignal(Button.SignalName.Pressed);
        for (int i = 0; i < 4; i++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        CheckVisibility("manual outside-combat open survives frame and snapshot updates", true);
        toggle.EmitSignal(Button.SignalName.Pressed);
        CheckVisibility("manual toggle hides", false);
        // Opaque context tokens test panel transitions only. They are never
        // installed as game combat states or passed to gameplay code.
        var battle = new object();
        visibility.Invoke(overlay, [battle, false]); CheckVisibility("enter battle opens panel with entry hidden", true);
        manual.SetValue(overlay, false);
        visibility.Invoke(overlay, [battle, false]); CheckVisibility("manual battle hide persists", false);
        visibility.Invoke(overlay, [battle, true]); CheckVisibility("execution starts hidden", false);
        manual.SetValue(overlay, true);
        visibility.Invoke(overlay, [battle, true]); CheckVisibility("manual execution open is retained", true);
        visibility.Invoke(overlay, [battle, false]); CheckVisibility("execution stops in battle shows panel", true);
        visibility.Invoke(overlay, [null, false]); CheckVisibility("combat exit hides", false);
        visibility.Invoke(overlay, [new object(), false]); CheckVisibility("next combat resets manual override and opens panel", true);
        var autoPanel = CheckBox("AutoShowCombatPanel");
        if (!autoPanel.ButtonPressed || !interfaceStore.Load().Settings.AutoShowCombatPanel)
            throw new InvalidOperationException("Combat popup must default to enabled");
        autoPanel.ButtonPressed = false;
        CheckVisibility("turning off automatic opening keeps current settings visible", true);
        visibility.Invoke(overlay, [new object(), false]); CheckVisibility("manual-only mode hides on next battle", false);
        manual.SetValue(overlay, true);
        visibility.Invoke(overlay, [new object(), false]); CheckVisibility("manual-only mode does not auto-reopen for another battle", false);
        if (interfaceStore.Load().Settings.AutoShowCombatPanel || toggle.Visible)
            throw new InvalidOperationException("Entry and popup preferences interfered or failed to persist");
        autoPanel.ButtonPressed = true;
        visibility.Invoke(overlay, [new object(), false]); CheckVisibility("re-enabled automatic mode opens next battle", true);
        if (!interfaceStore.Load().Settings.AutoShowCombatPanel)
            throw new InvalidOperationException("Automatic mode preference was not saved");
        visibility.Invoke(overlay, [null, false]);
        toggle.EmitSignal(Button.SignalName.Pressed);
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
        var optionChecks = new List<object>();
        CheckBox("LocalIncludePotions").ButtonPressed = true;
        CheckBox("LocalStopOnZeroLoss").ButtonPressed = false;
        foreach (bool skipFinal in new[] { false, true })
        {
            skip.ButtonPressed = skipFinal;
            var preferences = store.Load().Settings;
            if (preferences.LocalSkipFinalVerification != skipFinal || !preferences.LocalIncludePotions || preferences.LocalStopOnZeroLoss)
                throw new InvalidOperationException("Local checkbox changes did not persist together");
            foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
            {
                var frozen = (LocalSearchRequest)configure.Invoke(overlay, [captured, order])!;
                if (frozen.SkipFinalVerification != skipFinal || !frozen.IncludePotions || frozen.StopOnZeroLoss)
                    throw new InvalidOperationException("Local checkbox state diverges between algorithms");
                optionChecks.Add(new { algorithm = order.ToString(), skip_final_verification = skipFinal });
            }
        }
        CheckBox("LocalIncludePotions").ButtonPressed = false;
        CheckBox("LocalStopOnZeroLoss").ButtonPressed = true;
        foreach (var order in new[] { LocalSearchOrder.MonteCarlo, LocalSearchOrder.TurnFrontier })
        {
            var request = (LocalSearchRequest)configure.Invoke(overlay, [captured, order])!;
            if (request.SearchOrder != order || request.Workers != 8 || request.MaxNodes != 257 || request.MaxRounds != 130 ||
                request.BudgetSeconds != 125 || request.TargetVictoryRounds != 120 ||
                (int)count.Invoke(pool, [request.Workers, request.AdaptiveWorkers])! != 8)
                throw new InvalidOperationException("Merged controls no longer configure both algorithms or manual concurrency");
        }
        var layouts = new List<object>();
        var progressPanel = (LocalProgressPanel)typeof(CoachOverlay).GetField("_localProgress", fields)!.GetValue(overlay)!;
        var visual = (Control)typeof(LocalProgressPanel).GetField("_visual", fields)!.GetValue(progressPanel)!;
        var map = typeof(LocalProgressPanel).GetField("_map", fields)!.GetValue(progressPanel)!;
        var pulse = (Godot.Timer)map.GetType().GetField("_pulse", fields)!.GetValue(map)!;
        void PreviewRoutes()
        {
            // Presentation fixtures only: not native outcomes, benchmark evidence,
            // game combat states, executable checkpoints or generated plans.
            progressPanel.Begin(captured);
            for (int worker = 0; worker < 16; worker++)
            {
                var preview = new LocalSimState(6, 87 - worker, 87, 48, 2, "壁垒", ["全身撞击+"], 2,
                    [new(1, "演示敌人", 84 + worker, 180, 0, "", "攻击")]);
                LocalSimEvent[] events = [new(1, 6, "打出「武装+」。", "升级手牌。"),
                    new(2, 6, "打出「防御+」。", "格挡 40→48"), new(3, 6, "打出「全身撞击+」。", "敌方生命 132→84")];
                progressPanel.Accept(new(captured.Id, captured.SnapshotId, worker, 16, 1, worker + 1,
                    5, 64, worker == 0 ? 1 : 0, 5000, 60, "正在探索", preview, events,
                    Best: worker == 0 ? new(1, 87, 87, 87, 6, 0) : null));
            }
            progressPanel.Accept(new("stale-job", captured.SnapshotId, 0, 16, 9, 100, 10, 64, 2, 9000, 60,
                "过期数据", null, []));
        }
        foreach (var size in new[] { new Vector2I(1280, 720), new Vector2I(1920, 1080) })
        {
            tree.Root.ContentScaleSize = size;
            tree.Root.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
            typeof(CoachOverlay).GetMethod("Resize", fields)!.Invoke(overlay, null);
            ((Label)typeof(CoachOverlay).GetField("_battle", fields)!.GetValue(overlay)!).Text =
                "第 6 回合 · 生命 87/87 · 能量 3";
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
            PreviewRoutes();
            for (int i = 0; i < 4; i++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            var technical = (Control)layer.FindChild("LocalProgressDetails", true, false)!;
            var caption = (Label)typeof(LocalProgressPanel).GetField("_caption", fields)!.GetValue(progressPanel)!;
            if (!visual.IsVisibleInTree() || pulse.IsStopped() || technical.Visible || caption.Text.Contains("过期"))
                throw new InvalidOperationException("Route visualization failed to show valid progress with technical records hidden");
            rect = panel.GetGlobalRect();
            if (Descendants(panel).OfType<Control>().Where(c => c.IsVisibleInTree()).Any(c =>
                c.GetGlobalRect().Position.X < rect.Position.X - 1 || c.GetGlobalRect().End.X > rect.End.X + 1))
                throw new InvalidOperationException("Route visualization exceeds panel width");
            var routePicker = (OptionButton)layer.FindChild("LocalProgressWorker", true, false)!;
            routePicker.Select(15); routePicker.EmitSignal(OptionButton.SignalName.ItemSelected, 15);
            var healthLabel = (Label)typeof(LocalProgressPanel).GetField("_health", fields)!.GetValue(progressPanel)!;
            if (!healthLabel.Text.Contains("72/87")) throw new InvalidOperationException("Route sixteen cannot be selected");
            routePicker.Select(0); routePicker.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
            ((ScrollContainer)typeof(CoachOverlay).GetField("_contentScroll", fields)!.GetValue(overlay)!).ScrollVertical = 120;
            if (DisplayServer.GetName() != "headless")
            {
                await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                tree.Root.GetTexture().GetImage().SavePng(Path.Combine(root, $"coach-routes-{size.X}x{size.Y}.png"));
            }
            visual.Hide();
            if (!pulse.IsStopped()) throw new InvalidOperationException("Hidden visualization keeps animating");
            visual.Show();
            if (pulse.IsStopped()) throw new InvalidOperationException("Reopened visualization did not resume");
            progressPanel.Finish("完成", false);
            if (!pulse.IsStopped() || visual.Visible || !progressPanel.View.Visible)
                throw new InvalidOperationException("Finished visualization did not stop and collapse");
            progressPanel.Accept(new(captured.Id, captured.SnapshotId, 0, 16, 100, 200, 20, 64, 2, 9000, 60,
                "已结束后的进度", null, []));
            if (caption.Text != "完成") throw new InvalidOperationException("Late progress overwrote the completed view");
            progressPanel.Finish("", true);
            var settings = (Control)typeof(CoachOverlay).GetField("_settingsPanel", fields)!.GetValue(overlay)!;
            var aiOptions = (Control)layer.FindChild("AiOptions", true, false)!;
            var advanced = (Control)panel.FindChild("LocalAdvancedOptions", recursive: true, owned: false);
            var diagnostics = (Control)panel.FindChild("CoachDiagnostics", recursive: true, owned: false);
            foreach (var expanded in new[] { settings, advanced, diagnostics })
            {
                if (expanded == settings) aiOptions.Show();
                expanded.Show();
                for (int i = 0; i < 4; i++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                rect = panel.GetGlobalRect();
                if (rect.End.Y > tree.Root.GetVisibleRect().End.Y + 1 || Descendants(panel).OfType<Control>()
                    .Where(c => c.IsVisibleInTree()).Any(c => c.GetGlobalRect().Position.X < rect.Position.X - 1 || c.GetGlobalRect().End.X > rect.End.X + 1))
                    throw new InvalidOperationException("Expanded coach section exceeds the viewport");
                expanded.Hide();
                if (expanded == settings) aiOptions.Hide();
            }
            ((ScrollContainer)typeof(CoachOverlay).GetField("_contentScroll", fields)!.GetValue(overlay)!).ScrollVertical = 0;
            layouts.Add(new { width = size.X, height = size.Y, measured_width = rect.Size.X, measured_height = rect.Size.Y,
                controls = controls.Length, horizontal_overflow = overflow.Length, primary_actions_fixed = true,
                expanded_sections_checked = 3, visualization_workers = 16, visualization_rendered = true,
                hidden_and_finished_animation_stops = true, route_sixteen_selectable = true });
        }
        layer.QueueFree();
        LocalWire.Write(Path.Combine(root, "integration-presentation-summary.json"), new
        {
            passed = true, version = typeof(CoachOverlay).Assembly.GetName().Version?.ToString(3),
            regular_scene_dependency_preserved = nativeNeedsScene, data_callback_preserves_unsubscribe = true,
            creature_hp_unchanged = true, no_combat_scene = true, no_worker_started = true, layouts
            , merged_budget_controls_persisted = true, both_algorithm_requests_checked = true,
            manual_worker_limit_preserved = 8, visibility_checks = visibilityChecks,
            visibility_contexts_are_ui_only = true
            , entry_default_hidden = true, entry_opt_in_persisted = true, only_f8_global_shortcut = true,
            previous_f9_f10_have_no_action = true, algorithm_names_match_results = true,
            algorithm_buttons = algorithmButtons
            , combat_popup_default_enabled = true, manual_only_mode_persisted = true,
            entry_and_popup_are_independent = true
            , skip_final_verification_defaults_checked = true, checkbox_options_persisted = true,
            checkbox_algorithm_checks = optionChecks, advanced_and_diagnostics_default_hidden = true,
            visualization_input_is_presentation_fixture_only = true, stale_and_late_progress_rejected = true,
            visual_factories = visualFactories,
            form_visual_checks = visualChecks, global_test_mode_unchanged = !TestMode.IsOn
        });
    }

    private static IEnumerable<Node> Descendants(Node parent)
    {
        foreach (var child in parent.GetChildren())
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
