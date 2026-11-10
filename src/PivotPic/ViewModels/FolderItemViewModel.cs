using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using PivotPic.Models;
using PivotPic.Services;

namespace PivotPic.ViewModels;

/// <summary>
/// 一张监控目录卡片的数据视图模型。
/// <para>
/// 所有可写属性都要「先判等、再写 Model、再落盘、最后通知」，
/// 因为卡片的每个开关都直接决定监控行为，必须立刻生效并持久化。
/// </para>
/// </summary>
public sealed class FolderItemViewModel : INotifyPropertyChanged
{
    /// <summary>阈值允许的最小值。</summary>
    public const double MinThreshold = 1;

    /// <summary>阈值允许的最大值。</summary>
    public const double MaxThreshold = 100_000;

    /// <summary>阈值输入的合法原始文本（用户失焦时越界就回退到它）。</summary>
    private string _lastValidThresholdText;

    private string _thresholdText;

    private bool _isScanning;
    private int _scanScanned;
    private int _scanTotal;
    private bool _isExpanded;

    private bool _isEditing;
    private string _editingPath = string.Empty;

    private string _resolvedPath = string.Empty;
    private IReadOnlyList<string> _unknownTokens = Array.Empty<string>();

    /// <summary>包装一个目录配置。</summary>
    /// <param name="model">来自 <see cref="AppConfig.Folders"/> 的实例，不能为 null。</param>
    public FolderItemViewModel(WatchedFolder model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _thresholdText = FormatThreshold(Model.Threshold);
        _lastValidThresholdText = _thresholdText;
        RefreshTemplateInfo();
        RefreshIgnoredFolders();
        RefreshSizeRules();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>底层配置对象。</summary>
    public WatchedFolder Model { get; }

    /// <summary>目录 Id，用于和扫描进度事件对齐。</summary>
    public string FolderId => Model.Id;

    /// <summary>用户填写的原始路径模板（可能带占位符）。</summary>
    public string DisplayPath => Model.Path;

    /// <summary>占位符展开后的真实路径。</summary>
    public string ResolvedPath
    {
        get => _resolvedPath;
        private set
        {
            if (string.Equals(_resolvedPath, value, StringComparison.Ordinal))
            {
                return;
            }

            _resolvedPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResolvedLine));
            OnPropertyChanged(nameof(PathTooltip));
        }
    }

    /// <summary>模板中无法识别的占位符（含花括号原文）。</summary>
    public IReadOnlyList<string> UnknownTokens
    {
        get => _unknownTokens;
        private set
        {
            _unknownTokens = value ?? Array.Empty<string>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUnknownTokens));
            OnPropertyChanged(nameof(UnknownTokenText));
            OnPropertyChanged(nameof(ResolvedLine));
        }
    }

    /// <summary>是否存在未识别占位符，界面据此把整行文字转为警告色。</summary>
    public bool HasUnknownTokens => _unknownTokens.Count > 0;

    /// <summary>未识别占位符的拼接文本。</summary>
    public string UnknownTokenText => string.Join("、", _unknownTokens);

    /// <summary>卡片第二行文字：实际路径 + 可选的未识别占位符提示。</summary>
    public string ResolvedLine => HasUnknownTokens
        ? $"实际路径：{ResolvedPath} · 未识别占位符：{UnknownTokenText}"
        : $"实际路径：{ResolvedPath}";

    /// <summary>路径文本的悬停提示，展示完整解析结果。</summary>
    public string PathTooltip => string.IsNullOrEmpty(ResolvedPath) ? DisplayPath : ResolvedPath;

    // ==================== 成品幅面上限 ====================

    private string? _maxSideCmText;

    /// <summary>是否按成品幅面跳过超大图。</summary>
    public bool SizeLimitEnabled
    {
        get => Model.SizeLimitEnabled;
        set
        {
            if (Model.SizeLimitEnabled == value)
            {
                return;
            }

            Model.SizeLimitEnabled = value;
            OnPropertyChanged();
            PersistConfig();
        }
    }

    /// <summary>JPEG 兼容模式（按目录）：连续 JPEG 转基线标准。</summary>
    public bool ConvertProgressiveJpeg
    {
        get => Model.ConvertProgressiveJpeg;
        set
        {
            if (Model.ConvertProgressiveJpeg == value)
            {
                return;
            }

            Model.ConvertProgressiveJpeg = value;
            OnPropertyChanged();
            PersistConfig();
        }
    }

    /// <summary>幅面上限的输入文本（厘米）。</summary>
    public string MaxSideCmText
    {
        get => _maxSideCmText ??= FormatCm(Model.MaxSideCm);
        set
        {
            if (string.Equals(_maxSideCmText, value, StringComparison.Ordinal))
            {
                return;
            }

            _maxSideCmText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>提交输入：非法值回退，合法值夹在 1–10000 厘米之间。</summary>
    public void CommitMaxSideCm()
    {
        var text = (MaxSideCmText ?? string.Empty).Trim();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var cm))
        {
            MaxSideCmText = FormatCm(Model.MaxSideCm);
            return;
        }

        cm = Math.Clamp(cm, 1, 10_000);
        Model.MaxSideCm = cm;
        MaxSideCmText = FormatCm(cm);
        PersistConfig();
    }

    private static string FormatCm(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);

    // ==================== 幅面规则（按文件名关键词） ====================

    /// <summary>界面上展示的规则标签。</summary>
    public ObservableCollection<SizeRuleChip> SizeRuleChips { get; } = new();

    /// <summary>从模型重载规则标签。</summary>
    public void RefreshSizeRules()
    {
        SizeRuleChips.Clear();

        foreach (var rule in Model.SizeRules)
        {
            SizeRuleChips.Add(new SizeRuleChip(rule));
        }
    }

    /// <summary>新增一条规则：关键词 + 上限（厘米）。重复或非法输入返回 false。</summary>
    public bool AddSizeRule(string? keyword, string? cmText)
    {
        var key = (keyword ?? string.Empty).Trim();
        if (key.Length == 0 || Model.SizeRules.Count >= 20)
        {
            return false;
        }

        if (!double.TryParse((cmText ?? string.Empty).Trim(), NumberStyles.Float,
                CultureInfo.CurrentCulture, out var cm))
        {
            return false;
        }

        cm = Math.Clamp(cm, 1, 10_000);

        if (Model.SizeRules.Any(existing =>
                string.Equals(existing.Keyword, key, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var rule = new SizeRule { Keyword = key, MaxSideCm = cm };
        Model.SizeRules.Add(rule);
        SizeRuleChips.Add(new SizeRuleChip(rule));
        PersistConfig();
        return true;
    }

    /// <summary>删除一条规则。</summary>
    public void RemoveSizeRule(SizeRuleChip? chip)
    {
        if (chip is null)
        {
            return;
        }

        Model.SizeRules.Remove(chip.Model);
        SizeRuleChips.Remove(chip);
        PersistConfig();
    }

    // ==================== 忽略的子文件夹 ====================

    /// <summary>界面上展示的忽略名单，和 <see cref="WatchedFolder.IgnoredFolders"/> 保持同步。</summary>
    public ObservableCollection<string> IgnoredFolders { get; } = new();

    /// <summary>从模型重新装载一遍（配置导入或外部变更后调用）。</summary>
    public void RefreshIgnoredFolders()
    {
        IgnoredFolders.Clear();

        foreach (var name in Model.IgnoredFolders)
        {
            IgnoredFolders.Add(name);
        }
    }

    /// <summary>添加一个忽略目录名；重名或非法字符会被挡掉。返回是否真的添加成功。</summary>
    public bool AddIgnoredFolder(string? raw)
    {
        var name = (raw ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return false;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid.ToString(), string.Empty);
        }

        name = name.Trim();
        if (name.Length == 0 || name is "." or "..")
        {
            return false;
        }

        if (Model.IgnoredFolders.Count >= 20)
        {
            return false;
        }

        if (Model.IgnoredFolders.Any(existing =>
                string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Model.IgnoredFolders.Add(name);
        IgnoredFolders.Add(name);
        AppServices.Config?.SaveDebounced();
        return true;
    }

    /// <summary>移除一个忽略目录名。</summary>
    public void RemoveIgnoredFolder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var removed = Model.IgnoredFolders.RemoveAll(existing =>
            string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            return;
        }

        var item = IgnoredFolders.FirstOrDefault(existing =>
            string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

        if (item is not null)
        {
            IgnoredFolders.Remove(item);
        }

        AppServices.Config?.SaveDebounced();
    }

    // ==================== 路径健康状态 ====================

    private bool _pathExists = true;
    private bool _isNetwork;
    private long? _latencyMs;

    /// <summary>展开后的目录当前是否存在。</summary>
    public bool PathExists => _pathExists;

    /// <summary>路径是否位于网络位置（UNC 或映射盘）。</summary>
    public bool IsNetwork => _isNetwork;

    /// <summary>最近一次测得的网络延迟（毫秒）；不可达为 null。</summary>
    public long? LatencyMs => _latencyMs;

    /// <summary>是否需要显示状态标签：本地且存在时不显示。</summary>
    public bool HasStatus => !_pathExists || _isNetwork;

    /// <summary>卡片上的状态文字。</summary>
    public string StatusText
    {
        get
        {
            if (!_isNetwork)
            {
                return _pathExists ? string.Empty : "路径不存在";
            }

            // 网络路径优先报连接状况：不可达/超时比"路径不存在"更能说明问题。
            if (_latencyMs is null)
            {
                return "网络位置 · 无法连接";
            }

            if (_latencyMs.Value > NetworkPath.UnstableLatencyMs)
            {
                return $"网络 {_latencyMs.Value} ms · 连接不稳定，请联系管理员优化";
            }

            return _pathExists ? $"网络 {_latencyMs.Value} ms" : "网络可达，但目录不存在";
        }
    }

    /// <summary>状态是否需要告警配色。</summary>
    public bool StatusIsWarning => !_pathExists
        || (_isNetwork && (_latencyMs is null || _latencyMs.Value > NetworkPath.UnstableLatencyMs));

    /// <summary>由页面定时刷新：目录是否存在、是否网络路径、网络延迟。</summary>
    public void UpdateHealth(bool exists, bool isNetwork, long? latencyMs)
    {
        if (_pathExists == exists && _isNetwork == isNetwork && _latencyMs == latencyMs)
        {
            return;
        }

        _pathExists = exists;
        _isNetwork = isNetwork;
        _latencyMs = latencyMs;

        OnPropertyChanged(nameof(PathExists));
        OnPropertyChanged(nameof(IsNetwork));
        OnPropertyChanged(nameof(LatencyMs));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusIsWarning));
    }

    // ==================== 路径编辑 ====================

    /// <summary>编辑中的路径草稿，随输入实时更新。</summary>
    public string EditingPath
    {
        get => _editingPath;
        set
        {
            if (string.Equals(_editingPath, value, StringComparison.Ordinal))
            {
                return;
            }

            _editingPath = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditingPreviewLine));
            OnPropertyChanged(nameof(HasEditingUnknown));
        }
    }

    /// <summary>编辑时的实时解析预览，让用户立刻看到 {mm-dd} 会展开成什么。</summary>
    public string EditingPreviewLine
    {
        get
        {
            var text = (_editingPath ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return "输入路径，可插入 {mm-dd} 这类时间占位符";
            }

            try
            {
                var resolved = AppServices.PathTemplate?.Resolve(text) ?? text;
                var unknown = AppServices.PathTemplate?.FindUnknownPlaceholders(text) ?? Array.Empty<string>();
                return unknown.Count > 0
                    ? $"预览：{resolved} · 未识别占位符：{string.Join("、", unknown)}"
                    : $"预览：{resolved}";
            }
            catch (Exception)
            {
                return "预览：路径包含非法字符";
            }
        }
    }

    /// <summary>编辑草稿里是否存在未识别占位符，界面据此把预览行转为警告色。</summary>
    public bool HasEditingUnknown
    {
        get
        {
            try
            {
                return AppServices.PathTemplate
                    ?.FindUnknownPlaceholders(_editingPath ?? string.Empty)
                    .Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>把草稿重置为已保存的路径模板（打开设置弹窗、提交成功或放弃时调用）。</summary>
    public void BeginEdit() => EditingPath = Model.Path ?? string.Empty;

    /// <summary>
    /// 提交编辑。返回 true 表示路径确实变了，调用方需要重挂该目录的监控。
    /// 空路径视为取消，不做任何改动。
    /// </summary>
    public bool CommitEdit()
    {
        var next = (_editingPath ?? string.Empty).Trim();

        if (next.Length == 0 || string.Equals(next, Model.Path, StringComparison.Ordinal))
        {
            EditingPath = Model.Path ?? string.Empty;
            return false;
        }

        Model.Path = next;
        RefreshTemplateInfo();
        OnPropertyChanged(nameof(DisplayPath));
        OnPropertyChanged(nameof(PathTooltip));
        return true;
    }

    /// <summary>是否包含子文件夹。</summary>
    public bool IncludeSubdirectories
    {
        get => Model.IncludeSubdirectories;
        set
        {
            if (Model.IncludeSubdirectories == value)
            {
                return;
            }

            Model.IncludeSubdirectories = value;
            OnPropertyChanged();
            PersistConfig();

            // 该开关直接决定 FileSystemWatcher 的注册范围，必须重挂才生效。
            RemountIfMonitoring();
        }
    }

    /// <summary>是否启用分辨率阈值检查。</summary>
    public bool ResolutionCheckEnabled
    {
        get => Model.ResolutionCheckEnabled;
        set
        {
            if (Model.ResolutionCheckEnabled == value)
            {
                return;
            }

            Model.ResolutionCheckEnabled = value;
            OnPropertyChanged();
            PersistConfig();
        }
    }

    /// <summary>该目录是否参与监控。</summary>
    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value)
            {
                return;
            }

            Model.Enabled = value;
            OnPropertyChanged();
            PersistConfig();

            // 全局总开关关闭时只记配置，等总开关打开再统一挂载。
            if (!IsMonitoringGloballyEnabled)
            {
                return;
            }

            if (value)
            {
                AppServices.Monitor?.StartFolder(Model);

                // 刚启用：目录里已经存在的图片只能靠扫描发现（文件事件只覆盖之后进来的），
                // 所以这里补扫一次，等价于「加进来就开始处理」的老行为。
                StartScan();
            }
            else
            {
                AppServices.Monitor?.StopFolder(Model.Id);
            }
        }
    }

    /// <summary>阈值数值（写入配置）。</summary>
    public double Threshold
    {
        get => Model.Threshold;
        set
        {
            var clamped = ClampThreshold(value);
            if (Math.Abs(Model.Threshold - clamped) < 0.0001)
            {
                return;
            }

            Model.Threshold = clamped;
            OnPropertyChanged();

            SetThresholdText(FormatThreshold(clamped), keepAsValid: true);
            PersistConfig();
        }
    }

    /// <summary>
    /// 阈值输入框的文本。用户逐字键入时不做回退，只有落在合法区间内的中间态才写回配置，
    /// 越界或非数字由 LostFocus 统一校验并回退。
    /// </summary>
    public string ThresholdText
    {
        get => _thresholdText;
        set
        {
            var text = value ?? string.Empty;
            if (string.Equals(_thresholdText, text, StringComparison.Ordinal))
            {
                return;
            }

            _thresholdText = text;
            OnPropertyChanged();

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
                && parsed >= MinThreshold
                && parsed <= MaxThreshold)
            {
                _lastValidThresholdText = text;

                if (Math.Abs(Model.Threshold - parsed) > 0.0001)
                {
                    Model.Threshold = parsed;
                    OnPropertyChanged(nameof(Threshold));
                    PersistConfig();
                }
            }
        }
    }

    /// <summary>阈值口径在 <see cref="ThresholdMetric"/> 中的序号（0 长边 / 1 短边 / 2 总像素 / 3 体积）。</summary>
    public int MetricIndex
    {
        get => (int)Model.Metric;
        set
        {
            var index = Math.Clamp(value, 0, 3);
            if ((int)Model.Metric == index)
            {
                return;
            }

            Model.Metric = (ThresholdMetric)index;
            OnPropertyChanged();
            PersistConfig();
        }
    }

    /// <summary>是否正在扫描（决定卡片底部进度条的显隐）。</summary>
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (_isScanning == value)
            {
                return;
            }

            _isScanning = value;

            if (!value)
            {
                // 结束扫描时把进度归零，下次进入不会先闪一帧旧进度。
                _scanScanned = 0;
                _scanTotal = 0;
                OnPropertyChanged(nameof(ScanScanned));
                OnPropertyChanged(nameof(ScanTotal));
                NotifyScanRatio();
            }

            OnPropertyChanged();
        }
    }

    /// <summary>已扫描文件数。</summary>
    public int ScanScanned
    {
        get => _scanScanned;
        set
        {
            if (_scanScanned == value)
            {
                return;
            }

            _scanScanned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScanText));
            NotifyScanRatio();
        }
    }

    /// <summary>待扫描文件总数。</summary>
    public int ScanTotal
    {
        get => _scanTotal;
        set
        {
            if (_scanTotal == value)
            {
                return;
            }

            _scanTotal = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScanText));
            NotifyScanRatio();
        }
    }

    /// <summary>扫描进度百分比（0–100）。</summary>
    public double ScanPercent => _scanTotal <= 0
        ? 0
        : Math.Clamp(_scanScanned * 100.0 / _scanTotal, 0, 100);

    /// <summary>扫描进度小数（0–1），供 ScaleTransform 直接使用，省掉一个转换器。</summary>
    public double ScanFraction => _scanTotal <= 0
        ? 0
        : Math.Clamp((double)_scanScanned / _scanTotal, 0, 1);

    /// <summary>进度条右侧的文字。</summary>
    public string ScanText => $"已扫描 {_scanScanned} / 共 {_scanTotal}";

    /// <summary>
    /// 开始一次全量扫描（卡片扫描行会显示进度）。
    /// <para>
    /// 卡片上的「扫描」按钮与「目录设置」弹窗关闭时都走这里，保证两条入口的行为完全一致：
    /// 清零进度 → 立刻置 <see cref="IsScanning"/> 让扫描行展开 → 后台执行，不阻塞 UI。
    /// </para>
    /// </summary>
    public void StartScan()
    {
        // 判断口径以监控服务的忙碌状态为准，不再看 VM 的 IsScanning：
        // 扫描完成后的 2 秒淡出动画期间 IsScanning 仍为 true，但扫描其实已经结束，
        // 这时补扫应当照常发生（进度行闪一下可接受，不涉及数据正确性）。
        // 真正的并发保护由 IsFolderBusy 提供：改路径/批量启动等流程是直接调
        // MonitorService.ScanAsync 的，首个进度事件回来前 IsScanning 还是 false，
        // 若此时再起一个 ScanAsync，会让 _scans[Id] 互相覆盖（前一个的清理会摘掉
        // 后一个的 CTS），导致取消扫描/忙碌判定失效、同一批文件重复入队。
        // 服务未初始化时退回 VM 自己的扫描状态。
        if (AppServices.Monitor?.IsFolderBusy(FolderId) ?? IsScanning)
        {
            return;
        }

        ScanScanned = 0;
        ScanTotal = 0;
        IsScanning = true;

        _ = RunScanAsync();
    }

    /// <summary>执行一次扫描；失败只记日志并复位扫描状态，异常不允许冒到 UI 线程。</summary>
    private async Task RunScanAsync()
    {
        try
        {
            var monitor = AppServices.Monitor;

            if (monitor is null)
            {
                IsScanning = false;
                return;
            }

            // 回到 UI 线程继续，后面改 IsScanning / 进度属性要触发界面通知。
            await monitor.ScanAsync(Model, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Error("扫描", $"扫描失败：{ex.Message}", Model.Path);
            IsScanning = false;
        }
    }

    /// <summary>卡片路径是否处于展开（换行显示全文）状态。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    /// <summary>重新解析占位符（配置或时间变化后调用）。</summary>
    public void RefreshTemplateInfo()
    {
        var template = Model.Path ?? string.Empty;

        try
        {
            ResolvedPath = AppServices.PathTemplate?.Resolve(template) ?? template;
            UnknownTokens = AppServices.PathTemplate?.FindUnknownPlaceholders(template)
                            ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            // 模板里出现非法字符时 Resolve 可能抛异常，界面退回原文即可，不允许影响页面渲染。
            ResolvedPath = template;
            UnknownTokens = Array.Empty<string>();
        }
    }

    /// <summary>把阈值文本回退到最近一次的合法值（越界校验失败时调用）。</summary>
    public void RevertThresholdText()
    {
        SetThresholdText(_lastValidThresholdText, keepAsValid: true);
    }

    /// <summary>取一个「回退目标」：优先上次合法文本，否则用模型当前值。</summary>
    public string CurrentValidThresholdText => string.IsNullOrWhiteSpace(_lastValidThresholdText)
        ? FormatThreshold(Model.Threshold)
        : _lastValidThresholdText;

    /// <summary>数值是否落在允许区间内。</summary>
    public static bool IsThresholdInRange(double value)
    {
        return value >= MinThreshold && value <= MaxThreshold;
    }

    /// <summary>全局总开关是否打开。</summary>
    private static bool IsMonitoringGloballyEnabled
    {
        get
        {
            try
            {
                return AppServices.Config?.Current?.MonitoringEnabled ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>把当前 Model 写回配置（防抖落盘，失败只记日志）。</summary>
    private static void PersistConfig()
    {
        try
        {
            AppServices.Config?.SaveDebounced();
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("配置", $"保存目录配置失败：{ex.Message}");
        }
    }

    /// <summary>按当前配置重挂 watcher，让「包含子文件夹」之类的改动立即生效。</summary>
    private void RemountIfMonitoring()
    {
        if (!Model.Enabled || !IsMonitoringGloballyEnabled)
        {
            return;
        }

        try
        {
            AppServices.Monitor?.StartFolder(Model);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn("监控", $"重挂目录失败：{ex.Message}", Model.Path);
        }
    }

    /// <summary>更新阈值文本并通知界面（避免 setter 里的相等短路吞掉回退）。</summary>
    private void SetThresholdText(string text, bool keepAsValid)
    {
        var normalized = text ?? string.Empty;

        if (keepAsValid)
        {
            _lastValidThresholdText = normalized;
        }

        if (string.Equals(_thresholdText, normalized, StringComparison.Ordinal))
        {
            // 文本没变但界面可能被用户改脏了，强制刷新一次绑定。
            OnPropertyChanged(nameof(ThresholdText));
            return;
        }

        _thresholdText = normalized;
        OnPropertyChanged(nameof(ThresholdText));
    }

    /// <summary>通知三个派生进度属性。</summary>
    private void NotifyScanRatio()
    {
        OnPropertyChanged(nameof(ScanPercent));
        OnPropertyChanged(nameof(ScanFraction));
    }

    /// <summary>阈值格式化为最多两位小数。</summary>
    private static string FormatThreshold(double value)
    {
        return value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>把阈值限制在合法区间。</summary>
    private static double ClampThreshold(double value)
    {
        if (double.IsNaN(value) || value < MinThreshold)
        {
            return MinThreshold;
        }

        return value > MaxThreshold ? MaxThreshold : value;
    }

    /// <summary>触发属性变更通知。</summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>规则标签的展示包装。</summary>
public sealed class SizeRuleChip
{
    public SizeRuleChip(SizeRule model)
    {
        Model = model;
    }

    /// <summary>底层规则对象。</summary>
    public SizeRule Model { get; }

    /// <summary>标签文字，如「UV · 500cm」。</summary>
    public string Display => $"{Model.Keyword} · {Model.MaxSideCm:0.#}cm";
}
