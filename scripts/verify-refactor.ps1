[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Full')]
    [string]$Mode = 'Quick',

    [string]$TestFilter
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'BeMusicSeeker.sln'
$uiExecutable = Join-Path $repoRoot 'bin\x64\Release\net472\BeMusicSeeker.exe'
$verificationArtifactsDirectory = Join-Path $repoRoot 'artifacts\verification'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

function Start-TestHangDiagnostics {
    param(
        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory
    )

    $snapshotPath = Join-Path $DiagnosticsDirectory 'process-tree-at-180-seconds.json'
    $terminationPath = Join-Path $DiagnosticsDirectory 'testhost-termination-at-180-seconds.json'
    $diagnosticStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $diagnosticDeadline = [TimeSpan]::FromSeconds(20)
    try {
        $processes = @(Get-CimInstance Win32_Process -OperationTimeoutSec 5)
        $processIds = [System.Collections.Generic.HashSet[int]]::new()
        [void]$processIds.Add($RootProcessId)
        do {
            $added = $false
            foreach ($candidate in $processes) {
                if ($processIds.Contains([int]$candidate.ParentProcessId) -and ($processIds.Add([int]$candidate.ProcessId))) {
                    $added = $true
                }
            }
        } while ($added)
        $processTree = @($processes |
            Where-Object { $processIds.Contains([int]$_.ProcessId) } |
            Select-Object ProcessId, ParentProcessId, Name, CommandLine, CreationDate)
        $processTree |
            ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath $snapshotPath -Encoding utf8

        $testHosts = @($processTree | Where-Object { $_.Name -like 'testhost*' })
        if ($testHosts.Count -eq 0) {
            throw 'No testhost process was found in the monitored dotnet process tree.'
        }
        $terminationAttempts = foreach ($testHost in $testHosts) {
            if ($diagnosticStopwatch.Elapsed -ge $diagnosticDeadline) {
                [pscustomobject]@{
                    ProcessId = $testHost.ProcessId
                    Name = $testHost.Name
                    Process = $null
                    TerminationRequested = $false
                    HasExited = $false
                    Failure = 'The shared 20-second diagnostic deadline elapsed before the termination request.'
                }
                continue
            }
            try {
                $testHostProcess = [System.Diagnostics.Process]::GetProcessById([int]$testHost.ProcessId)
                $testHostProcess.Kill($true)
                [pscustomobject]@{
                    ProcessId = $testHost.ProcessId
                    Name = $testHost.Name
                    Process = $testHostProcess
                    TerminationRequested = $true
                    HasExited = $false
                    Failure = $null
                }
            }
            catch {
                [pscustomobject]@{
                    ProcessId = $testHost.ProcessId
                    Name = $testHost.Name
                    Process = $null
                    TerminationRequested = $false
                    HasExited = $false
                    Failure = $_.Exception.Message
                }
            }
        }
        do {
            $waitingForExit = $false
            foreach ($termination in $terminationAttempts | Where-Object { $_.TerminationRequested -and -not $_.HasExited }) {
                try {
                    $termination.Process.Refresh()
                    $termination.HasExited = $termination.Process.HasExited
                }
                catch [System.ArgumentException] {
                    $termination.HasExited = $true
                }
                if (-not $termination.HasExited) {
                    $waitingForExit = $true
                }
            }
            if ($waitingForExit -and $diagnosticStopwatch.Elapsed -lt $diagnosticDeadline) {
                Start-Sleep -Milliseconds 100
            }
        } while ($waitingForExit -and $diagnosticStopwatch.Elapsed -lt $diagnosticDeadline)
        $terminations = foreach ($termination in $terminationAttempts) {
            if ($termination.Process -ne $null) {
                $termination.Process.Dispose()
            }
            $failure = $termination.Failure
            if ($termination.TerminationRequested -and -not $termination.HasExited) {
                $failure = "Testhost process $($termination.ProcessId) remained alive at the shared 20-second diagnostic deadline."
            }
            [pscustomobject]@{
                ProcessId = $termination.ProcessId
                Name = $termination.Name
                TerminationRequested = $termination.TerminationRequested -and $termination.HasExited
                Failure = $failure
            }
        }
        @($terminations) |
            ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath $terminationPath -Encoding utf8
        $terminationFailures = @($terminations | Where-Object { -not $_.TerminationRequested })
        if ($terminationFailures.Count -gt 0) {
            throw "One or more testhost processes could not be stopped: $($terminationFailures.Failure -join '; ')"
        }
    }
    catch {
        $diagnosticFailure = $_.Exception
        $failurePath = Join-Path $DiagnosticsDirectory 'diagnostic-start-failure.json'
        try {
            @{
                RootProcessId = $RootProcessId
                Failure = $diagnosticFailure.Message
            } |
                ConvertTo-Json |
                Set-Content -LiteralPath $failurePath -Encoding utf8
        }
        catch {
        }
        try {
            $rootProcess = [System.Diagnostics.Process]::GetProcessById($RootProcessId)
            $rootProcess.Kill($true)
            if (-not $rootProcess.WaitForExit(5000)) {
                throw "Monitored root process $RootProcessId remained alive after the diagnostic failure kill request."
            }
        }
        catch {
            throw "Test hang diagnostics failed at 180 seconds and the monitored process tree could not be stopped: $($diagnosticFailure.Message); termination failure: $($_.Exception.Message)"
        }
        throw "Test hang diagnostics failed at 180 seconds. The monitored process tree was stopped: $($diagnosticFailure.Message); diagnostics: $DiagnosticsDirectory"
    }
    finally {
        $diagnosticStopwatch.Stop()
    }
}

function Invoke-MonitoredTestCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    $diagnosticThreshold = [TimeSpan]::FromSeconds(180)
    $terminationFallbackThreshold = [TimeSpan]::FromSeconds(210)
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Command
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $diagnosticStarted = $false
    try {
        if (-not $process.Start()) {
            throw "Test process did not start: $Command $($Arguments -join ' ')"
        }
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit(1000)) {
            if (-not $diagnosticStarted -and $stopwatch.Elapsed -ge $diagnosticThreshold) {
                $diagnosticStarted = $true
                Start-TestHangDiagnostics -RootProcessId $process.Id -DiagnosticsDirectory $DiagnosticsDirectory
                Write-Warning "Test execution reached 180 seconds. The testhost was stopped to finalize the blame sequence; diagnostics: $DiagnosticsDirectory"
            }
            if ($stopwatch.Elapsed -lt $terminationFallbackThreshold) {
                continue
            }
            $terminationFailure = $null
            try {
                $process.Kill($true)
            }
            catch {
                $terminationFailure = $_.Exception
            }
            if ($null -ne $terminationFailure) {
                throw "Test diagnostics did not return within 30 seconds after the 180-second threshold, but process tree $($process.Id) could not be stopped: $($terminationFailure.Message)"
            }
            $processStopped = $process.WaitForExit(5000)
            if (-not $processStopped) {
                throw "Test diagnostics did not return within 30 seconds after the 180-second threshold, and process tree $($process.Id) remained alive after the kill request."
            }
            if (-not $standardOutput.Wait(5000) -or -not $standardError.Wait(5000)) {
                throw "Test process tree $($process.Id) stopped after diagnostic fallback, but redirected output did not close within 5 seconds."
            }
            Write-Host ($standardOutput.GetAwaiter().GetResult()) -NoNewline
            $errorOutput = $standardError.GetAwaiter().GetResult()
            if (-not [string]::IsNullOrEmpty($errorOutput)) {
                Write-Error $errorOutput -ErrorAction Continue
            }
            throw "Test execution reached the 180-second diagnostic threshold and did not return after 30 seconds of diagnostics. The process tree was stopped: $Command $($Arguments -join ' ')"
        }
        if (-not $standardOutput.Wait(5000) -or -not $standardError.Wait(5000)) {
            throw "Test process exited, but redirected output did not close within 5 seconds: $Command $($Arguments -join ' ')"
        }
        Write-Host ($standardOutput.GetAwaiter().GetResult()) -NoNewline
        $errorOutput = $standardError.GetAwaiter().GetResult()
        if (-not [string]::IsNullOrEmpty($errorOutput)) {
            Write-Error $errorOutput -ErrorAction Continue
        }
        if ($diagnosticStarted) {
            $sequenceFiles = @(Get-ChildItem -LiteralPath $DiagnosticsDirectory -Filter 'Sequence*.xml' -Recurse -File)
            if ($sequenceFiles.Count -eq 0) {
                throw "Test execution reached 180 seconds and the testhost was stopped, but no blame sequence was produced: $DiagnosticsDirectory"
            }
            throw "Test execution reached the 180-second diagnostic threshold. The testhost was stopped and the run failed for investigation: $DiagnosticsDirectory"
        }
        if ($process.ExitCode -ne 0) {
            throw "Command failed with exit code $($process.ExitCode): $Command $($Arguments -join ' ')"
        }
    }
    finally {
        $stopwatch.Stop()
        $process.Dispose()
    }
}

Push-Location $repoRoot
try {
    if ($Mode -eq 'Full') {
        Invoke-CheckedCommand dotnet restore $solution
        Invoke-CheckedCommand dotnet tool restore
    }

    # Build/format/analyzer commands run to completion. Test execution has a monitored
    # diagnostic threshold so a deadlock cannot hold the verification cycle indefinitely.
    Invoke-CheckedCommand dotnet build $solution '/p:Configuration=Release' '--no-restore'

    if (-not (Test-Path -LiteralPath $uiExecutable -PathType Leaf)) {
        throw "Release UI smoke executable was not produced: $uiExecutable"
    }

    $resolvedUiExecutable = (Resolve-Path -LiteralPath $uiExecutable).Path
    Write-Host "Release UI smoke executable: $resolvedUiExecutable"

    $testArguments = @('test', $solution, '/p:Configuration=Release', '--no-build', '--no-restore')
    $testDiagnosticsDirectory = Join-Path $verificationArtifactsDirectory (
        'tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    [void](New-Item -ItemType Directory -Path $testDiagnosticsDirectory -Force)
    $testArguments += @(
        '--results-directory', $testDiagnosticsDirectory,
        '--blame')
    if ($Mode -eq 'Quick' -and -not [string]::IsNullOrWhiteSpace($TestFilter)) {
        $testArguments += @('--filter', $TestFilter)
    }
    Invoke-MonitoredTestCommand dotnet -DiagnosticsDirectory $testDiagnosticsDirectory @testArguments

    Invoke-CheckedCommand dotnet format whitespace $solution '--verify-no-changes' '--no-restore' '--verbosity' 'minimal'

    if ($Mode -eq 'Full') {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) {
            throw "vswhere.exe was not found: $vswhere"
        }

        $msbuildPath = & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin' | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($msbuildPath)) {
            throw 'Visual Studio 2022 MSBuild 17 was not found.'
        }

        Invoke-CheckedCommand dotnet roslynator analyze $solution '--msbuild-path' $msbuildPath '--properties' 'Configuration=Release' '--severity-level' 'warning' '--verbosity' 'minimal'
    }

    Invoke-CheckedCommand git diff '--check' 'HEAD' '--'

    $untrackedFiles = @(git ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate untracked files (exit code $LASTEXITCODE)."
    }

    if ($untrackedFiles.Count -gt 0) {
        $emptyFile = New-TemporaryFile
        try {
            foreach ($untrackedFile in $untrackedFiles) {
                $checkOutput = @(& git -c core.autocrlf=false diff --no-index --check -- $emptyFile.FullName $untrackedFile 2>&1)
                $checkExitCode = $LASTEXITCODE
                if ($checkOutput.Count -gt 0) {
                    throw "Whitespace error in untracked file '$untrackedFile':`n$($checkOutput -join [Environment]::NewLine)"
                }
                if ($checkExitCode -gt 1) {
                    throw "Unable to inspect untracked file '$untrackedFile' (exit code $checkExitCode)."
                }
            }
        }
        finally {
            Remove-Item -LiteralPath $emptyFile.FullName -Force
        }
    }
}
finally {
    Pop-Location
}
