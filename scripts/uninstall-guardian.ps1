#requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$commonScript = Join-Path $PSScriptRoot 'package-common.ps1'
if (-not (Test-Path -LiteralPath $commonScript -PathType Leaf)) {
    throw "package-common.ps1 is missing next to this script: $commonScript"
}
. $commonScript

# Lifecycle order: the application owns the lease and the hardware, so it is stopped and confirmed
# gone first. Removing the Guardian service is never combined with deleting a lease - that would
# destroy recovery ownership without returning the hardware to EC automatic. A lease that survives
# the graceful stop must be cleared by the recovery script, which performs the EC write and the
# compare-and-clear under the lease lock.
Stop-MiniProToolkitGracefully
$leasePath = Get-LeasePath
if (Test-Path -LiteralPath $leasePath -PathType Leaf) {
    throw "An active Guardian lease exists at $leasePath. Run scripts\restore-ec-automatic.ps1 (or uninstall-package.ps1 -RestoreEcAutomatic) first; removing the service does not clear recovery ownership."
}

Stop-GuardianService
Remove-GuardianService
if (Test-Path -LiteralPath $leasePath -PathType Leaf) {
    throw "A Guardian lease appeared while the service was being removed; it was left in place: $leasePath"
}
Write-Host "Removed $ServiceName. No lease file was deleted by this script." -ForegroundColor Green
