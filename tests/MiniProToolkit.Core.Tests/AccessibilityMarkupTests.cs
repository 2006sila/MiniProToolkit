using System.Xml.Linq;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Static XML and source guards: no WPF loading, window construction, or hardware access.
/// These checks do not replace manual screen-reader and keyboard navigation validation.
/// Dynamic hotkey rows are checked only for source calls, not runtime accessibility behavior.
/// </summary>
public sealed class AccessibilityMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly HashSet<string> InteractiveControls =
        ["Button", "CheckBox", "Slider", "TextBox", "ComboBox", "ListBox"];

    [Theory]
    [InlineData("DiagnosticsWindow.xaml")]
    [InlineData("OsdSettingsWindow.xaml")]
    [InlineData("TaskbarSettingsWindow.xaml")]
    [InlineData("FloatingStatusSettingsWindow.xaml")]
    public void SettingsMarkupParsesAndInteractiveControlsHaveExplicitNames(string fileName)
    {
        var document = LoadMarkup(fileName);
        var root = Assert.IsType<XElement>(document.Root);
        Assert.Equal(Presentation + "Window", root.Name);
        Assert.Equal("MiniProToolkit.App." + Path.GetFileNameWithoutExtension(fileName),
            (string?)root.Attribute(Xaml + "Class"));

        var controls = root.Descendants()
            .Where(element => element.Name.Namespace == Presentation &&
                              InteractiveControls.Contains(element.Name.LocalName)).ToArray();
        Assert.NotEmpty(controls);
        foreach (var control in controls)
            AssertHasName(control);

        Assert.DoesNotContain(root.DescendantsAndSelf(), element =>
            string.Equals((string?)element.Attribute("FocusVisualStyle"), "{x:Null}",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnosticsTargetsAndReadOnlyReportsHaveSemanticNames()
    {
        var document = LoadMarkup("DiagnosticsWindow.xaml");
        AssertNamedControls(document,
            "AdminText", "GuardianText", "LeaseText", "CurveConfigText", "JsonlText", "CsvText",
            "AppLogText", "VersionText", "RecoveryText", "CapabilitiesText", "SystemIndicatorsText",
            "ProbeSystemControlsButton", "OutputVolumeTarget", "MicrophoneMuteTarget",
            "BrightnessTarget", "SystemControlsText", "RecentText");

        foreach (var name in new[] { "OutputVolumeTarget", "MicrophoneMuteTarget", "BrightnessTarget" })
        {
            var target = FindNamed(document, name);
            AssertHelpText(target);
            Assert.Equal("False", (string?)target.Attribute("IsEnabled"));
        }

        var reports = document.Descendants(Presentation + "TextBox").ToArray();
        Assert.Equal(4, reports.Length);
        foreach (var report in reports)
        {
            Assert.Equal("True", (string?)report.Attribute("IsReadOnly"));
            AssertHasName(report);
        }

        var close = Assert.Single(document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Click") == "CloseClick");
        Assert.Equal("True", (string?)close.Attribute("IsCancel"));
    }

    [Fact]
    public void DiagnosticsHardwareWriteButtonsHaveDistinctNamesAndAreNotDefault()
    {
        var document = LoadMarkup("DiagnosticsWindow.xaml");
        var expected = new[]
        {
            (Id: "SetOutputVolumeButton", Name: "设置输出音量并读回", Handler: "SetOutputVolumeClick"),
            (Id: "SetMicrophoneMuteButton", Name: "设置麦克风静音并读回", Handler: "SetMicrophoneMuteClick"),
            (Id: "SetBrightnessButton", Name: "设置内建屏幕亮度并读回", Handler: "SetBrightnessClick")
        };
        Assert.Equal(3, document.Descendants(Presentation + "Button")
            .Count(element => (string?)element.Attribute("Content") == "设置并读回"));

        foreach (var (id, name, handler) in expected)
        {
            var button = FindNamed(document, id);
            Assert.Equal(Presentation + "Button", button.Name);
            Assert.Equal(name, (string?)button.Attribute("AutomationProperties.Name"));
            Assert.Equal(handler, (string?)button.Attribute("Click"));
            Assert.Equal("False", (string?)button.Attribute("IsEnabled"));
            var isDefault = (string?)button.Attribute("IsDefault");
            Assert.True(isDefault is null || string.Equals(isDefault, "False", StringComparison.OrdinalIgnoreCase),
                $"{id} must not be a default button.");
            Assert.Null(button.Attribute("IsDefaulted"));
            Assert.Null(button.Attribute("Command"));
            Assert.Null(button.Attribute("InputBindings"));
            Assert.DoesNotContain(button.Descendants(), element => element.Name.LocalName.EndsWith("InputBindings"));
            AssertHelpText(button);
        }
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName is "KeyBinding" or "MouseBinding");
    }

    [Fact]
    public void OsdAppearanceAndEventTemplateInputsHaveNamesAndRelevantHints()
    {
        var document = LoadMarkup("OsdSettingsWindow.xaml");
        AssertNamedControls(document,
            "ValidationText", "SaveButton", "ForegroundCombo", "ForegroundTextBox", "ForegroundErrorText",
            "BackgroundCombo", "BackgroundTextBox", "BackgroundErrorText", "OpacitySlider", "OpacityText",
            "FontSizeCombo", "FontFamilyTextBox", "FontFamilyHint", "PlacementCombo", "EventCombo",
            "TemplateOverrideCheck", "TemplateSafetyText", "TemplateTitleText", "TemplateBodyCheck",
            "TemplateAdditionalText", "TemplateIconCombo", "TemplateDurationCombo", "SummaryText");
        foreach (var name in new[]
                 {
                     "ForegroundTextBox", "BackgroundTextBox", "OpacitySlider", "FontSizeCombo",
                     "FontFamilyTextBox", "EventCombo", "TemplateOverrideCheck", "TemplateTitleText",
                     "TemplateBodyCheck", "TemplateAdditionalText", "TemplateIconCombo", "TemplateDurationCombo"
                 })
            AssertHelpText(FindNamed(document, name));
    }

    [Fact]
    public void TaskbarAndMarkupHotkeyControlsHaveNamesAndRelevantHints()
    {
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");
        AssertNamedControls(document,
            "ItemsList", "CompactCheck", "VerticalCheck", "LayoutModeCombo", "LayoutModeHint",
            "RefreshCombo", "FontFamilyText", "FontSizeText",
            "ColorSwatches", "AdvancedColorText", "BackgroundOpacitySlider", "HotkeyPanel", "HotkeyStatusText");
        foreach (var name in new[]
                 {
                     "ItemsList", "LayoutModeCombo", "RefreshCombo", "ColorSwatches", "AdvancedColorText",
                     "BackgroundOpacitySlider", "HotkeyPanel"
                 })
            AssertHelpText(FindNamed(document, name));

        var itemCheck = Assert.Single(document.Descendants(Presentation + "CheckBox"),
            element => element.Ancestors(Presentation + "DataTemplate").Any());
        Assert.Equal("{Binding Label}", (string?)itemCheck.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding Enabled, UpdateSourceTrigger=PropertyChanged}",
            (string?)itemCheck.Attribute("IsChecked"));

        // The bottom panel only has 取消/保存: the former 上移/下移 reorder buttons are gone, so the
        // only reorder handles are the row Thumb and Ctrl+↑ / Ctrl+↓. A static guard keeps both the
        // old names/handlers and a silently re-added duplicate from creeping back in.
        Assert.DoesNotContain(document.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Click") is "MoveUpClick" or "MoveDownClick");
        Assert.DoesNotContain(document.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Content") is "上移" or "下移");
        Assert.DoesNotContain(document.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("AutomationProperties.Name") is { } name &&
            (name.Contains("上移所选任务栏项目", StringComparison.Ordinal) ||
             name.Contains("下移所选任务栏项目", StringComparison.Ordinal)));
        var bottomPanel = Assert.Single(document.Descendants(Presentation + "StackPanel"),
            element => (string?)element.Attribute("DockPanel.Dock") == "Bottom");
        var bottomButtons = bottomPanel.Elements(Presentation + "Button").ToArray();
        Assert.Equal(2, bottomButtons.Length);
        Assert.Equal("取消", (string?)bottomButtons[0].Attribute("Content"));
        Assert.Equal("保存", (string?)bottomButtons[1].Attribute("Content"));
        var list = FindNamed(document, "ItemsList");
        Assert.Equal("ItemsListPreviewKeyDown", (string?)list.Attribute("PreviewKeyDown"));
        var listHelp = (string?)list.Attribute("AutomationProperties.HelpText");
        Assert.False(string.IsNullOrWhiteSpace(listHelp));
        Assert.Contains("Ctrl+↑", listHelp);
        Assert.Contains("Ctrl+↓", listHelp);
        Assert.Contains("手柄", listHelp!);

        foreach (var handler in new[] { "ResetHotkeysClick" })
        {
            var button = Assert.Single(document.Descendants(Presentation + "Button"),
                element => (string?)element.Attribute("Click") == handler);
            AssertHasName(button);
            AssertHelpText(button);
        }
    }

    [Fact]
    public void TaskbarStatusOverlayUsesVisibleLayoutCellsWithSemanticName()
    {
        // The overlay no longer forwards through one StatusText element: the visible layout root
        // is named, and the code-behind names every dynamic cell it creates.
        var document = LoadMarkup("TaskbarStatusWindow.xaml");
        var root = Assert.Single(document.Descendants(Presentation + "Grid"),
            element => (string?)element.Attribute(Xaml + "Name") == "StatusLayoutRoot");
        AssertHasName(root);
        AssertHelpText(root);
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "StatusText");
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        Assert.Contains("AutomationProperties.SetName(", source);
        Assert.Contains("StatusLayoutRoot", source);
        Assert.DoesNotContain("StatusText", source);
    }

    [Fact]
    public void TaskbarOverlayOverflowMarkerStaysVisibleNamedAndExplained()
    {
        // The "+N" marker is built in code, so its accessibility contract is checked as a source
        // guard plus the pure plan metadata (see TaskbarItemLayoutTests): a hidden metric is never
        // dropped silently, the marker is always visible, it has a spoken name and its tooltip
        // lists every omitted metric with the full text.
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        Assert.Contains("CreateOverflowMarker(", source);
        Assert.Contains("plan.Overflow", source);
        Assert.Contains("CreateCellText(overflow.Text, metric: null, overflow.AccessibleName)", source);
        Assert.Contains("block.ToolTip = overflow.ToolTip", source);
        // The plan receives the real safe budget so it can decide the +N marker before clipping.
        Assert.Contains("ContentBudgetDip()", source);
        Assert.Contains("TaskbarItemLayout.Build(", source);
        // Tabular digits keep a live value from re-widening a cell.
        Assert.Contains("Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular)", source);
    }

    [Fact]
    public void TaskbarOverlayDrawsOneDividerOnlyAtThePlannedGroupBoundary()
    {
        // The plan only carries group numbers; one 1px line sits exactly at a group change and a
        // plain gap everywhere else, so A/C never grow extra separators. The grid column order is
        // no longer re-derived by the renderer: it consumes the pure TaskbarGridLayout projection,
        // whose exact metric/line/gap/marker indices are unit-tested in TaskbarGridLayoutTests.
        var source = File.ReadAllText(FindAppSource("TaskbarStatusWindow.xaml.cs"));
        Assert.Contains("TaskbarGridLayout.Project(plan, _verticalLayout)", source);
        Assert.Contains("TaskbarGridColumnKind.Separator", source);
        Assert.Contains("layout.MetricColumns[cell.Column]", source);
        Assert.Contains("layout.OverflowColumn", source);
        Assert.Contains("if (previousGroup == group)", source);
        Assert.Contains("TaskbarItemLayout.InlineItemGapDip", source);
        // The divider decision and its 10/1/10 DIP columns live in the Core projection.
        var projection = File.ReadAllText(FindCoreSource("TaskbarGridLayout.cs"));
        Assert.Contains("columnGroups[column] != columnGroups[column - 1]", projection);
        Assert.Contains("TaskbarItemLayout.GroupGapDip", projection);
        // A gets the colon wording; D/C keep the unchanged flat text; E uses its card style.
        Assert.Contains("cell.Style == TaskbarCellStyle.Colon ? metric.ColonText : metric.Text", source);
        Assert.Contains("cell.Style == TaskbarCellStyle.Card", source);
        // The card label/value use their own single names, never a combined duplicate.
        Assert.Contains("CreateCellText(metric.CardLabel, metric, metric.CardLabel)", source);
        Assert.Contains("CreateCellText(metric.ValueText, metric, metric.ValueText)", source);
        // A vertical strip puts the marker in its own row (a column would not be visible there).
        Assert.Contains("Grid.SetRow(marker, plan.Rows)", source);
    }

    [Fact]
    public void TaskbarLayoutModeHelpExplainsTheTwoGroupsAndTheExplicitOverflow()
    {
        // C's two groups are "first 2 items / the rest" because there is no grouping metadata;
        // the dialog must say so, and it must explain that a narrow strip shows +N instead of
        // silently trimming the line.
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");
        var combo = FindNamed(document, "LayoutModeCombo");
        var help = (string?)combo.Attribute("AutomationProperties.HelpText");
        Assert.False(string.IsNullOrWhiteSpace(help));
        Assert.Contains("前 2 项", help);
        Assert.Contains("+N", help);
        Assert.Contains("竖排", help);
    }

    [Fact]
    public void TaskbarSettingsMarkupHasNoCustomPositionControls()
    {
        // The overlay is pinned to the taskbar's starting edge, so the dialog must not offer a
        // position anchor or an along-the-bar offset any more: no such input can be reached by
        // keyboard or screen reader, and the code-behind must not read one.
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");
        Assert.DoesNotContain(document.Descendants(), element =>
        {
            var name = (string?)element.Attribute(Xaml + "Name");
            return name is not null && name.Contains("Position", StringComparison.Ordinal);
        });
        Assert.DoesNotContain(document.Descendants(), element =>
            ((string?)element.Attribute("AutomationProperties.Name"))?.Contains("位置", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(document.Descendants(Presentation + "TextBlock"), element =>
            ((string?)element.Attribute("Text"))?.Contains("位置锚点", StringComparison.Ordinal) == true);

        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        Assert.DoesNotContain("PositionAnchor", source);
        Assert.DoesNotContain("PositionOffset", source);
        Assert.DoesNotContain("TaskbarPositionAnchor", source);
    }

    [Fact]
    public void TaskbarItemRowsCarryADedicatedDragHandleNextToTheUnchangedCheckBox()
    {
        // The reorder handle is a separate Thumb hit target: the row's CheckBox keeps its exact
        // binding contract (one CheckBox in the template, bound to Label/Enabled), so toggling a
        // checkbox can never be turned into a drag of the row.
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");
        var row = Assert.Single(document.Descendants(Presentation + "DataTemplate"),
            element => element.Descendants(Presentation + "CheckBox").Any());

        var handle = Assert.Single(row.Descendants(Presentation + "Thumb"));
        Assert.Equal("ItemDragStarted", (string?)handle.Attribute("DragStarted"));
        Assert.Equal("ItemDragDelta", (string?)handle.Attribute("DragDelta"));
        Assert.Equal("ItemDragCompleted", (string?)handle.Attribute("DragCompleted"));
        AssertHasName(handle);
        AssertHelpText(handle);

        var number = Assert.Single(row.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding DisplayIndex}");
        Assert.NotNull(number);

        var checkBox = Assert.Single(row.Descendants(Presentation + "CheckBox"));
        Assert.Null(checkBox.Attribute("DragStarted"));
        Assert.Null(checkBox.Attribute("DragDelta"));
        Assert.Null(checkBox.Attribute("PreviewMouseLeftButtonDown"));

        // The static event handlers bound in XAML must exist in the code-behind (no silent
        // "handler not found" failure at window construction). The retired MoveUpClick /
        // MoveDownClick handlers must stay gone; the keyboard path they shared is still there.
        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        foreach (var handler in new[]
                 {
                     "ItemDragStarted", "ItemDragDelta", "ItemDragCompleted", "ItemsListPreviewKeyDown"
                 })
            Assert.Contains("private void " + handler + "(", source);
        Assert.DoesNotContain("MoveUpClick", source);
        Assert.DoesNotContain("MoveDownClick", source);
        Assert.Contains("private void MoveSelected(int delta)", source);
        Assert.Contains("MoveSelected(-1);", source);
        Assert.Contains("MoveSelected(1);", source);
        Assert.Contains("System.Windows.Input.Key.Up:", source);
        Assert.Contains("System.Windows.Input.Key.Down:", source);
        Assert.Contains("System.Windows.Input.ModifierKeys.Control", source);
    }

    [Fact]
    public void DynamicHotkeyBuildSourceGuardNamesEachInputWithTheLocalizedAction()
    {
        // Deliberately a source guard, not a C# semantic or screen-reader validation.
        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        var rowStart = source.IndexOf("private sealed class HotkeyRow(", StringComparison.Ordinal);
        Assert.True(rowStart >= 0, "HotkeyRow must be present.");
        var buildStart = source.IndexOf("public FrameworkElement Build()", rowStart, StringComparison.Ordinal);
        Assert.True(buildStart >= 0, "HotkeyRow.Build must be present.");
        var buildEnd = source.IndexOf("public void Apply(", buildStart, StringComparison.Ordinal);
        Assert.True(buildEnd > buildStart, "Build source must end before Apply.");
        var build = System.Text.RegularExpressions.Regex.Replace(source[buildStart..buildEnd], @"\s+", " ");
        Assert.Contains("var actionLabel = HotkeySettings.LabelFor(Action);", build);
        Assert.Contains("Text = actionLabel", build);
        Assert.Contains("const string gestureHelpText =", build);
        foreach (var hint in new[] { "Ctrl、Alt 或 Win", "Shift 不能单独使用", "A-Z、0-9、F1-F12", "不能重复", "保存后生效" })
            Assert.Contains(hint, build);

        foreach (var (control, label) in new[]
                 {
                     ("_control", "Ctrl 修饰键"), ("_alt", "Alt 修饰键"),
                     ("_shift", "Shift 修饰键"), ("_win", "Win 修饰键"), ("_key", "主键")
                 })
        {
            var suffix = control == "_key" ? label : " " + label;
            Assert.Contains("AutomationProperties.SetName(" + control + ", $\"{actionLabel} 快捷键" + suffix + "\");", build);
            Assert.Contains("AutomationProperties.SetHelpText(" + control + ", gestureHelpText);", build);
        }
    }

    [Fact]
    public void TaskbarSettingsOfferFourNamedPhysicalNudgesAndStillNoAnchorEditor()
    {
        // The dialog gained exactly four fixed-step physical direction buttons (each click 8 DIP).
        // They are not an anchor/coordinate editor: no numeric offset box, no position combo, and
        // the retired anchor identifiers stay absent from the code-behind.
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");
        var expected = new[]
        {
            (Name: "实时上移任务栏状态条", Handler: "NudgeUpClick"),
            (Name: "实时下移任务栏状态条", Handler: "NudgeDownClick"),
            (Name: "实时左移任务栏状态条", Handler: "NudgeLeftClick"),
            (Name: "实时右移任务栏状态条", Handler: "NudgeRightClick")
        };
        foreach (var (name, handler) in expected)
        {
            var button = Assert.Single(document.Descendants(Presentation + "Button"),
                element => (string?)element.Attribute("Click") == handler);
            Assert.Equal(name, (string?)button.Attribute("AutomationProperties.Name"));
            AssertHelpText(button);
            // Each physical nudge button states its own 8 DIP step in the visible content; the
            // list-reorder buttons this used to be confused with no longer exist.
            Assert.Contains("8", (string?)button.Attribute("Content"));
        }

        // Live readout of the current nudge, announced with a semantic name.
        AssertHasName(FindNamed(document, "OffsetStatusText"));

        // Still no free offset editor: the only way to change the nudge is the four buttons.
        Assert.DoesNotContain(document.Descendants(Presentation + "TextBox"), element =>
            (string?)element.Attribute(Xaml + "Name") is { } name && name.Contains("Offset", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Descendants(Presentation + "ComboBox"), element =>
            (string?)element.Attribute(Xaml + "Name") is { } name && name.Contains("Offset", StringComparison.Ordinal));

        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        Assert.Contains("TaskbarDisplaySettings.OffsetStepDip", source);
        Assert.Contains("TaskbarDisplaySettings.MinimumOffsetDip", source);
        Assert.Contains("TaskbarDisplaySettings.MaximumOffsetDip", source);
        foreach (var handler in expected.Select(entry => entry.Handler))
            Assert.Contains("private void " + handler + "(", source);
        Assert.DoesNotContain("PositionAnchor", source);
        Assert.DoesNotContain("PositionOffset", source);
        Assert.DoesNotContain("TaskbarPositionAnchor", source);
    }

    [Fact]
    public void TaskbarColorCardHasOneSharedPaletteAndOneAdvancedHexEditor()
    {
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");

        // One card, one palette, one 更多颜色 button, one advanced hex box. The former per-target
        // hex boxes and the two stacked palette panels are gone for real - no hidden duplicates.
        var card = FindNamed(document, "ColorCard");
        Assert.Equal(Presentation + "Border", card.Name);
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") is "TextSwatches" or "BackgroundSwatches"
                or "TextColorText" or "BackgroundColorText");
        var swatches = Assert.Single(document.Descendants(Presentation + "WrapPanel"),
            element => (string?)element.Attribute(Xaml + "Name") == "ColorSwatches");
        AssertHasName(swatches);
        AssertHelpText(swatches);
        Assert.Single(document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Click") == "MoreColorsClick");
        Assert.Single(document.Descendants(Presentation + "TextBox"),
            element => (string?)element.Attribute(Xaml + "Name") == "AdvancedColorText");
        Assert.Single(document.Descendants(Presentation + "Expander"),
            element => (string?)element.Attribute(Xaml + "Name") == "AdvancedColorExpander");

        var buttons = swatches.Elements(Presentation + "Button").ToArray();
        Assert.True(buttons.Length >= 12, "the shared palette needs at least 12 presets.");
        foreach (var required in new[] { "黑色", "白色", "灰色", "红色", "橙色", "黄色", "绿色", "青色", "蓝色", "紫色" })
            Assert.Contains(buttons, button =>
                ((string?)button.Attribute("AutomationProperties.Name"))?.Contains(required, StringComparison.Ordinal) == true);
        // Black was missing from the old palettes and is the obvious dark-theme neutral.
        Assert.Contains(buttons, button => (string?)button.Attribute("Tag") == "#000000");

        foreach (var button in buttons)
        {
            var name = (string?)button.Attribute("AutomationProperties.Name");
            var tooltip = (string?)button.Attribute("ToolTip");
            var tag = (string?)button.Attribute("Tag");
            Assert.False(string.IsNullOrWhiteSpace(name), "every colour sample needs an accessible name.");
            Assert.False(string.IsNullOrWhiteSpace(tooltip), "every colour sample needs a name+value tooltip.");
            Assert.False(string.IsNullOrWhiteSpace(tag), "every colour sample needs its hex value.");
            Assert.StartsWith("#", tag);
            Assert.Contains(tag, name!);
            Assert.Contains(tag, tooltip!);
            Assert.Null(button.Attribute("IsEnabled"));
            Assert.NotEqual("False", (string?)button.Attribute("Focusable"));
            // A sample is a real Button with its own painted WPF plate, not a mouse-only shape.
            Assert.Single(button.Elements(Presentation + "Border"));
            Assert.Equal("ColorSwatchClick", (string?)button.Attribute("Click"));
        }
    }

    [Fact]
    public void TaskbarColorTargetButtonsAreMutuallyExclusiveAndAccessible()
    {
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");
        foreach (var (id, label) in new[] { ("TextColorTargetRadio", "文字"), ("BackgroundColorTargetRadio", "背景") })
        {
            var radio = FindNamed(document, id);
            Assert.Equal(Presentation + "RadioButton", radio.Name);
            Assert.Equal("TaskbarColorTarget", (string?)radio.Attribute("GroupName"));
            Assert.Equal("ColorTargetChanged", (string?)radio.Attribute("Checked"));
            AssertHasName(radio);
            AssertHelpText(radio);
            Assert.Contains(label, (string?)radio.Attribute("AutomationProperties.Name"));
            // Each target carries a small chip with the checkerboard transparency backdrop.
            Assert.Single(radio.Descendants(Presentation + "Border"),
                border => (string?)border.Attribute("Background") == "{StaticResource TaskbarColorCheckerBrush}");
        }
        Assert.Equal("True", (string?)FindNamed(document, "TextColorTargetRadio").Attribute("IsChecked"));
        Assert.Null(FindNamed(document, "BackgroundColorTargetRadio").Attribute("IsChecked"));

        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        // Switching the target only re-points the editor: no colour write, no preview, no save.
        var switchBody = SourceBetween(source, "private void ColorTargetChanged(",
            "private void BackgroundOpacityChanged(");
        Assert.Contains("_colorState.TrySelectTarget(target)", switchBody);
        Assert.Contains("ApplyColorStateToEditor();", switchBody);
        Assert.DoesNotContain("TryPreview", switchBody);
        Assert.DoesNotContain("Store", switchBody);
        Assert.DoesNotContain("Save(", switchBody);
        // An already rejected advanced value cannot be carried over to the other target.
        Assert.Contains("AdvancedColorExpander.IsExpanded = true;", switchBody);
    }

    [Fact]
    public void TaskbarBackgroundOpacityAndTransparentControlsAreNamedAndBackgroundOnly()
    {
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");
        var panel = FindNamed(document, "BackgroundOpacityPanel");
        Assert.Equal("Collapsed", (string?)panel.Attribute("Visibility"));

        var slider = FindNamed(document, "BackgroundOpacitySlider");
        Assert.Equal(Presentation + "Slider", slider.Name);
        Assert.Equal("0", (string?)slider.Attribute("Minimum"));
        Assert.Equal("100", (string?)slider.Attribute("Maximum"));
        Assert.Equal("BackgroundOpacityChanged", (string?)slider.Attribute("ValueChanged"));
        AssertHasName(slider);
        AssertHelpText(slider);

        var readout = FindNamed(document, "BackgroundOpacityText");
        AssertHasName(readout);

        var transparent = FindNamed(document, "BackgroundTransparentButton");
        Assert.Equal("BackgroundTransparentClick", (string?)transparent.Attribute("Click"));
        AssertHasName(transparent);
        AssertHelpText(transparent);
        Assert.Contains("完全透明", (string?)transparent.Attribute("Content"));

        // The hint explains a transparent background instead of silently making it visible.
        var hint = FindNamed(document, "BackgroundOpacityHint");
        Assert.Equal("Collapsed", (string?)hint.Attribute("Visibility"));
        Assert.Contains("全透明", (string?)hint.Attribute("Text"));

        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        Assert.Contains("_colorState.TrySetBackgroundOpacity(", source);
        Assert.Contains("_colorState.SetBackgroundFullyTransparent();", source);
        // The background-only controls are hidden for the text target, so text edits cannot alter
        // the background alpha.
        Assert.Contains("BackgroundOpacityPanel.Visibility = isBackground ? Visibility.Visible : Visibility.Collapsed;",
            source);

        // The pure state covers alpha/remembered-RGB behaviour; this markup test only guards that
        // the controls are wired to it.
        Assert.Contains("TaskbarColorEditorState", source);
    }

    [Fact]
    public void TaskbarAdvancedHexIsFoldedByDefaultAndSavesAreBlockedByInvalidInput()
    {
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");
        var expander = FindNamed(document, "AdvancedColorExpander");
        Assert.Equal("False", (string?)expander.Attribute("IsExpanded"));
        AssertHasName(expander);
        AssertHelpText(expander);

        var editor = FindNamed(document, "AdvancedColorText");
        Assert.Equal("AdvancedColorTextLostFocus", (string?)editor.Attribute("LostFocus"));
        Assert.Equal("AdvancedColorTextKeyDown", (string?)editor.Attribute("KeyDown"));
        AssertHasName(editor);
        AssertHelpText(editor);
        Assert.Single(document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute(Xaml + "Name") == "AdvancedColorErrorText");

        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        // One source of truth: both colours are read from the state, never from UI text boxes.
        Assert.Contains("private readonly TaskbarColorEditorState _colorState;", source);
        Assert.Contains("_colorState.TextHex,", source);
        Assert.Contains("_colorState.BackgroundHex,", source);
        Assert.DoesNotContain("TextSwatches", source);
        Assert.DoesNotContain("BackgroundSwatches", source);

        // Save commits the active advanced editor first and refuses an invalid value.
        var saveBody = SourceBetween(source, "private void SaveClick(", "private static string LabelFor(");
        var commitIndex = saveBody.IndexOf("if (!CommitAdvancedColorEditor())", StringComparison.Ordinal);
        var readIndex = saveBody.IndexOf("settings = ReadDisplaySettings();", StringComparison.Ordinal);
        Assert.True(commitIndex >= 0 && readIndex > commitIndex,
            "Save must commit (and reject invalid) advanced text before reading the settings.");
        Assert.Contains("TaskbarColorEditorState.InvalidHexMessage", saveBody);

        // Rejecting an advanced value leaves the committed colours and therefore the preview (which
        // reads the same state) untouched.
        var commitBody = SourceBetween(source, "private bool CommitAdvancedColorEditor()",
            "private void ShowAdvancedError(");
        Assert.Contains("_colorState.TryCommitAdvanced(AdvancedColorText.Text)", commitBody);
        Assert.True(commitBody.IndexOf("TryPreview();", StringComparison.Ordinal) >
                    commitBody.IndexOf("ApplyColorStateToEditor();", StringComparison.Ordinal));
        Assert.Contains("return false;", commitBody);
    }

    [Fact]
    public void TaskbarSettingsPreviewIsLiveAndCancelRestoresTheOpenedState()
    {
        var markup = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml"));
        // The one advanced hex box previews when it loses focus; samples preview on click.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markup, "LostFocus=\"AdvancedColorTextLostFocus\""));

        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        Assert.Contains("private void ColorSwatchClick(", source);
        Assert.Contains("private void MoreColorsClick(", source);
        Assert.Contains("private void BackgroundOpacityChanged(", source);
        Assert.Contains("private void BackgroundTransparentClick(", source);
        Assert.Contains("_colorState.TryApplyRgb(color.Rgb)", source);
        Assert.DoesNotContain("TextColorText", source);
        Assert.DoesNotContain("BackgroundColorText", source);
        Assert.Contains("_preview(preview)", source);
        Assert.Contains("_preview(_initialSettings)", source);
        Assert.Contains("protected override void OnClosed(EventArgs e)", source);
        Assert.Contains("DialogResult != true", source);

        // The preview path never persists and never touches hardware: no store, no save call.
        var previewStart = source.IndexOf("private void TryPreview()", StringComparison.Ordinal);
        var previewEnd = source.IndexOf("protected override void OnClosed(EventArgs e)", previewStart, StringComparison.Ordinal);
        Assert.True(previewStart >= 0 && previewEnd > previewStart, "TryPreview and OnClosed must be present.");
        var preview = source[previewStart..previewEnd];
        Assert.DoesNotContain("Store", preview);
        Assert.DoesNotContain("Save(", preview);
        Assert.DoesNotContain("TaskbarSettingsWindow", preview);

        // MainWindow supplies the preview; it follows the switch (close on disabled, create on
        // enabled) and applies the edit buffer without writing the file.
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        Assert.Contains("new TaskbarSettingsWindow(_taskbarSettings, _hotkeySettings, PreviewTaskbarSettings)", main);
        Assert.Contains("window.UpdateDisplaySettings(settings)", main);
        Assert.Contains("catch (Exception ex) { AppLog.Write(\"taskbar settings preview failed\", ex); }", main);

        var previewPath = SourceBetween(main, "private void PreviewTaskbarSettings(TaskbarDisplaySettings settings)",
            "private void TaskbarSettingsClick(");
        // Nothing may be created after the main window started closing.
        Assert.Contains("if (_closing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)", previewPath);
        Assert.Contains("_taskbarPreviewEnabled = settings.Enabled;", previewPath);
        // Disabled preview closes only the taskbar strip and clears it from the collection.
        Assert.Contains("if (!settings.Enabled)", previewPath);
        Assert.Contains("window.Close();", previewPath);
        Assert.Contains("_taskbarWindows.Clear();", previewPath);
        // Enabled preview creates a missing strip through the normal path, then applies the buffer.
        Assert.Contains("if (_taskbarWindows.Count == 0)", previewPath);
        Assert.Contains("EnsureTaskbars();", previewPath);
        Assert.True(previewPath.IndexOf("EnsureTaskbars();", StringComparison.Ordinal) <
                    previewPath.IndexOf("window.UpdateDisplaySettings(settings);", StringComparison.Ordinal),
            "A newly created strip must receive the previewed settings right after creation.");
        // The preview never persists anything.
        Assert.DoesNotContain("_taskbarSettingsStore", previewPath);
        Assert.DoesNotContain("_taskbarSettings =", previewPath);
        // The floating card is untouched by the taskbar preview path.
        Assert.DoesNotContain("Floating", previewPath);
    }

    [Fact]
    public void TaskbarSettingsSaveFailurePutsThePreviewBackOnTheSavedValue()
    {
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        var handlerStart = main.IndexOf("private void TaskbarSettingsClick(", StringComparison.Ordinal);
        var handlerEnd = main.IndexOf("private void DisplaySourceSettingsClick(", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart, "TaskbarSettingsClick must be present.");
        var handler = main[handlerStart..handlerEnd];

        // The file write comes first and the new value is adopted only afterwards, so a throwing
        // save leaves _taskbarSettings on the settings that are actually on disk.
        var saveIndex = handler.IndexOf("_taskbarSettingsStore.Save(settings);", StringComparison.Ordinal);
        var adoptIndex = handler.IndexOf("_taskbarSettings = settings;", StringComparison.Ordinal);
        Assert.True(saveIndex >= 0 && adoptIndex > saveIndex,
            "_taskbarSettings must only be replaced after the store write succeeded.");

        // The save-failure branch undoes the preview the already-closed dialog left on screen, and
        // it does so in its own guarded block before the failure is reported.
        var catchIndex = handler.IndexOf("catch (Exception ex)", saveIndex, StringComparison.Ordinal);
        var hotkeyComment = handler.IndexOf("// Hotkeys are a separate file", catchIndex, StringComparison.Ordinal);
        Assert.True(catchIndex > saveIndex && hotkeyComment > catchIndex,
            "The taskbar save must have its own catch block before the hotkey save.");
        var catchBlock = handler[catchIndex..hotkeyComment];
        var revertIndex = catchBlock.IndexOf("PreviewTaskbarSettings(_taskbarSettings);", StringComparison.Ordinal);
        var messageBoxIndex = catchBlock.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
        Assert.True(revertIndex >= 0, "A failed taskbar save must revert the preview to _taskbarSettings.");
        Assert.True(messageBoxIndex > revertIndex, "The preview must be reverted before the failure is reported.");
        Assert.Contains("catch (Exception revertEx) when (revertEx is not OutOfMemoryException)", catchBlock);

        // The hotkey file stays a separate step after the taskbar block, now inside the rebind
        // transaction that only writes it after the new registrations are fully live.
        var hotkeySection = handler[hotkeyComment..];
        Assert.Contains("_hotkeySettingsStore.Save(hotkeys)", hotkeySection);
        Assert.Contains("HotkeyRebindTransaction.Run(", hotkeySection);
        Assert.True(hotkeySection.IndexOf("HotkeyRebindTransaction.Run(", StringComparison.Ordinal) <
                    hotkeySection.IndexOf("_hotkeySettingsStore.Save(hotkeys)", StringComparison.Ordinal),
            "The hotkey file may only be written by the transaction's save delegate.");
    }

    [Fact]
    public void TaskbarSettingsColourDialogIsOwnedByTheSettingsWindow()
    {
        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        var pickStart = source.IndexOf("private void PickCustomColor()", StringComparison.Ordinal);
        Assert.True(pickStart >= 0, "PickCustomColor() must be present.");
        var pickEnd = source.IndexOf("private static System.Windows.Media.Brush BrushFrom(", pickStart, StringComparison.Ordinal);
        Assert.True(pickEnd > pickStart, "PickCustomColor must end before BrushFrom.");
        var pick = source[pickStart..pickEnd];

        // The WinForms dialog is owned by this WPF window's HWND, so it cannot appear behind it.
        Assert.Contains("System.Windows.Forms.IWin32Window", source);
        Assert.Contains("new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle()", pick);
        Assert.Contains("dialog.ShowDialog(new WindowHandleOwner(ownerHandle))", pick);
        Assert.Contains("ownerHandle != IntPtr.Zero", pick);
        // The plain overload survives only as the fallback of that conditional call.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(pick, "dialog\\.ShowDialog\\(\\)"));
        // The dialog only supplies RGB; the edited target keeps its existing alpha.
        Assert.Contains("_colorState.ActiveColor", pick);
        Assert.Contains("_colorState.TryApplyRgb(rgb)", pick);
        Assert.DoesNotContain("target.Text", pick);
        // Cancelling changes no colour and never previews.
        Assert.Contains("if (choice != System.Windows.Forms.DialogResult.OK) return;", pick);
    }

    [Fact]
    public void MainWindowEveryNamedElementIsUnique()
    {
        // Every named element is looked up by field name in the code-behind, so a copy-and-paste
        // duplicate would compile but bind the wrong control; the whole markup is checked.
        var document = LoadMarkup("MainWindow.xaml");
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        Assert.NotEmpty(names);
        var duplicates = names.GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key!)
            .ToArray();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void MainWindowFloatingEntryIsANavButtonNextToTaskbarAndOverviewHasNoStatusCard()
    {
        // The floating window's settings entry is a sidebar navigation item directly after 任务栏,
        // exactly like the other settings entries. The overview carries no status-surface card at
        // all any more: the taskbar on/off switch lives only in the taskbar settings dialog and the
        // floating switch only in the floating settings dialog.
        var document = LoadMarkup("MainWindow.xaml");

        var navFloating = FindNamed(document, "NavFloatingButton");
        Assert.Equal(Presentation + "Button", navFloating.Name);
        Assert.Equal("{StaticResource ObsidianNavButtonStyle}", (string?)navFloating.Attribute("Style"));
        Assert.Equal("FloatingSettingsClick", (string?)navFloating.Attribute("Click"));
        Assert.Equal("悬浮窗设置", (string?)navFloating.Attribute("AutomationProperties.Name"));
        Assert.Contains(navFloating.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "悬浮窗");
        Assert.Contains(navFloating.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Style") == "{StaticResource ObsidianNavIconStyle}");

        // Same navigation StackPanel as 任务栏 and its immediate successor: no other item in between.
        var navTaskbar = FindNamed(document, "NavTaskbarButton");
        Assert.Equal("{StaticResource ObsidianNavButtonStyle}", (string?)navTaskbar.Attribute("Style"));
        Assert.Equal("TaskbarSettingsClick", (string?)navTaskbar.Attribute("Click"));
        var navPanel = Assert.IsType<XElement>(navTaskbar.Parent);
        Assert.Equal(Presentation + "StackPanel", navPanel.Name);
        Assert.Equal(navPanel, navFloating.Parent);
        var navItems = navPanel.Elements(Presentation + "Button").ToArray();
        var taskbarIndex = Array.IndexOf(navItems, navTaskbar);
        Assert.True(taskbarIndex >= 0, "NavTaskbarButton must be a direct child of the navigation panel.");
        Assert.True(taskbarIndex + 1 < navItems.Length, "NavFloatingButton must follow NavTaskbarButton.");
        Assert.Equal(navFloating, navItems[taskbarIndex + 1]);

        // The settings button and the click-through escape button are gone from the whole window;
        // the escape path now lives in the tray menu and the global hotkey in MainWindow.xaml.cs.
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") is "FloatingSettingsButton" or "FloatingClickThroughOffButton");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("Click") == "CloseFloatingClickThroughClick");

        // The removed overview card left no quick toggle behind: the taskbar enable switch exists
        // exactly once in the whole window set, inside the taskbar settings dialog.
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") is "TaskbarToggle" or "FloatingToggle" or "StatusSurfacesCard");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("Checked") == "TaskbarToggleChanged");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("Unchecked") == "TaskbarToggleChanged");

        // The top bar keeps its three remaining children and none of the removed status controls.
        var pause = FindNamed(document, "AutomationPauseButton");
        var topBar = Assert.Single(pause.Ancestors(Presentation + "WrapPanel"));
        Assert.Equal("2", (string?)topBar.Attribute("Grid.Row"));
        Assert.Contains(FindNamed(document, "GuardianStatusText"), topBar.Descendants());
        Assert.Contains(FindNamed(document, "DashboardEcPillText"), topBar.Descendants());
        Assert.Contains(pause, topBar.Elements());
        Assert.DoesNotContain(topBar.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") is "TaskbarToggle" or "FloatingToggle");

        // The fixed 220 DIP overview left column keeps exactly one card - the CPU temperature main
        // visual - and no toggle of any kind.
        var temperature = FindNamed(document, "DashboardTemperatureValue");
        var temperatureCard = Assert.Single(temperature.Ancestors(Presentation + "Border"));
        var column = Assert.IsType<XElement>(temperatureCard.Parent);
        Assert.Equal(Presentation + "StackPanel", column.Name);
        Assert.Equal("0", (string?)column.Attribute("Grid.Column"));
        var cards = column.Elements(Presentation + "Border").ToArray();
        Assert.Single(cards);
        Assert.Equal(temperatureCard, cards[0]);
        Assert.Equal("{StaticResource ObsidianCardStyle}", (string?)cards[0].Attribute("Style"));
        Assert.Empty(column.Descendants(Presentation + "CheckBox"));
        Assert.Empty(column.Descendants(Presentation + "Button"));

        // The band still has the 220 DIP column plus the unchanged four-cell metric strip.
        var band = Assert.IsType<XElement>(column.Parent);
        Assert.Equal(Presentation + "Grid", band.Name);
        var widths = band.Elements(Presentation + "Grid.ColumnDefinitions").Single()
            .Elements(Presentation + "ColumnDefinition")
            .Select(definition => (string?)definition.Attribute("Width") ?? string.Empty)
            .ToArray();
        Assert.Equal(["220", "*"], widths);
        var metrics = Assert.Single(band.Elements(Presentation + "Grid"),
            element => (string?)element.Attribute("Grid.Column") == "1");
        Assert.Equal(4, metrics.Elements(Presentation + "Border").Count());
        Assert.Contains(metrics.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "DashboardFanValue");
    }

    [Fact]
    public void TaskbarSettingsDialogOwnsTheSingleEnabledSwitch()
    {
        // The only taskbar on/off switch: named, defaulted from the saved settings in code, and
        // previewing through the shared callback until 保存 persists it.
        var document = LoadMarkup("TaskbarSettingsWindow.xaml");
        var enabled = FindNamed(document, "EnabledCheck");
        Assert.Equal(Presentation + "CheckBox", enabled.Name);
        Assert.Equal("EnabledChanged", (string?)enabled.Attribute("Checked"));
        Assert.Equal("EnabledChanged", (string?)enabled.Attribute("Unchecked"));
        Assert.Equal("显示任务栏状态", (string?)enabled.Attribute("Content"));
        Assert.Equal("显示任务栏状态", (string?)enabled.Attribute("AutomationProperties.Name"));
        var help = (string?)enabled.Attribute("AutomationProperties.HelpText") ?? string.Empty;
        Assert.Contains("任务栏状态条", help);
        // The help text states plainly that only the display stops, not the fan/telemetry schedule.
        Assert.Contains("风扇", help);
        Assert.Contains("遥测", help);

        var source = File.ReadAllText(FindAppSource("TaskbarSettingsWindow.xaml.cs"));
        // The dialog reads the switch into the same settings object every other edit uses.
        var read = SourceBetween(source, "private TaskbarDisplaySettings ReadDisplaySettings()",
            "private void TryPreview()");
        Assert.Contains("EnabledCheck.IsChecked == true", read);
        // The opened value initialises the checkbox under the guard, and only a user click previews.
        Assert.Contains("EnabledCheck.IsChecked = settings.Enabled;", source);
        Assert.Contains("_enabledEditorReady = true;", source);
        var handler = SourceBetween(source, "private void EnabledChanged(object sender, RoutedEventArgs e)",
            "private void UpdateLayoutModeHint()");
        Assert.Contains("if (!_enabledEditorReady) return;", handler);
        Assert.Contains("TryPreview();", handler);
        Assert.DoesNotContain("DialogResult", handler);
        Assert.DoesNotContain("Store", handler);

        // MainWindow keeps no quick toggle and no old handler name anywhere in production code.
        var main = File.ReadAllText(FindAppSource("MainWindow.xaml.cs"));
        Assert.DoesNotContain("TaskbarToggle", main);
        Assert.DoesNotContain("TaskbarToggleChanged", main);
        Assert.Contains("_taskbarSettings.Enabled", main);
    }

    /// <summary>Source slice between two method markers; keeps the source guards readable.</summary>
    private static string SourceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker {endMarker} after {startMarker}");
        return source[start..end];
    }

    private static void AssertNamedControls(XDocument document, params string[] names)
    {
        foreach (var name in names)
            AssertHasName(FindNamed(document, name));
    }

    private static XElement FindNamed(XDocument document, string name) =>
        Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == name);

    private static void AssertHasName(XElement element) =>
        Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute("AutomationProperties.Name")),
            $"{element.Name.LocalName} {(string?)element.Attribute(Xaml + "Name")} needs an explicit accessible name.");

    private static void AssertHelpText(XElement element) =>
        Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute("AutomationProperties.HelpText")),
            $"{(string?)element.Attribute(Xaml + "Name") ?? (string?)element.Attribute("Click")} needs help text.");

    private static XDocument LoadMarkup(string fileName) =>
        XDocument.Load(FindAppSource(fileName), LoadOptions.SetLineInfo);

    private static string FindAppSource(string fileName) => FindSource("MiniProToolkit.App", fileName);

    private static string FindCoreSource(string fileName) => FindSource("MiniProToolkit.Core", fileName);

    private static string FindSource(string projectName, string fileName)
    {
        // Start at the test output directory; work in any checkout without a user-specific path.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "tests", "MiniProToolkit.Core.Tests", "MiniProToolkit.Core.Tests.csproj");
            var candidate = Path.Combine(directory.FullName, "src", projectName, fileName);
            if (File.Exists(project) && File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"Could not locate {projectName} source {fileName} above the test directory.");
    }
}
