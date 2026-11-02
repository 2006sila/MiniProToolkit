using System.ComponentModel;
using System.Diagnostics;
using MiniProToolkit.Core;
using Microsoft.Win32;

namespace MiniProToolkit.Hardware;

/// <summary>
/// Read-only CPU frequency reader. It prefers the Windows
/// <c>Processor Information\% Processor Performance</c> PerformanceCounter and
/// falls back to the legacy <c>Processor\% Processor Performance</c> counter. Both
/// are percent-of-nominal values, so the nominal clock is taken once from
/// <c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\~MHz</c> (read-only).
/// <para>
/// Nothing is written: no MSR, no EC register, no fan or power-mode call. Every
/// failure (missing counter, corrupted counter registry, missing base clock,
/// permission error) degrades to a null reading with a short status instead of an
/// exception, so this reader can be called from the sampling loop safely.
/// </para>
/// <para>
/// The first pass only establishes the rate-counter baseline and therefore returns
/// <c>null</c> for the current clock: a PerformanceCounter has no interval before
/// its second read and would otherwise report a fake 0 MHz.
/// </para>
/// </summary>
public sealed class CpuFrequencyReader : IDisposable
{
    private const int MaximumStatusLength = 160;

    /// <summary>
    /// Two counter reads closer than this cannot produce a meaningful rate, so the
    /// previous reading is returned instead of asking the counter too early.
    /// </summary>
    private static readonly TimeSpan MinimumCounterInterval = TimeSpan.FromMilliseconds(500);

    private static readonly (string Category, string Counter, CpuFrequencySource Source)[] Candidates =
    [
        ("Processor Information", "% Processor Performance", CpuFrequencySource.ProcessorInformationPercent),
        ("Processor", "% Processor Performance", CpuFrequencySource.ProcessorPercent)
    ];

    private const string CounterUnavailableStatus =
        "CPU 频率性能计数器不可用（可尝试 lodctr /R 重建计数器），频率显示为未知而不是 0。";

    private const string NotStartedStatus = "CPU 频率尚未采样";

    private readonly object _sync = new();
    private readonly CpuFrequencyCalculator _calculator = new();

    private PerformanceCounter? _counter;
    private CpuFrequencySource _source = CpuFrequencySource.Unavailable;
    private CpuFrequencyReading _lastReading = CpuFrequencyReading.Unavailable(NotStartedStatus);
    private TimeSpan? _lastCounterRead;
    private bool _counterUnavailable;
    private bool _baseClockResolved;
    private bool _disposed;

    public CpuFrequencyReading LastReading
    {
        get { lock (_sync) return _lastReading; }
    }

    /// <summary>Nominal clock in MHz from the registry, or null when it is not exposed.</summary>
    public double? BaseFrequencyMhz
    {
        get { lock (_sync) return _calculator.BaseFrequencyMhz; }
    }

    /// <summary>Which counter path is in use (<see cref="CpuFrequencySource.Unavailable"/> until the first read).</summary>
    public CpuFrequencySource Source
    {
        get { lock (_sync) return _source; }
    }

    /// <summary>Samples the current clock. Call from a background thread; never throws.</summary>
    public CpuFrequencyReading Read()
    {
        try
        {
            lock (_sync)
                return ReadCore();
        }
        catch (Exception ex)
        {
            // Last-resort guard: a type-load or interop failure must not reach the
            // sampling loop. The counter is dropped so the next call can rebuild it.
            lock (_sync)
            {
                DropCounter();
                _counterUnavailable = true;
                return _lastReading = CpuFrequencyReading.Unavailable(
                    Sanitize($"CPU 频率读取失败：{ex.GetBaseException().Message}"), _source);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            DropCounter();
            _lastReading = CpuFrequencyReading.Unavailable("CPU 频率读取已关闭", _source);
        }
    }

    private CpuFrequencyReading ReadCore()
    {
        if (_disposed)
            return _lastReading;

        EnsureBaseClock();

        if (_counterUnavailable)
            return _lastReading;

        if (_counter is null && !TryCreateCounter(out var createStatus))
        {
            _counterUnavailable = true;
            return _lastReading = CpuFrequencyReading.Unavailable(
                Sanitize($"{CounterUnavailableStatus}（{createStatus}）"), _source);
        }

        var now = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
        if (_lastCounterRead is { } last && now - last < MinimumCounterInterval)
            return _lastReading;
        _lastCounterRead = now;

        try
        {
            var raw = _counter!.NextValue();
            return _lastReading = _calculator.Observe(raw, _source);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or
            Win32Exception or PlatformNotSupportedException)
        {
            DropCounter();
            _counterUnavailable = true;
            return _lastReading = CpuFrequencyReading.Unavailable(
                Sanitize($"{CounterUnavailableStatus}（{ex.GetBaseException().Message}）"), _source);
        }
    }

    private bool TryCreateCounter(out string status)
    {
        status = string.Empty;
        foreach (var candidate in Candidates)
        {
            PerformanceCounter? counter = null;
            try
            {
                counter = new PerformanceCounter(candidate.Category, candidate.Counter, "_Total", readOnly: true);
                // The warm-up read validates that the counter really exists and consumes
                // its unavoidable first-sample baseline; the calculator still discards one
                // further sample, so no fake value can ever surface.
                _ = counter.NextValue();
                _counter = counter;
                _source = candidate.Source;
                status = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or
                Win32Exception or PlatformNotSupportedException or ArgumentException)
            {
                counter?.Dispose();
                status = $"{candidate.Category}/{candidate.Counter}: {ex.GetBaseException().Message}";
            }
        }

        status = string.IsNullOrWhiteSpace(status) ? "未找到可用计数器" : status;
        return false;    }

    private void DropCounter()
    {
        try { _counter?.Dispose(); }
        catch { /* best effort: the reader is optional and must never throw */ }
        _counter = null;
        _lastCounterRead = null;
        _calculator.Reset();
    }

    /// <summary>
    /// Nominal clock, read once. Without it the reader still reports
    /// <see cref="CpuFrequencyReading.PercentOfBase"/> but leaves the MHz value null
    /// instead of guessing a clock.
    /// </summary>
    private void EnsureBaseClock()
    {
        if (_baseClockResolved)
            return;
        _baseClockResolved = true;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            double? mhz = key?.GetValue("~MHz") switch
            {
                int value => value,
                long value => value,
                _ => null
            };
            _calculator.SetBaseFrequency(mhz);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or
            IOException)
        {
            // Registry read is optional: the percent value stays available.
            _calculator.SetBaseFrequency(null);
        }
    }

    private static string Sanitize(string text)
    {
        var redacted = DiagnosticRedactor.Redact(text);
        return redacted.Length <= MaximumStatusLength
            ? redacted
            : redacted[..MaximumStatusLength] + "…";
    }
}
