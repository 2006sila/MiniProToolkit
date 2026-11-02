[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    # Payload directory that holds release-manifest.json. Defaults to the publisher's
    # default output; pass the same value used for publish-release.ps1 -OutputRoot when
    # the payload was produced somewhere else. Relative paths are resolved against the
    # repository root, absolute paths are used as given.
    [string]$PayloadRoot,
    # Directory that receives the setup executable and its .sha256 sidecar. Defaults to
    # the historical dist\installer. Relative paths are resolved against the repository
    # root; an absolute path (for example a temporary directory) is used as given.
    [string]$OutputRoot,
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Invalid release version: $Version" }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Both roots are pure path inputs: resolve them to a full path before any file is touched so
# that ISCC always receives absolute /DPayloadRoot and /DInstallerOutput values (the Inno
# compiler resolves relative paths against the .iss location, which is not what the caller
# means when it passes a repository-relative or temporary directory).
function Resolve-InstallerPath {
    param(
        [AllowEmptyString()][string]$Path,
        [Parameter(Mandatory)][string]$DefaultRelative,
        [Parameter(Mandatory)][string]$BaseDirectory
    )
    $value = if ([string]::IsNullOrWhiteSpace($Path)) { $DefaultRelative } else { $Path }
    $candidate = if ([System.IO.Path]::IsPathRooted($value)) { $value } else { Join-Path $BaseDirectory $value }
    $full = [System.IO.Path]::GetFullPath($candidate)
    # A trailing separator would turn Inno's "{#PayloadRoot}\*" source pattern into
    # "<dir>\\*" and shows up in log/error text; drive roots ("C:\") are left alone.
    if ($full.Length -gt 3 -and ($full.EndsWith('\') -or $full.EndsWith('/'))) {
        $full = $full.TrimEnd('\', '/')
    }
    return $full
}

$payload = Resolve-InstallerPath -Path $PayloadRoot -DefaultRelative (Join-Path 'dist' "MiniProToolkit-x64-v$Version") -BaseDirectory $root
$outputBase = Resolve-InstallerPath -Path $OutputRoot -DefaultRelative (Join-Path 'dist' 'installer') -BaseDirectory $root

$installerName = "MiniProToolkit-Setup-x64-v$Version.exe"
$installer = Join-Path $outputBase $installerName
$installerHashFile = "$installer.sha256"

# An existing setup executable or sidecar is a published artifact: it is never replaced,
# truncated or deleted by this script, not even when the new compilation fails. The caller
# has to pick a different -OutputRoot (or archive the old files itself after verifying them).
function Assert-InstallerTargetFree {
    $existing = @()
    if (Test-Path -LiteralPath $installer) { $existing += $installer }
    if (Test-Path -LiteralPath $installerHashFile) { $existing += $installerHashFile }
    if ($existing.Count -gt 0) {
        throw ("Installer output already exists and will not be overwritten: $($existing -join '; '). " +
            "Verify or archive the existing package, then re-run with another -OutputRoot " +
            "(for example -OutputRoot temp\installer-$Version).")
    }
}

Assert-InstallerTargetFree

if (-not (Test-Path -LiteralPath $payload -PathType Container)) {
    throw "Release payload does not exist: $payload. Run publish-release.ps1 -Version $Version first, or pass -PayloadRoot."
}
$manifestPath = Join-Path $payload 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Release payload has no release-manifest.json: $payload"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.Version -ne $Version) {
    throw "Payload version '$($manifest.Version)' does not match requested installer version '$Version'."
}
$payloadFileCount = 0
foreach ($file in $manifest.Files) {
    $path = Join-Path $payload ($file.RelativePath -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Payload file is missing: $($file.RelativePath)" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $file.Sha256) { throw "Payload hash mismatch: $($file.RelativePath)" }
    $payloadFileCount++
}
Write-Host "Payload gate: $payloadFileCount files match release-manifest.json in $payload" -ForegroundColor Green

# Second opinion from the payload's own self-check (layout plus re-hash) before the compiler
# runs: the installer is only ever built from a payload that passes its shipped validation
# script. The script is part of the payload contract (publish-release.ps1 rejects a tree without
# it), so a payload that lacks it is broken rather than merely unverified: the missing file is a
# hard failure before ISCC runs, never a warning that lets an unchecked payload be packaged.
$validateScript = Join-Path $payload 'scripts\validate-package.ps1'
if (-not (Test-Path -LiteralPath $validateScript -PathType Leaf)) {
    throw ("Payload has no scripts\validate-package.ps1: $payload. " +
        "The payload must ship its own self-check, so an unvalidated tree is never compiled into an installer. " +
        "Re-run publish-release.ps1 (or repair the payload) and try again. No installer was built.")
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validateScript -PackageRoot $payload
if ($LASTEXITCODE -ne 0) {
    throw "Payload self-check failed: $validateScript exited with code $LASTEXITCODE. No installer was built."
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $candidates = @()
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($base) { $candidates += (Join-Path $base 'Inno Setup 6\ISCC.exe') }
    }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe') }
    $candidates += (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    $CompilerPath = @($candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }) | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw "Inno Setup compiler not found. Pass -CompilerPath to ISCC.exe or install Inno Setup 6."
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path

$scriptPath = Join-Path $root 'installer\MiniProToolkit.iss'

# Transaction: ISCC writes into a scratch directory inside the requested output root (same
# volume, so the final move is a rename) and the finished setup + sidecar are committed only
# after the compiler exited 0 and produced a non-empty executable. A failed or partial run
# therefore leaves the output root without any new setup/hash files.
$staging = Join-Path $outputBase ('.staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    Write-Host "ISCC: $CompilerPath" -ForegroundColor Cyan
    Write-Host "PayloadRoot: $payload" -ForegroundColor Cyan
    Write-Host "Staging output: $staging" -ForegroundColor Cyan
    # /D values are passed as single arguments; PowerShell quotes them for the native command
    # line, and Inno receives /DPayloadRoot=<full path> unchanged (spaces included). The .iss
    # only falls back to its own dist defaults when those defines are absent.
    $isccArguments = @(
        "/DAppVersion=$Version",
        "/DPayloadRoot=$payload",
        "/DInstallerOutput=$staging",
        $scriptPath
    )
    & $CompilerPath @isccArguments
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }

    $stagedInstaller = Join-Path $staging $installerName
    if (-not (Test-Path -LiteralPath $stagedInstaller -PathType Leaf)) {
        throw "ISCC completed without producing the expected installer: $stagedInstaller"
    }
    $stagedItem = Get-Item -LiteralPath $stagedInstaller
    if ($stagedItem.Length -le 0) { throw "ISCC produced an empty installer file: $stagedInstaller" }
    $signature = New-Object byte[] 2
    $stream = [System.IO.File]::OpenRead($stagedInstaller)
    try { $signatureRead = $stream.Read($signature, 0, 2) } finally { $stream.Dispose() }
    if ($signatureRead -ne 2 -or $signature[0] -ne 0x4D -or $signature[1] -ne 0x5A) {
        throw "ISCC output is not a PE executable (missing MZ header): $stagedInstaller"
    }
    $hash = (Get-FileHash -LiteralPath $stagedInstaller -Algorithm SHA256).Hash
    $stagedHashFile = Join-Path $staging ($installerName + '.sha256')
    Set-Content -LiteralPath $stagedHashFile -Value "$hash  $installerName" -Encoding ASCII

    # Re-check right before commit: the target must still be absent, otherwise this run would
    # silently replace an artifact that appeared while ISCC was running.
    Assert-InstallerTargetFree
    Move-Item -LiteralPath $stagedInstaller -Destination $installer
    try {
        Move-Item -LiteralPath $stagedHashFile -Destination $installerHashFile
    }
    catch {
        # The executable was just committed without its sidecar. Remove only the file this run
        # created so a half package is not left behind looking complete; no previous artifact
        # exists at this point (Assert-InstallerTargetFree passed).
        Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
        throw
    }
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host "Installer: $installer" -ForegroundColor Green
Write-Host "SHA-256: $hash" -ForegroundColor Cyan
