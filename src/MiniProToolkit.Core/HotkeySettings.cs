using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MiniProToolkit.Core;

/// <summary>
/// The application's own Windows global hotkey actions. These are registered with
/// <c>RegisterHotKey</c> by the app itself and have nothing to do with vendor Fn keys
/// (vendor Fn/touchpad support is explicitly out of scope).
/// </summary>
public enum HotkeyAction
{
    // Numeric values are part of the persisted settings and of the registration ids
    // (registration id = value + 1); append new actions, never renumber.
    SilenceMode = 0,
    BalanceMode = 1,
    PerformanceMode = 2,
    AutomaticMode = 3,
    FullSpeed = 4,
    // Second phase: display-only actions of the standalone desktop floating window. They are
    // appended so the five original values - and therefore their registration ids - never change.
    ToggleFloatingStatus = 5,
    DisableFloatingClickThrough = 6
}

/// <summary>Modifier bits, kept equal to the Win32 <c>MOD_*</c> values so no translation table is needed.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

/// <summary>
/// The whitelist of keys a hotkey may use: <c>A-Z</c>, <c>0-9</c> and <c>F1-F12</c>. Nothing else is
/// accepted, so an arbitrary key name (or a key that only exists on one keyboard layout) can never
/// reach <c>RegisterHotKey</c>. The virtual-key mapping is pure arithmetic and unit-tested.
/// </summary>
public static class HotkeyKeyCatalog
{
    /// <summary>Every allowed key, in menu order (digits, then letters, then function keys).</summary>
    public static IReadOnlyList<string> Keys { get; } =
        [.. Enumerable.Range(0, 10).Select(index => ((char)('0' + (index + 1) % 10)).ToString()),
         .. Enumerable.Range(0, 26).Select(index => ((char)('A' + index)).ToString()),
         .. Enumerable.Range(1, 12).Select(index => "F" + index.ToString(CultureInfo.InvariantCulture))];

    /// <summary>Canonical form of a key (upper case, trimmed) or <c>null</c> when it is not whitelisted.</summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var trimmed = key.Trim().ToUpperInvariant();
        return TryGetVirtualKey(trimmed, out _) ? trimmed : null;
    }

    /// <summary>Maps a whitelisted key to its virtual-key code; false for anything else.</summary>
    public static bool TryGetVirtualKey(string? key, out uint virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(key))
            return false;
        var trimmed = key.Trim().ToUpperInvariant();
        if (trimmed.Length == 1 && char.IsAsciiDigit(trimmed[0]))
        {
            virtualKey = (uint)('0' + (trimmed[0] - '0'));
            return true;
        }
        if (trimmed.Length == 1 && trimmed[0] is >= 'A' and <= 'Z')
        {
            virtualKey = (uint)('A' + (trimmed[0] - 'A'));
            return true;
        }
        if (trimmed.Length is 2 or 3 && trimmed[0] == 'F' && int.TryParse(trimmed.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function))
        {
            if (function is >= 1 and <= 12)
            {
                virtualKey = (uint)(0x70 + function - 1);
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// One concrete hotkey: a modifier set plus one whitelisted key. Validation rejects everything that
/// could never be registered or would be dangerous to own globally: no modifier at all (it would
/// swallow ordinary typing), Shift-only combinations (they collide with normal shifted text) and the
/// shell's reserved combinations.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>Default shape of every shipped hotkey: <c>Ctrl+Alt+&lt;key&gt;</c>.</summary>
    public static HotkeyGesture CtrlAlt(string key) => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, key);

    /// <summary>Canonical key (upper case) without touching the stored value.</summary>
    public string NormalizedKey => HotkeyKeyCatalog.Normalize(Key) ?? Key?.Trim() ?? string.Empty;

    /// <summary>Win32 modifier bits for <c>RegisterHotKey</c>; only valid for a validated gesture.</summary>
    public uint NativeModifiers => (uint)(Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows));

    /// <summary>Win32 virtual-key code; throws for a key outside the whitelist.</summary>
    public uint VirtualKey => HotkeyKeyCatalog.TryGetVirtualKey(Key, out var virtualKey)
        ? virtualKey
        : throw new ArgumentException("不支持的快捷键按键。", nameof(Key));

    /// <summary>Keys the shell already owns; owning them globally would break basic Windows behaviour.</summary>
    private static readonly (HotkeyModifiers Modifiers, string Key, string Reason)[] Reserved =
    [
        (HotkeyModifiers.Alt, "F4", "Alt+F4 用于关闭当前窗口"),
        (HotkeyModifiers.Windows, "L", "Win+L 用于锁定系统"),
        (HotkeyModifiers.Windows, "D", "Win+D 用于显示桌面"),
        (HotkeyModifiers.Windows, "E", "Win+E 用于打开资源管理器"),
        (HotkeyModifiers.Windows, "R", "Win+R 用于打开运行对话框"),
        (HotkeyModifiers.Windows, "S", "Win+S 用于打开搜索")
    ];

    /// <summary>
    /// Throws <see cref="ArgumentException"/> for a gesture that may not be owned globally. Messages are
    /// user-facing (the settings dialog shows them verbatim) and never contain a path or a command line.
    /// </summary>
    public void Validate(string? paramName = null)
    {
        if (Modifiers == HotkeyModifiers.None)
            throw new ArgumentException("快捷键必须至少包含一个修饰键（Ctrl/Alt/Shift/Win）。", paramName);
        if ((Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) == 0)
            throw new ArgumentException("快捷键必须包含 Ctrl、Alt 或 Win 之一，只有 Shift 的组合会与正常输入冲突。", paramName);
        if (HotkeyKeyCatalog.Normalize(Key) is not { } key)
            throw new ArgumentException("不支持的快捷键按键：仅允许 A-Z、0-9、F1-F12。", paramName);
        foreach (var (reservedModifiers, reservedKey, reason) in Reserved)
        {
            if (reservedModifiers == Modifiers && reservedKey == key)
                throw new ArgumentException($"{DescribeModifiers(Modifiers)}+{key} 不可用：{reason}。", paramName);
        }
    }

    /// <summary>Human-readable form used by the settings UI, the tray text and the log summary.</summary>
    public string Describe()
    {
        var modifiers = DescribeModifiers(Modifiers);
        var key = NormalizedKey;
        return modifiers.Length == 0 ? key : $"{modifiers}+{key}";
    }

    /// <summary>Canonical storage form ("Control+Alt+Shift+Win"); empty for no modifier.</summary>
    public string DescribeStorage() => DescribeModifiers(Modifiers, "Control");

    private static string DescribeModifiers(HotkeyModifiers modifiers, string controlName = "Ctrl")
    {
        var builder = new StringBuilder();
        foreach (var (flag, name) in new[]
                 {
                     (HotkeyModifiers.Control, controlName),
                     (HotkeyModifiers.Alt, "Alt"),
                     (HotkeyModifiers.Shift, "Shift"),
                     (HotkeyModifiers.Windows, "Win")
                 })
        {
            if ((modifiers & flag) == 0)
                continue;
            if (builder.Length > 0)
                builder.Append('+');
            builder.Append(name);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Parses a modifier list ("Ctrl+Alt", "Control, Alt", "win") into flags. Unknown tokens make the
    /// whole string invalid instead of being silently dropped.
    /// </summary>
    public static bool TryParseModifiers(string? text, out HotkeyModifiers modifiers)
    {
        modifiers = HotkeyModifiers.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        foreach (var token in text.Split([',', '+', '|', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win" or "windows" or "meta":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    return false;
            }
        }
        return modifiers != HotkeyModifiers.None;
    }
}

/// <summary>One action together with the gesture bound to it.</summary>
public sealed record HotkeyBinding(HotkeyAction Action, HotkeyGesture Gesture);

/// <summary>
/// The complete set of global hotkeys: exactly one gesture per <see cref="HotkeyAction"/>, no
/// duplicates and only whitelisted combinations. Pure data plus validation, so the whole rule set is
/// unit-tested without registering anything.
/// </summary>
public sealed record HotkeySettings(IReadOnlyList<HotkeyBinding> Bindings)
{
    /// <summary>
    /// The compatible defaults (<c>Ctrl+Alt+1/2/3/A/F</c> for the five original control actions and
    /// <c>Ctrl+Alt+F9</c> / <c>Ctrl+Alt+F10</c> for the two desktop-floating display actions). A
    /// settings file from before rebinding existed, a missing field or a missing action all resolve to
    /// these, so nobody loses a working hotkey because of the new feature. The two appended actions are
    /// last in the enum, so an old file's user bindings are read first and win any collision; only when
    /// a user already owns <c>Ctrl+Alt+F9</c>/<c>F10</c> does the loader move the new action to the first
    /// free <c>Ctrl+Alt</c> combination instead.
    /// </summary>
    public static HotkeySettings Default { get; } = CreateDefault();

    private static HotkeySettings CreateDefault() => new(
    [
        new HotkeyBinding(HotkeyAction.SilenceMode, HotkeyGesture.CtrlAlt("1")),
        new HotkeyBinding(HotkeyAction.BalanceMode, HotkeyGesture.CtrlAlt("2")),
        new HotkeyBinding(HotkeyAction.PerformanceMode, HotkeyGesture.CtrlAlt("3")),
        new HotkeyBinding(HotkeyAction.AutomaticMode, HotkeyGesture.CtrlAlt("A")),
        new HotkeyBinding(HotkeyAction.FullSpeed, HotkeyGesture.CtrlAlt("F")),
        new HotkeyBinding(HotkeyAction.ToggleFloatingStatus, HotkeyGesture.CtrlAlt("F9")),
        new HotkeyBinding(HotkeyAction.DisableFloatingClickThrough, HotkeyGesture.CtrlAlt("F10"))
    ]);

    /// <summary>Gesture bound to <paramref name="action"/>; the defaults are complete, so this always resolves.</summary>
    public HotkeyGesture GestureFor(HotkeyAction action) => Bindings.First(binding => binding.Action == action).Gesture;

    /// <summary>Copy with one action rebound. Does not validate; call <see cref="Validate"/> before saving.</summary>
    public HotkeySettings WithGesture(HotkeyAction action, HotkeyGesture gesture) => new(
        Bindings.Select(binding => binding.Action == action ? binding with { Gesture = gesture } : binding).ToArray());

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when an action is missing or duplicated, a gesture is not
    /// allowed globally, or two actions share the same combination (a conflict would make the second
    /// registration fail silently and leave the user with a dead hotkey).
    /// </summary>
    public void Validate()
    {
        if (Bindings is null || Bindings.Count == 0)
            throw new ArgumentException("快捷键设置不能为空。", nameof(Bindings));
        var actions = Enum.GetValues<HotkeyAction>();
        foreach (var action in actions)
        {
            var matches = Bindings.Count(binding => binding.Action == action);
            if (matches != 1)
                throw new ArgumentException($"{LabelFor(action)} 必须且只能配置一个快捷键。", nameof(Bindings));
        }
        if (Bindings.Count != actions.Length)
            throw new ArgumentException("快捷键设置包含未知动作。", nameof(Bindings));
        foreach (var binding in Bindings)
        {
            binding.Gesture.Validate(nameof(Bindings));
            foreach (var other in Bindings)
            {
                if (ReferenceEquals(other, binding) || other.Action == binding.Action)
                    continue;
                if (other.Gesture.Modifiers == binding.Gesture.Modifiers && other.Gesture.NormalizedKey == binding.Gesture.NormalizedKey)
                    throw new ArgumentException($"{LabelFor(binding.Action)} 与 {LabelFor(other.Action)} 不能使用同一个组合 {binding.Gesture.Describe()}。", nameof(Bindings));
            }
        }
    }

    /// <summary>Log-safe one-line summary (action labels and gestures only, never a path or command line).</summary>
    public string Describe() => string.Join("；", Bindings.Select(binding => $"{LabelFor(binding.Action)}={binding.Gesture.Describe()}"));

    /// <summary>User-facing action label; used by the settings UI, the OSD text and the log summary.</summary>
    public static string LabelFor(HotkeyAction action) => action switch
    {
        HotkeyAction.SilenceMode => "静音模式",
        HotkeyAction.BalanceMode => "均衡模式",
        HotkeyAction.PerformanceMode => "性能模式",
        HotkeyAction.AutomaticMode => "EC 自动",
        HotkeyAction.FullSpeed => "满速",
        HotkeyAction.ToggleFloatingStatus => "显示/隐藏桌面悬浮窗",
        HotkeyAction.DisableFloatingClickThrough => "关闭悬浮窗鼠标穿透",
        _ => action.ToString()
    };
}

/// <summary>One action that could not be registered (per-action degradation).</summary>
public sealed record HotkeyRegistrationFailure(HotkeyAction Action, string Gesture, string Reason);

/// <summary>
/// Builds the log-safe registration summary. Per-action degradation keeps every hotkey that could be
/// registered alive; only the failing ones are reported, and the report carries nothing but the action
/// label, the gesture and a fixed reason text.
/// </summary>
public static class HotkeyRegistrationReport
{
    /// <summary><c>ERROR_HOTKEY_ALREADY_REGISTERED</c>: another process (or a previous instance) owns the combination.</summary>
    public const int HotkeyAlreadyRegistered = 1409;

    public static string ReasonForWin32Error(int errorCode) => errorCode switch
    {
        0 => "系统未返回错误码",
        HotkeyAlreadyRegistered => "已被其他程序占用",
        _ => $"系统拒绝（错误码 {errorCode}）"
    };

    /// <summary>Null when every hotkey registered, so a healthy start logs nothing.</summary>
    public static string? Summarize(IReadOnlyList<HotkeyRegistrationFailure> failures)
    {
        if (failures is null || failures.Count == 0)
            return null;
        var parts = failures.Select(failure =>
            $"{HotkeySettings.LabelFor(failure.Action)}（{failure.Gesture}）→{failure.Reason}");
        return $"全局快捷键部分降级注册失败 {failures.Count} 项：{string.Join("；", parts)}";
    }
}

/// <summary>
/// Persists the global hotkeys at <c>%LOCALAPPDATA%\MiniProToolkit\hotkeys.json</c>. Loading never
/// throws: a missing file, a missing field, an unknown action, an unsupported key or a version written
/// by a newer build falls back to the shipped default per action (with a warning), so a broken file can
/// never start the app without hotkeys. Conflicts inside the file are resolved per action — the first
/// action keeps the combination, later ones fall back to their default and, when even that combination
/// is taken, to the first unused <c>Ctrl+Alt</c> combination — and only a file that cannot be resolved
/// at all resets every action to the default.
/// </summary>
public sealed class HotkeySettingsStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public HotkeySettingsStore(string? path = null) => Path = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiniProToolkit",
        "hotkeys.json");

    /// <summary>
    /// The five actions every existing <c>hotkeys.json</c> can contain. A missing one of these is
    /// reported; a missing newer action is just the documented migration default.
    /// </summary>
    private static readonly HotkeyAction[] LegacyActions =
    [
        HotkeyAction.SilenceMode,
        HotkeyAction.BalanceMode,
        HotkeyAction.PerformanceMode,
        HotkeyAction.AutomaticMode,
        HotkeyAction.FullSpeed
    ];

    public string Path { get; }

    public HotkeySettings LoadOrDefault(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(Path))
                return HotkeySettings.Default;
            var document = JsonSerializer.Deserialize<ReadDocument>(File.ReadAllText(Path), JsonOptions)
                           ?? throw new InvalidDataException("快捷键设置为空。");
            // A file without a version field is read as the current version: an older or hand-written file
            // that simply omits "version" must keep its bindings instead of being reset. Only an explicit
            // out-of-range value (including a version written by a newer build) resets the whole file.
            if ((document.Version ?? CurrentVersion) is < 1 or > CurrentVersion)
                throw new InvalidDataException("不支持的快捷键设置版本。");
            var actions = Enum.GetValues<HotkeyAction>();
            var notes = new List<string>();
            var stored = ReadBindings(document, notes);
            // Only the actions of the first hotkeys schema are counted here. A file written before the
            // two desktop-floating actions were appended is a normal migration - those two load from
            // their defaults - and must not be reported as a degraded file; a rejected stored entry
            // already added its own note in ReadBindings.
            var legacyMissing = LegacyActions.Count(action => !stored.ContainsKey(action));
            if (document.Bindings is not null && legacyMissing > 0)
                notes.Add($"{legacyMissing} 个动作没有可用的快捷键配置，已使用默认或避让组合");
            var bindings = new List<HotkeyBinding>(actions.Length);
            var used = new List<HotkeyGesture>(actions.Length);
            foreach (var action in actions)
            {
                var label = HotkeySettings.LabelFor(action);
                // The stored gesture wins; the shipped default is the first fallback; a conflict between two
                // actions is repaired with the first unused Ctrl+Alt combination from the key whitelist.
                if (stored.TryGetValue(action, out var preferred) && IsFree(preferred, used))
                {
                    bindings.Add(new HotkeyBinding(action, preferred));
                    used.Add(preferred);
                    continue;
                }
                var fallback = HotkeySettings.Default.GestureFor(action);
                if (IsFree(fallback, used))
                {
                    bindings.Add(new HotkeyBinding(action, fallback));
                    used.Add(fallback);
                    if (preferred is not null)
                        notes.Add($"{label} 的快捷键 {preferred.Describe()} 与其它动作冲突，已恢复默认 {fallback.Describe()}");
                    continue;
                }
                // Even the default is taken by another action: pick the first free Ctrl+Alt combination in a
                // fixed order (digits, letters, function keys) so the result never depends on hash ordering and
                // never loops forever; a completely exhausted whitelist still resets the whole file.
                var substitute = FindUnusedCtrlAlt(used)
                                 ?? throw new InvalidDataException($"{label} 找不到可用的快捷键组合。");
                bindings.Add(new HotkeyBinding(action, substitute));
                used.Add(substitute);
                notes.Add($"{label} 的默认组合 {fallback.Describe()} 已被占用，改用 {substitute.Describe()}");
            }
            var settings = new HotkeySettings(bindings.AsReadOnly());
            settings.Validate();
            if (notes.Count > 0)
                warning = $"快捷键设置已部分恢复默认：{string.Join("；", notes)}";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            warning = $"快捷键设置已恢复默认：{ex.GetBaseException().Message}";
            return HotkeySettings.Default;
        }
    }

    public void Save(HotkeySettings settings)
    {
        settings.Validate();
        var directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("Hotkey path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var document = new WriteDocument(CurrentVersion,
                settings.Bindings.Select(binding => new WriteBinding(binding.Action.ToString(), binding.Gesture.DescribeStorage(), binding.Gesture.NormalizedKey)).ToList());
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, Path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static bool IsFree(HotkeyGesture gesture, List<HotkeyGesture> used)
    {
        try
        {
            gesture.Validate();
        }
        catch (ArgumentException)
        {
            return false;
        }
        return !used.Any(other => other.Modifiers == gesture.Modifiers && other.NormalizedKey == gesture.NormalizedKey);
    }

    /// <summary>
    /// First <c>Ctrl+Alt+&lt;key&gt;</c> that is neither reserved nor already taken, in the fixed whitelist
    /// order (0-9, A-Z, F1-F12); <c>null</c> when every combination is used so the caller degrades safely.
    /// </summary>
    private static HotkeyGesture? FindUnusedCtrlAlt(List<HotkeyGesture> used)
    {
        foreach (var key in HotkeyKeyCatalog.Keys)
        {
            var candidate = HotkeyGesture.CtrlAlt(key);
            if (IsFree(candidate, used))
                return candidate;
        }
        return null;
    }

    private static Dictionary<HotkeyAction, HotkeyGesture> ReadBindings(ReadDocument document, List<string> notes)
    {
        var result = new Dictionary<HotkeyAction, HotkeyGesture>();
        if (document.Bindings is null)
        {
            notes.Add("旧设置没有快捷键字段，全部使用默认组合");
            return result;
        }
        foreach (var binding in document.Bindings)
        {
            if (binding is null || !Enum.TryParse<HotkeyAction>(binding.Action, ignoreCase: true, out var action) || !Enum.IsDefined(action))
            {
                notes.Add("已忽略无法识别的快捷键动作");
                continue;
            }
            var label = HotkeySettings.LabelFor(action);
            if (result.ContainsKey(action))
            {
                notes.Add($"{label} 重复定义，已忽略后一项");
                continue;
            }
            var gesture = TryReadGesture(binding, out var reason);
            if (gesture is null)
            {
                notes.Add($"{label} 使用默认组合（{reason}）");
                continue;
            }
            result[action] = gesture;
        }
        return result;
    }

    private static HotkeyGesture? TryReadGesture(ReadBinding binding, out string reason)
    {
        if (!HotkeyGesture.TryParseModifiers(binding.Modifiers, out var modifiers))
        {
            reason = "修饰键缺失或不受支持";
            return null;
        }
        if (HotkeyKeyCatalog.Normalize(binding.Key) is not { } key)
        {
            reason = "按键不受支持";
            return null;
        }
        var gesture = new HotkeyGesture(modifiers, key);
        try
        {
            gesture.Validate();
        }
        catch (ArgumentException ex)
        {
            reason = ex.Message;
            return null;
        }
        reason = string.Empty;
        return gesture;
    }

    private sealed record WriteDocument(int Version, List<WriteBinding> Bindings);

    private sealed record WriteBinding(string Action, string Modifiers, string Key);

    // Read shape mirrors the written contract but keeps every field nullable: an older file may miss
    // a field entirely and a hand-edited file may carry a wrong type for one binding only.
    // Nullable version: a file that omits "version" is treated as the current version instead of 0.
    private sealed record ReadDocument(int? Version, List<ReadBinding?>? Bindings);

    private sealed record ReadBinding(string? Action, string? Modifiers, string? Key);
}
