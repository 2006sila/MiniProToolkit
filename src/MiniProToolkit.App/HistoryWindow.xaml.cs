using System.Globalization;
using System.IO;
using IOPath = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MiniProToolkit.Core;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace MiniProToolkit.App;

public partial class HistoryWindow : Window
{
    private readonly string _directory;
    private IReadOnlyList<HardwareTelemetryRecord> _records = [];
    private IReadOnlyList<HardwareTelemetryRecord> _filtered = [];
    private IReadOnlyList<HistoryEvent> _events = [];
    private IReadOnlyList<HistoryEvent> _visibleEvents = [];
    private CancellationTokenSource? _load;
    private DateTimeOffset _start, _end;
    private double _zoom = 1;
    private bool _ready;
    private bool _closed;
    private bool _local = true;
    private string[] _externalFiles = [];

    // Application and Guardian logs are written through Core BoundedTextLog:
    // 2 MiB live file + 3 rotated archives (path, path.1, path.2, path.3). The history window must
    // read the same bounded set, otherwise every record that rotated out of the live file silently
    // disappears from the timeline.
    private const int LogRetainedArchives = BoundedTextLog.DefaultRetainedFiles; // 3 -> path.1 .. path.3
    private const int LogFilesPerSource = LogRetainedArchives + 1;
    private const int LogTailBytesPerFile = TelemetryHistory.DefaultLogTailBytes;   // 2 MiB
    private const int LogTailEntriesPerFile = TelemetryHistory.DefaultLogTailEntries; // 2000
    private const long LogBytesMiB = 1024 * 1024;

    /// <summary>Rotated archives oldest-first (path.3, path.2, path.1) followed by the live file.</summary>
    private static IReadOnlyList<string> BoundedLogFilesOldestFirst(string path) =>
        Enumerable.Range(1, LogRetainedArchives).Reverse().Select(index => $"{path}.{index}").Append(path).ToArray();

    public HistoryWindow(string directory)
    {
        _directory = directory;
        InitializeComponent();
        // 纯展示：与其他功能窗共用同一份深色原生标题栏实现（DWM 不可用时静默跳过）。
        ObsidianWindowChrome.Apply(this);
        _ready = true;
        var settings = new TelemetryRetentionSettingsStore(_directory).Load(out var warning);
        RetainedText.Text = settings.RetainedArchiveCount.ToString(CultureInfo.InvariantCulture);
        MaxBytesText.Text = (settings.MaximumFileBytes / 1048576).ToString(CultureInfo.InvariantCulture);
        MaxAgeText.Text = settings.MaximumArchiveAgeDays?.ToString(CultureInfo.InvariantCulture) ?? "";
        FromText.Text = FormatTime(DateTimeOffset.Now.AddHours(-24));
        ToText.Text = FormatTime(DateTimeOffset.Now);
        StatusText.Text = warning ?? "默认加载最近 24 小时；全部时间会读取全部归档。";
        Loaded += async (_, _) => await LoadLocalAsync();
        Closed += (_, _) => { _closed = true; _load?.Cancel(); };
    }
    private async Task LoadLocalAsync()
    {
        try
        {
            if (!TryRange(out var start, out var end)) return;
            await LoadFilesAsync([], true, start, end);
        }
        catch (Exception ex) { if (!_closed) StatusText.Text = "历史加载失败：" + ex.Message; }
    }
    private async Task LoadFilesAsync(string[] files, bool local, DateTimeOffset? start = null, DateTimeOffset? end = null)
    {
        if (_closed) return;
        _local = local;
        if (!local) _externalFiles = files;
        _load?.Cancel();
        var cancellation = new CancellationTokenSource();
        _load = cancellation;
        StatusText.Text = "正在加载记录…";
        try
        {
            var data = await Task.Run(() =>
            {
                var result = local ? TelemetryHistory.LoadLocal(_directory, start, end, cancellation.Token) :
                    TelemetryHistory.Load(files, cancellation.Token, start, end);
                var events = TelemetryHistory.Events(result.Records).ToList();
                var warnings = result.Warnings.ToList();
                if (local)
                {
                    var sources = new[]
                    {
                        (Path: AppLog.Path, Source: "应用"),
                        (Path: IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiniProToolkit", "guardian.log"), Source: "Guardian")
                    };
                    var archivedAny = false;
                    foreach (var log in sources)
                    {
                        var archivesFound = 0;
                        foreach (var file in BoundedLogFilesOldestFirst(log.Path))
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            if (!File.Exists(file)) continue;
                            if (!string.Equals(file, log.Path, StringComparison.Ordinal)) archivesFound++;
                            try
                            {
                                // Per-file budget: newest 2 MiB / 2000 lines, so four files per source
                                // cap the transient read at ~8 MiB / 8000 records (both sources ~16 MiB
                                // / 16000). ReadLog reports its own truncation through the warning hook.
                                var name = IOPath.GetFileName(file);
                                events.AddRange(TelemetryHistory.ReadLog(file, log.Source,
                                    LogTailBytesPerFile, LogTailEntriesPerFile,
                                    message => warnings.Add($"{log.Source}日志 {name}：{message}"),
                                    cancellation.Token));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            { warnings.Add($"{log.Source}日志 {IOPath.GetFileName(file)}：{ex.Message}"); }
                        }
                        if (archivesFound > 0 || File.Exists(log.Path))
                        {
                            archivedAny = true;
                            // Rotation discards the oldest archive, and even the live file is only read
                            // as a bounded tail: never suggest that the whole log history is available.
                            warnings.Add($"{log.Source}日志：每份仅读取末尾 {LogTailBytesPerFile / 1024} KiB、最多 {LogTailEntriesPerFile} 条，最多 {LogRetainedArchives} 份轮转归档（当前发现 {archivesFound} 份，单来源约 {LogFilesPerSource * LogTailBytesPerFile / LogBytesMiB} MiB / {LogFilesPerSource * LogTailEntriesPerFile} 条）；已轮转的日志归档按保留数量有限，最早内容可能已淘汰。");
                        }
                    }
                    if (archivedAny)
                        warnings.Add($"日志读取总预算：应用与 Guardian 合计约 {2 * LogFilesPerSource * LogTailBytesPerFile / LogBytesMiB} MiB / {2 * LogFilesPerSource * LogTailEntriesPerFile} 条临时记录，超出部分不加载。");
                    foreach (var journal in new[]
                    {
                        (IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniProToolkit", "history", "diagnostic-events.jsonl"), "应用"),
                        (IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiniProToolkit", "diagnostic-events.jsonl"), "Guardian")
                    })
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        try
                        {
                            events.AddRange(new DiagnosticEventStore(journal.Item1).ReadRecent(200).Select(item =>
                                new HistoryEvent(item.Timestamp, Enum.TryParse<HistoryEventKind>(item.Category, true, out var kind) ? kind : TelemetryHistory.Classify(item.Message), item.Message, journal.Item2 + "/" + item.Source)));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"{journal.Item2}持久化事件：{ex.Message}"); }
                    }
                }
                cancellation.Token.ThrowIfCancellationRequested();
                return (result.Records, events, warnings);
            }, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested || !ReferenceEquals(_load, cancellation)) return;
            _records = data.Records;
            _events = data.events.Distinct().OrderBy(e => e.Timestamp).ToArray();
            var times = _records.Select(r => r.Timestamp).Concat(_events.Select(r => r.Timestamp)).ToArray();
            _start = start ?? (times.Length > 0 ? times.Min() : DateTimeOffset.Now.AddHours(-1));
            _end = end ?? (times.Length > 0 ? times.Max() : DateTimeOffset.Now);
            FromText.Text = FormatTime(_start); ToText.Text = FormatTime(_end);
            ApplyFilter();
            _ = new TelemetryRetentionSettingsStore(_directory).Load(out var warning);
            StatusText.Text = $"加载 {_records.Count} 条采样、{_events.Count} 条事件（{(local ? "本机索引按时段加载" : "所选文件；不使用持久索引")}）。" + string.Join("；", data.warnings) + warning;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !cancellation.IsCancellationRequested && ReferenceEquals(_load, cancellation)) StatusText.Text = "加载失败：" + ex.Message; }
        finally { if (ReferenceEquals(_load, cancellation)) _load = null; cancellation.Dispose(); }
    }
    private async void ReloadClick(object sender, RoutedEventArgs e) => await LoadLocalAsync();
    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "遥测 JSONL (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await LoadFilesAsync(dialog.FileNames, false);
    }
    private static string FormatTime(DateTimeOffset value) => value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    private async void AllClick(object sender, RoutedEventArgs e) => await LoadFilesAsync(_externalFiles, _local);
    private bool TryRange(out DateTimeOffset start, out DateTimeOffset end)
    {
        var validStart = DateTimeOffset.TryParse(FromText.Text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out start);
        var validEnd = DateTimeOffset.TryParse(ToText.Text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out end);
        if (validStart && validEnd && start <= end) return true;
        StatusText.Text = "请输入有效起止时间，起始不能晚于结束，例如 2026-10-31 09:30:00。";
        return false;
    }
    private async void FilterClick(object sender, RoutedEventArgs e)
    {
        if (!TryRange(out var start, out var end)) return;
        await LoadFilesAsync(_externalFiles, _local, start, end);
    }
    private TelemetryRetentionSettings ReadRetention()
    {
        if (!int.TryParse(RetainedText.Text, out var count) || !int.TryParse(MaxBytesText.Text, out var mib) ||
            (!string.IsNullOrWhiteSpace(MaxAgeText.Text) && !int.TryParse(MaxAgeText.Text, out _)))
            throw new ArgumentException("请输入整数，年龄留空表示关闭。");
        var settings = new TelemetryRetentionSettings { RetainedArchiveCount = count, MaximumFileBytes = (long)mib * 1048576,
            MaximumArchiveAgeDays = string.IsNullOrWhiteSpace(MaxAgeText.Text) ? null : int.Parse(MaxAgeText.Text) };
        if (!settings.IsValid) throw new ArgumentException("份数 1–30、大小 1–100 MiB、年龄 1–365 天或留空。");
        return settings;
    }
    private void SaveRetentionClick(object sender, RoutedEventArgs e)
    {
        try { new TelemetryRetentionSettingsStore(_directory).Save(ReadRetention()); StatusText.Text = "已保存；下次写入时生效（最多 60 秒），不立即清理现有文件。"; }
        catch (Exception ex) { StatusText.Text = "保留设置未保存：" + ex.Message; }
    }
    private async void CleanupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadRetention();
            var plan = await Task.Run(() => TelemetryMaintenance.Plan(_directory, settings, DateTimeOffset.UtcNow));
            if (_closed) return;
            if (plan.Files.Count == 0) { StatusText.Text = "没有符合当前保留条件的归档。"; return; }
            var summary = string.Join("\n", plan.Files.Take(60).Select(f => $"{IOPath.GetFileName(f.Path)} · {f.Length / 1048576d:0.00} MiB · {f.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"));
            if (System.Windows.MessageBox.Show(this, $"将删除 {plan.Files.Count} 份归档，目录：{plan.Directory}\n按归档序号/文件修改时间清理，不逐行删除。活跃文件与 schema 备份保留。\n{summary}\n（最多列出前 60 份）\n此操作不可撤销。", "确认清理历史归档", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var deleted = await Task.Run(() => TelemetryMaintenance.Execute(plan));
            if (!_closed) StatusText.Text = $"已删除 {deleted} 份归档；预览后变化的文件已跳过。";
        }
        catch (Exception ex) { if (!_closed) StatusText.Text = "清理失败：" + ex.Message; }
    }
    private void ApplyFilter()
    {
        _filtered = _records.Where(r => r.Timestamp >= _start && r.Timestamp <= _end).ToArray();
        _zoom = 1; PanSlider.Value = 0;
        FilterEvents(); RenderChart();
        StatusText.Text = $"所选时段：{_filtered.Count} 条采样、{_visibleEvents.Count} 条筛选事件。";
    }
    private void FilterEvents()
    {
        if (!_ready) return;
        HistoryEventKind? kind = KindCombo.SelectedIndex switch
        { 1 => HistoryEventKind.Error, 2 => HistoryEventKind.Recovery, 3 => HistoryEventKind.Mode, 4 => HistoryEventKind.Control, _ => null };
        var text = SearchText.Text.Trim();
        _visibleEvents = _events.Where(e => e.Timestamp >= _start && e.Timestamp <= _end &&
            (kind is null || e.Kind == kind) && (text.Length == 0 || e.Message.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.Timestamp).ToArray();
        EventsGrid.ItemsSource = _visibleEvents;
    }
    private void EventFilterChanged(object sender, SelectionChangedEventArgs e) => FilterEvents();
    private void SearchChanged(object sender, TextChangedEventArgs e) => FilterEvents();
    private void ZoomInClick(object sender, RoutedEventArgs e) { _zoom = Math.Min(1024, _zoom * 2); RenderChart(); }
    private void ZoomOutClick(object sender, RoutedEventArgs e) { _zoom = Math.Max(1, _zoom / 2); RenderChart(); }
    private void ResetZoomClick(object sender, RoutedEventArgs e) { _zoom = 1; PanSlider.Value = 0; RenderChart(); }
    private void SeriesChanged(object sender, RoutedEventArgs e) => RenderChart();
    private void UnitChanged(object sender, SelectionChangedEventArgs e) { if (_ready) RenderChart(); }
    private void ChartChanged(object sender, SizeChangedEventArgs e) => RenderChart();
    private void PanChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_ready) RenderChart(); }

    private void RenderChart()
    {
        if (!_ready || _closed || Chart.ActualWidth < 120) return;
        Chart.Children.Clear();
        var span = Math.Max(1, (_end - _start).TotalSeconds);
        var window = span / _zoom;
        var from = _start.AddSeconds((span - window) * PanSlider.Value);
        var to = from.AddSeconds(window);
        var rows = _filtered.Where(r => r.Timestamp >= from && r.Timestamp <= to).ToArray();
        SamplesGrid.ItemsSource = rows;
        RangeText.Text = $"{FormatTime(from)} — {FormatTime(to)} · {_zoom:0}× · 当前视野 {rows.Length} 条；四个独立单位面板共享时间/缩放/平移，导出为所选完整时段。青=温度，蓝=实际 RPM，灰蓝=目标 RPM（曲线优先），紫=duty %，金=CPU %，橙=CPU 封装功耗 W（非整机）。温度/功耗使用记录主显示源；采样表和导出温度始终为 °C。";
        var width = Chart.ActualWidth - 105;
        var panelHeight = Math.Max(20, (Chart.ActualHeight - 12) / 4);
        var fahrenheit = TemperatureUnitCombo.SelectedIndex == 1;
        var series = new (HistoryChartSeries Kind, System.Windows.Media.Brush Color, bool Enabled)[]
        {
            (HistoryChartSeries.CpuTemperature, ObsidianTheme.BrushOf(this, "ObsidianChartTemperatureBrush"), TempCheck.IsChecked == true),
            (HistoryChartSeries.ActualRpm, ObsidianTheme.BrushOf(this, "ObsidianChartRpmBrush"), RpmCheck.IsChecked == true),
            (HistoryChartSeries.TargetRpm, ObsidianTheme.BrushOf(this, "ObsidianChartTargetRpmBrush"), TargetCheck.IsChecked == true),
            (HistoryChartSeries.Duty, ObsidianTheme.BrushOf(this, "ObsidianChartDutyBrush"), DutyCheck.IsChecked == true),
            (HistoryChartSeries.CpuUsage, ObsidianTheme.BrushOf(this, "ObsidianWarningBrush"), CpuCheck.IsChecked == true),
            (HistoryChartSeries.CpuPower, ObsidianTheme.BrushOf(this, "ObsidianChartPowerBrush"), PowerCheck.IsChecked == true)
        };
        var units = new[] { fahrenheit ? HistoryChartUnit.Fahrenheit : HistoryChartUnit.Celsius, HistoryChartUnit.Rpm, HistoryChartUnit.Percent, HistoryChartUnit.Watts };
        var positiveGaps = rows.Zip(rows.Skip(1), (a, b) => (b.Timestamp - a.Timestamp).TotalSeconds).Where(v => v > 0).OrderBy(v => v).ToArray();
        var gapLimit = positiveGaps.Length == 0 ? 30 : Math.Max(5, positiveGaps[positiveGaps.Length / 2] * 3);
        for (var panel = 0; panel < units.Length; panel++)
        {
            var group = series.Where(s => HistoryChartScale.Unit(s.Kind, fahrenheit) == units[panel]).ToArray();
            var axis = HistoryChartScale.Axis(units[panel], rows.SelectMany(r => group.Where(s => s.Enabled).Select(s => HistoryChartScale.Value(r, s.Kind, fahrenheit))));
            var top = 10 + panel * panelHeight;
            var height = panelHeight - 18;
            var axisColor = group[0].Color;
            for (var tick = 0; tick <= 2; tick++)
            {
                var y = top + tick * height / 2;
                Chart.Children.Add(new Line { X1 = 85, X2 = 85 + width, Y1 = y, Y2 = y, Stroke = ObsidianTheme.BrushOf(this, "ObsidianChartGuideBrush") });
                Label($"{axis.Maximum * (1 - tick / 2d):0.#} {axis.Label}", 3, y - 7, axisColor);
            }
            foreach (var marker in _events.Where(e => e.Timestamp >= from && e.Timestamp <= to && e.Kind is HistoryEventKind.Mode or HistoryEventKind.Recovery or HistoryEventKind.Error)
                .GroupBy(e => (int)((e.Timestamp - from).TotalSeconds / window * width)).Select(g => g.OrderByDescending(e => e.Kind == HistoryEventKind.Error).First()))
            {
                var x = 85 + (marker.Timestamp - from).TotalSeconds / window * width;
                Chart.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + height, Stroke = ObsidianTheme.BrushOf(this, marker.Kind == HistoryEventKind.Error ? "ObsidianDangerBrush" : "ObsidianTextTertiaryBrush"), StrokeDashArray = [2, 4], ToolTip = marker.Message });
            }
            foreach (var item in group.Where(s => s.Enabled)) Draw(item.Kind, item.Color, axis, top, height);
        }
        void Draw(HistoryChartSeries kind, System.Windows.Media.Brush color, HistoryAxis axis, double top, double height)
        {
            var line = NewLine();
            var bucket = new List<HardwareTelemetryRecord>();
            var pixel = -1;
            DateTimeOffset? previous = null;
            foreach (var row in rows)
            {
                var value = HistoryChartScale.Value(row, kind, fahrenheit);
                if (value is null || (previous is { } last && (row.Timestamp - last).TotalSeconds > gapLimit))
                { FlushBucket(); FlushLine(); }
                if (value is null) { previous = null; continue; }
                var nextPixel = (int)((row.Timestamp - from).TotalSeconds / window * width);
                if (nextPixel != pixel) { FlushBucket(); pixel = nextPixel; }
                bucket.Add(row); previous = row.Timestamp;
            }
            FlushBucket(); FlushLine();
            Polyline NewLine() => new() { Stroke = color, StrokeThickness = 1.5, IsHitTestVisible = false };
            void FlushBucket()
            {
                if (bucket.Count == 0) return;
                foreach (var row in new[] { bucket.First(), bucket.MinBy(r => HistoryChartScale.Value(r, kind, fahrenheit))!, bucket.MaxBy(r => HistoryChartScale.Value(r, kind, fahrenheit))!, bucket.Last() }.Distinct().OrderBy(r => r.Timestamp))
                    line.Points.Add(new Point(85 + (row.Timestamp - from).TotalSeconds / window * width,
                        top + height * (1 - axis.Normalize(HistoryChartScale.Value(row, kind, fahrenheit)!.Value))));
                bucket.Clear();
            }
            void FlushLine() { if (line.Points.Count > 0) Chart.Children.Add(line); line = NewLine(); }
        }
    }
    private void Label(string text, double x, double y, System.Windows.Media.Brush? color = null)
    {
        var label = new TextBlock { Text = text, FontSize = 10, Foreground = color ?? ObsidianTheme.BrushOf(this, "ObsidianTextSecondaryBrush") };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); Chart.Children.Add(label);
    }
    private void ExportRecordsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv|JSONL (*.jsonl)|*.jsonl", FileName = "history.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { TelemetryHistory.ExportRecords(dialog.FileName, _filtered, dialog.FilterIndex == 1); StatusText.Text = $"已导出 {_filtered.Count} 条采样。"; }
        catch (Exception ex) { StatusText.Text = "导出失败：" + ex.Message; }
    }
    private void ExportEventsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "events.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { TelemetryHistory.ExportEvents(dialog.FileName, _visibleEvents); StatusText.Text = $"已导出 {_visibleEvents.Count} 条事件。"; }
        catch (Exception ex) { StatusText.Text = "导出失败：" + ex.Message; }
    }
}
