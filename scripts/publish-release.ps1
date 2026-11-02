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
$archive = "$output.zip"

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
    'performance-review.md',
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
        'ToolkitCommandRequestCache',
        'TaskbarDisplaySettings',
        'FloatingStatusSettings',
        'TaskbarRenderSignature',
        'BoundedTextLog'
    )
    'App\MiniProToolkit.dll' = @(
        'DisplaySourceSettingsWindow',
        'TaskbarStatusWindow',
        'IsExpectedCommandPipeServer',
        'FloatingStatusWindow',
        'FloatingStatusSettingsWindow',
        'TaskbarSettingsWindow'
    )
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

# Version helpers. The release version is a three/four component numeric version; the manifest
# announces it as a string, while the binaries carry FileVersion/ProductVersion (win32 resource) and
# an InformationalVersion that may append build metadata ('0.1.0+abc1234'). Normalizing by dropping
# the metadata and trailing '.0' segments makes '0.1.0', '0.1.0.0' and '0.1.0+abc' compare equal
# while any real difference ('0.1.0.1' vs '0.1.0', '0.2.0' vs '0.1.0') still fails.
function ConvertTo-NormalizedVersion {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $value = $Text.Trim()
    $value = @($value -split '\+')[0]
    $value = @($value -split '\s+')[0]
    $value = $value.Trim()
    if ($value -notmatch '^\d+(\.\d+){1,3}$') { return '' }
    $segments = New-Object 'System.Collections.Generic.List[string]'
    foreach ($segment in $value.Split('.')) { $segments.Add($segment) }
    while ($segments.Count -gt 2 -and $segments[$segments.Count - 1] -eq '0') {
        $segments.RemoveAt($segments.Count - 1)
    }
    return ($segments -join '.')
}

# Input side of the version gate. It runs before any output path is created, modified or deleted:
# a -Version that is not a release version, or that differs from the version compiled into the
# binaries, must fail here instead of producing a manifest that announces a version the payload
# does not carry.
function Assert-ReleaseVersionInput {
    param(
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )
    $failures = @()
    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        $failures += "-Version '$ExpectedVersion' is not a numeric release version (expected three or four numeric components, e.g. 0.1.0)"
    }
    $propsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        $failures += "Directory.Build.props is missing at $propsPath, so the version compiled into the binaries cannot be verified"
        throw "Release version gate failed before any output path was touched: $($failures -join '; ')"
    }
    try {
        $propsXml = [xml](Get-Content -LiteralPath $propsPath -Raw)
    }
    catch {
        throw "Release version gate failed before any output path was touched: Directory.Build.props cannot be parsed ($($_.Exception.Message))."
    }
    $expectedNormalized = ConvertTo-NormalizedVersion -Text $ExpectedVersion
    $propsVersion = $null
    foreach ($propertyName in @('Version', 'FileVersion', 'AssemblyVersion', 'InformationalVersion')) {
        $node = $propsXml.SelectSingleNode("//$propertyName")
        if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
            if ($propertyName -eq 'Version') { $failures += 'Directory.Build.props declares no <Version>' }
            continue
        }
        $value = $node.InnerText.Trim()
        if ($propertyName -eq 'Version') { $propsVersion = $value }
        $normalized = ConvertTo-NormalizedVersion -Text $value
        if ($normalized -eq '') {
            $failures += "Directory.Build.props <$propertyName> '$value' is not a numeric version"
            continue
        }
        if ($normalized -ne $expectedNormalized) {
            $failures += "Directory.Build.props <$propertyName> '$value' does not match -Version '$ExpectedVersion'"
        }
    }
    if ($failures.Count -gt 0) {
        throw "Release version gate failed before any output path was touched: $($failures -join '; ')"
    }
    Write-Host "Version gate: -Version $ExpectedVersion matches Directory.Build.props <Version> $propsVersion and its file/assembly/informational versions." -ForegroundColor Green
    return [pscustomobject]@{
        Version = $ExpectedVersion
        PropsVersion = $propsVersion
        PropsPath = $propsPath
    }
}

# Output side of the same gate. Reading the version resource does not load the assembly, so the
# payload stays unlocked and no dependency closure is needed. At least one field must match the
# requested version and no field may name a different version: otherwise the manifest, the
# repository property and the published binary disagree and the payload must not be announced.
function Assert-PayloadAssemblyVersion {
    param(
        [Parameter(Mandatory)][string]$AssemblyPath,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$PropsVersion
    )
    if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
        throw "Assembly version gate failed: the published App assembly is missing: $AssemblyPath"
    }
    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($AssemblyPath)
    }
    catch {
        throw "Assembly version gate failed: the version resource of $AssemblyPath cannot be read ($($_.Exception.Message))."
    }
    $observed = [ordered]@{
        FileVersion = [string]$info.FileVersion
        ProductVersion = [string]$info.ProductVersion
        FileVersionRaw = [string]$info.FileVersionRaw
        ProductVersionRaw = [string]$info.ProductVersionRaw
    }
    $expectedNormalized = ConvertTo-NormalizedVersion -Text $ExpectedVersion
    $failures = @()
    $matched = 0
    foreach ($name in $observed.Keys) {
        $text = $observed[$name]
        if ([string]::IsNullOrWhiteSpace($text)) { continue }
        $normalized = ConvertTo-NormalizedVersion -Text $text
        if ($normalized -eq '') {
            $failures += "$name '$text' is not a numeric version"
            continue
        }
        if ($normalized -eq $expectedNormalized) { $matched++ }
        else { $failures += "$name '$text' does not match version '$ExpectedVersion'" }
    }
    if ($matched -eq 0) {
        $failures += "no version field of the published assembly matches '$ExpectedVersion'"
    }
    if ($failures.Count -gt 0) {
        throw "Assembly version gate failed; requested version '$ExpectedVersion' (Directory.Build.props '$PropsVersion') disagrees with the published binaries: $($failures -join '; '). The payload must not be announced with this version."
    }
    Write-Host "Assembly version gate: App assembly FileVersion $($observed.FileVersion) / ProductVersion $($observed.ProductVersion) matches version $ExpectedVersion (Directory.Build.props $PropsVersion)." -ForegroundColor Green
}

# Artifact commit. Nothing under the output base is modified before every gate above has accepted
# the staged payload and the staged ZIP. The commit then only renames same-volume paths: the old
# payload, the old ZIP and the old sidecar are moved to '.prev-<transaction>' backups instead of
# being deleted, and the new sidecar is written after the new ZIP is in place, so a failure can be
# rolled back and never leaves a new payload next to an old ZIP or a new ZIP next to a stale hash.
function Invoke-ReleaseCommit {
    param(
        [Parameter(Mandatory)][string]$StagedPayload,
        [Parameter(Mandatory)][string]$StagedArchive,
        [Parameter(Mandatory)][string]$Payload,
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$TransactionId,
        [Parameter(Mandatory)][string]$ExpectedArchiveHash
    )
    $sidecar = "$Archive.sha256"
    $payloadBackup = "$Payload.prev-$TransactionId"
    $archiveBackup = "$Archive.prev-$TransactionId"
    $sidecarBackup = "$sidecar.prev-$TransactionId"
    $sidecarWriting = "$sidecar.writing-$TransactionId"
    foreach ($candidate in @($payloadBackup, $archiveBackup, $sidecarBackup)) {
        if (Test-Path -LiteralPath $candidate) {
            throw "Release commit aborted before any change: backup path already exists: $candidate"
        }
    }
    $state = [ordered]@{
        OldPayloadMoved = $false
        NewPayloadPlaced = $false
        OldSidecarMoved = $false
        OldArchiveMoved = $false
        NewArchivePlaced = $false
        NewSidecarWritten = $false
    }
    $committedHash = $null
    $sidecarLine = $null
    try {
        if (Test-Path -LiteralPath $Payload) {
            Move-Item -LiteralPath $Payload -Destination $payloadBackup
            $state.OldPayloadMoved = $true
        }
        Move-Item -LiteralPath $StagedPayload -Destination $Payload
        $state.NewPayloadPlaced = $true
        # The old sidecar leaves before the new ZIP arrives, so no moment exists in which a stale
        # .sha256 describes a ZIP it was not written for.
        if (Test-Path -LiteralPath $sidecar) {
            Move-Item -LiteralPath $sidecar -Destination $sidecarBackup
            $state.OldSidecarMoved = $true
        }
        if (Test-Path -LiteralPath $Archive) {
            Move-Item -LiteralPath $Archive -Destination $archiveBackup
            $state.OldArchiveMoved = $true
        }
        Move-Item -LiteralPath $StagedArchive -Destination $Archive
        $state.NewArchivePlaced = $true
        $committedHash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash
        if ($committedHash -ne $ExpectedArchiveHash) {
            throw "committed archive hash '$committedHash' does not match the verified staged hash '$ExpectedArchiveHash'"
        }
        $sidecarLine = "$committedHash  $([System.IO.Path]::GetFileName($Archive))"
        [System.IO.File]::WriteAllText($sidecarWriting, $sidecarLine + [Environment]::NewLine, (New-Object System.Text.ASCIIEncoding))
        Move-Item -LiteralPath $sidecarWriting -Destination $sidecar
        $state.NewSidecarWritten = $true
    }
    catch {
        $commitError = $_.Exception.Message
        # Rollback. Every recovery step below has its own try block: a failure of one step must not
        # skip the remaining restores, and the run may only claim a complete rollback when all of
        # them succeeded. A step never touches a path it cannot prove to be part of this
        # transaction - the new payload/ZIP/sidecar are recognised through this run's state, its
        # archive hash and its own sidecar line, and a previous artifact is only moved back while
        # its landing path is free. Unknown occupants are left in place and reported.
        $recoveryFailures = New-Object 'System.Collections.Generic.List[string]'
        $recoveryDone = New-Object 'System.Collections.Generic.List[string]'
        $recoveryNotes = New-Object 'System.Collections.Generic.List[string]'
        $recoveryResidual = New-Object 'System.Collections.Generic.List[string]'

        try {
            if (Test-Path -LiteralPath $sidecarWriting) {
                Remove-Item -LiteralPath $sidecarWriting -Force
                $recoveryDone.Add("removed the half-written sidecar '$sidecarWriting'")
            }
        }
        catch {
            $recoveryFailures.Add("remove the half-written sidecar '$sidecarWriting': $($_.Exception.Message)")
        }

        try {
            if ($state.NewSidecarWritten -and (Test-Path -LiteralPath $sidecar)) {
                $currentSidecar = Get-Content -LiteralPath $sidecar -Raw
                if ($null -ne $currentSidecar) { $currentSidecar = $currentSidecar.Trim() }
                if ($currentSidecar -eq $sidecarLine) {
                    Remove-Item -LiteralPath $sidecar -Force
                    $recoveryDone.Add("removed the sidecar '$sidecar' written by this run")
                }
                else {
                    $recoveryFailures.Add("sidecar '$sidecar' no longer carries the hash this run wrote; left in place")
                    $recoveryResidual.Add($sidecar)
                }
            }
        }
        catch {
            $recoveryFailures.Add("remove the sidecar '$sidecar' written by this run: $($_.Exception.Message)")
        }

        try {
            if ($state.NewArchivePlaced -and (Test-Path -LiteralPath $Archive)) {
                $currentArchiveHash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash
                # Ownership is established by this run's state: the file sits at the release path
                # only because the move above placed it there. A hash difference is suspicious and
                # reported, but the file is returned to the staging path (never deleted) instead of
                # being left behind as a would-be release ZIP.
                if ($currentArchiveHash -ne $ExpectedArchiveHash) {
                    $recoveryNotes.Add("archive '$Archive' no longer matches the hash this run verified ($currentArchiveHash); it is returned to the staging path for inspection")
                }
                if (Test-Path -LiteralPath $StagedArchive) {
                    $recoveryFailures.Add("staged archive path '$StagedArchive' is occupied; the new archive stays at '$Archive'")
                    $recoveryResidual.Add($Archive)
                }
                else {
                    Move-Item -LiteralPath $Archive -Destination $StagedArchive
                    $recoveryDone.Add("moved the new archive back to '$StagedArchive'")
                }
            }
        }
        catch {
            $recoveryFailures.Add("move the new archive back to '$StagedArchive': $($_.Exception.Message)")
        }

        try {
            if ($state.OldArchiveMoved -and (Test-Path -LiteralPath $archiveBackup)) {
                if (Test-Path -LiteralPath $Archive) {
                    $recoveryFailures.Add("ZIP path '$Archive' is occupied; the previous archive stays at '$archiveBackup'")
                    $recoveryResidual.Add($Archive)
                }
                else {
                    Move-Item -LiteralPath $archiveBackup -Destination $Archive
                    $recoveryDone.Add("restored the previous archive from '$archiveBackup'")
                }
            }
        }
        catch {
            $recoveryFailures.Add("restore the previous archive from '$archiveBackup': $($_.Exception.Message)")
        }

        try {
            if ($state.OldSidecarMoved -and (Test-Path -LiteralPath $sidecarBackup)) {
                if (Test-Path -LiteralPath $sidecar) {
                    $recoveryFailures.Add("sidecar path '$sidecar' is occupied; the previous hash stays at '$sidecarBackup'")
                    $recoveryResidual.Add($sidecar)
                }
                else {
                    Move-Item -LiteralPath $sidecarBackup -Destination $sidecar
                    $recoveryDone.Add("restored the previous sidecar from '$sidecarBackup'")
                }
            }
        }
        catch {
            $recoveryFailures.Add("restore the previous sidecar from '$sidecarBackup': $($_.Exception.Message)")
        }

        try {
            if ($state.NewPayloadPlaced -and (Test-Path -LiteralPath $Payload)) {
                if (Test-Path -LiteralPath $StagedPayload) {
                    $recoveryFailures.Add("staging path '$StagedPayload' is occupied; the new payload stays at '$Payload'")
                    $recoveryResidual.Add($Payload)
                }
                else {
                    Move-Item -LiteralPath $Payload -Destination $StagedPayload
                    $recoveryDone.Add("moved the new payload back to '$StagedPayload'")
                }
            }
        }
        catch {
            $recoveryFailures.Add("move the new payload back to '$StagedPayload': $($_.Exception.Message)")
        }

        try {
            if ($state.OldPayloadMoved -and (Test-Path -LiteralPath $payloadBackup)) {
                if (Test-Path -LiteralPath $Payload) {
                    $recoveryFailures.Add("payload path '$Payload' is occupied; the previous payload stays at '$payloadBackup'")
                    $recoveryResidual.Add($Payload)
                }
                else {
                    Move-Item -LiteralPath $payloadBackup -Destination $Payload
                    $recoveryDone.Add("restored the previous payload from '$payloadBackup'")
                }
            }
        }
        catch {
            $recoveryFailures.Add("restore the previous payload from '$payloadBackup': $($_.Exception.Message)")
        }

        $noteSuffix = ''
        if ($recoveryNotes.Count -gt 0) {
            $noteSuffix = " Rollback notes: $($recoveryNotes -join '; ')."
        }
        if ($recoveryFailures.Count -eq 0) {
            throw "Release commit failed and every previous artifact was rolled back ($($recoveryDone -join '; ')).$noteSuffix Root cause: $commitError"
        }
        $remainingBackups = @(@($payloadBackup, $archiveBackup, $sidecarBackup) | Where-Object { Test-Path -LiteralPath $_ })
        $partial = "Release commit failed ($commitError) and the rollback only partly succeeded: $($recoveryFailures -join '; ')."
        $partial += $noteSuffix
        if ($remainingBackups.Count -gt 0) {
            $partial += " The previous payload/ZIP/sidecar hash are kept at these backups and must be restored manually: $($remainingBackups -join ', ')."
        }
        $uniqueResidual = @($recoveryResidual | Select-Object -Unique)
        if ($uniqueResidual.Count -gt 0) {
            $partial += " Paths left holding artifacts of this failed run (not deleted, not overwritten): $($uniqueResidual -join ', ')."
        }
        throw $partial
    }
    return [pscustomobject]@{
        ArchiveHash = $committedHash
        PayloadBackup = $(if ($state.OldPayloadMoved) { $payloadBackup } else { $null })
        ArchiveBackup = $(if ($state.OldArchiveMoved) { $archiveBackup } else { $null })
        SidecarBackup = $(if ($state.OldSidecarMoved) { $sidecarBackup } else { $null })
    }
}

# Staging cleanup is restricted to the directory this run created: the leaf name must carry this
# run's transaction id and the directory must sit directly under the output base. Anything else -
# a user directory, another run's staging directory - is refused instead of deleted recursively.
function Remove-PublishStaging {
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$OutputBase,
        [Parameter(Mandatory)][string]$TransactionId
    )
    if (-not (Test-Path -LiteralPath $StagingRoot)) { return }
    $resolvedBase = [System.IO.Path]::GetFullPath($OutputBase).TrimEnd('\')
    $resolvedStaging = [System.IO.Path]::GetFullPath($StagingRoot).TrimEnd('\')
    $expectedLeaf = ".publish-staging-$TransactionId"
    if (-not $resolvedStaging.StartsWith($resolvedBase + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedStaging) -ne $expectedLeaf) {
        throw "Refusing to clean a staging directory this run did not create: $StagingRoot"
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
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

# The version gate is still a pre-publish gate: it rejects a non-release or non-matching -Version
# before any output path is created, modified or deleted, so a wrong version string cannot produce
# a manifest that disagrees with the binaries it describes.
$versionInput = Assert-ReleaseVersionInput -ExpectedVersion $Version -RepositoryRoot $root

# ---- staged transaction ----
# The payload and its ZIP are built in a unique staging directory under the output base and swapped
# into place only after every gate above and below has accepted them. The previous payload, ZIP and
# sidecar hash are never deleted: they are renamed to '.prev-<transaction>' backups, and a failed run
# cleans up its own staging directory instead of leaving a half-written or mixed output root.
$transactionId = (Get-Date -Format 'yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stagingRoot = Join-Path $outputBase ".publish-staging-$transactionId"
$stage = Join-Path $stagingRoot 'payload'
$stagedArchive = Join-Path $stagingRoot 'package.zip'
$stagedArchiveBuilding = Join-Path $stagingRoot 'package.zip.building'
if (Test-Path -LiteralPath $stagingRoot) {
    throw "Staging directory already exists and will not be reused: $stagingRoot"
}
New-Item -ItemType Directory -Force -Path $stage, (Join-Path $stage 'App'), (Join-Path $stage 'Guardian'), (Join-Path $stage 'scripts') | Out-Null
$commit = $null
try {

    Push-Location $root
    try {
        dotnet restore .\src\MiniProToolkit.App\MiniProToolkit.App.csproj -r win-x64
        if ($LASTEXITCODE -ne 0) { throw "Main application restore failed with exit code $LASTEXITCODE." }
        dotnet restore .\src\MiniProToolkit.Guardian\MiniProToolkit.Guardian.csproj -r win-x64
        if ($LASTEXITCODE -ne 0) { throw "Guardian restore failed with exit code $LASTEXITCODE." }
        dotnet publish .\src\MiniProToolkit.App\MiniProToolkit.App.csproj `
            -c Release -r win-x64 --self-contained true --no-restore `
            -p:PublishSingleFile=false -p:DebugType=None -o (Join-Path $stage 'App')
        if ($LASTEXITCODE -ne 0) { throw "Main application publish failed with exit code $LASTEXITCODE." }

        dotnet publish .\src\MiniProToolkit.Guardian\MiniProToolkit.Guardian.csproj `
            -c Release -r win-x64 --self-contained true --no-restore `
            -p:PublishSingleFile=false -p:DebugType=None -o (Join-Path $stage 'Guardian')
        if ($LASTEXITCODE -ne 0) { throw "Guardian publish failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }

    # Three-way version gate: the version the manifest will announce, the version in
    # Directory.Build.props and the version resource of the published App assembly must agree.
    # Without it the manifest could announce 0.2.0 while the shipped MiniProToolkit.dll is 0.1.0.
    Assert-PayloadAssemblyVersion -AssemblyPath (Join-Path $stage 'App\MiniProToolkit.dll') -ExpectedVersion $Version -PropsVersion $versionInput.PropsVersion

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
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptFile) -Destination (Join-Path $stage 'scripts') -Force
    }
    $docs = Join-Path $stage 'docs'
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

    $files = Get-ChildItem -LiteralPath $stage -File -Recurse |
        Where-Object { $_.Name -ne 'release-manifest.json' } |
        ForEach-Object {
            [pscustomobject]@{
                RelativePath = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
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
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding UTF8

    # ---- post-publish gates (payload self-check, capability symbols, manifest, archive) ----
    # They all run before the ZIP sidecar hash is written: a red gate can therefore never leave
    # a complete-looking package with a matching .sha256 behind.
    Write-Host 'Self-check: scripts\validate-package.ps1' -ForegroundColor Cyan
    $validateScript = Join-Path $stage 'scripts\validate-package.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validateScript -PackageRoot $stage
    if ($LASTEXITCODE -ne 0) {
        throw "Package self-check failed: the payload's own validate-package.ps1 exited with code $LASTEXITCODE. The payload, manifest and hashes of this run must not be shipped."
    }

    $copyPairs = @()
    foreach ($scriptFile in $scriptFiles) {
        $copyPairs += [pscustomobject]@{
            Source = (Join-Path $PSScriptRoot $scriptFile)
            Payload = (Join-Path (Join-Path $stage 'scripts') $scriptFile)
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
    Assert-CapabilitySymbols -PackageRoot $stage -Expected $capabilitySymbols
    Assert-ReleaseManifestConsistency -Manifest $manifest -PackageRoot $stage -ExpectedVersion $Version -PublishStartUtc $publishStartUtc

    # Archive. ZipFile.CreateFromDirectory + separator repair instead of Compress-Archive: the
    # latter writes '\' entry separators on Windows PowerShell 5.1, which is not the ZIP spec
    # ('/') and turns the directory tree into flat backslash file names on Linux/macOS tools.
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    # The archive is built inside the staging directory too; the published ZIP path is not touched
    # until the structure gate below has accepted this staged archive.
    if (Test-Path -LiteralPath $stagedArchive) { Remove-Item -LiteralPath $stagedArchive -Force }
    if (Test-Path -LiteralPath $stagedArchiveBuilding) { Remove-Item -LiteralPath $stagedArchiveBuilding -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage,
        $stagedArchiveBuilding,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    try {
        Repair-ZipEntrySeparators -SourceArchive $stagedArchiveBuilding -DestinationArchive $stagedArchive
    }
    finally {
        if (Test-Path -LiteralPath $stagedArchiveBuilding) { Remove-Item -LiteralPath $stagedArchiveBuilding -Force }
    }
    $zipSummary = Assert-ReleaseZipStructure -ArchivePath $stagedArchive -Manifest $manifest -RequiredEntries $requiredArchiveEntries
    Write-Host "Archive structure gate: $($zipSummary.FileCount) files in $($zipSummary.TopLevel -join ', ')." -ForegroundColor Green

    $releaseSourceFiles = Get-ReleaseSourceFile -RepositoryRoot $root -ShippedDocs ($shippedDocs + 'architecture.md')
    Assert-SourceNotNewerThanPublish -SourceFiles $releaseSourceFiles -PublishStartUtc $publishStartUtc

    $archiveHash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash
    $commit = Invoke-ReleaseCommit -StagedPayload $stage -StagedArchive $stagedArchive -Payload $output -Archive $archive -TransactionId $transactionId -ExpectedArchiveHash $archiveHash
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        try {
            Remove-PublishStaging -StagingRoot $stagingRoot -OutputBase $outputBase -TransactionId $transactionId
        }
        catch {
            Write-Warning "Staging cleanup failed ($($_.Exception.Message)). Remove '$stagingRoot' manually; the committed payload, ZIP and sidecar were not affected."
        }
    }
}

Write-Host "Published package: $output" -ForegroundColor Green
Write-Host "Archive: $archive" -ForegroundColor Green
Write-Host "Archive SHA-256: $($commit.ArchiveHash)" -ForegroundColor Cyan
if ($null -ne $commit.PayloadBackup) { Write-Host "Previous payload kept as: $($commit.PayloadBackup)" -ForegroundColor DarkGray }
if ($null -ne $commit.ArchiveBackup) { Write-Host "Previous archive kept as: $($commit.ArchiveBackup)" -ForegroundColor DarkGray }
if ($null -ne $commit.SidecarBackup) { Write-Host "Previous sidecar kept as: $($commit.SidecarBackup)" -ForegroundColor DarkGray }
if ($null -ne $commit.PayloadBackup -or $null -ne $commit.ArchiveBackup) {
    Write-Host 'The .prev-* paths above are the replaced artifacts of this run; delete them after the new package is accepted.' -ForegroundColor Cyan
}
Write-Host 'All publish gates passed (test/AST/version/copy/capability/manifest/archive/freshness).' -ForegroundColor Green
Write-Host 'Run scripts\build-installer.ps1 next; installation itself stays a target-machine step.' -ForegroundColor Cyan
