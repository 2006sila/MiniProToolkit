using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MiniProToolkit.Core;
// The app project enables WinForms implicit usings as well, so the WPF control types used by the
// hotkey rows have to be aliased explicitly.
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;

namespace MiniProToolkit.App;

public partial class TaskbarSettingsWindow : Window
{
    private readonly ObservableCollection<EditableItem> _items;
    private readonly List<HotkeyRow> _hotkeyRows = [];
    public TaskbarDisplaySettings? Result { get; private set; }

    /// <summary>
    /// Rebound global hotkeys, or <c>null</c> when the user cancelled. A separate result keeps the two
    /// persisted files (taskbar.json / hotkeys.json) independent: the caller saves and applies each
    /// one on its own, so a failure in one never silently drops the other.
    /// </summary>
    public HotkeySettings? HotkeyResult { get; private set; }

    public TaskbarSettingsWindow(TaskbarDisplaySettings settings) : this(settings, HotkeySettings.Default)
    {
    }

    public TaskbarSettingsWindow(TaskbarDisplaySettings settings, HotkeySettings hotkeys)
    {
        InitializeComponent();
        settings.Validate();
        hotkeys.Validate();
        _items = new ObservableCollection<EditableItem>(
            Enum.GetValues<TaskbarDisplayItem>().Select(item => new EditableItem(
                item,
                LabelFor(item),
                settings.Items.Contains(item))));
        // Keep configured order first, then append currently hidden items.
        var ordered = settings.Items
            .Concat(Enum.GetValues<TaskbarDisplayItem>().Except(settings.Items))
            .Select(item => _items.First(entry => entry.Item == item))
            .ToArray();
        _items.Clear();
        foreach (var item in ordered)
            _items.Add(item);
        ItemsList.ItemsSource = _items;
        CompactCheck.IsChecked = settings.Compact;
        VerticalCheck.IsChecked = settings.Vertical;
        FontFamilyText.Text = settings.FontFamily;
        FontSizeText.Text = settings.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TextColorText.Text = settings.TextColor;
        BackgroundColorText.Text = settings.BackgroundColor;
        RefreshCombo.SelectedIndex = Math.Max(0, settings.RefreshIntervalSeconds switch
        {
            1 => 0,
            2 => 1,
            3 => 2,
            5 => 3,
            10 => 4,
            _ => 0
        });
        BuildHotkeyRows(hotkeys);
    }

    /// <summary>
    /// Builds one editor row per hotkey action. The key box is filled from
    /// <see cref="HotkeyKeyCatalog.Keys"/>, so the UI cannot offer (or produce) a key outside the
    /// whitelist that the Core validation and the registration path accept.
    /// </summary>
    private void BuildHotkeyRows(HotkeySettings settings)
    {
        _hotkeyRows.Clear();
        HotkeyPanel.Children.Clear();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var row = new HotkeyRow(action);
            row.Apply(settings.GestureFor(action));
            HotkeyPanel.Children.Add(row.Build());
            _hotkeyRows.Add(row);
        }
    }

    private void ResetHotkeysClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _hotkeyRows)
            row.Apply(HotkeySettings.Default.GestureFor(row.Action));
        HotkeyStatusText.Text = "已恢复默认组合，点击“保存”后生效。";
    }

    private HotkeySettings ReadHotkeySettings() => new(
        _hotkeyRows.Select(row => new HotkeyBinding(row.Action, row.Read())).ToArray());

    private void MoveUpClick(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not EditableItem item)
            return;
        var index = _items.IndexOf(item);
        if (index > 0)
            _items.Move(index, index - 1);
        ItemsList.SelectedItem = item;
    }

    private void MoveDownClick(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not EditableItem item)
            return;
        var index = _items.IndexOf(item);
        if (index >= 0 && index < _items.Count - 1)
            _items.Move(index, index + 1);
        ItemsList.SelectedItem = item;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        TaskbarDisplaySettings settings;
        HotkeySettings hotkeys;
        try
        {
            var selected = _items.Where(item => item.Enabled).Select(item => item.Item).ToArray();
            var refresh = int.Parse(((System.Windows.Controls.ComboBoxItem)RefreshCombo.SelectedItem).Content.ToString()!);
            settings = new TaskbarDisplaySettings(
                selected,
                CompactCheck.IsChecked == true,
                VerticalCheck.IsChecked == true,
                refresh,
                FontFamilyText.Text.Trim(),
                double.Parse(FontSizeText.Text, System.Globalization.CultureInfo.InvariantCulture),
                TextColorText.Text.Trim(),
                BackgroundColorText.Text.Trim());
            settings.Validate();
            // The whitelist/conflict rules live in Core; the dialog only surfaces the message.
            hotkeys = ReadHotkeySettings();
            hotkeys.Validate();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                "设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = settings;
        HotkeyResult = hotkeys;
        DialogResult = true;
    }

    private static string LabelFor(TaskbarDisplayItem item) => item switch
    {
        TaskbarDisplayItem.CpuTemperature => "CPU 温度",
        TaskbarDisplayItem.FanRpm => "实际风扇 RPM",
        TaskbarDisplayItem.CpuPackagePower => "CPU 功耗（估算）",
        TaskbarDisplayItem.TargetRpm => "目标 RPM",
        TaskbarDisplayItem.TargetDuty => "当前 duty",
        TaskbarDisplayItem.RpmFeedback => "RPM 闭环状态",
        TaskbarDisplayItem.PowerMode => "性能模式",
        TaskbarDisplayItem.CpuUsage => "CPU 占用",
        TaskbarDisplayItem.MemoryUsage => "内存占用",
        TaskbarDisplayItem.Guardian => "Guardian 状态",
        TaskbarDisplayItem.WmiStatus => "WMI 状态",
        TaskbarDisplayItem.CpuFrequency => "CPU 频率",
        // Read-only LHM GPU temperature; opt-in only, never part of an existing settings file.
        TaskbarDisplayItem.GpuTemperature => "GPU 温度（LHM 只读）",
        _ => item.ToString()
    };

    private sealed class EditableItem(TaskbarDisplayItem item, string label, bool enabled)
    {
        public TaskbarDisplayItem Item { get; } = item;
        public string Label { get; } = label;
        public bool Enabled { get; set; } = enabled;
    }

    /// <summary>
    /// One editable hotkey row: four modifier checkboxes plus a whitelisted key box. It only reads and
    /// writes the gesture; every rule is enforced by <see cref="HotkeySettings.Validate"/>.
    /// </summary>
    private sealed class HotkeyRow(HotkeyAction action)
    {
        private readonly CheckBox _control = new() { Content = "Ctrl", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _alt = new() { Content = "Alt", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _shift = new() { Content = "Shift", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _win = new() { Content = "Win", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox _key = new() { Width = 80, ItemsSource = HotkeyKeyCatalog.Keys, VerticalAlignment = VerticalAlignment.Center };

        public HotkeyAction Action { get; } = action;

        public FrameworkElement Build()
        {
            var actionLabel = HotkeySettings.LabelFor(Action);
            const string gestureHelpText = "全局快捷键组合必须包含 Ctrl、Alt 或 Win；Shift 不能单独使用。主键仅允许 A-Z、0-9、F1-F12，同一组合不能重复；保存后生效。";
            System.Windows.Automation.AutomationProperties.SetName(_control, $"{actionLabel} 快捷键 Ctrl 修饰键");
            System.Windows.Automation.AutomationProperties.SetName(_alt, $"{actionLabel} 快捷键 Alt 修饰键");
            System.Windows.Automation.AutomationProperties.SetName(_shift, $"{actionLabel} 快捷键 Shift 修饰键");
            System.Windows.Automation.AutomationProperties.SetName(_win, $"{actionLabel} 快捷键 Win 修饰键");
            System.Windows.Automation.AutomationProperties.SetName(_key, $"{actionLabel} 快捷键主键");
            System.Windows.Automation.AutomationProperties.SetHelpText(_control, gestureHelpText);
            System.Windows.Automation.AutomationProperties.SetHelpText(_alt, gestureHelpText);
            System.Windows.Automation.AutomationProperties.SetHelpText(_shift, gestureHelpText);
            System.Windows.Automation.AutomationProperties.SetHelpText(_win, gestureHelpText);
            System.Windows.Automation.AutomationProperties.SetHelpText(_key, gestureHelpText);
            var modifiers = new StackPanel { Orientation = Orientation.Horizontal };
            modifiers.Children.Add(_control);
            modifiers.Children.Add(_alt);
            modifiers.Children.Add(_shift);
            modifiers.Children.Add(_win);
            Grid.SetColumn(modifiers, 1);
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = actionLabel, VerticalAlignment = VerticalAlignment.Center });
            grid.Children.Add(modifiers);
            Grid.SetColumn(_key, 2);
            grid.Children.Add(_key);
            return grid;
        }

        public void Apply(HotkeyGesture gesture)
        {
            _control.IsChecked = gesture.Modifiers.HasFlag(HotkeyModifiers.Control);
            _alt.IsChecked = gesture.Modifiers.HasFlag(HotkeyModifiers.Alt);
            _shift.IsChecked = gesture.Modifiers.HasFlag(HotkeyModifiers.Shift);
            _win.IsChecked = gesture.Modifiers.HasFlag(HotkeyModifiers.Windows);
            _key.SelectedItem = HotkeyKeyCatalog.Normalize(gesture.Key) ?? HotkeyKeyCatalog.Keys[0];
        }

        public HotkeyGesture Read()
        {
            var modifiers = HotkeyModifiers.None;
            if (_control.IsChecked == true) modifiers |= HotkeyModifiers.Control;
            if (_alt.IsChecked == true) modifiers |= HotkeyModifiers.Alt;
            if (_shift.IsChecked == true) modifiers |= HotkeyModifiers.Shift;
            if (_win.IsChecked == true) modifiers |= HotkeyModifiers.Windows;
            return new HotkeyGesture(modifiers, _key.SelectedItem as string ?? string.Empty);
        }
    }
}
