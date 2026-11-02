#requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = "$env:ProgramFiles\MiniProToolkit",
    [string]$PackageRoot,
    [switch]$RestoreEcAutomatic
)

. (Join-Path $PSScriptRoot 'package-common.ps1')
$packageRoot = if ([string]::IsNullOrWhiteSpace($PackageRoot)) { Get-PackageRoot (Join-Path $PSScriptRoot '..') } else { (Resolve-Path $PackageRoot).Path }
Assert-PackagePayload $packageRoot
$manifest = Get-ReleaseManifest $packageRoot
$resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
if (-not (Test-Path -LiteralPath $resolvedInstallRoot -PathType Container)) {
    throw "Installation root does not exist: $resolvedInstallRoot"
}
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    throw "Guardian service is not installed. Use install-package.ps1 for a new installation or repair-package.ps1 to repair this one."
}
if (-not $PSCmdlet.ShouldProcess($resolvedInstallRoot, "Upgrade MiniProToolkit to $($manifest.Version)")) { return }

# Fail-closed pre-flight (read-only): an active lease must not be cleared implicitly.
Assert-RestoreAuthorization -RestoreEcAutomatic:$RestoreEcAutomatic
# Lifecycle order: stop the application and confirm it is gone first; only then may EC automatic
# recovery write 101 and clear the lease. The Guardian service keeps running as the safety net
# until the lease is settled, because it performs the same recovery if the lease goes stale.
Stop-MiniProToolkitGracefully
Invoke-RestoreIfRequired -RestoreEcAutomatic:$RestoreEcAutomatic
Stop-GuardianService
$statePath = Join-Path $resolvedInstallRoot 'install-state.json'
$previousState = if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw } else { $null }
$backupRoot = $null
try {
    $backupRoot = Copy-Payload $packageRoot $resolvedInstallRoot -KeepBackup
    Start-GuardianService
    Write-InstallState $resolvedInstallRoot $manifest
    New-StartMenuShortcut $resolvedInstallRoot
    Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Upgraded MiniProToolkit to $($manifest.Version). User data was preserved." -ForegroundColor Green
}
catch {
    $upgradeError = $_
    try {
        Stop-GuardianService
        if ($backupRoot -and (Test-Path -LiteralPath $backupRoot)) {
            Restore-Payload $resolvedInstallRoot $backupRoot
            if ($null -ne $previousState) { Set-Content -LiteralPath $statePath -Value $previousState -Encoding UTF8 }
            else { Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue }
        }
        Start-GuardianService
    }
    catch {
        $rollbackError = $_
        if ($backupRoot -and (Test-Path -LiteralPath $backupRoot)) {
            throw "Upgrade failed and rollback needs attention. Backup preserved at '$backupRoot'. Original error: $($upgradeError.Exception.Message). Rollback error: $($rollbackError.Exception.Message)"
        }
        throw "Upgrade failed and recovery needs attention. Original error: $($upgradeError.Exception.Message). Recovery error: $($rollbackError.Exception.Message)"
    }
    throw $upgradeError
}
