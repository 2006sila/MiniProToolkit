[CmdletBinding()]
param()

# Offline failure-injection only. This does NOT run real Core/Release tests.
# The publisher and its helper are copied byte-for-byte to a disposable repository.
# A child PowerShell function shadows dotnet; no SDK, EC, Guardian or install runs.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = Join-Path $repository 'research\evidence\current-validation'
$reportPath = Join-Path $evidence 'release-gate-interception.json'
$logPath = Join-Path $evidence 'release-gate-interception.log'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$log = New-Object 'System.Collections.Generic.List[string]'
$checks = New-Object 'System.Collections.Generic.List[object]'
$temp = $null
$exitCode = 1
$report = [ordered]@{
    SchemaVersion = 1
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    Invocation = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/test-release-gates.ps1'
    Scope = 'Offline fixture failure interception; NOT a real Core or Release test pass.'
    PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    InjectedDotnetExitCode = 73
    RealDotnetExecuted = $false
    RealPublishExecuted = $false
    RepositoryPayloadAccessed = $false
    SourceHashes = @()
    GateOrder = @()
    ScriptParseResults = @()
    Cases = @()
    Status = 'FAILED'
}

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $log.Add("PASS: $Message")
}

function Get-TreeSnapshot {
    param([string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory)) { return @() }
    return @(Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName.Substring($Directory.Length).TrimStart('\')
            Directory = $_.PSIsContainer
            Length = $(if ($_.PSIsContainer) { 0 } else { $_.Length })
            Sha256 = $(if ($_.PSIsContainer) { $null } else { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
            CreatedUtc = $_.CreationTimeUtc.ToString('O')
            ModifiedUtc = $_.LastWriteTimeUtc.ToString('O')
        }
    })
}

try {
    $log.Add("Started UTC: $($report.StartedUtc)")
    $log.Add($report.Scope)
    $publisher = Join-Path $PSScriptRoot 'publish-release.ps1'
    $helper = Join-Path $PSScriptRoot 'package-archive.ps1'
    $parseResults = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File | Sort-Object Name)) {
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        $parseResults += [pscustomobject]@{
            File = $file.Name
            ErrorCount = @($errors).Count
            Errors = @($errors | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" })
        }
        if ($file.FullName -eq $publisher) { $publisherAst = $ast }
        if ($file.FullName -eq $helper) { $helperAst = $ast }
    }
    $report.ScriptParseResults = $parseResults
    Assert-Condition (@($parseResults | Where-Object { $_.ErrorCount -ne 0 }).Count -eq 0) 'All current top-level scripts parse (read-only AST check).'

    # Fail closed if the publisher shape changes: only the reviewed prefix may run
    # before dotnet test. Never fall back to executing the real repository publisher.
    $top = @($publisherAst.EndBlock.Statements)
    $gate = @($top | Where-Object { $_.Extent.Text.StartsWith('if ($SkipTests)') })
    Assert-Condition ($gate.Count -eq 1) 'Exactly one top-level SkipTests/Core gate exists.'
    $gateText = $gate[0].Extent.Text
    Assert-Condition ($gateText.Contains('dotnet test $testProject -c Release --nologo -v minimal')) 'Gate invokes only the Core test project in Release configuration.'
    Assert-Condition ($gateText.Contains('$testExitCode = $LASTEXITCODE') -and $gateText.Contains('if ($testExitCode -ne 0)') -and $gateText.Contains('throw "Test gate failed: Core tests exited with code $testExitCode.')) 'Gate captures the injected exit code and throws on failure.'
    $prefixCommands = @($publisherAst.FindAll({ param($node)
        $node -is [System.Management.Automation.Language.CommandAst]
    }, $true) | Where-Object {
        $_.Extent.StartOffset -lt $gate[0].Extent.StartOffset -and
        -not ($_.Parent -is [System.Management.Automation.Language.FunctionDefinitionAst])
    })
    # Function bodies are definitions, not startup work; exclude their descendants.
    foreach ($command in $prefixCommands) {
        $parent = $command.Parent
        $inFunction = $false
        while ($null -ne $parent) {
            if ($parent -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $inFunction = $true; break }
            $parent = $parent.Parent
        }
        if ($inFunction) { continue }
        $name = $command.GetCommandName()
        $allowed = @('Set-StrictMode', 'Resolve-Path', 'Join-Path')
        $isHelper = $command.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Dot -and
            $command.Extent.Text -eq ". (Join-Path `$PSScriptRoot 'package-archive.ps1')"
        Assert-Condition (($allowed -contains $name) -or $isHelper) "Reviewed safe publisher prefix command: $($command.Extent.Text)"
    }
    foreach ($statement in @($helperAst.EndBlock.Statements)) {
        Assert-Condition ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
            $statement.Extent.Text -eq 'Set-StrictMode -Version Latest' -or
            $statement.Extent.Text -eq "`$ErrorActionPreference = 'Stop'") 'Archive helper startup contains only function definitions and strict/error settings.'
    }
    $syntax = @($top | Where-Object { $_.Extent.Text -eq 'Assert-ScriptSyntaxGate -ScriptsDirectory $PSScriptRoot' })
    $remove = @($top | Where-Object { $_.Extent.Text.StartsWith('if (Test-Path -LiteralPath $output)') })
    $create = @($top | Where-Object { $_.Extent.Text.StartsWith('New-Item -ItemType Directory -Force -Path $output,') })
    Assert-Condition ($syntax.Count -eq 1 -and $remove.Count -eq 1 -and $create.Count -eq 1) 'AST identifies syntax gate, payload deletion and payload creation.'
    Assert-Condition ($gate[0].Extent.EndOffset -lt $syntax[0].Extent.StartOffset -and
        $syntax[0].Extent.EndOffset -lt $remove[0].Extent.StartOffset -and
        $remove[0].Extent.EndOffset -lt $create[0].Extent.StartOffset) 'Order is Core test gate -> script AST gate -> delete old payload -> create payload.'
    $report.GateOrder = @(
        [pscustomobject]@{ Stage = 'Core test gate'; Line = $gate[0].Extent.StartLineNumber },
        [pscustomobject]@{ Stage = 'Script AST gate'; Line = $syntax[0].Extent.StartLineNumber },
        [pscustomobject]@{ Stage = 'Delete old payload'; Line = $remove[0].Extent.StartLineNumber },
        [pscustomobject]@{ Stage = 'Create payload'; Line = $create[0].Extent.StartLineNumber }
    )
    $report.SourceHashes = @($publisher, $helper | ForEach-Object {
        [pscustomobject]@{ Path = $_; Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })

    $temp = Join-Path ([IO.Path]::GetTempPath()) ('MiniProToolkit-release-gates-' + [Guid]::NewGuid().ToString('N'))
    $temp = [IO.Path]::GetFullPath($temp)
    Assert-Condition (-not $temp.StartsWith($repository.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'Disposable fixture is outside the repository.'
    New-Item -ItemType Directory -Path $temp | Out-Null
    $log.Add("Temporary fixture root: $temp")
    foreach ($caseName in @('existing-payload', 'absent-payload')) {
        $fixture = Join-Path $temp $caseName
        $scripts = Join-Path $fixture 'scripts'
        $projectDirectory = Join-Path $fixture 'tests\MiniProToolkit.Core.Tests'
        $outputRoot = Join-Path $fixture 'artifacts'
        $payload = Join-Path $outputRoot 'MiniProToolkit-x64-vgate-fixture'
        New-Item -ItemType Directory -Path $scripts, $projectDirectory, $outputRoot -Force | Out-Null
        foreach ($source in @($publisher, $helper)) {
            $copy = Join-Path $scripts ([IO.Path]::GetFileName($source))
            Copy-Item -LiteralPath $source -Destination $copy
            Assert-Condition ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $copy).Hash) "${caseName}: fixture script is byte-identical: $([IO.Path]::GetFileName($source))"
        }
        # Existence-only fixture, never compiled; real tests/assertions remain untouched.
        [IO.File]::WriteAllText((Join-Path $projectDirectory 'MiniProToolkit.Core.Tests.csproj'), '<Project />', $utf8)
        if ($caseName -eq 'existing-payload') {
            New-Item -ItemType Directory -Path $payload | Out-Null
            [IO.File]::WriteAllText((Join-Path $payload 'old-payload.sentinel'), 'OLD PAYLOAD - MUST SURVIVE FAILED CORE GATE', $utf8)
        }
        $before = @(Get-TreeSnapshot $outputRoot)
        $bootstrap = Join-Path $fixture 'invoke-fixture.ps1'
        $bootstrapText = @'
param([string]$FixtureRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:InjectedCallFile = Join-Path $FixtureRoot 'dotnet-calls.jsonl'
function global:dotnet {
    $arguments = @($args)
    $entry = [ordered]@{ Command = 'dotnet'; Arguments = $arguments; Directory = (Get-Location).Path; ExitCode = 73 }
    [IO.File]::AppendAllText($global:InjectedCallFile, (($entry | ConvertTo-Json -Compress) + [Environment]::NewLine))
    if ($arguments.Count -ne 7 -or $arguments[0] -ne 'test' -or
        $arguments[1] -ne (Join-Path $FixtureRoot 'tests\MiniProToolkit.Core.Tests\MiniProToolkit.Core.Tests.csproj') -or
        ($arguments[2..6] -join ' ') -ne '-c Release --nologo -v minimal') {
        throw 'Safety guard: unexpected dotnet invocation; no real executable was called.'
    }
    Write-Host 'OFFLINE FAKE DOTNET: Core test failure injected; exit code 73; no real tests executed.'
    $global:LASTEXITCODE = 73
}
function global:powershell.exe { throw 'Safety guard: nested payload validator execution forbidden.' }
& (Join-Path $FixtureRoot 'scripts\publish-release.ps1') -Version 'gate-fixture' -OutputRoot (Join-Path $FixtureRoot 'artifacts')
throw 'Assertion failure: publisher returned instead of terminating at the failed test gate.'
'@
        [IO.File]::WriteAllText($bootstrap, $bootstrapText, $utf8)
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path $PSHOME 'powershell.exe'
        $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $bootstrap + '" -FixtureRoot "' + $fixture + '"'
        $start.WorkingDirectory = $fixture
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $start
        try {
            [void]$process.Start()
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) {
                $process.Kill()
                $process.WaitForExit()
                throw 'Fixture timeout (30 seconds); child terminated, no elevation attempted.'
            }
            $childExit = $process.ExitCode
            $stdout = $stdoutTask.Result
            $stderr = $stderrTask.Result
        }
        finally { $process.Dispose() }
        $log.Add("--- $caseName child exit: $childExit ---")
        $log.Add($stdout)
        $log.Add($stderr)
        $after = @(Get-TreeSnapshot $outputRoot)
        $callFile = Join-Path $fixture 'dotnet-calls.jsonl'
        $calls = @()
        if (Test-Path -LiteralPath $callFile) {
            $calls = @(Get-Content -LiteralPath $callFile | ForEach-Object { $_ | ConvertFrom-Json })
        }
        $artifacts = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File | Where-Object {
            $_.Name -eq 'release-manifest.json' -or $_.Name -match '\.(zip|sha256|building)$'
        } | ForEach-Object { $_.FullName })
        $result = [pscustomobject]@{
            Name = $caseName
            FixtureRoot = $fixture
            ChildExitCode = $childExit
            StandardOutput = $stdout
            StandardError = $stderr
            DotnetCalls = $calls
            Before = $before
            After = $after
            UnexpectedArtifacts = $artifacts
            Passed = $false
        }
        $checks.Add($result)
        Assert-Condition ($childExit -eq 1) "${caseName}: publisher subprocess exits 1 for the terminating gate exception."
        Assert-Condition (($stdout + $stderr).Contains('Test gate failed: Core tests exited with code 73.')) "${caseName}: output identifies Core gate failure and injected code 73."
        Assert-Condition ($calls.Count -eq 1 -and $calls[0].ExitCode -eq 73 -and $calls[0].Arguments[0] -eq 'test') "${caseName}: exactly one fake dotnet test call; no restore/publish."
        Assert-Condition (($before | ConvertTo-Json -Depth 5 -Compress) -ceq ($after | ConvertTo-Json -Depth 5 -Compress)) "${caseName}: complete output tree, sentinel hashes and timestamps are unchanged."
        Assert-Condition ($artifacts.Count -eq 0) "${caseName}: no manifest, ZIP, building archive or SHA256 sidecar created."
        Assert-Condition (-not ($stdout + $stderr).Contains('AST gate:')) "${caseName}: subsequent AST gate was not reached."
        $result.Passed = $true
    }
    foreach ($source in $report.SourceHashes) {
        Assert-Condition ((Get-FileHash -LiteralPath $source.Path -Algorithm SHA256).Hash -eq $source.Sha256) 'Original publisher/helper remains unchanged.'
    }
    $report.Status = 'PASSED'
    $exitCode = 0
}
catch {
    $report.Status = 'FAILED'
    $report['Error'] = $_.Exception.Message
    $report['ErrorPosition'] = $_.InvocationInfo.PositionMessage
    $log.Add("FAIL: $($_.Exception.Message)")
    $log.Add($_.InvocationInfo.PositionMessage)
}
finally {
    if ($null -ne $temp -and (Test-Path -LiteralPath $temp)) {
        try {
            Remove-Item -LiteralPath $temp -Recurse -Force
            $report['FixtureRemoved'] = $true
        }
        catch {
            $report['FixtureRemoved'] = $false
            $report.Status = 'FAILED'
            $exitCode = 1
            $log.Add("FAIL: temporary fixture cleanup: $($_.Exception.Message)")
        }
    }
    $report.Cases = @($checks.ToArray())
    $report['FinishedUtc'] = [DateTime]::UtcNow.ToString('O')
    $report['HarnessExitCode'] = $exitCode
    $log.Add("Harness status: $($report.Status); exit code: $exitCode")
    $log.Add('This evidence proves injected offline failure interception only; it does not establish real Release test success.')
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 12), $utf8)
    [IO.File]::WriteAllText($logPath, ($log -join [Environment]::NewLine), $utf8)
}
Write-Host "Offline release-gate assertions: $($report.Status)"
Write-Host "JSON: $reportPath"
Write-Host "Log: $logPath"
exit $exitCode
