# Shared bounded process / redirected-stream lifecycle used by the verification runner and
# deterministic process-integration probes.  This file is sourceable and has no entry-point
# side effects.

function Get-VerificationProcessIdentity {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [string]$CommandIdentity
    )

    $startTimeTicks = $null
    try {
        $startTimeTicks = $Process.StartTime.ToUniversalTime().Ticks
    }
    catch {
        # A process can exit between Start() and StartTime.  The process handle and PID are
        # still retained; the command identity remains useful for diagnostics.
    }

    return [pscustomobject]@{
        ProcessId = $Process.Id
        StartTimeUtcTicks = $startTimeTicks
        CommandIdentity = $CommandIdentity
    }
}

if (-not ('VerificationProcessSnapshotNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class VerificationProcessSnapshotEntry
{
    public int ProcessId { get; init; }
    public int ParentProcessId { get; init; }
    public long? CreationTimeUtcTicks { get; init; }
    public string ExecutableName { get; init; } = string.Empty;
}

public static class VerificationProcessSnapshotNative
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public long ToLong() => ((long)dwHighDateTime << 32) | dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out FILETIME creationTime,
        out FILETIME exitTime,
        out FILETIME kernelTime,
        out FILETIME userTime);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static VerificationProcessSnapshotEntry[] Capture()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to capture the process table.");
        }

        try
        {
            var entries = new List<VerificationProcessSnapshotEntry>();
            var nativeEntry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };
            if (!Process32FirstW(snapshot, ref nativeEntry))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 18) // ERROR_NO_MORE_FILES
                {
                    return Array.Empty<VerificationProcessSnapshotEntry>();
                }
                throw new Win32Exception(error, "Unable to read the process snapshot.");
            }

            do
            {
                entries.Add(new VerificationProcessSnapshotEntry
                {
                    ProcessId = unchecked((int)nativeEntry.th32ProcessID),
                    ParentProcessId = unchecked((int)nativeEntry.th32ParentProcessID),
                    CreationTimeUtcTicks = TryGetCreationTimeUtcTicks(nativeEntry.th32ProcessID),
                    ExecutableName = nativeEntry.szExeFile ?? string.Empty
                });
                nativeEntry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            }
            while (Process32NextW(snapshot, ref nativeEntry));

            int nextError = Marshal.GetLastWin32Error();
            if (nextError != 18) // ERROR_NO_MORE_FILES
            {
                throw new Win32Exception(nextError, "Unable to finish reading the process snapshot.");
            }
            return entries.ToArray();
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static long? TryGetCreationTimeUtcTicks(uint processId)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GetProcessTimes(process, out FILETIME creation, out _, out _, out _)
                ? DateTime.FromFileTimeUtc(creation.ToLong()).Ticks
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
'@
}

function Get-VerificationProcessTable {
    try {
        return @(
            [VerificationProcessSnapshotNative]::Capture() |
                ForEach-Object {
                    [pscustomobject]@{
                        ProcessId = $_.ProcessId
                        ParentProcessId = $_.ParentProcessId
                        CreationIdentity = if ($null -eq $_.CreationTimeUtcTicks) { '' } else { [string]$_.CreationTimeUtcTicks }
                        ExecutablePath = $_.ExecutableName
                        CommandLine = [string]::Empty
                    }
                })
    }
    catch {
        throw "Unable to observe the owned process lineage: $($_.Exception.Message)"
    }
}

function Update-VerificationProcessLineage {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IList]$Lineage,

        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$RootProcess
    )

    $RootProcessId = $RootProcess.Id
    $table = @(Get-VerificationProcessTable)
    $reachable = [System.Collections.Generic.HashSet[int]]::new()
    $rootTracked = @($Lineage | Where-Object { $_.IsRoot } | Select-Object -First 1)[0]
    if (Test-VerificationProcessExited -Process $RootProcess) {
        try {
            $rootTracked.ExitTimeUtcTicks = $RootProcess.ExitTime.ToUniversalTime().Ticks
        }
        catch {
            # The retained process handle normally provides ExitTime.  If it cannot, the
            # already captured descendant identities remain authoritative.
        }
    }
    $rootCurrent = @($table | Where-Object { $_.ProcessId -eq $RootProcessId })
    if ($null -eq $rootTracked.StartTimeUtcTicks) {
        # Without the retained root creation time, a post-exit parent PID is not a safe
        # ownership proof.  Root-handle cleanup can still proceed, but lineage expansion
        # stays disabled and the start-identity diagnostic remains visible to the caller.
    }
    elseif ($rootCurrent.Count -eq 1) {
        if ($rootCurrent[0].CreationIdentity -ceq [string]$rootTracked.StartTimeUtcTicks) {
            [void]$reachable.Add($RootProcessId)
        }
    }
    else {
        # A Toolhelp snapshot retains each child's parent PID after the root exits.  Seeding
        # the captured root identity lets cleanup discover a child that was created just
        # before a fast root exit, without requiring the root PID to remain live.
        [void]$reachable.Add($RootProcessId)
    }
    foreach ($tracked in @($Lineage | Where-Object { -not $_.IsRoot })) {
        $trackedCurrent = @($table | Where-Object {
                $_.ProcessId -eq $tracked.ProcessId -and
                ('{0}|{1}|{2}|{3}' -f $_.CreationIdentity, $_.ExecutablePath, $_.CommandLine, $_.ProcessId) -ceq $tracked.Identity })
        if ($trackedCurrent.Count -eq 1) {
            [void]$reachable.Add($tracked.ProcessId)
        }
    }

    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $table) {
            if (-not $reachable.Contains($process.ParentProcessId) -or
                $process.ProcessId -eq $RootProcessId -or
                [string]::IsNullOrWhiteSpace($process.CreationIdentity)) {
                continue
            }

            $creationTimeUtcTicks = [long]$process.CreationIdentity
            if ($creationTimeUtcTicks -lt [long]$rootTracked.StartTimeUtcTicks -or
                ($process.ParentProcessId -eq $RootProcessId -and
                    $null -ne $rootTracked.ExitTimeUtcTicks -and
                    $creationTimeUtcTicks -gt [long]$rootTracked.ExitTimeUtcTicks)) {
                # Parent PID alone is not ownership: exclude processes outside the retained
                # root handle's lifetime so PID reuse cannot expand the owned lineage.
                continue
            }

            if ($reachable.Add($process.ProcessId)) {
                $changed = $true
            }

            $identity = '{0}|{1}|{2}|{3}' -f `
                $process.CreationIdentity,
                $process.ExecutablePath,
                $process.CommandLine,
                $process.ProcessId
            $alreadyTracked = @($Lineage | Where-Object {
                    $_.ProcessId -eq $process.ProcessId -and $_.Identity -ceq $identity })
            if ($alreadyTracked.Count -eq 0) {
                [void]$Lineage.Add([pscustomobject]@{
                        ProcessId = $process.ProcessId
                        ParentProcessId = $process.ParentProcessId
                        Identity = $identity
                        CommandIdentity = if ([string]::IsNullOrWhiteSpace($process.CommandLine)) {
                            $process.ExecutablePath
                        }
                        else {
                            $process.CommandLine
                        }
                        IsRoot = $false
                    })
            }
        }
    }

    return $table
}

function Test-VerificationProcessExited {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    try {
        return [bool]$Process.HasExited
    }
    catch {
        return $true
    }
}

function Get-VerificationCurrentOwnedDescendants {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IList]$Lineage,

        [Parameter(Mandatory)]
        [object[]]$ProcessTable
    )

    $current = @()
    foreach ($tracked in @($Lineage | Where-Object { -not $_.IsRoot })) {
        $matches = @($ProcessTable | Where-Object {
                $_.ProcessId -eq $tracked.ProcessId -and
                ('{0}|{1}|{2}|{3}' -f $_.CreationIdentity, $_.ExecutablePath, $_.CommandLine, $_.ProcessId) -ceq $tracked.Identity })
        if ($matches.Count -eq 1) {
            $current += $tracked
        }
    }
    return $current
}

function Stop-VerificationOwnedProcessTree {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$RootProcess,

        [Parameter(Mandatory)]
        [System.Collections.IList]$Lineage,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics
    )

    $rootStopAttempted = $false
    while ([DateTime]::UtcNow -lt $CleanupDeadlineUtc) {
        try {
            $table = @(Update-VerificationProcessLineage -Lineage $Lineage -RootProcess $RootProcess)
        }
        catch {
            $CleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
            $table = @()
        }

        if (-not (Test-VerificationProcessExited -Process $RootProcess) -and -not $rootStopAttempted) {
            $rootStopAttempted = $true
            try {
                # Process.Kill(entireProcessTree) is rooted at the process handle supplied by
                # the runner.  It never performs a process-name or machine-wide scan.
                $RootProcess.Kill($true)
            }
            catch {
                $CleanupDiagnostics.Add(
                    "owned-process-stop: PID $RootProcessId ($($RootProcess.StartInfo.FileName)) could not be stopped: $($_.Exception.Message)")
            }
        }

        foreach ($tracked in @(Get-VerificationCurrentOwnedDescendants -Lineage $Lineage -ProcessTable $table)) {
            try {
                $descendant = [System.Diagnostics.Process]::GetProcessById($tracked.ProcessId)
                try {
                    # The PID is killed only after the current creation identity matches the identity
                    # captured while this runner's root was alive.  PID reuse is not owned.
                    $descendant.Kill($true)
                }
                finally {
                    $descendant.Dispose()
                }
            }
            catch [System.ArgumentException] {
                # The descendant exited between the identity check and GetProcessById.
            }
            catch {
                $CleanupDiagnostics.Add(
                    "owned-descendant-stop: PID $($tracked.ProcessId) ($($tracked.CommandIdentity)) could not be stopped: $($_.Exception.Message)")
            }
        }

        $rootActive = -not (Test-VerificationProcessExited -Process $RootProcess)
        $remainingDescendants = @()
        try {
            $table = @(Update-VerificationProcessLineage -Lineage $Lineage -RootProcess $RootProcess)
            $remainingDescendants = @(Get-VerificationCurrentOwnedDescendants -Lineage $Lineage -ProcessTable $table)
        }
        catch {
            $CleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
        }
        if (-not $rootActive -and $remainingDescendants.Count -eq 0) {
            return
        }

        $remainingMilliseconds = [Math]::Max(
            1,
            [int][Math]::Min(50, ($CleanupDeadlineUtc - [DateTime]::UtcNow).TotalMilliseconds))
        try {
            if ($rootActive) {
                [void]$RootProcess.WaitForExit($remainingMilliseconds)
            }
            else {
                $waitTarget = @($remainingDescendants | Select-Object -First 1)
                if ($waitTarget.Count -eq 0) {
                    break
                }
                $descendant = [System.Diagnostics.Process]::GetProcessById($waitTarget[0].ProcessId)
                try {
                    [void]$descendant.WaitForExit($remainingMilliseconds)
                }
                finally {
                    $descendant.Dispose()
                }
            }
        }
        catch {
            # A process that exits while being polled is handled by the next identity check.
        }
    }

    try {
        $table = @(Update-VerificationProcessLineage -Lineage $Lineage -RootProcess $RootProcess)
        if (-not (Test-VerificationProcessExited -Process $RootProcess)) {
            $CleanupDiagnostics.Add("owned-process-residual: PID $RootProcessId remained active after the cleanup deadline")
        }
        foreach ($tracked in @(Get-VerificationCurrentOwnedDescendants -Lineage $Lineage -ProcessTable $table)) {
            $CleanupDiagnostics.Add(
                "owned-descendant-residual: PID $($tracked.ProcessId) ($($tracked.CommandIdentity)) remained active after the cleanup deadline")
        }
    }
    catch {
        $CleanupDiagnostics.Add("process-lineage-residual-check: $($_.Exception.Message)")
    }
}

function Register-VerificationTaskObservation {
    param(
        [Parameter(Mandatory)]
        [System.Threading.Tasks.Task]$Task
    )

    # Observe a late reader fault without synchronously waiting after the cleanup deadline.
    $observer = [Action[System.Threading.Tasks.Task]]{
        param($completedTask)
        if ($completedTask.IsFaulted) {
            [void]$completedTask.Exception
        }
    }
    [void]$Task.ContinueWith($observer, [System.Threading.Tasks.TaskScheduler]::Default)
}

function Get-VerificationCompletedTaskOutput {
    param(
        [Parameter(Mandatory)]
        [System.Threading.Tasks.Task[string]]$Task,

        [Parameter(Mandatory)]
        [string]$StreamName,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics
    )

    if ($Task.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
        # The completion state is checked before the synchronous retrieval.  No incomplete
        # stream task can enter this path.
        return $Task.GetAwaiter().GetResult()
    }
    if ($Task.IsFaulted) {
        [void]$Task.Exception
        $CleanupDiagnostics.Add("stream-fault: ${StreamName}: $($Task.Exception.ToString())")
        return [string]::Empty
    }
    if ($Task.IsCanceled) {
        $CleanupDiagnostics.Add("stream-canceled: $StreamName")
        return [string]::Empty
    }

    $CleanupDiagnostics.Add("stream-drain-timeout: $StreamName did not complete before the cleanup deadline")
    return [string]::Empty
}

function Invoke-BoundedProcessLifecycle {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Threading.Tasks.Task[string]]$StandardOutputTask,

        [Parameter(Mandatory)]
        [System.Threading.Tasks.Task[string]]$StandardErrorTask,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [string]$RootProcessIdentity,

        [Parameter(Mandatory)]
        [string]$CommandIdentity,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [DateTime]$ProcessDeadlineUtc,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [TimeSpan]$RemainingCleanupBudget,

        [switch]$TerminateProcessTree
    )

    if ($PSBoundParameters.ContainsKey('RemainingCleanupBudget')) {
        $budgetDeadlineUtc = [DateTime]::UtcNow.Add($RemainingCleanupBudget)
        if ($budgetDeadlineUtc -lt $CleanupDeadlineUtc) {
            $CleanupDeadlineUtc = $budgetDeadlineUtc
        }
    }

    [void](New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force)
    $cleanupDiagnostics = [System.Collections.Generic.List[string]]::new()
    $diagnosticWriteDiagnostics = [System.Collections.Generic.List[string]]::new()
    $lineage = [System.Collections.Generic.List[object]]::new()
    $rootStartTimeUtcTicks = $null
    try {
        $rootStartTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
    }
    catch {
        $cleanupDiagnostics.Add("process-start-identity: $($_.Exception.Message)")
    }
    [void]$lineage.Add([pscustomobject]@{
            ProcessId = $RootProcessId
            ParentProcessId = $null
            Identity = $RootProcessIdentity
            StartTimeUtcTicks = $rootStartTimeUtcTicks
            ExitTimeUtcTicks = $null
            CommandIdentity = $CommandIdentity
            IsRoot = $true
        })

    Register-VerificationTaskObservation -Task $StandardOutputTask
    Register-VerificationTaskObservation -Task $StandardErrorTask

    $processTimedOut = $false
    $processExited = $false
    $exitCode = $null
    $cleanupStarted = $false
    $remainingOwnedProcessIds = [System.Collections.Generic.List[int]]::new()
    $lifecycleStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        try {
            [void](Update-VerificationProcessLineage -Lineage $lineage -RootProcess $Process)
        }
        catch {
            $cleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
        }

        while (-not (Test-VerificationProcessExited -Process $Process)) {
            try {
                [void](Update-VerificationProcessLineage -Lineage $lineage -RootProcess $Process)
            }
            catch {
                $cleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
                break
            }

            if ([DateTime]::UtcNow -ge $ProcessDeadlineUtc) {
                $processTimedOut = $true
                break
            }
            $remainingMilliseconds = [Math]::Max(
                1,
                [int][Math]::Min(100, ($ProcessDeadlineUtc - [DateTime]::UtcNow).TotalMilliseconds))
            try {
                [void]$Process.WaitForExit($remainingMilliseconds)
            }
            catch {
                $cleanupDiagnostics.Add("process-exit-wait: $($_.Exception.Message)")
                break
            }
        }

        $processExited = Test-VerificationProcessExited -Process $Process
        if ($processTimedOut -and $processExited) {
            # The process crossed the deadline between the bounded wait and the state check.
            $processTimedOut = $false
        }
        if ($processTimedOut -or $TerminateProcessTree) {
            $cleanupStarted = $true
            Stop-VerificationOwnedProcessTree `
                -RootProcess $Process `
                -Lineage $lineage `
                -RootProcessId $RootProcessId `
                -CleanupDeadlineUtc $CleanupDeadlineUtc `
                -CleanupDiagnostics $cleanupDiagnostics
        }
        elseif ($processExited) {
            # A successful root exit must not leave an attributed descendant holding a
            # redirected handle or running after the runner has completed its operation.
            try {
                $table = @(Update-VerificationProcessLineage -Lineage $lineage -RootProcess $Process)
                $ownedDescendants = @(Get-VerificationCurrentOwnedDescendants -Lineage $lineage -ProcessTable $table)
                if ($ownedDescendants.Count -gt 0) {
                    if (-not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted) {
                        foreach ($descendant in $ownedDescendants) {
                            $cleanupDiagnostics.Add(
                                "owned-descendant-cleanup: root PID $RootProcessId exited while descendant PID $($descendant.ProcessId) ($($descendant.CommandIdentity)) remained active")
                        }
                    }
                    $cleanupStarted = $true
                    Stop-VerificationOwnedProcessTree `
                        -RootProcess $Process `
                        -Lineage $lineage `
                        -RootProcessId $RootProcessId `
                        -CleanupDeadlineUtc $CleanupDeadlineUtc `
                        -CleanupDiagnostics $cleanupDiagnostics
                }
            }
            catch {
                $cleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
            }
        }

        while ((-not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted) -and
            [DateTime]::UtcNow -lt $CleanupDeadlineUtc) {
            $pendingTasks = @(
                @($StandardOutputTask, $StandardErrorTask) |
                    Where-Object { -not $_.IsCompleted })
            if ($pendingTasks.Count -eq 0) {
                break
            }
            $remainingMilliseconds = [Math]::Max(
                1,
                [int][Math]::Min(100, ($CleanupDeadlineUtc - [DateTime]::UtcNow).TotalMilliseconds))
            try {
                [void][System.Threading.Tasks.Task]::WaitAll(
                    [System.Threading.Tasks.Task[]]$pendingTasks,
                    $remainingMilliseconds)
            }
            catch [System.AggregateException] {
                # Faulted readers are recorded below; WaitAll remains bounded by the same
                # cleanup deadline and never synchronously retrieves an incomplete task.
            }
        }

        if (-not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted) {
            if (-not $cleanupStarted) {
                $cleanupStarted = $true
                Stop-VerificationOwnedProcessTree `
                    -RootProcess $Process `
                    -Lineage $lineage `
                    -RootProcessId $RootProcessId `
                    -CleanupDeadlineUtc $CleanupDeadlineUtc `
                    -CleanupDiagnostics $cleanupDiagnostics
            }

            # Closing the redirected readers releases this runner-owned handle.  The
            # continuation registered above observes a fault that arrives after the deadline.
            try { $Process.StandardOutput.Close() } catch { $cleanupDiagnostics.Add("stdout-reader-close: $($_.Exception.Message)") }
            try { $Process.StandardError.Close() } catch { $cleanupDiagnostics.Add("stderr-reader-close: $($_.Exception.Message)") }
        }

        $standardOutput = Get-VerificationCompletedTaskOutput `
            -Task $StandardOutputTask `
            -StreamName 'stdout' `
            -CleanupDiagnostics $cleanupDiagnostics
        $standardError = Get-VerificationCompletedTaskOutput `
            -Task $StandardErrorTask `
            -StreamName 'stderr' `
            -CleanupDiagnostics $cleanupDiagnostics

        try {
            if ($processExited -or (Test-VerificationProcessExited -Process $Process)) {
                $exitCode = $Process.ExitCode
                $processExited = $true
            }
        }
        catch {
            $cleanupDiagnostics.Add("process-exit-code: $($_.Exception.Message)")
        }

        try {
            $table = @(Update-VerificationProcessLineage -Lineage $lineage -RootProcess $Process)
            if (-not (Test-VerificationProcessExited -Process $Process)) {
                [void]$remainingOwnedProcessIds.Add($RootProcessId)
                $cleanupDiagnostics.Add("owned-process-residual: PID $RootProcessId remained active after the cleanup deadline")
            }
            foreach ($tracked in @(Get-VerificationCurrentOwnedDescendants -Lineage $lineage -ProcessTable $table)) {
                [void]$remainingOwnedProcessIds.Add($tracked.ProcessId)
                $cleanupDiagnostics.Add(
                    "owned-descendant-residual: PID $($tracked.ProcessId) ($($tracked.CommandIdentity)) remained active after the cleanup deadline")
            }
        }
        catch {
            $cleanupDiagnostics.Add("process-lineage-residual-check: $($_.Exception.Message)")
        }
    }
    finally {
        $lifecycleStopwatch.Stop()
        try {
            [System.IO.File]::WriteAllText(
                (Join-Path $DiagnosticsDirectory 'stdout.log'),
                [string]$standardOutput,
                [System.Text.UTF8Encoding]::new($false))
        }
        catch {
            $diagnosticWriteDiagnostics.Add("stdout.log: $($_.Exception.Message)")
        }
        try {
            [System.IO.File]::WriteAllText(
                (Join-Path $DiagnosticsDirectory 'stderr.log'),
                [string]$standardError,
                [System.Text.UTF8Encoding]::new($false))
        }
        catch {
            $diagnosticWriteDiagnostics.Add("stderr.log: $($_.Exception.Message)")
        }
        if ($cleanupDiagnostics.Count -gt 0 -or $diagnosticWriteDiagnostics.Count -gt 0) {
            try {
                $diagnostics = @($cleanupDiagnostics) + @($diagnosticWriteDiagnostics)
                [System.IO.File]::WriteAllLines(
                    (Join-Path $DiagnosticsDirectory 'process-lifecycle.log'),
                    $diagnostics,
                    [System.Text.UTF8Encoding]::new($false))
            }
            catch {
                $diagnosticWriteDiagnostics.Add("process-lifecycle.log: $($_.Exception.Message)")
            }
        }
        try {
            $Process.Dispose()
        }
        catch {
            $cleanupDiagnostics.Add("process-dispose: $($_.Exception.Message)")
        }
    }

    $primaryFailure = $null
    if ($processTimedOut) {
        $primaryFailure = 'timeout'
    }
    elseif ($null -ne $exitCode -and $exitCode -ne 0) {
        $primaryFailure = 'nonzero-exit'
    }

    $secondaryDiagnostics = @($cleanupDiagnostics) + @($diagnosticWriteDiagnostics)
    return [pscustomobject][ordered]@{
        RootProcessId = $RootProcessId
        RootProcessIdentity = $RootProcessIdentity
        CommandIdentity = $CommandIdentity
        ProcessTimedOut = $processTimedOut
        ProcessExited = $processExited
        ExitCode = $exitCode
        PrimaryFailureKind = $primaryFailure
        StandardOutput = [string]$standardOutput
        StandardError = [string]$standardError
        CleanupDiagnostics = @($cleanupDiagnostics)
        DiagnosticWriteDiagnostics = @($diagnosticWriteDiagnostics)
        SecondaryDiagnostics = $secondaryDiagnostics
        RemainingOwnedProcessIds = @($remainingOwnedProcessIds)
        ElapsedMilliseconds = $lifecycleStopwatch.ElapsedMilliseconds
    }
}

function Get-VerificationLifecycleFailureMessage {
    param(
        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [object]$Result,

        [int]$TimeoutSeconds = 0
    )

    if ($Result.PrimaryFailureKind -ceq 'timeout') {
        return "$Label exceeded the ${TimeoutSeconds}-second timeout."
    }
    if ($Result.PrimaryFailureKind -ceq 'nonzero-exit') {
        return "$Label failed with exit code $($Result.ExitCode)."
    }
    if (@($Result.SecondaryDiagnostics).Count -gt 0) {
        return "$Label cleanup failed: $(@($Result.SecondaryDiagnostics) -join '; ')"
    }
    return $null
}
