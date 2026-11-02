namespace MiniProToolkit.Core;

public interface IFanProfileTarget
{
    void RestoreAutomatic();
    void SetPowerMode(PowerMode mode);
    void SetCurve(FanCurveSettings settings);
    void ApplyFanMode(FanProfile profile);
}

public sealed record FanProfileApplyResult(bool Success, bool RecoveryRequired, string Message);

public static class FanProfileApplication
{
    // Caller holds its hardware gate. Never restart an old manual mode during rollback.
    public static FanProfileApplyResult Apply(FanProfile profile, PowerMode previousMode,
        FanCurveSettings previousCurve, IFanProfileTarget target)
    {
        profile.Validate();
        try { target.RestoreAutomatic(); }
        catch (Exception ex)
        {
            return new(false, true, $"停止旧控制失败，未应用新方案：{ex.Message}");
        }
        try
        {
            target.SetPowerMode(profile.PowerMode);
            target.SetCurve(profile.FanCurve);
            target.ApplyFanMode(profile);
            return new(true, false, $"已应用 {profile.Name}：{profile.Summary}");
        }
        catch (Exception ex)
        {
            var errors = new List<string>();
            try { target.RestoreAutomatic(); }
            catch (Exception restore) { errors.Add($"EC 自动恢复失败：{restore.Message}"); }
            var recoveryRequired = errors.Count > 0;
            // Settings/controller replacement is unsafe while old control still needs recovery.
            if (!recoveryRequired)
            {
                try { target.SetCurve(previousCurve); }
                catch (Exception rollback) { errors.Add($"曲线配置回退失败：{rollback.Message}"); }
            }
            if (previousMode is PowerMode.Balance or PowerMode.Silence or PowerMode.Performance)
            {
                try { target.SetPowerMode(previousMode); }
                catch (Exception rollback) { errors.Add($"性能模式回退失败：{rollback.Message}"); }
            }
            return new(false, recoveryRequired,
                $"方案应用失败：{ex.Message}。" + (errors.Count == 0
                    ? "已回退原性能模式和曲线配置，并请求 EC 自动；旧手动控制未重新启用。"
                    : string.Join("；", errors)));
        }
    }
}
