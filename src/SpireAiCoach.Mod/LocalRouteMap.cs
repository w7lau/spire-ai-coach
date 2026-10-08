using Godot;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Bounded, read-only telemetry fragments, not an invented search tree or an
// estimate of how many possible states have been exhausted.
internal sealed class LocalRouteMap
{
    public Control View { get; } = new() { Name = "LocalRouteMap", MouseFilter = Control.MouseFilterEnum.Stop };
    public event Action<int>? Selected;
    private readonly Dictionary<(int Worker, int Route), LocalSimEvent[]> _recent = new();
    private readonly Queue<(int Worker, int Route)> _order = new();
    private readonly SortedDictionary<int, LocalProgress> _latest = new();
    private readonly Godot.Timer _pulse = new() { WaitTime = .1 };
    private readonly Color _cyan = new("69d9e1"), _green = new("82e7b1"), _purple = new("aa96ed"), _red = new("df8290");
    private bool _active;
    private int _selected;
    private int _pass;
    private sealed record Row(int Worker, LocalSimEvent[] Events, int? Hp, int Round, bool Victory, bool Stopped,
        string Activity, string LaneState, int Evaluated);

    public LocalRouteMap()
    {
        View.CustomMinimumSize = new Vector2(0, 100);
        View.Draw += Draw;
        View.AddChild(_pulse);
        _pulse.Timeout += () => { if (_active && View.IsVisibleInTree()) View.QueueRedraw(); };
        View.VisibilityChanged += UpdatePulse;
        View.Resized += View.QueueRedraw;
        View.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
            var rows = Rows();
            int index = (int)((click.Position.Y - 22) / 40);
            if (index >= 0 && index < rows.Count && !rows[index].Victory) Selected?.Invoke(rows[index].Worker);
        };
        View.TooltipText = "节点是实际收到的出牌或回合结算。绿色为已完成的获胜候选；流光仅用于显示计算仍在进行。点击探索中的路线可查看其当前状态。";
    }
    public void Accept(LocalProgress progress)
    {
        if (progress.Pass > _pass)
        { _recent.Clear(); _order.Clear(); _latest.Clear(); _pass = progress.Pass; }
        _latest[progress.Worker] = progress;
        var key = (progress.Worker, progress.Route);
        if (!_recent.ContainsKey(key))
        {
            _order.Enqueue(key);
            while (_order.Count > 64) _recent.Remove(_order.Dequeue());
        }
        // Twelve events per update; at most sixty-four route fragments. No
        // per-action game calls, additional simulation, or new IPC are needed.
        if (progress.Events.Length > 0 || !_recent.ContainsKey(key)) _recent[key] = progress.Events;
        View.CustomMinimumSize = new Vector2(0, Math.Max(100, 30 + Rows().Count * 40));
        View.QueueRedraw();
    }
    public void Select(int worker) { _selected = worker; View.QueueRedraw(); }
    public void SetActive(bool active) { _active = active; UpdatePulse(); View.QueueRedraw(); }
    public void Clear()
    {
        SetActive(false); _recent.Clear(); _order.Clear(); _latest.Clear(); _selected = 0; _pass = 0;
        View.CustomMinimumSize = new Vector2(0, 100); View.QueueRedraw();
    }
    private void UpdatePulse()
    {
        if (_active && View.IsVisibleInTree()) _pulse.Start(); else _pulse.Stop();
    }
    private List<Row> Rows()
    {
        var rows = new List<Row>();
        var winner = LocalProgressBook.BestVictory(_latest.Values);
        if (winner?.Best is { } best)
        {
            _recent.TryGetValue((winner.Worker, best.Route), out var trace);
            rows.Add(new(winner.Worker, trace ?? [], best.Hp, best.Rounds, true, true, "获胜候选", "获胜候选", 0));
        }
        // The accepted telemetry is already bounded to sixteen logical lanes.
        // Keep completed lanes visible so their stopped pulse has an explanation.
        rows.AddRange(_latest.Values.Select(p => new Row(p.Worker, p.Events, p.State?.Hp,
            p.State?.Round ?? 0, false, LocalProgressBook.Finished(p), LocalProgressBook.Activity(p),
            LocalProgressBook.LaneState(p), p.Evaluated)));
        return rows;
    }
    private void Draw()
    {
        float width = View.Size.X;
        if (width < 100) return;
        var font = View.GetThemeDefaultFont();
        View.DrawRect(new Rect2(Vector2.Zero, View.Size), new Color("101f2e"));
        var rows = Rows();
        for (float x = 18; x < width; x += 28)
            for (float y = 12; y < View.Size.Y; y += 20)
                View.DrawCircle(new Vector2(x, y), .7f, new Color("2a4055"));
        if (rows.Count == 0)
        {
            View.DrawArc(new Vector2(30, 44), 11, 0, Mathf.Tau, 32, _cyan with { A = .3f }, 1.5f, true);
            View.DrawCircle(new Vector2(30, 44), 4, _cyan);
            View.DrawString(font, new Vector2(53, 49), "正在准备计算资源…", fontSize: 15, modulate: CoachTheme.Muted);
            return;
        }
        float end = width - 100;
        float split = Math.Min(65, end - 12);
        var origin = new Vector2(14, 22 + rows.Count * 20);
        float flow = (Time.GetTicksMsec() % 1800) / 1800f;
        View.DrawCircle(origin, 9, _cyan with { A = .13f }); View.DrawCircle(origin, 4, _cyan);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; float y = 34 + i * 40;
            var tint = row.Victory ? _green : row.Hp <= 0 ? _red : row.Worker == _selected ? _cyan : _purple;
            var events = row.Events.TakeLast(6).ToArray();
            View.DrawLine(origin, new Vector2(split - 16, y), tint with { A = .22f }, 1.4f, true);
            View.DrawLine(new Vector2(split - 16, y), new Vector2(end, y), tint with { A = .08f }, 8, true);
            View.DrawLine(new Vector2(split - 16, y), new Vector2(end, y), tint with { A = .55f }, 1.5f, true);
            if (events.Length == 0)
                View.DrawString(font, new Vector2(split, y - 9), row.Activity, fontSize: 12, modulate: tint);
            for (int n = 0; n < events.Length; n++)
            {
                float x = events.Length == 1 ? split : Mathf.Lerp(split, end - 17, n / (float)(events.Length - 1));
                var node = new Vector2(x, y); bool turn = events[n].Action.StartsWith("结束回合", StringComparison.Ordinal);
                View.DrawCircle(node, 8, tint with { A = .1f });
                if (turn) View.DrawArc(node, 4, 0, Mathf.Tau, 20, CoachTheme.Gold, 1.7f, true);
                else View.DrawCircle(node, n == events.Length - 1 ? 4.5f : 3, tint);
                string label = ShortAction(events[n].Action);
                float available = Math.Max(12, (end - split) / Math.Max(1, events.Length) - 2);
                while (label.Length > 1 && font.GetStringSize(label, fontSize: 11).X > available) label = label[..^1];
                float labelWidth = font.GetStringSize(label, fontSize: 11).X;
                float labelX = Math.Clamp(x - labelWidth / 2, split - 8, Math.Max(split - 8, end - labelWidth - 12));
                View.DrawString(font, new Vector2(labelX, y + 17), label, fontSize: 11, modulate: CoachTheme.Muted);
            }
            if (_active && !row.Stopped && View.IsVisibleInTree())
            {
                var dot = new Vector2(Mathf.Lerp(split - 16, end, (flow + i * .13f) % 1), y);
                View.DrawCircle(dot, 7, tint with { A = .12f }); View.DrawCircle(dot, 2, tint);
            }
            if (row.Victory) View.DrawArc(new Vector2(end + 1, y), 7, 0, Mathf.Tau, 24, _green, 2, true);
            else View.DrawCircle(new Vector2(end + 1, y), 3.5f, tint);
            View.DrawString(font, new Vector2(end + 15, y - 3), row.Victory ? "获胜候选" : $"{row.Worker + 1}路 · {row.LaneState}", fontSize: 11, modulate: tint);
            View.DrawString(font, new Vector2(end + 15, y + 14), !row.Victory && row.Stopped ? $"已评估 {row.Evaluated} 条" :
                row.Hp is { } hp ? $"{hp} HP · {row.Round} 回合" : row.LaneState, fontSize: 10, modulate: CoachTheme.Muted);
        }
    }
    internal static string ShortAction(string action)
    {
        int start = action.IndexOf('「'), end = action.IndexOf('」');
        return start >= 0 && end > start ? action[(start + 1)..end] :
            action.StartsWith("结束回合", StringComparison.Ordinal) ? "结束回合" : action;
    }
}
