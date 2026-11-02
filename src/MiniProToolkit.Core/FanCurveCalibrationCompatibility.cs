namespace MiniProToolkit.Core;

/// <summary>
/// Startup-only compatibility policy for a saved curve and a replaced/fallback calibration.
/// Disables only explicit RPM targets when the otherwise valid curve cannot track on this
/// table. It never changes the caller's settings, duty safety line or other safety options.
/// Interactive application must instead call ValidateCalibration before saving or takeover.
/// </summary>
public static class FanCurveCalibrationCompatibility
{
    public static FanCurveSettings Resolve(
        FanCurveSettings settings, FanCalibration calibration, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(calibration);
        // Do not mask malformed safety settings or an invalid tracking/feedback combination.
        // The configuration store already handles structurally invalid saved curves.
        settings.Validate();
        warning = null;
        if (!settings.HasExplicitRpmTargets)
            return settings;

        try
        {
            settings.ValidateCalibration(calibration);
            return settings;
        }
        catch (ArgumentException ex)
        {
            warning = $"显式目标 RPM 与当前标定不兼容，已禁用显式 RPM 跟踪；保留温度 duty 安全曲线及全部安全设置。{ex.Message}";
            return settings with
            {
                Points = Array.AsReadOnly(settings.Points.Select(point => point with { TargetRpm = null }).ToArray())
            };
        }
    }
}
