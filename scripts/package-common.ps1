Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ServiceName = 'MiniProToolkitGuardian'
$script:AppProcessName = 'MiniProToolkit'
$script:RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:RunValueName = 'MiniProToolkit'

function Get-PackageRoot {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    }
    return (Resolve-Path $Value).Path
}

function Get-DefaultInstallRoot {
    return (Join-Path ${env:ProgramFiles} 'MiniProToolkit')
}

function Get-LeasePath {
    return (Join-Path ${env:ProgramData} 'MiniProToolkit\guardian-lease.json')
}

function Get-LeaseLockPath {
    return (Get-LeasePath) + '.lock'
}

# Mirrors GuardianLeaseStore: the cross-process lease lock is the sibling <lease>.lock file opened
# read-only with FileShare.None, retried every 10 ms with a total wait bounded to 300 ms (10 ms x 30).
# Unlike the C# helper - where a busy lock is a safe no-op - every caller here must fail closed.
$script:LeaseLockRetryMilliseconds = 10
$script:LeaseLockTimeoutMilliseconds = 300

function Open-GuardianLeaseLock {
    param([string]$LeasePath = (Get-LeasePath))
    $lockPath = $LeasePath + '.lock'
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $stream = $null
        try {
            $stream = [System.IO.FileStream]::new(
                $lockPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::None,
                1,
                [System.IO.FileOptions]::None)
        }
        catch [System.IO.IOException] {
            $stream = $null
        }
        catch [System.UnauthorizedAccessException] {
            $stream = $null
        }
        if ($null -ne $stream) {
            $watch.Stop()
            return $stream
        }
        if ($watch.ElapsedMilliseconds -ge $script:LeaseLockTimeoutMilliseconds) { break }
        Start-Sleep -Milliseconds $script:LeaseLockRetryMilliseconds
    }
    $watch.Stop()
    throw "The Guardian lease lock '$lockPath' stayed busy for $($script:LeaseLockTimeoutMilliseconds) ms; the lease was left untouched."
}

function Read-GuardianLease {
    param([string]$LeasePath = (Get-LeasePath))
    # Lock-free read, exactly like GuardianLeaseStore.Read(): the owner replaces the record with an
    # atomic rename, so a reader sees either the old or the new record. A missing file means "no
    # lease"; an unreadable or unparsable file is fatal, because "cannot tell" must never be
    # treated as "nothing to do".
    if (-not (Test-Path -LiteralPath $LeasePath -PathType Leaf)) { return $null }
    try {
        $raw = Get-Content -LiteralPath $LeasePath -Raw -ErrorAction Stop
    }
    catch {
        throw "Unable to read the Guardian lease '$LeasePath': $($_.Exception.Message)"
    }
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "The Guardian lease '$LeasePath' is empty; the owning process cannot be identified."
    }
    try {
        return ($raw | ConvertFrom-Json -ErrorAction Stop)
    }
    catch {
        throw "The Guardian lease '$LeasePath' is not valid JSON: $($_.Exception.Message)"
    }
}

function ConvertTo-LeaseIdentityText {
    param([object]$Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [DateTime]) {
        return $Value.ToUniversalTime().ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [DateTimeOffset]) {
        return $Value.ToUniversalTime().ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
    }
    return [string]$Value
}

function Get-GuardianLeaseIdentity {
    # Owner identity of a lease record: processId | processStartUtc | heartbeatUtc | controlMode.
    # Two records with the same identity describe the same owner and state, so deleting either one
    # is equivalent. processStartUtc is optional in GuardianLease, the other three are not.
    param([object]$Lease)
    if ($null -eq $Lease) { return $null }
    if ($Lease -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'The Guardian lease record is not a JSON object; the owning process cannot be identified.'
    }
    $parts = @()
    foreach ($name in @('processId', 'processStartUtc', 'heartbeatUtc', 'controlMode')) {
        $property = $Lease.PSObject.Properties[$name]
        if ($null -eq $property) {
            if ($name -eq 'processStartUtc') {
                $parts += ConvertTo-LeaseIdentityText $null
                continue
            }
            throw "The Guardian lease record is missing '$name'; the owning process cannot be identified."
        }
        $parts += ConvertTo-LeaseIdentityText $property.Value
    }
    return ($parts -join '|')
}

function Remove-GuardianLeaseIfUnchanged {
    # Compare-and-clear under the cross-process lease lock. The record is re-read inside the lock and
    # deleted only when its owner identity is still the one the caller evaluated. A busy lock, a
    # disappeared record or a changed record throws: it never deletes ownership it did not decide
    # about, and it never reports success. The <lease>.lock file itself is only released, never
    # deleted (GuardianLeaseStore depends on that).
    param([object]$ExpectedLease, [string]$LeasePath = (Get-LeasePath))
    if ($null -eq $ExpectedLease) {
        throw 'A Guardian lease snapshot is required before the lease may be cleared.'
    }
    $expectedIdentity = Get-GuardianLeaseIdentity $ExpectedLease
    $lock = Open-GuardianLeaseLock -LeasePath $LeasePath
    try {
        $current = Read-GuardianLease -LeasePath $LeasePath
        if ($null -eq $current) {
            throw "The Guardian lease '$LeasePath' disappeared while the lease lock was held; nothing was deleted."
        }
        $currentIdentity = Get-GuardianLeaseIdentity $current
        if ($currentIdentity -ne $expectedIdentity) {
            throw "The Guardian lease changed during recovery (evaluated: '$expectedIdentity', found: '$currentIdentity'); it was left in place."
        }
        try {
            Remove-Item -LiteralPath $LeasePath -Force -ErrorAction Stop
        }
        catch {
            throw "Unable to delete the Guardian lease '$LeasePath': $($_.Exception.Message)"
        }
        if (Test-Path -LiteralPath $LeasePath -PathType Leaf) {
            throw "The Guardian lease '$LeasePath' still exists after it was deleted."
        }
    }
    finally {
        $lock.Dispose()
    }
}

function Get-ProgramDataRoot {
    return (Join-Path ${env:ProgramData} 'MiniProToolkit')
}

function Get-UserDataRoot {
    return (Join-Path ${env:LOCALAPPDATA} 'MiniProToolkit')
}

function Assert-PackagePayload {
    param([string]$PackageRoot)
    $required = @(
        (Join-Path $PackageRoot 'App\MiniProToolkit.exe'),
        (Join-Path $PackageRoot 'Guardian\MiniProToolkit.Guardian.exe'),
        (Join-Path $PackageRoot 'scripts\package-common.ps1'),
        (Join-Path $PackageRoot 'scripts\install-package.ps1'),
        (Join-Path $PackageRoot 'scripts\upgrade-package.ps1'),
        (Join-Path $PackageRoot 'scripts\repair-package.ps1'),
        (Join-Path $PackageRoot 'scripts\uninstall-package.ps1'),
        (Join-Path $PackageRoot 'scripts\restore-ec-automatic.ps1'),
        (Join-Path $PackageRoot 'docs\README.md'),
        (Join-Path $PackageRoot 'docs\architecture.md'),
        (Join-Path $PackageRoot 'docs\release-packaging.md'),
        (Join-Path $PackageRoot 'docs\phase-3-validation.md'),
        (Join-Path $PackageRoot 'docs\phase-4-validation.md'),
        (Join-Path $PackageRoot 'docs\phase-5-validation.md'),
        (Join-Path $PackageRoot 'docs\fan-calibration.md'),
        (Join-Path $PackageRoot 'docs\fan-control-hardening.md'),
        (Join-Path $PackageRoot 'docs\history-and-diagnostics.md'),
        (Join-Path $PackageRoot 'docs\profile-automation-and-installer.md'),
        (Join-Path $PackageRoot 'docs\product-roadmap.md'),
        (Join-Path $PackageRoot 'docs\THIRD-PARTY-NOTICES.md'),
        (Join-Path $PackageRoot 'release-manifest.json')
    )
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Package payload is incomplete: $path"
        }
    }
}

function Get-ReleaseManifest {
    param([string]$PackageRoot)
    $path = Join-Path $PackageRoot 'release-manifest.json'
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Assert-MiniProToolkitStopped {
    # The lifecycle order is "stop the application, confirm it is gone, then touch the lease". No
    # caller may write EC automatic (101) or replace payload files while an owner can still be
    # running, and a surviving process is fatal - it is never force-killed here.
    $running = @(Get-Process -Name $script:AppProcessName -ErrorAction SilentlyContinue |
        Where-Object { -not $_.HasExited })
    if ($running.Count -eq 0) { return }
    $processIds = @($running | ForEach-Object { $_.Id })
    throw "MiniProToolkit is still running (PID: $($processIds -join ', ')). The package scripts do not force-kill it: close it normally and retry."
}

function Stop-MiniProToolkitGracefully {
    $signal = $null
    try {
        $signal = [System.Threading.EventWaitHandle]::OpenExisting('Local\MiniProToolkit.Shutdown')
        [void]$signal.Set()
    }
    catch [System.Threading.WaitHandleCannotBeOpenedException] {
        # The application is not running or is an older build without the signal.
    }
    catch {
        throw "Unable to signal MiniProToolkit for graceful shutdown: $($_.Exception.Message)"
    }
    finally {
        if ($signal) { $signal.Dispose() }
    }

    $processes = @(Get-Process -Name $script:AppProcessName -ErrorAction SilentlyContinue |
        Where-Object { -not $_.HasExited })
    foreach ($process in $processes) {
        try {
            [void]$process.CloseMainWindow()
        }
        catch {
            throw "Unable to request a graceful shutdown for PID $($process.Id): $($_.Exception.Message)"
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $remaining = @(Get-Process -Name $script:AppProcessName -ErrorAction SilentlyContinue |
            Where-Object { -not $_.HasExited })
        if ($remaining.Count -eq 0) {
            Assert-MiniProToolkitStopped
            return
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "MiniProToolkit is still running. Close it normally and retry; the package script will not force-kill it."
}

function Assert-RestoreAuthorization {
    # Read-only pre-flight: an active lease is never cleared implicitly, and it is never cleared
    # while the application may still be running. Nothing is written or deleted here.
    param([switch]$RestoreEcAutomatic)
    $leasePath = Get-LeasePath
    if (-not (Test-Path -LiteralPath $leasePath -PathType Leaf)) { return }
    if (-not $RestoreEcAutomatic) {
        throw "An active Guardian lease exists at $leasePath. Re-run with -RestoreEcAutomatic after confirming the hardware may be returned to EC automatic control."
    }
}

function Invoke-RestoreIfRequired {
    param([switch]$RestoreEcAutomatic)
    $leasePath = Get-LeasePath
    $lockPath = Get-LeaseLockPath
    if (-not (Test-Path -LiteralPath $leasePath -PathType Leaf)) { return }
    if (-not $RestoreEcAutomatic) {
        throw "An active Guardian lease exists at $leasePath. Re-run with -RestoreEcAutomatic after confirming the hardware may be returned to EC automatic control."
    }
    # Ordering precondition: the application must already be stopped. A live owner keeps renewing the
    # heartbeat, so writing 101 underneath it would be unsafe and the lease could not be cleared.
    Assert-MiniProToolkitStopped

    $restoreScript = Join-Path $PSScriptRoot 'restore-ec-automatic.ps1'
    if (-not (Test-Path -LiteralPath $restoreScript -PathType Leaf)) {
        throw "The recovery script is missing: $restoreScript"
    }
    $lockExisted = Test-Path -LiteralPath $lockPath -PathType Leaf
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $restoreScript
    $restoreExitCode = $LASTEXITCODE
    if ($restoreExitCode -ne 0) {
        throw "EC automatic recovery failed with exit code $restoreExitCode. The lifecycle operation was aborted and the Guardian lease was left in place."
    }
    # The recovery script must never delete the lease lock; only release it.
    if ($lockExisted -and -not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        throw "EC automatic recovery removed the lease lock file '$lockPath'. The lease lock must survive; the lifecycle operation was aborted."
    }
    # Re-confirm the outcome instead of trusting the child process alone.
    Assert-MiniProToolkitStopped
    if (Test-Path -LiteralPath $leasePath -PathType Leaf) {
        throw "EC automatic recovery exited with code 0 but the Guardian lease still exists at $leasePath. The lifecycle operation was aborted."
    }
    Write-Verbose "EC automatic recovery completed with exit code 0 and the Guardian lease is gone."
}

function Stop-GuardianService {
    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) { return }
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $script:ServiceName -Force -ErrorAction Stop
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $service -or $service.Status -eq 'Stopped') { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Guardian service did not stop within 20 seconds; locked binaries were not replaced."
}

function Start-GuardianService {
    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) { throw "Guardian service is not installed." }
    Start-Service -Name $script:ServiceName -ErrorAction Stop
    $service = Get-Service -Name $script:ServiceName -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(20))
}

function Install-GuardianService {
    param([string]$GuardianExecutable)
    if (Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue) {
        throw "Guardian service already exists. Use upgrade-package.ps1 or repair-package.ps1."
    }
    $displayName = 'MiniProToolkit Guardian'
    $description = 'Restores Lecoo MINI PRO-AHP fan control to EC automatic mode after an abandoned control lease.'
    & sc.exe create $script:ServiceName binPath= "`"$GuardianExecutable`"" start= auto DisplayName= "`"$displayName`"" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }
    try {
        & sc.exe description $script:ServiceName $description | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed with exit code $LASTEXITCODE." }
        & sc.exe failure $script:ServiceName reset= 86400 actions= restart/5000/restart/15000/none/0 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "sc.exe failure configuration failed with exit code $LASTEXITCODE." }
    }
    catch {
        $configurationError = $_
        try { Remove-GuardianService }
        catch { throw "Guardian was created but service configuration and cleanup both failed. Configuration: $($configurationError.Exception.Message). Cleanup: $($_.Exception.Message)" }
        throw $configurationError
    }
}

function Remove-GuardianService {
    if (-not (Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue)) { return }
    Stop-GuardianService
    & sc.exe delete $script:ServiceName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if (-not (Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Guardian service still exists after deletion request.'
}

function Copy-Payload {
    param([string]$PackageRoot, [string]$InstallRoot, [switch]$KeepBackup)
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    $suffix = [Guid]::NewGuid().ToString('N')
    $stageRoot = Join-Path $InstallRoot ".stage-$suffix"
    $backupRoot = Join-Path $InstallRoot ".backup-$suffix"
    $directoryNames = @('App', 'Guardian', 'scripts', 'docs')
    $manifestName = 'release-manifest.json'
    $stageManifest = Join-Path $stageRoot $manifestName
    $installManifest = Join-Path $InstallRoot $manifestName
    $backupManifest = Join-Path $backupRoot $manifestName
    $backedUp = @{}
    $committed = @{}
    New-Item -ItemType Directory -Force -Path $stageRoot, $backupRoot | Out-Null
    foreach ($name in $directoryNames) {
        New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot $name) | Out-Null
        $backedUp[$name] = $false
        $committed[$name] = $false
    }
    $manifestBackedUp = $false
    $manifestCommitted = $false
    $preserveBackup = $false

    try {
        foreach ($name in $directoryNames) {
            Copy-Item -Path (Join-Path (Join-Path $PackageRoot $name) '*') -Destination (Join-Path $stageRoot $name) -Recurse -Force
        }
        Copy-Item -LiteralPath (Join-Path $PackageRoot $manifestName) -Destination $stageManifest -Force
        if (-not (Test-Path -LiteralPath (Join-Path $stageRoot 'App\MiniProToolkit.exe')) -or
            -not (Test-Path -LiteralPath (Join-Path $stageRoot 'Guardian\MiniProToolkit.Guardian.exe')) -or
            -not (Test-Path -LiteralPath (Join-Path $stageRoot 'scripts\package-common.ps1'))) {
            throw 'Staged package is missing a required executable or lifecycle script; existing installation was not replaced.'
        }

        foreach ($name in $directoryNames) {
            $destination = Join-Path $InstallRoot $name
            if (Test-Path -LiteralPath $destination) {
                Move-Item -LiteralPath $destination -Destination (Join-Path $backupRoot $name) | Out-Null
                $backedUp[$name] = $true
            }
        }
        if (Test-Path -LiteralPath $installManifest) {
            Move-Item -LiteralPath $installManifest -Destination $backupManifest | Out-Null
            $manifestBackedUp = $true
        }
        foreach ($name in $directoryNames) {
            Move-Item -LiteralPath (Join-Path $stageRoot $name) -Destination (Join-Path $InstallRoot $name) | Out-Null
            $committed[$name] = $true
        }
        Move-Item -LiteralPath $stageManifest -Destination $installManifest | Out-Null
        $manifestCommitted = $true
        if ($KeepBackup) { $preserveBackup = $true; return $backupRoot }
    }
    catch {
        $copyError = $_
        try {
            foreach ($name in $directoryNames) {
                $destination = Join-Path $InstallRoot $name
                if ($committed[$name] -and (Test-Path -LiteralPath $destination)) {
                    Remove-Item -LiteralPath $destination -Recurse -Force
                }
                $backup = Join-Path $backupRoot $name
                if ($backedUp[$name] -and (Test-Path -LiteralPath $backup)) {
                    Move-Item -LiteralPath $backup -Destination $destination -Force | Out-Null
                }
            }
            if ($manifestCommitted -and (Test-Path -LiteralPath $installManifest)) {
                Remove-Item -LiteralPath $installManifest -Force
            }
            if ($manifestBackedUp -and (Test-Path -LiteralPath $backupManifest)) {
                Move-Item -LiteralPath $backupManifest -Destination $installManifest -Force | Out-Null
            }
        }
        catch {
            $preserveBackup = $true
            throw "Payload replacement failed and automatic rollback was incomplete. Backup preserved at '$backupRoot'. Original error: $($copyError.Exception.Message). Rollback error: $($_.Exception.Message)"
        }
        throw $copyError
    }
    finally {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (-not $preserveBackup) { Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Restore-Payload {
    param([string]$InstallRoot, [string]$BackupRoot)
    foreach ($name in @('App', 'Guardian', 'scripts', 'docs')) {
        $destination = Join-Path $InstallRoot $name
        $backup = Join-Path $BackupRoot $name
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
        if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $destination -Force | Out-Null }
    }
    $manifest = Join-Path $InstallRoot 'release-manifest.json'
    $backupManifest = Join-Path $BackupRoot 'release-manifest.json'
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
    if (Test-Path -LiteralPath $backupManifest) { Move-Item -LiteralPath $backupManifest -Destination $manifest -Force | Out-Null }
    Remove-Item -LiteralPath $BackupRoot -Recurse -Force -ErrorAction SilentlyContinue
}

function Write-InstallState {
    param([string]$InstallRoot, [object]$Manifest)
    [pscustomobject]@{
        Version = [string]$Manifest.Version
        InstalledUtc = [DateTime]::UtcNow.ToString('O')
        InstallRoot = $InstallRoot
        ServiceName = $script:ServiceName
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallRoot 'install-state.json') -Encoding UTF8
}

function New-StartMenuShortcut {
    param([string]$InstallRoot)
    $programs = [Environment]::GetFolderPath('CommonPrograms')
    $shortcutPath = Join-Path $programs 'MiniProToolkit.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $InstallRoot 'App\MiniProToolkit.exe'
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = Join-Path $InstallRoot 'App'
    $shortcut.Description = 'MiniProToolkit control center'
    $shortcut.Save()
}

function Remove-Shortcuts {
    param([string]$InstallRoot)
    $programs = [Environment]::GetFolderPath('CommonPrograms')
    $shortcutPath = Join-Path $programs 'MiniProToolkit.lnk'
    if (-not (Test-Path -LiteralPath $shortcutPath)) { return }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $target = [string]$shortcut.TargetPath
    if ([string]::IsNullOrWhiteSpace($target)) { return }
    $target = [System.IO.Path]::GetFullPath($target)
    $root = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $shortcutPath -Force }
}

function Set-StartupRegistration {
    param([string]$Executable, [bool]$Enabled)
    New-Item -Path $script:RunKeyPath -Force | Out-Null
    if ($Enabled) {
        Set-ItemProperty -Path $script:RunKeyPath -Name $script:RunValueName -Value "`"$Executable`" --startup"
    }
    else {
        Remove-ItemProperty -Path $script:RunKeyPath -Name $script:RunValueName -ErrorAction SilentlyContinue
    }
}

function Remove-StartupRegistrationIfOwned {
    param([string]$InstallRoot)
    $property = Get-ItemProperty -Path $script:RunKeyPath -Name $script:RunValueName -ErrorAction SilentlyContinue
    $value = if ($property) { $property | Select-Object -ExpandProperty $script:RunValueName -ErrorAction SilentlyContinue } else { $null }
    if ($value -and $value.IndexOf($InstallRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Remove-ItemProperty -Path $script:RunKeyPath -Name $script:RunValueName -ErrorAction SilentlyContinue
    }
}

function Remove-GuardianProgramData {
    # Purge helper for %ProgramData%\MiniProToolkit. It never deletes the lease or the lease lock
    # file: a bare recursive delete of that directory could destroy recovery ownership (the lease)
    # or break the cross-process lock contract (the .lock file is never deleted). Only the known,
    # disposable Guardian artifacts are removed; an active lease, an unrecognised file or a
    # subdirectory aborts the purge instead of widening the delete.
    $root = Get-ProgramDataRoot
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
    $leasePath = Get-LeasePath
    if (Test-Path -LiteralPath $leasePath -PathType Leaf) {
        throw "Refusing to purge Guardian program data while a lease exists: $leasePath"
    }
    $leaseName = [System.IO.Path]::GetFileName($leasePath)
    $lockName = [System.IO.Path]::GetFileName((Get-LeaseLockPath))
    $disposable = @('guardian.log', 'diagnostic-events.jsonl')
    $protected = @($leaseName, $lockName)
    $unexpected = @()
    foreach ($item in @(Get-ChildItem -LiteralPath $root -Force -ErrorAction Stop)) {
        if ($item -is [System.IO.DirectoryInfo]) { $unexpected += $item.FullName; continue }
        if ($protected -contains $item.Name) { continue }
        if ($disposable -notcontains $item.Name) { $unexpected += $item.FullName }
    }
    if ($unexpected.Count -gt 0) {
        throw "Refusing to purge unrecognised Guardian program data: $($unexpected -join '; '). Remove it manually after checking that no lease is involved."
    }
    foreach ($name in $disposable) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        }
    }
    # The directory is only removed when it is empty; a surviving <lease>.lock keeps it in place.
    if (@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) {
        Remove-Item -LiteralPath $root -Force -ErrorAction Stop
    }
}
