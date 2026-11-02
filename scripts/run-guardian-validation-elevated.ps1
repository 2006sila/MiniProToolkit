$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot 'validate-guardian-recovery.ps1'
$log = Join-Path $env:TEMP 'minipro-guardian-validation.log'
try {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $target *>&1 |
    Tee-Object -FilePath $log
  exit $LASTEXITCODE
}
catch {
  $_ | Out-String | Set-Content $log
  exit 1
}
