using System.Reflection;
using Godot;
using SpireAiCoach.Core;
using SpireAiCoach.Mod;

namespace SpireLocalIntegration;

internal static class CompactOverlayIntegration
{
    public static async Task Run(string root, SceneTree tree, CoachOverlay overlay, CanvasLayer layer)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(CoachOverlay).GetField(name, flags)!.GetValue(overlay)!;
        void Set(string name, object? value) => typeof(CoachOverlay).GetField(name, flags)!.SetValue(overlay, value);
        void Call(string name, params object?[] args) => typeof(CoachOverlay).GetMethod(name, flags)!.Invoke(overlay, args);
        Button Button(string name) => (Button)layer.FindChild(name, true, false)!;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("Compact UI: " + message); }
        async Task Frames() { for (int i=0;i<4;i++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame); }
        var full = Field<PanelContainer>("_panel");
        var compact = layer.GetNode<PanelContainer>("CoachCompactPanel");
        var store = Field<SettingsStore>("_store");
        Check(!compact.Visible, "default must retain full layout");
        Button("CoachCompactMode").EmitSignal(Godot.Button.SignalName.Pressed);
        await Frames();
        Check(compact.Visible && !full.Visible && store.Load().Settings.CompactOverlay, "switch or persistence failed");
        Check(!Field<Control>("_settingsPanel").IsVisibleInTree() && !Field<Control>("_contentScroll").IsVisibleInTree(), "settings leaked into compact view");
        var box=(StyleBoxFlat)compact.GetThemeStylebox("panel");
        Check(box.BgColor.A > .5f && box.BgColor.A < .85f && compact.Modulate.A == 1, "background must be translucent without fading text");

        using (var activityRequest = new CancellationTokenSource())
        {
            Set("_request", activityRequest); Set("_localAnalyzing", true);
            Set("_compactSearchStarted", System.Environment.TickCount64 - 2500);
            var progress = Field<LocalProgressPanel>("_localProgress");
            progress.Begin(new("compact-test", "snapshot", [], "native", 1, [], false));
            Field<Label>("_status").Text = "准备计算…"; Call("RefreshCompactPanel");
            var activity = (ProgressBar)compact.FindChild("CompactActivity", true, false)!;
            var status = (Label)compact.FindChild("CompactStatus", true, false)!;
            var detail = (Label)compact.FindChild("CompactResult", true, false)!;
            Check(status.Text.StartsWith("准备中") && activity.Visible && activity.Indeterminate, "preparation activity missing");
            progress.Accept(new("compact-test", "snapshot", 0, 8, 1, 43, 42, 64, 12, 2500, 60,
                "试走路线", null, [], RootBranches: 7));
            Field<Label>("_status").Text = "正在计算…"; Call("RefreshCompactPanel");
            Check(status.Text.StartsWith("搜索中") && detail.Text.Contains("42 条") && detail.Text.Contains("12 条"), "search counters missing");
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (DisplayServer.GetName() != "headless") tree.Root.GetTexture().GetImage().SavePng(Path.Combine(root, "coach-compact-progress.png"));
            Field<Label>("_status").Text = "正在用一个实例核对战斗起点…"; Call("RefreshCompactPanel");
            Check(status.Text.StartsWith("核对起点") && !detail.Text.Contains("42 条"), "stale search hides compatibility check");
            Field<Label>("_status").Text = "正在复核最终路线…"; Call("RefreshCompactPanel");
            Check(status.Text.StartsWith("复核中"), "verification mislabeled");
            Set("_request", null); Set("_localAnalyzing", false); progress.Finish("", true); Call("RefreshCompactPanel");
            Check(!activity.Visible && !activity.Indeterminate, "idle activity did not stop");
        }

        foreach (var (sourceName, targetName) in new[] { ("_localAnalyze","CompactBattleSearch"), ("_turnAnalyze","CompactTurnSearch"), ("_execute","CompactExecute") })
        {
            var source=Field<Button>(sourceName); int forwarded=0;
            void Count() => forwarded++;
            source.Pressed += Count;
            source.Disabled=true; Call("RefreshCompactPanel");
            Check(Button(targetName).Disabled, "disabled gate not mirrored");
            Button(targetName).EmitSignal(Godot.Button.SignalName.Pressed);
            Check(forwarded==0, "disabled source invoked");
            source.Disabled=false; Call("RefreshCompactPanel");
            Check(!Button(targetName).Disabled, "enabled control not mirrored");
            // Force a stale rendered button; click must still consult the source.
            source.Disabled=true; Button(targetName).EmitSignal(Godot.Button.SignalName.Pressed);
            Check(forwarded==0, "stale view bypassed source gate");
            source.Disabled=false; Button(targetName).EmitSignal(Godot.Button.SignalName.Pressed);
            Check(forwarded==1 && Field<object?>("_request")==null && !Field<bool>("_executing"), "outside-combat button bypassed product validation");
            source.Pressed-=Count;
        }
        using (var cancellation=new CancellationTokenSource())
        {
            Set("_request",cancellation); Call("RefreshCompactPanel");
            Button("CompactExpand").EmitSignal(Godot.Button.SignalName.Pressed);
            Button("CoachCompactMode").EmitSignal(Godot.Button.SignalName.Pressed);
            Check(ReferenceEquals(Field<object?>("_request"),cancellation) && !cancellation.IsCancellationRequested,
                "mode switch replaced or canceled a request");
            Button("CompactStop").EmitSignal(Godot.Button.SignalName.Pressed);
            Check(cancellation.IsCancellationRequested && Field<object?>("_request")==null, "stop failed to cancel existing request");
        }
        using (var cancellation=new CancellationTokenSource())
        {
            Set("_execution",cancellation); Set("_executing",true); Call("RefreshCompactPanel");
            Button("CompactStop").EmitSignal(Godot.Button.SignalName.Pressed);
            Check(cancellation.IsCancellationRequested, "stop failed to cancel execution token");
            Set("_execution",null); Set("_executing",false);
        }
        Field<Button>("_useVictory").Disabled=false; Call("RefreshCompactPanel");
        Check(Button("CompactUseVictory").Visible, "available victory return is missing");
        Field<Button>("_useVictory").Disabled=true; Call("RefreshCompactPanel");
        Check(!Button("CompactUseVictory").Visible, "unavailable victory action adds clutter");
        var battle=new object();
        Call("ApplyPanelContext",battle,false);
        Call("ApplyPanelContext",battle,true);
        Check(compact.Visible && !full.Visible, "execution hid compact stop");
        Set("_manualPanelVisibility",false); Call("ApplyPanelContext",battle,true);
        Check(!compact.Visible, "manual hide ignored during execution");
        Call("ApplyPanelContext",null,false);
        Call("SetPanelVisible",true);
        // Render presentation-only text, without manufacturing a native route.
        var layouts=new List<object>();
        foreach (var size in new[] { new Vector2I(1280,720), new Vector2I(1920,1080) })
        {
            tree.Root.ContentScaleSize=size; tree.Root.ContentScaleMode=Window.ContentScaleModeEnum.CanvasItems;
            Call("Resize"); await Frames();
            compact.Position=new Vector2(tree.Root.GetVisibleRect().Size.X-360,70);
            Field<Label>("_battle").Text="第 6 回合 · 生命 87/91 · 能量 3";
            Field<Label>("_status").Text="计算完成 · 12.4 秒";
            Field<Label>("_freshness").Text="预计获胜 · 87/91 血\n剩余 18 步 · 未独立复核";
            Call("RefreshCompactPanel");
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var rect=compact.GetGlobalRect(); var viewport=tree.Root.GetVisibleRect();
            Check(rect.Size.X<=342 && rect.Size.Y<=300 && rect.Position.X>=0 && rect.End.X<=viewport.End.X && rect.End.Y<=viewport.End.Y, "compact bounds overflow");
            Check(Descendants(compact).OfType<Control>().Where(c=>c.IsVisibleInTree()).All(c=>rect.Grow(1).Encloses(c.GetGlobalRect())), "visible controls overflow");
            foreach (var labelName in new[] { "CompactBattle", "CompactStatus", "CompactResult" })
            {
                var label=(Label)compact.FindChild(labelName,true,false)!;
                Check(!string.IsNullOrWhiteSpace(label.Text) && label.Size.Y>=22 && label.Size.X>=250,
                    "status text collapsed: "+labelName);
                if (labelName == "CompactResult")
                    Check(label.GetLineCount() == 2 && label.GetVisibleLineCount() == 2,
                        $"result lines clipped: total={label.GetLineCount()}, visible={label.GetVisibleLineCount()}, height={label.Size.Y}");
            }
            if (DisplayServer.GetName()!="headless") tree.Root.GetTexture().GetImage().SavePng(Path.Combine(root,$"coach-compact-{size.X}x{size.Y}.png"));
            // A dragged window is kept on screen when the viewport changes.
            compact.Position=new Vector2(9999,9999); Call("Resize"); await Frames();
            Check(viewport.Grow(1).Encloses(compact.GetGlobalRect()), "offscreen position not clamped");
            layouts.Add(new { width=size.X,height=size.Y,panel_width=rect.Size.X,panel_height=rect.Size.Y });
        }
        Button("CompactHide").EmitSignal(Godot.Button.SignalName.Pressed); await Frames();
        Check(!compact.Visible && !full.Visible, "hide did not persist");
        Call("TogglePanel"); await Frames(); Check(compact.Visible,"F8 toggle target lost compact mode");
        Button("CompactExpand").EmitSignal(Godot.Button.SignalName.Pressed); await Frames();
        Check(full.Visible && !compact.Visible && !store.Load().Settings.CompactOverlay, "expand failed or preference not saved");
        LocalWire.Write(Path.Combine(root,"integration-compact-ui.json"),new { passed=true, version=typeof(CoachOverlay).Assembly.GetName().Version?.ToString(3),
            layouts, opacity=box.BgColor.A, controls_mirror_existing_gates=true, stale_click_rejected=true,
            outside_combat_actions_rejected=true, search_and_execution_cancel_tokens_checked=true,
            execution_keeps_stop_visible=true, hide_and_expand_checked=true, mode_persisted=true,
            mode_switch_preserves_request=true, status_labels_have_nonzero_area=true, result_both_lines_visible=true,
            compact_activity_phases_and_counters=true, idle_activity_stops=true, fixture_only_no_combat_executed=true });
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren()) { yield return child; foreach(var next in Descendants(child)) yield return next; }
    }
}
