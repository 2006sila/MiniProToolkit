#requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$commonScript = Join-Path $PSScriptRoot 'package-common.ps1'
if (-not (Test-Path -LiteralPath $commonScript -PathType Leaf)) {
    throw "package-common.ps1 is missing next to the recovery script: $commonScript"
}
. $commonScript

# Snapshot the lease before any hardware write. The record is re-read under the lease lock after the
# EC write and deleted only when the owner identity (PID / process start / heartbeat / mode) is still
# the one evaluated here; a busy lock, a changed record or a missing record throws.
$leasePath = Get-LeasePath
$leaseBefore = Read-GuardianLease -LeasePath $leasePath

$instanceName = 'ACPI\PNP0C14\IP3POWERSWITCH_0'
$ci = Get-CimInstance -Namespace root/WMI -ClassName PowerSwitchInterface |
  Where-Object { $_.InstanceName -eq $instanceName }
if ($null -eq $ci) { throw "PowerSwitchInterface instance was not found: $instanceName" }
function Assert-WmiMethodSucceeded {
  param([object]$Result, [string]$MethodName)
  $returnProperty = $Result.PSObject.Properties['ReturnValue']
  if ($null -eq $returnProperty -or $null -eq $returnProperty.Value) { return }
  $value = $returnProperty.Value
  if ($value -is [bool]) {
    if (-not $value) { throw "$MethodName returned false." }
    return
  }
  if ([uint32]$value -ne 0) {
    throw "$MethodName failed with WMI return value $value."
  }
}
$setResult = Invoke-CimMethod -InputObject $ci -MethodName SetFanControl `
  -Arguments @{ FanNumber = [byte]1; FanDuty = [byte]101 }
Assert-WmiMethodSucceeded -Result $setResult -MethodName 'SetFanControl'
$readResult = Invoke-CimMethod -InputObject $ci -MethodName GetFanControl `
  -Arguments @{ FanNumber = [byte]1 }
Assert-WmiMethodSucceeded -Result $readResult -MethodName 'GetFanControl'
$rpm = [uint32]$readResult.FanDuty -band 0xFFFF

if ($null -eq $leaseBefore) {
    # Nothing claimed recovery ownership when the EC write started, so nothing may claim it now:
    # a lease that appeared in between belongs to a newer owner and is left untouched.
    if (Test-Path -LiteralPath $leasePath -PathType Leaf) {
        throw "A Guardian lease appeared while EC automatic recovery was running; it was left in place: $leasePath"
    }
}
else {
    Remove-GuardianLeaseIfUnchanged -ExpectedLease $leaseBefore -LeasePath $leasePath
}
Write-Host "EC automatic requested. Fan 1 RPM: $rpm" -ForegroundColor Green
