using System.Windows;
using MiniProToolkit.Core;
using Media = System.Windows.Media;

namespace MiniProToolkit.App;

/// <summary>
/// OSD appearance editor: foreground/background color, background opacity, font size, font family and
/// screen position. Colors and the font family are free-form text fields, validated on every keystroke:
/// a color must be a strict <c>#RRGGBB</c>/<c>#AARRGGBB</c> string whose input length is capped at
/// <see cref="OsdColorRules.MaxInputLength"/>, a font family must be a plain family name (no path, URI
/// or separator) of at most <see cref="OsdFontFamilyRules.MaxLength"/> characters. An invalid field
/// disables 保存 and shows an inline error; an empty field means "follow the theme". The preset
/// dropdowns stay as quick picks and simply fill the text field. Presentation only:
/// <see cref="Result"/> is persisted by <c>MainWindow.OsdSettingsClick</c>; this window never touches
/// hardware, the network or the control loop.
/// </summary>
public partial class OsdSettingsWindow : Window
{
    private static readonly Media.Brush ErrorBrush = Freeze(0xC6, 0x28, 0x28);
    private static readonly Media.Brush WarningBrush = Freeze(0xB2, 0x6A, 0x00);
    private static readonly Media.Brush MutedBrush = Freeze(0x66, 0x66, 0x66);

    private bool _syncing;
    private readonly Dictionary<string, OsdEventTemplate> _templates = new(StringComparer.Ordinal);
    private OsdEventDefinition? _selectedEvent;
    private bool _loadingTemplate;

    public OsdAppearance? Result { get; private set; }
    public IReadOnlyDictionary<string, OsdEventTemplate>? TemplateResult { get; private set; }

    public OsdSettingsWindow(OsdAppearance? appearance,
        IReadOnlyDictionary<string, OsdEventTemplate>? templates = null)
    {
        InitializeComponent();
        LoadAppearance((appearance ?? OsdAppearance.Default).Sanitize());
        foreach (var entry in OsdEventTemplates.SanitizeOverrides(templates) ?? [])
            _templates[entry.Key] = entry.Value;
        TemplateIconCombo.ItemsSource = new[] { new IconEntry(null, "跟随事件") }
            .Concat(Enum.GetValues<OsdIcon>().Select(icon => new IconEntry(icon, icon.ToString()))).ToArray();
        TemplateDurationCombo.ItemsSource = new[] { new DurationEntry(null) }
            .Concat(Enumerable.Range(1, 30).Select(seconds => new DurationEntry(seconds))).ToList();
        EventCombo.ItemsSource = OsdEventTemplates.Events;
        EventCombo.SelectedIndex = 0;
    }

    private void StoreCurrentTemplate()
    {
        if (_selectedEvent is null || _loadingTemplate) return;
        var key = _selectedEvent.Key;
        if (TemplateOverrideCheck.IsChecked != true)
        {
            _templates.Remove(key);
            return;
        }
        _templates[key] = OsdEventTemplates.Sanitize(key, new OsdEventTemplate(
            TemplateTitleText.Text, TemplateBodyCheck.IsChecked == true, TemplateAdditionalText.Text,
            (TemplateIconCombo.SelectedItem as IconEntry)?.Icon,
            (TemplateDurationCombo.SelectedItem as DurationEntry)?.Seconds));
    }

    private void EventChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        StoreCurrentTemplate();
        _selectedEvent = EventCombo.SelectedItem as OsdEventDefinition;
        LoadCurrentTemplate();
    }

    private void LoadCurrentTemplate()
    {
        if (_selectedEvent is null) return;
        _loadingTemplate = true;
        try
        {
            var hasOverride = _templates.TryGetValue(_selectedEvent.Key, out var template);
            template ??= new OsdEventTemplate();
            TemplateOverrideCheck.IsChecked = hasOverride;
            TemplateTitleText.Text = template.Title ?? string.Empty;
            TemplateBodyCheck.IsChecked = template.ShowOriginalBody;
            TemplateAdditionalText.Text = template.AdditionalText ?? string.Empty;
            TemplateIconCombo.SelectedItem = ((IEnumerable<IconEntry>)TemplateIconCombo.ItemsSource)
                .First(item => item.Icon == template.Icon);
            var durations = (List<DurationEntry>)TemplateDurationCombo.ItemsSource;
            if (durations.All(item => item.Seconds != template.DurationSeconds))
            {
                durations = [.. durations, new DurationEntry(template.DurationSeconds)];
                TemplateDurationCombo.ItemsSource = durations;
            }
            TemplateDurationCombo.SelectedItem = durations.First(item => item.Seconds == template.DurationSeconds);
            TemplateSafetyText.Text = _selectedEvent.IsSafetyEvent
                ? "安全事件：保留原始标题、全部正文和图标；严重度不可更改。" : "普通事件：可定制标题、正文显示、静态说明和图标。";
            UpdateTemplateEnabled();
        }
        finally { _loadingTemplate = false; }
    }

    private void TemplateOverrideChanged(object sender, RoutedEventArgs e) => UpdateTemplateEnabled();

    private void UpdateTemplateEnabled()
    {
        TemplateFields.IsEnabled = TemplateOverrideCheck.IsChecked == true;
        var editable = _selectedEvent?.IsSafetyEvent != true;
        TemplateTitleText.IsEnabled = editable;
        TemplateBodyCheck.IsEnabled = editable;
        TemplateIconCombo.IsEnabled = editable;
    }

    private void RestoreEventDefaultsClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEvent is null) return;
        _templates.Remove(_selectedEvent.Key);
        LoadCurrentTemplate();
    }

    /// <summary>Loads a (already sanitized) appearance into the controls.</summary>
    private void LoadAppearance(OsdAppearance appearance)
    {
        _syncing = true;
        try
        {
            LoadColors(ForegroundCombo, ForegroundTextBox, OsdColorCatalog.Foregrounds, appearance.ForegroundColor);
            LoadColors(BackgroundCombo, BackgroundTextBox, OsdColorCatalog.Backgrounds, appearance.BackgroundColor);
            FontFamilyTextBox.Text = appearance.FontFamily ?? string.Empty;
            LoadFontSizes(appearance.FontSize);
            LoadPlacements(appearance.Placement);
            OpacitySlider.Value = appearance.BackgroundOpacity;
            UpdateOpacityText();
        }
        finally
        {
            _syncing = false;
        }
        UpdateValidation();
    }

    private void LoadColors(System.Windows.Controls.ComboBox combo, System.Windows.Controls.TextBox textBox,
        IReadOnlyList<OsdColorOption> catalog, string? current)
    {
        List<ColorEntry> items = [.. catalog.Select(option => new ColorEntry(option.Color, option.DisplayName))];
        if (current is not null && items.All(item => !SameColor(item.Color, current)))
            items.Add(new ColorEntry(current, $"自定义 {current}"));
        combo.ItemsSource = items;
        textBox.Text = current ?? string.Empty;
        SelectMatchingColor(combo, current);
    }

    private void LoadFontSizes(double? current)
    {
        List<FontEntry> items = [new(null, $"跟随主题（{OsdTheme.Default.TitleFontSize:0.#}）")];
        items.AddRange(OsdFontSizeCatalog.Options.Select(size => new FontEntry(size, $"{size:0.#}")));
        if (current is { } value && items.All(item => item.Size != value))
            items.Add(new FontEntry(value, $"自定义 {value:0.#}"));
        FontSizeCombo.ItemsSource = items;
        FontSizeCombo.SelectedItem = items.FirstOrDefault(item => item.Size == current) ?? items[0];
    }

    private void LoadPlacements(OsdPlacement current)
    {
        List<PlacementEntry> items = [.. OsdPlacementCatalog.Placements.Select(
            placement => new PlacementEntry(placement, OsdPlacementCatalog.DisplayName(placement)))];
        PlacementCombo.ItemsSource = items;
        PlacementCombo.SelectedItem = items.First(item => item.Placement == OsdPlacementCatalog.Normalize(current));
    }

    /// <summary>Rebuilds the appearance from the controls. Text that is not empty is trimmed; invalid
    /// text is kept as typed so <see cref="UpdateValidation"/> can report it (保存 stays disabled).
    /// Tolerates controls that are still being created (the slider raises its event during XAML parse).</summary>
    private OsdAppearance BuildAppearance() => new(
        NullIfEmpty(BackgroundTextBox?.Text),
        NullIfEmpty(ForegroundTextBox?.Text),
        (FontSizeCombo?.SelectedItem as FontEntry)?.Size,
        (PlacementCombo?.SelectedItem as PlacementEntry)?.Placement ?? OsdPlacementCatalog.DefaultPlacement,
        Math.Round(OpacitySlider?.Value ?? OsdAppearance.Default.BackgroundOpacity, 2),
        NullIfEmpty(FontFamilyTextBox?.Text));

    private void ColorPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;
        _syncing = true;
        try
        {
            if (ReferenceEquals(sender, ForegroundCombo) && ForegroundCombo.SelectedItem is ColorEntry foreground)
                ForegroundTextBox.Text = foreground.Color ?? string.Empty;
            else if (ReferenceEquals(sender, BackgroundCombo) && BackgroundCombo.SelectedItem is ColorEntry background)
                BackgroundTextBox.Text = background.Color ?? string.Empty;
        }
        finally
        {
            _syncing = false;
        }
        UpdateValidation();
    }

    private void ColorTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing)
            return;
        _syncing = true;
        try
        {
            // Keep the preset dropdown in sync with what was typed (no match = no selection).
            if (ReferenceEquals(sender, ForegroundTextBox))
                SelectMatchingColor(ForegroundCombo, ForegroundTextBox.Text);
            else if (ReferenceEquals(sender, BackgroundTextBox))
                SelectMatchingColor(BackgroundCombo, BackgroundTextBox.Text);
        }
        finally
        {
            _syncing = false;
        }
        UpdateValidation();
    }

    private static void SelectMatchingColor(System.Windows.Controls.ComboBox combo, string? current)
    {
        if (combo.ItemsSource is not IEnumerable<ColorEntry> items)
            return;
        var normalized = OsdColorRules.NormalizeColor(current);
        combo.SelectedItem = items.FirstOrDefault(item =>
            normalized is null ? item.Color is null : SameColor(item.Color, normalized));
    }

    private void SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateSummary();

    private void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityText();
        UpdateSummary();
    }

    private void UpdateOpacityText()
    {
        if (OpacityText is null || OpacitySlider is null)
            return;
        OpacityText.Text = $"{Math.Round(OpacitySlider.Value, 2):P0}";
    }

    /// <summary>Instant per-field validation: inline errors, a hint for an unknown font and the
    /// 保存 button state. Runs on load, on every keystroke and on every preset change.</summary>
    private void UpdateValidation()
    {
        // The dialog is still being built when the slider raises its first event; nothing to validate yet.
        if (ForegroundTextBox is null || BackgroundTextBox is null || FontFamilyTextBox is null)
            return;
        var foregroundError = ColorError(ForegroundTextBox.Text, "前景色");
        var backgroundError = ColorError(BackgroundTextBox.Text, "背景色");
        var fontFamilyError = FontFamilyError(out var fontFamilyHint, out var fontFamilyHintIsWarning);

        if (ForegroundErrorText is not null)
            ForegroundErrorText.Text = foregroundError ?? string.Empty;
        if (BackgroundErrorText is not null)
            BackgroundErrorText.Text = backgroundError ?? string.Empty;
        if (FontFamilyHint is not null)
        {
            FontFamilyHint.Text = fontFamilyError ?? fontFamilyHint ?? string.Empty;
            FontFamilyHint.Foreground = fontFamilyError is not null
                ? ErrorBrush
                : fontFamilyHintIsWarning ? WarningBrush : MutedBrush;
        }

        var hasErrors = foregroundError is not null || backgroundError is not null || fontFamilyError is not null;
        if (ValidationText is not null)
            ValidationText.Text = hasErrors ? "存在无效输入，修正后才能保存" : string.Empty;
        if (SaveButton is not null)
            SaveButton.IsEnabled = !hasErrors;

        UpdateSummary();
    }

    /// <summary>Empty means "follow the theme"; anything else must be a strict hex color.</summary>
    private static string? ColorError(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text.Trim().Length > OsdColorRules.MaxInputLength)
            return $"{field}最多 {OsdColorRules.MaxInputLength} 个字符（#RRGGBB 或 #AARRGGBB）。";
        if (!OsdColorRules.IsValidColor(text))
            return $"{field}必须是 #RRGGBB 或 #AARRGGBB。";
        return null;
    }

    /// <summary>Validates the typed font family and describes whether it is installed.</summary>
    private string? FontFamilyError(out string? hint, out bool hintIsWarning)
    {
        hint = null;
        hintIsWarning = false;
        var text = FontFamilyTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            hint = $"跟随主题（{OsdFontFamilyRules.DefaultFontFamily}）";
            return null;
        }
        if (text.Trim().Length > OsdFontFamilyRules.MaxLength)
            return $"字体名称最多 {OsdFontFamilyRules.MaxLength} 个字符。";
        var normalized = OsdFontFamilyRules.NormalizeFontFamily(text);
        if (normalized is null)
            return "字体名称只能包含字母、数字、空格及 - _ . + ( ) '，不能包含路径或文件后缀。";
        if (OsdFontResolver.IsInstalled(normalized))
        {
            hint = $"已安装：{normalized}";
            return null;
        }
        hint = "本机未检测到该字体，弹窗将回退默认字体。";
        hintIsWarning = true;
        return null;
    }

    private void UpdateSummary()
    {
        if (SummaryText is null)
            return;
        SummaryText.Text = $"当前选择：{BuildAppearance().Sanitize().Describe()}（保存后立即应用，可随时恢复默认）";
    }

    private void RestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        LoadAppearance(OsdAppearance.Default);
        _templates.Clear();
        LoadCurrentTemplate();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var appearance = BuildAppearance();
            appearance.Validate();
            StoreCurrentTemplate();
            Result = appearance;
            TemplateResult = OsdEventTemplates.SanitizeOverrides(_templates);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            System.Windows.MessageBox.Show(this, ex.GetBaseException().Message,
                "OSD 外观设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool SameColor(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static Media.Brush Freeze(byte red, byte green, byte blue)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed record IconEntry(OsdIcon? Icon, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DurationEntry(double? Seconds)
    {
        public override string ToString() => Seconds is { } seconds ? $"{seconds:0.##} 秒" : "跟随事件";
    }

    private sealed record ColorEntry(string? Color, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record FontEntry(double? Size, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record PlacementEntry(OsdPlacement Placement, string Label)
    {
        public override string ToString() => Label;
    }
}
