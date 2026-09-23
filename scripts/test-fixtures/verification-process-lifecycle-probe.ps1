[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'normal',
        'utf8-output',
        'nonzero',
        'late-success',
        'descendant-root',
        'nonzero-descendant',
        'stream-timeout',
        'asymmetric-stdout-complete',
        'asymmetric-stderr-complete',
        'same-pwsh-late-fault',
        'post-start-exception',
        'terminal-diagnostic',
        'terminal-flush-failure',
        'functional-completed-success',
        'functional-shared-deadline',
        'missing-result',
        'fanout-order',
        'expired-residual')]
    [string]$Scenario,

    [Parameter(Mandatory)]
    [string]$DiagnosticsDirectory,

    [Parameter(Mandatory)]
    [string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repositoryRoot 'scripts\verification-process-lifecycle.ps1')
$childScript = Join-Path $PSScriptRoot 'verification-process-lifecycle-child.ps1'
$descendantScript = Join-Path $PSScriptRoot 'verification-process-lifecycle-sleeper.vbs'
$ledgerPath = Join-Path $DiagnosticsDirectory 'ownership-ledger.jsonl'
$primitiveEvents = [System.Collections.Concurrent.ConcurrentQueue[object]]::new()
$primitiveSequence = [long]0
$primitiveObserverState = [pscustomobject]@{
    ResidualGate = $null
    ResidualProcessId = $null
    ContaminationBStarted = $null
}
$primitiveObserver = [pscustomobject]@{
    Observe = {
        param($event)
        $sequence = [System.Threading.Interlocked]::Increment([ref]$primitiveSequence)
        [void]$primitiveEvents.Enqueue([pscustomobject]@{
                Sequence = $sequence
                Operation = [string]$event.Operation
                RootProcessId = [int]$event.RootProcessId
                Context = [string]$event.Context
                UtcTicks = [long]$event.UtcTicks
            })
        if ($Scenario -ceq 'same-pwsh-late-fault' -and
            $event.Operation -ceq 'cleanup-transition' -and
            $event.Context -ceq 'same-pwsh-B' -and
            $null -ne $primitiveObserverState.ContaminationBStarted) {
            $primitiveObserverState.ContaminationBStarted.Set()
        }
        if ($Scenario -ceq 'expired-residual' -and
            $event.Operation -ceq 'descendant-stop' -and
            $event.RootProcessId -eq $primitiveObserverState.ResidualProcessId -and
            $null -ne $primitiveObserverState.ResidualGate) {
            $primitiveObserverState.ResidualGate.Wait()
        }
    }.GetNewClosure()
}

function Write-LifecycleLedgerEntry {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    $entry = [ordered]@{
        pid = $Process.Id
        creationIdentity = $Process.StartTime.ToUniversalTime().Ticks
    } | ConvertTo-Json -Compress
    [System.IO.File]::AppendAllText(
        $ledgerPath,
        $entry + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

function Test-ProbeExactIdentityAlive {
    param(
        [Parameter(Mandatory)]
        [int]$ProcessId,

        [Parameter(Mandatory)]
        [long]$CreationIdentity
    )

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($ProcessId)
        try {
            return -not $process.HasExited -and
                $process.StartTime.ToUniversalTime().Ticks -eq $CreationIdentity
        }
        finally {
            $process.Dispose()
        }
    }
    catch [ArgumentException] {
        return $false
    }
}

function Wait-ProbeRootExitBounded {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [int]$TimeoutMilliseconds
    )

    if (-not $Process.WaitForExit($TimeoutMilliseconds)) {
        throw "The lifecycle root did not exit within the bounded probe watchdog (${TimeoutMilliseconds}ms)."
    }
}

if (-not ('VerificationLifecycleProbeTasks' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Threading;
using System.Threading.Tasks;

public static class VerificationLifecycleProbeTasks
{
    public static Task<string> FaultAfter(string message, int delayMilliseconds)
    {
        return Task.Run(async () =>
        {
            await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            return await Task.FromException<string>(new InvalidOperationException(message));
        });
    }

    public static Task ReleaseGateAt(ManualResetEventSlim gate, long deadlineUtcTicks)
    {
        return Task.Run(async () =>
        {
            while (DateTime.UtcNow.Ticks < deadlineUtcTicks)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }
            gate.Set();
        });
    }

    /// <summary>通知済みの子を実行期限より後に解放し、起動負荷によらず遅い終了を作ります。</summary>
    /// <param name="gate">呼出元が所有し、返されたTaskの完了後に破棄する終了gate。</param>
    /// <param name="deadlineUtcTicks">維持する実行期限のUTC ticks。</param>
    /// <returns>終了gateを解放したことを表すTask。</returns>
    public static Task ReleaseEventAfter(EventWaitHandle gate, long deadlineUtcTicks)
    {
        return Task.Run(async () =>
        {
            while (DateTime.UtcNow.Ticks <= deadlineUtcTicks)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }
            gate.Set();
        });
    }

    public static Task ReleaseGateWhen(ManualResetEventSlim trigger, ManualResetEventSlim gate)
    {
        return Task.Run(() =>
        {
            trigger.Wait();
            gate.Set();
        });
    }

    public static Task<string> FaultWhen(ManualResetEventSlim gate, string message)
    {
        Func<string> fault = () =>
        {
            gate.Wait();
            throw new InvalidOperationException(message);
        };
        return Task.Run(fault);
    }
}
'@
}

if ($Scenario -ceq 'utf8-output') {
    $parentEncoding = [Console]::OutputEncoding.CodePage
    $parentUtf8Setting = [Environment]::GetEnvironmentVariable('DOTNET_CLI_FORCE_UTF8_ENCODING')
    $policy = Resolve-VerificationDeadlinePair -TimeoutSeconds 10
    $owned = [System.Collections.Generic.List[object]]::new()
    try {
        $started = Start-VerificationRedirectedProcess `
            -FileName 'pwsh' `
            -Arguments @('-NoProfile', '-File', $childScript, '-Scenario', 'utf8-output') `
            -WorkingDirectory $repositoryRoot `
            -DeadlinePolicy $policy `
            -DiagnosticsDirectory $DiagnosticsDirectory `
            -OwnedProcessRecords $owned
        Write-LifecycleLedgerEntry -Process $started.Process
        $result = Complete-VerificationRedirectedProcess `
            -Started $started `
            -DiagnosticsDirectory $DiagnosticsDirectory `
            -DeadlinePolicy $policy
        [System.IO.File]::WriteAllText(
            $ResultPath,
            ([ordered]@{
                    scenario = $Scenario
                    exitCode = $result.ExitCode
                    processTimedOut = $result.ProcessTimedOut
                    stdout = $result.StandardOutput
                    stderr = $result.StandardError
                    stdoutArtifact = [System.IO.File]::ReadAllText((Join-Path $DiagnosticsDirectory 'stdout.log'))
                    stderrArtifact = [System.IO.File]::ReadAllText((Join-Path $DiagnosticsDirectory 'stderr.log'))
                    remainingOwnedProcessIds = @($result.RemainingOwnedProcessIds)
                    secondaryDiagnostics = @($result.SecondaryDiagnostics)
                    parentEncodingUnchanged = [Console]::OutputEncoding.CodePage -eq $parentEncoding
                    parentEnvironmentUnchanged = [Environment]::GetEnvironmentVariable('DOTNET_CLI_FORCE_UTF8_ENCODING') -ceq $parentUtf8Setting
                } | ConvertTo-Json -Depth 8),
            [System.Text.UTF8Encoding]::new($false))
    }
    finally {
        if ($owned.Count -gt 0) {
            Stop-VerificationOwnedProcessRecords -StartedProcesses $owned -CleanupDeadlineUtc $policy.CleanupDeadlineUtc
        }
    }
    return
}

if ($Scenario -ceq 'fanout-order') {
    $fanoutRoots = [System.Collections.Generic.List[object]]::new()
    foreach ($index in 1..2) {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'pwsh'
        $startInfo.WorkingDirectory = $repositoryRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @(
                '-NoProfile',
                '-File',
                $childScript,
                '-Scenario',
                'descendant-child',
                '-LedgerPath',
                $ledgerPath,
                '-DescendantScriptPath',
                $descendantScript)) {
            [void]$startInfo.ArgumentList.Add($argument)
        }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw "Unable to start fanout root $index."
        }
        Write-LifecycleLedgerEntry -Process $process
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $identity = Get-VerificationProcessIdentity `
            -Process $process `
            -CommandIdentity "pwsh -File $childScript -Scenario descendant-child"
        [void]$fanoutRoots.Add([pscustomobject]@{
                Name = "fanout-$index"
                Process = $process
                ProcessId = $identity.ProcessId
                RootProcessIdentity = "$($identity.StartTimeUtcTicks)|$($identity.ProcessId)"
                CommandIdentity = "pwsh -File $childScript -Scenario descendant-child"
                StandardOutputTask = $outputTask
                StandardErrorTask = $errorTask
                Directory = $DiagnosticsDirectory
            })
    }

    $fanoutDeadlineUtc = [DateTime]::UtcNow.AddSeconds(4)
    $fanoutExecutionDeadlineUtc = $fanoutDeadlineUtc.AddSeconds(-10)
    $functionalCleanup = Invoke-VerificationFunctionalCleanup `
        -Entries @($fanoutRoots.ToArray()) `
        -ExecutionDeadlineUtc $fanoutExecutionDeadlineUtc `
        -CleanupDeadlineUtc $fanoutDeadlineUtc `
        -FailureCleanup `
        -StopRoots `
        -PrimitiveObserver $primitiveObserver
    $lineageResults = [System.Collections.Generic.List[object]]::new()
    foreach ($entryResult in @($functionalCleanup.EntryResults)) {
        if ($null -ne $entryResult.Result) {
            [void]$lineageResults.Add($entryResult.Result)
        }
    }
    $firstCleanupPrimitiveSequence = @(
        $primitiveEvents.ToArray() |
            Where-Object { $_.Operation -ceq 'lineage-snapshot' -or $_.Operation -ceq 'creation-query' } |
            Sort-Object Sequence |
            Select-Object -First 1).Sequence
    $lastRootStopPrimitiveSequence = @(
        $primitiveEvents.ToArray() |
            Where-Object { $_.Operation -ceq 'root-stop' } |
            Sort-Object Sequence |
            Select-Object -Last 1).Sequence
    $rootsExitedBeforeLineage = $firstCleanupPrimitiveSequence -gt $lastRootStopPrimitiveSequence
    [System.IO.File]::WriteAllText(
        $ResultPath,
        ([ordered]@{
                scenario = $Scenario
                fanoutRootCount = $fanoutRoots.Count
                fanoutRootsExitedBeforeLineage = $rootsExitedBeforeLineage
                fanoutFailures = @($functionalCleanup.FanoutFailures)
                fanoutRootIds = @($fanoutRoots | ForEach-Object { $_.ProcessId })
                lifecycleTransitionCounts = @($lineageResults | ForEach-Object { $_.CleanupTransitionCount })
                remainingOwnedProcessIds = @($lineageResults | ForEach-Object { $_.RemainingOwnedProcessIds })
                secondaryDiagnostics = @($lineageResults | ForEach-Object { $_.SecondaryDiagnostics })
                primitiveEvents = @($primitiveEvents.ToArray())
                cleanupDeadlineUtcTicks = $fanoutDeadlineUtc.Ticks
            } | ConvertTo-Json -Depth 8 -Compress),
        [System.Text.UTF8Encoding]::new($false))
    exit 0
}

if ($Scenario -ceq 'same-pwsh-late-fault') {
    function Start-SamePwshLifecycleRoot {
        param(
            [Parameter(Mandatory)]
            [string]$Directory
        )

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'pwsh'
        $startInfo.WorkingDirectory = $repositoryRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @(
                '-NoProfile',
                '-File',
                $childScript,
                '-Scenario',
                'normal',
                '-LedgerPath',
                $ledgerPath)) {
            [void]$startInfo.ArgumentList.Add($argument)
        }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw 'Unable to start the same-pwsh lifecycle root.'
        }
        Write-LifecycleLedgerEntry -Process $process
        $identity = Get-VerificationProcessIdentity -Process $process -CommandIdentity 'pwsh -File verification-process-lifecycle-child.ps1 -Scenario normal'
        return [pscustomobject]@{
            Process = $process
            Identity = $identity
            StandardOutputTask = $process.StandardOutput.ReadToEndAsync()
            StandardErrorTask = $process.StandardError.ReadToEndAsync()
            Directory = $Directory
        }
    }

    $aDirectory = Join-Path $DiagnosticsDirectory 'same-pwsh-A'
    $bDirectory = Join-Path $DiagnosticsDirectory 'same-pwsh-B'
    [void](New-Item -ItemType Directory -Path $aDirectory -Force)
    [void](New-Item -ItemType Directory -Path $bDirectory -Force)
    $a = Start-SamePwshLifecycleRoot -Directory $aDirectory
    $aGate = [System.Threading.ManualResetEventSlim]::new($false)
    $aStdout = [System.Threading.Tasks.Task[string]]::FromResult([string]'A-complete')
    $aStderr = [VerificationLifecycleProbeTasks]::FaultWhen($aGate, 'lifecycle-A late fault')
    $aDeadline = [DateTime]::UtcNow.AddSeconds(1)
    $aResult = Invoke-BoundedProcessLifecycle `
        -Process $a.Process `
        -StandardOutputTask $aStdout `
        -StandardErrorTask $aStderr `
        -RootProcessId $a.Identity.ProcessId `
        -RootProcessIdentity ("$($a.Identity.StartTimeUtcTicks)|$($a.Identity.ProcessId)") `
        -CommandIdentity 'same-pwsh-A' `
        -DiagnosticsDirectory $a.Directory `
        -ProcessDeadlineUtc ([DateTime]::UtcNow) `
        -CleanupDeadlineUtc $aDeadline `
        -PrimitiveObserver $primitiveObserver `
        -LifecycleName 'same-pwsh-A'

    $bStarted = [System.Threading.ManualResetEventSlim]::new($false)
    $primitiveObserverState.ContaminationBStarted = $bStarted
    $bGate = [System.Threading.ManualResetEventSlim]::new($false)
    $releaseA = [VerificationLifecycleProbeTasks]::ReleaseGateWhen($bStarted, $aGate)
    $releaseB = [VerificationLifecycleProbeTasks]::ReleaseGateWhen($bStarted, $bGate)
    $b = Start-SamePwshLifecycleRoot -Directory $bDirectory
    $bStdout = [System.Threading.Tasks.Task[string]]::FromResult([string]'B-complete')
    $bStderr = [VerificationLifecycleProbeTasks]::FaultWhen($bGate, 'lifecycle-B owned fault')
    $bDeadline = [DateTime]::UtcNow.AddSeconds(3)
    $bResult = Invoke-BoundedProcessLifecycle `
        -Process $b.Process `
        -StandardOutputTask $bStdout `
        -StandardErrorTask $bStderr `
        -RootProcessId $b.Identity.ProcessId `
        -RootProcessIdentity ("$($b.Identity.StartTimeUtcTicks)|$($b.Identity.ProcessId)") `
        -CommandIdentity 'same-pwsh-B' `
        -DiagnosticsDirectory $b.Directory `
        -ProcessDeadlineUtc ([DateTime]::UtcNow) `
        -CleanupDeadlineUtc $bDeadline `
        -PrimitiveObserver $primitiveObserver `
        -LifecycleName 'same-pwsh-B'
    [void]($releaseA.Wait(2000))
    [void]($releaseB.Wait(2000))
    Drain-VerificationLateTaskObservations `
        -ObservationScope $aResult.ObservationScope `
        -PrimitiveObserver $primitiveObserver `
        -IncludePostSeal
    [System.IO.File]::WriteAllText(
        $ResultPath,
        ([ordered]@{
                scenario = $Scenario
                lifecycleASecondaryDiagnostics = @($aResult.SecondaryDiagnostics)
                lifecycleBSecondaryDiagnostics = @($bResult.SecondaryDiagnostics)
                lifecycleBContainsLifecycleADiagnostic = [bool](@($bResult.SecondaryDiagnostics) -match 'lifecycle-A')
                lifecycleAPostSealFaultObservationCount = @($primitiveEvents.ToArray() | Where-Object { $_.Operation -ceq 'late-task-fault' -and $_.Context -eq 'stderr' }).Count
                lifecycleAStdout = $aResult.StandardOutput
                lifecycleBStdout = $bResult.StandardOutput
                primitiveEvents = @($primitiveEvents.ToArray())
                cleanupDeadlineUtcTicks = $bDeadline.Ticks
            } | ConvertTo-Json -Depth 8 -Compress),
        [System.Text.UTF8Encoding]::new($false))
    exit 0
}

if ($Scenario -ceq 'post-start-exception') {
    $postStartDirectory = Join-Path $DiagnosticsDirectory 'post-start'
    [void](New-Item -ItemType Directory -Path $postStartDirectory -Force)
    # Seed a nonempty artifact so a post-start failure can prove that an empty outer catch
    # write never replaces an existing diagnostic.
    [System.IO.File]::WriteAllText(
        (Join-Path $postStartDirectory 'stderr.log'),
        'existing-stderr-artifact',
        [System.Text.UTF8Encoding]::new($false))
    $signal = [System.Threading.ManualResetEventSlim]::new($true)
    $guard = [VerificationPostStartFaultGuard]::new($signal)
    . (Join-Path $repositoryRoot 'scripts' 'verify-refactor.ps1') `
        -Mode Quick `
        -InternalTestGuard $guard
    $caughtException = $null
    try {
        Invoke-MonitoredCommand `
            -Label 'Deterministic post-start probe' `
            -CommandPath 'pwsh' `
            -Arguments @(
                '-NoProfile'
                '-File'
                $childScript
                '-Scenario'
                'descendant-root'
                '-DescendantScriptPath'
                $descendantScript
                '-LedgerPath'
                $ledgerPath) `
            -WorkingDirectory $repositoryRoot `
            -DiagnosticsDirectory $postStartDirectory `
            -TimeoutSeconds 2 `
            -PostStartFaultGuard $guard
    }
    catch {
        $caughtException = $_.Exception
    }
    if ($null -eq $caughtException) {
        throw 'The post-start fault probe unexpectedly completed without the guarded exception.'
    }
    $rootPid = if ($caughtException.Data.Contains('VerificationRootProcessId')) {
        [int]$caughtException.Data['VerificationRootProcessId']
    }
    else { 0 }
    $rootIdentity = if ($caughtException.Data.Contains('VerificationRootProcessIdentity')) {
        [string]$caughtException.Data['VerificationRootProcessIdentity']
    }
    else { [string]::Empty }
    $lifecycleResult = $caughtException.Data['VerificationLifecycleResult']
    $residualOwnedIds = if ($null -ne $lifecycleResult) {
        @($lifecycleResult.RemainingOwnedProcessIds)
    }
    else { @() }
    $ledgerEntries = @(Get-Content -LiteralPath $ledgerPath -ErrorAction SilentlyContinue |
            ForEach-Object { $_ | ConvertFrom-Json })
    $ledgerResiduals = @($ledgerEntries | Where-Object {
            Test-ProbeExactIdentityAlive -ProcessId ([int]$_.pid) -CreationIdentity ([long]$_.creationIdentity) } |
            ForEach-Object { [int]$_.pid })
    $artifactText = if (Test-Path -LiteralPath (Join-Path $postStartDirectory 'stderr.log') -PathType Leaf) {
        [System.IO.File]::ReadAllText((Join-Path $postStartDirectory 'stderr.log'), [System.Text.UTF8Encoding]::new($false))
    }
    else { $null }
    [System.IO.File]::WriteAllText(
        $ResultPath,
        ([ordered]@{
                scenario = $Scenario
                primaryMessage = $caughtException.Message
                primaryType = $caughtException.GetType().FullName
                rootProcessId = $rootPid
                rootProcessIdentity = $rootIdentity
                rootResidual = if ($rootPid -gt 0 -and $rootIdentity -match '^([0-9]+)\|') {
                    Test-ProbeExactIdentityAlive -ProcessId $rootPid -CreationIdentity ([long]$Matches[1])
                }
                else { $false }
                remainingOwnedProcessIds = @($residualOwnedIds)
                ledgerProcessIds = @($ledgerEntries | ForEach-Object { [int]$_.pid })
                ledgerResidualProcessIds = $ledgerResiduals
                lifecycleSecondaryDiagnostics = if ($null -ne $lifecycleResult) { @($lifecycleResult.SecondaryDiagnostics) } else { @() }
                stdoutArtifact = if (Test-Path -LiteralPath (Join-Path $postStartDirectory 'stdout.log') -PathType Leaf) { [System.IO.File]::ReadAllText((Join-Path $postStartDirectory 'stdout.log'), [System.Text.UTF8Encoding]::new($false)) } else { $null }
                stderrArtifact = $artifactText
                primitiveEvents = @($primitiveEvents.ToArray())
            } | ConvertTo-Json -Depth 8 -Compress),
        [System.Text.UTF8Encoding]::new($false))
    exit 0
}

if ($Scenario -ceq 'functional-completed-success') {
    $directory = Join-Path $DiagnosticsDirectory 'functional-completed-success'
    [void](New-Item -ItemType Directory -Path $directory -Force)
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'pwsh'
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $childScript, '-Scenario', 'late-success')) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'Unable to start the completed-success wrapper root.'
    }
    $identity = Get-VerificationProcessIdentity `
        -Process $process `
        -CommandIdentity 'pwsh -File verification-process-lifecycle-child.ps1 -Scenario late-success'
    $entry = [pscustomobject]@{
        Name = 'functional-completed-success'
        Directory = $directory
        Process = $process
        ProcessId = $identity.ProcessId
        RootProcessIdentity = "$($identity.StartTimeUtcTicks)|$($identity.ProcessId)"
        CommandIdentity = 'pwsh -File verification-process-lifecycle-child.ps1 -Scenario late-success'
        StandardOutputTask = $process.StandardOutput.ReadToEndAsync()
        StandardErrorTask = $process.StandardError.ReadToEndAsync()
    }
    # wrapperへ渡す前に成功と両ストリームの完了を確定し、子の起動時間を検証対象から分離する。
    Wait-ProbeRootExitBounded -Process $process -TimeoutMilliseconds 30000
    if (-not [System.Threading.Tasks.Task]::WaitAll(
            [System.Threading.Tasks.Task[]]@($entry.StandardOutputTask, $entry.StandardErrorTask), 30000)) {
        throw 'The completed-success wrapper streams did not complete within the probe watchdog.'
    }
    $executionDeadlineUtc = [DateTime]::UtcNow.AddSeconds(5)
    $cleanupDeadlineUtc = $executionDeadlineUtc.AddSeconds(10)
    $functionalCleanup = Invoke-VerificationFunctionalCleanup `
        -Entries @($entry) `
        -ExecutionDeadlineUtc $executionDeadlineUtc `
        -CleanupDeadlineUtc $cleanupDeadlineUtc `
        -PrimitiveObserver $primitiveObserver
    $entryResult = @($functionalCleanup.EntryResults)[0]
    [System.IO.File]::WriteAllText(
        $ResultPath,
        ([ordered]@{
                scenario = $Scenario
                entryName = $entryResult.Entry.Name
                skippedAfterDeadline = [bool]$entryResult.SkippedAfterDeadline
                entryError = if ($null -ne $entryResult.Error) { $entryResult.Error.Exception.Message } else { $null }
                processTimedOut = if ($null -ne $entryResult.Result) { [bool]$entryResult.Result.ProcessTimedOut } else { $null }
                processExited = if ($null -ne $entryResult.Result) { [bool]$entryResult.Result.ProcessExited } else { $null }
                exitCode = if ($null -ne $entryResult.Result) { $entryResult.Result.ExitCode } else { $null }
                primaryFailureKind = if ($null -ne $entryResult.Result) { $entryResult.Result.PrimaryFailureKind } else { $null }
                stdout = if ($null -ne $entryResult.Result) { $entryResult.Result.StandardOutput } else { $null }
                stderr = if ($null -ne $entryResult.Result) { $entryResult.Result.StandardError } else { $null }
                stdoutArtifact = if (Test-Path -LiteralPath (Join-Path $directory 'stdout.log') -PathType Leaf) {
                    [System.IO.File]::ReadAllText((Join-Path $directory 'stdout.log'), [System.Text.UTF8Encoding]::new($false))
                }
                else { $null }
                executionDeadlineUtcTicks = $executionDeadlineUtc.Ticks
                cleanupDeadlineUtcTicks = $cleanupDeadlineUtc.Ticks
                cleanupCutoffUtcTicks = if ($null -ne $entryResult.Result) { $entryResult.Result.CleanupCutoffUtc.Ticks } else { $null }
                secondaryDiagnostics = if ($null -ne $entryResult.Result) { @($entryResult.Result.SecondaryDiagnostics) } else { @($entryResult.Error.Exception.Message) }
            } | ConvertTo-Json -Depth 8 -Compress),
        [System.Text.UTF8Encoding]::new($false))
    exit 0
}

if ($Scenario -ceq 'functional-shared-deadline') {
    function Start-SharedDeadlineRoot {
        param(
            [Parameter(Mandatory)]
            [string]$Directory
        )

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'pwsh'
        $startInfo.WorkingDirectory = $repositoryRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile', '-File', $childScript, '-Scenario', 'normal')) {
            [void]$startInfo.ArgumentList.Add($argument)
        }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw 'Unable to start the shared-deadline root.'
        }
        $identity = Get-VerificationProcessIdentity -Process $process -CommandIdentity 'pwsh -File verification-process-lifecycle-child.ps1 -Scenario normal'
        return [pscustomobject]@{
            Name = [IO.Path]::GetFileName($Directory)
            Directory = $Directory
            Process = $process
            ProcessId = $identity.ProcessId
            RootProcessIdentity = "$($identity.StartTimeUtcTicks)|$($identity.ProcessId)"
            CommandIdentity = 'pwsh -File verification-process-lifecycle-child.ps1 -Scenario normal'
            StandardOutputTask = $process.StandardOutput.ReadToEndAsync()
            StandardErrorTask = $process.StandardError.ReadToEndAsync()
        }
    }

    $firstDirectory = Join-Path $DiagnosticsDirectory 'shared-deadline-first'
    $secondDirectory = Join-Path $DiagnosticsDirectory 'shared-deadline-second'
    [void](New-Item -ItemType Directory -Path $firstDirectory -Force)
    [void](New-Item -ItemType Directory -Path $secondDirectory -Force)
    $first = Start-SharedDeadlineRoot -Directory $firstDirectory
    $second = Start-SharedDeadlineRoot -Directory $secondDirectory
    $neverReleased = [System.Threading.ManualResetEventSlim]::new($false)
    $first.StandardOutputTask = [System.Threading.Tasks.Task[string]]::FromResult([string]'first-complete')
    $first.StandardErrorTask = [VerificationLifecycleProbeTasks]::FaultWhen($neverReleased, 'first pending stream')
    $sharedDeadline = [DateTime]::UtcNow.AddSeconds(1)
    $sharedExecutionDeadline = $sharedDeadline.AddSeconds(-10)
    $cleanup = Invoke-VerificationFunctionalCleanup `
        -Entries @($first, $second) `
        -ExecutionDeadlineUtc $sharedExecutionDeadline `
        -CleanupDeadlineUtc $sharedDeadline `
        -FailureCleanup `
        -PrimitiveObserver $primitiveObserver
    $entryResults = @($cleanup.EntryResults)
    [System.IO.File]::WriteAllText(
        $ResultPath,
        ([ordered]@{
                scenario = $Scenario
                sharedCleanupDeadlineUtcTicks = $sharedDeadline.Ticks
                fanoutFailures = @($cleanup.FanoutFailures)
                entryNames = @($entryResults | ForEach-Object { $_.Entry.Name })
                skippedAfterDeadline = @($entryResults | ForEach-Object { [bool]$_.SkippedAfterDeadline })
                entrySecondaryDiagnostics = @($entryResults | ForEach-Object { if ($null -ne $_.Result) { @($_.Result.SecondaryDiagnostics) } else { @($_.Error.Exception.Message) } })
                primitiveEvents = @($primitiveEvents.ToArray())
            } | ConvertTo-Json -Depth 8 -Compress),
        [System.Text.UTF8Encoding]::new($false))
    exit 0
}

$rootScenario = if ($Scenario -in @(
        'nonzero-descendant',
        'stream-timeout',
        'asymmetric-stdout-complete',
        'asymmetric-stderr-complete',
        'missing-result',
        'expired-residual')) {
    'descendant-root'
}
else {
    $Scenario
}
$rootInfo = [System.Diagnostics.ProcessStartInfo]::new()
$rootInfo.FileName = 'pwsh'
$rootInfo.WorkingDirectory = $repositoryRoot
$rootInfo.UseShellExecute = $false
$rootInfo.CreateNoWindow = $true
$rootInfo.RedirectStandardOutput = $true
$rootInfo.RedirectStandardError = $true
foreach ($argument in @(
        '-NoProfile',
        '-File',
        $childScript,
        '-Scenario',
        $rootScenario,
        '-LedgerPath',
        $ledgerPath)) {
    [void]$rootInfo.ArgumentList.Add($argument)
}
if ($rootScenario -ceq 'descendant-root') {
    [void]$rootInfo.ArgumentList.Add('-DescendantScriptPath')
    [void]$rootInfo.ArgumentList.Add($descendantScript)
}
if ($Scenario -ceq 'nonzero-descendant') {
    # The root probe still exits nonzero after starting its inherited-handle child.
    $rootInfo.Environment['BMS_LIFECYCLE_PROBE_NONZERO'] = '1'
}

$root = [System.Diagnostics.Process]::new()
$root.StartInfo = $rootInfo
$ready = $null
$release = $null
$releaseTask = $null
$lateExitTimeUtc = $null
$rootStarted = $false
$preparationFailure = $null
if ($Scenario -ceq 'late-success') {
    $eventPrefix = 'BeMusicSeeker-Lifecycle-' + [Guid]::NewGuid().ToString('N')
    $ready = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, "$eventPrefix-ready")
    $release = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, "$eventPrefix-release")
    foreach ($argument in @('-ReadyEventName', "$eventPrefix-ready", '-ReleaseEventName', "$eventPrefix-release")) {
        [void]$rootInfo.ArgumentList.Add($argument)
    }
}
try {
    if (-not $root.Start()) {
        throw 'Unable to start the lifecycle root probe.'
    }
    $rootStarted = $true
    Write-LifecycleLedgerEntry -Process $root
    $ledgerReadyDeadlineUtc = [DateTime]::UtcNow.AddSeconds(5)
    if ($Scenario -ceq 'expired-residual') {
        # Do not enter the bounded lifecycle until the child launch has produced its exact
        # sidecar identity.  This makes the residual case deterministic without making the
        # production lifecycle discover ownership from the ledger.
        while ([DateTime]::UtcNow -lt $ledgerReadyDeadlineUtc) {
            if (@(Get-Content -LiteralPath $ledgerPath -ErrorAction SilentlyContinue).Count -ge 2) {
                break
            }
            Start-Sleep -Milliseconds 20
        }
        if (@(Get-Content -LiteralPath $ledgerPath -ErrorAction SilentlyContinue).Count -lt 2) {
            throw 'The expired residual probe did not observe the child launch ledger entry.'
        }
        $ledgerEntries = @(Get-Content -LiteralPath $ledgerPath |
                ForEach-Object { $_ | ConvertFrom-Json })
        $primitiveObserverState.ResidualProcessId = [int]$ledgerEntries[-1].pid
    }
    $sourceOutputTask = $root.StandardOutput.ReadToEndAsync()
    $sourceErrorTask = $root.StandardError.ReadToEndAsync()
    $sourceObservationScope = [VerificationProcessTaskObservationScope]::new()
    $standardOutputTask = $sourceOutputTask
    $standardErrorTask = $sourceErrorTask
    if ($Scenario -in @('descendant-root', 'nonzero-descendant')) {
        # 実際の子孫生成とroot終了を準備で確定する。子孫の停止は本体の所有者へ残す。
        Wait-ProbeRootExitBounded -Process $root -TimeoutMilliseconds 30000
        $descendantEntries = @(Get-Content -LiteralPath $ledgerPath | ForEach-Object { $_ | ConvertFrom-Json })
        if ($descendantEntries.Count -ne 2 -or
            -not (Test-ProbeExactIdentityAlive -ProcessId $descendantEntries[1].pid -CreationIdentity $descendantEntries[1].creationIdentity)) {
            throw 'The descendant probe did not prepare its exact live child before root exit.'
        }
    }
    if ($Scenario -ceq 'late-success') {
        # flush後の通知を待ってから既存の1秒期限を開始し、その期限後だけ終了gateを開く。
        $readyWatchdog = [DateTime]::UtcNow.AddSeconds(30)
        while (-not $ready.WaitOne(100)) {
            if ($root.HasExited -or [DateTime]::UtcNow -ge $readyWatchdog) {
                throw 'The late-success child did not signal flushed output before the probe watchdog.'
            }
        }
        $lateExecutionDeadlineUtc = [DateTime]::UtcNow.AddSeconds(1)
        $releaseTask = [VerificationLifecycleProbeTasks]::ReleaseEventAfter($release, $lateExecutionDeadlineUtc.Ticks)
        Wait-ProbeRootExitBounded -Process $root -TimeoutMilliseconds 30000
        $lateExitTimeUtc = $root.ExitTime.ToUniversalTime()
        [void]$releaseTask.GetAwaiter().GetResult()
    }
}
catch {
    $preparationFailure = $_
    throw
}
finally {
    if ($null -ne $release) {
        [void]$release.Set()
        try {
            if ($rootStarted -and $null -eq $lateExitTimeUtc -and -not $root.HasExited) {
                $root.Kill($true)
                Wait-ProbeRootExitBounded -Process $root -TimeoutMilliseconds 30000
            }
            if ($null -ne $releaseTask) {
                [void]$releaseTask.GetAwaiter().GetResult()
            }
        }
        catch {
            if ($null -eq $preparationFailure) {
                throw
            }
            $preparationFailure.Exception.Data['ProbeCleanupFailure'] = $_.Exception.Message
        }
        finally {
            $ready.Dispose()
            $release.Dispose()
        }
    }
}
if ($Scenario -in @('stream-timeout', 'asymmetric-stdout-complete', 'asymmetric-stderr-complete')) {
    # The retained source reads remain observed independently so inherited handles do not
    # create an unobserved task fault.  Root exit is confirmed before the lifecycle
    # deadline is created; the process budget remains a production contract, while this
    # watchdog prevents process-heavy fan-out from consuming that contract before the
    # stream-drain behavior is exercised.
    Register-VerificationTaskObservation `
        -Task $sourceOutputTask `
        -ObservationScope $sourceObservationScope
    Register-VerificationTaskObservation `
        -Task $sourceErrorTask `
        -ObservationScope $sourceObservationScope
    Wait-ProbeRootExitBounded -Process $root -TimeoutMilliseconds 30000

    # The production seam sees deterministic faulting tasks after its cleanup deadline.
    $standardOutputTask = [VerificationLifecycleProbeTasks]::FaultAfter('stdout late fault', 4500)
    $standardErrorTask = [VerificationLifecycleProbeTasks]::FaultAfter('stderr late fault', 4500)
}
if ($Scenario -ceq 'asymmetric-stdout-complete') {
    $standardOutputTask = [System.Threading.Tasks.Task[string]]::FromResult([string]'stdout-asymmetric-complete')
}
elseif ($Scenario -ceq 'asymmetric-stderr-complete') {
    $standardErrorTask = [System.Threading.Tasks.Task[string]]::FromResult([string]'stderr-asymmetric-complete')
}
if ($Scenario -ceq 'missing-result') {
    # Wait only for the sidecar ownership signal so the harness test can prove that
    # cleanup is independent of production result JSON.  This is a bounded failure
    # watchdog, not a normal-completion delay.
    $ledgerDeadlineUtc = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $ledgerDeadlineUtc) {
        if ((Get-Content -LiteralPath $ledgerPath -ErrorAction SilentlyContinue).Count -ge 2) {
            break
        }
        Start-Sleep -Milliseconds 20
    }
    throw 'Intentional probe failure before production lifecycle result persistence.'
}
$commandIdentity = "pwsh -File $childScript -Scenario $rootScenario"
$identity = Get-VerificationProcessIdentity -Process $root -CommandIdentity $commandIdentity
if ($Scenario -eq 'terminal-diagnostic' -or $Scenario -eq 'terminal-flush-failure') {
    # A directory at the destination makes the terminal stream replace fail without
    # destroying a pre-existing artifact.  The final diagnostic flush must still capture
    # that terminal failure when its sink is healthy.
    [void](New-Item -ItemType Directory -Path (Join-Path $DiagnosticsDirectory 'stdout.log') -Force)
}
if ($Scenario -eq 'terminal-flush-failure') {
    # The sink fault is deterministic and local: a directory occupies the final log path,
    # so the result secondary diagnostics are authoritative and no empty file is created.
    [void](New-Item -ItemType Directory -Path (Join-Path $DiagnosticsDirectory 'process-lifecycle.log') -Force)
}
$processBudgetSeconds = if ($Scenario -ceq 'normal' -or $Scenario -ceq 'nonzero') { 10 } elseif ($Scenario -ceq 'late-success') { 1 } else { 2 }
$phaseStartUtc = [DateTime]::UtcNow
$processDeadlineUtc = if ($Scenario -ceq 'late-success') {
    $lateExecutionDeadlineUtc
}
elseif ($Scenario -ceq 'expired-residual') {
    # Start this failure probe with an already-expired execution deadline while retaining
    # the exact ten-second cleanup pair for the owned residual.
    $phaseStartUtc.AddSeconds(-1)
}
else {
    $phaseStartUtc.AddSeconds($processBudgetSeconds)
}
$phaseDeadlineUtc = $processDeadlineUtc.AddSeconds(10)
$residualGate = $null
$residualReleaseTask = $null
if ($Scenario -ceq 'expired-residual') {
    $residualGate = [System.Threading.ManualResetEventSlim]::new($false)
    $primitiveObserverState.ResidualGate = $residualGate
    $residualReleaseTask = [VerificationLifecycleProbeTasks]::ReleaseGateAt(
        $residualGate,
        $phaseDeadlineUtc.Ticks)
}
$result = Invoke-BoundedProcessLifecycle `
    -Process $root `
    -StandardOutputTask $standardOutputTask `
    -StandardErrorTask $standardErrorTask `
    -RootProcessId $identity.ProcessId `
    -RootProcessIdentity ("$($identity.StartTimeUtcTicks)|$($identity.ProcessId)") `
    -CommandIdentity $commandIdentity `
    -DiagnosticsDirectory $DiagnosticsDirectory `
    -ProcessDeadlineUtc $processDeadlineUtc `
    -CleanupDeadlineUtc $phaseDeadlineUtc `
    -RetainedExitTimeUtc $(if ($null -ne $lateExitTimeUtc) { $lateExitTimeUtc } else { [DateTime]::MinValue }) `
    -PrimitiveObserver $primitiveObserver `
    -LifecycleName $Scenario

if ($Scenario -ceq 'stream-timeout') {
    # Wait for both deterministic late-fault continuations without extending production
    # cleanup or using a fixed sleep.  This is a bounded probe watchdog only.
    $lateFaultDeadlineUtc = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $lateFaultDeadlineUtc) {
        Drain-VerificationLateTaskObservations `
            -ObservationScope $result.ObservationScope `
            -PrimitiveObserver $primitiveObserver `
            -IncludePostSeal
        if (@($primitiveEvents.ToArray() | Where-Object { $_.Operation -ceq 'late-task-fault' }).Count -ge 2) {
            break
        }
        Start-Sleep -Milliseconds 20
    }
}

if ($Scenario -ceq 'nonzero-descendant' -and $null -ne $result.ExitCode -and $result.ExitCode -eq 0) {
    throw 'The nonzero descendant probe did not produce its required primary exit failure.'
}

$serializedResult = [ordered]@{
    scenario = $Scenario
    rootProcessId = $result.RootProcessId
    processTimedOut = $result.ProcessTimedOut
    processExited = $result.ProcessExited
    exitCode = $result.ExitCode
    primaryFailureKind = $result.PrimaryFailureKind
    stdout = $result.StandardOutput
    stderr = $result.StandardError
    cleanupDiagnostics = @($result.CleanupDiagnostics)
    secondaryDiagnostics = @($result.SecondaryDiagnostics)
    remainingOwnedProcessIds = @($result.RemainingOwnedProcessIds)
    cleanupTransitionCount = $result.CleanupTransitionCount
    cleanupDeadlineUtc = $result.CleanupDeadlineUtc
    cleanupDeadlineUtcTicks = $result.CleanupDeadlineUtc.Ticks
    executionDeadlineUtcTicks = $processDeadlineUtc.Ticks
    actualExitTimeUtcTicks = if ($null -ne $lateExitTimeUtc) { $lateExitTimeUtc.Ticks } else { $null }
    cleanupCutoffUtcTicks = $result.CleanupCutoffUtc.Ticks
    terminalOperationsDeadlineUtcTicks = $result.TerminalOperationsDeadlineUtc.Ticks
    primitiveEvents = @($primitiveEvents.ToArray())
    stdoutArtifact = if (Test-Path -LiteralPath (Join-Path $DiagnosticsDirectory 'stdout.log') -PathType Leaf) {
        [System.IO.File]::ReadAllText((Join-Path $DiagnosticsDirectory 'stdout.log'), [System.Text.UTF8Encoding]::new($false))
    }
    else { $null }
    stderrArtifact = if (Test-Path -LiteralPath (Join-Path $DiagnosticsDirectory 'stderr.log') -PathType Leaf) {
        [System.IO.File]::ReadAllText((Join-Path $DiagnosticsDirectory 'stderr.log'), [System.Text.UTF8Encoding]::new($false))
    }
    else { $null }
    lifecycleArtifact = if (Test-Path -LiteralPath (Join-Path $DiagnosticsDirectory 'process-lifecycle.log') -PathType Leaf) {
        [System.IO.File]::ReadAllText((Join-Path $DiagnosticsDirectory 'process-lifecycle.log'), [System.Text.UTF8Encoding]::new($false))
    }
    else { $null }
    diagnosticsDirectory = $DiagnosticsDirectory
    ownershipLedgerPath = $ledgerPath
} | ConvertTo-Json -Depth 8 -Compress

# The inherited-handle scenario intentionally keeps the inner redirected pipe open until
# bounded cleanup completes.  Persist the structured result independently of this probe's
# own stdout EOF so callers observe the production seam result, not handle inheritance from
# an outer test-harness pipe.
[System.IO.File]::WriteAllText(
    $ResultPath,
    $serializedResult,
    [System.Text.UTF8Encoding]::new($false))
