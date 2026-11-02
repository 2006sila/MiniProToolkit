using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

public partial class FanCalibrationEditorWindow : Window
{
    private readonly ObservableCollection<EditablePoint> _points = [];
    public FanCalibration? Result { get; private set; }

    private readonly Func<CancellationToken, IProgress<FanCalibrationSamplingProgress>, Task<FanCalibration>>? _sampleAsync;
    private CancellationTokenSource? _samplingCts;
    private Task? _samplingTask;
    private bool _closeAfterSampling;

    public FanCalibrationEditorWindow(FanCalibration calibration,
        Func<CancellationToken, IProgress<FanCalibrationSamplingProgress>, Task<FanCalibration>>? sampleAsync = null)
    {
        InitializeComponent();
        _sampleAsync = sampleAsync;
        StartSamplingButton.IsEnabled = sampleAsync is not null;
        SamplingStatusText.Text = sampleAsync is null ? "当前模式不提供硬件实测（演示/只读）。" : "尚未开始实测。";
        PointsGrid.ItemsSource = _points;
        ReplacePoints(calibration);
    }

    private async void StartSamplingClick(object sender, RoutedEventArgs e)
    {
        if (_samplingCts is not null || _sampleAsync is null) return;
        // Commit the existing draft before disabling editing; a failed/cancelled run never replaces it.
        if (!PointsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !PointsGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            SamplingStatusText.Text = "请先结束当前单元格编辑。";
            return;
        }
        _samplingCts = new CancellationTokenSource();
        EditButtons.IsEnabled = PointsGrid.IsEnabled = StartSamplingButton.IsEnabled = false;
        CancelSamplingButton.IsEnabled = true;
        var progress = new Progress<FanCalibrationSamplingProgress>(value =>
        {
            if (_samplingCts is not null)
                SamplingStatusText.Text = $"{value.DutyPercent}% · 已完成 {value.CompletedPoints}/7 · 稳定样本 {value.StableSamples}/5 · {value.Message}";
        });
        _samplingTask = SampleIntoDraftAsync(_samplingCts.Token, progress);
        try { await _samplingTask; }
        finally
        {
            _samplingTask = null;
            _samplingCts.Dispose();
            _samplingCts = null;
            EditButtons.IsEnabled = PointsGrid.IsEnabled = true;
            StartSamplingButton.IsEnabled = _sampleAsync is not null;
            CancelSamplingButton.IsEnabled = false;
            if (_closeAfterSampling) DialogResult = false;
        }
    }

    private async Task SampleIntoDraftAsync(CancellationToken token, IProgress<FanCalibrationSamplingProgress> progress)
    {
        try
        {
            var measured = await _sampleAsync!(token, progress);
            token.ThrowIfCancellationRequested();
            measured.ValidateForFixedRpm();
            ReplacePoints(measured); // Callback guarantees EC automatic restoration before returning.
            SamplingStatusText.Text = "实测完成且 EC 自动已恢复；已替换草稿，点击保存才会应用。";
        }
        catch (OperationCanceledException)
        {
            SamplingStatusText.Text = "实测已取消；原草稿保留。";
        }
        catch (Exception ex)
        {
            SamplingStatusText.Text = "实测未应用，原草稿保留：" + ex.GetBaseException().Message;
        }
    }

    private void CancelSamplingClick(object sender, RoutedEventArgs e)
    {
        SamplingStatusText.Text = "正在取消；等待在途操作完成并恢复 EC 自动…";
        _samplingCts?.Cancel();
    }

    private void EditorClosing(object? sender, CancelEventArgs e)
    {
        if (_samplingCts is null) return;
        e.Cancel = true;
        _closeAfterSampling = true;
        SamplingStatusText.Text = "正在取消并等待恢复 EC 自动；窗口尚未关闭…";
        _samplingCts.Cancel();
    }

    private void ReplacePoints(FanCalibration calibration)
    {
        _points.Clear();
        foreach (var point in calibration.Points)
            _points.Add(CreatePoint(point.DutyPercent, point.Rpm));
        UpdatePreview();
    }

    private EditablePoint CreatePoint(int duty, int rpm)
    {
        var point = new EditablePoint(duty, rpm);
        point.PropertyChanged += (_, _) => UpdatePreview();
        return point;
    }

    private void AddPointClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var calibration = BuildCalibration();
            if (_points.Count >= 64)
                throw new ArgumentException("最多支持 64 个标定点。");
            var gap = Enumerable.Range(1, calibration.Points.Count - 1)
                .OrderByDescending(index => calibration.Points[index].DutyPercent - calibration.Points[index - 1].DutyPercent)
                .First();
            var lower = calibration.Points[gap - 1];
            var upper = calibration.Points[gap];
            if (upper.DutyPercent - lower.DutyPercent < 2)
                throw new ArgumentException("当前标定点之间没有可插入的整数 duty。");
            var duty = (lower.DutyPercent + upper.DutyPercent) / 2;
            _points.Insert(gap, CreatePoint(duty, calibration.RpmForDuty(duty)));
            PointsGrid.SelectedIndex = gap;
            UpdatePreview();
        }
        catch (Exception ex) when (IsInputError(ex))
        {
            ShowError("添加标定点失败", ex);
        }
    }

    private void RemovePointClick(object sender, RoutedEventArgs e)
    {
        if (PointsGrid.SelectedItem is EditablePoint point && _points.Count > 2)
        {
            _points.Remove(point);
            UpdatePreview();
        }
    }

    private void ResetClick(object sender, RoutedEventArgs e) => ReplacePoints(FanCalibration.Default);

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            Title = "导入风扇标定",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ReplacePoints(new FanCalibrationStore(dialog.FileName).Load());
        }
        catch (Exception ex) when (IsInputError(ex) || ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowError("导入失败", ex);
        }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var calibration = BuildCalibration();
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON 文件 (*.json)|*.json",
                FileName = "fan-calibration.json",
                Title = "导出风扇标定"
            };
            if (dialog.ShowDialog(this) == true)
                new FanCalibrationStore(dialog.FileName).Save(calibration);
        }
        catch (Exception ex) when (IsInputError(ex) || ex is IOException or UnauthorizedAccessException)
        {
            ShowError("导出失败", ex);
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (_samplingCts is not null)
        {
            _closeAfterSampling = true;
            _samplingCts.Cancel();
            return;
        }
        DialogResult = false;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_samplingCts is not null) return;
        try
        {
            Result = BuildCalibration();
            DialogResult = true;
        }
        catch (Exception ex) when (IsInputError(ex))
        {
            ShowError("标定校验失败", ex);
        }
    }

    private FanCalibration BuildCalibration()
    {
        if (!PointsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !PointsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new FormatException("请先修正正在编辑的单元格。");
        return ParseCalibration();
    }

    private FanCalibration ParseCalibration()
    {
        var calibration = new FanCalibration(_points.Select(point => new FanCalibrationPoint(
            ParseInt(point.DutyPercent, "Duty"), ParseInt(point.Rpm, "RPM"))));
        calibration.ValidateForFixedRpm();
        return calibration;
    }

    private static int ParseInt(string text, string name) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw new FormatException($"{name}必须是整数。");

    private void PreviewSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (RangeText is null || PreviewCanvas is null) return;
        PreviewCanvas.Children.Clear();
        try
        {
            var calibration = ParseCalibration();
            RangeText.Text = $"固定 RPM 估计范围：{calibration.RpmForDuty(40)}–{calibration.RpmForDuty(100)} RPM（40%–100% duty）";
            RangeText.Foreground = System.Windows.Media.Brushes.DarkGreen;
            var width = PreviewCanvas.ActualWidth;
            var height = PreviewCanvas.ActualHeight;
            if (width <= 1 || height <= 1) return;
            var maximum = Math.Max(1, calibration.Points[^1].Rpm);
            var line = new System.Windows.Shapes.Polyline
            {
                Stroke = System.Windows.Media.Brushes.SteelBlue,
                StrokeThickness = 2
            };
            foreach (var point in calibration.Points)
                line.Points.Add(new System.Windows.Point(
                    6 + point.DutyPercent / 100.0 * (width - 12),
                    height - 6 - point.Rpm / (double)maximum * (height - 12)));
            PreviewCanvas.Children.Add(line);
        }
        catch (Exception ex) when (IsInputError(ex))
        {
            RangeText.Text = "标定无效：" + ex.Message;
            RangeText.Foreground = System.Windows.Media.Brushes.DarkOrange;
        }
    }

    private static bool IsInputError(Exception ex) => ex is FormatException or ArgumentException or OverflowException;
    private void ShowError(string title, Exception ex) => System.Windows.MessageBox.Show(
        this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public sealed class EditablePoint : INotifyPropertyChanged
    {
        private string _dutyPercent;
        private string _rpm;
        public EditablePoint(int dutyPercent, int rpm)
        {
            _dutyPercent = dutyPercent.ToString(CultureInfo.InvariantCulture);
            _rpm = rpm.ToString(CultureInfo.InvariantCulture);
        }
        public string DutyPercent
        {
            get => _dutyPercent;
            set { _dutyPercent = value; PropertyChanged?.Invoke(this, new(nameof(DutyPercent))); }
        }
        public string Rpm
        {
            get => _rpm;
            set { _rpm = value; PropertyChanged?.Invoke(this, new(nameof(Rpm))); }
        }
        public string Description => "用于 duty↔RPM 插值";
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
