[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('normal', 'nonzero', 'descendant-root', 'nonzero-descendant', 'stream-timeout', 'missing-result', 'fanout-order')]
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
    public static Task<string> HoldCompletion(Task<string> source)
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = source.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return gate.Task;
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
        -CleanupFailures $fanoutFailures
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
                -TerminateProcessTree))
    }
    [System.IO.File]::WriteAllText(
        $ResultPath,
        ([ordered]@{
                scenario = $Scenario
                fanoutRootCount = $fanoutRoots.Count
                fanoutRootsExitedBeforeLineage = $rootsExitedBeforeLineage
                fanoutFailures = @($fanoutFailures)
                lifecycleTransitionCounts = @($lineageResults | ForEach-Object { $_.CleanupTransitionCount })
                remainingOwnedProcessIds = @($lineageResults | ForEach-Object { $_.RemainingOwnedProcessIds })
                secondaryDiagnostics = @($lineageResults | ForEach-Object { $_.SecondaryDiagnostics })
            } | ConvertTo-Json -Depth 8 -Compress),
        [System.Text.UTF8Encoding]::new($false))
    exit 0
}

$rootScenario = if ($Scenario -ceq 'nonzero-descendant' -or $Scenario -ceq 'stream-timeout' -or $Scenario -ceq 'missing-result') {
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
$standardOutputTask = $root.StandardOutput.ReadToEndAsync()
$standardErrorTask = $root.StandardError.ReadToEndAsync()
if ($Scenario -ceq 'stream-timeout') {
    # Keep the real redirected-handle reads active, but expose an intentionally
    # incomplete completion gate to the production seam.  The source tasks still drain
    # the inherited handles and observe any late faults; the gate makes the bounded
    # stream-timeout path deterministic without a fixed sleep.
    $standardOutputTask = [VerificationLifecycleProbeTasks]::HoldCompletion($standardOutputTask)
    $standardErrorTask = [VerificationLifecycleProbeTasks]::HoldCompletion($standardErrorTask)
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
$processBudgetSeconds = if ($Scenario -ceq 'normal' -or $Scenario -ceq 'nonzero') { 10 } else { 2 }
$cleanupBudgetSeconds = if ($Scenario -ceq 'normal' -or $Scenario -ceq 'nonzero') { 12 } else { 4 }
$phaseDeadlineUtc = [DateTime]::UtcNow.AddSeconds($cleanupBudgetSeconds)
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
    -CleanupDeadlineUtc $phaseDeadlineUtc

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
