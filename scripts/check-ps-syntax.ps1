# Static checker for the PowerShell lifecycle scripts. It never runs, dot-sources or edits a script:
# it parses the files with the PowerShell parser and inspects the resulting AST / text, so it is safe
# on a machine where the package must not be installed, upgraded, repaired, uninstalled or recovered.
#
# Usage (Windows PowerShell 5.1):
#   .\scripts\check-ps-syntax.ps1                       # lifecycle script set: syntax + invariants
#   .\scripts\check-ps-syntax.ps1 -All                  # every scripts\*.ps1: syntax + invariants
#   .\scripts\check-ps-syntax.ps1 -Path .\scripts\a.ps1 # syntax only for the listed files
#   .\scripts\check-ps-syntax.ps1 -SkipLifecycleChecks  # syntax only
#
# Exit codes: 0 = every file parsed and every checked invariant held; 2 = at least one failure.
[CmdletBinding()]
param(
    [string[]]$Path,
    [switch]$All,
    [switch]$SkipLifecycleChecks
)

$ErrorActionPreference = 'Stop'

# The default set is the lifecycle surface: the scripts that ship in the payload plus the two
# helpers that can write EC automatic / delete a lease.
$lifecycleFiles = @(
    'package-common.ps1',
    'install-package.ps1',
    'upgrade-package.ps1',
    'repair-package.ps1',
    'uninstall-package.ps1',
    'uninstall-guardian.ps1',
    'restore-ec-automatic.ps1',
    'validate-package.ps1'
)
$orderedScripts = @('install-package.ps1', 'upgrade-package.ps1', 'repair-package.ps1', 'uninstall-package.ps1')

if (-not $Path -or $Path.Count -eq 0) {
    if ($All) {
        $Path = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' | ForEach-Object { $_.FullName })
    }
    else {
        $Path = @($lifecycleFiles | ForEach-Object { Join-Path $PSScriptRoot $_ })
    }
}

function Get-FirstCommandIndex {
    param([object[]]$Commands, [string]$Name)
    for ($index = 0; $index -lt $Commands.Count; $index++) {
        if ($Commands[$index].GetCommandName() -eq $Name) { return $index }
    }
    return -1
}

$failures = @()
$parsed = @{}

foreach ($file in $Path) {
    $resolvedPath = (Resolve-Path -LiteralPath $file).Path
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($resolvedPath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        foreach ($parseError in $parseErrors) {
            $failures += "$resolvedPath($($parseError.Extent.StartLineNumber)): $($parseError.Message)"
        }
        continue
    }
    $parsed[$resolvedPath] = $ast
    Write-Output "$resolvedPath syntax ok"
}

if (-not $SkipLifecycleChecks) {
    # Safety contract of the package scripts: authorize the lease decision, then stop the application
    # and confirm it is gone, and only then restore EC automatic (write 101 + compare-and-clear).
    foreach ($name in $orderedScripts) {
        $file = $null
        foreach ($candidate in @($parsed.Keys)) {
            if ([System.IO.Path]::GetFileName($candidate) -eq $name) { $file = $candidate; break }
        }
        if ($null -eq $file) { continue }
        $commands = @($parsed[$file].FindAll(
            { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
            $true))
        $authorizationIndex = Get-FirstCommandIndex $commands 'Assert-RestoreAuthorization'
        $stopIndex = Get-FirstCommandIndex $commands 'Stop-MiniProToolkitGracefully'
        $restoreIndex = Get-FirstCommandIndex $commands 'Invoke-RestoreIfRequired'
        if ($authorizationIndex -lt 0) {
            $failures += "${name}: Assert-RestoreAuthorization is missing; a lease could be settled without an explicit switch."
        }
        if ($stopIndex -lt 0) {
            $failures += "${name}: Stop-MiniProToolkitGracefully is missing; EC recovery could run while the application still owns the hardware."
        }
        if ($restoreIndex -lt 0) {
            $failures += "${name}: Invoke-RestoreIfRequired is missing; an active lease would not be settled."
        }
        if ($authorizationIndex -ge 0 -and $stopIndex -ge 0 -and $authorizationIndex -gt $stopIndex) {
            $failures += "${name}: Assert-RestoreAuthorization must run before Stop-MiniProToolkitGracefully."
        }
        if ($stopIndex -ge 0 -and $restoreIndex -ge 0 -and $stopIndex -gt $restoreIndex) {
            $failures += "${name}: Stop-MiniProToolkitGracefully must run before Invoke-RestoreIfRequired."
        }
    }

    # Only the single locked compare-and-clear call site may delete the lease record, no script may
    # delete the lease lock file, and %ProgramData%\MiniProToolkit is never deleted recursively.
    foreach ($file in @($parsed.Keys)) {
        $name = [System.IO.Path]::GetFileName($file)
        if ($name -eq 'check-ps-syntax.ps1') { continue }
        $lines = @(Get-Content -LiteralPath $file)
        for ($lineNumber = 0; $lineNumber -lt $lines.Count; $lineNumber++) {
            $line = $lines[$lineNumber]
            if ($line.TrimStart().StartsWith('#')) { continue }
            if ($line -notmatch 'Remove-Item') { continue }
            if ($line -match 'guardian-lease') {
                $failures += "${name}($($lineNumber + 1)): the Guardian lease must only be deleted through the locked compare-and-clear helper."
            }
            if ($line -match 'lease\.json\.lock' -or $line -match 'LeaseLockPath' -or $line -match '\$lockPath') {
                $failures += "${name}($($lineNumber + 1)): the lease lock file must never be deleted."
            }
            if ($line -match 'Get-ProgramDataRoot' -and $line -match 'Recurse') {
                $failures += "${name}($($lineNumber + 1)): %ProgramData%\MiniProToolkit must not be deleted recursively (the lease and its lock live there)."
            }
        }
    }

    $restoreFile = Join-Path $PSScriptRoot 'restore-ec-automatic.ps1'
    if ($parsed.ContainsKey($restoreFile)) {
        $restoreText = Get-Content -LiteralPath $restoreFile -Raw
        if ($restoreText -notmatch 'Remove-GuardianLeaseIfUnchanged') {
            $failures += 'restore-ec-automatic.ps1: the lease must be cleared through Remove-GuardianLeaseIfUnchanged (locked compare-and-clear).'
        }
        if ($restoreText -notmatch 'SetFanControl') {
            $failures += 'restore-ec-automatic.ps1: the EC automatic WMI write (SetFanControl) is missing.'
        }
        if ($restoreText -match 'SilentlyContinue') {
            $failures += 'restore-ec-automatic.ps1: lease handling must fail closed, SilentlyContinue is not allowed.'
        }
    }

    $uninstallFile = Join-Path $PSScriptRoot 'uninstall-package.ps1'
    if ($parsed.ContainsKey($uninstallFile)) {
        $uninstallText = Get-Content -LiteralPath $uninstallFile -Raw
        if ($uninstallText -notmatch 'Remove-GuardianProgramData') {
            $failures += 'uninstall-package.ps1: -PurgeUserData must use Remove-GuardianProgramData (whitelist) instead of a recursive delete.'
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 2
}
Write-Output "All $($parsed.Count) script(s) passed."
exit 0
