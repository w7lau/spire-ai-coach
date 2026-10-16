using Godot;

namespace SpireAiCoach.Mod;

// A small view of the existing controls. It owns no search or execution state.
internal sealed class CompactCoachPanel
{
    public PanelContainer View { get; }
    private readonly Label _battle;
    private readonly Label _status;
    private readonly Label _result;
    private readonly Button _stop;
    private readonly Button _winning;
    private readonly ProgressBar _activity;
    private readonly List<(Button View, Button Source)> _buttons = [];
    private bool _dragging;
    private bool _positioned;
    private Vector2 _dragOffset;

    public CompactCoachPanel(Theme theme, Button monteCarlo, Button frontier, Button execute,
        Button useVictory, Action expand, Action hide, Action stop)
    {
        View = new PanelContainer { Name = "CoachCompactPanel", Theme = theme, Visible = false };
        View.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color("111b27b8"), BorderColor = new Color("b8a37699"),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 9, CornerRadiusTopRight = 9, CornerRadiusBottomLeft = 9, CornerRadiusBottomRight = 9,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8
        });
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 6); View.AddChild(body);
        var heading = new HBoxContainer(); body.AddChild(heading);
        var title = new Label { Name = "CompactDragHandle", Text = "尖塔教练 · 精简",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Move, TooltipText = "拖动标题移动 · F8 收起 / 打开" };
        title.AddThemeColorOverride("font_color", CoachTheme.Gold); heading.AddChild(title);
        title.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouse)
            {
                _dragging = mouse.Pressed; _dragOffset = View.GetGlobalMousePosition() - View.Position;
                title.AcceptEvent();
            }
            else if (_dragging && input is InputEventMouseMotion)
            {
                View.Position = View.GetGlobalMousePosition() - _dragOffset;
                ClampPosition(); title.AcceptEvent();
            }
        };
        var full = new Button { Name = "CompactExpand", Text = "展开", TooltipText = "返回完整面板查看路线和设置" };
        full.Pressed += () => { _dragging = false; expand(); }; heading.AddChild(full);
        var close = new Button { Name = "CompactHide", Text = "收起", TooltipText = "F8 可重新打开；收起不会停止任务" };
        close.Pressed += () => { _dragging = false; hide(); }; heading.AddChild(close);
        _battle = Text("CompactBattle", 1); body.AddChild(_battle);
        _status = Text("CompactStatus", 1); _status.AddThemeColorOverride("font_color", CoachTheme.Gold); body.AddChild(_status);
        _activity = new ProgressBar { Name = "CompactActivity", ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 5), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore,
            TooltipText = "活动指示，不代表完成百分比" };
        body.AddChild(_activity);
        _result = Text("CompactResult", 2); _result.AddThemeColorOverride("font_color", CoachTheme.Muted); body.AddChild(_result);
        var search = new GridContainer { Columns = 2 }; body.AddChild(search);
        search.AddChild(Mirror("CompactBattleSearch", "整场试算", monteCarlo));
        search.AddChild(Mirror("CompactTurnSearch", "分支搜索", frontier));
        var actions = new GridContainer { Columns = 2 }; body.AddChild(actions);
        var play = Mirror("CompactExecute", "执行方案", execute); CoachTheme.Accent(play); actions.AddChild(play);
        _stop = new Button { Name = "CompactStop", Text = "停止 · Esc", Disabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "停止当前计算或执行；已出手动作正常结算" };
        _stop.Pressed += () => { if (!_stop.Disabled) stop(); }; actions.AddChild(_stop);
        _winning = Mirror("CompactUseVictory", "停止搜索并采用胜利路线", useVictory);
        _winning.Visible = false; body.AddChild(_winning);
        View.VisibilityChanged += () => { if (!View.Visible) _dragging = false; };
    }

    private static Label Text(string name, int lines) => new()
    {
        Name = name, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        CustomMinimumSize = new Vector2(0, lines * 28),
        MaxLinesVisible = lines, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        MouseFilter = Control.MouseFilterEnum.Pass
    };

    private Button Mirror(string name, string text, Button source)
    {
        var button = new Button { Name = name, Text = text, Disabled = true,
            TooltipText = source.TooltipText, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        // Recheck the source at click time; a stale view must not bypass its gate.
        button.Pressed += () => { if (!source.Disabled) source.EmitSignal(Button.SignalName.Pressed); };
        _buttons.Add((button, source)); return button;
    }

    public void Refresh(string battle, string status, string result, bool busy)
    {
        _battle.Text = _battle.TooltipText = battle;
        _status.Text = _status.TooltipText = status;
        _result.Text = _result.TooltipText = result;
        foreach (var (view, source) in _buttons) view.Disabled = source.Disabled;
        _stop.Disabled = !busy;
        _activity.Visible = busy;
        _activity.Indeterminate = busy && View.IsVisibleInTree();
        _winning.Visible = !_winning.Disabled;
        if (!Input.IsMouseButtonPressed(MouseButton.Left)) _dragging = false;
    }

    public void Resize()
    {
        var screen = View.GetViewport().GetVisibleRect().Size;
        View.Size = new Vector2(Math.Min(340, Math.Max(280, screen.X - 24)), 0);
        if (!_positioned)
        {
            View.Position = new Vector2(Math.Max(12, screen.X - View.Size.X - 20), 70);
            _positioned = true;
        }
        ClampPosition();
    }

    private void ClampPosition()
    {
        var screen = View.GetViewport().GetVisibleRect().Size;
        View.Position = new Vector2(Math.Clamp(View.Position.X, 8, Math.Max(8, screen.X - View.Size.X - 8)),
            Math.Clamp(View.Position.Y, 8, Math.Max(8, screen.Y - View.Size.Y - 8)));
    }
}
