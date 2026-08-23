[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('normal', 'nonzero', 'descendant-root', 'nonzero-descendant', 'stream-timeout', 'missing-result', 'fanout-order', 'expired-residual')]
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
$ledgerPath = Join-Path $DiagnosticsDirectory 'ownership-ledger.jsonl'
$primitiveEvents = [System.Collections.Concurrent.ConcurrentQueue[object]]::new()
$primitiveSequence = [long]0
$primitiveObserverState = [pscustomobject]@{ ResidualGate = $null }
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
        if ($Scenario -ceq 'expired-residual' -and
            $event.Operation -ceq 'descendant-stop' -and
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
}
'@
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
                $ledgerPath)) {
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

    $fanoutFailures = [System.Collections.Generic.List[string]]::new()
    $fanoutDeadlineUtc = [DateTime]::UtcNow.AddSeconds(4)
    Invoke-VerificationRootStopFanout `
        -Entries @($fanoutRoots.ToArray()) `
        -CleanupDeadlineUtc $fanoutDeadlineUtc `
        -CleanupFailures $fanoutFailures `
        -PrimitiveObserver $primitiveObserver
    $rootsExitedBeforeLineage = $true
    foreach ($entry in $fanoutRoots) {
        if (-not $entry.Process.WaitForExit(1000)) {
            $rootsExitedBeforeLineage = $false
        }
    }
    $lineageResults = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $fanoutRoots) {
        [void]$lineageResults.Add((Invoke-BoundedProcessLifecycle `
                -Process $entry.Process `
                -StandardOutputTask $entry.StandardOutputTask `
                -StandardErrorTask $entry.StandardErrorTask `
                -RootProcessId $entry.ProcessId `
                -RootProcessIdentity $entry.RootProcessIdentity `
                -CommandIdentity $entry.CommandIdentity `
                -DiagnosticsDirectory $DiagnosticsDirectory `
                -ProcessDeadlineUtc ([DateTime]::UtcNow) `
                -PhaseDeadlineUtc $fanoutDeadlineUtc `
                -CleanupDeadlineUtc $fanoutDeadlineUtc `
                -RootStopAlreadyRequested `
                -TerminateProcessTree `
                -PrimitiveObserver $primitiveObserver `
                -LifecycleName $entry.Name))
    }
    [System.IO.File]::WriteAllText(
        $ResultPath,
        ([ordered]@{
                scenario = $Scenario
                fanoutRootCount = $fanoutRoots.Count
                fanoutRootsExitedBeforeLineage = $rootsExitedBeforeLineage
                fanoutFailures = @($fanoutFailures)
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

$rootScenario = if ($Scenario -ceq 'nonzero-descendant' -or $Scenario -ceq 'stream-timeout' -or $Scenario -ceq 'missing-result' -or $Scenario -ceq 'expired-residual') {
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
if ($Scenario -ceq 'nonzero-descendant') {
    # The root probe still exits nonzero after starting its inherited-handle child.
    $rootInfo.Environment['BMS_LIFECYCLE_PROBE_NONZERO'] = '1'
}

$root = [System.Diagnostics.Process]::new()
$root.StartInfo = $rootInfo
if (-not $root.Start()) {
    throw 'Unable to start the lifecycle root probe.'
}
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
}
$sourceOutputTask = $root.StandardOutput.ReadToEndAsync()
$sourceErrorTask = $root.StandardError.ReadToEndAsync()
$standardOutputTask = $sourceOutputTask
$standardErrorTask = $sourceErrorTask
if ($Scenario -ceq 'stream-timeout') {
    # The production seam sees deterministic faulting tasks after its cleanup deadline.
    # The retained source reads remain observed independently so inherited handles do not
    # create an unobserved task fault.
    Register-VerificationTaskObservation -Task $sourceOutputTask
    Register-VerificationTaskObservation -Task $sourceErrorTask
    $standardOutputTask = [VerificationLifecycleProbeTasks]::FaultAfter('stdout late fault', 4500)
    $standardErrorTask = [VerificationLifecycleProbeTasks]::FaultAfter('stderr late fault', 4500)
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
$processBudgetSeconds = if ($Scenario -ceq 'normal' -or $Scenario -ceq 'nonzero' -or $Scenario -ceq 'expired-residual') { 10 } else { 2 }
$scenarioCleanupSeconds = if ($Scenario -ceq 'normal' -or $Scenario -ceq 'nonzero') { 12 } elseif ($Scenario -ceq 'expired-residual') { 1 } else { 4 }
$phaseDeadlineUtc = [DateTime]::UtcNow.AddSeconds($scenarioCleanupSeconds)
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
    -ProcessDeadlineUtc ([DateTime]::UtcNow.AddSeconds($processBudgetSeconds)) `
    -PhaseDeadlineUtc $phaseDeadlineUtc `
    -CleanupDeadlineUtc $phaseDeadlineUtc `
    -PrimitiveObserver $primitiveObserver `
    -LifecycleName $Scenario

if ($Scenario -ceq 'stream-timeout') {
    # Wait for both deterministic late-fault continuations without extending production
    # cleanup or using a fixed sleep.  This is a bounded probe watchdog only.
    $lateFaultDeadlineUtc = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $lateFaultDeadlineUtc) {
        Drain-VerificationLateTaskObservations -PrimitiveObserver $primitiveObserver
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
    primitiveEvents = @($primitiveEvents.ToArray())
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
