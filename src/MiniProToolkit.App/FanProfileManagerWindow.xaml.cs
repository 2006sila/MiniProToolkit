using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

public partial class FanProfileManagerWindow : Window
{
    private readonly ObservableCollection<FanProfile> _profiles;
    private FanCurveSettings _draftCurve = FanCurveSettings.Default;
    private readonly FanCalibration _calibration;
    private readonly Func<HardwareSnapshot?>? _snapshotSource;
    private int _editing = -1;
    private bool _sync;
    public IReadOnlyList<FanProfile>? Result { get; private set; }
    public string? SelectedName { get; private set; }
    public bool ApplyRequested { get; private set; }

    public FanProfileManagerWindow(IEnumerable<FanProfile> profiles, string? selectedName,
        FanCalibration? calibration = null, Func<HardwareSnapshot?>? snapshotSource = null)
    {
        _calibration = calibration ?? FanCalibration.Default;
        _snapshotSource = snapshotSource;
        InitializeComponent();
        // 纯展示：与其他功能窗共用同一份深色原生标题栏实现（DWM 不可用时静默跳过）。
        ObsidianWindowChrome.Apply(this);
        _profiles = new(profiles);
        ProfilesList.ItemsSource = _profiles;
        ProfilesList.SelectedIndex = Math.Max(0, _profiles.ToList().FindIndex(p => p.Name == selectedName));
    }

    private bool Commit()
    {
        if (_editing < 0 || _editing >= _profiles.Count) return true;
        try
        {
            if (!int.TryParse(RpmBox.Text, out var rpm)) throw new ArgumentException("RPM 必须为整数。");
            var profile = new FanProfile(NameBox.Text.Trim(), (PowerMode)PowerCombo.SelectedIndex,
                _draftCurve, (ProfileFanMode)FanCombo.SelectedIndex, rpm);
            profile.Validate();
            if (_profiles.Where((_, i) => i != _editing).Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("名称重复，请使用不同的方案名称。");
            _sync = true;
            _profiles[_editing] = profile;
            ProfilesList.SelectedIndex = _editing;
            FeedbackText.Text = $"已更新草稿：{profile.Name}。关闭前请保存全部。";
            return true;
        }
        catch (Exception ex) { FeedbackText.Text = ex.Message; return false; }
        finally { _sync = false; }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || _profiles is null) return;
        var next = ProfilesList.SelectedIndex;
        if (!Commit())
        {
            _sync = true;
            ProfilesList.SelectedIndex = _editing;
            _sync = false;
            return;
        }
        Select(next);
    }

    private void Select(int index)
    {
        _editing = index;
        _sync = true;
        ProfilesList.SelectedIndex = index;
        _sync = false;
        if (index < 0 || index >= _profiles.Count) return;
        var p = _profiles[index];
        NameBox.Text = p.Name;
        PowerCombo.SelectedIndex = (int)p.PowerMode;
        FanCombo.SelectedIndex = (int)p.FanMode;
        RpmBox.Text = p.FixedRpm.ToString();
        _draftCurve = p.FanCurve;
        UpdateCurveText();
    }

    // Read-only summary so a profile's optional safety settings stay visible in the manager;
    // the curve editor is the only place that writes them.
    private void UpdateCurveText()
    {
        var safety = new List<string>();
        if (_draftCurve.RpmRateLimit is { } limit)
            safety.Add($"RPM/s 限速 {limit.MaximumRiseRpmPerSecond}↑/{limit.MaximumFallRpmPerSecond}↓");
        if (_draftCurve.WarningTier is { } warning)
            safety.Add($"预警 {warning.TemperatureC:0.##}°C/{warning.DutyPercent}%");
        if (_draftCurve.PreEmergencyTier is { } pre)
            safety.Add($"前级 {pre.TemperatureC:0.##}°C/{pre.DutyPercent}%");
        CurveText.Text = $"曲线：{_draftCurve.Points.Count} 个点 · 最低 {_draftCurve.MinimumDutyPercent}% · {_draftCurve.EmergencyTemperatureC}°C 紧急满速" +
            (safety.Count == 0 ? " · 可选安全设置关闭" : " · " + string.Join(" · ", safety));
    }
    private void CommitClick(object sender, RoutedEventArgs e) => Commit();
    private string UniqueName(string basis)
    {
        basis = basis.Length > 52 ? basis[..52] : basis;
        var name = basis;
        for (var i = 2; _profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{basis} {i}";
        return name;
    }
    private void Add(FanProfile profile)
    {
        if (_profiles.Count >= 100) { FeedbackText.Text = "最多支持 100 个方案。"; return; }
        _sync = true;
        _profiles.Add(profile);
        _sync = false;
        Select(_profiles.Count - 1);
    }
    private void NewClick(object sender, RoutedEventArgs e)
    {
        if (Commit()) Add(new(UniqueName("新方案"), PowerMode.Balance, FanCurveSettings.Default));
    }
    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (Commit() && _editing >= 0) Add(_profiles[_editing] with { Name = UniqueName(_profiles[_editing].Name + " 副本") });
    }
    private void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count <= 1) { FeedbackText.Text = "至少保留一个方案。"; return; }
        if (_editing < 0) return;
        var index = _editing;
        _sync = true;
        _editing = -1;
        _profiles.RemoveAt(index);
        _sync = false;
        Select(Math.Min(index, _profiles.Count - 1));
    }
    private void CurveClick(object sender, RoutedEventArgs e)
    {
        var editor = new FanCurveEditorWindow(_draftCurve, _calibration, _snapshotSource) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } curve) { _draftCurve = curve; UpdateCurveText(); }
    }
    private void ResetClick(object sender, RoutedEventArgs e)
    {
        _sync = true;
        _editing = -1;
        _profiles.Clear();
        foreach (var p in FanProfile.Defaults) _profiles.Add(p);
        _sync = false;
        Select(0);
        FeedbackText.Text = "草稿已恢复内置方案，保存全部后生效；取消可撤销。";
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "方案 JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = new FanProfileStore(dialog.FileName).Load();
            if (_profiles.Count + imported.Count > 100) throw new ArgumentException("合并后超过 100 个方案。");
            foreach (var p in imported) Add(p with { Name = UniqueName(p.Name) });
            FeedbackText.Text = $"已导入 {imported.Count} 个方案到草稿；重名已自动编号。";
        }
        catch (Exception ex) { FeedbackText.Text = $"导入失败：{ex.Message}"; }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "方案 JSON (*.json)|*.json", FileName = "minipro-profiles.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { new FanProfileStore(dialog.FileName).Save(_profiles); FeedbackText.Text = "全部方案已导出。"; }
        catch (Exception ex) { FeedbackText.Text = $"导出失败：{ex.Message}"; }
    }
    private void Finish(bool apply)
    {
        if (!Commit()) return;
        try { FanProfileStore.ValidateCollection(_profiles); }
        catch (Exception ex) { FeedbackText.Text = ex.Message; return; }
        Result = _profiles.ToArray();
        SelectedName = _profiles[_editing].Name;
        ApplyRequested = apply;
        DialogResult = true;
    }
    private void SaveClick(object sender, RoutedEventArgs e) => Finish(false);
    private void ApplyClick(object sender, RoutedEventArgs e) => Finish(true);
}
