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
if (-not $PSCmdlet.ShouldProcess($resolvedInstallRoot, "Repair MiniProToolkit $($manifest.Version)")) { return }

# Fail-closed pre-flight (read-only): an active lease must not be cleared implicitly.
Assert-RestoreAuthorization -RestoreEcAutomatic:$RestoreEcAutomatic
# Lifecycle order: stop the application and confirm it is gone first; only then may EC automatic
# recovery write 101 and clear the lease.
Stop-MiniProToolkitGracefully
Invoke-RestoreIfRequired -RestoreEcAutomatic:$RestoreEcAutomatic
$serviceExisted = [bool](Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
if ($serviceExisted) { Stop-GuardianService }
else { New-Item -ItemType Directory -Force -Path $resolvedInstallRoot | Out-Null }
$statePath = Join-Path $resolvedInstallRoot 'install-state.json'
$previousState = if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw } else { $null }
$backupRoot = $null
$serviceCreated = $false
try {
    $backupRoot = Copy-Payload $packageRoot $resolvedInstallRoot -KeepBackup
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        Install-GuardianService (Join-Path $resolvedInstallRoot 'Guardian\MiniProToolkit.Guardian.exe')
        $serviceCreated = $true
    }
    Start-GuardianService
    Write-InstallState $resolvedInstallRoot $manifest
    New-StartMenuShortcut $resolvedInstallRoot
    Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Repaired MiniProToolkit at $resolvedInstallRoot. User data was preserved." -ForegroundColor Green
}
catch {
    $repairError = $_
    try {
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { Stop-GuardianService }
        if (-not $serviceExisted -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { Remove-GuardianService }
        if ($backupRoot -and (Test-Path -LiteralPath $backupRoot)) {
            Restore-Payload $resolvedInstallRoot $backupRoot
            if ($null -ne $previousState) { Set-Content -LiteralPath $statePath -Value $previousState -Encoding UTF8 }
            else { Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue }
        }
    }
    catch {
        $rollbackError = $_
        if ($backupRoot -and (Test-Path -LiteralPath $backupRoot)) {
            throw "Repair failed and rollback needs attention. Backup preserved at '$backupRoot'. Original error: $($repairError.Exception.Message). Rollback error: $($rollbackError.Exception.Message)"
        }
        throw "Repair failed and recovery needs attention. Original error: $($repairError.Exception.Message). Recovery error: $($rollbackError.Exception.Message)"
    }
    if ($serviceExisted) { Start-GuardianService }
    throw $repairError
}
