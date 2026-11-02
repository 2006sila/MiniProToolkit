[CmdletBinding()]
param(
    [string]$PackageRoot = "$PSScriptRoot\.."
)

$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path $PackageRoot).Path
. (Join-Path $resolved 'scripts\package-common.ps1')
Assert-PackagePayload $resolved
$manifest = Get-ReleaseManifest $resolved
$failures = @()
foreach ($file in $manifest.Files) {
    $path = Join-Path $resolved ($file.RelativePath -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures += "Missing: $($file.RelativePath)"
        continue
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $file.Sha256) {
        $failures += "Hash mismatch: $($file.RelativePath)"
    }
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}
[pscustomobject]@{
    Product = $manifest.Product
    Version = $manifest.Version
    Runtime = $manifest.Runtime
    SelfContained = $manifest.SelfContained
    FileCount = @($manifest.Files).Count
    MainExecutableExists = Test-Path (Join-Path $resolved $manifest.MainExecutable)
    GuardianExecutableExists = Test-Path (Join-Path $resolved $manifest.GuardianExecutable)
    Hashes = 'OK'
} | Format-List
