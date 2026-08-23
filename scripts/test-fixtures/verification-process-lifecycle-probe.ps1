[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('normal', 'nonzero', 'descendant-root', 'nonzero-descendant')]
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

$rootScenario = if ($Scenario -ceq 'nonzero-descendant') { 'descendant-root' } else { $Scenario }
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
        $rootScenario)) {
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
$standardOutputTask = $root.StandardOutput.ReadToEndAsync()
$standardErrorTask = $root.StandardError.ReadToEndAsync()
$commandIdentity = "pwsh -File $childScript -Scenario $rootScenario"
$identity = Get-VerificationProcessIdentity -Process $root -CommandIdentity $commandIdentity
$processBudgetSeconds = if ($Scenario -ceq 'normal' -or $Scenario -ceq 'nonzero') { 10 } else { 2 }
$cleanupBudgetSeconds = if ($Scenario -ceq 'normal' -or $Scenario -ceq 'nonzero') { 12 } else { 4 }
$result = Invoke-BoundedProcessLifecycle `
    -Process $root `
    -StandardOutputTask $standardOutputTask `
    -StandardErrorTask $standardErrorTask `
    -RootProcessId $identity.ProcessId `
    -RootProcessIdentity ("$($identity.StartTimeUtcTicks)|$($identity.ProcessId)") `
    -CommandIdentity $commandIdentity `
    -DiagnosticsDirectory $DiagnosticsDirectory `
    -ProcessDeadlineUtc ([DateTime]::UtcNow.AddSeconds($processBudgetSeconds)) `
    -CleanupDeadlineUtc ([DateTime]::UtcNow.AddSeconds($cleanupBudgetSeconds))

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
    diagnosticsDirectory = $DiagnosticsDirectory
} | ConvertTo-Json -Depth 8 -Compress

# The inherited-handle scenario intentionally keeps the inner redirected pipe open until
# bounded cleanup completes.  Persist the structured result independently of this probe's
# own stdout EOF so callers observe the production seam result, not handle inheritance from
# an outer test-harness pipe.
[System.IO.File]::WriteAllText(
    $ResultPath,
    $serializedResult,
    [System.Text.UTF8Encoding]::new($false))
