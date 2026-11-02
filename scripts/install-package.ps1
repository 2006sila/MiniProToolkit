#requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = "$env:ProgramFiles\MiniProToolkit",
    [string]$PackageRoot,
    [switch]$RestoreEcAutomatic,
    [switch]$EnableStartup
)

. (Join-Path $PSScriptRoot 'package-common.ps1')
$packageRoot = if ([string]::IsNullOrWhiteSpace($PackageRoot)) { Get-PackageRoot (Join-Path $PSScriptRoot '..') } else { (Resolve-Path $PackageRoot).Path }
Assert-PackagePayload $packageRoot
$manifest = Get-ReleaseManifest $packageRoot
if (-not $PSCmdlet.ShouldProcess($InstallRoot, "Install MiniProToolkit $($manifest.Version)")) { return }

# Fail-closed pre-flight (read-only): an active lease must not be cleared implicitly.
Assert-RestoreAuthorization -RestoreEcAutomatic:$RestoreEcAutomatic
# Lifecycle order: stop the application and confirm it is gone first; only then may EC automatic
# recovery write 101 and clear the lease.
Stop-MiniProToolkitGracefully
Invoke-RestoreIfRequired -RestoreEcAutomatic:$RestoreEcAutomatic
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Guardian service already exists. Use upgrade-package.ps1 instead."
}

$resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
if (@('App', 'Guardian', 'scripts', 'docs', 'install-state.json', 'release-manifest.json') |
    Where-Object { Test-Path -LiteralPath (Join-Path $resolvedInstallRoot $_) } | Select-Object -First 1) {
    throw "Existing payload detected at $resolvedInstallRoot. Use upgrade-package.ps1 or repair-package.ps1."
}
New-Item -ItemType Directory -Force -Path $resolvedInstallRoot | Out-Null
$serviceCreated = $false
try {
    Copy-Payload $packageRoot $resolvedInstallRoot
    $guardian = Join-Path $resolvedInstallRoot 'Guardian\MiniProToolkit.Guardian.exe'
    Install-GuardianService $guardian
    $serviceCreated = $true
    Start-GuardianService
    Write-InstallState $resolvedInstallRoot $manifest
    New-StartMenuShortcut $resolvedInstallRoot
    $app = Join-Path $resolvedInstallRoot 'App\MiniProToolkit.exe'
    Set-StartupRegistration $app ([bool]$EnableStartup)
    Write-Host "Installed MiniProToolkit $($manifest.Version) to $resolvedInstallRoot." -ForegroundColor Green
}
catch {
    $installError = $_
    try {
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { Remove-GuardianService }
        Remove-Shortcuts $resolvedInstallRoot
        Remove-StartupRegistrationIfOwned $resolvedInstallRoot
        foreach ($directory in @('App', 'Guardian', 'scripts', 'docs')) {
            $path = Join-Path $resolvedInstallRoot $directory
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
        Remove-Item -LiteralPath (Join-Path $resolvedInstallRoot 'release-manifest.json') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $resolvedInstallRoot 'install-state.json') -Force -ErrorAction SilentlyContinue
    }
    catch { throw "Install failed and cleanup needs attention. Original error: $($installError.Exception.Message). Cleanup error: $($_.Exception.Message)" }
    throw $installError
}
