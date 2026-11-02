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

    public GuardianWorker(
        GuardianWorkerOptions options,
        GuardianRecoveryCoordinator coordinator,
        ILogger<GuardianWorker> logger)
    {
        _options = options;
        _coordinator = coordinator;
        _logger = logger;
        _leaseStore = new GuardianLeaseStore(options.LeasePath);
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
        var lease = _leaseStore.Read();
        var decision = _coordinator.Evaluate(
            lease,
            DateTimeOffset.UtcNow,
            IsProcessAlive,
            GetProcessStartUtc);
        if (decision.Action != GuardianRecoveryAction.RecoverAutomatic || lease is null)
            return;

        // Re-read before writing: a newly started owner may have replaced an old lease.
        var currentLease = _leaseStore.Read();
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

    private void LogRecovery(string message, Exception exception)
    {
        _logger.LogError(exception, "{Message}", message);
        try
        {
            var directory = Path.GetDirectoryName(_options.LogPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(_options.LogPath,
                $"{DateTimeOffset.UtcNow:O} {message}: {exception.GetBaseException().Message}{Environment.NewLine}");
        }
        catch
        {
            // The service logger remains the fallback when the file is unavailable.
        }
    }
}
