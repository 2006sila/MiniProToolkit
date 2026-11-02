using System.Collections.ObjectModel;
using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

public partial class ProfileAutomationWindow : Window
{
    private readonly ObservableCollection<RuleView> _rules;
    private readonly IReadOnlyList<FanProfile> _profiles;
    private readonly IReadOnlyDictionary<string, ProfileAutomationRuleStatus> _statuses;
    private readonly ProfileAutomationHistory _history;
    private readonly IReadOnlyDictionary<string, string> _ruleNames;
    private readonly IReadOnlyDictionary<string, string> _profileNames;
    private IReadOnlyList<ProfileAutomationExecution> _historyEntries = [];
    private CancellationTokenSource? _historyLoadCts;
    private bool _closed;
    private int _editing = -1;
    private bool _sync;
    public ProfileAutomationSettings? Result { get; private set; }

    /// <summary>
    /// <paramref name="statuses"/> is an optional runtime snapshot from <see cref="ProfileAutomationStatusBoard"/>.
    /// It is display-only: the status text comes from a closed reason code and never contains a process path,
    /// command line or pattern. Without a snapshot the window simply shows "尚无运行记录".
    /// </summary>
    public ProfileAutomationWindow(
        ProfileAutomationSettings settings,
        IReadOnlyList<FanProfile> profiles,
        IReadOnlyList<ProfileAutomationRuleStatus>? statuses = null,
        string? historyPath = null)
    {
        InitializeComponent();
        // 纯展示：与其他功能窗共用同一份深色原生标题栏实现（DWM 不可用时静默跳过）。
        ObsidianWindowChrome.Apply(this);
        _profiles = profiles;
        _history = new ProfileAutomationHistory(historyPath);
        // Snapshot the caller's current settings, not uncommitted edits or any persisted history labels.
        _ruleNames = settings.Rules.GroupBy(rule => ProfileAutomationHistory.AnonymousId(rule.Name))
            .ToDictionary(group => group.Key, group => group.First().Name);
        _profileNames = profiles.GroupBy(profile => ProfileAutomationHistory.AnonymousId(profile.Name))
            .ToDictionary(group => group.Key, group => group.First().Name);
        Loaded += async (_, _) => await LoadHistoryAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _historyLoadCts?.Cancel();
        };
        _statuses = (statuses ?? []).Where(status => status is not null)
            .GroupBy(status => status.RuleName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        _rules = new(settings.Rules.Select(r => new RuleView(r, StatusOf(r.Name)))); RulesList.ItemsSource = _rules;
        ProfileCombo.ItemsSource = profiles; ProfileCombo.DisplayMemberPath = "Name";
        EnabledCheck.IsChecked = settings.Enabled; PausedCheck.IsChecked = settings.Paused;
        StatusSummaryText.Text = ProfileAutomationStatusBoard.DescribeSummary(_statuses.Values);
        if (_rules.Count > 0) RulesList.SelectedIndex = 0;
    }

    private async void RefreshHistoryClick(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    private async Task LoadHistoryAsync()
    {
        if (_closed) return;
        _historyLoadCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _historyLoadCts = cts;
        RefreshHistoryButton.IsEnabled = false;
        ExportHistoryButton.IsEnabled = false;
        HistoryFeedbackText.Text = "正在读取执行历史…";
        try
        {
            var entries = await Task.Run(() => _history.ReadRecent(200, cts.Token), cts.Token);
            if (_closed || cts.IsCancellationRequested || !ReferenceEquals(_historyLoadCts, cts)) return;
            _historyEntries = entries;
            HistoryGrid.ItemsSource = entries.Select(entry => new
            {
                Time = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Rule = _ruleNames.TryGetValue(entry.RuleId, out var rule) ? rule : entry.RuleId,
                Profile = _profileNames.TryGetValue(entry.ProfileId, out var profile) ? profile : entry.ProfileId,
                Result = entry.Reason switch
                {
                    ProfileAutomationExecutionReason.Applied => "已应用",
                    ProfileAutomationExecutionReason.MissingProfile => "方案不存在",
                    ProfileAutomationExecutionReason.Exception => "应用异常",
                    _ => "未应用 / 安全拒绝"
                },
                Duration = entry.DurationMilliseconds
            }).ToArray();
            HistoryFeedbackText.Text = entries.Count == 0 ? "暂无可读取的执行历史。" : $"已显示最近 {entries.Count} 条执行记录。";
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed) HistoryFeedbackText.Text = "执行历史暂不可用。"; }
        finally
        {
            if (ReferenceEquals(_historyLoadCts, cts))
            {
                _historyLoadCts = null;
                if (!_closed)
                {
                    RefreshHistoryButton.IsEnabled = true;
                    ExportHistoryButton.IsEnabled = _historyEntries.Count > 0;
                }
            }
        }
    }

    private async void ExportHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_closed || _historyEntries.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出去标识执行历史", Filter = "CSV 文件 (*.csv)|*.csv", FileName = "automation-executions.csv"
        };
        if (dialog.ShowDialog(this) != true || _closed) return;
        // Export the typed anonymous entries, never the name-mapped DataGrid projection.
        var csv = ProfileAutomationHistory.ExportCsv(_historyEntries);
        ExportHistoryButton.IsEnabled = false;
        try
        {
            await Task.Run(() => System.IO.File.WriteAllText(dialog.FileName, csv, new System.Text.UTF8Encoding(true)));
            if (!_closed) HistoryFeedbackText.Text = "已导出去标识执行历史（仅哈希 ID，不含当前名称）。";
        }
        catch { if (!_closed) HistoryFeedbackText.Text = "去标识历史导出失败。"; }
        finally { if (!_closed) ExportHistoryButton.IsEnabled = _historyEntries.Count > 0; }
    }

    private ProfileAutomationRuleStatus? StatusOf(string? ruleName) =>
        ruleName is not null && _statuses.TryGetValue(ruleName.Trim(), out var status) ? status : null;
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_sync || RulesList.SelectedIndex < 0) return; if (_editing >= 0 && !Commit()) { _sync = true; RulesList.SelectedIndex = _editing; _sync = false; return; } Select(RulesList.SelectedIndex); }
    private void Select(int index)
    {
        _editing = index; if (index < 0 || index >= _rules.Count) return; var r = _rules[index].ToRule(); _sync = true; RulesList.SelectedIndex = index; _sync = false;
        NameText.Text = r.Name; RuleEnabledCheck.IsChecked = r.Enabled; PriorityText.Text = r.Priority.ToString(CultureInfo.InvariantCulture); DebounceText.Text = r.DebounceSeconds.ToString(CultureInfo.InvariantCulture); ProfileCombo.SelectedItem = _profiles.FirstOrDefault(p => string.Equals(p.Name, r.ProfileName, StringComparison.OrdinalIgnoreCase)); TriggerCombo.SelectedIndex = (int)r.Trigger; ThresholdText.Text = r.Threshold.ToString(CultureInfo.InvariantCulture); ProcessNameText.Text = r.ProcessName ?? ""; ProcessPathText.Text = r.ProcessPath ?? ""; ProcessCommandLineText.Text = r.ProcessCommandLine ?? ""; ProcessCommandLineRegexText.Text = r.ProcessCommandLineRegex ?? ""; StartText.Text = FormatMinute(r.StartMinute); EndText.Text = FormatMinute(r.EndMinute);
        RuleStatusText.Text = StatusOf(r.Name)?.Describe() ?? "这条规则还没有运行记录：命中/跳过只在主窗口运行期间统计，不写入配置文件。";
    }
    private static string FormatMinute(int minute) => minute == 1440 ? "24:00" : $"{minute / 60:00}:{minute % 60:00}";
    private static int ParseMinute(string text)
    {
        if (string.Equals(text.Trim(), "24:00", StringComparison.Ordinal)) return 1440;
        if (TimeSpan.TryParseExact(text.Trim(), ["h\\:mm", "hh\\:mm", "H\\:mm", "HH\\:mm"], CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero && time <= TimeSpan.FromHours(24))
            return time == TimeSpan.FromHours(24) ? 1440 : time.Hours * 60 + time.Minutes;
        return int.Parse(text, CultureInfo.InvariantCulture);
    }

    private bool Commit()
    {
        if (_editing < 0 || _editing >= _rules.Count) return true;
        try
        {
            var rule = BuildRuleFromInputs();
            rule.Validate(_profiles.Select(p => p.Name).ToArray());
            if (_rules.Where((_, i) => i != _editing).Any(r => string.Equals(r.Name, rule.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("规则名称重复。");
            _rules[_editing] = new RuleView(rule, StatusOf(rule.Name)); FeedbackText.Text = $"已更新：{rule.Name}"; return true;
        }
        catch (Exception ex) { FeedbackText.Text = ex.Message; return false; }
    }

    /// <summary>
    /// Builds the rule straight from the editor fields. Process path/command-line values are validated by the Core
    /// rule validation only; nothing here writes them anywhere but the rule itself and the settings file.
    /// </summary>
    private ProfileAutomationRule BuildRuleFromInputs()
    {
        string? path = ProfileAutomationProcessCondition.CleanOptional(ProcessPathText.Text);
        string? commandLineText = ProfileAutomationProcessCondition.CleanOptional(ProcessCommandLineText.Text);
        string? commandLineRegex = ProfileAutomationProcessCondition.CleanOptional(ProcessCommandLineRegexText.Text);
        if (commandLineText is not null && commandLineRegex is not null)
            throw new ArgumentException("命令行条件只能填写「命令行包含」或「命令行正则」其中之一。");
        // The pattern is never echoed: it may contain user secrets.
        if (commandLineRegex is not null && !ProfileAutomationProcessCondition.IsValidCommandLinePattern(commandLineRegex))
            throw new ArgumentException("命令行正则无效，请检查括号、量词和转义；该内容不会被写入日志或诊断。");
        return new ProfileAutomationRule(
            NameText.Text.Trim(),
            RuleEnabledCheck.IsChecked == true,
            int.Parse(PriorityText.Text, CultureInfo.InvariantCulture),
            (ProfileCombo.SelectedItem as FanProfile)?.Name ?? "",
            (ProfileAutomationTrigger)TriggerCombo.SelectedIndex,
            double.Parse(ThresholdText.Text, CultureInfo.InvariantCulture),
            ParseMinute(StartText.Text),
            ParseMinute(EndText.Text),
            int.Parse(DebounceText.Text, CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(ProcessNameText.Text) ? null : ProcessNameText.Text.Trim(),
            path,
            commandLineText,
            commandLineRegex);
    }
    private void NewClick(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0 || !Commit()) return;
        var baseName = "规则"; var name = baseName;
        for (var i = 2; _rules.Any(r => r.Name == name); i++) name = baseName + i;
        _rules.Add(new RuleView(new(name, true, 100, _profiles[0].Name, ProfileAutomationTrigger.AcPower), null));
        Select(_rules.Count - 1);
    }
    private void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (_editing < 0 || _editing >= _rules.Count) return;
        var index = _editing;
        _sync = true; _rules.RemoveAt(index); _sync = false;
        Select(Math.Min(index, _rules.Count - 1));
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        try { var settings = new ProfileAutomationSettings(EnabledCheck.IsChecked == true, PausedCheck.IsChecked == true, _rules.Select(r => r.ToRule()).ToArray()); settings.Validate(_profiles.Select(p => p.Name).ToArray()); Result = settings; DialogResult = true; }
        catch (Exception ex) { FeedbackText.Text = ex.Message; }
    }
    private sealed class RuleView(ProfileAutomationRule rule, ProfileAutomationRuleStatus? status)
    {
        private ProfileAutomationRule _rule = rule;
        public string Name => _rule.Name; public bool Enabled => _rule.Enabled; public int Priority => _rule.Priority; public string ProfileName => _rule.ProfileName;
        public int DebounceSeconds => _rule.DebounceSeconds;
        public ProfileAutomationTrigger Trigger => _rule.Trigger;
        public double Threshold => _rule.Threshold;
        public int StartMinute => _rule.StartMinute;
        public int EndMinute => _rule.EndMinute;
        public string ProcessName => _rule.ProcessName ?? "";
        /// <summary>Log/UI-safe summary: the executable path and command-line text are never shown in the list.</summary>
        public string Summary => $"{_rule.ProfileName} · {_rule.Trigger}{(_rule.ProcessName is null ? "" : " · " + _rule.ProcessName)}{(ProfileAutomationProcessCondition.CleanOptional(_rule.ProcessPath) is null ? "" : " · 路径条件")}{(ProfileAutomationProcessCondition.CleanOptional(_rule.ProcessCommandLine) is null && ProfileAutomationProcessCondition.CleanOptional(_rule.ProcessCommandLineRegex) is null ? "" : " · 命令行条件")} · P{_rule.Priority}";
        /// <summary>Runtime status line: closed reason code plus counters, never a path, command line or pattern.</summary>
        public string Status => status?.CompactSummary ?? "";
        public Visibility StatusVisibility => string.IsNullOrEmpty(Status) ? Visibility.Collapsed : Visibility.Visible;
        public ProfileAutomationRule ToRule() => _rule;
    }
}
