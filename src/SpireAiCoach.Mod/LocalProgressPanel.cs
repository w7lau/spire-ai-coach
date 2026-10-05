using Godot;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// UI consumes immutable worker telemetry only; never a live game object or command.
public sealed class LocalProgressPanel
{
    public VBoxContainer View { get; } = new() { Name = "LocalRouteExplorer", Visible = false };
    private readonly VBoxContainer _visual = new() { Visible = false };
    private readonly VBoxContainer _details = new() { Name = "LocalProgressDetails", Visible = false };
    private readonly Label _caption = Wrap("寻找损伤更低的路线…");
    private readonly Label _best = Wrap("");
    private readonly OptionButton _worker = new() { Name = "LocalProgressWorker" };
    private readonly Label _budgetText = Wrap("");
    private readonly ProgressBar _budget = new() { MaxValue = 100, ShowPercentage = false };
    private readonly ProgressBar _hp = new() { MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8) };
    private readonly ProgressBar _enemyHp = new() { MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8) };
    private readonly Label _health = Wrap("");
    private readonly Label _enemies = Wrap("");
    private readonly Label _action = Wrap("");
    private readonly Label _changes = Wrap("");
    private readonly LocalRouteMap _map = new();
    private readonly RichTextLabel _overview = TextBox(90);
    private readonly RichTextLabel _state = TextBox(130);
    private readonly RichTextLabel _events = TextBox(220);
    private LocalProgressBook? _book;
    private bool _accepting;

    public LocalProgressPanel()
    {
        View.AddChild(CoachTheme.Disclosure("路线探索", _visual)); View.AddChild(_visual);
        _caption.AddThemeColorOverride("font_color", CoachTheme.Gold);
        _best.AddThemeColorOverride("font_color", new Color("82e7b1"));
        _best.AddThemeFontSizeOverride("font_size", 18);
        _visual.AddChild(_caption); _visual.AddChild(_best); _visual.AddChild(_map.View);
        var legend = Wrap("● 正在探索     ◉ 获胜候选     ○ 回合结束");
        legend.AddThemeColorOverride("font_color", CoachTheme.Muted); legend.AddThemeFontSizeOverride("font_size", 12);
        _visual.AddChild(legend); _visual.AddChild(_worker);
        _worker.ItemSelected += _ => Render();
        _map.Selected += worker => { _worker.Select(worker); Render(); };
        _visual.AddChild(_health); _visual.AddChild(_hp); _visual.AddChild(_enemies); _visual.AddChild(_enemyHp);
        _hp.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color("70cfa0"), CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 });
        _enemyHp.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color("ca7787"), CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 });
        _action.AddThemeFontSizeOverride("font_size", 18); _action.AddThemeColorOverride("font_color", new Color("69d9e1"));
        _changes.MaxLinesVisible = 2; _changes.AddThemeColorOverride("font_color", CoachTheme.Muted);
        _visual.AddChild(_action); _visual.AddChild(_changes);
        _visual.AddChild(CoachTheme.Disclosure("技术记录", _details)); _visual.AddChild(_details);
        _details.VisibilityChanged += () => { if (_details.Visible) RenderDetails(); };
        _details.AddChild(_overview); _details.AddChild(_budgetText); _details.AddChild(_budget);
        _details.AddChild(_state); _details.AddChild(Wrap("本路线最近 12 步 · 每步结算前后的变化")); _details.AddChild(_events);
    }
    private static Label Wrap(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private static RichTextLabel TextBox(float height) => new()
    {
        BbcodeEnabled = false, SelectionEnabled = true, ScrollActive = true,
        CustomMinimumSize = new Vector2(0, height), FitContent = false
    };
    public void Begin(LocalSearchRequest request)
    {
        Finish("", true);
        _book = new(request.Id, request.SnapshotId);
        _accepting = true;
        _caption.Text = "准备路线探索…";
        _best.Text = ""; _health.Text = _enemies.Text = _action.Text = _changes.Text = "";
        View.Show(); _visual.Show(); _map.SetActive(true);
    }
    public void Accept(LocalProgress progress)
    {
        if (!_accepting || _book?.Accept(progress) != true) return;
        while (_worker.ItemCount < progress.Workers) _worker.AddItem($"查看路线 {_worker.ItemCount + 1}");
        if (_worker.Selected < 0) _worker.Select(0);
        _map.Accept(progress); Render();
    }
    public void Finish(string text, bool clear)
    {
        _accepting = false; _map.SetActive(false); _details.Hide(); _visual.Hide();
        _caption.Text = text;
        if (!clear) return;
        View.Hide(); _map.Clear();
        _book = null; _worker.Clear(); _overview.Text = _state.Text = _events.Text = "";
        _budget.Value = _hp.Value = _enemyHp.Value = 0; _budgetText.Text = "";
    }
    private void Render()
    {
        if (_book == null) return;
        int victories = _book.Latest.Values.Sum(p => p.Victories);
        _caption.Text = victories > 0 ? $"已找到 {victories} 条获胜路线 · 继续寻找更好的出牌" : "寻找损伤更低的路线…";
        var best = LocalProgressBook.BestVictory(_book.Latest.Values)?.Best;
        _best.Text = best == null ? "" : $"获胜候选 · 战后生命 {best.Hp}/{best.MaxHp}" +
            (best.HpChange is { } change ? $" · 生命净变化 {change:+0;-0;0}" : "");
        _map.Select(Math.Max(0, _worker.Selected));
        if (!_book.Latest.TryGetValue(Math.Max(0, _worker.Selected), out var progress))
        {
            _health.Text = _enemies.Text = _action.Text = _changes.Text = ""; _hp.Value = _enemyHp.Value = 0;
            return;
        }
        if (progress.State is { } state)
        {
            _hp.MaxValue = Math.Max(1, state.MaxHp); _hp.Value = Math.Clamp(state.Hp, 0, Math.Max(1, state.MaxHp));
            _health.Text = $"此路线 · 第 {state.Round} 回合 · 生命 {state.Hp}/{state.MaxHp} · 格挡 {state.Block} · 能量 {state.Energy}";
            int enemyHp = state.Enemies.Sum(e => Math.Max(0, e.Hp));
            _enemyHp.MaxValue = Math.Max(1, state.Enemies.Sum(e => Math.Max(0, e.MaxHp))); _enemyHp.Value = enemyHp;
            _enemies.Text = $"敌方剩余生命 {enemyHp}";
        }
        else { _hp.Value = _enemyHp.Value = 0; _health.Text = "等待此路线开始…"; _enemies.Text = ""; }
        var last = progress.Events.LastOrDefault();
        _action.Text = last == null ? "" : $"第 {last.Round} 回合 · {LocalRouteMap.ShortAction(last.Action)}";
        _action.TooltipText = last?.Action ?? "";
        _changes.Text = last?.Changes ?? ""; _changes.TooltipText = _changes.Text;
        if (_details.Visible) RenderDetails();
    }
    private void RenderDetails()
    {
        if (_book == null) return;
        _overview.Text = string.Join("\n", _book.Latest.Values.Select(LocalProgressBook.Overview));
        if (!_book.Latest.TryGetValue(Math.Max(0, _worker.Selected), out var progress)) return;
        _budget.Value = LocalProgressBook.BudgetUsed(progress);
        _budgetText.Text = $"{progress.Phase} · {progress.ElapsedMs / 1000.0:F1} / {progress.BudgetSeconds} 秒";
        _budget.TooltipText = "已使用的搜索时间预算，不是穷举完成率；准备和复核另计。";
        _state.Text = progress.State is { } state ? LocalProgressBook.StateText(state) : progress.Phase;
        _events.Text = string.Join("\n\n", progress.Events.Select(e => $"第 {e.Round} 回合 · 步骤 {e.Step}：{e.Action}\n{e.Changes}"));
    }
}
