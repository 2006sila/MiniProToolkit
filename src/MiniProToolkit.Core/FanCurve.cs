namespace MiniProToolkit.Core;

public sealed record FanCurvePoint(
    double TemperatureC,
    int DutyPercent,
    double? RampDownTemperatureC = null,
    int? TargetRpm = null)
{
    public void Deconstruct(out double temperatureC, out int dutyPercent, out double? rampDownTemperatureC)
    {
        temperatureC = TemperatureC;
        dutyPercent = DutyPercent;
        rampDownTemperatureC = RampDownTemperatureC;
    }
}

public enum FanCurveAction
{
    NoChange,
    SetDuty,
    RestoreAutomatic
}

/// <summary>
/// Graded temperature response reported by <see cref="FanCurveEngine"/>. Normal and
/// Emergency always exist; Warning and PreEmergency only become reachable when the
/// matching optional tier is configured. Emergency stays the fixed 90 °C full-speed
/// protection and is never replaced by a tier.
/// </summary>
public enum FanTemperatureLevel
{
    Normal = 0,
    Warning = 1,
    PreEmergency = 2,
    Emergency = 3
}

public sealed record FanCurveDecision(FanCurveAction Action, int? DutyPercent, string Reason)
{
    public FanTemperatureLevel Level { get; init; }
    public int? TargetRpm { get; init; }
    public int? ActualRpm { get; init; }
    /// <summary>Target minus actual RPM; null when feedback is unavailable.</summary>
    public int? RpmError { get; init; }
    public FanCurveRpmTrackingState TrackingState { get; init; }

    /// <summary>
    /// True while the optional 90 °C emergency latch holds full speed even though the CPU has
    /// cooled down again. It is only cleared by the curve controller's Disable/Restore path.
    /// </summary>
    public bool EmergencyLatched { get; init; }

    /// <summary>
    /// True when this restore decision came from the optional RPM feedback loop (stall, unreachable
    /// target or lost feedback) instead of an unusable temperature sample. The status formatter
    /// uses it to keep the two restore causes apart.
    /// </summary>
    public bool RpmFeedbackFault { get; init; }

    public static FanCurveDecision NoChange(string reason) => new(FanCurveAction.NoChange, null, reason);
    public static FanCurveDecision SetDuty(int duty, string reason) => new(FanCurveAction.SetDuty, duty, reason);
    public static FanCurveDecision Restore(string reason) => new(FanCurveAction.RestoreAutomatic, null, reason);
}

/// <summary>
/// Optional graded temperature tier: once the CPU reaches <see cref="TemperatureC"/>
/// the curve target is raised to at least <see cref="DutyPercent"/>. A tier can only
/// add cooling; it is never allowed to sit below the curve duty at the same temperature.
/// </summary>
public sealed record FanTemperatureTier(double TemperatureC, int DutyPercent)
{
    public void Validate()
    {
        if (!double.IsFinite(TemperatureC) || TemperatureC is < 1 or > 90)
            throw new ArgumentOutOfRangeException(nameof(TemperatureC));
        if (DutyPercent is < 40 or > 100)
            throw new ArgumentOutOfRangeException(nameof(DutyPercent));
    }
}

/// <summary>
/// Optional fan RPM-per-second rate limit. Units are fan RPM per second (fan 1 actual
/// feedback, <see cref="HardwareSnapshot.Fan1Rpm"/> tracked between snapshots); this is
/// NOT a duty-per-tick limit. It only engages while real RPM feedback is available and the
/// accumulated sampling window is at least <see cref="MinimumSampleSeconds"/>; otherwise it
/// stays silent and the existing duty-per-tick limits remain the only rate control. Shorter
/// intermediate samples never move the window anchor, so a burst of short samples cannot
/// reset the baseline and slip past the limit.
/// </summary>
public sealed record FanRpmRateLimit(
    int MaximumRiseRpmPerSecond = 600,
    int MaximumFallRpmPerSecond = 1200,
    double MinimumSampleSeconds = 1)
{
    public void Validate()
    {
        if (MaximumRiseRpmPerSecond is < 50 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(MaximumRiseRpmPerSecond));
        if (MaximumFallRpmPerSecond is < 50 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(MaximumFallRpmPerSecond));
        if (!double.IsFinite(MinimumSampleSeconds) || MinimumSampleSeconds is < 0.25 or > 10)
            throw new ArgumentOutOfRangeException(nameof(MinimumSampleSeconds));
    }
}

public enum FanRpmRateLimitOutcome
{
    RpmUnavailable,
    SampleIntervalTooShort,
    WithinLimit,
    RiseLimited,
    FallLimited
}

public sealed record FanRpmRateLimitDecision(
    FanRpmRateLimitOutcome Outcome,
    double? MeasuredRpmPerSecond,
    bool AllowDutyChange,
    string Reason);

// Pure RPM/s calculation. Direction is +1 for a rising duty request, -1 for a falling one.
// Anything it cannot measure (missing RPM, too-short sample window) resolves to "allow",
// so an absent feedback sensor never changes the existing duty-based safety behaviour.
public static class FanRpmRateLimiter
{
    public static FanRpmRateLimitDecision Evaluate(
        FanRpmRateLimit settings,
        int direction,
        int? previousRpm,
        int? currentRpm,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (previousRpm is not { } previous || currentRpm is not { } current ||
            previous is < 0 or > 20000 || current is < 0 or > 20000)
            return new(FanRpmRateLimitOutcome.RpmUnavailable, null, true,
                "RPM 不可用，沿用原有 duty 升降温限制");
        if (elapsed <= TimeSpan.Zero || elapsed.TotalSeconds < settings.MinimumSampleSeconds)
            return new(FanRpmRateLimitOutcome.SampleIntervalTooShort, null, true,
                "RPM 采样间隔不足，沿用原有 duty 升降温限制");

        var rate = (current - previous) / elapsed.TotalSeconds;
        if (direction > 0 && rate > settings.MaximumRiseRpmPerSecond)
            return new(FanRpmRateLimitOutcome.RiseLimited, rate, false,
                $"RPM 上升 {rate:0} RPM/s 超过 {settings.MaximumRiseRpmPerSecond} RPM/s，本轮保持 duty");
        if (direction < 0 && -rate > settings.MaximumFallRpmPerSecond)
            return new(FanRpmRateLimitOutcome.FallLimited, rate, false,
                $"RPM 下降 {-rate:0} RPM/s 超过 {settings.MaximumFallRpmPerSecond} RPM/s，本轮保持 duty");

        return new(FanRpmRateLimitOutcome.WithinLimit, rate, true, "RPM 变化率在限制内");
    }
}

public sealed record FanCurveSettings(
    IReadOnlyList<FanCurvePoint> Points,
    int MinimumDutyPercent = 40,
    int MaximumDutyPercent = 100,
    int EmergencyDutyPercent = 100,
    double HysteresisC = 2,
    int MaximumDutyChangePerTick = 10,
    double EmergencyTemperatureC = 90,
    int? MaximumDutyIncreasePerTick = null,
    int? MaximumDutyDecreasePerTick = null,
    FanRpmRateLimit? RpmRateLimit = null,
    FanTemperatureTier? WarningTier = null,
    FanTemperatureTier? PreEmergencyTier = null,
    FanCurveRpmFeedbackSettings? RpmFeedback = null,
    bool LatchCurveEmergency = false)
{
    // Conservative shipped template used for the built-in profiles, the curve editor reset and
    // the store fallback when no valid file exists. The graded tiers only ever add cooling on top
    // of the curve and stay strictly below the fixed 90 C / 100 % emergency protection, so this
    // template can never weaken the emergency path. RpmRateLimit stays null on purpose: the
    // RPM/s limit depends on real fan feedback and is opt-in only.
    public const double DefaultWarningTierTemperatureC = 70;
    public const int DefaultWarningTierDutyPercent = 80;
    public const double DefaultPreEmergencyTierTemperatureC = 82;
    public const int DefaultPreEmergencyTierDutyPercent = 100;

    public static FanCurveSettings Default => new(
        Array.AsReadOnly<FanCurvePoint>([
            new(45, 40, 43), new(55, 45, 53), new(65, 55, 63),
            new(75, 70, 73), new(85, 85, 83), new(90, 100, 88)
        ]),
        WarningTier: new FanTemperatureTier(
            DefaultWarningTierTemperatureC, DefaultWarningTierDutyPercent),
        PreEmergencyTier: new FanTemperatureTier(
            DefaultPreEmergencyTierTemperatureC, DefaultPreEmergencyTierDutyPercent));

    public int EffectiveDutyIncreasePerTick => MaximumDutyIncreasePerTick ?? MaximumDutyChangePerTick;
    public int EffectiveDutyDecreasePerTick => MaximumDutyDecreasePerTick ?? MaximumDutyChangePerTick;

    public bool HasGradedTemperatureTiers => WarningTier is not null || PreEmergencyTier is not null;

    /// <summary>RPM feedback is opt-in and off by default.</summary>
    public bool HasRpmFeedback => RpmFeedback is not null;
    public bool HasExplicitRpmTargets => Points.Any(point => point?.TargetRpm is not null);

    public void ValidateCalibration(FanCalibration calibration)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(calibration);
        if (!HasExplicitRpmTargets) return;
        calibration.ValidateForFixedRpm();
        // Reachability describes the calibrated safe hardware envelope, not the independent
        // temperature floor: a higher configured floor may intentionally constrain a low target.
        var minimum = calibration.RpmForDuty(40);
        var maximum = calibration.RpmForDuty(100);
        if (Points.Any(point => point.TargetRpm < minimum || point.TargetRpm > maximum))
            throw new ArgumentException($"Explicit RPM targets must be within actual calibration range {minimum}-{maximum} RPM.", nameof(Points));
    }

    /// <summary>Ramp-up duty the curve commands at <paramref name="temperatureC"/>.</summary>
    public int CurveDutyAt(double temperatureC)
    {
        if (!double.IsFinite(temperatureC))
            throw new ArgumentOutOfRangeException(nameof(temperatureC));
        if (Points is null || Points.Count == 0)
            return MinimumDutyPercent;
        if (temperatureC <= Points[0].TemperatureC)
            return Points[0].DutyPercent;

        for (var index = 1; index < Points.Count; index++)
        {
            var upper = Points[index];
            if (temperatureC > upper.TemperatureC)
                continue;
            var lower = Points[index - 1];
            var span = upper.TemperatureC - lower.TemperatureC;
            if (span <= 0)
                return upper.DutyPercent;
            var ratio = (temperatureC - lower.TemperatureC) / span;
            return (int)Math.Round(lower.DutyPercent + (upper.DutyPercent - lower.DutyPercent) * ratio);
        }
        return Points[^1].DutyPercent;
    }

    public void Validate()
    {
        if (Points is null || Points.Count == 0)
            throw new ArgumentException("At least one fan curve point is required.", nameof(Points));
        if (MinimumDutyPercent is < 40 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MinimumDutyPercent));
        if (MaximumDutyPercent != 100 || EmergencyDutyPercent != 100)
            throw new ArgumentException("Maximum and emergency duty must be 100 percent.");
        if (!double.IsFinite(HysteresisC) || HysteresisC is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(HysteresisC));
        if (MaximumDutyChangePerTick is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MaximumDutyChangePerTick));
        if (EffectiveDutyIncreasePerTick is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MaximumDutyIncreasePerTick));
        if (EffectiveDutyDecreasePerTick is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MaximumDutyDecreasePerTick));
        if (!double.IsFinite(EmergencyTemperatureC) || EmergencyTemperatureC is < 60 or > 90)
            throw new ArgumentOutOfRangeException(nameof(EmergencyTemperatureC));

        var previousTemperature = double.MinValue;
        var previousRampDown = double.MinValue;
        var previousDuty = MinimumDutyPercent;
        var explicitTargets = HasExplicitRpmTargets;
        var previousRpm = 0;
        if (explicitTargets && RpmFeedback is not null)
            throw new ArgumentException("Explicit RPM tracking and legacy RpmFeedback cannot both be enabled.", nameof(RpmFeedback));
        foreach (var point in Points)
        {
            if (point is null)
                throw new ArgumentException("Fan curve points cannot be null.", nameof(Points));
            if (!double.IsFinite(point.TemperatureC) ||
                point.TemperatureC <= previousTemperature || point.TemperatureC is < 1 or > 90)
                throw new ArgumentException("Temperatures must be finite, strictly increasing and within 1-90 C.", nameof(Points));
            if (point.DutyPercent < previousDuty || point.DutyPercent > MaximumDutyPercent)
                throw new ArgumentException("Duty must be nondecreasing and within the safety range.", nameof(Points));

            var rampDown = point.RampDownTemperatureC ?? point.TemperatureC - HysteresisC;
            if (!double.IsFinite(rampDown) || rampDown < 1 || rampDown > point.TemperatureC || rampDown <= previousRampDown)
                throw new ArgumentException("Ramp-down temperatures must be increasing and no higher than ramp-up temperatures.", nameof(Points));

            if (explicitTargets)
            {
                if (point.TargetRpm is not { } rpm || rpm is < 1 or > 10000 || rpm < previousRpm)
                    throw new ArgumentException("Explicit RPM targets must be present on every point, within 1-10000 RPM and nondecreasing.", nameof(Points));
                previousRpm = rpm;
            }
            previousTemperature = point.TemperatureC;
            previousRampDown = rampDown;
            previousDuty = point.DutyPercent;
        }
        if (Points[^1].TemperatureC > EmergencyTemperatureC)
            throw new ArgumentException("The last curve point cannot exceed the emergency temperature.", nameof(Points));

        RpmRateLimit?.Validate();
        RpmFeedback?.Validate();
        ValidateGradedTiers();
    }

    // Tiers may only add cooling and must stay strictly below the fixed emergency threshold,
    // so adding them can never weaken the existing 90 °C full-speed protection.
    private void ValidateGradedTiers()
    {
        if (WarningTier is { } warning)
        {
            warning.Validate();
            ValidateTierAgainstCurve(warning, nameof(WarningTier));
        }
        if (PreEmergencyTier is { } preEmergency)
        {
            preEmergency.Validate();
            ValidateTierAgainstCurve(preEmergency, nameof(PreEmergencyTier));
        }
        if (WarningTier is { } lower && PreEmergencyTier is { } upper)
        {
            if (upper.TemperatureC <= lower.TemperatureC)
                throw new ArgumentException("The pre-emergency tier must be hotter than the warning tier.", nameof(PreEmergencyTier));
            if (lower.DutyPercent > upper.DutyPercent)
                throw new ArgumentException("The warning tier duty cannot exceed the pre-emergency tier duty.", nameof(WarningTier));
        }
    }

    private void ValidateTierAgainstCurve(FanTemperatureTier tier, string parameterName)
    {
        if (tier.TemperatureC >= EmergencyTemperatureC)
            throw new ArgumentException(
                "Tier temperatures must stay below the emergency temperature; the 90 C full-speed threshold is fixed.",
                parameterName);
        var curveDuty = CurveDutyAt(tier.TemperatureC);
        if (tier.DutyPercent < curveDuty)
            throw new ArgumentException(
                $"Tier duty cannot fall below the curve duty ({curveDuty}%) at {tier.TemperatureC:0.##} C.",
                parameterName);
    }
}

// Stateful evaluation only; the caller must serialize evaluations and hardware writes.
public sealed class FanCurveEngine
{
    // Upper bound for a single RPM/s window; longer gaps are treated as a fresh baseline so a
    // paused caller cannot hide a jump inside one averaged interval. Matches the largest value
    // FanRpmRateLimit.Validate accepts for MinimumSampleSeconds.
    private const double MaximumRpmWindowSeconds = 10;

    private readonly FanCurveSettings _settings;
    private readonly FanCalibration _calibration;
    private int? _lastDuty;
    private int? _targetDuty;
    private double? _lastTemperature;
    private FanTemperatureLevel _activeLevel;
    private bool _emergencyLatched;
    private int? _rpmWindowAnchorRpm;
    private DateTimeOffset? _rpmWindowAnchorTimestamp;
    private readonly FanCurveRpmFeedbackController? _rpmFeedback;
    private readonly FanCurveRpmTrackingController? _rpmTracking;
    // Independent legacy evaluation preserves the temperature/tier/ramp/RPM-rate safety floor.
    private readonly FanCurveEngine? _temperatureFloor;
    private int? _targetRpm;

    /// <param name="settings">Validated curve settings.</param>
    /// <param name="calibration">
    /// The application's real fan calibration (duty to RPM). Passing null keeps the shipped
    /// <see cref="FanCalibration.Default" /> table, so every existing two-argument-free call site
    /// keeps its previous behaviour.
    /// </param>
    public FanCurveEngine(FanCurveSettings settings, FanCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings with { Points = Array.AsReadOnly(settings.Points.ToArray()) };
        _calibration = calibration ?? FanCalibration.Default;
        _settings.ValidateCalibration(_calibration);
        if (_settings.HasExplicitRpmTargets)
        {
            _rpmTracking = new FanCurveRpmTrackingController(_calibration);
            _temperatureFloor = new FanCurveEngine(_settings with
            {
                Points = Array.AsReadOnly(_settings.Points.Select(point => point with { TargetRpm = null }).ToArray())
            }, _calibration);
        }
        _rpmFeedback = _settings.RpmFeedback is { } feedback
            ? new FanCurveRpmFeedbackController(feedback, _calibration)
            : null;
    }

    /// <summary>Calibration used to turn a curve duty target into the expected RPM.</summary>
    public FanCalibration Calibration => _calibration;

    public int? CurrentDuty => _lastDuty;
    public int? TargetDuty => _targetDuty;
    public FanTemperatureLevel ActiveLevel => _activeLevel;

    /// <summary>
    /// True while the optional emergency latch keeps the curve at full speed after the CPU has
    /// cooled down. Only the controller's Disable/Restore path clears it, so a temperature dip
    /// can never release the 90 °C protection on its own.
    /// </summary>
    public bool EmergencyLatched => _settings.LatchCurveEmergency && _emergencyLatched;

    /// <summary>
    /// Clears the running curve state. <paramref name="clearEmergencyLatch"/> also releases the
    /// optional emergency latch and must only be used when control is handed back (controller
    /// Disable/Restore), never for a transient bad sensor sample.
    /// </summary>
    public void Reset(bool clearEmergencyLatch = false)
    {
        _lastDuty = null;
        _targetDuty = null;
        _lastTemperature = null;
        _activeLevel = FanTemperatureLevel.Normal;
        if (clearEmergencyLatch)
            _emergencyLatched = false;
        ClearRpmWindow();
        _rpmFeedback?.Reset();
        _rpmTracking?.Reset();
        _temperatureFloor?.Reset(clearEmergencyLatch);
        _targetRpm = null;
    }

    public FanCurveDecision Evaluate(HardwareSnapshot snapshot, DateTimeOffset? now = null)
    {
        var age = (now ?? DateTimeOffset.UtcNow) - snapshot.Timestamp;
        if (snapshot.Error is not null || snapshot.CpuTemperatureC is not { } temperature ||
            !double.IsFinite(temperature) || temperature is <= 0 or > 125 ||
            age > TimeSpan.FromSeconds(5) || age < TimeSpan.FromSeconds(-1))
        {
            Reset();
            return FanCurveDecision.Restore("Invalid or stale temperature; automatic control required");
        }

        _temperatureFloor?.Evaluate(snapshot, now);
        var emergency = temperature >= _settings.EmergencyTemperatureC;
        if (emergency && _settings.LatchCurveEmergency)
            _emergencyLatched = true;
        // The latch holds the curve at the emergency target, so a cool-down after a real 90 °C
        // event never silently releases full speed. It outranks RPM feedback as well.
        var fullSpeed = emergency || EmergencyLatched;
        var level = fullSpeed ? FanTemperatureLevel.Emergency : ResolveLevel(temperature);

        if (fullSpeed)
        {
            _targetDuty = _settings.EmergencyDutyPercent;
        }
        else if (_targetDuty is null || level != _activeLevel || ShouldRecalculateTarget(temperature))
        {
            var rising = _lastTemperature is null || temperature >= _lastTemperature.Value;
            _targetDuty = ApplyTierFloor(Interpolate(temperature, rising), level);
            if (_rpmTracking is not null)
                _targetRpm = Interpolate(temperature, rising, rpm: true);
        }

        _lastTemperature = temperature;
        _activeLevel = level;

        // RPM feedback only ever adds duty on top of the curve target. Emergency/latched full
        // speed is computed first and never runs through the feedback path, so feedback cannot
        // slow the 90 °C protection down. A fault is reported as a restore decision; the caller
        // (FanCurveController) owns the automatic-restore/recovery handling.
        var target = _targetDuty!.Value;
        FanCurveRpmTrackingDecision? tracking = null;
        if (_rpmTracking is { } tracker && !fullSpeed)
        {
            tracking = tracker.Evaluate(_targetRpm!.Value, snapshot.Fan1Rpm, _temperatureFloor!.CurrentDuty!.Value);
            if (tracking.IsFault)
            {
                Reset();
                return new FanCurveDecision(FanCurveAction.RestoreAutomatic, null, tracking.Reason)
                {
                    Level = level, RpmFeedbackFault = true,
                    TargetRpm = tracking.TargetRpm, ActualRpm = tracking.ActualRpm,
                    RpmError = tracking.RpmError, TrackingState = tracking.State
                };
            }
            target = tracking.CommandDuty;
        }
        if (_rpmFeedback is { } feedback && !fullSpeed)
        {
            var verdict = feedback.Evaluate(target, snapshot.Fan1Rpm);
            if (verdict.IsFault)
            {
                Reset();
                return new FanCurveDecision(FanCurveAction.RestoreAutomatic, null, verdict.Reason)
                { Level = level, EmergencyLatched = EmergencyLatched, RpmFeedbackFault = true };
            }
            target = Math.Max(target, verdict.CommandDuty);
        }

        var duty = fullSpeed || _lastDuty is null
            ? target
            : MoveToward(_lastDuty.Value, target);
        var heldByRpmLimit = TryApplyRpmRateLimit(snapshot, ref duty, fullSpeed);
        if (_temperatureFloor?.CurrentDuty is { } floorDuty)
            duty = Math.Max(duty, floorDuty);

        FanCurveDecision WithTracking(FanCurveDecision decision) => decision with
        {
            TargetRpm = tracking?.TargetRpm,
            ActualRpm = tracking?.ActualRpm,
            RpmError = tracking?.RpmError,
            TrackingState = fullSpeed && _rpmTracking is not null
                ? FanCurveRpmTrackingState.EmergencyBypass
                : tracking?.State ?? FanCurveRpmTrackingState.Disabled
        };
        if (_lastDuty == duty)
            return WithTracking(new FanCurveDecision(FanCurveAction.NoChange, null,
                heldByRpmLimit ?? tracking?.Reason ?? "Target unchanged or within hysteresis band")
            { Level = level, EmergencyLatched = EmergencyLatched });

        _lastDuty = duty;
        return WithTracking(new FanCurveDecision(FanCurveAction.SetDuty, duty,
            fullSpeed ? DecisionReason(true, level) : tracking?.Reason ?? DecisionReason(false, level))
        { Level = level, EmergencyLatched = EmergencyLatched });
    }

    // Emergency full speed always bypasses the RPM/s limit, exactly like it bypasses the
    // duty-per-tick ramp. A missing RPM sample only means "no opinion": the duty limits stay.
    //
    // The window anchor is deliberately sticky: samples shorter than MinimumSampleSeconds only
    // observe the fan, they never move the anchor. The anchor is established by the first valid
    // sample, advanced once a complete window has elapsed, dropped when the sensor reports
    // nothing usable and re-established after a non-positive or absurdly long interval, so a
    // long pause is never averaged into one RPM/s reading.
    private string? TryApplyRpmRateLimit(HardwareSnapshot snapshot, ref int duty, bool emergency)
    {
        if (_settings.RpmRateLimit is not { } limit)
            return null;
        if (snapshot.Fan1Rpm is not { } rpm || rpm is < 0 or > 20000)
        {
            ClearRpmWindow();
            return null;
        }
        if (_rpmWindowAnchorRpm is not { } anchorRpm ||
            _rpmWindowAnchorTimestamp is not { } anchorTimestamp)
        {
            StartRpmWindow(rpm, snapshot.Timestamp);
            return null;
        }

        var elapsed = snapshot.Timestamp - anchorTimestamp;
        var maximumWindowSeconds = Math.Max(MaximumRpmWindowSeconds, limit.MinimumSampleSeconds);
        if (elapsed <= TimeSpan.Zero || elapsed.TotalSeconds > maximumWindowSeconds)
        {
            StartRpmWindow(rpm, snapshot.Timestamp);
            return null;
        }
        // Incomplete window: keep the anchor where it is and leave the duty to the existing
        // per-tick limits, exactly as the previous SampleIntervalTooShort outcome did.
        if (elapsed.TotalSeconds < limit.MinimumSampleSeconds)
            return null;

        string? holdReason = null;
        if (!emergency && _lastDuty is { } previousDuty && duty != previousDuty)
        {
            var decision = FanRpmRateLimiter.Evaluate(
                limit, duty > previousDuty ? 1 : -1, anchorRpm, rpm, elapsed);
            if (!decision.AllowDutyChange)
            {
                duty = previousDuty;
                holdReason = decision.Reason;
            }
        }
        // A complete window has been measured, so this sample becomes the next anchor.
        StartRpmWindow(rpm, snapshot.Timestamp);
        return holdReason;
    }

    private void StartRpmWindow(int rpm, DateTimeOffset timestamp)
    {
        _rpmWindowAnchorRpm = rpm;
        _rpmWindowAnchorTimestamp = timestamp;
    }

    private void ClearRpmWindow()
    {
        _rpmWindowAnchorRpm = null;
        _rpmWindowAnchorTimestamp = null;
    }

    private static string DecisionReason(bool emergency, FanTemperatureLevel level) => emergency
        ? "Emergency temperature limit reached"
        : level is FanTemperatureLevel.Warning or FanTemperatureLevel.PreEmergency
            ? $"Graded temperature level reached ({level})"
            : "Curve target changed";

    private int ApplyTierFloor(int curveDuty, FanTemperatureLevel level)
    {
        var tierDuty = level switch
        {
            FanTemperatureLevel.Warning => _settings.WarningTier?.DutyPercent,
            FanTemperatureLevel.PreEmergency => _settings.PreEmergencyTier?.DutyPercent,
            _ => null
        };
        // Math.Max keeps the tier additive: it can only raise duty above the curve.
        return tierDuty is { } floor ? Math.Max(curveDuty, floor) : curveDuty;
    }

    // Upgrade immediately at the tier temperature; drop back only once the temperature
    // falls a full hysteresis band below it, so a tier cannot flap the duty target.
    private FanTemperatureLevel ResolveLevel(double temperature)
    {
        if (_settings.PreEmergencyTier is { } preEmergency)
        {
            if (temperature >= preEmergency.TemperatureC)
                return FanTemperatureLevel.PreEmergency;
            if (_activeLevel == FanTemperatureLevel.PreEmergency &&
                temperature >= preEmergency.TemperatureC - _settings.HysteresisC)
                return FanTemperatureLevel.PreEmergency;
        }
        if (_settings.WarningTier is { } warning)
        {
            if (temperature >= warning.TemperatureC)
                return FanTemperatureLevel.Warning;
            if (_activeLevel == FanTemperatureLevel.Warning &&
                temperature >= warning.TemperatureC - _settings.HysteresisC)
                return FanTemperatureLevel.Warning;
        }
        return FanTemperatureLevel.Normal;
    }

    private bool ShouldRecalculateTarget(double temperature)
    {
        if (_lastTemperature is not { } previous)
            return true;
        return Math.Abs(temperature - previous) >= _settings.HysteresisC;
    }

    private int MoveToward(int current, int target)
    {
        var limit = target >= current
            ? _settings.EffectiveDutyIncreasePerTick
            : _settings.EffectiveDutyDecreasePerTick;
        return current + Math.Clamp(target - current, -limit, limit);
    }

    private int Interpolate(double temperature, bool rising, bool rpm = false)
    {
        int Value(FanCurvePoint point) => rpm ? point.TargetRpm!.Value : point.DutyPercent;
        var points = _settings.Points;
        if (points.Count == 1)
            return Value(points[0]);

        var firstThreshold = Threshold(points[0], rising);
        if (temperature <= firstThreshold)
            return Value(points[0]);

        for (var index = 1; index < points.Count; index++)
        {
            var upper = points[index];
            var upperThreshold = Threshold(upper, rising);
            if (temperature > upperThreshold)
                continue;
            var lower = points[index - 1];
            var lowerThreshold = Threshold(lower, rising);
            if (upperThreshold <= lowerThreshold)
                return Value(upper);
            var ratio = (temperature - lowerThreshold) / (upperThreshold - lowerThreshold);
            return (int)Math.Round(Value(lower) + (Value(upper) - Value(lower)) * ratio);
        }
        return Value(points[^1]);
    }

    private double Threshold(FanCurvePoint point, bool rising) =>
        rising ? point.TemperatureC : point.RampDownTemperatureC ?? point.TemperatureC - _settings.HysteresisC;
}
