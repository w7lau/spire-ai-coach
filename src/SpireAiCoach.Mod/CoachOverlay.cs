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
    private VBoxContainer _settingsPanel = null!;
    private Label _status = null!;
    private Label _battle = null!;
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
    private Button _localSkipVerification = null!;
    private bool _executing;
    private CancellationTokenSource? _execution;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _preparedCombat;
    private Button _execute = null!;
    private Button _stopExecution = null!;
    private LocalContinuation? _continuation;
    private bool _continuationPending;
    private SpinBox _localWorkers = null!;
    private CheckBox _localPotions = null!;
    private LocalProgressPanel _localProgress = null!;
    private TextEdit _localTiming = null!;
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
        var toggle = new Button { Text = "尖塔教练 · F8", Position = new Vector2(24, 12) };
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
        body.AddChild(new Label { Text = "尖塔教练 · AI / 本地", ThemeTypeVariation = "HeaderLarge" });
        body.AddChild(Wrapped("F9 调用 AI · 本地计算无需 API · 算好后可点击执行方案 · F10 设置"));
        _battle = Wrapped("进入战斗后可分析当前回合。"); body.AddChild(_battle);
        _status = Wrapped("本地计算无需 API；使用 AI 时请先填写地址、模型和密钥。"); body.AddChild(_status);
        var row = new HBoxContainer(); body.AddChild(row);
        _analyze = new Button { Text = "AI 分析 · F9", Disabled = true }; row.AddChild(_analyze);
        _analyze.Pressed += Analyze;
        _cancel = new Button { Text = "取消", Disabled = true }; row.AddChild(_cancel);
        _cancel.Pressed += () => Cancel("已取消分析。");
        var config = new Button { Text = "设置" }; row.AddChild(config);
        config.Pressed += () => _settingsPanel.Visible = !_settingsPanel.Visible;
        var hide = new Button { Text = "收起" }; row.AddChild(hide);
        hide.Pressed += () => _panel.Hide();
        var localRow = new HBoxContainer(); body.AddChild(localRow);
        _localAnalyze = new Button { Name = "LocalBattleSearch", Text = "本地整场计算（实验）", Disabled = true }; localRow.AddChild(_localAnalyze);
        _localAnalyze.Pressed += () => AnalyzeLocal(LocalSearchOrder.MonteCarlo);
        _turnAnalyze = new Button { Name = "LocalTurnSearch", Text = "新算法整场计算（实验）", Disabled = true }; localRow.AddChild(_turnAnalyze);
        _turnAnalyze.Pressed += () => AnalyzeLocal(LocalSearchOrder.TurnFrontier);
        body.AddChild(Wrapped("两种算法共用后台模拟。每路最多 64 次整场尝试、60 秒搜索，先达到一项即结束；准备和最终复核另计。"));
        body.AddChild(Wrapped("规划整场战斗，最多 64 轮，优先减少战后净生命损失、保留药水。关闭“无伤通关后立即返回”可继续优化无伤路线。"));
        var localOptions = new HBoxContainer(); body.AddChild(localOptions);
        localOptions.AddChild(new Label { Text = "并发上限（0 自动，1–16）" });
        _localWorkers = new SpinBox { MinValue = 0, MaxValue = 16, Step = 1, Value = Math.Clamp(_settings.LocalWorkers, 0, 16) };
        localOptions.AddChild(_localWorkers);
        var saveLocal = new Button { Text = "保存本地设置" }; localOptions.AddChild(saveLocal);
        saveLocal.Pressed += () =>
        {
            try { _store.SaveLocalOptions((int)_localWorkers.Value, _localPotions.ButtonPressed, _localStopOnZeroLoss.ButtonPressed, (int)_localTargetVictoryRounds.Value); _settings = _settings with { LocalWorkers = (int)_localWorkers.Value, LocalIncludePotions = _localPotions.ButtonPressed, LocalStopOnZeroLoss = _localStopOnZeroLoss.ButtonPressed, LocalTargetVictoryRounds = (int)_localTargetVictoryRounds.Value }; _status.Text = "本地设置已保存，下次计算生效。"; }
            catch (Exception ex) { _status.Text = "本地并发保存失败：" + ex.GetType().Name; }
        };
        _localPotions = new CheckBox { Text = "必要时考虑药水（优先保留）", ButtonPressed = _settings.LocalIncludePotions };
        _localPotions.Toggled += _ =>
        {
            if (_continuation == null && !_localAnalyzing) return;
            Cancel("药水选项已变化，请重新计算。");
            _adviceHash = null; _advice.Text = "药水策略已改变，原本地路线已清除。";
        };
        body.AddChild(_localPotions);
        _localStopOnZeroLoss = new CheckBox { Text = "无伤通关后立即返回（含回血）", ButtonPressed = _settings.LocalStopOnZeroLoss,
            TooltipText = "默认开启。找到获胜且战后生命不低于计算起点的路线，就停止全部搜索；默认复核后返回，开启跳过复核则直接显示结果。关闭后可继续搜索更佳路线。" };
        _localStopOnZeroLoss.Toggled += enabled =>
        {
            _settings = _settings with { LocalStopOnZeroLoss = enabled };
            if (_localAnalyzing) Cancel("停止条件已改变，请重新计算。");
            try { _store.SaveLocalOptions((int)_localWorkers.Value, _localPotions.ButtonPressed, enabled, (int)_localTargetVictoryRounds.Value); }
            catch (Exception ex) { _status.Text = "选项本次已生效，保存失败：" + ex.GetType().Name; }
        };
        body.AddChild(_localStopOnZeroLoss);
        var targetOptions = new HBoxContainer(); body.AddChild(targetOptions);
        targetOptions.AddChild(new Label { Text = "提前返回目标回合（0 不限）" });
        _localTargetVictoryRounds = new SpinBox { Name = "LocalTargetVictoryRounds", MinValue = 0, MaxValue = LocalCalculation.Rounds,
            Step = 1, Value = Math.Clamp(_settings.LocalTargetVictoryRounds, 0, LocalCalculation.Rounds),
            TooltipText = "填 6：只有六回合内获胜、战后生命不低于起点、不主动用药且确认敌方伤害为 0，才提前返回。允许自身扣血后回复。0 沿用原无伤条件；搜索仍可走到 64 回合，预算不变。" };
        targetOptions.AddChild(_localTargetVictoryRounds);
        _localTargetVictoryRounds.ValueChanged += value =>
        {
            _settings = _settings with { LocalTargetVictoryRounds = (int)value };
            if (_localAnalyzing) Cancel("目标回合已改变，请重新计算。");
        };
        _localSkipVerification = new Button { Name = "LocalSkipVerification", Text = "跳过最终复核：关闭", ToggleMode = true,
            TooltipText = "默认关闭。开启后省去最终路线的独立重放，仍可点击执行方案；执行时逐步核对首次模拟记录，偏离即停止。" };
        _localSkipVerification.Toggled += enabled =>
        {
            _localSkipVerification.Text = enabled ? "跳过最终复核：开启" : "跳过最终复核：关闭";
            if (_localAnalyzing) Cancel("复核选项已改变，请重新计算。");
            _status.Text = enabled ? "下次本地计算跳过最终复核，仍可执行取得完整逐步记录的方案。" : "下次本地计算会复核最终路线。";
        };
        body.AddChild(_localSkipVerification);
        _localProgress = new LocalProgressPanel(); body.AddChild(_localProgress.View);
        var timing = new Button { Text = "展开 / 收起耗时分析" }; body.AddChild(timing);
        _localTiming = new TextEdit { Editable = false, Visible = false, Text = "计算完成后显示耗时分析。",
            CustomMinimumSize = new Vector2(0, 260), WrapMode = TextEdit.LineWrappingMode.Boundary };
        body.AddChild(_localTiming);
        timing.Pressed += () => _localTiming.Visible = !_localTiming.Visible;

        _settingsPanel = new VBoxContainer { Visible = string.IsNullOrEmpty(_settings.Model) || loadError != null };
        body.AddChild(_settingsPanel);
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
        _freshness = Wrapped(""); body.AddChild(_freshness);
        var executionRow = new HBoxContainer(); body.AddChild(executionRow);
        _continueOptimize = new Button { Text = "继续优化", Disabled = true }; executionRow.AddChild(_continueOptimize);
        _continueOptimize.Pressed += () => AnalyzeLocal(_lastLocalOrder, true);
        _execute = new Button { Text = "执行方案", Disabled = true }; executionRow.AddChild(_execute);
        _execute.Pressed += ExecuteLocalPlan;
        _stopExecution = new Button { Text = "停止执行 · Esc", Disabled = true }; executionRow.AddChild(_stopExecution);
        _stopExecution.Pressed += () => Cancel("已停止执行；已出手的动作会正常结算。");
        _advice = new RichTextLabel
        {
            BbcodeEnabled = false, SelectionEnabled = true, FitContent = true,
            ScrollActive = false, CustomMinimumSize = new Vector2(0, 190),
            Text = "等待分析。建议出现后，按编号顺序操作；遇到抽牌、随机结果或额外选牌时可再次分析。"
        };
        body.AddChild(_advice);
        var preview = new Button { Text = "展开 / 收起实时战斗预览（尚未发送）" }; body.AddChild(preview);
        _context = new TextEdit
        {
            Editable = false, Visible = false, CustomMinimumSize = new Vector2(0, 240),
            WrapMode = TextEdit.LineWrappingMode.Boundary
        };
        body.AddChild(_context);
        preview.Pressed += () => { _context.Visible = !_context.Visible; RefreshPreview(); };
        var diagnosticButton = new Button { Text = "展开 / 收起最近请求与 AI 原始回复" }; body.AddChild(diagnosticButton);
        _diagnosticView = new TextEdit { Editable = false, Visible = false, Text = "暂无请求记录。",
            CustomMinimumSize = new Vector2(0, 300), WrapMode = TextEdit.LineWrappingMode.Boundary };
        body.AddChild(_diagnosticView);
        diagnosticButton.Pressed += () => _diagnosticView.Visible = !_diagnosticView.Visible;
        var copy = new Button { Text = "复制最近诊断记录（含战斗信息，已隐藏本次密钥）" }; body.AddChild(copy);
        copy.Pressed += () => DisplayServer.ClipboardSet(_diagnosticJson);
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
                $"第 {snapshot.Round} 轮 · {snapshot.Phase}\n生命 {snapshot.Player.Hp}/{snapshot.Player.MaxHp} · 格挡 {snapshot.Player.Block} · 能量 {snapshot.Player.Energy}\n手牌 {snapshot.Hand.Count} / 抽牌 {snapshot.DrawPile.Count} / 弃牌 {snapshot.DiscardPile.Count} / 消耗 {snapshot.ExhaustPile.Count}";
            _analyze.Disabled = _executing || _request != null || snapshot?.CanAdvise != true;
            _localAnalyze.Disabled = _turnAnalyze.Disabled = _analyze.Disabled;
            _execute.Disabled = _analyze.Disabled || _continuation == null || _continuation.Invalid || !LocalCapture.Stable();
            _continueOptimize.Disabled = _execute.Disabled;
            _stopExecution.Disabled = !_executing;
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
        if (!snapshot.CanAdvise || !LocalCapture.Stable())
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
            _advice.Text = LocalSearchPolicy.Format(next);
            _adviceHash = _snapshotHash;
            _freshness.Text = "实际操作历史和下一步原生状态一致，已续用原路线；仍受原有 Mod 覆盖限制。";
            _status.Text = $"已完成 {_continuation.CompletedActions} 步 · 续用剩余方案，无需重新搜索";
        }
        catch
        {
            _continuation = null; _adviceHash = null;
            _advice.Text = "实际操作或状态偏离原路线，剩余建议已清除，请重新计算。";
            _freshness.Text = "未复用不一致或无法验证的路线。";
        }
    }

    private void SaveSettings()
    {
        try
        {
            var next = _settings with { BaseUrl = _url.Text.Trim(), Model = _model.Text.Trim(),
                RememberKey = _remember.ButtonPressed, RevealDrawOrder = _reveal.ButtonPressed,
                IncludeStreamUsage = _usage.ButtonPressed, LocalWorkers = (int)_localWorkers.Value,
                LocalIncludePotions = _localPotions.ButtonPressed, LocalStopOnZeroLoss = _localStopOnZeroLoss.ButtonPressed };
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
        catch (CoachException ex) { _status.Text = ex.Message; _settingsPanel.Show(); return; }
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
            request = LocalCalculation.Configure(LocalCapture.Capture(_snapshotHash!, continueOptimization), order,
                (int)_localWorkers.Value, _localPotions.ButtonPressed, _localStopOnZeroLoss.ButtonPressed,
                _localSkipVerification.ButtonPressed, (int)_localTargetVictoryRounds.Value);
            // Reuse only the suffix matching this combat, mods, native state and complete history.
            // It is an exploration seed; the worker re-executes and verifies it, never copies its score.
            if (order == LocalSearchOrder.MonteCarlo && _continuation != null && request.History != null)
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
                    _advice.Text = LocalSearchPolicy.Format(result);
                    _localTiming.Text = LocalTimeline.Format(result);
                    var timingPath = ProjectSettings.GlobalizePath("user://spire_ai_coach/diagnostics/local-timing-latest.json");
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(timingPath)!);
                            LocalWire.Write(timingPath, new { version = typeof(ModEntry).Assembly.GetName().Version!.ToString(3), request.SearchOrder, request.MaxNodes, request.BudgetSeconds,
                                request.SkipFinalVerification, request.StopOnZeroLoss, request.TargetVictoryRounds,
                                request.TargetPotionUses, request.RequireKnownZeroEnemyDamage, result.VerificationSkipped, result.ElapsedMs, result.Workers,
                                result.Evaluated, result.Victories, result.Timing, result.Trace });
                        }
                        catch (Exception ex) { GD.Print("[SpireAiCoach] Timing save failed: " + ex.GetType().Name); }
                    });
                    _adviceHash = request.SnapshotId;
                    _freshness.Text = result.VerificationSkipped ? LocalSearchPolicy.HasExecutionPoints(result) ?
                        "已跳过最终复核；可执行方案，实际状态偏离时自动停止。当前为预算内候选。" :
                        "已跳过最终复核，但逐步记录不完整；目前仅供手动查看。" :
                        "路线已复核；按建议操作可续用。当前为预算内最佳候选，尚未证明全局最优。";
                    _status.Text = LocalCalculation.Name(order) + "完成 · 不消耗 API";
                    if (LocalSearchPolicy.HasExecutionPoints(result))
                    {
                        _continuation = new(_snapshot!.CombatId, request.LoadedMods, result);
                        _continuationPending = false;
                    }
                    _localProgress.Finish("计算完成。以下保留后台过程记录，正式建议见下方。", false);
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
        _execute.Disabled = true; _stopExecution.Disabled = false;
        _continueOptimize.Disabled = true;
        _analyze.Disabled = true; _localAnalyze.Disabled = _turnAnalyze.Disabled = true;
        _freshness.Text = "正在按方案执行。点击停止或按 Esc 可随时停止后续动作。";
        try
        {
            _status.Text = await new LocalPlanExecutor(_tree).Execute(plan,
                () => _capture.Capture(false)?.CombatId, _localPotions.ButtonPressed,
                text => _status.Text = text, cancellation.Token);
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "已停止执行；已出手的动作会正常结算。"; }
        catch (Exception ex) { if (!_disposed) _status.Text = ex.Message; }
        finally
        {
            _execution = null; _executing = false; _continuation = null; _adviceHash = null;
            if (!_disposed)
            {
                _stopExecution.Disabled = true; _execute.Disabled = true;
                _freshness.Text = "执行已停止。若需继续，请重新计算当前状态。";
                RefreshSnapshot();
            }
        }
    }
}
