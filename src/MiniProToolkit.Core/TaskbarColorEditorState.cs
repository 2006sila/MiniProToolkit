using System.Globalization;

namespace MiniProToolkit.Core;

/// <summary>Which half of the single taskbar colour card is being edited.</summary>
public enum TaskbarColorTarget
{
    Text = 0,
    Background = 1
}

/// <summary>
/// One <c>#AARRGGBB</c> colour value. Alpha is kept separate from RGB on purpose: the colour
/// samples and the system colour dialog only choose RGB, while the background's opacity stays an
/// explicit, independent choice.
/// </summary>
public readonly record struct TaskbarArgbColor(byte A, byte R, byte G, byte B)
{
    /// <summary>Parses exactly <c>#RRGGBB</c> (alpha 0xFF) or <c>#AARRGGBB</c>.</summary>
    public static bool TryParse(string? text, out TaskbarArgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.Length is not (7 or 9) || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return false;
        color = value.Length == 7
            ? new TaskbarArgbColor(0xFF, (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : new TaskbarArgbColor((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        return true;
    }

    /// <summary>Opaque RGB from a 24-bit value, the shape a palette sample or the dialog produces.</summary>
    public static TaskbarArgbColor FromRgb(uint rgb) =>
        new(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>The 24-bit RGB part; alpha is deliberately excluded from preset matching.</summary>
    public uint Rgb => (uint)((R << 16) | (G << 8) | B);

    public bool SameRgb(TaskbarArgbColor other) => R == other.R && G == other.G && B == other.B;

    public TaskbarArgbColor WithRgb(uint rgb) => new(A, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public TaskbarArgbColor WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>
    /// Stored form: <c>#RRGGBB</c> when the colour is fully opaque, otherwise <c>#AARRGGBB</c>.
    /// The dialog only rewrites a value it actually edited; an untouched setting keeps its
    /// original file text.
    /// </summary>
    public string ToHex() => A == 0xFF
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Pure state of the one taskbar colour card: two colour values (text and background), the target
/// currently being edited, the background's remembered RGB and the last advanced-hex input result.
/// It has no WPF dependency and never writes a settings file, so the target separation, the
/// alpha-preserving behaviour and the invalid-input rules are unit-tested without a window.
/// </summary>
public sealed class TaskbarColorEditorState
{
    /// <summary>The background's canonical fully transparent value written by the 完全透明 button.</summary>
    public const string FullyTransparentHex = "#00000000";

    public const string InvalidHexMessage = "色号必须是 #RRGGBB 或 #AARRGGBB。";

    private string _textHex;
    private string _backgroundHex;
    // Kept even when the background becomes the canonical #00000000 (which has no meaningful RGB),
    // so moving the opacity slider back above zero restores the RGB the user last picked.
    private uint _backgroundRgbMemory;

    public TaskbarColorEditorState(string textHex, string backgroundHex)
    {
        if (!TaskbarArgbColor.TryParse(textHex, out var text))
            throw new ArgumentException("Text colour must be #RRGGBB or #AARRGGBB.", nameof(textHex));
        if (!TaskbarArgbColor.TryParse(backgroundHex, out var background))
            throw new ArgumentException("Background colour must be #RRGGBB or #AARRGGBB.", nameof(backgroundHex));
        _textHex = textHex.Trim();
        _backgroundHex = backgroundHex.Trim();
        // A stored fully transparent background carries no visible RGB; remember black until the
        // user picks a palette colour, which then becomes the RGB restored by the slider.
        _backgroundRgbMemory = background.Rgb;
    }

    public TaskbarColorTarget ActiveTarget { get; private set; } = TaskbarColorTarget.Text;

    /// <summary>The committed text colour in its stored form (never rewritten by a target switch).</summary>
    public string TextHex => _textHex;

    /// <summary>The committed background colour in its stored form.</summary>
    public string BackgroundHex => _backgroundHex;

    public TaskbarArgbColor TextColor => Parse(_textHex);

    public TaskbarArgbColor BackgroundColor => Parse(_backgroundHex);

    public TaskbarArgbColor ActiveColor => ActiveTarget == TaskbarColorTarget.Text ? TextColor : BackgroundColor;

    public string ActiveHex => ActiveTarget == TaskbarColorTarget.Text ? _textHex : _backgroundHex;

    /// <summary>Last RGB the user actually chose for the background (used after 完全透明).</summary>
    public uint BackgroundRgbMemory => _backgroundRgbMemory;

    /// <summary>0..100, derived from the committed background alpha.</summary>
    public int BackgroundOpacityPercent => (int)Math.Round(BackgroundColor.A * 100.0 / 255.0, MidpointRounding.AwayFromZero);

    public bool BackgroundIsFullyTransparent => BackgroundColor.A == 0;

    /// <summary>
    /// Set when the last advanced-hex commit was rejected. Such input is never written to the
    /// committed values, and it blocks a target switch and a save until it is fixed or cancelled.
    /// </summary>
    public string? AdvancedError { get; private set; }

    public bool HasPendingInvalidInput => AdvancedError is not null;

    /// <summary>
    /// Switches the edited target. It never touches either colour; it only refuses while the active
    /// advanced editor holds text that was already rejected, so invalid text cannot leak into the
    /// other target.
    /// </summary>
    public bool TrySelectTarget(TaskbarColorTarget target)
    {
        if (!Enum.IsDefined(target)) return false;
        if (HasPendingInvalidInput) return false;
        ActiveTarget = target;
        return true;
    }

    /// <summary>
    /// Commits the advanced hex box for the active target. An invalid value leaves both stored
    /// colours untouched and sets <see cref="AdvancedError"/>; the caller shows it inline instead of
    /// previewing or saving a half-typed value.
    /// </summary>
    public bool TryCommitAdvanced(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (!TaskbarArgbColor.TryParse(trimmed, out var color))
        {
            AdvancedError = InvalidHexMessage;
            return false;
        }
        AdvancedError = null;
        // Re-typing the value that is already committed (possibly in the file's original
        // #RRGGBB/#AARRGGBB spelling) must not rewrite the string or mark the dialog dirty.
        if (string.Equals(trimmed, ActiveHex, StringComparison.OrdinalIgnoreCase)) return true;
        StoreActive(color);
        return true;
    }

    /// <summary>Discards a rejected advanced edit; the committed colour is shown again.</summary>
    public void CancelAdvanced() => AdvancedError = null;

    /// <summary>
    /// Applies RGB to the edited colour. RGB presets and the system colour dialog use this path, so
    /// alpha is always preserved: a transparent background stays transparent, a semi-transparent
    /// one keeps its alpha, and a deliberately translucent text colour keeps its alpha too.
    /// </summary>
    public bool TryApplyRgb(uint rgb)
    {
        if (rgb > 0xFFFFFF) return false;
        if (ActiveTarget == TaskbarColorTarget.Background) _backgroundRgbMemory = rgb;
        AdvancedError = null;
        StoreActive(ActiveColor.WithRgb(rgb));
        return true;
    }

    /// <summary>
    /// Background opacity slider in percent. 0 is the canonical <c>#00000000</c> and keeps the
    /// remembered RGB; any value above 0 rebuilds the colour from that remembered RGB, so a
    /// transparent background never silently loses the chosen colour.
    /// </summary>
    public bool TrySetBackgroundOpacity(int percent)
    {
        if (percent is < 0 or > 100) return false;
        AdvancedError = null;
        if (percent == 0)
        {
            RememberBackgroundRgb();
            _backgroundHex = FullyTransparentHex;
            return true;
        }
        RememberBackgroundRgb();
        var alpha = (byte)Math.Round(percent * 255.0 / 100.0, MidpointRounding.AwayFromZero);
        _backgroundHex = TaskbarArgbColor.FromRgb(_backgroundRgbMemory).WithAlpha(alpha).ToHex();
        return true;
    }

    /// <summary>
    /// The 完全透明 button: writes exactly <c>#00000000</c> (the value the overlay already treats as
    /// "draw no background") and keeps the last RGB for the slider.
    /// </summary>
    public void SetBackgroundFullyTransparent()
    {
        AdvancedError = null;
        RememberBackgroundRgb();
        _backgroundHex = FullyTransparentHex;
    }

    /// <summary>RGB-only preset match: alpha is irrelevant, so #FF0000 and #FFFF0000 select the same sample.</summary>
    public bool IsActiveRgbSelected(uint rgb) => ActiveColor.Rgb == rgb;

    private void RememberBackgroundRgb()
    {
        var current = BackgroundColor;
        if (current.A != 0) _backgroundRgbMemory = current.Rgb;
    }

    private void StoreActive(TaskbarArgbColor color)
    {
        var hex = color.ToHex();
        if (ActiveTarget == TaskbarColorTarget.Text) _textHex = hex;
        else _backgroundHex = hex;
    }

    private static TaskbarArgbColor Parse(string hex)
    {
        if (!TaskbarArgbColor.TryParse(hex, out var color))
            throw new InvalidOperationException("A committed taskbar colour became invalid.");
        return color;
    }
}
