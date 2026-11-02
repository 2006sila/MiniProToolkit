using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniProToolkit.Core;
using MiniProToolkit.Hardware;

namespace MiniProToolkit.Guardian;

public sealed class GuardianWorker : BackgroundService
{
    private readonly GuardianWorkerOptions _options;
    private readonly GuardianRecoveryCoordinator _coordinator;
    private readonly ILogger<GuardianWorker> _logger;
    private readonly GuardianLeaseStore _leaseStore;
    // The recovery log lives under ProgramData and is only written by the LocalSystem service.
    // It is bounded exactly like the app log: 2 MiB live file + 3 rotated archives, 32 KiB per
    // entry, so a repeated poll/restore failure cannot grow it without limit. Path and directory
    // permissions are unchanged; rotation only renames files inside the existing directory.
    private readonly BoundedTextLog _recoveryLog;
    // Every poll is 2 s apart: a persistent unreadable lease (share violation, ACL, corrupt file)
    // would otherwise emit a warning plus a bounded-log and a diagnostic entry on every single poll.
    // The throttle keeps the first failure, a change of reason and then at most one entry per 30 s.
    private readonly GuardianLeaseReadWarningThrottle _leaseReadWarnings = new();

    public GuardianWorker(
        GuardianWorkerOptions options,
        GuardianRecoveryCoordinator coordinator,
        ILogger<GuardianWorker> logger)
    {
        _options = options;
        _coordinator = coordinator;
        _logger = logger;
        _leaseStore = new GuardianLeaseStore(options.LeasePath);
        _recoveryLog = new BoundedTextLog(
            options.LogPath,
            BoundedTextLog.DefaultMaximumFileBytes,
            BoundedTextLog.DefaultRetainedFiles,
            BoundedTextLog.DefaultMaximumEntryBytes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Guardian started. Lease: {LeasePath}", _leaseStore.Path);
        using var timer = new PeriodicTimer(_options.PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckLeaseAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRecovery("guardian poll failed", ex);
            }
        }
        _logger.LogInformation("Guardian stopped.");
    }

    private async Task CheckLeaseAsync(CancellationToken cancellationToken)
    {
        // An unreadable lease is not "no lease": the owner is unknown, so the decision is deferred
        // to the next poll instead of being evaluated against null (which would look like a healthy
        // "nothing to recover" and could let a corrupt record hide a live owner).
        var read = _leaseStore.ReadResult();
        if (read.Status == GuardianLeaseReadStatus.Unreadable)
        {
            ReportUnreadableLease("first", read);
            return;
        }
        ReportReadableLease();
        var lease = read.Lease;
        var decision = _coordinator.Evaluate(
            lease,
            DateTimeOffset.UtcNow,
            IsProcessAlive,
            GetProcessStartUtc);
        if (decision.Action != GuardianRecoveryAction.RecoverAutomatic || lease is null)
            return;

        // Re-read before writing: a newly started owner may have replaced an old lease. The second
        // read is typed as well - if the record became unreadable there is no identity to justify a
        // hardware write, so recovery is deferred with a bounded warning.
        var currentRead = _leaseStore.ReadResult();
        if (currentRead.Status == GuardianLeaseReadStatus.Unreadable)
        {
            ReportUnreadableLease("second", currentRead);
            return;
        }
        var currentLease = currentRead.Lease;
        if (currentLease is null || currentLease != lease)
            return;

        _logger.LogWarning("Recovering EC automatic control for PID {ProcessId}: {Reason}",
            lease.ProcessId, decision.Reason);
        AppendDiagnostic("Recovery", $"开始自动恢复：PID {lease.ProcessId} · {decision.Reason}");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await Task.Run(() =>
            {
                using var hardware = new PowerSwitchHardware();
                hardware.RestoreAutomatic();
            }, cancellationToken);
            // Compare-and-clear under the cross-process lease lock: the app may have published a
            // fresh lease while the restore was running, and that newer ownership must survive this
            // recovery. A busy lock is a safe no-op too - returning false only means "nothing was
            // deleted", so the lease is kept and the next poll retries the whole decision.
            if (_leaseStore.ClearOwned(lease))
            {
                _logger.LogInformation("EC automatic control restored.");
                AppendDiagnostic("Recovery", $"EC 自动恢复请求已完成：PID {lease.ProcessId}");
            }
            else
            {
                _logger.LogWarning(
                    "Lease was not cleared (changed or locked); ownership for PID {ProcessId} is retained.",
                    lease.ProcessId);
                AppendDiagnostic("Recovery", $"恢复期间租约未清理（已变更或忙），保留租约：旧 PID {lease.ProcessId}");
            }
        }
        catch (Exception ex)
        {
            LogRecovery("automatic fan restore failed; lease retained for retry", ex);
            AppendDiagnostic("Recovery", $"EC 自动恢复失败，租约保留重试：{ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Reports an unreadable lease once per throttle window. The message is bounded and path-free by
    /// construction: it carries only the fixed reason from
    /// <see cref="GuardianLeaseReadResult.Reason"/>, never the lease path or the file content.
    /// </summary>
    private void ReportUnreadableLease(string stage, GuardianLeaseReadResult result)
    {
        if (!_leaseReadWarnings.ShouldReport(result.Reason, DateTimeOffset.UtcNow))
            return;
        _logger.LogWarning(
            "Guardian lease is unreadable ({Stage} read): {Reason}. Recovery is deferred to the next poll.",
            stage, result.Reason);
        AppendRecoveryLine($"guardian lease unreadable ({stage} read): {result.Reason}; recovery deferred");
        AppendDiagnostic("Recovery", $"租约无法读取（{stage}），本轮跳过 EC 恢复并等待下一次轮询：{result.Reason}");
    }

    /// <summary>Clears the warning state after a readable poll and reports the recovery exactly once.</summary>
    private void ReportReadableLease()
    {
        if (!_leaseReadWarnings.Clear())
            return;
        _logger.LogInformation("Guardian lease is readable again; lease checks resumed.");
        AppendRecoveryLine("guardian lease readable again; lease checks resumed");
    }

    private static DateTimeOffset? GetProcessStartUtc(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void AppendDiagnostic(string category, string message)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(_options.LogPath)!, "diagnostic-events.jsonl");
            new DiagnosticEventStore(path).Append(category, message, "Guardian");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to append diagnostic event."); }
    }

    /// <summary>
    /// Appends one already-bounded line to the recovery log. Used by the unreadable-lease warning:
    /// the entry is shortened/rotated by <see cref="BoundedTextLog"/> and a failure to write must
    /// never break the poll loop, so it is swallowed like the error path below.
    /// </summary>
    private void AppendRecoveryLine(string message)
    {
        try
        {
            _recoveryLog.Append($"{DateTimeOffset.UtcNow:O} {message}");
        }
        catch
        {
            // The service logger remains the fallback when the file is unavailable.
        }
    }

    private void LogRecovery(string message, Exception exception)
    {
        _logger.LogError(exception, "{Message}", message);
        try
        {
            // Same line shape as before: "2026-11-02T17:03:04.5678901+00:00 message: reason".
            // The append (including its rotation rename) may fail on a concurrent rotation or a
            // locked file, and logging must never break the recovery loop, so keep the swallow.
            _recoveryLog.Append($"{DateTimeOffset.UtcNow:O} {message}: {exception.GetBaseException().Message}");
        }
        catch
        {
            // The service logger remains the fallback when the file is unavailable.
        }
    }
}
