using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniProToolkit.Core;

public enum TaskbarDisplayItem
{
    // Keep numeric values from schema v1/v2 stable; older settings may store enum numbers.
    CpuTemperature = 0,
    FanRpm = 1,
    TargetRpm = 2,
    TargetDuty = 3,
    RpmFeedback = 4,
    PowerMode = 5,
    CpuUsage = 6,
    MemoryUsage = 7,
    Guardian = 8,
    WmiStatus = 9,
    CpuPackagePower = 10,
    // Additive values, so every previously stored number keeps its meaning.
    CpuFrequency = 11,
    // Read-only LHM GPU die temperature. Appended only: existing settings files keep their
    // exact item list and the user opts in through the existing taskbar settings dialog.
    GpuTemperature = 12
}

/// <summary>
/// How the saved display items are arranged on the taskbar overlay. The values are append-only
/// for the same reason as <see cref="TaskbarDisplayItem"/>: a stored number keeps its meaning if
/// one form is ever retired. New files store the name (the store uses a string-enum converter);
/// old files without the field load as <see cref="StackedPairs"/>.
/// </summary>
public enum TaskbarLayoutMode
{
    /// <summary>A - two stacked rows per column, colon-style rows and a 1px column separator. Default.</summary>
    StackedPairs = 0,
    /// <summary>C - one horizontal line with two groups (first two items, then the rest) and one divider; one item per line on a vertical taskbar.</summary>
    Inline = 1,
    /// <summary>D - exactly two rows; the column count adapts to the available width/height and font size. Narrow strips fall back to an explicit +N overflow marker.</summary>
    AdaptiveColumns = 2,
    /// <summary>E - one small card per item, label above value.</summary>
    LabelOverValue = 3
}

public sealed record TaskbarDisplaySettings(
    IReadOnlyList<TaskbarDisplayItem> Items,
    bool Compact,
    bool Vertical,
    int RefreshIntervalSeconds,
    string FontFamily = "Segoe UI",
    double FontSize = 12,
    string TextColor = "#E8EAED",
    // Transparent by default so the status text sits directly on the taskbar pixels like
    // TrafficMonitor. A semi-transparent or opaque value (#AARRGGBB / #RRGGBB) is still honoured
    // as an explicit contrast card; a value already stored in taskbar.json is never rewritten.
    string BackgroundColor = "#00000000",
    // Appended at the end so every existing construction site keeps its defaults. A is the
    // documented default and the value an old taskbar.json without the field loads as.
    TaskbarLayoutMode LayoutMode = TaskbarLayoutMode.StackedPairs,
    // Two bounded coordinate nudges in DIPs relative to the fixed taskbar start origin (left end of
    // a horizontal bar, top of a vertical bar). They are physical screen directions, not an anchor:
    // the dialog moves the overlay 8 DIP per click and the placement still clamps the window inside
    // the appbar rectangle. A missing field in an older taskbar.json means 0/0, i.e. exactly the
    // previously fixed placement.
    int OffsetXDip = 0,
    int OffsetYDip = 0,
    // Appended at the end for the same reason as every field above. This is the single persisted
    // on/off switch of the whole taskbar strip (the settings dialog's 显示任务栏状态 checkbox); a
    // taskbar.json written before schema 6 has no field and loads as true, i.e. exactly the strip
    // those installations had. Turning it off only stops creating/refreshing the overlay windows:
    // telemetry, fan control and the floating card are separate and unaffected.
    bool Enabled = true)
{
    /// <summary>Smallest allowed nudge on either axis, in DIPs.</summary>
    public const int MinimumOffsetDip = -200;

    /// <summary>Largest allowed nudge on either axis, in DIPs.</summary>
    public const int MaximumOffsetDip = 200;

    /// <summary>One click of the dialog's direction buttons, in DIPs.</summary>
    public const int OffsetStepDip = 8;

    public static TaskbarDisplaySettings Default => new(
        [TaskbarDisplayItem.CpuTemperature, TaskbarDisplayItem.FanRpm, TaskbarDisplayItem.CpuPackagePower, TaskbarDisplayItem.PowerMode], true, false, 1);

    public void Validate()
    {
        if (Items is null || Items.Count == 0) throw new ArgumentException("At least one taskbar display item is required.", nameof(Items));
        if (Items.Count != Items.Distinct().Count() || Items.Any(item => !Enum.IsDefined(item))) throw new ArgumentException("Taskbar display items are invalid.", nameof(Items));
        if (RefreshIntervalSeconds is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(RefreshIntervalSeconds));
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 100) throw new ArgumentException("Invalid taskbar font.", nameof(FontFamily));
        if (!double.IsFinite(FontSize) || FontSize is < 8 or > 32) throw new ArgumentOutOfRangeException(nameof(FontSize));
        if (!Enum.IsDefined(LayoutMode)) throw new ArgumentException("Taskbar layout mode is invalid.", nameof(LayoutMode));
        ValidateOffset(OffsetXDip, nameof(OffsetXDip)); ValidateOffset(OffsetYDip, nameof(OffsetYDip));
        ValidateColor(TextColor, nameof(TextColor)); ValidateColor(BackgroundColor, nameof(BackgroundColor));
    }

    /// <summary>
    /// One bounded nudge per axis. The range is deliberately small: the offset only moves the strip
    /// within its own appbar rectangle, it is not a free screen coordinate.
    /// </summary>
    private static void ValidateOffset(int value, string name)
    {
        if (value is < MinimumOffsetDip or > MaximumOffsetDip)
            throw new ArgumentOutOfRangeException(name, $"Taskbar nudge must stay within {MinimumOffsetDip}..{MaximumOffsetDip} DIP.");
    }
    private static void ValidateColor(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('#') || (value.Length is not (7 or 9)) ||
            !value.Skip(1).All(c => Uri.IsHexDigit(c))) throw new ArgumentException("Color must be #RRGGBB or #AARRGGBB.", name);
    }
}

public sealed class TaskbarDisplaySettingsStore
{
    private const int CurrentVersion = 6;
    // The version that introduced each migrated item. Anything at or above the version that
    // added an item was saved with the item available to the user, so a missing item there is
    // a deliberate removal and must not be re-added.
    private const int CpuPowerItemVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public TaskbarDisplaySettingsStore(string? path = null) => Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniProToolkit", "taskbar.json");
    public string Path { get; }
    public TaskbarDisplaySettings LoadOrDefault(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(Path)) return TaskbarDisplaySettings.Default;
            // Items stay raw JSON elements so an item name this build no longer knows (an old
            // settings file saved by a build with an extra metric) can be dropped on its own
            // instead of throwing and resetting the whole list to the defaults.
            var document = JsonSerializer.Deserialize<ReadDocument>(File.ReadAllText(Path), JsonOptions) ?? throw new InvalidDataException("Taskbar settings are empty.");
            if (document.Version is < 1 or > CurrentVersion) throw new InvalidDataException("Unsupported taskbar settings version.");
            // A JSON null item list deserializes to null; report it as invalid data instead of
            // letting a NullReferenceException escape the fallback filter below.
            if (document.Items is null) throw new InvalidDataException("Taskbar settings have no item list.");
            var items = new List<TaskbarDisplayItem>(document.Items.Count);
            var ignored = 0;
            foreach (var element in document.Items)
            {
                if (TryReadItem(element, out var item)) items.Add(item);
                else ignored++;
            }
            if (items.Count == 0) throw new InvalidDataException("Taskbar settings have no supported display item.");
            // Prior versions had no CPU package power item. Add it during migration
            // so existing installations see the newly supported metric immediately.
            // The CPU frequency item added in version 4 is deliberately *not* injected: the
            // default taskbar items stay exactly as before and the user opts in through the
            // existing taskbar settings list.
            if (document.Version < CpuPowerItemVersion && !items.Contains(TaskbarDisplayItem.CpuPackagePower))
                items.Insert(Math.Min(2, items.Count), TaskbarDisplayItem.CpuPackagePower);
            // An old file has no layoutMode and loads as A. A value this build does not know
            // (hand-edited or written by a newer build) also falls back to A for the layout only:
            // every other stored setting is kept, so one unknown field can never reset the file.
            var layoutMode = document.LayoutMode is { } storedMode && Enum.IsDefined(storedMode)
                ? storedMode
                : TaskbarLayoutMode.StackedPairs;
            // Schema 6 appended the taskbar on/off switch. An older file (v1..v5) has no
            // "enabled" member, so it loads as true, which keeps that installation's strip visible.
            var settings = new TaskbarDisplaySettings(items.AsReadOnly(), document.Compact, document.Vertical, document.RefreshIntervalSeconds, document.FontFamily ?? "Segoe UI", document.FontSize ?? 12, document.TextColor ?? "#E8EAED", document.BackgroundColor ?? TaskbarDisplaySettings.Default.BackgroundColor, layoutMode, document.OffsetXDip ?? 0, document.OffsetYDip ?? 0, document.Enabled ?? true);
            settings.Validate();
            if (ignored > 0) warning = $"任务栏设置包含 {ignored} 个当前版本已不支持的显示项，已自动忽略（其余设置保留）。";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { warning = $"Taskbar settings were reset: {ex.GetBaseException().Message}"; return TaskbarDisplaySettings.Default; }
    }
    public void Save(TaskbarDisplaySettings settings)
    {
        settings.Validate();
        var directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("Taskbar path has no directory."); Directory.CreateDirectory(directory);
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(CurrentVersion, settings.Items.ToList(), settings.Compact, settings.Vertical, settings.RefreshIntervalSeconds, settings.FontFamily, settings.FontSize, settings.TextColor, settings.BackgroundColor, settings.LayoutMode, settings.OffsetXDip, settings.OffsetYDip, settings.Enabled), JsonOptions)); File.Move(temporary, Path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // The optional fields stay nullable on read so a file written before they existed keeps
    // loading (absent -> null -> documented default). Placement is fixed to the taskbar's
    // starting edge and carries no settings, so a taskbar.json written by a build that had
    // temporary positionAnchor / positionOffsetDip fields simply has them ignored (System.Text.Json
    // skips unmapped members by default) and every other setting is preserved, including the
    // additive bounded DIP nudge added in schema 5 (an older file without it loads as 0/0, i.e.
    // exactly the previously fixed placement). Schema 6 appends the persisted taskbar on/off
    // switch; it is written as a plain true/false ("enabled") and an older file without it reads as
    // true. A file written by this build is version 6, so an older build rejects it through the
    // unchanged strict version guard (> CurrentVersion is unsupported) and resets to Default instead
    // of silently misreading the extra member; that downgrade direction is documented as unsupported.
    private sealed record Document(int Version, List<TaskbarDisplayItem> Items, bool Compact, bool Vertical, int RefreshIntervalSeconds, string? FontFamily = null, double? FontSize = null, string? TextColor = null, string? BackgroundColor = null, TaskbarLayoutMode? LayoutMode = null, int? OffsetXDip = null, int? OffsetYDip = null, bool? Enabled = null);
    // Read-only shape: items stay untyped so an unknown/retired item name cannot fail the
    // whole deserialization. Every other field keeps the same JSON contract as Document. The
    // layout mode uses a tolerant converter so an unknown name/number becomes null (= A) instead
    // of a JsonException that would reset the whole file.
    private sealed record ReadDocument(int Version, List<JsonElement>? Items, bool Compact, bool Vertical, int RefreshIntervalSeconds, string? FontFamily = null, double? FontSize = null, string? TextColor = null, string? BackgroundColor = null, [property: JsonConverter(typeof(TolerantTaskbarLayoutModeConverter))] TaskbarLayoutMode? LayoutMode = null, int? OffsetXDip = null, int? OffsetYDip = null, bool? Enabled = null);

    /// <summary>
    /// Reads an optional <see cref="TaskbarLayoutMode"/> without throwing on a value written by a
    /// newer or hand-edited build. An unusable value is null, which the loader maps to A while the
    /// remaining settings stay untouched.
    /// </summary>
    private sealed class TolerantTaskbarLayoutModeConverter : JsonConverter<TaskbarLayoutMode?>
    {
        public override TaskbarLayoutMode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.String:
                    return Enum.TryParse<TaskbarLayoutMode>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named)
                        ? named
                        : null;
                case JsonTokenType.Number when reader.TryGetInt32(out var number):
                {
                    var numeric = (TaskbarLayoutMode)number;
                    return Enum.IsDefined(numeric) ? numeric : null;
                }
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, TaskbarLayoutMode? value, JsonSerializerOptions options)
        {
            if (value is { } mode) writer.WriteStringValue(mode.ToString());
            else writer.WriteNullValue();
        }
    }

    /// <summary>
    /// Reads one stored display item, accepting both the enum name and the numeric form. An
    /// unknown name or an undefined number (a metric this build no longer ships) is skipped.
    /// </summary>
    private static bool TryReadItem(JsonElement element, out TaskbarDisplayItem item)
    {
        string? text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
        if (text is not null && Enum.TryParse<TaskbarDisplayItem>(text, ignoreCase: true, out item) && Enum.IsDefined(item))
            return true;
        item = default;
        return false;
    }
}

/// <summary>
/// Unit formatting for the CPU-frequency metrics shared by the main window, the taskbar
/// overlay and the tests. Pure string work: a missing value always renders as a clear
/// placeholder ("--"), never as a fabricated 0, so "unknown" stays distinguishable from
/// "0 MHz".
/// </summary>
public static class MetricDisplayFormat
{
    public const string UnknownMegahertz = "-- MHz";
    public const string UnknownPercent = "--%";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>CPU clock in MHz; the unit is spelled out so 4200 is never read as GHz.</summary>
    public static string Megahertz(double? mhz) =>
        mhz is { } value && double.IsFinite(value) && value > 0
            ? string.Create(Invariant, $"{value:0} MHz")
            : UnknownMegahertz;

    /// <summary>Clock as a percentage of the nominal clock, for example "115%".</summary>
    public static string PercentOfBase(double? percent) =>
        percent is { } value && double.IsFinite(value) && value > 0
            ? string.Create(Invariant, $"{value:0}%")
            : UnknownPercent;

    /// <summary>
    /// Clock together with the nominal clock, so a turbo value can never be mistaken for the
    /// base clock: "4200 MHz（基准 2800 MHz，150%）". Without a usable base clock only the
    /// clock itself is shown.
    /// </summary>
    public static string Frequency(double? mhz, double? baseMhz, double? percentOfBase) =>
        mhz is { } value && double.IsFinite(value) && value > 0 &&
        baseMhz is { } baseValue && double.IsFinite(baseValue) && baseValue > 0
            ? $"{Megahertz(value)}（基准 {baseValue.ToString("0", Invariant)} MHz，{PercentOfBase(percentOfBase)}）"
            : Megahertz(mhz);
}
