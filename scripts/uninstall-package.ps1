#requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = "$env:ProgramFiles\MiniProToolkit",
    [switch]$RestoreEcAutomatic,
    [switch]$PurgeUserData,
    [switch]$KeepInstallRoot
)

. (Join-Path $PSScriptRoot 'package-common.ps1')
$resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
if (-not $PSCmdlet.ShouldProcess($resolvedInstallRoot, 'Uninstall MiniProToolkit')) { return }

# Fail-closed pre-flight (read-only): an active lease must not be cleared implicitly.
Assert-RestoreAuthorization -RestoreEcAutomatic:$RestoreEcAutomatic
# Lifecycle order: stop the application and confirm it is gone first; only then may EC automatic
# recovery write 101 and clear the lease.
Stop-MiniProToolkitGracefully
Invoke-RestoreIfRequired -RestoreEcAutomatic:$RestoreEcAutomatic
$leasePath = Get-LeasePath
if (Test-Path -LiteralPath $leasePath) {
    throw "A Guardian lease remains. Uninstall stopped to preserve recovery ownership: $leasePath"
}
$guardianWasRunning = [bool](Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
Stop-GuardianService
if (Test-Path -LiteralPath $leasePath) {
    if ($guardianWasRunning) { Start-GuardianService }
    throw "A Guardian lease appeared while stopping the service; Guardian was restarted and uninstall was aborted: $leasePath"
}
Remove-GuardianService
Remove-Shortcuts $resolvedInstallRoot
Remove-StartupRegistrationIfOwned $resolvedInstallRoot
if (-not $KeepInstallRoot -and (Test-Path -LiteralPath $resolvedInstallRoot)) {
    Remove-Item -LiteralPath $resolvedInstallRoot -Recurse -Force
}
if ($PurgeUserData) {
    $userData = Get-UserDataRoot
    if (Test-Path -LiteralPath $userData) { Remove-Item -LiteralPath $userData -Recurse -Force }
    # Never delete %ProgramData%\MiniProToolkit recursively: that would remove the lease and the
    # lease lock file with it. Only whitelisted, disposable Guardian artifacts are purged.
    Remove-GuardianProgramData
}
Write-Host "MiniProToolkit was uninstalled. User configuration/logs were $(if($PurgeUserData){'purged'}else{'preserved'})." -ForegroundColor Green
