#requires -RunAsAdministrator
param(
  [string]$BinaryPath = "$PSScriptRoot\..\src\MiniProToolkit.Guardian\bin\Release\net9.0-windows\win-x64\publish\MiniProToolkit.Guardian.exe"
)
$ErrorActionPreference = 'Stop'
$serviceName = 'MiniProToolkitGuardian'
$displayName = 'MiniProToolkit Guardian'
$description = 'Restores Lecoo MINI PRO-AHP fan control to EC automatic mode after an abandoned control lease.'
$resolved = (Resolve-Path $BinaryPath).Path

& sc.exe stop $serviceName | Out-Null
& sc.exe delete $serviceName | Out-Null
Start-Sleep -Milliseconds 500
& sc.exe create $serviceName binPath= "`"$resolved`"" start= auto DisplayName= "`"$displayName`"" | Out-Host
& sc.exe description $serviceName $description | Out-Host
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/none/0 | Out-Host
& sc.exe start $serviceName | Out-Host
Write-Host "Installed and started $displayName from $resolved" -ForegroundColor Green
