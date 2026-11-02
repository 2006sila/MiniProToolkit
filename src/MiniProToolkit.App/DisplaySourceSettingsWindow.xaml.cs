using System.Windows;
using System.Windows.Controls;
using MiniProToolkit.Core;

namespace MiniProToolkit.App;

public partial class DisplaySourceSettingsWindow : Window
{
    public CpuDisplaySourceSettings? Result { get; private set; }

    public DisplaySourceSettingsWindow(CpuDisplaySourceSettings settings, string? lhmStatus = null)
    {
        InitializeComponent();
        settings.Validate();
        SourceCombo.SelectedIndex = settings.Source == CpuDisplaySource.LibreHardwareMonitor ? 1 : 0;
        LhmStatusText.Text = string.IsNullOrWhiteSpace(lhmStatus)
            ? "LHM 当前状态：尚未采样或未启用。"
            : $"LHM 当前状态：{lhmStatus}";
        UpdateDetail();
    }

    private CpuDisplaySource SelectedSource => SourceCombo.SelectedIndex == 1
        ? CpuDisplaySource.LibreHardwareMonitor
        : CpuDisplaySource.EcEnergyMeter;

    private void SourceChanged(object sender, SelectionChangedEventArgs e) => UpdateDetail();

    private void UpdateDetail()
    {
        if (DetailText is null)
            return;
        DetailText.Text = SelectedSource == CpuDisplaySource.LibreHardwareMonitor
            ? "选择 LHM 后，主窗口、任务栏、实时趋势和历史新采样使用 LHM 温度/功耗；任一项不可用时该项单独回退到 EC 温度或 Energy Meter 功耗，并显示提示。风扇控制与 90°C 保护仍只读 EC 温度。"
            : "当前为兼容默认：显示 EC 温度与 Windows Energy Meter CPU 封装功耗，LHM 仍作为对照数据记录。";
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new CpuDisplaySourceSettings(SelectedSource);
            settings.Validate();
            Result = settings;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                "主显示源设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
