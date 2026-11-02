namespace MiniProToolkit.Core;

public sealed record FanCalibrationPoint(int DutyPercent, int Rpm)
{
    public void Validate()
    {
        if (DutyPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(DutyPercent));
        if (Rpm is < 0 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(Rpm));
    }
}

public sealed class FanCalibration
{
    public FanCalibration(IEnumerable<FanCalibrationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        Points = Array.AsReadOnly(points.ToArray());
        Validate();
    }

    public IReadOnlyList<FanCalibrationPoint> Points { get; }

    public static FanCalibration Default { get; } = new([
        new(20, 1200),
        new(50, 2536),
        new(80, 3520),
        new(100, 4083)
    ]);

    public void Validate()
    {
        if (Points.Count is < 2 or > 64)
            throw new ArgumentException("标定表需要 2–64 个点。", nameof(Points));

        var previousDuty = -1;
        var previousRpm = -1;
        foreach (var point in Points)
        {
            if (point is null)
                throw new ArgumentException("标定点不能为空。", nameof(Points));
            point.Validate();
            if (point.DutyPercent <= previousDuty)
                throw new ArgumentException("Calibration duty values must be strictly increasing.", nameof(Points));
            if (point.Rpm < previousRpm)
                throw new ArgumentException("Calibration RPM values must be nondecreasing.", nameof(Points));
            previousDuty = point.DutyPercent;
            previousRpm = point.Rpm;
        }
    }

    public void ValidateForFixedRpm()
    {
        Validate();
        if (Points[0].DutyPercent > 40 || Points[^1].DutyPercent != 100)
            throw new ArgumentException("固定 RPM 标定必须覆盖 40%–100% duty。", nameof(Points));
        if (RpmForDuty(40) <= 100 || RpmForDuty(100) <= RpmForDuty(40))
            throw new ArgumentException("40% duty 的 RPM 必须大于 100，且 100% duty 的 RPM 必须更高。", nameof(Points));
    }

    public int RpmForDuty(int dutyPercent)
    {
        var duty = Math.Clamp(dutyPercent, Points[0].DutyPercent, Points[^1].DutyPercent);
        return InterpolateByDuty(duty);
    }

    public int DutyForRpm(int rpm)
    {
        var target = Math.Clamp(rpm, Points[0].Rpm, Points[^1].Rpm);
        for (var index = 1; index < Points.Count; index++)
        {
            var upper = Points[index];
            if (target > upper.Rpm)
                continue;
            var lower = Points[index - 1];
            if (upper.Rpm == lower.Rpm)
                return upper.DutyPercent;
            var ratio = (target - lower.Rpm) / (double)(upper.Rpm - lower.Rpm);
            return (int)Math.Round(lower.DutyPercent + (upper.DutyPercent - lower.DutyPercent) * ratio);
        }
        return Points[^1].DutyPercent;
    }

    private int InterpolateByDuty(int duty)
    {
        for (var index = 1; index < Points.Count; index++)
        {
            var upper = Points[index];
            if (duty > upper.DutyPercent)
                continue;
            var lower = Points[index - 1];
            var ratio = (duty - lower.DutyPercent) / (double)(upper.DutyPercent - lower.DutyPercent);
            return (int)Math.Round(lower.Rpm + (upper.Rpm - lower.Rpm) * ratio);
        }
        return Points[^1].Rpm;
    }
}

public sealed record FanRpmTargetResult(
    int RequestedRpm,
    int ClampedRpm,
    int DutyPercent,
    bool WasClamped);

public sealed class FanRpmTargetController
{
    private readonly FanCalibration _calibration;
    private readonly int _minimumDuty;
    private readonly int _maximumDuty;

    public FanRpmTargetController(
        FanCalibration? calibration = null,
        int minimumDuty = 40,
        int maximumDuty = 100)
    {
        _calibration = calibration ?? FanCalibration.Default;
        if (minimumDuty is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(minimumDuty));
        if (maximumDuty < minimumDuty || maximumDuty > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumDuty));
        _minimumDuty = minimumDuty;
        _maximumDuty = maximumDuty;
    }

    public FanRpmTargetResult Resolve(int requestedRpm)
    {
        var minRpm = _calibration.RpmForDuty(_minimumDuty);
        var maxRpm = _calibration.RpmForDuty(_maximumDuty);
        var clampedRpm = Math.Clamp(requestedRpm, minRpm, maxRpm);
        var duty = Math.Clamp(_calibration.DutyForRpm(clampedRpm), _minimumDuty, _maximumDuty);
        return new(requestedRpm, clampedRpm, duty, clampedRpm != requestedRpm);
    }
}
