using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using SpireAiCoach.Core;

namespace SpireAiCoach.Mod;

// Plain Godot controls + signals avoid requiring a .pck or generated Godot C# scripts.
public sealed class CoachOverlay
{
    private readonly SceneTree _tree;
    private readonly StateCapture _capture = new();
    private readonly CoachClient _client = new();
    private readonly SettingsStore _store = new(ProjectSettings.GlobalizePath("user://spire_ai_coach"));
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private CoachSettings _settings = new();
    private string _key = "";
    private CombatSnapshot? _snapshot;
    private string? _snapshotHash;
    private string? _adviceHash;
    private CancellationTokenSource? _request;
    private int _generation;
    private long _nextPoll;
    private bool _f8, _f9, _f10;
    private bool _disposed;
    private CanvasLayer _layer = null!;
    private PanelContainer _panel = null!;
    private VBoxContainer _settingsPanel = null!;
    private Label _status = null!;
    private Label _battle = null!;
    private Label _feedback = null!;
    private Label _freshness = null!;
    private RichTextLabel _advice = null!;
    private TextEdit _context = null!;
    private Button _analyze = null!;
    private Button _cancel = null!;
    private LineEdit _url = null!;
    private LineEdit _model = null!;
    private LineEdit _apiKey = null!;
    private CheckBox _remember = null!;
    private CheckBox _reveal = null!;

    public CoachOverlay(SceneTree tree) => _tree = tree;

    public void Mount()
    {
        string? loadError = null;
        try { (_settings, _key) = _store.Load(); }
        catch (Exception ex) { loadError = $"本地设置未能读取（{ex.GetType().Name}），请重新填写后保存。"; }
        _layer = new CanvasLayer { Name = "SpireAiCoach", Layer = 90 };
        _tree.Root.AddChild(_layer);
        var toggle = new Button { Text = "AI 教练 · F8", Position = new Vector2(24, 12) };
        toggle.Pressed += () => _panel.Visible = !_panel.Visible;
        _layer.AddChild(toggle);

        _panel = new PanelContainer { Name = "CoachPanel", Visible = false };
        var font = new SystemFont { FontNames = ["Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC"] };
        _panel.Theme = new Theme { DefaultFont = font, DefaultFontSize = 17 };
        toggle.AddThemeFontOverride("font", font);
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color("17202bef"), BorderColor = new Color("b89965"),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 14, ContentMarginBottom = 14
        });
        _layer.AddChild(_panel);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _panel.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(body);
        body.AddChild(new Label { Text = "尖塔 AI 教练", ThemeTypeVariation = "HeaderLarge" });
        body.AddChild(Wrapped("自己出牌，AI 帮你规划。F9 分析当前回合 · F10 设置"));
        _battle = Wrapped("进入战斗后可分析当前回合。"); body.AddChild(_battle);
        _status = Wrapped("先填写 API 地址、模型和密钥。"); body.AddChild(_status);
        var row = new HBoxContainer(); body.AddChild(row);
        _analyze = new Button { Text = "分析本回合 · F9", Disabled = true }; row.AddChild(_analyze);
        _analyze.Pressed += Analyze;
        _cancel = new Button { Text = "取消", Disabled = true }; row.AddChild(_cancel);
        _cancel.Pressed += () => Cancel("已取消分析。");
        var config = new Button { Text = "设置" }; row.AddChild(config);
        config.Pressed += () => _settingsPanel.Visible = !_settingsPanel.Visible;
        var hide = new Button { Text = "收起" }; row.AddChild(hide);
        hide.Pressed += () => _panel.Hide();

        _settingsPanel = new VBoxContainer { Visible = string.IsNullOrEmpty(_settings.Model) || loadError != null };
        body.AddChild(_settingsPanel);
        _url = Field("API URL（基础地址或完整 /chat/completions 地址）", _settings.BaseUrl);
        _model = Field("模型名称", _settings.Model);
        _apiKey = Field("API Key（本地免密服务可以留空）", _key); _apiKey.Secret = true;
        _remember = new CheckBox { Text = "在此 Windows 账户加密保存密钥", ButtonPressed = _settings.RememberKey };
        _settingsPanel.AddChild(_remember);
        _reveal = new CheckBox { Text = "让 AI 查看抽牌堆的真实顶部顺序", ButtonPressed = _settings.RevealDrawOrder };
        _settingsPanel.AddChild(_reveal);
        _settingsPanel.AddChild(Wrapped("点击分析时，会将下方战斗信息发送到你填写的服务商；每次点击调用一次，可能产生费用。支持 Chat Completions 兼容接口。"));
        var save = new Button { Text = "保存设置" }; save.Pressed += SaveSettings; _settingsPanel.AddChild(save);
        _feedback = Wrapped(loadError ?? "密钥不会写入游戏日志，也不会提交到 GitHub。"); _settingsPanel.AddChild(_feedback);
        _freshness = Wrapped(""); body.AddChild(_freshness);
        _advice = new RichTextLabel
        {
            BbcodeEnabled = false, SelectionEnabled = true, FitContent = true,
            ScrollActive = false, CustomMinimumSize = new Vector2(0, 190),
            Text = "等待分析。建议出现后，按编号顺序操作；遇到抽牌、随机结果或额外选牌时可再次分析。"
        };
        body.AddChild(_advice);
        var preview = new Button { Text = "展开 / 收起发送给 AI 的战斗信息" }; body.AddChild(preview);
        _context = new TextEdit
        {
            Editable = false, Visible = false, CustomMinimumSize = new Vector2(0, 240),
            WrapMode = TextEdit.LineWrappingMode.Boundary
        };
        body.AddChild(_context);
        preview.Pressed += () => { _context.Visible = !_context.Visible; RefreshPreview(); };
        _tree.ProcessFrame += OnFrame;
        _layer.TreeExiting += Dispose;
        Resize();
        _tree.Root.SizeChanged += Resize;
        GD.Print("[SpireAiCoach] UI mounted.");
    }

    private static Label Wrapped(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private LineEdit Field(string title, string value)
    {
        _settingsPanel.AddChild(Wrapped(title));
        var input = new LineEdit { Text = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _settingsPanel.AddChild(input);
        return input;
    }
    private void Resize()
    {
        var screen = _tree.Root.GetVisibleRect().Size;
        var width = Math.Min(610, Math.Max(320, screen.X - 48));
        _panel.Position = new Vector2(Math.Max(12, screen.X - width - 20), 50);
        _panel.Size = new Vector2(width, Math.Max(280, screen.Y - 80));
    }

    private void OnFrame()
    {
        if (_disposed) return;
        while (_mainThread.TryDequeue(out var action)) action();
        bool f8 = Input.IsKeyPressed(Key.F8), f9 = Input.IsKeyPressed(Key.F9), f10 = Input.IsKeyPressed(Key.F10);
        var focus = _tree.Root.GuiGetFocusOwner();
        if (focus is not LineEdit && focus is not TextEdit)
        {
            if (f8 && !_f8) _panel.Visible = !_panel.Visible;
            if (f10 && !_f10) { _panel.Show(); _settingsPanel.Visible = !_settingsPanel.Visible; }
            if (f9 && !_f9) { _panel.Show(); Analyze(); }
        }
        _f8 = f8; _f9 = f9; _f10 = f10;
        long now = System.Environment.TickCount64;
        if (now < _nextPoll) return;
        _nextPoll = now + 750;
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        try
        {
            var snapshot = _capture.Capture(_settings.RevealDrawOrder);
            var hash = snapshot?.Fingerprint();
            if (hash != _snapshotHash)
            {
                _snapshot = snapshot; _snapshotHash = hash;
                if (_request != null) Cancel("战斗状态已变化，请重新分析。");
                if (_adviceHash != null && _adviceHash != hash)
                    _freshness.Text = "旧建议已过期：手牌、目标、资源或回合状态发生了变化。";
                RefreshPreview();
            }
            _battle.Text = snapshot == null ? "当前不在战斗中。" :
                $"第 {snapshot.Round} 轮 · {snapshot.Phase}\n生命 {snapshot.Player.Hp}/{snapshot.Player.MaxHp} · 格挡 {snapshot.Player.Block} · 能量 {snapshot.Player.Energy}\n手牌 {snapshot.Hand.Count} / 抽牌 {snapshot.DrawPile.Count} / 弃牌 {snapshot.DiscardPile.Count} / 消耗 {snapshot.ExhaustPile.Count}";
            _analyze.Disabled = _request != null || snapshot?.CanAdvise != true;
        }
        catch (Exception ex)
        {
            _snapshot = null; _snapshotHash = null;
            Cancel("当前战斗信息读取失败，请等待动画结束；若持续失败，可能需要适配此 Mod。");
            _freshness.Text = "当前状态不可用，旧建议不适用。";
            _battle.Text = $"读取失败：{ex.GetType().Name}";
            _analyze.Disabled = true;
        }
    }

    private void RefreshPreview()
    {
        if (_context.Visible) _context.Text = _snapshot == null ? "无战斗快照" : PromptBuilder.UserPrompt(_snapshot);
    }

    private void SaveSettings()
    {
        try
        {
            var next = _settings with { BaseUrl = _url.Text.Trim(), Model = _model.Text.Trim(),
                RememberKey = _remember.ButtonPressed, RevealDrawOrder = _reveal.ButtonPressed };
            _store.Save(next, _apiKey.Text.Trim());
            Cancel("设置已保存，可以分析本回合。");
            _settings = next; _key = _apiKey.Text.Trim();
            _feedback.Text = "已保存。";
            RefreshSnapshot();
        }
        catch (CoachException ex) { _feedback.Text = ex.Message; }
        catch (Exception ex) { _feedback.Text = $"保存失败（{ex.GetType().Name}）；可取消“记住密钥”后重试。"; }
    }

    private void Analyze()
    {
        if (_request != null) return;
        RefreshSnapshot();
        if (_snapshot?.CanAdvise != true) { _status.Text = "请等待自己的出牌阶段，再分析当前回合。"; return; }
        try { _settings.Validate(); }
        catch (CoachException ex) { _status.Text = ex.Message; _settingsPanel.Show(); return; }
        var frozen = _snapshot;
        var settings = _settings;
        var key = _key;
        var generation = ++_generation;
        var cancellation = new CancellationTokenSource();
        _request = cancellation;
        _analyze.Disabled = true; _cancel.Disabled = false;
        _status.Text = "正在分析… 可以取消；继续出牌会使本次分析失效。";
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _client.AnalyzeAsync(settings, key, frozen, cancellation.Token);
                _mainThread.Enqueue(() =>
                {
                    if (generation != _generation) return;
                    RefreshSnapshot(); // Recheck on the main thread at delivery, not only the timer.
                    if (generation != _generation || _snapshotHash != frozen.Fingerprint()) return;
                    _advice.Text = AdviceFormatter.Format(result.Advice, frozen);
                    _adviceHash = result.Advice.SnapshotId;
                    _freshness.Text = "基于当前状态的 AI 建议；后续效果仍需在游戏中核对。";
                    _status.Text = $"分析完成 · {result.ElapsedMs / 1000.0:F1} 秒";
                    GD.Print($"[SpireAiCoach] success snapshot={_adviceHash} elapsed_ms={result.ElapsedMs} prompt={PromptBuilder.Version}");
                });
            }
            catch (OperationCanceledException) { /* Cancel() owns the visible status. */ }
            catch (CoachException ex)
            {
                _mainThread.Enqueue(() => { if (generation == _generation) _status.Text = ex.Message; });
                // Category only; never log the API URL, key, raw HTTP errors or provider response.
                GD.Print($"[SpireAiCoach] request failed category={ex.Category}");
            }
            catch (Exception ex)
            {
                _mainThread.Enqueue(() => { if (generation == _generation) _status.Text = $"分析未完成（{ex.GetType().Name}）。"; });
            }
            finally
            {
                _mainThread.Enqueue(() =>
                {
                    if (generation == _generation)
                    {
                        _request = null; _cancel.Disabled = true;
                        _analyze.Disabled = _snapshot?.CanAdvise != true;
                    }
                });
                cancellation.Dispose();
            }
        });
    }

    private void Cancel(string status)
    {
        ++_generation;
        if (_request != null)
        {
            try { _request.Cancel(); } catch (ObjectDisposedException) { }
            _request = null;
        }
        _cancel.Disabled = true;
        _status.Text = status;
    }

    private void Dispose()
    {
        _disposed = true;
        Cancel("关闭");
        _tree.ProcessFrame -= OnFrame;
        _tree.Root.SizeChanged -= Resize;
        _capture.Dispose();
        _client.Dispose();
    }
}
