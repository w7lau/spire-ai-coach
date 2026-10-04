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
    private readonly DiagnosticStore _diagnostics = new(ProjectSettings.GlobalizePath("user://spire_ai_coach/diagnostics"));
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
    private object? _panelCombat;
    private bool _panelExecuting;
    private bool? _manualPanelVisibility;
    private ScrollContainer _contentScroll = null!;
    private VBoxContainer _settingsPanel = null!;
    private VBoxContainer _aiOptions = null!;
    private Label _status = null!;
    private Label _battle = null!;
    private Label _resources = null!;
    private Label _feedback = null!;
    private Label _freshness = null!;
    private RichTextLabel _advice = null!;
    private TextEdit _context = null!;
    private Button _analyze = null!;
    private Button _localAnalyze = null!;
    private Button _turnAnalyze = null!;
    private LocalSearchOrder _lastLocalOrder = LocalSearchOrder.MonteCarlo;
    private Button _continueOptimize = null!;
    private LocalWorkerPool _localPool = null!;
    private bool _localAnalyzing;
    private CheckBox _localStopOnZeroLoss = null!;
    private SpinBox _localTargetVictoryRounds = null!;
    private CheckBox _localSkipVerification = null!;
    private bool _executing;
    private CancellationTokenSource? _execution;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _preparedCombat;
    private Button _execute = null!;
    private Button _stopExecution = null!;
    private LocalContinuation? _continuation;
    private bool _continuationPending;
    private SpinBox _localWorkers = null!;
    private SpinBox _localMaxAttempts = null!;
    private SpinBox _localMaxRounds = null!;
    private SpinBox _localSearchSeconds = null!;
    private CheckBox _localPotions = null!;
    private OptionButton _localPlayCard = null!;
    private OptionButton _localFinisherCard = null!;
    private CheckBox _localCardGoalThreshold = null!;
    private SpinBox _localCardGoalLoss = null!;
    private Label _localCardGoalNotice = null!;
    private string? _cardGoalDeckKey;
    private Dictionary<string, string> _cardGoalNames = new(StringComparer.Ordinal);
    private bool _refreshingCardGoals;
    private LocalProgressPanel _localProgress = null!;
    private TextEdit _localTiming = null!;
    private TextEdit _localResultDetails = null!;
    private readonly ConcurrentDictionary<int, LocalProgress> _pendingLocalProgress = new();
    private Button _cancel = null!;
    private LineEdit _url = null!;
    private LineEdit _model = null!;
    private LineEdit _apiKey = null!;
    private CheckBox _remember = null!;
    private CheckBox _reveal = null!;
    private CheckBox _usage = null!;
    private TextEdit _diagnosticView = null!;
    private string _diagnosticJson = "暂无请求记录。";
    private int _diagnosticGeneration;
    private sealed record StreamPreview(int Generation, string Text);
    private StreamPreview? _pendingStream;
    private bool _streaming;

    public CoachOverlay(SceneTree tree) => _tree = tree;

    public void Mount()
    {
        _localPool = new LocalWorkerPool(ProjectSettings.GlobalizePath("user://spire_ai_coach/local-workers"));
        string? loadError = null;
        try { (_settings, _key) = _store.Load(); }
        catch (Exception ex) { loadError = $"本地设置未能读取（{ex.GetType().Name}），请重新填写后保存。"; }
        _layer = new CanvasLayer { Name = "SpireAiCoach", Layer = 90 };
        _tree.Root.AddChild(_layer);
        var toggle = new Button { Name = "CoachToggle", Text = "尖塔教练 · F8", Position = new Vector2(24, 12) };
        toggle.Pressed += TogglePanel;
        _layer.AddChild(toggle);

        _panel = new PanelContainer { Name = "CoachPanel", Visible = false };
        _panel.Theme = CoachTheme.Create(); toggle.Theme = _panel.Theme;
        _layer.AddChild(_panel);
        var shell = new VBoxContainer(); shell.AddThemeConstantOverride("separation", 10); _panel.AddChild(shell);
        var scroll = _contentScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(body);
        var heading = new HBoxContainer(); shell.AddChild(heading);
        var title = new Label { Text = "尖塔教练", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "F8 面板 · F9 AI 指导 · F10 AI 设置\nv" + typeof(CoachOverlay).Assembly.GetName().Version?.ToString(3) };
        title.AddThemeFontSizeOverride("font_size", 22); title.AddThemeColorOverride("font_color", CoachTheme.Gold);
        heading.AddChild(title);
        _battle = Wrapped("进入战斗后可计算。"); shell.AddChild(_battle);
        _status = Wrapped("本地计算无需配置 API。"); _status.AddThemeColorOverride("font_color", CoachTheme.Gold); shell.AddChild(_status);
        var row = new HBoxContainer();
        _analyze = new Button { Text = "AI 分析 · F9", Disabled = true }; row.AddChild(_analyze);
        _analyze.Pressed += Analyze;
        _cancel = new Button { Text = "取消", Disabled = true };
        _cancel.Pressed += () => Cancel("已取消分析。");
        var config = new Button { Text = "AI 设置 · F10" }; row.AddChild(config);
        config.Pressed += () => { _aiOptions.Show(); _settingsPanel.Visible = !_settingsPanel.Visible; };
        var hide = new Button { Text = "收起" }; heading.AddChild(hide);
        hide.Pressed += () => SetPanelVisible(false);
        var localRow = new GridContainer { Columns = 2 }; shell.AddChild(localRow);
        _localAnalyze = new Button { Name = "LocalBattleSearch", Text = "本地计算", Disabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "整场战斗 · 原算法。两种算法共用搜索设置；每路达到尝试次数或时间上限即结束，准备和复核另计。" }; localRow.AddChild(_localAnalyze);
        _localAnalyze.Pressed += () => AnalyzeLocal(LocalSearchOrder.MonteCarlo);
        _turnAnalyze = new Button { Name = "LocalTurnSearch", Text = "新算法计算", Disabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "整场战斗 · 按回合组织搜索。预算与本地计算一致，共用原生模拟和执行保护。" }; localRow.AddChild(_turnAnalyze);
        CoachTheme.Accent(_turnAnalyze);
        _turnAnalyze.Pressed += () => AnalyzeLocal(LocalSearchOrder.TurnFrontier);
        var options = CoachTheme.Section(body, "计算选项");
        var advanced = new VBoxContainer { Name = "LocalAdvancedOptions", Visible = false };
        _resources = Wrapped("计算资源 · 尚未准备"); _resources.AddThemeColorOverride("font_color", CoachTheme.Muted); advanced.AddChild(_resources);
        var localOptions = new GridContainer { Columns = 2 }; advanced.AddChild(localOptions);
        localOptions.AddChild(new Label { Text = "并发上限", TooltipText = "0 自动，1–16 为手动上限。按待办任务逐步增加，健康实例保留供后续计算。" });
        _localWorkers = new SpinBox { Name = "LocalWorkers", MinValue = 0, MaxValue = 16, Step = 1, Value = Math.Clamp(_settings.LocalWorkers, 0, 16),
            TooltipText = "0 按 CPU 和内存自动估算；手动值为实际并发上限。按待办任务逐步增加，无伤返回或任务结束后停止扩并。" };
        localOptions.AddChild(_localWorkers);
        var saveLocal = new Button { Name = "LocalSaveSettings", Text = "保存计算选项" }; advanced.AddChild(saveLocal);
        saveLocal.Pressed += () =>
        {
            try { SaveLocalSettings(); _status.Text = "本地设置已保存，下次计算生效。"; }
            catch (Exception ex) { _status.Text = "本地设置保存失败：" + ex.GetType().Name; }
        };
        var limits = new GridContainer { Columns = 2 }; advanced.AddChild(limits);
        limits.AddChild(new Label { Text = "每路尝试上限" });
        _localMaxAttempts = new SpinBox { Name = "LocalMaxAttempts", MinValue = 1, MaxValue = LocalCalculation.MaximumAttempts, Step = 1,
            Value = Math.Clamp(_settings.LocalMaxAttempts, 1, LocalCalculation.MaximumAttempts) };
        limits.AddChild(_localMaxAttempts);
        limits.AddChild(new Label { Text = "最大规划回合" });
        _localMaxRounds = new SpinBox { Name = "LocalMaxRounds", MinValue = 1, MaxValue = LocalCalculation.MaximumRounds, Step = 1,
            Value = Math.Clamp(_settings.LocalMaxRounds, 1, LocalCalculation.MaximumRounds) };
        limits.AddChild(_localMaxRounds);
        limits.AddChild(new Label { Text = "每路搜索秒数" });
        _localSearchSeconds = new SpinBox { Name = "LocalSearchSeconds", MinValue = 1, MaxValue = LocalCalculation.MaximumSearchSeconds, Step = 1,
            Value = Math.Clamp(_settings.LocalSearchSeconds, 1, LocalCalculation.MaximumSearchSeconds) };
        limits.AddChild(_localSearchSeconds);
        _localPotions = new CheckBox { Name = "LocalIncludePotions", Text = "必要时考虑药水", ButtonPressed = _settings.LocalIncludePotions,
            TooltipText = "优先选择战后净损血较少的路线；同等损血优先保留药水，不会要求全部喝掉。" };
        _localPotions.Toggled += _ =>
        {
            if (_continuation != null || _localAnalyzing)
            {
                Cancel("药水选项已变化，请重新计算。");
                _adviceHash = null; _advice.Text = "药水策略已改变，原本地路线已清除。";
            }
            try { SaveLocalSettings(); }
            catch (Exception ex) { _status.Text = "选项本次已生效，保存失败：" + ex.GetType().Name; }
        };
        options.AddChild(_localPotions);
        _localStopOnZeroLoss = new CheckBox { Name = "LocalStopOnZeroLoss", Text = "达到最低损失即返回", ButtonPressed = _settings.LocalStopOnZeroLoss,
            TooltipText = "战后无伤，或路线达到已证明的最低净损失且无需多喝药水，就停止搜索。回复上限未知或操作尚未覆盖时，不按正数下界停止。关闭后继续优化；最终复核由下方开关决定。" };
        _localStopOnZeroLoss.Toggled += enabled =>
        {
            _settings = _settings with { LocalStopOnZeroLoss = enabled };
            if (_localAnalyzing) Cancel("停止条件已改变，请重新计算。");
            try { SaveLocalSettings(); }
            catch (Exception ex) { _status.Text = "选项本次已生效，保存失败：" + ex.GetType().Name; }
        };
        options.AddChild(_localStopOnZeroLoss);
        var targetOptions = new GridContainer { Columns = 2 }; advanced.AddChild(targetOptions);
        targetOptions.AddChild(new Label { Text = "目标回合", TooltipText = "提前返回的目标回合数；0 不限。不是战斗搜索的回合上限。" });
        _localTargetVictoryRounds = new SpinBox { Name = "LocalTargetVictoryRounds", MinValue = 0, MaxValue = _localMaxRounds.Value,
            Step = 1, Value = Math.Clamp(_settings.LocalTargetVictoryRounds, 0, (int)_localMaxRounds.Value),
            TooltipText = "填 6：只有六回合内获胜、战后生命不低于起点、不主动用药且确认敌方伤害为 0，才提前返回。允许自身扣血后回复。0 沿用原无伤条件；搜索上限由本地设置决定。" };
        targetOptions.AddChild(_localTargetVictoryRounds);
        _localMaxRounds.ValueChanged += value => _localTargetVictoryRounds.MaxValue = value;
        _localTargetVictoryRounds.ValueChanged += value =>
        {
            _settings = _settings with { LocalTargetVictoryRounds = (int)value };
            if (_localAnalyzing) Cancel("目标回合已改变，请重新计算。");
        };
        _localSkipVerification = new CheckBox { Name = "LocalSkipVerification", Text = "跳过最终复核",
            ButtonPressed = _settings.LocalSkipFinalVerification,
            TooltipText = "默认勾选，省去最终路线的再次模拟，取得完整记录后仍可执行；执行时每一步都会核对，偏离即停止。取消勾选可恢复独立复核。" };
        _localSkipVerification.Toggled += enabled =>
        {
            if (_localAnalyzing) Cancel("复核选项已改变，请重新计算。");
            _status.Text = enabled ? "下次计算跳过最终复核。" : "下次计算会复核最终路线。";
            try { SaveLocalSettings(); }
            catch (Exception ex) { _status.Text = "选项本次已生效，保存失败：" + ex.GetType().Name; }
        };
        options.AddChild(_localSkipVerification);
        var cardGoals = new VBoxContainer { Name = "LocalCardGoals", Visible = false };
        options.AddChild(CoachTheme.Disclosure("可选出牌目标", cardGoals)); options.AddChild(cardGoals);
        var cardFields = new GridContainer { Columns = 2 }; cardGoals.AddChild(cardFields);
        cardFields.AddChild(new Label { Text = "尽可能多打" });
        _localPlayCard = new OptionButton { Name = "LocalPlayCard", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "从当前牌组选一张；从当前状态起统计后续打出次数，合并所有副本和升级版，包含自动和重复打出。选“不启用”关闭目标。" };
        cardFields.AddChild(_localPlayCard);
        cardFields.AddChild(new Label { Text = "尽量用来补刀" });
        _localFinisherCard = new OptionButton { Name = "LocalFinisherCard", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "尽量用这张牌击杀敌人，按游戏实际记录的卡牌伤害来源计数。两项都启用时，先比较补刀次数，再比较使用次数。" };
        cardFields.AddChild(_localFinisherCard);
        _localCardGoalThreshold = new CheckBox { Name = "LocalCardGoalThreshold", Text = "允许少量损血，优先可选目标",
            ButtonPressed = _settings.LocalCardGoalThresholdEnabled };
        cardGoals.AddChild(_localCardGoalThreshold);
        cardFields = new GridContainer { Columns = 2 }; cardGoals.AddChild(cardFields);
        cardFields.AddChild(new Label { Text = "战后净损血小于" });
        _localCardGoalLoss = new SpinBox { Name = "LocalCardGoalLoss", MinValue = 1, MaxValue = int.MaxValue,
            Step = 1, Value = Math.Max(1, _settings.LocalCardGoalHpLossThreshold),
            TooltipText = "严格小于：填 5 表示净损血 0–4。包含战中和战后回血；没有符合的获胜路线时，仍按损血较少选路。" };
        cardFields.AddChild(_localCardGoalLoss);
        _localCardGoalNotice = Wrapped(""); cardGoals.AddChild(_localCardGoalNotice);
        _localPlayCard.ItemSelected += _ => CardGoalsChanged();
        _localFinisherCard.ItemSelected += _ => CardGoalsChanged();
        _localCardGoalThreshold.Toggled += _ => CardGoalsChanged();
        _localCardGoalLoss.ValueChanged += _ => CardGoalsChanged();
        UpdateCardGoalNotice();
        options.AddChild(CoachTheme.Disclosure("高级设置", advanced)); options.AddChild(advanced);
        _localProgress = new LocalProgressPanel(); body.AddChild(_localProgress.View);
        var tools = new VBoxContainer { Name = "CoachDiagnostics", Visible = false };
        _localResultDetails = new TextEdit { Name = "LocalResultDetails", Editable = false, Visible = false,
            Text = "计算完成后显示详细结果。", CustomMinimumSize = new Vector2(0, 260), WrapMode = TextEdit.LineWrappingMode.Boundary };
        tools.AddChild(CoachTheme.Disclosure("搜索统计与完整结果", _localResultDetails)); tools.AddChild(_localResultDetails);
        _localTiming = new TextEdit { Editable = false, Visible = false, Text = "计算完成后显示耗时分析。",
            CustomMinimumSize = new Vector2(0, 260), WrapMode = TextEdit.LineWrappingMode.Boundary };
        tools.AddChild(CoachTheme.Disclosure("耗时分析", _localTiming)); tools.AddChild(_localTiming);

        _aiOptions = new VBoxContainer { Name = "AiOptions", Visible = loadError != null };
        _settingsPanel = new VBoxContainer { Visible = loadError != null };
        _settingsPanel.VisibilityChanged += () =>
        {
            if (_settingsPanel.IsVisibleInTree()) Callable.From(() =>
            {
                if (!_disposed && _settingsPanel.IsVisibleInTree()) _contentScroll.EnsureControlVisible(_settingsPanel);
            }).CallDeferred();
        };
        _url = Field("API URL（基础地址或完整 /chat/completions 地址）", _settings.BaseUrl);
        _model = Field("模型名称", _settings.Model);
        _apiKey = Field("API Key（本地免密服务可以留空）", _key); _apiKey.Secret = true;
        _remember = new CheckBox { Text = "在此 Windows 账户加密保存密钥", ButtonPressed = _settings.RememberKey };
        _settingsPanel.AddChild(_remember);
        _reveal = new CheckBox { Text = "让 AI 查看抽牌堆的真实顶部顺序", ButtonPressed = _settings.RevealDrawOrder };
        _settingsPanel.AddChild(_reveal);
        _usage = new CheckBox { Text = "请求流式用量统计（接口不支持时可关闭）", ButtonPressed = _settings.IncludeStreamUsage };
        _settingsPanel.AddChild(_usage);
        _settingsPanel.AddChild(Wrapped("点击分析时，会将下方战斗信息发送到你填写的服务商；每次点击调用一次，可能产生费用。支持 Chat Completions 兼容接口。"));
        var save = new Button { Text = "保存设置" }; save.Pressed += SaveSettings; _settingsPanel.AddChild(save);
        _feedback = Wrapped(loadError ?? "密钥不会写入游戏日志，也不会提交到 GitHub。"); _settingsPanel.AddChild(_feedback);
        var resultSection = CoachTheme.Section(body, "推荐路线");
        _freshness = Wrapped(""); _freshness.AddThemeColorOverride("font_color", CoachTheme.Muted); resultSection.AddChild(_freshness);
        var executionRow = new GridContainer { Columns = 3 }; shell.AddChild(executionRow);
        _continueOptimize = new Button { Text = "继续优化", Disabled = true }; resultSection.AddChild(_continueOptimize);
        _continueOptimize.Pressed += () => AnalyzeLocal(_lastLocalOrder, true);
        _execute = new Button { Text = "执行方案", Disabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; executionRow.AddChild(_execute);
        CoachTheme.Accent(_execute);
        _execute.Pressed += ExecuteLocalPlan;
        _stopExecution = new Button { Text = "停止 · Esc", Disabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; executionRow.AddChild(_stopExecution);
        _stopExecution.Pressed += () => Cancel("已停止执行；已出手的动作会正常结算。");
        executionRow.AddChild(_cancel);
        _advice = new RichTextLabel
        {
            BbcodeEnabled = false, SelectionEnabled = true, FitContent = true,
            ScrollActive = false, CustomMinimumSize = new Vector2(0, 190),
            Text = "计算后显示出牌方案。"
        };
        _advice.AddThemeConstantOverride("line_separation", 5); resultSection.AddChild(_advice);
        _aiOptions.AddChild(row); _aiOptions.AddChild(_settingsPanel);
        body.AddChild(CoachTheme.Disclosure("AI 指导与设置", _aiOptions)); body.AddChild(_aiOptions);
        _context = new TextEdit
        {
            Editable = false, Visible = false, CustomMinimumSize = new Vector2(0, 240),
            WrapMode = TextEdit.LineWrappingMode.Boundary
        };
        var preview = CoachTheme.Disclosure("当前战斗信息（尚未发送）", _context);
        tools.AddChild(preview); tools.AddChild(_context);
        preview.Pressed += RefreshPreview;
        _diagnosticView = new TextEdit { Editable = false, Visible = false, Text = "暂无请求记录。",
            CustomMinimumSize = new Vector2(0, 300), WrapMode = TextEdit.LineWrappingMode.Boundary };
        tools.AddChild(CoachTheme.Disclosure("最近请求与 AI 原始回复", _diagnosticView)); tools.AddChild(_diagnosticView);
        var copy = new Button { Text = "复制诊断记录", TooltipText = "含战斗信息，已隐藏本次 API 密钥。" }; tools.AddChild(copy);
        copy.Pressed += () => DisplayServer.ClipboardSet(_diagnosticJson);
        body.AddChild(CoachTheme.Disclosure("详细记录", tools)); body.AddChild(tools);
        shell.AddChild(scroll);
        _tree.ProcessFrame += OnFrame;
        _layer.TreeExiting += Dispose;
        Resize();
        // Wrapped text settles its minimum height after the first layout frame.
        // Reapply the viewport height when that temporary minimum shrinks.
        _panel.MinimumSizeChanged += Resize;
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
        if (_disposed) return;
        var screen = _tree.Root.GetVisibleRect().Size;
        var width = Math.Min(560, Math.Max(300, screen.X - 32));
        _panel.Position = new Vector2(Math.Max(12, screen.X - width - 20), 50);
        _panel.Size = new Vector2(width, Math.Max(280, screen.Y - 80));
    }

    private void RefreshPanelVisibility()
    {
        var manager = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
        ApplyPanelContext(manager.IsInProgress && !manager.IsOverOrEnding ? manager.DebugOnlyGetState() : null, _executing);
    }

    private void ApplyPanelContext(object? combat, bool executing)
    {
        // A manual choice lasts for this battle/execution context. A new
        // context restores the default, without reopening on every refresh.
        if (!ReferenceEquals(_panelCombat, combat) || _panelExecuting != executing)
        {
            _panelCombat = combat; _panelExecuting = executing;
            _manualPanelVisibility = null;
        }
        _panel.Visible = _manualPanelVisibility ?? (combat != null && !executing);
    }

    private void SetPanelVisible(bool visible)
    {
        RefreshPanelVisibility();
        _manualPanelVisibility = visible;
        _panel.Visible = visible;
    }

    private void TogglePanel()
    {
        RefreshPanelVisibility();
        SetPanelVisible(!_panel.Visible);
    }

    private void OnFrame()
    {
        if (_disposed) return;
        RefreshPanelVisibility();
        if (_executing && Input.IsKeyPressed(Key.Escape)) Cancel("已停止执行；已出手的动作会正常结算。");
        while (_mainThread.TryDequeue(out var action)) action();
        foreach (var worker in _pendingLocalProgress.Keys)
            if (_pendingLocalProgress.TryRemove(worker, out var local)) _localProgress.Accept(local);
        var preview = Interlocked.Exchange(ref _pendingStream, null);
        if (preview != null && preview.Generation == _generation && _streaming)
        {
            RefreshSnapshot();
            if (preview.Generation == _generation && _streaming)
            {
                _advice.Text = preview.Text;
                _status.Text = "正在接收 AI 回复…";
            }
        }
        bool f8 = Input.IsKeyPressed(Key.F8), f9 = Input.IsKeyPressed(Key.F9), f10 = Input.IsKeyPressed(Key.F10);
        var focus = _tree.Root.GuiGetFocusOwner();
        if (focus is not LineEdit && focus is not TextEdit)
        {
            if (f8 && !_f8) TogglePanel();
            if (f10 && !_f10) { SetPanelVisible(true); _aiOptions.Show(); _settingsPanel.Visible = !_settingsPanel.Visible; }
            if (f9 && !_f9) { SetPanelVisible(true); Analyze(); }
        }
        _f8 = f8; _f9 = f9; _f10 = f10;
        long now = System.Environment.TickCount64;
        if (now < _nextPoll) return;
        _nextPoll = now + 750;
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        RefreshPanelVisibility();
        try
        {
            var snapshot = _capture.Capture(_settings.RevealDrawOrder);
            RefreshCardGoalDeck();
            var hash = snapshot?.Fingerprint();
            var changed = hash != _snapshotHash;
            if (changed)
            {
                _snapshot = snapshot; _snapshotHash = hash;
                if (_request != null) Cancel("战斗状态已变化，请重新分析。");
                _pendingLocalProgress.Clear(); _localProgress.Finish("战斗状态已变化，过程记录已过期。", true);
                if (_adviceHash != null && _adviceHash != hash)
                    _freshness.Text = "旧建议已过期：手牌、目标、资源或回合状态发生了变化。";
                RefreshPreview();
            }
            if (!_executing && _continuation != null && (changed || _continuationPending)) ContinueLocalPlan(snapshot);
            _battle.Text = snapshot == null ? "当前不在战斗中。" :
                $"第 {snapshot.Round} 回合 · 生命 {snapshot.Player.Hp}/{snapshot.Player.MaxHp} · 能量 {snapshot.Player.Energy}";
            _analyze.Disabled = _executing || _request != null || snapshot?.CanAdvise != true;
            _localAnalyze.Disabled = _turnAnalyze.Disabled = _analyze.Disabled;
            _localPlayCard.Disabled = _localFinisherCard.Disabled = _executing;
            _localCardGoalThreshold.Disabled = _executing;
            _localCardGoalLoss.Editable = !_executing && _localCardGoalThreshold.ButtonPressed;
            _execute.Disabled = _analyze.Disabled || _continuation == null || _continuation.Invalid ||
                !LocalCapture.Stable() || !LocalCapture.ExecutionSettled();
            _continueOptimize.Disabled = _execute.Disabled;
            _stopExecution.Disabled = !_executing;
            var resources = _localPool.Resources();
            _resources.Text = $"计算资源 · {resources.Ready} 路可复用" +
                (resources.Preparing > 0 ? $" · {resources.Preparing} 路准备中" : "");
            _resources.TooltipText = resources.LastChange + "\n手动并发提前准备所选数量，自动模式按需增加；可用实例会复用。";
            var preparationScope = snapshot?.CombatId ??
                (MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress ? "active-run" : null);
            if (preparationScope != null && _preparedCombat != preparationScope && _request == null && !_executing &&
                MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.Type == MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Singleplayer &&
                System.Environment.GetEnvironmentVariable("SPIRE_LOCAL_INTEGRATION") == null)
            {
                _preparedCombat = preparationScope;
                var installation = LocalCapture.Installation();
                var workers = (int)_localWorkers.Value;
                _ = Task.Run(async () =>
                {
                    try { await _localPool.Prepare(installation, workers, _lifetime.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { GD.Print($"[SpireAiCoach] preparation failed: {ex.GetType().Name}"); }
                });
            }
        }
        catch (Exception ex)
        {
            _snapshot = null; _snapshotHash = null;
            Cancel("当前战斗信息读取失败，请等待动画结束；若持续失败，可能需要适配此 Mod。");
            _freshness.Text = "当前状态不可用，旧建议不适用。";
            _battle.Text = $"读取失败：{ex.GetType().Name}";
            _analyze.Disabled = true;
            _localAnalyze.Disabled = _turnAnalyze.Disabled = true;
            _execute.Disabled = true;
            _continueOptimize.Disabled = true;
        }
    }

    private void RefreshPreview()
    {
        if (_context.Visible) _context.Text = _snapshot == null ? "无战斗快照" : PromptBuilder.InputPreview(_snapshot);
    }

    private void ContinueLocalPlan(CombatSnapshot? snapshot)
    {
        if (_continuation == null) return;
        if (snapshot == null)
        { _continuation = null; _adviceHash = null; _advice.Text = "当前战斗已结束，路线已清除。"; return; }
        if (!snapshot.CanAdvise || !LocalCapture.Stable() || !LocalCapture.ExecutionSettled())
        {
            _continuationPending = true;
            _freshness.Text = "正在结算，暂停建议；稳定后核对操作历史与预测状态。";
            _advice.Text = "等待结算完成后续用原路线…";
            return;
        }
        _continuationPending = false;
        try
        {
            var next = _continuation.Advance(snapshot.CombatId, LocalCapture.LoadedMods(),
                LocalCapture.Fingerprint(), LocalCapture.History(), requireProgress: _adviceHash != _snapshotHash);
            if (next == null) throw new InvalidOperationException("Continuation mismatch");
            ShowLocalAdvice(next);
            _adviceHash = _snapshotHash;
            _freshness.Text = "已跟随你的操作更新剩余方案。";
            _status.Text = $"已完成 {_continuation.CompletedActions} 步 · 续用剩余方案，无需重新搜索";
        }
        catch
        {
            _continuation = null; _adviceHash = null;
            _advice.Text = "实际操作或状态偏离原路线，剩余建议已清除，请重新计算。";
            _freshness.Text = "未复用不一致或无法验证的路线。";
        }
    }

    private CoachSettings ReadLocalSettings() => _settings with
    {
        LocalWorkers = (int)_localWorkers.Value,
        LocalIncludePotions = _localPotions.ButtonPressed,
        LocalStopOnZeroLoss = _localStopOnZeroLoss.ButtonPressed,
        LocalSkipFinalVerification = _localSkipVerification.ButtonPressed,
        LocalTargetVictoryRounds = (int)_localTargetVictoryRounds.Value,
        LocalMaxAttempts = (int)_localMaxAttempts.Value,
        LocalMaxRounds = (int)_localMaxRounds.Value,
        LocalSearchSeconds = (int)_localSearchSeconds.Value,
        LocalPlayCardModelId = SelectedCard(_localPlayCard), LocalFinisherCardModelId = SelectedCard(_localFinisherCard),
        LocalCardGoalThresholdEnabled = _localCardGoalThreshold.ButtonPressed,
        LocalCardGoalHpLossThreshold = (int)_localCardGoalLoss.Value
    };

    private void SaveLocalSettings()
    {
        var next = ReadLocalSettings();
        _store.SaveLocalOptions(next.LocalWorkers, next.LocalIncludePotions, next.LocalStopOnZeroLoss,
            next.LocalTargetVictoryRounds, next.LocalMaxAttempts, next.LocalMaxRounds, next.LocalSearchSeconds,
            next.LocalSkipFinalVerification, next.LocalPlayCardModelId, next.LocalFinisherCardModelId,
            next.LocalCardGoalThresholdEnabled, next.LocalCardGoalHpLossThreshold);
        _settings = next;
    }

    private LocalSearchRequest ConfigureLocalRequest(LocalSearchRequest captured, LocalSearchOrder order) =>
        LocalCalculation.Configure(captured, order, (int)_localWorkers.Value, _localPotions.ButtonPressed,
            _localStopOnZeroLoss.ButtonPressed, _localSkipVerification.ButtonPressed,
            (int)_localTargetVictoryRounds.Value, (int)_localMaxAttempts.Value, (int)_localMaxRounds.Value,
            (int)_localSearchSeconds.Value, CurrentCardGoals());

    private static string SelectedCard(OptionButton picker) => picker.Selected > 0 && !picker.IsItemDisabled(picker.Selected)
        ? picker.GetItemMetadata(picker.Selected).AsString() : "";

    private LocalCardGoals? CurrentCardGoals()
    {
        var play = SelectedCard(_localPlayCard); var finisher = SelectedCard(_localFinisherCard);
        if (play.Length == 0 && finisher.Length == 0) return null;
        return new(play.Length == 0 ? null : play, finisher.Length == 0 ? null : finisher,
            _localCardGoalThreshold.ButtonPressed ? (int)_localCardGoalLoss.Value : null,
            play.Length == 0 ? null : _cardGoalNames.GetValueOrDefault(play, play),
            finisher.Length == 0 ? null : _cardGoalNames.GetValueOrDefault(finisher, finisher));
    }

    private void RefreshCardGoalDeck()
    {
        var state = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState();
        var player = state == null ? null : MegaCrit.Sts2.Core.Context.LocalContext.GetMe(state);
        if (player == null) return; // Preserve the selection while outside combat.
        var cards = player.Deck.Cards.GroupBy(c => c.Id.ToString(), StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Name: string.Join(" / ", g.Select(c => c.Title).Distinct(StringComparer.Ordinal)), Count: g.Count()))
            .OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var key = string.Join("\n", cards.Select(c => c.Id + ":" + c.Name + ":" + c.Count));
        if (key == _cardGoalDeckKey) return;
        _cardGoalNames = cards.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        _refreshingCardGoals = true;
        try
        {
            void Fill(OptionButton picker, string selected)
            {
                picker.Clear(); picker.AddItem("不启用"); picker.SetItemMetadata(0, "");
                foreach (var card in cards)
                {
                    string label = card.Name + $" ×{card.Count}";
                    if (cards.Count(c => c.Name == card.Name) > 1) label += " · " + card.Id;
                    picker.AddItem(label); int index = picker.ItemCount - 1;
                    picker.SetItemMetadata(index, card.Id);
                    if (card.Id == selected) picker.Select(index);
                }
                if (selected.Length > 0 && !cards.Any(c => c.Id == selected))
                {
                    picker.AddItem("原选择已不在牌组，本次不启用");
                    int index = picker.ItemCount - 1; picker.SetItemDisabled(index, true); picker.Select(index);
                }
            }
            Fill(_localPlayCard, _settings.LocalPlayCardModelId);
            Fill(_localFinisherCard, _settings.LocalFinisherCardModelId);
            _cardGoalDeckKey = key;
        }
        finally { _refreshingCardGoals = false; }
        UpdateCardGoalNotice();
    }

    private void CardGoalsChanged()
    {
        if (_refreshingCardGoals) return;
        if (_localAnalyzing || _continuation != null)
        {
            Cancel("出牌目标已变化，请重新计算。");
            _adviceHash = null; _advice.Text = "出牌目标已变化，请重新计算。";
        }
        UpdateCardGoalNotice();
        try { SaveLocalSettings(); }
        catch (Exception ex) { _status.Text = "目标本次已生效，保存失败：" + ex.GetType().Name; }
    }

    private void UpdateCardGoalNotice()
    {
        _localCardGoalLoss.Editable = _localCardGoalThreshold.ButtonPressed && !_executing;
        _localCardGoalNotice.Text = CurrentCardGoals() is not { Enabled: true } ? "未启用可选目标，按原生命与药水策略选路。" :
            (_localCardGoalThreshold.ButtonPressed ? "在所填净损血范围内优先补刀及多打牌；没有符合路线时优先少损血。" :
                "优先保住战后生命，同血量时优先补刀及多打牌。") +
            "\n启用可选目标后不会在无伤或最低损失时立即返回，会继续搜索到原定上限。";
    }

    private void ShowLocalAdvice(LocalSearchResult result)
    {
        _advice.Text = LocalSearchPolicy.FormatAdvice(result);
        _localResultDetails.Text = LocalSearchPolicy.Format(result);
    }

    private void SaveSettings()
    {
        try
        {
            var next = ReadLocalSettings() with { BaseUrl = _url.Text.Trim(), Model = _model.Text.Trim(),
                RememberKey = _remember.ButtonPressed, RevealDrawOrder = _reveal.ButtonPressed,
                IncludeStreamUsage = _usage.ButtonPressed };
            _store.Save(next, _apiKey.Text.Trim());
            Cancel("设置已保存，可以开始分析。");
            _settings = next; _key = _apiKey.Text.Trim();
            _feedback.Text = "已保存。";
            RefreshSnapshot();
        }
        catch (CoachException ex) { _feedback.Text = ex.Message; }
        catch (Exception ex) { _feedback.Text = $"保存失败（{ex.GetType().Name}）；可取消“记住密钥”后重试。"; }
    }

    private void Analyze()
    {
        if (_request != null || _executing) return;
        RefreshSnapshot();
        if (_snapshot?.CanAdvise != true) { _status.Text = "请等待自己的出牌阶段，再分析当前回合。"; return; }
        try { _settings.Validate(); }
        catch (CoachException ex) { _status.Text = ex.Message; _aiOptions.Show(); _settingsPanel.Show(); return; }
        var frozen = _snapshot;
        var settings = _settings;
        var key = _key;
        var generation = ++_generation;
        var cancellation = new CancellationTokenSource();
        var trace = new CallDiagnostics();
        _request = cancellation;
        _streaming = true;
        _continuation = null;
        _pendingLocalProgress.Clear(); _localProgress.Finish("当前使用 AI 分析。", true);
        _adviceHash = null;
        _advice.Text = "等待 AI 开始回复…";
        _localResultDetails.Text = "当前使用 AI 指导。";
        _freshness.Text = "接收中的内容尚未完成，请等待整理后的出牌建议。";
        _analyze.Disabled = true; _localAnalyze.Disabled = _turnAnalyze.Disabled = true; _cancel.Disabled = false;
        _status.Text = "正在分析… 可以取消；继续出牌会使本次分析失效。";
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _client.AnalyzeAsync(settings, key, frozen, cancellation.Token, trace,
                    text => Interlocked.Exchange(ref _pendingStream, new StreamPreview(generation, SecretRedactor.Redact(text, key, true))));
                _mainThread.Enqueue(() =>
                {
                    if (generation != _generation) return;
                    RefreshSnapshot(); // Recheck on the main thread at delivery, not only the timer.
                    if (generation != _generation || _snapshotHash != frozen.Fingerprint()) return;
                    _streaming = false;
                    _advice.Text = AdviceFormatter.Format(result.Advice, frozen);
                    _adviceHash = result.Advice.SnapshotId;
                    _freshness.Text = "基于当前状态的 AI 建议；后续效果仍需在游戏中核对。";
                    _status.Text = $"分析完成 · {result.ElapsedMs / 1000.0:F1} 秒" +
                        (trace.ResponseFormat == "json" ? "（服务商一次性返回）" : "") + "\n" + trace.TokenUsage.Display();
                    GD.Print($"[SpireAiCoach] success snapshot={_adviceHash} elapsed_ms={result.ElapsedMs} prompt={PromptBuilder.Version}");
                });
            }
            catch (OperationCanceledException) { /* Cancel() owns the visible status. */ }
            catch (CoachException ex)
            {
                trace.Outcome = ex.Category; trace.ErrorDetail = ex.Message;
                _mainThread.Enqueue(() => { if (generation == _generation)
                    { _streaming = false; _status.Text = ex.Message; _freshness.Text = "本次未生成有效建议；部分回复可在诊断中查看。"; _advice.Text = "未取得完整有效建议。"; } });
                // Category only; never log the API URL, key, raw HTTP errors or provider response.
                GD.Print($"[SpireAiCoach] request failed category={ex.Category} call={trace.CallId} snapshot={frozen.Fingerprint()}");
            }
            catch (Exception ex)
            {
                trace.Outcome = "unexpected"; trace.ErrorDetail = ex.GetType().Name;
                _mainThread.Enqueue(() => { if (generation == _generation)
                    { _streaming = false; _status.Text = $"分析未完成（{ex.GetType().Name}）。"; _freshness.Text = "本次未生成有效建议。"; _advice.Text = "未取得完整有效建议。"; } });
            }
            finally
            {
                var diagnosticJson = trace.RedactedJson(key);
                string location;
                try { location = _diagnostics.Save(trace, diagnosticJson); }
                catch (Exception ex) { location = $"诊断保存失败（{ex.GetType().Name}），仍可复制下方记录。"; }
                _mainThread.Enqueue(() =>
                {
                    if (generation >= _diagnosticGeneration)
                    {
                        _diagnosticGeneration = generation;
                        _diagnosticJson = diagnosticJson;
                        _diagnosticView.Text = $"调用 {trace.CallId}\n结果：{trace.Outcome}\n文件：{location}\n\n" +
                            DiagnosticDisplay.Format(diagnosticJson);
                    }
                    if (generation == _generation)
                    {
                        _request = null; _cancel.Disabled = true;
                        _analyze.Disabled = _snapshot?.CanAdvise != true;
                        _localAnalyze.Disabled = _turnAnalyze.Disabled = _analyze.Disabled;
                    }
                });
                cancellation.Dispose();
            }
        });
    }

    private void AnalyzeLocal(LocalSearchOrder order, bool continueOptimization = false)
    {
        if (_request != null || _executing) return;
        var timeline = new LocalTimeline(capacity: 65536);
        using var capturing = timeline.Measure(-1, "main", "capture");
        RefreshSnapshot();
        if (_snapshot?.CanAdvise != true) { _status.Text = "请等待自己的出牌阶段。"; return; }
        LocalSearchRequest request;
        LocalInstallation installation;
        try
        {
            request = ConfigureLocalRequest(LocalCapture.Capture(_snapshotHash!, continueOptimization), order);
            // Reuse only the suffix matching this combat, mods, native state and complete history.
            // It is an exploration seed; the worker re-executes and verifies it, never copies its score.
            if (_continuation != null && request.History != null)
            {
                var seed = _continuation.Advance(_snapshot!.CombatId, request.LoadedMods, request.NativeHash, request.History);
                if (seed?.IncludePotions == request.IncludePotions) request = request with { InitialPlan = seed.Best?.Actions };
            }
            installation = LocalCapture.Installation();
        }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        capturing.Dispose();
        request = request with { TimelineOrigin = timeline.Origin, InitialTrace = timeline.Snapshot() };
        var generation = ++_generation;
        var cancellation = new CancellationTokenSource();
        _request = cancellation;
        _lastLocalOrder = order;
        _localAnalyzing = true;
        _continuation = null;
        _pendingLocalProgress.Clear();
        _localProgress.Begin(request);
        _adviceHash = null;
        _advice.Text = LocalCalculation.Name(order) + "进行中，不调用 AI…";
        _localResultDetails.Text = "正在计算，完成后显示详细结果。";
        _status.Text = "准备计算…";
        _freshness.Text = "继续出牌会取消本次计算；候选路线仅在后台执行。";
        _analyze.Disabled = true; _localAnalyze.Disabled = _turnAnalyze.Disabled = true; _cancel.Disabled = false;
        _continueOptimize.Disabled = true; _execute.Disabled = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _localPool.Analyze(request, installation,
                    message => _mainThread.Enqueue(() => { if (generation == _generation) _status.Text = message; }), cancellation.Token,
                    preview => { if (!cancellation.IsCancellationRequested && generation == Volatile.Read(ref _generation)) _pendingLocalProgress[preview.Worker] = preview; });
                double ready = timeline.ElapsedMs;
                _mainThread.Enqueue(() =>
                {
                    if (generation != _generation) return;
                    var complete = new LocalTimeline(timeline.Origin, 65536);
                    complete.Import(result.Trace);
                    complete.Add(new(-1, "main", "display_wait", "", ready, complete.ElapsedMs - ready));
                    using var displaying = complete.Measure(-1, "main", "display");
                    RefreshSnapshot();
                    if (generation != _generation || _snapshotHash != request.SnapshotId) return;
                    if (LocalCapture.Fingerprint() != request.NativeHash)
                    { Cancel("原生战斗状态已变化，请重新计算。"); return; }
                    displaying.Dispose();
                    result = result with { ElapsedMs = (long)complete.ElapsedMs, Trace = complete.Snapshot() };
                    if (request.TargetVictoryRounds is { } target)
                        result = result with { Message = (LocalSearchPolicy.MeetsGoal(result.Best, request) ?
                            $"已达到 {target} 回合内获胜、净损失 0、无药且敌方伤害 0 的目标。" :
                            $"未达到 {target} 回合目标，显示预算内已取得的可用路线。") + "\n" + result.Message };
                    ShowLocalAdvice(result);
                    if (request.TargetVictoryRounds is { } requestedTarget && !LocalSearchPolicy.MeetsGoal(result.Best, request))
                        _advice.Text = $"尚未达成 {requestedTarget} 回合目标。\n" + _advice.Text;
                    _localTiming.Text = LocalTimeline.Format(result);
                    var timingPath = ProjectSettings.GlobalizePath("user://spire_ai_coach/diagnostics/local-timing-latest.json");
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(timingPath)!);
                            LocalTimingArchive.Write(Path.GetDirectoryName(timingPath)!, request.Id, new {
                                request.Id, request.SnapshotId, request.NativeHash, completed_at = DateTimeOffset.UtcNow,
                                version = typeof(ModEntry).Assembly.GetName().Version!.ToString(3), request.SearchOrder, request.MaxNodes, request.MaxRounds, request.BudgetSeconds,
                                ConfiguredWorkers = request.Workers, result.WorkerLimit,
                                request.SkipFinalVerification, request.StopOnZeroLoss, request.TargetVictoryRounds,
                                request.TargetPotionUses, request.RequireKnownZeroEnemyDamage, result.VerificationSkipped, result.ElapsedMs, result.Workers,
                                request.CardGoals,
                                result.Evaluated, result.Victories, result.HealthBounds, result.RecoveredFailures,
                                turn_search = result.TurnSearch is { } turns ? new { turns.Probes, turns.BoundPruned,
                                    turns.Offered, turns.DuplicateOffers, turns.Pending, turns.UnknownRecoveryChecks,
                                    turns.CoveredPrefixes, turns.CompletedHistories, turns.RepeatedHistories } : null,
                                result.Status, result.Best, result.MinimumLoss,
                                round_losses = result.Best is { } candidate ? LocalRouteFeedback.RoundLosses(candidate) : null,
                                result.Timing, result.Trace });
                        }
                        catch (Exception ex) { GD.Print("[SpireAiCoach] Timing save failed: " + ex.GetType().Name); }
                    });
                    _adviceHash = request.SnapshotId;
                    string optimality = LocalSearchPolicy.HasMinimumProof(result) ? "已达到最低净损失。" : "候选路线尚未证明最优。";
                    _freshness.Text = result.VerificationSkipped ? LocalSearchPolicy.HasExecutionPoints(result) ?
                        "可执行方案，偏离时自动停止。" + optimality :
                        "暂不能自动执行，可手动参考。" :
                        "路线已复核，偏离时自动停止。" + optimality;
                    _status.Text = $"计算完成 · {result.ElapsedMs / 1000d:F1} 秒";
                    if (LocalSearchPolicy.HasExecutionPoints(result))
                    {
                        _continuation = new(_snapshot!.CombatId, request.LoadedMods, result);
                        _continuationPending = false;
                    }
                    _localProgress.Finish("查看路线探索回放", false);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _mainThread.Enqueue(() => { if (generation == _generation)
                    { _status.Text = ex.Message; _advice.Text = "本次未取得可靠的本地方案，可改用 AI 分析。";
                        _pendingLocalProgress.Clear(); _localProgress.Finish("计算失败，过程数据已清除。", true); } });
            }
            finally
            {
                _mainThread.Enqueue(() =>
                {
                    if (generation != _generation) return;
                    _localAnalyzing = false; _request = null; _cancel.Disabled = true;
                    _analyze.Disabled = _snapshot?.CanAdvise != true; _localAnalyze.Disabled = _turnAnalyze.Disabled = _analyze.Disabled;
                });
                cancellation.Dispose();
            }
        });
    }

    private void Cancel(string status)
    {
        _execution?.Cancel();
        _continuation = null; _continuationPending = false;
        _execute.Disabled = true;
        _continueOptimize.Disabled = true;
        ++_generation;
        if (_localAnalyzing)
        {
            _pendingLocalProgress.Clear(); _localProgress.Finish("计算已停止，过程数据已清除。", true);
            _localAnalyzing = false;
            _advice.Text = "本次本地计算已停止。";
            _freshness.Text = "未发布本地建议。";
        }
        if (_streaming)
        {
            _streaming = false;
            _freshness.Text = "接收已停止，未生成有效建议。";
            _advice.Text = "本次分析已停止。";
        }
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
        _panelCombat = null;
        _lifetime.Cancel();
        Cancel("关闭");
        _tree.ProcessFrame -= OnFrame;
        _tree.Root.SizeChanged -= Resize;
        _capture.Dispose();
        _client.Dispose();
        _localPool.Dispose();
    }

    private async void ExecuteLocalPlan()
    {
        if (_executing || _request != null) return;
        RefreshSnapshot();
        if (_continuation == null || _snapshot?.CanAdvise != true || !LocalCapture.Stable())
        { _status.Text = "请先计算当前战斗的方案。"; return; }
        var plan = _continuation;
        using var cancellation = new CancellationTokenSource();
        _execution = cancellation; _executing = true;
        RefreshPanelVisibility();
        _execute.Disabled = true; _stopExecution.Disabled = false;
        _continueOptimize.Disabled = true;
        _analyze.Disabled = true; _localAnalyze.Disabled = _turnAnalyze.Disabled = true;
        _freshness.Text = "正在按方案执行。点击停止或按 Esc 可随时停止后续动作。";
        var executor = new LocalPlanExecutor(_tree);
        try
        {
            _status.Text = await executor.Execute(plan,
                () => _capture.Capture(false)?.CombatId, _localPotions.ButtonPressed,
                text => _status.Text = text, cancellation.Token);
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "已停止执行；已出手的动作会正常结算。"; }
        catch (Exception ex) { if (!_disposed) _status.Text = ex.Message; }
        finally
        {
            if (executor.Report is { } report)
            {
                GD.Print($"[SpireAiCoach] execution request={report.RequestId} " +
                    $"settled={report.StartingActionIndex + report.SettledActions}/{report.PlannedActions} " +
                    $"dispatched={report.DispatchedActions} stage={report.Stage}: {report.Message}");
                var directory = ProjectSettings.GlobalizePath("user://spire_ai_coach/diagnostics");
                _ = Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        var json = System.Text.Json.JsonSerializer.Serialize(report);
                        LocalWire.WriteJson(Path.Combine(directory, $"local-execution-{report.Id}.json"), json);
                        LocalWire.WriteJson(Path.Combine(directory, "local-execution-latest.json"), json);
                    }
                    catch (Exception ex) { GD.Print("[SpireAiCoach] Execution diagnostic save failed: " + ex.GetType().Name); }
                });
            }
            _execution = null; _executing = false; _continuation = null; _adviceHash = null;
            if (!_disposed)
            {
                _stopExecution.Disabled = true; _execute.Disabled = true;
                _freshness.Text = executor.Report?.BattleEnded == true ? "战斗已结束。" :
                    executor.Report?.ExpectedVictory == false && executor.Report.Stage == "partial-route" ?
                    "已到达部分路线的末尾，尚未完成战斗。" : "执行已停止。原因见上方状态，请重新计算当前状态。";
                RefreshSnapshot();
            }
        }
    }
}
