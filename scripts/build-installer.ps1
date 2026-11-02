[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Invalid release version: $Version" }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$payload = Join-Path $root "dist\MiniProToolkit-x64-v$Version"
$manifestPath = Join-Path $payload 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Release payload does not exist. Run publish-release.ps1 -Version $Version first."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.Version -ne $Version) {
    throw "Payload version '$($manifest.Version)' does not match requested installer version '$Version'."
}
foreach ($file in $manifest.Files) {
    $path = Join-Path $payload ($file.RelativePath -replace '/', '\\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Payload file is missing: $($file.RelativePath)" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $file.Sha256) { throw "Payload hash mismatch: $($file.RelativePath)" }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $CompilerPath = $candidates | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw "Inno Setup compiler not found. Pass -CompilerPath to ISCC.exe or install Inno Setup 6."
}
$outputDirectory = Join-Path $root 'dist\installer'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$scriptPath = Join-Path $root 'installer\MiniProToolkit.iss'
& $CompilerPath "/DAppVersion=$Version" $scriptPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
$installer = Join-Path $outputDirectory "MiniProToolkit-Setup-x64-v$Version.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "ISCC completed without producing the expected installer: $installer" }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
Set-Content -LiteralPath "$installer.sha256" -Value "$hash  $([System.IO.Path]::GetFileName($installer))" -Encoding ASCII
Write-Host "Installer: $installer" -ForegroundColor Green
Write-Host "SHA-256: $hash" -ForegroundColor Cyan
