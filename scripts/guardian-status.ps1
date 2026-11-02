# Guardian read-only diagnostic. Does not write WMI or change service state.
$ErrorActionPreference = 'Stop'
$serviceName = 'MiniProToolkitGuardian'
$leasePath = Join-Path $env:ProgramData 'MiniProToolkit\guardian-lease.json'
$logPath = Join-Path $env:ProgramData 'MiniProToolkit\guardian.log'
$binaryPath = Join-Path $PSScriptRoot '..\src\MiniProToolkit.Guardian\bin\Release\net9.0-windows\win-x64\publish\MiniProToolkit.Guardian.exe'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$lease = $null
if (Test-Path $leasePath) {
  try { $lease = Get-Content $leasePath -Raw | ConvertFrom-Json } catch { $lease = "INVALID_JSON" }
}

[pscustomobject]@{
  ServiceName = $serviceName
  ServiceStatus = if ($service) { $service.Status } else { 'NotFound' }
  StartType = if ($service) { $service.StartType } else { 'Unknown' }
  BinaryExists = Test-Path $binaryPath
  BinaryPath = (Resolve-Path $binaryPath -ErrorAction SilentlyContinue).Path
  LeaseExists = Test-Path $leasePath
  LeasePath = $leasePath
  Lease = $lease
  LogExists = Test-Path $logPath
  LogPath = $logPath
} | Format-List

if (Test-Path $logPath) {
  Write-Host '--- Guardian log tail ---' -ForegroundColor Cyan
  Get-Content $logPath -Tail 20
}
