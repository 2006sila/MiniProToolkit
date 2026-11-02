# Offline fixture for scripts\package-common.ps1: the %ProgramData% purge whitelist, its
# fail-closed guards and the required-payload list.
#
# The script under test is COPIED into a temporary directory and dot-sourced there, and every purge
# runs against a sandbox whose $env:ProgramData / $env:LOCALAPPDATA point into that sandbox. The
# real %ProgramData%\MiniProToolkit, the repository tree, services, leases and the installer are
# never touched. Nothing is elevated and no real publish/install runs.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\test-package-common-purge.ps1
# Exit codes: 0 = every check passed (skips are reported), 1 = at least one check failed.
[CmdletBinding()]
param(
    [string]$WorkRoot
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceScript = Join-Path (Join-Path $repositoryRoot 'scripts') 'package-common.ps1'
if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) {
    Write-Host "Missing script under test: $sourceScript"
    exit 1
}

$work = if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    Join-Path (Join-Path $repositoryRoot 'temp') ('purge-fixture-' + [Guid]::NewGuid().ToString('N'))
}
else { $WorkRoot }
$work = [System.IO.Path]::GetFullPath($work)
if (-not $work.StartsWith([System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'temp')), [StringComparison]::OrdinalIgnoreCase) -and
    -not [string]::IsNullOrWhiteSpace($WorkRoot)) {
    # An explicit -WorkRoot may live elsewhere, but it must never be %ProgramData% itself.
    $programDataFull = [System.IO.Path]::GetFullPath(${env:ProgramData})
    if ($work.StartsWith($programDataFull, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "-WorkRoot must not point into the real %ProgramData%."
        exit 1
    }
}

$failures = New-Object System.Collections.Generic.List[string]
$skips = New-Object System.Collections.Generic.List[string]
function Assert-Check {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "ok   $Message" }
    else { Write-Host "FAIL $Message"; $script:failures.Add($Message) }
}

$originalProgramData = ${env:ProgramData}
$originalLocalAppData = ${env:LOCALAPPDATA}
$sandbox = $null
try {
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    # 1. Static: the purge whitelist is an exact-name list, never a pattern.
    $lines = @(Get-Content -LiteralPath $sourceScript)
    function Get-AssignmentBlock {
        param([string[]]$Text, [string]$Pattern)
        $start = -1
        for ($index = 0; $index -lt $Text.Count; $index++) {
            if ($Text[$index] -match $Pattern) { $start = $index; break }
        }
        if ($start -lt 0) { return $null }
        $depth = 0
        for ($index = $start; $index -lt $Text.Count; $index++) {
            $depth += ([regex]::Matches($Text[$index], '\(')).Count
            $depth -= ([regex]::Matches($Text[$index], '\)')).Count
            if ($depth -le 0) { return @($Text[$start..$index]) }
        }
        return $null
    }

    $disposableBlock = Get-AssignmentBlock -Text $lines -Pattern '^\s*\$disposable\s*=\s*@\('
    Assert-Check ($null -ne $disposableBlock) 'package-common: the $disposable whitelist block was found'
    if ($null -ne $disposableBlock) {
        $blockText = $disposableBlock -join "`n"
        $names = @([regex]::Matches($blockText, "'(?<name>[^']*)'") | ForEach-Object { $_.Groups['name'].Value })
        $expected = @(
            'guardian.log', 'guardian.log.1', 'guardian.log.2', 'guardian.log.3',
            'diagnostic-events.jsonl', 'diagnostic-events.jsonl.1', 'diagnostic-events.jsonl.2',
            'diagnostic-events.jsonl.3', 'diagnostic-events.jsonl.4'
        )
        Assert-Check (($names -join '|') -eq ($expected -join '|')) "package-common: the purge whitelist is exactly guardian.log(.1..3) + diagnostic-events.jsonl(.1..4) [found: $($names -join ', ')]"
        Assert-Check (-not ($blockText -match '\*|\?|-like|StartsWith|EndsWith|GetFiles')) 'package-common: the purge whitelist uses no wildcard/prefix/glob matching'
    }

    $requiredBlock = Get-AssignmentBlock -Text $lines -Pattern '^\s*\$required\s*=\s*@\('
    Assert-Check ($null -ne $requiredBlock) 'package-common: the $required payload block was found'
    $requiredRelative = @()
    if ($null -ne $requiredBlock) {
        $requiredRelative = @([regex]::Matches(($requiredBlock -join "`n"), "Join-Path \`$PackageRoot '(?<rel>[^']+)'") |
            ForEach-Object { $_.Groups['rel'].Value })
        Assert-Check ($requiredRelative -contains 'docs\performance-review.md') 'package-common: Assert-PackagePayload requires docs\performance-review.md'
        Assert-Check ($requiredRelative -contains 'scripts\validate-package.ps1') 'package-common: Assert-PackagePayload requires scripts\validate-package.ps1'
        Assert-Check (($requiredRelative -notcontains 'docs\gap-list.md') -and ($requiredRelative -notcontains 'docs\console-product-plan.md') -and ($requiredRelative -notcontains 'docs\LICENSE-DECISION-REQUIRED.md')) 'package-common: internal docs stay out of the required payload list'
    }

    # 2. Runtime (offline copy): copy the script, sandbox %ProgramData%, exercise the purge.
    $copyDirectory = Join-Path $work 'offline-scripts'
    New-Item -ItemType Directory -Force -Path $copyDirectory | Out-Null
    $copyScript = Join-Path $copyDirectory 'package-common.ps1'
    Copy-Item -LiteralPath $sourceScript -Destination $copyScript -Force
    Assert-Check ((Get-FileHash -LiteralPath $copyScript -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $sourceScript -Algorithm SHA256).Hash) 'fixture: the offline copy is byte-identical to the repository script'
    . $copyScript

    $caseIndex = 0
    function New-Sandbox {
        # A fresh fake %ProgramData%/%LOCALAPPDATA% pair; never the real ones.
        param([string]$CaseName)
        $script:caseIndex++
        $sandboxRoot = Join-Path $work ('case-{0:d2}-{1}' -f $script:caseIndex, $CaseName)
        New-Item -ItemType Directory -Force -Path $sandboxRoot | Out-Null
        $fakeProgramData = Join-Path $sandboxRoot 'ProgramData'
        New-Item -ItemType Directory -Force -Path $fakeProgramData | Out-Null
        $env:ProgramData = $fakeProgramData
        $env:LOCALAPPDATA = (Join-Path $sandboxRoot 'LocalAppData')
        $root = Join-Path $fakeProgramData 'MiniProToolkit'
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        return [pscustomobject]@{ Root = $root; ProgramData = $fakeProgramData }
    }

    function New-DisposableFiles {
        param([string]$Root)
        $names = @(
            'guardian.log', 'guardian.log.1', 'guardian.log.2', 'guardian.log.3',
            'diagnostic-events.jsonl', 'diagnostic-events.jsonl.1', 'diagnostic-events.jsonl.2',
            'diagnostic-events.jsonl.3', 'diagnostic-events.jsonl.4'
        )
        foreach ($name in $names) { Set-Content -LiteralPath (Join-Path $Root $name) -Value 'fixture' -Encoding UTF8 }
        return $names
    }

    function Invoke-PurgeOffline {
        # Returns the failure message, or '' when the purge succeeded.
        try { Remove-GuardianProgramData; return '' }
        catch { return $_.Exception.Message }
    }

    $env:ProgramData = $originalProgramData
    $env:LOCALAPPDATA = $originalLocalAppData

    # 2a. All nine whitelisted artifacts plus the retained lock file: purge succeeds, lock survives.
    $case = New-Sandbox 'full-whitelist'
    $names = New-DisposableFiles -Root $case.Root
    $lockPath = Join-Path $case.Root 'guardian-lease.json.lock'
    Set-Content -LiteralPath $lockPath -Value '' -Encoding UTF8
    $message = Invoke-PurgeOffline
    Assert-Check ($message -eq '') "purge: the exact whitelist deletes cleanly [message: $message]"
    $survivors = @($names | Where-Object { Test-Path -LiteralPath (Join-Path $case.Root $_) })
    Assert-Check ($survivors.Count -eq 0) "purge: all nine disposable artifacts are gone [left: $($survivors -join ', ')]"
    Assert-Check (Test-Path -LiteralPath $lockPath) 'purge: the lease lock file is never deleted'
    Assert-Check (Test-Path -LiteralPath $case.Root) 'purge: the directory survives while the lock file exists'

    # 2b. Only disposable artifacts: the now-empty directory is removed as well.
    $case = New-Sandbox 'empty-directory'
    [void](New-DisposableFiles -Root $case.Root)
    $message = Invoke-PurgeOffline
    Assert-Check ($message -eq '') "purge: the whitelist-only directory deletes cleanly [message: $message]"
    Assert-Check (-not (Test-Path -LiteralPath $case.Root)) 'purge: an empty program data directory is removed'

    # 2c. An unrecognised file blocks the whole purge and nothing is deleted.
    $case = New-Sandbox 'unknown-file'
    [void](New-DisposableFiles -Root $case.Root)
    Set-Content -LiteralPath (Join-Path $case.Root 'notes.txt') -Value 'x' -Encoding UTF8
    $message = Invoke-PurgeOffline
    Assert-Check ($message -match 'unrecognised') "purge: an unknown file fails closed [message: $message]"
    Assert-Check (Test-Path -LiteralPath (Join-Path $case.Root 'guardian.log')) 'purge: an unknown file leaves the disposable artifacts in place'

    # 2d. Out-of-bound rotation numbers are not whitelisted.
    foreach ($name in @('guardian.log.4', 'diagnostic-events.jsonl.5')) {
        $case = New-Sandbox ('rotation-' + ($name -replace '[^A-Za-z0-9]', '-'))
        [void](New-DisposableFiles -Root $case.Root)
        Set-Content -LiteralPath (Join-Path $case.Root $name) -Value 'x' -Encoding UTF8
        $message = Invoke-PurgeOffline
        Assert-Check ($message -match 'unrecognised') "purge: $name is not whitelisted [message: $message]"
        Assert-Check (Test-Path -LiteralPath (Join-Path $case.Root $name)) "purge: $name is left in place"
    }

    # 2e. A subdirectory blocks the purge.
    $case = New-Sandbox 'subdirectory'
    [void](New-DisposableFiles -Root $case.Root)
    New-Item -ItemType Directory -Force -Path (Join-Path $case.Root 'history') | Out-Null
    $message = Invoke-PurgeOffline
    Assert-Check ($message -match 'unrecognised') "purge: a subdirectory fails closed [message: $message]"
    Assert-Check (Test-Path -LiteralPath (Join-Path $case.Root 'diagnostic-events.jsonl.4')) 'purge: a subdirectory leaves the files in place'

    # 2f. An active lease blocks the purge before anything is deleted.
    $case = New-Sandbox 'active-lease'
    [void](New-DisposableFiles -Root $case.Root)
    Set-Content -LiteralPath (Join-Path $case.Root 'guardian-lease.json') -Value '{}' -Encoding UTF8
    $message = Invoke-PurgeOffline
    Assert-Check ($message -match 'lease exists') "purge: an active lease fails closed [message: $message]"
    Assert-Check (Test-Path -LiteralPath (Join-Path $case.Root 'guardian.log.1')) 'purge: an active lease leaves the files in place'

    # 2g. Reparse points: a link with a whitelisted name, and a linked program data root.
    $case = New-Sandbox 'linked-entry'
    [void](New-DisposableFiles -Root $case.Root)
    $decoy = Join-Path $case.ProgramData 'decoy'
    New-Item -ItemType Directory -Force -Path $decoy | Out-Null
    Set-Content -LiteralPath (Join-Path $decoy 'guardian.log.1') -Value 'keep' -Encoding UTF8
    $junctionPath = Join-Path $case.Root 'guardian.log.1'
    $linkMade = $false
    try {
        Remove-Item -LiteralPath $junctionPath -Force
        New-Item -ItemType Junction -Path $junctionPath -Target $decoy -ErrorAction Stop | Out-Null
        $linkMade = $true
    }
    catch { $skips.Add("purge: could not create a junction fixture ($($_.Exception.Message))") }
    if ($linkMade) {
        $message = Invoke-PurgeOffline
        Assert-Check ($message -ne '') "purge: a linked entry is refused [message: $message]"
        Assert-Check (Test-Path -LiteralPath (Join-Path $decoy 'guardian.log.1')) 'purge: a linked entry is never deleted through the link'
    }

    $case = New-Sandbox 'linked-root'
    $decoyRoot = Join-Path $case.ProgramData 'decoy-root'
    New-Item -ItemType Directory -Force -Path $decoyRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $decoyRoot 'guardian.log') -Value 'keep' -Encoding UTF8
    Remove-Item -LiteralPath $case.Root -Force
    $rootLinkMade = $false
    try {
        New-Item -ItemType Junction -Path $case.Root -Target $decoyRoot -ErrorAction Stop | Out-Null
        $rootLinkMade = $true
    }
    catch { $skips.Add("purge: could not create a program-data junction fixture ($($_.Exception.Message))") }
    if ($rootLinkMade) {
        $message = Invoke-PurgeOffline
        Assert-Check ($message -match 'reparse point') "purge: a linked program data root is refused [message: $message]"
        Assert-Check (Test-Path -LiteralPath (Join-Path $decoyRoot 'guardian.log')) 'purge: a linked root never deletes the link target content'
    }

    # 2h. A file symbolic link with a whitelisted name is refused by the reparse check. Creating a
    # file link needs elevation or Developer Mode, so an unprivileged machine records a skip.
    $case = New-Sandbox 'linked-file'
    [void](New-DisposableFiles -Root $case.Root)
    $decoyFile = Join-Path $case.ProgramData 'decoy.txt'
    Set-Content -LiteralPath $decoyFile -Value 'keep' -Encoding UTF8
    $fileLinkPath = Join-Path $case.Root 'diagnostic-events.jsonl.2'
    $fileLinkMade = $false
    try {
        Remove-Item -LiteralPath $fileLinkPath -Force
        New-Item -ItemType SymbolicLink -Path $fileLinkPath -Target $decoyFile -ErrorAction Stop | Out-Null
        $fileLinkMade = $true
    }
    catch { $skips.Add("purge: no unprivileged file-symlink fixture on this machine ($($_.Exception.Message)); the directory-junction cases cover the reparse refusal") }
    if ($fileLinkMade) {
        $message = Invoke-PurgeOffline
        Assert-Check ($message -match 'reparse point') "purge: a linked file entry is refused as a reparse point [message: $message]"
        Assert-Check (Test-Path -LiteralPath $decoyFile) 'purge: a linked file entry never deletes the link target content'
    }

    # 3. Runtime (offline copy): the required-payload list is exact and fails closed.
    $fakePackage = Join-Path $work 'fake-payload'
    New-Item -ItemType Directory -Force -Path $fakePackage | Out-Null
    foreach ($relative in $requiredRelative) {
        $target = Join-Path $fakePackage $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Set-Content -LiteralPath $target -Value 'fixture' -Encoding UTF8
    }
    $payloadError = ''
    try { Assert-PackagePayload -PackageRoot $fakePackage } catch { $payloadError = $_.Exception.Message }
    Assert-Check ($payloadError -eq '') "payload: a payload with every required file passes the gate [message: $payloadError]"
    foreach ($removed in @('docs\performance-review.md', 'scripts\validate-package.ps1')) {
        $target = Join-Path $fakePackage $removed
        $backup = $target + '.bak'
        Move-Item -LiteralPath $target -Destination $backup
        $message = ''
        try { Assert-PackagePayload -PackageRoot $fakePackage } catch { $message = $_.Exception.Message }
        Assert-Check ($message -match 'incomplete') "payload: a payload without $removed fails the gate [message: $message]"
        Move-Item -LiteralPath $backup -Destination $target
    }
}
catch {
    Write-Host "FAIL fixture error: $($_.Exception.Message)"
    $failures.Add($_.Exception.Message)
}
finally {
    $env:ProgramData = $originalProgramData
    $env:LOCALAPPDATA = $originalLocalAppData
}

foreach ($skip in $skips) { Write-Host "skip $skip" }
if ($failures.Count -gt 0) {
    Write-Host "FAILED: $($failures.Count) check(s) failed (work directory: $work)" -ForegroundColor Red
    exit 1
}
Write-Host "PASSED: package-common purge/payload fixture (offline copy, work directory: $work)" -ForegroundColor Green
exit 0
