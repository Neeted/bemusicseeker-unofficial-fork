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

function Invoke-VerificationPrimitiveObserver {
    param(
        [object]$Observer,

        [Parameter(Mandatory)]
        [string]$Operation,

        [int]$RootProcessId,
        [string]$Context,

        [long]$UtcTicks
    )

    if ($null -eq $Observer) {
        return
    }

    $event = [pscustomobject]@{
        Operation = $Operation
        RootProcessId = $RootProcessId
        Context = $Context
        UtcTicks = if ($PSBoundParameters.ContainsKey('UtcTicks')) {
            $UtcTicks
        }
        else {
            [DateTime]::UtcNow.Ticks
        }
    }
    try {
        if ($Observer -is [scriptblock]) {
            & $Observer $event
            return
        }
        $observeMethod = $Observer.PSObject.Methods['Observe']
        if ($null -ne $observeMethod) {
            [void]$Observer.Observe($event)
            return
        }
        $observeProperty = $Observer.PSObject.Properties['Observe']
        if ($null -ne $observeProperty) {
            & $observeProperty.Value $event
        }
    }
    catch {
        # The observer is a probe-only seam.  It must not alter production cleanup or
        # replace a primary process failure when a diagnostic observer itself fails.
    }
}

if (-not ('VerificationProcessSnapshotNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

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

public static class VerificationProcessTaskObserver
{
    public sealed class LateObservation
    {
        public string StreamName { get; init; } = string.Empty;
        public int RootProcessId { get; init; }
        public long UtcTicks { get; init; }
        public string ExceptionText { get; init; } = string.Empty;
    }

    private static readonly ConcurrentQueue<LateObservation> LateObservations = new();

    public static void Observe(Task completedTask)
    {
        if (completedTask.IsFaulted)
        {
            _ = completedTask.Exception;
        }
    }

    public static Action<Task> CreateObserver(string streamName, int rootProcessId)
    {
        return completedTask =>
        {
            if (completedTask.IsFaulted)
            {
                LateObservations.Enqueue(new LateObservation
                {
                    StreamName = streamName ?? string.Empty,
                    RootProcessId = rootProcessId,
                    UtcTicks = DateTime.UtcNow.Ticks,
                    ExceptionText = completedTask.Exception?.ToString() ?? "<unknown task fault>"
                });
            }
            Observe(completedTask);
        };
    }

    public static object[] DrainLateObservations()
    {
        var observations = new List<object>();
        while (LateObservations.TryDequeue(out LateObservation observation))
        {
            observations.Add(observation);
        }
        return observations.ToArray();
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
        [System.Diagnostics.Process]$RootProcess,

        [DateTime]$CleanupDeadlineUtc,

        [object]$CleanupDiagnostics,

        [object]$PrimitiveObserver,

        [string]$ObserverContext
    )

    $RootProcessId = $RootProcess.Id
    if ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc') -and
        [DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
        if ($null -ne $CleanupDiagnostics) {
            $CleanupDiagnostics.Add(
                "process-lineage-observation: skipped after cleanup deadline before process-table scan; root PID $RootProcessId; ownership remains uncertain")
        }
        return @()
    }
    Invoke-VerificationPrimitiveObserver `
        -Observer $PrimitiveObserver `
        -Operation 'lineage-snapshot' `
        -RootProcessId $RootProcessId `
        -Context $ObserverContext
    if ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc') -and
        [DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
        if ($null -ne $CleanupDiagnostics) {
            $CleanupDiagnostics.Add(
                "process-lineage-observation: skipped after cleanup deadline before process-table scan; root PID $RootProcessId; ownership remains uncertain")
        }
        return @()
    }
    $table = @(Get-VerificationProcessTable)
    if ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc') -and
        [DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
        if ($null -ne $CleanupDiagnostics) {
            $CleanupDiagnostics.Add(
                "process-lineage-observation: process-table scan completed after cleanup deadline; root PID $RootProcessId; ownership remains uncertain")
        }
        return $table
    }
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
                        CreationTimeUtcTicks = $creationTimeUtcTicks
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

function Stop-VerificationOwnedDescendantHandle {
    param(
        [Parameter(Mandatory)]
        [object]$Tracked,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc

        ,

        [object]$PrimitiveObserver
    )

    $descendant = $null
    try {
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-descendant-stop: PID $($Tracked.ProcessId) handle open skipped after cleanup deadline; ownership remains uncertain")
            return $false
        }
        Invoke-VerificationPrimitiveObserver `
            -Observer $PrimitiveObserver `
            -Operation 'descendant-handle-open' `
            -RootProcessId $Tracked.ProcessId `
            -Context $Tracked.CommandIdentity
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-descendant-stop: PID $($Tracked.ProcessId) handle open skipped after cleanup deadline; ownership remains uncertain")
            return $false
        }
        $descendant = [System.Diagnostics.Process]::GetProcessById($Tracked.ProcessId)
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-descendant-stop: PID $($Tracked.ProcessId) creation identity query skipped after cleanup deadline; ownership remains uncertain")
            return $false
        }
        $handleCreationTimeUtcTicks = $descendant.StartTime.ToUniversalTime().Ticks
        if ($null -eq $Tracked.CreationTimeUtcTicks -or
            $handleCreationTimeUtcTicks -ne [long]$Tracked.CreationTimeUtcTicks) {
            $observedCreationTime = if ($null -eq $Tracked.CreationTimeUtcTicks) {
                '<unavailable>'
            }
            else {
                [string]$Tracked.CreationTimeUtcTicks
            }
            $CleanupDiagnostics.Add(
                "owned-descendant-stop: PID $($Tracked.ProcessId) handle creation identity mismatch; expected $observedCreationTime, observed $handleCreationTimeUtcTicks; not stopped")
            return $false
        }
        # A descendant is stopped only after both the historical snapshot and the
        # actual Process handle agree on creation identity.  PID reuse is not owned.
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-descendant-stop: PID $($Tracked.ProcessId) kill skipped after cleanup deadline; ownership remains uncertain")
            return $false
        }
        Invoke-VerificationPrimitiveObserver `
            -Observer $PrimitiveObserver `
            -Operation 'descendant-stop' `
            -RootProcessId $Tracked.ProcessId `
            -Context $Tracked.CommandIdentity
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-descendant-stop: PID $($Tracked.ProcessId) kill skipped after cleanup deadline; ownership remains uncertain")
            return $false
        }
        $descendant.Kill($false)
        return $true
    }
    catch [System.ArgumentException] {
        # The descendant exited between the identity check and GetProcessById.
        return $true
    }
    catch {
        $CleanupDiagnostics.Add(
            "owned-descendant-stop: PID $($Tracked.ProcessId) ($($Tracked.CommandIdentity)) could not be stopped: $($_.Exception.Message)")
        return $false
    }
    finally {
        if ($null -ne $descendant) {
            Dispose-VerificationProcessHandleBounded `
                -Process $descendant `
                -OperationName "owned-descendant PID $($Tracked.ProcessId) dispose" `
                -CleanupDeadlineUtc $CleanupDeadlineUtc `
                -CleanupDiagnostics $CleanupDiagnostics `
                -RootProcessId $Tracked.ProcessId `
                -PrimitiveObserver $PrimitiveObserver
        }
    }
}

function Stop-VerificationOwnedRootHandle {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$RootProcess,

        [Parameter(Mandatory)]
        [object]$RootTracked,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [object]$PrimitiveObserver
    )

    try {
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-process-stop: PID $RootProcessId root handle open skipped after cleanup deadline; ownership remains uncertain")
            return $false
        }
        $handleCreationTimeUtcTicks = $RootProcess.StartTime.ToUniversalTime().Ticks
        if ($null -eq $RootTracked.StartTimeUtcTicks -or
            $handleCreationTimeUtcTicks -ne [long]$RootTracked.StartTimeUtcTicks) {
            $observedCreationTime = if ($null -eq $RootTracked.StartTimeUtcTicks) {
                '<unavailable>'
            }
            else {
                [string]$RootTracked.StartTimeUtcTicks
            }
            $CleanupDiagnostics.Add(
                "owned-process-stop: PID $RootProcessId handle creation identity mismatch; expected $observedCreationTime, observed $handleCreationTimeUtcTicks; not stopped")
            return $false
        }
        # Validate the retained root handle immediately before stopping it.  A PID that
        # was reused after the historical snapshot is not an owned process.
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-process-stop: PID $RootProcessId kill skipped after cleanup deadline; ownership remains uncertain")
            return $false
        }
        Invoke-VerificationPrimitiveObserver `
            -Observer $PrimitiveObserver `
            -Operation 'root-stop' `
            -RootProcessId $RootProcessId `
            -Context 'second-pass'
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-process-stop: PID $RootProcessId kill skipped after cleanup deadline; ownership remains uncertain")
            return $false
        }
        $RootProcess.Kill($false)
        return $true
    }
    catch [System.ArgumentException] {
        return $true
    }
    catch {
        $CleanupDiagnostics.Add(
            "owned-process-stop: PID $RootProcessId ($($RootProcess.StartInfo.FileName)) could not be stopped: $($_.Exception.Message)")
        return $false
    }
}

function Request-VerificationOwnedRootStop {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$RootProcess,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [string]$RootProcessIdentity,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics,

        [DateTime]$CleanupDeadlineUtc,

        [object]$PrimitiveObserver,

        [string]$RootName
    )

    $attemptedUtc = [DateTime]::UtcNow
    if ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc') -and
        $attemptedUtc -ge $CleanupDeadlineUtc) {
        $CleanupDiagnostics.Add(
            "owned-process-stop: PID $RootProcessId root-only request skipped after cleanup deadline; elapsed 0ms")
        return $false
    }

    $expectedStartTimeUtcTicks = $null
    $identityParts = $RootProcessIdentity -split '\|', 2
    if ($identityParts.Count -eq 2 -and
        -not [string]::IsNullOrWhiteSpace($identityParts[0])) {
        try {
            $expectedStartTimeUtcTicks = [long]$identityParts[0]
        }
        catch {
            $CleanupDiagnostics.Add(
                "owned-process-stop: PID $RootProcessId launch identity '$RootProcessIdentity' was invalid; elapsed $([int]([DateTime]::UtcNow - $attemptedUtc).TotalMilliseconds)ms")
            return $false
        }
    }
    if ($null -eq $expectedStartTimeUtcTicks) {
        $CleanupDiagnostics.Add(
            "owned-process-stop: PID $RootProcessId launch creation identity was unavailable; not stopped; elapsed $([int]([DateTime]::UtcNow - $attemptedUtc).TotalMilliseconds)ms")
        return $false
    }

    try {
        # This is the Functional first pass.  It intentionally touches only the retained
        # root handle and the launch identity; lineage discovery, waits, descendants, and
        # disposal belong to the second pass after every root has received this request.
        $handleCreationTimeUtcTicks = $RootProcess.StartTime.ToUniversalTime().Ticks
        if ($handleCreationTimeUtcTicks -ne $expectedStartTimeUtcTicks) {
            $CleanupDiagnostics.Add(
                "owned-process-stop: PID $RootProcessId handle creation identity mismatch; expected $expectedStartTimeUtcTicks, observed $handleCreationTimeUtcTicks; not stopped; elapsed $([int]([DateTime]::UtcNow - $attemptedUtc).TotalMilliseconds)ms")
            return $false
        }
        if ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc') -and
            [DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-process-stop: PID $RootProcessId root-only request reached the cleanup deadline before Kill(false); elapsed $([int]([DateTime]::UtcNow - $attemptedUtc).TotalMilliseconds)ms")
            return $false
        }
        Invoke-VerificationPrimitiveObserver `
            -Observer $PrimitiveObserver `
            -Operation 'root-stop' `
            -RootProcessId $RootProcessId `
            -Context $RootName
        if ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc') -and
            [DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "owned-process-stop: PID $RootProcessId root-only request reached the cleanup deadline before Kill(false); elapsed $([int]([DateTime]::UtcNow - $attemptedUtc).TotalMilliseconds)ms")
            return $false
        }
        $RootProcess.Kill($false)
        return $true
    }
    catch [System.ArgumentException] {
        # The retained process exited between identity validation and Kill(false).
        return $true
    }
    catch [System.InvalidOperationException] {
        # Kill(false) is also allowed to race with an already-exited root.
        return $true
    }
    catch {
        $CleanupDiagnostics.Add(
            "owned-process-stop: PID $RootProcessId ($($RootProcess.StartInfo.FileName)) could not be stopped: $($_.Exception.Message); elapsed $([int]([DateTime]::UtcNow - $attemptedUtc).TotalMilliseconds)ms")
        return $false
    }
}

function Invoke-VerificationRootStopFanout {
    param(
        [Parameter(Mandatory)]
        [object[]]$Entries,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [Parameter(Mandatory)]
        [object]$CleanupFailures,

        [object]$PrimitiveObserver
    )

    foreach ($entry in $Entries) {
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupFailures.Add(
                "$($entry.Name): owned-process termination request skipped after the shared cleanup deadline")
            continue
        }

        $requestDiagnostics = [System.Collections.Generic.List[string]]::new()
        [void](Request-VerificationOwnedRootStop `
                -RootProcess $entry.Process `
                -RootProcessId $entry.ProcessId `
                -RootProcessIdentity $entry.RootProcessIdentity `
                -CleanupDeadlineUtc $CleanupDeadlineUtc `
                -CleanupDiagnostics $requestDiagnostics `
                -PrimitiveObserver $PrimitiveObserver `
                -RootName $entry.Name)
        foreach ($diagnostic in @($requestDiagnostics)) {
            $CleanupFailures.Add("$($entry.Name): $diagnostic")
        }
    }
}

function Add-VerificationKnownResiduals {
    param(
        [Parameter(Mandatory)]
        [object[]]$TrackedDescendants,

        [Parameter(Mandatory)]
        [bool]$RootKnownActive,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [object]$RemainingOwnedProcessIds,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics,

        [Parameter(Mandatory)]
        [string]$Reason
    )

    $reported = $false
    if ($RootKnownActive) {
        if ($null -ne $RemainingOwnedProcessIds -and
            -not $RemainingOwnedProcessIds.Contains($RootProcessId)) {
            [void]$RemainingOwnedProcessIds.Add($RootProcessId)
        }
        $CleanupDiagnostics.Add(
            "owned-process-residual: PID $RootProcessId remained active when cleanup deadline expired; elapsed $Reason; ownership remains uncertain")
        $reported = $true
    }
    foreach ($tracked in @($TrackedDescendants)) {
        if ($null -ne $RemainingOwnedProcessIds -and
            -not $RemainingOwnedProcessIds.Contains([int]$tracked.ProcessId)) {
            [void]$RemainingOwnedProcessIds.Add([int]$tracked.ProcessId)
        }
        $CleanupDiagnostics.Add(
            "owned-descendant-residual: PID $($tracked.ProcessId) ($($tracked.CommandIdentity)) remained in the last exact lineage snapshot when cleanup deadline expired; elapsed $Reason; ownership remains uncertain")
        $reported = $true
    }
    if (-not $reported) {
        $CleanupDiagnostics.Add(
            "process-lineage-residual-check: skipped after cleanup deadline; root PID $RootProcessId; elapsed $Reason; ownership remains uncertain")
    }
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
        [object]$CleanupDiagnostics,

        [switch]$RootStopAlreadyRequested,

        [object]$PrimitiveObserver,

        [object]$RemainingOwnedProcessIds,

        [string]$ObserverContext
    )

    $rootStopAttempted = $RootStopAlreadyRequested.IsPresent
    $rootKnownActive = $true
    $knownRemainingDescendants = @()
    $cleanupStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $recordDeadlineUncertainty = {
        Add-VerificationKnownResiduals `
            -TrackedDescendants $knownRemainingDescendants `
            -RootKnownActive $rootKnownActive `
            -RootProcessId $RootProcessId `
            -RemainingOwnedProcessIds $RemainingOwnedProcessIds `
            -CleanupDiagnostics $CleanupDiagnostics `
            -Reason "$($cleanupStopwatch.ElapsedMilliseconds)ms"
    }
    while ([DateTime]::UtcNow -lt $CleanupDeadlineUtc) {
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            & $recordDeadlineUncertainty
            return
        }
        try {
            $table = @(Update-VerificationProcessLineage `
                    -Lineage $Lineage `
                    -RootProcess $RootProcess `
                    -CleanupDeadlineUtc $CleanupDeadlineUtc `
                    -CleanupDiagnostics $CleanupDiagnostics `
                    -PrimitiveObserver $PrimitiveObserver `
                    -ObserverContext $ObserverContext)
        }
        catch {
            $CleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
            $table = @()
        }

        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            & $recordDeadlineUncertainty
            return
        }

        $knownRemainingDescendants = @(Get-VerificationCurrentOwnedDescendants -Lineage $Lineage -ProcessTable $table)

        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            & $recordDeadlineUncertainty
            return
        }
        $rootActiveBeforeStop = -not (Test-VerificationProcessExited -Process $RootProcess)
        if ($rootActiveBeforeStop -and -not $rootStopAttempted) {
            $rootStopAttempted = $true
            $rootTracked = @($Lineage | Where-Object { $_.IsRoot } | Select-Object -First 1)[0]
            if ($null -eq $rootTracked) {
                $CleanupDiagnostics.Add("owned-process-stop: PID $RootProcessId has no tracked root identity; not stopped")
            }
            else {
                [void](Stop-VerificationOwnedRootHandle `
                        -RootProcess $RootProcess `
                        -RootTracked $rootTracked `
                        -RootProcessId $RootProcessId `
                        -CleanupDiagnostics $CleanupDiagnostics `
                        -CleanupDeadlineUtc $CleanupDeadlineUtc `
                        -PrimitiveObserver $PrimitiveObserver)
            }
        }
        elseif (-not $rootActiveBeforeStop) {
            $rootKnownActive = $false
        }

        foreach ($tracked in @($knownRemainingDescendants)) {
            if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                & $recordDeadlineUncertainty
                return
            }
            [void](Stop-VerificationOwnedDescendantHandle `
                    -Tracked $tracked `
                    -CleanupDiagnostics $CleanupDiagnostics `
                    -CleanupDeadlineUtc $CleanupDeadlineUtc `
                    -PrimitiveObserver $PrimitiveObserver)
            if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                & $recordDeadlineUncertainty
                return
            }
        }

        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            & $recordDeadlineUncertainty
            return
        }
        $rootActive = -not (Test-VerificationProcessExited -Process $RootProcess)
        $rootKnownActive = $rootActive
        $remainingDescendants = @()
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            & $recordDeadlineUncertainty
            return
        }
        try {
            $table = @(Update-VerificationProcessLineage `
                    -Lineage $Lineage `
                    -RootProcess $RootProcess `
                    -CleanupDeadlineUtc $CleanupDeadlineUtc `
                    -CleanupDiagnostics $CleanupDiagnostics `
                    -PrimitiveObserver $PrimitiveObserver `
                    -ObserverContext $ObserverContext)
            if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                & $recordDeadlineUncertainty
                return
            }
            $remainingDescendants = @(Get-VerificationCurrentOwnedDescendants -Lineage $Lineage -ProcessTable $table)
            $knownRemainingDescendants = $remainingDescendants
        }
        catch {
            $CleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
        }
        if (-not $rootActive -and $remainingDescendants.Count -eq 0) {
            return
        }

        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            & $recordDeadlineUncertainty
            return
        }
        $remainingMilliseconds = [Math]::Max(
            1,
            [int][Math]::Min(50, ($CleanupDeadlineUtc - [DateTime]::UtcNow).TotalMilliseconds))
        try {
            if ($rootActive) {
                if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                    & $recordDeadlineUncertainty
                    return
                }
                Invoke-VerificationPrimitiveObserver `
                    -Observer $PrimitiveObserver `
                    -Operation 'root-wait' `
                    -RootProcessId $RootProcessId `
                    -Context $ObserverContext
                if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                    & $recordDeadlineUncertainty
                    return
                }
                [void]$RootProcess.WaitForExit($remainingMilliseconds)
            }
            else {
                $waitTarget = @($remainingDescendants | Select-Object -First 1)
                if ($waitTarget.Count -eq 0) {
                    break
                }
                if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                    & $recordDeadlineUncertainty
                    return
                }
                Invoke-VerificationPrimitiveObserver `
                    -Observer $PrimitiveObserver `
                    -Operation 'descendant-wait-open' `
                    -RootProcessId $waitTarget[0].ProcessId `
                    -Context $waitTarget[0].CommandIdentity
                if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                    & $recordDeadlineUncertainty
                    return
                }
                $descendant = [System.Diagnostics.Process]::GetProcessById($waitTarget[0].ProcessId)
                try {
                    if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                        & $recordDeadlineUncertainty
                        return
                    }
                    Invoke-VerificationPrimitiveObserver `
                        -Observer $PrimitiveObserver `
                        -Operation 'descendant-wait' `
                        -RootProcessId $waitTarget[0].ProcessId `
                        -Context $waitTarget[0].CommandIdentity
                    if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                        & $recordDeadlineUncertainty
                        return
                    }
                    [void]$descendant.WaitForExit($remainingMilliseconds)
                }
                finally {
                    Dispose-VerificationProcessHandleBounded `
                        -Process $descendant `
                        -OperationName "owned-descendant PID $($waitTarget[0].ProcessId) wait handle dispose" `
                        -CleanupDeadlineUtc $CleanupDeadlineUtc `
                        -CleanupDiagnostics $CleanupDiagnostics `
                        -RootProcessId $waitTarget[0].ProcessId `
                        -PrimitiveObserver $PrimitiveObserver
                }
            }
        }
        catch {
            # A process that exits while being polled is handled by the next identity check.
        }
    }

    & $recordDeadlineUncertainty
}

function Register-VerificationTaskObservation {
    param(
        [Parameter(Mandatory)]
        [System.Threading.Tasks.Task]$Task,

        [string]$StreamName,

        [int]$RootProcessId,

        [object]$LateDiagnostics,

        [object]$PrimitiveObserver
    )

    # Observe a late reader fault without synchronously waiting after the cleanup deadline.
    # The queue is drained by the lifecycle before it returns; no continuation performs
    # blocking I/O or process operations after the deadline.
    $observer = [VerificationProcessTaskObserver]::CreateObserver($StreamName, $RootProcessId)
    [void]$Task.ContinueWith($observer, [System.Threading.Tasks.TaskScheduler]::Default)
}

function Drain-VerificationLateTaskObservations {
    param(
        [object]$LateDiagnostics,

        [object]$PrimitiveObserver
    )

    foreach ($observation in @([VerificationProcessTaskObserver]::DrainLateObservations())) {
        $message = "late-stream-fault: $($observation.StreamName); root PID $($observation.RootProcessId); $($observation.ExceptionText)"
        if ($null -ne $LateDiagnostics) {
            $LateDiagnostics.Enqueue($message)
        }
        Invoke-VerificationPrimitiveObserver `
            -Observer $PrimitiveObserver `
            -Operation 'late-task-fault' `
            -RootProcessId $observation.RootProcessId `
            -Context $observation.StreamName `
            -UtcTicks $observation.UtcTicks
    }
}

function Enter-VerificationCleanup {
    param(
        [Parameter(Mandatory)]
        [object]$State,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$LifecycleStopwatch,

        [object]$PrimitiveObserver,

        [string]$ObserverContext
    )

    if ([bool]$State.Started) {
        return
    }

    $State.Started = $true
    $State.TransitionCount = [int]$State.TransitionCount + 1
    $transitionDeadlineUtc = [DateTime]::UtcNow.AddSeconds(5)
    if ($State.PhaseDeadlineUtc -lt $transitionDeadlineUtc) {
        $transitionDeadlineUtc = $State.PhaseDeadlineUtc
    }
    if ($State.CleanupDeadlineCapUtc -lt $transitionDeadlineUtc) {
        $transitionDeadlineUtc = $State.CleanupDeadlineCapUtc
    }
    $State.DeadlineUtc = $transitionDeadlineUtc
    Invoke-VerificationPrimitiveObserver `
        -Observer $PrimitiveObserver `
        -Operation 'cleanup-transition' `
        -RootProcessId $RootProcessId `
        -Context $ObserverContext
    if ([DateTime]::UtcNow -ge $State.DeadlineUtc) {
        $CleanupDiagnostics.Add(
            "cleanup-transition: PID $RootProcessId entered cleanup after its deadline; elapsed $($LifecycleStopwatch.ElapsedMilliseconds)ms; terminal operations are uncertain")
    }
}

function Wait-VerificationCleanupTask {
    param(
        [Parameter(Mandatory)]
        [System.Threading.Tasks.Task]$Task,

        [Parameter(Mandatory)]
        [string]$OperationName,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics
    )

    $waitStartedUtc = [DateTime]::UtcNow
    Register-VerificationTaskObservation -Task $Task
    while (-not $Task.IsCompleted -and [DateTime]::UtcNow -lt $CleanupDeadlineUtc) {
        $remainingMilliseconds = [Math]::Max(
            1,
            [int][Math]::Min(50, ($CleanupDeadlineUtc - [DateTime]::UtcNow).TotalMilliseconds))
        try {
            [void]$Task.Wait($remainingMilliseconds)
        }
        catch [System.AggregateException] {
            # Fault details are recorded after the bounded wait below.
        }
    }

    if (-not $Task.IsCompleted) {
        $CleanupDiagnostics.Add(
            "$OperationName did not complete before the cleanup deadline; elapsed $([int]([DateTime]::UtcNow - $waitStartedUtc).TotalMilliseconds)ms")
        return $false
    }
    if ($Task.IsFaulted) {
        [void]$Task.Exception
        $CleanupDiagnostics.Add(
            "$OperationName failed after $([int]([DateTime]::UtcNow - $waitStartedUtc).TotalMilliseconds)ms: $($Task.Exception.ToString())")
        return $false
    }
    if ($Task.IsCanceled) {
        $CleanupDiagnostics.Add(
            "$OperationName was canceled after $([int]([DateTime]::UtcNow - $waitStartedUtc).TotalMilliseconds)ms")
        return $false
    }
    return $true
}

function Dispose-VerificationProcessHandleBounded {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [string]$OperationName,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics,

        [int]$RootProcessId,

        [object]$PrimitiveObserver
    )

    $disposeStartedUtc = [DateTime]::UtcNow
    if ($disposeStartedUtc -ge $CleanupDeadlineUtc) {
        $CleanupDiagnostics.Add(
            "$OperationName skipped after cleanup deadline; elapsed $([int]([DateTime]::UtcNow - $disposeStartedUtc).TotalMilliseconds)ms")
        return $false
    }

    try {
        Invoke-VerificationPrimitiveObserver `
            -Observer $PrimitiveObserver `
            -Operation 'dispose' `
            -RootProcessId $RootProcessId `
            -Context $OperationName
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "$OperationName skipped after cleanup deadline; elapsed $([int]([DateTime]::UtcNow - $disposeStartedUtc).TotalMilliseconds)ms")
            return $false
        }
        $disposeAction = [Action]$Process.Dispose
        $disposeTask = [System.Threading.Tasks.Task]::Run($disposeAction)
        [void](Wait-VerificationCleanupTask `
                -Task $disposeTask `
                -OperationName $OperationName `
                -CleanupDeadlineUtc $CleanupDeadlineUtc `
                -CleanupDiagnostics $CleanupDiagnostics)
    }
    catch {
        $CleanupDiagnostics.Add("$OperationName failed: $($_.Exception.Message)")
    }
}

function Close-VerificationProcessReaderBounded {
    param(
        [Parameter(Mandatory)]
        [System.IO.StreamReader]$Reader,

        [Parameter(Mandatory)]
        [string]$OperationName,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics,

        [object]$PrimitiveObserver
    )

    $closeStartedUtc = [DateTime]::UtcNow
    if ($closeStartedUtc -ge $CleanupDeadlineUtc) {
        $CleanupDiagnostics.Add(
            "$OperationName skipped after cleanup deadline; root PID $RootProcessId; elapsed $([int]([DateTime]::UtcNow - $closeStartedUtc).TotalMilliseconds)ms; reader state is uncertain")
        return $false
    }

    try {
        Invoke-VerificationPrimitiveObserver `
            -Observer $PrimitiveObserver `
            -Operation 'reader-close' `
            -RootProcessId $RootProcessId `
            -Context $OperationName
        if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
            $CleanupDiagnostics.Add(
                "$OperationName skipped after cleanup deadline; root PID $RootProcessId; elapsed $([int]([DateTime]::UtcNow - $closeStartedUtc).TotalMilliseconds)ms; reader state is uncertain")
            return $false
        }
        $closeTask = [System.Threading.Tasks.Task]::Run([Action]$Reader.Close)
        return Wait-VerificationCleanupTask `
            -Task $closeTask `
            -OperationName "$OperationName (root PID $RootProcessId)" `
            -CleanupDeadlineUtc $CleanupDeadlineUtc `
            -CleanupDiagnostics $CleanupDiagnostics
    }
    catch {
        $CleanupDiagnostics.Add(
            "$OperationName failed for root PID ${RootProcessId} after $([int]([DateTime]::UtcNow - $closeStartedUtc).TotalMilliseconds)ms: $($_.Exception.Message)")
        return $false
    }
}

function Get-VerificationCompletedTaskOutput {
    param(
        [Parameter(Mandatory)]
        [System.Threading.Tasks.Task[string]]$Task,

        [Parameter(Mandatory)]
        [string]$StreamName,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [long]$ElapsedMilliseconds,

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

    $CleanupDiagnostics.Add(
        "stream-drain-timeout: $StreamName; root PID $RootProcessId; elapsed ${ElapsedMilliseconds}ms; did not complete before the cleanup deadline")
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

        [DateTime]$PhaseDeadlineUtc,

        [switch]$RootStopAlreadyRequested,

        [switch]$TerminateProcessTree,

        [object]$PrimitiveObserver,

        [string]$LifecycleName
    )

    $cleanupDiagnostics = [System.Collections.Generic.List[string]]::new()
    $diagnosticWriteDiagnostics = [System.Collections.Generic.List[string]]::new()
    $lineage = [System.Collections.Generic.List[object]]::new()
    $rootStartTimeUtcTicks = $null
    $rootIdentityParts = $RootProcessIdentity -split '\|', 2
    if ($rootIdentityParts.Count -eq 2 -and
        -not [string]::IsNullOrWhiteSpace($rootIdentityParts[0])) {
        try {
            $rootStartTimeUtcTicks = [long]$rootIdentityParts[0]
        }
        catch {
            $cleanupDiagnostics.Add("process-start-identity: invalid launch identity '$RootProcessIdentity'")
        }
    }
    else {
        $cleanupDiagnostics.Add("process-start-identity: launch identity '$RootProcessIdentity' was unavailable")
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

    $processTimedOut = $false
    $processExited = $false
    $exitCode = $null
    $standardOutput = [string]::Empty
    $standardError = [string]::Empty
    $remainingOwnedProcessIds = [System.Collections.Generic.List[int]]::new()
    $lateDiagnostics = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
    $lifecycleStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $phaseDeadline = if ($PSBoundParameters.ContainsKey('PhaseDeadlineUtc')) {
        $PhaseDeadlineUtc
    }
    else {
        $CleanupDeadlineUtc
    }
    $cleanupState = [pscustomobject]@{
        Started = $false
        TransitionCount = 0
        PhaseDeadlineUtc = $phaseDeadline
        CleanupDeadlineCapUtc = $CleanupDeadlineUtc
        DeadlineUtc = $CleanupDeadlineUtc
    }

    Register-VerificationTaskObservation `
        -Task $StandardOutputTask `
        -StreamName 'stdout' `
        -RootProcessId $RootProcessId `
        -LateDiagnostics $lateDiagnostics `
        -PrimitiveObserver $PrimitiveObserver
    Register-VerificationTaskObservation `
        -Task $StandardErrorTask `
        -StreamName 'stderr' `
        -RootProcessId $RootProcessId `
        -LateDiagnostics $lateDiagnostics `
        -PrimitiveObserver $PrimitiveObserver

    try {
        if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
            try {
                [void](Update-VerificationProcessLineage `
                        -Lineage $lineage `
                        -RootProcess $Process `
                        -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                        -CleanupDiagnostics $cleanupDiagnostics `
                        -PrimitiveObserver $PrimitiveObserver `
                        -ObserverContext $LifecycleName)
            }
            catch {
                $cleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
            }
        }
        else {
            $cleanupDiagnostics.Add(
                "process-lineage-observation: skipped after cleanup deadline; root PID $RootProcessId; elapsed $($lifecycleStopwatch.ElapsedMilliseconds)ms; ownership remains uncertain")
        }

        while ($true) {
            if ([DateTime]::UtcNow -ge $ProcessDeadlineUtc) {
                if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
                    try {
                        $processExited = Test-VerificationProcessExited -Process $Process
                    }
                    catch {
                        $processExited = $false
                    }
                }
                if (-not $processExited) {
                    $processTimedOut = $true
                }
                break
            }
            if ([DateTime]::UtcNow -ge $cleanupState.DeadlineUtc) {
                Enter-VerificationCleanup `
                    -State $cleanupState `
                    -CleanupDiagnostics $cleanupDiagnostics `
                    -RootProcessId $RootProcessId `
                    -LifecycleStopwatch $lifecycleStopwatch `
                    -PrimitiveObserver $PrimitiveObserver `
                    -ObserverContext $LifecycleName
                break
            }
            if (Test-VerificationProcessExited -Process $Process) {
                $processExited = $true
                break
            }

            try {
                [void](Update-VerificationProcessLineage `
                        -Lineage $lineage `
                        -RootProcess $Process `
                        -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                        -CleanupDiagnostics $cleanupDiagnostics `
                        -PrimitiveObserver $PrimitiveObserver `
                        -ObserverContext $LifecycleName)
            }
            catch {
                $cleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
                break
            }

            $remainingWaitDeadlineUtc = $ProcessDeadlineUtc
            if ($cleanupState.DeadlineUtc -lt $remainingWaitDeadlineUtc) {
                $remainingWaitDeadlineUtc = $cleanupState.DeadlineUtc
            }
            $remainingMilliseconds = [Math]::Max(
                1,
                [int][Math]::Min(100, ($remainingWaitDeadlineUtc - [DateTime]::UtcNow).TotalMilliseconds))
            if ($remainingMilliseconds -le 0) {
                break
            }
            if ([DateTime]::UtcNow -ge $cleanupState.DeadlineUtc) {
                Enter-VerificationCleanup `
                    -State $cleanupState `
                    -CleanupDiagnostics $cleanupDiagnostics `
                    -RootProcessId $RootProcessId `
                    -LifecycleStopwatch $lifecycleStopwatch `
                    -PrimitiveObserver $PrimitiveObserver `
                    -ObserverContext $LifecycleName
                break
            }
            try {
                [void]$Process.WaitForExit($remainingMilliseconds)
            }
            catch {
                $cleanupDiagnostics.Add("process-exit-wait: $($_.Exception.Message)")
                break
            }
        }

        if (-not $processExited -and [DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
            $processExited = Test-VerificationProcessExited -Process $Process
            if ($processTimedOut -and $processExited) {
                # The process crossed the process deadline between the bounded wait and the
                # state check while cleanup operations were still permitted.
                $processTimedOut = $false
            }
        }
        if ($processTimedOut -or $TerminateProcessTree) {
            Enter-VerificationCleanup `
                -State $cleanupState `
                -CleanupDiagnostics $cleanupDiagnostics `
                -RootProcessId $RootProcessId `
                -LifecycleStopwatch $lifecycleStopwatch `
                -PrimitiveObserver $PrimitiveObserver `
                -ObserverContext $LifecycleName
            if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
                $stopParameters = @{
                    RootProcess = $Process
                    Lineage = $lineage
                    RootProcessId = $RootProcessId
                    CleanupDeadlineUtc = $cleanupState.DeadlineUtc
                    CleanupDiagnostics = $cleanupDiagnostics
                    PrimitiveObserver = $PrimitiveObserver
                    RemainingOwnedProcessIds = $remainingOwnedProcessIds
                    ObserverContext = $LifecycleName
                }
                if ($RootStopAlreadyRequested) {
                    $stopParameters.RootStopAlreadyRequested = $true
                }
                Stop-VerificationOwnedProcessTree @stopParameters
            }
            else {
                $cleanupDiagnostics.Add(
                    "owned-process-cleanup: PID $RootProcessId skipped after cleanup deadline; elapsed $($lifecycleStopwatch.ElapsedMilliseconds)ms; ownership remains uncertain")
            }
        }
        elseif ($processExited) {
            # Normal success also crosses the cleanup boundary exactly once before any
            # persistence or disposal.  A descendant snapshot, when still possible, is
            # part of that same bounded transition.
            Enter-VerificationCleanup `
                -State $cleanupState `
                -CleanupDiagnostics $cleanupDiagnostics `
                -RootProcessId $RootProcessId `
                -LifecycleStopwatch $lifecycleStopwatch `
                -PrimitiveObserver $PrimitiveObserver `
                -ObserverContext $LifecycleName
            if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
                try {
                    $table = @(Update-VerificationProcessLineage `
                            -Lineage $lineage `
                            -RootProcess $Process `
                            -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                            -CleanupDiagnostics $cleanupDiagnostics `
                            -PrimitiveObserver $PrimitiveObserver `
                            -ObserverContext $LifecycleName)
                    if ([DateTime]::UtcNow -ge $cleanupState.DeadlineUtc) {
                        $cleanupDiagnostics.Add(
                            "process-lineage-observation: residual ownership confirmation skipped after cleanup deadline; root PID $RootProcessId remains uncertain")
                    }
                    else {
                        $ownedDescendants = @(Get-VerificationCurrentOwnedDescendants -Lineage $lineage -ProcessTable $table)
                        if ($ownedDescendants.Count -gt 0) {
                            if (-not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted) {
                                foreach ($descendant in $ownedDescendants) {
                                    $cleanupDiagnostics.Add(
                                        "owned-descendant-cleanup: root PID $RootProcessId exited while descendant PID $($descendant.ProcessId) ($($descendant.CommandIdentity)) remained active")
                                }
                            }
                            $stopParameters = @{
                                RootProcess = $Process
                                Lineage = $lineage
                                RootProcessId = $RootProcessId
                                CleanupDeadlineUtc = $cleanupState.DeadlineUtc
                                CleanupDiagnostics = $cleanupDiagnostics
                                RootStopAlreadyRequested = $true
                                PrimitiveObserver = $PrimitiveObserver
                                RemainingOwnedProcessIds = $remainingOwnedProcessIds
                                ObserverContext = $LifecycleName
                            }
                            Stop-VerificationOwnedProcessTree @stopParameters
                        }
                    }
                }
                catch {
                    $cleanupDiagnostics.Add("process-lineage-observation: $($_.Exception.Message)")
                }
            }
            else {
                $cleanupDiagnostics.Add(
                    "process-lineage-observation: residual ownership confirmation skipped after cleanup deadline; root PID $RootProcessId remains uncertain")
            }
        }

        Enter-VerificationCleanup `
            -State $cleanupState `
            -CleanupDiagnostics $cleanupDiagnostics `
            -RootProcessId $RootProcessId `
            -LifecycleStopwatch $lifecycleStopwatch `
            -PrimitiveObserver $PrimitiveObserver `
            -ObserverContext $LifecycleName

        $streamsPending = -not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted
        while ($streamsPending -and [DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
            $pendingTasks = @(
                @($StandardOutputTask, $StandardErrorTask) |
                    Where-Object { -not $_.IsCompleted })
            if ($pendingTasks.Count -eq 0) {
                break
            }
            $remainingMilliseconds = [Math]::Max(
                1,
                [int][Math]::Min(100, ($cleanupState.DeadlineUtc - [DateTime]::UtcNow).TotalMilliseconds))
            if ([DateTime]::UtcNow -ge $cleanupState.DeadlineUtc) {
                break
            }
            try {
                [void][System.Threading.Tasks.Task]::WaitAll(
                    [System.Threading.Tasks.Task[]]$pendingTasks,
                    $remainingMilliseconds)
            }
            catch [System.AggregateException] {
                # Fault details are recorded by the non-blocking result inspection below.
            }
            $streamsPending = -not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted
        }

        if (-not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted) {
            if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
                Close-VerificationProcessReaderBounded `
                    -Reader $Process.StandardOutput `
                    -OperationName 'stdout-reader-close' `
                    -RootProcessId $RootProcessId `
                    -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                    -CleanupDiagnostics $cleanupDiagnostics `
                    -PrimitiveObserver $PrimitiveObserver
                Close-VerificationProcessReaderBounded `
                    -Reader $Process.StandardError `
                    -OperationName 'stderr-reader-close' `
                    -RootProcessId $RootProcessId `
                    -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                    -CleanupDiagnostics $cleanupDiagnostics `
                    -PrimitiveObserver $PrimitiveObserver
            }
            else {
                $cleanupDiagnostics.Add(
                    "stream-reader-close: skipped after cleanup deadline; root PID $RootProcessId; elapsed $($lifecycleStopwatch.ElapsedMilliseconds)ms; reader state is uncertain")
            }
        }

        $standardOutput = Get-VerificationCompletedTaskOutput `
            -Task $StandardOutputTask `
            -StreamName 'stdout' `
            -RootProcessId $RootProcessId `
            -ElapsedMilliseconds $lifecycleStopwatch.ElapsedMilliseconds `
            -CleanupDiagnostics $cleanupDiagnostics
        $standardError = Get-VerificationCompletedTaskOutput `
            -Task $StandardErrorTask `
            -StreamName 'stderr' `
            -RootProcessId $RootProcessId `
            -ElapsedMilliseconds $lifecycleStopwatch.ElapsedMilliseconds `
            -CleanupDiagnostics $cleanupDiagnostics

        Drain-VerificationLateTaskObservations `
            -LateDiagnostics $lateDiagnostics `
            -PrimitiveObserver $PrimitiveObserver
        $lateDiagnostic = $null
        while ($lateDiagnostics.TryDequeue([ref]$lateDiagnostic)) {
            $cleanupDiagnostics.Add($lateDiagnostic)
            $lateDiagnostic = $null
        }

        if ($processExited) {
            try {
                $exitCode = $Process.ExitCode
            }
            catch {
                $cleanupDiagnostics.Add("process-exit-code: $($_.Exception.Message)")
            }
        }
        elseif ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
            try {
                if (Test-VerificationProcessExited -Process $Process) {
                    $exitCode = $Process.ExitCode
                    $processExited = $true
                }
            }
            catch {
                $cleanupDiagnostics.Add("process-exit-code: $($_.Exception.Message)")
            }
        }
        else {
            $cleanupDiagnostics.Add(
                "process-exit-code: skipped after cleanup deadline; root PID $RootProcessId; ownership remains uncertain")
        }

        if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
            try {
                $table = @(Update-VerificationProcessLineage `
                        -Lineage $lineage `
                        -RootProcess $Process `
                        -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                        -CleanupDiagnostics $cleanupDiagnostics `
                        -PrimitiveObserver $PrimitiveObserver `
                        -ObserverContext $LifecycleName)
                if ([DateTime]::UtcNow -ge $cleanupState.DeadlineUtc) {
                    $knownDescendants = @(Get-VerificationCurrentOwnedDescendants -Lineage $lineage -ProcessTable $table)
                    Add-VerificationKnownResiduals `
                        -TrackedDescendants $knownDescendants `
                        -RootKnownActive (-not $processExited) `
                        -RootProcessId $RootProcessId `
                        -RemainingOwnedProcessIds $remainingOwnedProcessIds `
                        -CleanupDiagnostics $cleanupDiagnostics `
                        -Reason "$($lifecycleStopwatch.ElapsedMilliseconds)ms"
                }
                else {
                    $rootStillActive = -not (Test-VerificationProcessExited -Process $Process)
                    if ($rootStillActive) {
                        [void]$remainingOwnedProcessIds.Add($RootProcessId)
                        $cleanupDiagnostics.Add("owned-process-residual: PID $RootProcessId remained active after the cleanup deadline")
                    }
                    if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
                        foreach ($tracked in @(Get-VerificationCurrentOwnedDescendants -Lineage $lineage -ProcessTable $table)) {
                            if (-not $remainingOwnedProcessIds.Contains($tracked.ProcessId)) {
                                [void]$remainingOwnedProcessIds.Add($tracked.ProcessId)
                            }
                            $cleanupDiagnostics.Add(
                                "owned-descendant-residual: PID $($tracked.ProcessId) ($($tracked.CommandIdentity)) remained active after the cleanup deadline")
                        }
                    }
                    else {
                        $knownDescendants = @(Get-VerificationCurrentOwnedDescendants -Lineage $lineage -ProcessTable $table)
                        Add-VerificationKnownResiduals `
                            -TrackedDescendants $knownDescendants `
                            -RootKnownActive $rootStillActive `
                            -RootProcessId $RootProcessId `
                            -RemainingOwnedProcessIds $remainingOwnedProcessIds `
                            -CleanupDiagnostics $cleanupDiagnostics `
                            -Reason "$($lifecycleStopwatch.ElapsedMilliseconds)ms"
                    }
                }
            }
            catch {
                $cleanupDiagnostics.Add("process-lineage-residual-check: $($_.Exception.Message)")
            }
        }
        else {
            $cleanupDiagnostics.Add(
                "process-lineage-residual-check: skipped after cleanup deadline; root PID $RootProcessId; elapsed $($lifecycleStopwatch.ElapsedMilliseconds)ms; ownership remains uncertain")
        }
    }
    finally {
        Enter-VerificationCleanup `
            -State $cleanupState `
            -CleanupDiagnostics $cleanupDiagnostics `
            -RootProcessId $RootProcessId `
            -LifecycleStopwatch $lifecycleStopwatch `
            -PrimitiveObserver $PrimitiveObserver `
            -ObserverContext $LifecycleName
        Drain-VerificationLateTaskObservations `
            -LateDiagnostics $lateDiagnostics `
            -PrimitiveObserver $PrimitiveObserver
        $lateDiagnostic = $null
        while ($lateDiagnostics.TryDequeue([ref]$lateDiagnostic)) {
            $cleanupDiagnostics.Add($lateDiagnostic)
            $lateDiagnostic = $null
        }
        $lifecycleStopwatch.Stop()
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        $writeTasks = [System.Collections.Generic.List[object]]::new()
        $disposeTask = $null
        if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
            foreach ($stream in @(
                    [pscustomobject]@{ Name = 'stdout.log'; Value = [string]$standardOutput },
                    [pscustomobject]@{ Name = 'stderr.log'; Value = [string]$standardError })) {
                if ([DateTime]::UtcNow -ge $cleanupState.DeadlineUtc) {
                    $diagnosticWriteDiagnostics.Add(
                        "$($stream.Name) persistence skipped after cleanup deadline; root PID $RootProcessId; ownership remains uncertain")
                    continue
                }
                try {
                    Invoke-VerificationPrimitiveObserver `
                        -Observer $PrimitiveObserver `
                        -Operation 'persistence' `
                        -RootProcessId $RootProcessId `
                        -Context $stream.Name
                    if ([DateTime]::UtcNow -ge $cleanupState.DeadlineUtc) {
                        $diagnosticWriteDiagnostics.Add(
                            "$($stream.Name) persistence skipped after cleanup deadline; root PID $RootProcessId; ownership remains uncertain")
                        continue
                    }
                    $writeTask = [System.IO.File]::WriteAllTextAsync(
                        (Join-Path $DiagnosticsDirectory $stream.Name),
                        $stream.Value,
                        $utf8)
                    [void]$writeTasks.Add([pscustomobject]@{
                            Task = $writeTask
                            Name = "$($stream.Name) write"
                        })
                }
                catch {
                    $diagnosticWriteDiagnostics.Add("$($stream.Name): $($_.Exception.Message)")
                }
            }

            if (($cleanupDiagnostics.Count -gt 0 -or $diagnosticWriteDiagnostics.Count -gt 0) -and
                [DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
                try {
                    $diagnostics = @($cleanupDiagnostics) + @($diagnosticWriteDiagnostics)
                    Invoke-VerificationPrimitiveObserver `
                        -Observer $PrimitiveObserver `
                        -Operation 'persistence' `
                        -RootProcessId $RootProcessId `
                        -Context 'process-lifecycle.log'
                    if ([DateTime]::UtcNow -ge $cleanupState.DeadlineUtc) {
                        throw 'process-lifecycle.log persistence skipped after cleanup deadline.'
                    }
                    $lifecycleWriteTask = [System.IO.File]::WriteAllLinesAsync(
                        (Join-Path $DiagnosticsDirectory 'process-lifecycle.log'),
                        [string[]]$diagnostics,
                        $utf8)
                    [void]$writeTasks.Add([pscustomobject]@{
                            Task = $lifecycleWriteTask
                            Name = 'process-lifecycle.log write'
                        })
                }
                catch {
                    $diagnosticWriteDiagnostics.Add("process-lifecycle.log: $($_.Exception.Message)")
                }
            }

            if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
                try {
                    Invoke-VerificationPrimitiveObserver `
                        -Observer $PrimitiveObserver `
                        -Operation 'dispose' `
                        -RootProcessId $RootProcessId `
                        -Context 'process handle'
                    if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
                        $disposeAction = [Action]$Process.Dispose
                        $disposeTask = [System.Threading.Tasks.Task]::Run($disposeAction)
                    }
                    else {
                        $cleanupDiagnostics.Add(
                            "process-dispose: skipped after cleanup deadline; root PID $RootProcessId; ownership remains uncertain")
                    }
                }
                catch {
                    $cleanupDiagnostics.Add(
                        "process-dispose: root PID $RootProcessId could not start before cleanup deadline: $($_.Exception.Message)")
                }
            }

            foreach ($write in $writeTasks) {
                [void](Wait-VerificationCleanupTask `
                        -Task $write.Task `
                        -OperationName "$($write.Name) (root PID $RootProcessId)" `
                        -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                        -CleanupDiagnostics $diagnosticWriteDiagnostics)
            }
            if ($null -ne $disposeTask) {
                [void](Wait-VerificationCleanupTask `
                        -Task $disposeTask `
                        -OperationName "process-dispose (root PID $RootProcessId)" `
                        -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                        -CleanupDiagnostics $cleanupDiagnostics)
            }
        }
        else {
            $cleanupDiagnostics.Add(
                "cleanup-terminal-operations: persistence and process disposal skipped after cleanup deadline; root PID $RootProcessId elapsed $($lifecycleStopwatch.ElapsedMilliseconds)ms")
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
        CleanupTransitionCount = $cleanupState.TransitionCount
        CleanupDeadlineUtc = $cleanupState.DeadlineUtc
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
