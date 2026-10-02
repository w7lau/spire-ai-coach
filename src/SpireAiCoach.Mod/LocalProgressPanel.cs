using Godot;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// UI consumes immutable worker telemetry only; never a live game object or command.
public sealed class LocalProgressPanel
{
    public VBoxContainer View { get; } = new();
    private readonly VBoxContainer _details = new() { Visible = false };
    private readonly Label _caption = Wrap("尚未开始本地计算。");
    private readonly OptionButton _worker = new();
    private readonly Label _budgetText = Wrap("");
    private readonly ProgressBar _budget = new() { MaxValue = 100, ShowPercentage = false };
    private readonly ProgressBar _hp = new() { MaxValue = 1, ShowPercentage = false };
    private readonly RichTextLabel _overview = TextBox(90);
    private readonly RichTextLabel _state = TextBox(130);
    private readonly RichTextLabel _events = TextBox(220);
    private LocalProgressBook? _book;

    public LocalProgressPanel()
    {
        var toggle = new Button { Text = "展开 / 收起计算过程" };
        toggle.Pressed += () => _details.Visible = !_details.Visible;
        View.AddChild(toggle); View.AddChild(_details);
        _details.AddChild(_caption); _details.AddChild(_overview);
        _details.AddChild(Wrap("选择搜索分组查看正在尝试的路线"));
        _details.AddChild(_worker);
        _worker.ItemSelected += _ => Render();
        _details.AddChild(_budgetText); _details.AddChild(_budget);
        _details.AddChild(Wrap("模拟玩家生命")); _details.AddChild(_hp);
        _details.AddChild(_state);
        _details.AddChild(Wrap("本路线最近 12 步 · 显示每步结算前后的变化"));
        _details.AddChild(_events);
    }
    private static Label Wrap(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private static RichTextLabel TextBox(float height) => new()
    {
        BbcodeEnabled = false, SelectionEnabled = true, ScrollActive = true,
        CustomMinimumSize = new Vector2(0, height), FitContent = false
    };
    public void Begin(LocalSearchRequest request)
    {
        Finish("后台试走中；分支可能失败或被放弃，请等待最终方案。", true);
        _book = new(request.Id, request.SnapshotId);
        _details.Show();
    }
    public void Accept(LocalProgress progress)
    {
        if (_book?.Accept(progress) != true) return;
        while (_worker.ItemCount < progress.Workers) _worker.AddItem($"搜索分组 {_worker.ItemCount + 1}");
        if (_worker.Selected < 0) _worker.Select(0);
        Render();
    }
    public void Finish(string text, bool clear)
    {
        _caption.Text = text;
        if (!clear) { _details.Hide(); return; }
        _book = null; _worker.Clear(); _overview.Text = _state.Text = _events.Text = "";
        _budget.Value = _hp.Value = 0; _budgetText.Text = "";
    }
    private void Render()
    {
        if (_book == null) return;
        _overview.Text = string.Join("\n", _book.Latest.Values.Select(LocalProgressBook.Overview));
        if (!_book.Latest.TryGetValue(Math.Max(0, _worker.Selected), out var progress)) return;
        _budget.Value = LocalProgressBook.BudgetUsed(progress);
        _budgetText.Text = $"搜索用时 {progress.ElapsedMs / 1000.0:F1}/{progress.BudgetSeconds} 秒（含复核可能超时；此条不是穷举完成率）";
        if (progress.State is { } state)
        {
            _hp.MaxValue = Math.Max(1, state.MaxHp); _hp.Value = Math.Clamp(state.Hp, 0, Math.Max(1, state.MaxHp));
            _state.Text = LocalProgressBook.StateText(state);
        }
        else { _hp.Value = 0; _state.Text = progress.Phase + "；此阶段暂无战斗画面数据。"; }
        _events.Text = string.Join("\n\n", progress.Events.Select(e => $"第 {e.Round} 回合 · 步骤 {e.Step}：{e.Action}\n{e.Changes}"));
    }
}
