[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$OutputRoot = 'dist',
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputBase = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot
} else {
    Join-Path $root $OutputRoot
}
$output = [System.IO.Path]::GetFullPath((Join-Path $outputBase "MiniProToolkit-x64-v$Version"))

# Timestamp captured before any gate or build work. The freshness gate at the end of this
# script fails if any release-relevant repository file was written after this moment: such
# a file was changed while (or after) the payload was produced and cannot be inside it.
$publishStartUtc = [DateTime]::UtcNow

# ZIP helpers (read raw entry names, normalize '\' separators to '/', assert structure).
# This file is intentionally not shipped inside the payload; only the publisher uses it.
. (Join-Path $PSScriptRoot 'package-archive.ps1')

# Lines copied verbatim into the payload below; also used by the copy-fidelity gate and the
# freshness gate, so the shipped set cannot silently drift away from the verified set.
$shippedDocs = @(
    'phase-3-validation.md',
    'phase-4-validation.md',
    'phase-5-validation.md',
    'release-packaging.md',
    'fan-calibration.md',
    'fan-control-hardening.md',
    'history-and-diagnostics.md',
    'profile-automation-and-installer.md',
    'product-roadmap.md'
)

# Payload capability symbols. Every payload must contain these type names, otherwise the
# published binaries do not carry the features the release notes describe. Checked with a
# read-only UTF-8 byte search over the assembly metadata (no assembly load: loading would
# lock files and needs the full dependency closure).
$capabilitySymbols = [ordered]@{
    'App\MiniProToolkit.Core.dll' = @(
        'FanCurveLeaseGate',
        'TaskbarRefreshPolicy',
        'FixedRpmControlSession',
        'CpuDisplaySourceSettings',
        'ProfileAutomationProcessCondition',
        'ShutdownPhase',
        'PowerTransitionPolicy',
        'OsdEventTemplates',
        'CpuClassFrequencyReading',
        'AuxTemperatureSelector',
        'SystemControlWriteResult',
        'ToolkitCommandRequestCache'
    )
    'App\MiniProToolkit.dll' = @('DisplaySourceSettingsWindow', 'TaskbarStatusWindow', 'IsExpectedCommandPipeServer')
    'App\MiniProToolkit.Hardware.dll' = @(
        'CpuClassFrequencyReader',
        'LibreHardwareMonitorAuxTemperatureSensor',
        'AudioEndpointReader',
        'BrightnessReader'
    )
    'Guardian\MiniProToolkit.Guardian.dll' = @('GuardianWorker')
}

# Required package layout: the lifecycle scripts dot-source package-common.ps1 and
# validate-package.ps1 re-hashes the payload, so both must exist inside the ZIP.
$requiredArchiveEntries = @(
    'release-manifest.json',
    'App/MiniProToolkit.exe',
    'Guardian/MiniProToolkit.Guardian.exe',
    'scripts/package-common.ps1',
    'scripts/install-package.ps1',
    'scripts/validate-package.ps1',
    'docs/README.md',
    'docs/THIRD-PARTY-NOTICES.md'
)

# PowerShell AST gate: every top-level script must parse before the payload is touched.
# A payload whose lifecycle scripts cannot even be parsed must never replace a good one.
function Assert-ScriptSyntaxGate {
    param([Parameter(Mandatory)][string]$ScriptsDirectory)
    $scripts = @(Get-ChildItem -LiteralPath $ScriptsDirectory -Filter '*.ps1' -File)
    if ($scripts.Count -eq 0) { throw "AST gate failed: no PowerShell scripts found in $ScriptsDirectory." }
    $failures = @()
    foreach ($script in $scripts) {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
        foreach ($parseError in @($errors)) {
            $failures += "$($script.Name): $($parseError.Message) (line $($parseError.Extent.StartLineNumber))"
        }
    }
    if ($failures.Count -gt 0) {
        throw "AST gate failed; these scripts do not parse and must not be shipped: $($failures -join '; ')"
    }
    Write-Host "AST gate: $($scripts.Count) PowerShell scripts parsed." -ForegroundColor Green
}

# Assembly metadata text is cached per file; the Latin-1 decode maps one byte to one char,
# so ASCII type names can be searched with IndexOf instead of a per-byte PowerShell loop
# (a 5 MB assembly would otherwise cost tens of millions of interpreted iterations).
$script:PayloadAssemblyText = @{}

function Test-PayloadAssemblySymbol {
    param(
        [Parameter(Mandatory)][string]$AssemblyPath,
        [Parameter(Mandatory)][string]$Symbol
    )
    if (-not $script:PayloadAssemblyText.ContainsKey($AssemblyPath)) {
        $bytes = [System.IO.File]::ReadAllBytes($AssemblyPath)
        if ($bytes.Length -eq 0) { return $false }
        $script:PayloadAssemblyText[$AssemblyPath] = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
    }
    return ($script:PayloadAssemblyText[$AssemblyPath].IndexOf($Symbol, [System.StringComparison]::Ordinal) -ge 0)
}

function Assert-CapabilitySymbols {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Expected
    )
    $failures = @()
    $checked = 0
    foreach ($relativePath in $Expected.Keys) {
        $assemblyPath = Join-Path $PackageRoot ($relativePath -replace '\\', '\')
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            $failures += "Missing assembly: $relativePath"
            continue
        }
        foreach ($symbol in @($Expected[$relativePath])) {
            $checked++
            if (-not (Test-PayloadAssemblySymbol -AssemblyPath $assemblyPath -Symbol $symbol)) {
                $failures += "$relativePath does not contain '$symbol'"
            }
        }
    }
    if ($failures.Count -gt 0) {
        throw "Capability symbol gate failed; the payload does not contain the features the release notes claim: $($failures -join '; ')"
    }
    Write-Host "Capability symbol gate: $checked symbols found in $($Expected.Keys.Count) assemblies." -ForegroundColor Green
}

# Byte-identical copies only: the payload documentation and lifecycle scripts must be the
# exact repository files (README.md and architecture.md are rewritten on purpose and are
# verified separately by the link rewrite checks).
function Assert-PayloadCopiesMatchRepository {
    param([Parameter(Mandatory)][object[]]$Pairs)
    $failures = @()
    foreach ($pair in $Pairs) {
        if (-not (Test-Path -LiteralPath $pair.Source -PathType Leaf)) {
            $failures += "Repository file is missing: $($pair.Source)"
            continue
        }
        if (-not (Test-Path -LiteralPath $pair.Payload -PathType Leaf)) {
            $failures += "Payload file is missing: $($pair.Payload)"
            continue
        }
        $sourceHash = (Get-FileHash -LiteralPath $pair.Source -Algorithm SHA256).Hash
        $payloadHash = (Get-FileHash -LiteralPath $pair.Payload -Algorithm SHA256).Hash
        if ($sourceHash -ne $payloadHash) {
            $failures += "Payload copy differs from the repository file: $($pair.Payload)"
        }
        $payloadItem = Get-Item -LiteralPath $pair.Payload
        $sourceItem = Get-Item -LiteralPath $pair.Source
        if ($payloadItem.LastWriteTimeUtc -lt $sourceItem.LastWriteTimeUtc) {
            $failures += "Payload copy is older than its source: $($pair.Payload)"
        }
    }
    if ($failures.Count -gt 0) {
        throw "Payload copy gate failed: $($failures -join '; ')"
    }
    Write-Host "Payload copy gate: $($Pairs.Count) copied files match the repository." -ForegroundColor Green
}

function Assert-ReleaseManifestConsistency {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][datetime]$PublishStartUtc
    )
    $failures = @()
    if ([string]$Manifest.Product -ne 'MiniProToolkit') { $failures += "Product is '$($Manifest.Product)', expected 'MiniProToolkit'" }
    if ([string]$Manifest.Version -ne $ExpectedVersion) { $failures += "Version is '$($Manifest.Version)', expected '$ExpectedVersion'" }
    if ([string]$Manifest.Runtime -ne 'win-x64') { $failures += "Runtime is '$($Manifest.Runtime)', expected 'win-x64'" }
    if ($Manifest.SelfContained -ne $true) { $failures += "SelfContained is '$($Manifest.SelfContained)', expected true" }

    $generatedUtc = [datetime]::MinValue
    $parsed = [datetime]::TryParse(
        [string]$Manifest.GeneratedUtc,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$generatedUtc)
    if (-not $parsed) {
        $failures += "GeneratedUtc '$($Manifest.GeneratedUtc)' is not a parseable timestamp"
    }
    elseif ($generatedUtc.ToUniversalTime() -lt $PublishStartUtc) {
        $failures += "GeneratedUtc '$($Manifest.GeneratedUtc)' predates the start of this publish run ($($PublishStartUtc.ToString('O')))"
    }

    foreach ($executable in @([string]$Manifest.MainExecutable, [string]$Manifest.GuardianExecutable)) {
        if ([string]::IsNullOrWhiteSpace($executable)) { $failures += 'MainExecutable/GuardianExecutable is empty'; continue }
        if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $executable) -PathType Leaf)) {
            $failures += "Declared executable is missing from the payload: $executable"
        }
    }

    $relativePaths = @($Manifest.Files | ForEach-Object { ([string]$_.RelativePath).Replace('\', '/') })
    $duplicates = @($relativePaths | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    if ($duplicates.Count -gt 0) { $failures += "Manifest lists duplicate paths: $($duplicates -join ', ')" }
    $unexpected = @($relativePaths | Where-Object { $_ -notmatch '^(App|Guardian|scripts|docs)/' })
    if ($unexpected.Count -gt 0) { $failures += "Manifest lists files outside the payload layout: $($unexpected -join ', ')" }
    $diskCount = @(Get-ChildItem -LiteralPath $PackageRoot -File -Recurse |
        Where-Object { $_.Name -ne 'release-manifest.json' }).Count
    if ($diskCount -ne $relativePaths.Count) {
        $failures += "Manifest lists $($relativePaths.Count) files but the payload holds $diskCount (manifest and payload disagree)"
    }
    if ($failures.Count -gt 0) {
        throw "Release manifest is inconsistent: $($failures -join '; ')"
    }
    Write-Host "Manifest gate: version $ExpectedVersion, $($relativePaths.Count) files, GeneratedUtc $($Manifest.GeneratedUtc)." -ForegroundColor Green
}

# Release-relevant repository files only: bin/obj (build output), dist (artifacts under test),
# lite/ (separate sub-product) and the repository-internal planning documents are excluded.
function Get-ReleaseSourceFile {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [string[]]$ShippedDocs = @()
    )
    $files = @()
    foreach ($directory in @('src', 'tests', 'scripts')) {
        $full = Join-Path $RepositoryRoot $directory
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { continue }
        $files += @(Get-ChildItem -LiteralPath $full -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    }
    foreach ($relativePath in @('README.md', 'THIRD-PARTY-NOTICES.md', 'Directory.Build.props', 'installer\MiniProToolkit.iss')) {
        $full = Join-Path $RepositoryRoot $relativePath
        if (Test-Path -LiteralPath $full -PathType Leaf) { $files += Get-Item -LiteralPath $full }
    }
    foreach ($doc in $ShippedDocs) {
        $full = Join-Path (Join-Path $RepositoryRoot 'docs') $doc
        if (Test-Path -LiteralPath $full -PathType Leaf) { $files += Get-Item -LiteralPath $full }
    }
    return $files
}

# Freshness gate: a source/doc/script file written after $PublishStartUtc was not part of the
# build that produced this payload, so the artifacts must not be announced as current.
function Assert-SourceNotNewerThanPublish {
    param(
        [Parameter(Mandatory)][object[]]$SourceFiles,
        [Parameter(Mandatory)][datetime]$PublishStartUtc
    )
    $newer = @($SourceFiles |
        Where-Object { $_.LastWriteTimeUtc -gt $PublishStartUtc } |
        Sort-Object -Property LastWriteTimeUtc -Descending)
    if ($newer.Count -gt 0) {
        $detail = @($newer | Select-Object -First 5 | ForEach-Object {
            "$($_.FullName) ($($_.LastWriteTimeUtc.ToString('O')))"
        })
        throw "Freshness gate failed: $($newer.Count) release-relevant file(s) were modified after this publish run started ($($PublishStartUtc.ToString('O'))). The payload, ZIP and hashes of this run do not contain them - re-run the release and treat this run's artifacts as unapproved. Newest: $($detail -join '; ')"
    }
    Write-Host "Freshness gate: no release-relevant file is newer than the publish start ($($PublishStartUtc.ToString('O')))." -ForegroundColor Green
}

# Test gate. It runs before the payload directory is removed or rewritten, so a red
# test run can never leave a freshly built, publishable payload behind. Only the Core
# test project is executed on purpose: it is the single test project in the repository
# and it must not be replaced by the full solution, whose WPF/hardware paths need a
# real machine and cannot act as a gate here.
$testProject = Join-Path $root 'tests\MiniProToolkit.Core.Tests\MiniProToolkit.Core.Tests.csproj'
if ($SkipTests) {
    Write-Warning 'Test gate skipped (-SkipTests). The package produced by this run is NOT test-verified; record why the gate was bypassed in the release notes before shipping.'
} else {
    if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
        throw "Test gate failed: Core test project not found at $testProject."
    }
    Write-Host "Test gate: dotnet test $testProject -c Release" -ForegroundColor Cyan
    $testExitCode = 1
    Push-Location $root
    try {
        dotnet test $testProject -c Release --nologo -v minimal
        $testExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($testExitCode -ne 0) {
        throw "Test gate failed: Core tests exited with code $testExitCode. No release payload was created or modified. Use -SkipTests only for an emergency package documented as unverified."
    }
    Write-Host 'Test gate: Core tests passed.' -ForegroundColor Green
}

# The remaining pre-publish gate runs before the payload directory is removed or rewritten,
# so a red AST/parse result can never leave a freshly built payload behind (and -SkipTests
# only bypasses the test gate above, never this one).
Assert-ScriptSyntaxGate -ScriptsDirectory $PSScriptRoot

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $output, (Join-Path $output 'App'), (Join-Path $output 'Guardian'), (Join-Path $output 'scripts') | Out-Null

Push-Location $root
try {
    dotnet restore .\src\MiniProToolkit.App\MiniProToolkit.App.csproj -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "Main application restore failed with exit code $LASTEXITCODE." }
    dotnet restore .\src\MiniProToolkit.Guardian\MiniProToolkit.Guardian.csproj -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "Guardian restore failed with exit code $LASTEXITCODE." }
    dotnet publish .\src\MiniProToolkit.App\MiniProToolkit.App.csproj `
        -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=false -p:DebugType=None -o (Join-Path $output 'App')
    if ($LASTEXITCODE -ne 0) { throw "Main application publish failed with exit code $LASTEXITCODE." }

    dotnet publish .\src\MiniProToolkit.Guardian\MiniProToolkit.Guardian.csproj `
        -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=false -p:DebugType=None -o (Join-Path $output 'Guardian')
    if ($LASTEXITCODE -ne 0) { throw "Guardian publish failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

$scriptFiles = @(
    'package-common.ps1',
    'install-package.ps1',
    'upgrade-package.ps1',
    'repair-package.ps1',
    'uninstall-package.ps1',
    'validate-package.ps1',
    'restore-ec-automatic.ps1'
)
foreach ($scriptFile in $scriptFiles) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptFile) -Destination (Join-Path $output 'scripts') -Force
}
$docs = Join-Path $output 'docs'
New-Item -ItemType Directory -Force -Path $docs | Out-Null
# The repository README links to its siblings as docs\<name>.md. Inside the payload
# it is copied next to those documents, so the docs\ prefix has to be dropped;
# otherwise every link points at a non-existent docs\docs\ folder. The repository
# copy stays untouched because there it runs from the repository root.
$readmePath = Join-Path $root 'README.md'
$readmeText = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8
$readmePayloadText = $readmeText -replace '\]\(docs/', ']('
if ($readmePayloadText -eq $readmeText) {
    throw 'README link rewrite for the payload changed nothing; the packaging assumption (README links use a docs/ prefix) no longer holds.'
}
[System.IO.File]::WriteAllText((Join-Path $docs 'README.md'), $readmePayloadText, (New-Object System.Text.UTF8Encoding($false)))
foreach ($docName in $shippedDocs) {
    Copy-Item -LiteralPath (Join-Path (Join-Path $root 'docs') $docName) -Destination $docs -Force
}
# architecture.md is shipped (approved) so the payload explains the display-source and
# control boundary. Its single outbound document link points at the repository-root
# THIRD-PARTY-NOTICES.md as ../THIRD-PARTY-NOTICES.md; inside the payload both files
# live in docs\, so that link loses the parent prefix. The repository copy stays as is.
$architecturePath = Join-Path $root 'docs\architecture.md'
$architectureText = Get-Content -LiteralPath $architecturePath -Raw -Encoding UTF8
$architecturePayloadText = $architectureText -replace '\]\(\.\./THIRD-PARTY-NOTICES\.md\)', '](THIRD-PARTY-NOTICES.md)'
if ($architecturePayloadText -eq $architectureText) {
    throw 'architecture.md link rewrite for the payload changed nothing; the packaging assumption (architecture.md links ../THIRD-PARTY-NOTICES.md) no longer holds.'
}
[System.IO.File]::WriteAllText((Join-Path $docs 'architecture.md'), $architecturePayloadText, (New-Object System.Text.UTF8Encoding($false)))
# Third-party notices live under docs\ so that install/upgrade/repair staging
# (which copies the App, Guardian, scripts and docs directories) always carries them.
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $docs -Force

# Every relative markdown link inside the payload documentation must resolve inside
# the payload; a link that only works in the repository would be broken for the user
# who extracts the ZIP.
$linkFailures = @()
foreach ($markdown in Get-ChildItem -LiteralPath $docs -Filter '*.md' -File) {
    $markdownText = Get-Content -LiteralPath $markdown.FullName -Raw -Encoding UTF8
    foreach ($match in [regex]::Matches($markdownText, '\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value.Trim()
        if ($target -match '(?i)^(https?:|mailto:|#)') { continue }
        $target = ($target -split '#')[0]
        if ([string]::IsNullOrWhiteSpace($target)) { continue }
        $resolvedTarget = Join-Path $markdown.DirectoryName ($target -replace '/', '\')
        if (-not (Test-Path -LiteralPath $resolvedTarget)) {
            $linkFailures += "$($markdown.Name) -> $target"
        }
    }
}
if ($linkFailures.Count -gt 0) {
    throw "Payload documentation contains links that do not resolve inside the package: $($linkFailures -join '; ')"
}

$files = Get-ChildItem -LiteralPath $output -File -Recurse |
    Where-Object { $_.Name -ne 'release-manifest.json' } |
    ForEach-Object {
        [pscustomobject]@{
            RelativePath = $_.FullName.Substring($output.Length + 1).Replace('\', '/')
            Length = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
$manifest = [pscustomobject]@{
    Product = 'MiniProToolkit'
    Version = $Version
    Runtime = 'win-x64'
    SelfContained = $true
    GeneratedUtc = [DateTime]::UtcNow.ToString('O')
    MainExecutable = 'App/MiniProToolkit.exe'
    GuardianExecutable = 'Guardian/MiniProToolkit.Guardian.exe'
    Files = @($files)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Encoding UTF8

# ---- post-publish gates (payload self-check, capability symbols, manifest, archive) ----
# They all run before the ZIP sidecar hash is written: a red gate can therefore never leave
# a complete-looking package with a matching .sha256 behind.
Write-Host 'Self-check: scripts\validate-package.ps1' -ForegroundColor Cyan
$validateScript = Join-Path $output 'scripts\validate-package.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validateScript -PackageRoot $output
if ($LASTEXITCODE -ne 0) {
    throw "Package self-check failed: the payload's own validate-package.ps1 exited with code $LASTEXITCODE. The payload, manifest and hashes of this run must not be shipped."
}

$copyPairs = @()
foreach ($scriptFile in $scriptFiles) {
    $copyPairs += [pscustomobject]@{
        Source = (Join-Path $PSScriptRoot $scriptFile)
        Payload = (Join-Path (Join-Path $output 'scripts') $scriptFile)
    }
}
foreach ($docName in $shippedDocs) {
    $copyPairs += [pscustomobject]@{
        Source = (Join-Path (Join-Path $root 'docs') $docName)
        Payload = (Join-Path $docs $docName)
    }
}
$copyPairs += [pscustomobject]@{
    Source = (Join-Path $root 'THIRD-PARTY-NOTICES.md')
    Payload = (Join-Path $docs 'THIRD-PARTY-NOTICES.md')
}
Assert-PayloadCopiesMatchRepository -Pairs $copyPairs
Assert-CapabilitySymbols -PackageRoot $output -Expected $capabilitySymbols
Assert-ReleaseManifestConsistency -Manifest $manifest -PackageRoot $output -ExpectedVersion $Version -PublishStartUtc $publishStartUtc

# Archive. ZipFile.CreateFromDirectory + separator repair instead of Compress-Archive: the
# latter writes '\' entry separators on Windows PowerShell 5.1, which is not the ZIP spec
# ('/') and turns the directory tree into flat backslash file names on Linux/macOS tools.
Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
$archive = "$output.zip"
$archiveBuilding = "$archive.building"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
if (Test-Path -LiteralPath $archiveBuilding) { Remove-Item -LiteralPath $archiveBuilding -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $output,
    $archiveBuilding,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
try {
    Repair-ZipEntrySeparators -SourceArchive $archiveBuilding -DestinationArchive $archive
}
finally {
    if (Test-Path -LiteralPath $archiveBuilding) { Remove-Item -LiteralPath $archiveBuilding -Force }
}
$zipSummary = Assert-ReleaseZipStructure -ArchivePath $archive -Manifest $manifest -RequiredEntries $requiredArchiveEntries
Write-Host "Archive structure gate: $($zipSummary.FileCount) files in $($zipSummary.TopLevel -join ', ')." -ForegroundColor Green

$releaseSourceFiles = Get-ReleaseSourceFile -RepositoryRoot $root -ShippedDocs ($shippedDocs + 'architecture.md')
Assert-SourceNotNewerThanPublish -SourceFiles $releaseSourceFiles -PublishStartUtc $publishStartUtc

$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
Set-Content -LiteralPath "$archive.sha256" -Value "$archiveHash  $([System.IO.Path]::GetFileName($archive))" -Encoding ASCII

Write-Host "Published package: $output" -ForegroundColor Green
Write-Host "Archive: $archive" -ForegroundColor Green
Write-Host "Archive SHA-256: $archiveHash" -ForegroundColor Cyan
Write-Host 'All publish gates passed (test/AST/copy/capability/manifest/archive/freshness).' -ForegroundColor Green
Write-Host 'Run scripts\build-installer.ps1 next; installation itself stays a target-machine step.' -ForegroundColor Cyan
