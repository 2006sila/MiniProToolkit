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
            "ItemsList", "CompactCheck", "VerticalCheck", "RefreshCombo", "FontFamilyText", "FontSizeText",
            "TextColorText", "BackgroundColorText", "HotkeyPanel", "HotkeyStatusText");
        foreach (var name in new[] { "ItemsList", "RefreshCombo", "TextColorText", "BackgroundColorText", "HotkeyPanel" })
            AssertHelpText(FindNamed(document, name));

        var itemCheck = Assert.Single(document.Descendants(Presentation + "CheckBox"),
            element => element.Ancestors(Presentation + "DataTemplate").Any());
        Assert.Equal("{Binding Label}", (string?)itemCheck.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding Enabled, UpdateSourceTrigger=PropertyChanged}",
            (string?)itemCheck.Attribute("IsChecked"));

        foreach (var handler in new[] { "MoveUpClick", "MoveDownClick", "ResetHotkeysClick" })
        {
            var button = Assert.Single(document.Descendants(Presentation + "Button"),
                element => (string?)element.Attribute("Click") == handler);
            AssertHasName(button);
            AssertHelpText(button);
        }
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

    private static string FindAppSource(string fileName)
    {
        // Start at the test output directory; work in any checkout without a user-specific path.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "tests", "MiniProToolkit.Core.Tests", "MiniProToolkit.Core.Tests.csproj");
            var markup = Path.Combine(directory.FullName, "src", "MiniProToolkit.App", fileName);
            if (File.Exists(project) && File.Exists(markup))
                return markup;
        }
        throw new FileNotFoundException($"Could not locate app source {fileName} above the test directory.");
    }
}
