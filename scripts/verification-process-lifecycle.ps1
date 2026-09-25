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

function Resolve-VerificationDeadlinePair {
    param(
        [Parameter(Mandatory)]
        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds,

        [DateTime]$StartUtc = [DateTime]::MinValue,

        [DateTime]$ExecutionDeadlineUtc = [DateTime]::MinValue,

        [DateTime]$CleanupDeadlineUtc = [DateTime]::MinValue
    )

    $hasExecutionDeadline = $ExecutionDeadlineUtc -ne [DateTime]::MinValue
    $hasCleanupDeadline = $CleanupDeadlineUtc -ne [DateTime]::MinValue
    if ($hasExecutionDeadline -xor $hasCleanupDeadline) {
        throw 'Verification runner requires both ExecutionDeadlineUtc and CleanupDeadlineUtc.'
    }

    $startUtc = if ($StartUtc -eq [DateTime]::MinValue) {
        [DateTime]::UtcNow
    }
    elseif ($StartUtc.Kind -eq [DateTimeKind]::Unspecified) {
        [DateTime]::SpecifyKind($StartUtc, [DateTimeKind]::Utc)
    }
    else {
        $StartUtc.ToUniversalTime()
    }
    if (-not $hasExecutionDeadline) {
        $ExecutionDeadlineUtc = $startUtc.AddSeconds($TimeoutSeconds)
        $CleanupDeadlineUtc = $ExecutionDeadlineUtc.AddSeconds(10)
    }
    else {
        $ExecutionDeadlineUtc = $ExecutionDeadlineUtc.ToUniversalTime()
        $CleanupDeadlineUtc = $CleanupDeadlineUtc.ToUniversalTime()
        $startUtc = $ExecutionDeadlineUtc.AddSeconds(-$TimeoutSeconds)
    }

    $expectedCleanupDeadlineUtc = $ExecutionDeadlineUtc.AddSeconds(10)
    if ($CleanupDeadlineUtc.Ticks -ne $expectedCleanupDeadlineUtc.Ticks) {
        throw 'Verification cleanup deadline must be exactly ten seconds after the execution deadline.'
    }

    return [pscustomobject][ordered]@{
        StartUtc = $startUtc
        TimeoutSeconds = $TimeoutSeconds
        ExecutionDeadlineUtc = $ExecutionDeadlineUtc
        CleanupDeadlineUtc = $CleanupDeadlineUtc
        FailureCleanupDeadlineUtc = $CleanupDeadlineUtc
    }
}

function Get-VerificationRemainingMilliseconds {
    param(
        [Parameter(Mandatory)]
        [DateTime]$DeadlineUtc,

        [Parameter(Mandatory)]
        [string]$OperationName
    )

    $remainingMilliseconds = ($DeadlineUtc.ToUniversalTime() - [DateTime]::UtcNow).TotalMilliseconds
    if ($remainingMilliseconds -le 0) {
        throw "Verification execution deadline reached before $OperationName."
    }
    return [Math]::Max(1, [int][Math]::Min([double][int]::MaxValue, [Math]::Ceiling($remainingMilliseconds)))
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
using System.Threading;
using System.Threading.Tasks;

public sealed class VerificationProcessSnapshotEntry
{
    public int ProcessId { get; init; }
    public int ParentProcessId { get; init; }
    public long? CreationTimeUtcTicks { get; init; }
    public string ExecutableName { get; init; } = string.Empty;
}

public sealed class VerificationProcessSnapshotResult
{
    public VerificationProcessSnapshotEntry[] Entries { get; init; } = Array.Empty<VerificationProcessSnapshotEntry>();
    public bool IsComplete { get; init; }
    public bool DeadlineExpired { get; init; }
    public string FailureMessage { get; init; } = string.Empty;
}

public sealed class VerificationProcessCreationObservation
{
    public int ProcessId { get; init; }
    public long UtcTicks { get; init; }
}

public static class VerificationProcessSnapshotNative
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_NO_MORE_FILES = 18;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
    private static readonly ConcurrentQueue<VerificationProcessCreationObservation> CreationObservations = new();

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

    public static VerificationProcessSnapshotResult Capture(long cleanupDeadlineUtcTicks, bool observeCreationQueries)
    {
        var entries = new List<VerificationProcessSnapshotEntry>();
        if (IsDeadlineExpired(cleanupDeadlineUtcTicks))
        {
            return DeadlineResult(entries);
        }

        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue)
        {
            return FailureResult(
                entries,
                new Win32Exception(Marshal.GetLastWin32Error(), "Unable to capture the process table.").Message);
        }

        try
        {
            var nativeEntry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };
            if (IsDeadlineExpired(cleanupDeadlineUtcTicks))
            {
                return DeadlineResult(entries);
            }
            if (!Process32FirstW(snapshot, ref nativeEntry))
            {
                int error = Marshal.GetLastWin32Error();
                if (IsDeadlineExpired(cleanupDeadlineUtcTicks))
                {
                    return DeadlineResult(entries);
                }
                if (error == ERROR_NO_MORE_FILES)
                {
                    return CompleteResult(entries);
                }
                return FailureResult(entries, new Win32Exception(error, "Unable to read the process snapshot.").Message);
            }

            while (true)
            {
                if (IsDeadlineExpired(cleanupDeadlineUtcTicks))
                {
                    return DeadlineResult(entries);
                }
                bool creationQueryExpired;
                long? creationTimeUtcTicks = TryGetCreationTimeUtcTicks(
                    nativeEntry.th32ProcessID,
                    cleanupDeadlineUtcTicks,
                    observeCreationQueries,
                    out creationQueryExpired);
                if (creationQueryExpired)
                {
                    return DeadlineResult(entries);
                }
                entries.Add(new VerificationProcessSnapshotEntry
                {
                    ProcessId = unchecked((int)nativeEntry.th32ProcessID),
                    ParentProcessId = unchecked((int)nativeEntry.th32ParentProcessID),
                    CreationTimeUtcTicks = creationTimeUtcTicks,
                    ExecutableName = nativeEntry.szExeFile ?? string.Empty
                });
                nativeEntry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                if (IsDeadlineExpired(cleanupDeadlineUtcTicks))
                {
                    return DeadlineResult(entries);
                }
                bool hasNext = Process32NextW(snapshot, ref nativeEntry);
                if (IsDeadlineExpired(cleanupDeadlineUtcTicks))
                {
                    return DeadlineResult(entries);
                }
                if (!hasNext)
                {
                    int nextError = Marshal.GetLastWin32Error();
                    if (nextError == ERROR_NO_MORE_FILES)
                    {
                        return CompleteResult(entries);
                    }
                    return FailureResult(
                        entries,
                        new Win32Exception(nextError, "Unable to finish reading the process snapshot.").Message);
                }
            }
        }
        catch (Exception exception)
        {
            return FailureResult(entries, exception.Message);
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public static object[] DrainCreationObservations()
    {
        var observations = new List<object>();
        while (CreationObservations.TryDequeue(out VerificationProcessCreationObservation observation))
        {
            observations.Add(observation);
        }
        return observations.ToArray();
    }

    private static VerificationProcessSnapshotResult CompleteResult(List<VerificationProcessSnapshotEntry> entries)
    {
        return new VerificationProcessSnapshotResult
        {
            Entries = entries.ToArray(),
            IsComplete = true,
            DeadlineExpired = false
        };
    }

    private static VerificationProcessSnapshotResult DeadlineResult(List<VerificationProcessSnapshotEntry> entries)
    {
        return new VerificationProcessSnapshotResult
        {
            Entries = entries.ToArray(),
            IsComplete = false,
            DeadlineExpired = true,
            FailureMessage = "The process snapshot reached the cleanup deadline before it completed."
        };
    }

    private static VerificationProcessSnapshotResult FailureResult(
        List<VerificationProcessSnapshotEntry> entries,
        string failureMessage)
    {
        return new VerificationProcessSnapshotResult
        {
            Entries = entries.ToArray(),
            IsComplete = false,
            DeadlineExpired = false,
            FailureMessage = failureMessage
        };
    }

    private static bool IsDeadlineExpired(long cleanupDeadlineUtcTicks)
    {
        return DateTime.UtcNow.Ticks >= cleanupDeadlineUtcTicks;
    }

    private static long? TryGetCreationTimeUtcTicks(
        uint processId,
        long cleanupDeadlineUtcTicks,
        bool observeCreationQueries,
        out bool deadlineExpired)
    {
        deadlineExpired = IsDeadlineExpired(cleanupDeadlineUtcTicks);
        if (deadlineExpired)
        {
            return null;
        }
        if (observeCreationQueries)
        {
            CreationObservations.Enqueue(new VerificationProcessCreationObservation
            {
                ProcessId = unchecked((int)processId),
                UtcTicks = DateTime.UtcNow.Ticks
            });
        }
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        deadlineExpired = IsDeadlineExpired(cleanupDeadlineUtcTicks);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (deadlineExpired)
            {
                return null;
            }
            bool succeeded = GetProcessTimes(process, out FILETIME creation, out _, out _, out _);
            deadlineExpired = IsDeadlineExpired(cleanupDeadlineUtcTicks);
            return succeeded ? DateTime.FromFileTimeUtc(creation.ToLong()).Ticks : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}

public sealed class VerificationProcessTaskObservationScope
{
    public sealed class LateObservation
    {
        public string StreamName { get; init; } = string.Empty;
        public int RootProcessId { get; init; }
        public long UtcTicks { get; init; }
        public string ExceptionText { get; init; } = string.Empty;
    }

    private readonly object gate = new();
    private readonly ConcurrentQueue<LateObservation> ownerObservations = new();
    private readonly ConcurrentQueue<LateObservation> postSealObservations = new();
    private bool sealedScope;

    public Action<Task> CreateObserver(string streamName, int rootProcessId)
    {
        return completedTask =>
        {
            if (!completedTask.IsFaulted)
            {
                return;
            }

            // Reading Exception is the only work allowed after the scope is sealed.  The
            // callback never performs persistence, process, or reader operations.
            AggregateException exception = completedTask.Exception;
            var observation = new LateObservation
            {
                StreamName = streamName ?? string.Empty,
                RootProcessId = rootProcessId,
                UtcTicks = DateTime.UtcNow.Ticks,
                ExceptionText = exception?.ToString() ?? "<unknown task fault>"
            };
            lock (gate)
            {
                if (sealedScope)
                {
                    postSealObservations.Enqueue(observation);
                }
                else
                {
                    ownerObservations.Enqueue(observation);
                }
            }
        };
    }

    public void Seal()
    {
        lock (gate)
        {
            sealedScope = true;
        }
    }

    public object[] DrainOwnerObservations()
    {
        var observations = new List<object>();
        while (ownerObservations.TryDequeue(out LateObservation observation))
        {
            observations.Add(observation);
        }
        return observations.ToArray();
    }

    public object[] DrainPostSealObservations()
    {
        var observations = new List<object>();
        while (postSealObservations.TryDequeue(out LateObservation observation))
        {
            observations.Add(observation);
        }
        return observations.ToArray();
    }
}

public sealed class VerificationPostStartFaultGuard
{
    private readonly ManualResetEventSlim signal;
    private int consumed;

    public VerificationPostStartFaultGuard(ManualResetEventSlim signal)
    {
        this.signal = signal ?? throw new ArgumentNullException(nameof(signal));
    }

    public bool TryConsumeSignal()
    {
        return signal.IsSet && Interlocked.Exchange(ref consumed, 1) == 0;
    }
}
'@
}

function Get-VerificationProcessTable {
    param(
        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [object]$PrimitiveObserver,

        [string]$ObserverContext
    )

    try {
        [void][VerificationProcessSnapshotNative]::DrainCreationObservations()
        $nativeResult = [VerificationProcessSnapshotNative]::Capture(
            $CleanupDeadlineUtc.Ticks,
            $null -ne $PrimitiveObserver)
        $table = @(
            $nativeResult.Entries |
                ForEach-Object {
                    [pscustomobject]@{
                        ProcessId = $_.ProcessId
                        ParentProcessId = $_.ParentProcessId
                        CreationIdentity = if ($null -eq $_.CreationTimeUtcTicks) { '' } else { [string]$_.CreationTimeUtcTicks }
                        ExecutablePath = $_.ExecutableName
                        CommandLine = [string]::Empty
                    }
                })
        foreach ($observation in @([VerificationProcessSnapshotNative]::DrainCreationObservations())) {
            Invoke-VerificationPrimitiveObserver `
                -Observer $PrimitiveObserver `
                -Operation 'creation-query' `
                -RootProcessId $observation.ProcessId `
                -Context $ObserverContext `
                -UtcTicks $observation.UtcTicks
        }
        $deadlineExpired = [bool]$nativeResult.DeadlineExpired -or
            [DateTime]::UtcNow -ge $CleanupDeadlineUtc
        return [pscustomobject]@{
            Entries = $table
            IsComplete = [bool]$nativeResult.IsComplete -and -not $deadlineExpired
            DeadlineExpired = $deadlineExpired
            FailureMessage = [string]$nativeResult.FailureMessage
        }
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

        [Parameter(Mandatory)]
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
    $snapshot = Get-VerificationProcessTable `
        -CleanupDeadlineUtc $CleanupDeadlineUtc `
        -RootProcessId $RootProcessId `
        -PrimitiveObserver $PrimitiveObserver `
        -ObserverContext $ObserverContext
    if (-not $snapshot.IsComplete) {
        if ($snapshot.DeadlineExpired) {
            if ($null -ne $CleanupDiagnostics) {
                $CleanupDiagnostics.Add(
                    "process-lineage-observation: native process-table snapshot incomplete at cleanup deadline; root PID $RootProcessId; ownership remains uncertain")
            }
            return @()
        }
        throw "Native process-table snapshot was incomplete: $($snapshot.FailureMessage)"
    }
    $table = @($snapshot.Entries)
    if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
        if ($null -ne $CleanupDiagnostics) {
            $CleanupDiagnostics.Add(
                "process-lineage-observation: process-table scan completed after cleanup deadline; root PID $RootProcessId; ownership remains uncertain")
        }
        return @()
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

function Get-VerificationProcessExitObservation {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [DateTime]$ProcessDeadlineUtc
    )

    $observedAtUtc = [DateTime]::UtcNow
    $exited = Test-VerificationProcessExited -Process $Process
    $exitTimeUtc = $null
    if ($exited) {
        try {
            $exitTimeUtc = $Process.ExitTime.ToUniversalTime()
        }
        catch {
            # A retained process handle can report HasExited while ExitTime is not
            # available.  The observation time is the only safe boundary in that case.
        }
    }
    $exitedBeforeDeadline = $false
    if ($exited) {
        $exitedBeforeDeadline = if ($null -ne $exitTimeUtc) {
            $exitTimeUtc -le $ProcessDeadlineUtc
        }
        else {
            $observedAtUtc -le $ProcessDeadlineUtc
        }
    }
    return [pscustomobject]@{
        Exited = $exited
        ExitTimeUtc = $exitTimeUtc
        ExitedBeforeDeadline = $exitedBeforeDeadline
        ObservedAtUtc = $observedAtUtc
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
            [void](Dispose-VerificationProcessHandleBounded `
                -Process $descendant `
                -OperationName "owned-descendant PID $($Tracked.ProcessId) dispose" `
                -CleanupDeadlineUtc $CleanupDeadlineUtc `
                -CleanupDiagnostics $CleanupDiagnostics `
                -RootProcessId $Tracked.ProcessId `
                -PrimitiveObserver $PrimitiveObserver)
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

function Invoke-VerificationFunctionalCleanup {
    param(
        [Parameter(Mandatory)]
        [object[]]$Entries,

        [Parameter(Mandatory)]
        [DateTime]$ExecutionDeadlineUtc,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [switch]$StopRoots,

        [switch]$FailureCleanup,

        [hashtable]$RetainedExitTimesUtc,

        [object]$PrimitiveObserver
    )

    # The functional lane owns one execution/cleanup pair for the whole fan-out.  The
    # +10-second window is selected only by the invocation-level failure decision.
    $sharedOperationalCutoffUtc = if ($FailureCleanup) {
        $CleanupDeadlineUtc
    }
    else {
        $ExecutionDeadlineUtc
    }
    $fanoutFailures = [System.Collections.Generic.List[string]]::new()
    if ($StopRoots) {
        Invoke-VerificationRootStopFanout `
            -Entries $Entries `
            -CleanupDeadlineUtc $CleanupDeadlineUtc `
            -CleanupFailures $fanoutFailures `
            -PrimitiveObserver $PrimitiveObserver
    }

    $entryResults = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $Entries) {
        if ([DateTime]::UtcNow -ge $sharedOperationalCutoffUtc) {
            $diagnostic =
                "bounded process lifecycle skipped after shared cleanup deadline; root PID $($entry.ProcessId); ownership remains uncertain"
            $uncertaintyResult = [pscustomobject][ordered]@{
                RootProcessId = $entry.ProcessId
                RootProcessIdentity = $entry.RootProcessIdentity
                CommandIdentity = $entry.CommandIdentity
                ProcessTimedOut = $false
                ProcessExited = $false
                ExitCode = $null
                PrimaryFailureKind = $null
                StandardOutput = [string]::Empty
                StandardError = [string]::Empty
                CleanupDiagnostics = @($diagnostic)
                DiagnosticWriteDiagnostics = @()
                SecondaryDiagnostics = @($diagnostic)
                RemainingOwnedProcessIds = @()
                CleanupTransitionCount = 0
                CleanupDeadlineUtc = $CleanupDeadlineUtc
            }
            [void]$entryResults.Add([pscustomobject]@{
                    Entry = $entry
                    Result = $uncertaintyResult
                    Error = $null
                    SkippedAfterDeadline = $true
                })
            continue
        }

        try {
            $retainedExitTimeUtc = [DateTime]::MinValue
            if ($null -ne $RetainedExitTimesUtc -and
                $RetainedExitTimesUtc.ContainsKey($entry.Name)) {
                $retainedExitTimeUtc = ([DateTime]$RetainedExitTimesUtc[$entry.Name]).ToUniversalTime()
            }
            $lifecycleResult = Invoke-BoundedProcessLifecycle `
                -Process $entry.Process `
                -StandardOutputTask $entry.StandardOutputTask `
                -StandardErrorTask $entry.StandardErrorTask `
                -RootProcessId $entry.ProcessId `
                -RootProcessIdentity $entry.RootProcessIdentity `
                -CommandIdentity $entry.CommandIdentity `
                -DiagnosticsDirectory $entry.Directory `
                -ProcessDeadlineUtc $ExecutionDeadlineUtc `
                -CleanupDeadlineUtc $CleanupDeadlineUtc `
                -FailureCleanup:$FailureCleanup `
                -RetainedExitTimeUtc $retainedExitTimeUtc `
                -RootStopAlreadyRequested:$StopRoots `
                -TerminateProcessTree:$StopRoots `
                -PrimitiveObserver $PrimitiveObserver `
                -LifecycleName $entry.Name
            [void]$entryResults.Add([pscustomobject]@{
                    Entry = $entry
                    Result = $lifecycleResult
                    Error = $null
                    SkippedAfterDeadline = $false
                })
        }
        catch {
            [void]$entryResults.Add([pscustomobject]@{
                    Entry = $entry
                    Result = $null
                    Error = $_
                    SkippedAfterDeadline = $false
                })
        }
    }

    return [pscustomobject]@{
        FanoutFailures = @($fanoutFailures)
        EntryResults = @($entryResults)
    }
}

function Add-VerificationKnownResiduals {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
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
    # Establish root activity from the retained handle at entry, including the
    # RootStopAlreadyRequested path.  A root that already exited must not become a
    # synthetic residual merely because the descendant wait reaches the cutoff.
    $rootKnownActive = -not (Test-VerificationProcessExited -Process $RootProcess)
    # Preserve the last exact lineage snapshot supplied by the caller while a fresh
    # process-table query is still pending.  If the cutoff wins that query, those tracked
    # identities are the only safe residual candidates; each handle stop still rechecks
    # creation identity before acting.
    $knownRemainingDescendants = @($Lineage | Where-Object { -not $_.IsRoot })
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
                $rootStopSucceeded = Stop-VerificationOwnedRootHandle `
                        -RootProcess $RootProcess `
                        -RootTracked $rootTracked `
                        -RootProcessId $RootProcessId `
                        -CleanupDiagnostics $CleanupDiagnostics `
                        -CleanupDeadlineUtc $CleanupDeadlineUtc `
                        -PrimitiveObserver $PrimitiveObserver
                if ($rootStopSucceeded) {
                    # Kill was accepted before the cutoff; do not report a stale root PID
                    # merely because the post-kill identity poll could not start in time.
                    $rootKnownActive = $false
                }
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
            $descendantStopSucceeded = Stop-VerificationOwnedDescendantHandle `
                -Tracked $tracked `
                -CleanupDiagnostics $CleanupDiagnostics `
                -CleanupDeadlineUtc $CleanupDeadlineUtc `
                -PrimitiveObserver $PrimitiveObserver
            if ($descendantStopSucceeded) {
                $knownRemainingDescendants = @(
                    $knownRemainingDescendants |
                        Where-Object { $_.ProcessId -ne $tracked.ProcessId })
            }
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

        [Parameter(Mandatory)]
        [object]$ObservationScope,

        [object]$PrimitiveObserver
    )

    # Observe a late reader fault without synchronously waiting after the cleanup deadline.
    # The continuation is scoped to this lifecycle; after scope seal it only reads
    # Task.Exception and cannot publish a diagnostic to a later lifecycle.
    $observer = $ObservationScope.CreateObserver($StreamName, $RootProcessId)
    [void]$Task.ContinueWith($observer, [System.Threading.Tasks.TaskScheduler]::Default)
}

function Drain-VerificationLateTaskObservations {
    param(
        [Parameter(Mandatory)]
        [object]$ObservationScope,

        [object]$LateDiagnostics,

        [object]$PrimitiveObserver,

        [switch]$IncludePostSeal
    )

    $observations = if ($IncludePostSeal) {
        @($ObservationScope.DrainPostSealObservations())
    }
    else {
        @($ObservationScope.DrainOwnerObservations())
    }
    foreach ($observation in $observations) {
        $message = "late-stream-fault: $($observation.StreamName); root PID $($observation.RootProcessId); $($observation.ExceptionText)"
        if ($null -ne $LateDiagnostics -and -not $IncludePostSeal) {
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

        [string]$ObserverContext,

        [switch]$FailurePath
    )

    if ([bool]$State.Started) {
        return
    }

    $State.Started = $true
    $State.TransitionCount = [int]$State.TransitionCount + 1
    # A lifecycle has one execution deadline and one cleanup deadline.  Success keeps the
    # execution boundary; cleanup grace is selected only after a primary process failure.
    $State.DeadlineUtc = if ($FailurePath) {
        $State.CleanupDeadlineUtc
    }
    else {
        $State.ExecutionDeadlineUtc
    }
    $State.TerminalOperationsDeadlineUtc = $State.DeadlineUtc
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

function Add-VerificationExceptionSecondaryDiagnostic {
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception,

        [Parameter(Mandatory)]
        [string]$Diagnostic
    )

    $key = 'VerificationSecondaryDiagnostics'
    $existingValue = if ($Exception.Data.Contains($key)) {
        $Exception.Data[$key]
    }
    else {
        $null
    }
    $existing = if ($null -eq $existingValue) {
        @()
    }
    elseif ($existingValue -is [System.Collections.IEnumerable] -and
        $existingValue -isnot [string]) {
        @($existingValue)
    }
    else {
        @($existingValue)
    }
    $Exception.Data[$key] = @($existing) + @($Diagnostic)
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

        ,

        [int]$RootProcessId,

        [object]$PrimitiveObserver,

        [object]$ObservationScope
    )

    $waitStartedUtc = [DateTime]::UtcNow
    if ($null -ne $ObservationScope) {
        Register-VerificationTaskObservation `
            -Task $Task `
            -StreamName $OperationName `
            -RootProcessId $RootProcessId `
            -ObservationScope $ObservationScope
    }
    while (-not $Task.IsCompleted -and [DateTime]::UtcNow -lt $CleanupDeadlineUtc) {
        $remainingMilliseconds = [Math]::Max(
            1,
            [int][Math]::Min(50, ($CleanupDeadlineUtc - [DateTime]::UtcNow).TotalMilliseconds))
        try {
            if ([DateTime]::UtcNow -ge $CleanupDeadlineUtc) {
                break
            }
            $primitiveStartUtcTicks = [DateTime]::UtcNow.Ticks
            if ($primitiveStartUtcTicks -ge $CleanupDeadlineUtc.Ticks) {
                break
            }
            [void]$Task.Wait($remainingMilliseconds)
            Invoke-VerificationPrimitiveObserver `
                -Observer $PrimitiveObserver `
                -Operation 'cleanup-task-wait' `
                -RootProcessId $RootProcessId `
                -Context $OperationName `
                -UtcTicks $primitiveStartUtcTicks
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
                -CleanupDiagnostics $CleanupDiagnostics `
                -RootProcessId $RootProcessId `
                -PrimitiveObserver $PrimitiveObserver)
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
            -CleanupDiagnostics $CleanupDiagnostics `
            -RootProcessId $RootProcessId `
            -PrimitiveObserver $PrimitiveObserver
    }
    catch {
        $CleanupDiagnostics.Add(
            "$OperationName failed for root PID ${RootProcessId} after $([int]([DateTime]::UtcNow - $closeStartedUtc).TotalMilliseconds)ms: $($_.Exception.Message)")
        return $false
    }
}

function Update-VerificationStreamSnapshot {
    param(
        [Parameter(Mandatory)]
        [System.Threading.Tasks.Task[string]]$Task,

        [Parameter(Mandatory)]
        [string]$StreamName,

        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [object]$Snapshot,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics
    )

    if ([bool]$Snapshot.Terminal) {
        return
    }
    if ($Task.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
        # The completion state is checked before the synchronous retrieval.  No incomplete
        # stream task can enter this path.
        $Snapshot.Value = $Task.GetAwaiter().GetResult()
        $Snapshot.Terminal = $true
        return
    }
    if ($Task.IsFaulted) {
        [void]$Task.Exception
        $CleanupDiagnostics.Add("stream-fault: ${StreamName}: $($Task.Exception.ToString())")
        $Snapshot.Terminal = $true
        return
    }
    if ($Task.IsCanceled) {
        $CleanupDiagnostics.Add("stream-canceled: $StreamName")
        $Snapshot.Terminal = $true
        return
    }

    # An incomplete stream is deliberately left non-terminal while the bounded drain is
    # still running.  Finalize-VerificationStreamSnapshot records the durable timeout only
    # at the cutoff, independently of the other stream's completion state.
}

function Finalize-VerificationStreamSnapshot {
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
        [object]$Snapshot,

        [Parameter(Mandatory)]
        [object]$CleanupDiagnostics
    )

    Update-VerificationStreamSnapshot `
        -Task $Task `
        -StreamName $StreamName `
        -RootProcessId $RootProcessId `
        -Snapshot $Snapshot `
        -CleanupDiagnostics $CleanupDiagnostics
    if ([bool]$Snapshot.Terminal) {
        return
    }

    $CleanupDiagnostics.Add(
        "stream-drain-timeout: $StreamName; root PID $RootProcessId; elapsed ${ElapsedMilliseconds}ms; did not complete before the cleanup cutoff")
    $Snapshot.Terminal = $true
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

        [switch]$FailureCleanup,

        [DateTime]$RetainedExitTimeUtc = [DateTime]::MinValue,

        [switch]$RootStopAlreadyRequested,

        [switch]$TerminateProcessTree,

        [object]$PrimitiveObserver,

        [string]$LifecycleName,

        [switch]$PreserveExistingArtifactOnEmpty,

        [string[]]$InitialCleanupDiagnostics
    )

    $cleanupDiagnostics = [System.Collections.Generic.List[string]]::new()
    $diagnosticWriteDiagnostics = [System.Collections.Generic.List[string]]::new()
    foreach ($initialDiagnostic in @($InitialCleanupDiagnostics)) {
        if (-not [string]::IsNullOrWhiteSpace($initialDiagnostic)) {
            $cleanupDiagnostics.Add($initialDiagnostic)
        }
    }
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
    $stdoutSnapshot = [pscustomobject]@{ Value = [string]::Empty; Terminal = $false }
    $stderrSnapshot = [pscustomobject]@{ Value = [string]::Empty; Terminal = $false }
    $remainingOwnedProcessIds = [System.Collections.Generic.List[int]]::new()
    $lateDiagnostics = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
    $observationScope = [VerificationProcessTaskObservationScope]::new()
    $lifecycleStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $executionDeadlineUtc = $ProcessDeadlineUtc.ToUniversalTime()
    $cleanupDeadlineUtc = $CleanupDeadlineUtc.ToUniversalTime()
    $initialOperationalDeadlineUtc = if ($FailureCleanup) {
        $cleanupDeadlineUtc
    }
    else {
        $executionDeadlineUtc
    }
    $cleanupState = [pscustomobject]@{
        Started = $false
        TransitionCount = 0
        ExecutionDeadlineUtc = $executionDeadlineUtc
        CleanupDeadlineUtc = $cleanupDeadlineUtc
        DeadlineUtc = $initialOperationalDeadlineUtc
        TerminalOperationsDeadlineUtc = $initialOperationalDeadlineUtc
    }

    if ($RetainedExitTimeUtc -ne [DateTime]::MinValue) {
        $retainedExitTimeUtc = $RetainedExitTimeUtc.ToUniversalTime()
        $lineage[0].ExitTimeUtcTicks = $retainedExitTimeUtc.Ticks
        $processExited = $true
        $processTimedOut = $retainedExitTimeUtc -gt $cleanupState.ExecutionDeadlineUtc
    }

    Register-VerificationTaskObservation `
        -Task $StandardOutputTask `
        -StreamName 'stdout' `
        -RootProcessId $RootProcessId `
        -ObservationScope $observationScope `
        -PrimitiveObserver $PrimitiveObserver
    Register-VerificationTaskObservation `
        -Task $StandardErrorTask `
        -StreamName 'stderr' `
        -RootProcessId $RootProcessId `
        -ObservationScope $observationScope `
        -PrimitiveObserver $PrimitiveObserver

    try {
        if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
            try {
                $initialLineageTable = @(Update-VerificationProcessLineage `
                        -Lineage $lineage `
                        -RootProcess $Process `
                        -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                        -CleanupDiagnostics $cleanupDiagnostics `
                        -PrimitiveObserver $PrimitiveObserver `
                        -ObserverContext $LifecycleName)
                $initialRootTracked = @($lineage | Where-Object { $_.IsRoot } | Select-Object -First 1)[0]
                if ($null -ne $initialRootTracked -and $null -ne $initialRootTracked.ExitTimeUtcTicks) {
                    # Update-VerificationProcessLineage already observed the retained root
                    # handle exiting before the cutoff; reuse that observation without a
                    # second native query.
                    $processExited = $true
                    if ([long]$initialRootTracked.ExitTimeUtcTicks -gt $ProcessDeadlineUtc.Ticks) {
                        $processTimedOut = $true
                    }
                }
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
                        $exitObservation = Get-VerificationProcessExitObservation `
                            -Process $Process `
                            -ProcessDeadlineUtc $ProcessDeadlineUtc
                        $processExited = $exitObservation.Exited
                        if (-not $exitObservation.ExitedBeforeDeadline) {
                            $processTimedOut = $true
                        }
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
                $processTimedOut = $true
                Enter-VerificationCleanup `
                    -State $cleanupState `
                    -CleanupDiagnostics $cleanupDiagnostics `
                    -RootProcessId $RootProcessId `
                    -LifecycleStopwatch $lifecycleStopwatch `
                    -PrimitiveObserver $PrimitiveObserver `
                    -ObserverContext $LifecycleName `
                    -FailurePath
                break
            }
            $exitObservation = Get-VerificationProcessExitObservation `
                -Process $Process `
                -ProcessDeadlineUtc $ProcessDeadlineUtc
            if ($exitObservation.Exited) {
                $processExited = $true
                if (-not $exitObservation.ExitedBeforeDeadline) {
                    $processTimedOut = $true
                }
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
                $currentRootTracked = @($lineage | Where-Object { $_.IsRoot } | Select-Object -First 1)[0]
                if ($null -ne $currentRootTracked -and $null -ne $currentRootTracked.ExitTimeUtcTicks) {
                    $processExited = $true
                    if ([long]$currentRootTracked.ExitTimeUtcTicks -gt $ProcessDeadlineUtc.Ticks) {
                        $processTimedOut = $true
                    }
                    break
                }
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
                $processTimedOut = $true
                Enter-VerificationCleanup `
                    -State $cleanupState `
                    -CleanupDiagnostics $cleanupDiagnostics `
                    -RootProcessId $RootProcessId `
                    -LifecycleStopwatch $lifecycleStopwatch `
                    -PrimitiveObserver $PrimitiveObserver `
                    -ObserverContext $LifecycleName `
                    -FailurePath
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
            $exitObservation = Get-VerificationProcessExitObservation `
                -Process $Process `
                -ProcessDeadlineUtc $ProcessDeadlineUtc
            $processExited = $exitObservation.Exited
            if ($processExited -and -not $exitObservation.ExitedBeforeDeadline) {
                $processTimedOut = $true
            }
        }
        if ($processExited) {
            try {
                $exitCode = $Process.ExitCode
            }
            catch {
                $cleanupDiagnostics.Add("process-exit-code: $($_.Exception.Message)")
            }
        }

        if ($processExited -and -not $processTimedOut -and $exitCode -eq 0) {
            # A host that exited within the retained execution interval keeps normal
            # terminal work on that interval even when another host failed globally.
            $cleanupState.DeadlineUtc = $cleanupState.ExecutionDeadlineUtc
            $cleanupState.TerminalOperationsDeadlineUtc = $cleanupState.ExecutionDeadlineUtc
        }
        $processFailureDetected = $processTimedOut -or
            ($null -ne $exitCode -and $exitCode -ne 0)
        if ($processFailureDetected -or $TerminateProcessTree) {
            Enter-VerificationCleanup `
                -State $cleanupState `
                -CleanupDiagnostics $cleanupDiagnostics `
                -RootProcessId $RootProcessId `
                -LifecycleStopwatch $lifecycleStopwatch `
                -PrimitiveObserver $PrimitiveObserver `
                -ObserverContext $LifecycleName `
                -FailurePath:$processFailureDetected
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
                $primitiveStartUtcTicks = [DateTime]::UtcNow.Ticks
                if ($primitiveStartUtcTicks -ge $cleanupState.DeadlineUtc.Ticks) {
                    break
                }
                [void][System.Threading.Tasks.Task]::WaitAll(
                    [System.Threading.Tasks.Task[]]$pendingTasks,
                    $remainingMilliseconds)
                Invoke-VerificationPrimitiveObserver `
                    -Observer $PrimitiveObserver `
                    -Operation 'cleanup-task-wait' `
                    -RootProcessId $RootProcessId `
                    -Context 'redirected-stream-drain' `
                    -UtcTicks $primitiveStartUtcTicks
            }
            catch [System.AggregateException] {
                # Fault details are recorded by the non-blocking result inspection below.
            }
            Update-VerificationStreamSnapshot `
                -Task $StandardOutputTask `
                -StreamName 'stdout' `
                -RootProcessId $RootProcessId `
                -Snapshot $stdoutSnapshot `
                -CleanupDiagnostics $cleanupDiagnostics
            Update-VerificationStreamSnapshot `
                -Task $StandardErrorTask `
                -StreamName 'stderr' `
                -RootProcessId $RootProcessId `
                -Snapshot $stderrSnapshot `
                -CleanupDiagnostics $cleanupDiagnostics
            $streamsPending = -not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted
        }

        if (-not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted) {
            if ([DateTime]::UtcNow -lt $cleanupState.DeadlineUtc) {
                if (-not $StandardOutputTask.IsCompleted) {
                    Close-VerificationProcessReaderBounded `
                        -Reader $Process.StandardOutput `
                        -OperationName 'stdout-reader-close' `
                        -RootProcessId $RootProcessId `
                        -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                        -CleanupDiagnostics $cleanupDiagnostics `
                        -PrimitiveObserver $PrimitiveObserver
                }
                if (-not $StandardErrorTask.IsCompleted) {
                    Close-VerificationProcessReaderBounded `
                        -Reader $Process.StandardError `
                        -OperationName 'stderr-reader-close' `
                        -RootProcessId $RootProcessId `
                        -CleanupDeadlineUtc $cleanupState.DeadlineUtc `
                        -CleanupDiagnostics $cleanupDiagnostics `
                        -PrimitiveObserver $PrimitiveObserver
                }
            }
            else {
                $cleanupDiagnostics.Add(
                    "stream-reader-close: skipped after cleanup deadline; root PID $RootProcessId; elapsed $($lifecycleStopwatch.ElapsedMilliseconds)ms; reader state is uncertain")
            }
        }

        Finalize-VerificationStreamSnapshot `
            -Task $StandardOutputTask `
            -StreamName 'stdout' `
            -RootProcessId $RootProcessId `
            -ElapsedMilliseconds $lifecycleStopwatch.ElapsedMilliseconds `
            -Snapshot $stdoutSnapshot `
            -CleanupDiagnostics $cleanupDiagnostics
        Finalize-VerificationStreamSnapshot `
            -Task $StandardErrorTask `
            -StreamName 'stderr' `
            -RootProcessId $RootProcessId `
            -ElapsedMilliseconds $lifecycleStopwatch.ElapsedMilliseconds `
            -Snapshot $stderrSnapshot `
            -CleanupDiagnostics $cleanupDiagnostics
        $standardOutput = [string]$stdoutSnapshot.Value
        $standardError = [string]$stderrSnapshot.Value

        Drain-VerificationLateTaskObservations `
            -ObservationScope $observationScope `
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
                $exitObservation = Get-VerificationProcessExitObservation `
                    -Process $Process `
                    -ProcessDeadlineUtc $ProcessDeadlineUtc
                if ($exitObservation.Exited) {
                    $exitCode = $Process.ExitCode
                    $processExited = $true
                    if (-not $exitObservation.ExitedBeforeDeadline) {
                        $processTimedOut = $true
                    }
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

        if ([DateTime]::UtcNow -ge $cleanupState.DeadlineUtc -and
            (-not $StandardOutputTask.IsCompleted -or -not $StandardErrorTask.IsCompleted)) {
            $cleanupDiagnostics.Add(
                "process-lineage-residual-check: skipped after cleanup deadline; root PID $RootProcessId; ownership remains uncertain")
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
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        $terminalOperationsDeadlineUtc = $cleanupState.TerminalOperationsDeadlineUtc
        $terminalWriteTasks = [System.Collections.Generic.List[object]]::new()
        $disposeTask = $null
        if ([DateTime]::UtcNow -lt $terminalOperationsDeadlineUtc) {
            foreach ($stream in @(
                    [pscustomobject]@{ Name = 'stdout.log'; Value = [string]$standardOutput },
                    [pscustomobject]@{ Name = 'stderr.log'; Value = [string]$standardError })) {
                $artifactPath = Join-Path $DiagnosticsDirectory $stream.Name
                $temporaryPath = "$artifactPath.$([guid]::NewGuid().ToString('N')).tmp"
                if ($PreserveExistingArtifactOnEmpty -and
                    [string]::IsNullOrEmpty($stream.Value) -and
                    [System.IO.File]::Exists($artifactPath)) {
                    $diagnosticWriteDiagnostics.Add(
                        "$($stream.Name) empty output was not persisted after post-start exception; existing artifact was preserved")
                    continue
                }
                if ([DateTime]::UtcNow -ge $terminalOperationsDeadlineUtc) {
                    $diagnosticWriteDiagnostics.Add(
                        "$($stream.Name) persistence skipped after terminal-operation cutoff; root PID $RootProcessId; existing artifact was preserved")
                    continue
                }
                try {
                    Invoke-VerificationPrimitiveObserver `
                        -Observer $PrimitiveObserver `
                        -Operation 'persistence' `
                        -RootProcessId $RootProcessId `
                        -Context $stream.Name
                    if ([DateTime]::UtcNow -ge $terminalOperationsDeadlineUtc) {
                        $diagnosticWriteDiagnostics.Add(
                            "$($stream.Name) persistence skipped after terminal-operation cutoff; root PID $RootProcessId; existing artifact was preserved")
                        continue
                    }
                    $writeTask = [System.IO.File]::WriteAllTextAsync($temporaryPath, $stream.Value, $utf8)
                    [void]$terminalWriteTasks.Add([pscustomobject]@{
                            Task = $writeTask
                            Name = "$($stream.Name) write"
                            TemporaryPath = $temporaryPath
                            DestinationPath = $artifactPath
                        })
                }
                catch {
                    $diagnosticWriteDiagnostics.Add(
                        "$($stream.Name): $($_.Exception.Message); existing artifact was preserved")
                }
            }

            foreach ($write in $terminalWriteTasks) {
                $completed = Wait-VerificationCleanupTask `
                    -Task $write.Task `
                    -OperationName "$($write.Name) (root PID $RootProcessId)" `
                    -CleanupDeadlineUtc $terminalOperationsDeadlineUtc `
                    -CleanupDiagnostics $diagnosticWriteDiagnostics `
                    -RootProcessId $RootProcessId `
                    -PrimitiveObserver $PrimitiveObserver `
                    -ObservationScope $observationScope
                if ($completed -and [DateTime]::UtcNow -lt $terminalOperationsDeadlineUtc) {
                    try {
                        [System.IO.File]::Move($write.TemporaryPath, $write.DestinationPath, $true)
                    }
                    catch {
                        $diagnosticWriteDiagnostics.Add(
                            "$($write.Name) artifact replace failed: $($_.Exception.Message); existing artifact was preserved")
                    }
                }
                elseif (-not $completed) {
                    $diagnosticWriteDiagnostics.Add(
                        "$($write.Name) artifact replace skipped after terminal-operation cutoff; existing artifact was preserved")
                }
            }

            if ([DateTime]::UtcNow -lt $terminalOperationsDeadlineUtc) {
                try {
                    Invoke-VerificationPrimitiveObserver `
                        -Observer $PrimitiveObserver `
                        -Operation 'dispose' `
                        -RootProcessId $RootProcessId `
                        -Context 'process handle'
                    if ([DateTime]::UtcNow -lt $terminalOperationsDeadlineUtc) {
                        $disposeAction = [Action]$Process.Dispose
                        $disposeTask = [System.Threading.Tasks.Task]::Run($disposeAction)
                    }
                    else {
                        $cleanupDiagnostics.Add(
                            "process-dispose: skipped after terminal-operation cutoff; root PID $RootProcessId; ownership remains uncertain")
                    }
                }
                catch {
                    $cleanupDiagnostics.Add(
                        "process-dispose: root PID $RootProcessId could not start before cleanup deadline: $($_.Exception.Message)")
                }
            }
            if ($null -ne $disposeTask) {
                [void](Wait-VerificationCleanupTask `
                        -Task $disposeTask `
                        -OperationName "process-dispose (root PID $RootProcessId)" `
                        -CleanupDeadlineUtc $terminalOperationsDeadlineUtc `
                        -CleanupDiagnostics $cleanupDiagnostics `
                        -RootProcessId $RootProcessId `
                        -PrimitiveObserver $PrimitiveObserver `
                        -ObservationScope $observationScope)
            }
        }
        else {
            $cleanupDiagnostics.Add(
                "cleanup-terminal-operations: persistence and process disposal skipped after terminal-operation cutoff; root PID $RootProcessId elapsed $($lifecycleStopwatch.ElapsedMilliseconds)ms")
        }

        # No task or process operation is allowed after this point.  Seal the lifecycle
        # scope, drain owner diagnostics once, then perform exactly one final diagnostic
        # flush.  Faults observed after seal stay in the scope-local post-seal queue.
        $observationScope.Seal()
        Drain-VerificationLateTaskObservations `
            -ObservationScope $observationScope `
            -LateDiagnostics $lateDiagnostics `
            -PrimitiveObserver $PrimitiveObserver
        $lateDiagnostic = $null
        while ($lateDiagnostics.TryDequeue([ref]$lateDiagnostic)) {
            $cleanupDiagnostics.Add($lateDiagnostic)
            $lateDiagnostic = $null
        }

        if ($cleanupDiagnostics.Count -gt 0 -or $diagnosticWriteDiagnostics.Count -gt 0) {
            $diagnostics = @($cleanupDiagnostics) + @($diagnosticWriteDiagnostics)
            if ([DateTime]::UtcNow -lt $terminalOperationsDeadlineUtc) {
                $diagnosticPath = Join-Path $DiagnosticsDirectory 'process-lifecycle.log'
                $diagnosticTemporaryPath = "$diagnosticPath.$([guid]::NewGuid().ToString('N')).tmp"
                try {
                    Invoke-VerificationPrimitiveObserver `
                        -Observer $PrimitiveObserver `
                        -Operation 'persistence' `
                        -RootProcessId $RootProcessId `
                        -Context 'process-lifecycle.log'
                    if ([DateTime]::UtcNow -ge $terminalOperationsDeadlineUtc) {
                        throw 'process-lifecycle.log final diagnostic flush skipped after cleanup deadline.'
                    }
                    $diagnosticTask = [System.IO.File]::WriteAllLinesAsync(
                        $diagnosticTemporaryPath,
                        [string[]]$diagnostics,
                        $utf8)
                    $diagnosticWriteSucceeded = Wait-VerificationCleanupTask `
                        -Task $diagnosticTask `
                        -OperationName "process-lifecycle.log final diagnostic flush (root PID $RootProcessId)" `
                        -CleanupDeadlineUtc $terminalOperationsDeadlineUtc `
                        -CleanupDiagnostics $diagnosticWriteDiagnostics `
                        -RootProcessId $RootProcessId `
                        -PrimitiveObserver $PrimitiveObserver
                    if ($diagnosticWriteSucceeded -and [DateTime]::UtcNow -lt $terminalOperationsDeadlineUtc) {
                        [System.IO.File]::Move($diagnosticTemporaryPath, $diagnosticPath, $true)
                    }
                    elseif (-not $diagnosticWriteSucceeded) {
                        $diagnosticWriteDiagnostics.Add(
                            'terminal-diagnostic-flush: final write failed; existing artifact was preserved')
                    }
                    else {
                        $diagnosticWriteDiagnostics.Add(
                            'terminal-diagnostic-flush: final replace skipped after cleanup deadline; existing artifact was preserved')
                    }
                }
                catch {
                    $diagnosticWriteDiagnostics.Add(
                        "terminal-diagnostic-flush: $($_.Exception.Message); existing artifact was preserved")
                }
            }
            else {
                $diagnosticWriteDiagnostics.Add(
                    'terminal-diagnostic-flush: skipped after cleanup deadline; existing artifact was preserved')
            }
        }
        $lifecycleStopwatch.Stop()
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
        CleanupDeadlineUtc = $cleanupState.CleanupDeadlineUtc
        CleanupCutoffUtc = $cleanupState.DeadlineUtc
        TerminalOperationsDeadlineUtc = $cleanupState.TerminalOperationsDeadlineUtc
        ObservationScope = $observationScope
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

function Assert-VerificationProcessResult {
    param(
        [Parameter(Mandatory)]
        [object]$Result,

        [Parameter(Mandatory)]
        [string]$Label,

        [switch]$RequireSuccess,

        [string]$ExpectedPrimaryFailureKind,

        [int]$TimeoutSeconds = 0
    )

    if ($RequireSuccess) {
        if ($null -ne $Result.PrimaryFailureKind) {
            $failureMessage = Get-VerificationLifecycleFailureMessage `
                -Label $Label `
                -Result $Result `
                -TimeoutSeconds $TimeoutSeconds
            if ([string]::IsNullOrWhiteSpace($failureMessage)) {
                $failureMessage = "$Label produced primary failure kind '$($Result.PrimaryFailureKind)'."
            }
            throw $failureMessage
        }
    }
    elseif ($Result.PrimaryFailureKind -cne $ExpectedPrimaryFailureKind) {
        $actualPrimaryFailureKind = if ($null -eq $Result.PrimaryFailureKind) {
            '<none>'
        }
        else {
            [string]$Result.PrimaryFailureKind
        }
        throw "$Label produced unexpected primary failure kind '$actualPrimaryFailureKind'; expected '$ExpectedPrimaryFailureKind'."
    }

    if (@($Result.SecondaryDiagnostics).Count -gt 0) {
        throw "$Label reported lifecycle diagnostics: $(@($Result.SecondaryDiagnostics) -join '; ')"
    }

    return $Result
}

function New-VerificationOwnedProcessRecord {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [string]$CommandIdentity,

        [object]$StandardOutputTask,

        [object]$StandardErrorTask,

        [string]$DiagnosticsDirectory
    )

    $identity = Get-VerificationProcessIdentity -Process $Process -CommandIdentity $CommandIdentity
    if ($null -eq $identity.StartTimeUtcTicks) {
        throw "Process start identity was unavailable: $CommandIdentity"
    }
    $stdoutTask = if ($null -eq $StandardOutputTask) {
        [System.Threading.Tasks.Task[string]]::FromResult([string]::Empty)
    }
    else {
        [System.Threading.Tasks.Task[string]]$StandardOutputTask
    }
    $stderrTask = if ($null -eq $StandardErrorTask) {
        [System.Threading.Tasks.Task[string]]::FromResult([string]::Empty)
    }
    else {
        [System.Threading.Tasks.Task[string]]$StandardErrorTask
    }
    return [pscustomobject]@{
        Process = $Process
        ProcessId = $identity.ProcessId
        RootProcessIdentity = "$($identity.StartTimeUtcTicks)|$($identity.ProcessId)"
        CommandIdentity = $CommandIdentity
        StandardOutputTask = $stdoutTask
        StandardErrorTask = $stderrTask
        StandardOutput = [string]::Empty
        StandardError = [string]::Empty
        DiagnosticsDirectory = $DiagnosticsDirectory
        LifecycleCompleted = $false
        LifecycleResult = $null
    }
}

# dotnet (.NET 10) and pwsh emit UTF-8. Select their pipe decoder before Start;
# writing the resulting strings as UTF-8 later cannot undo an ANSI misdecode.
# Other native programs retain their own default encoding contract.
function Set-VerificationRedirectedProcessEncoding {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.ProcessStartInfo]$StartInfo
    )

    $name = [System.IO.Path]::GetFileNameWithoutExtension($StartInfo.FileName)
    if ($name -ieq 'dotnet' -or $name -ieq 'pwsh') {
        $StartInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $StartInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
        if ($name -ieq 'dotnet') {
            # Child-local only: do not change the runner's console, culture or environment.
            $StartInfo.Environment['DOTNET_CLI_FORCE_UTF8_ENCODING'] = '1'
        }
    }
}

function Start-VerificationRedirectedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$FileName,

        [string[]]$Arguments = @(),

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [hashtable]$Environment,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy,

        [string]$DiagnosticsDirectory,

        [object]$PostStartFaultGuard,

        [System.Collections.IList]$OwnedProcessRecords
    )

    [void](Get-VerificationRemainingMilliseconds `
            -DeadlineUtc $DeadlinePolicy.ExecutionDeadlineUtc `
            -OperationName "process start: $FileName")
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }
    if ($null -ne $Environment) {
        foreach ($name in @($Environment.Keys)) {
            $startInfo.Environment[$name] = [string]$Environment[$name]
        }
    }

    Set-VerificationRedirectedProcessEncoding -StartInfo $startInfo
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $record = $null
    $commandIdentity = "$FileName $($Arguments -join ' ')"
    try {
        if (-not $process.Start()) {
            throw "Unable to start process: $FileName"
        }
        $started = $true
        # Both drains start immediately so a child cannot fill one pipe while the other is
        # waiting for the process to exit.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $record = New-VerificationOwnedProcessRecord `
            -Process $process `
            -CommandIdentity $commandIdentity `
            -StandardOutputTask $stdoutTask `
            -StandardErrorTask $stderrTask `
            -DiagnosticsDirectory $DiagnosticsDirectory
        if ($null -ne $OwnedProcessRecords) {
            [void]$OwnedProcessRecords.Add($record)
        }
        if ($null -ne $PostStartFaultGuard) {
            if ($PostStartFaultGuard -isnot [VerificationPostStartFaultGuard]) {
                throw 'Post-start fault guard was not created by the internal deterministic probe.'
            }
            if ($PostStartFaultGuard.TryConsumeSignal()) {
                throw "Internal post-start fault injection for $commandIdentity."
            }
        }
        return $record
    }
    catch {
        $primaryException = $_.Exception
        $secondaryDiagnostics = [System.Collections.Generic.List[string]]::new()
        if ($started -and $null -ne $record) {
            try {
                $cleanup = Stop-VerificationOwnedProcessRecord `
                    -Started $record `
                    -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc `
                    -OperationName "process start cleanup: $FileName" `
                    -PreserveExistingArtifactOnEmpty `
                    -InitialCleanupDiagnostics @("post-start-exception: $($primaryException.Message)")
                foreach ($diagnostic in @($cleanup.Diagnostics)) {
                    $secondaryDiagnostics.Add([string]$diagnostic)
                }
                $primaryException.Data['VerificationRootProcessId'] = $record.ProcessId
                $primaryException.Data['VerificationRootProcessIdentity'] = $record.RootProcessIdentity
                if ($null -ne $cleanup.LifecycleResult) {
                    $primaryException.Data['VerificationLifecycleResult'] = $cleanup.LifecycleResult
                }
            }
            catch {
                $secondaryDiagnostics.Add($_.Exception.ToString())
            }
        }
        elseif ($started) {
            try { $process.Dispose() }
            catch { $secondaryDiagnostics.Add("process start cleanup: $($_.Exception.Message)") }
        }
        else {
            try { $process.Dispose() }
            catch { $secondaryDiagnostics.Add("process dispose: $($_.Exception.Message)") }
        }
        if ($secondaryDiagnostics.Count -gt 0) {
            $primaryException.Data['VerificationSecondaryDiagnostics'] = $secondaryDiagnostics
        }
        throw $primaryException
    }
}

function Complete-VerificationRedirectedProcess {
    param(
        [Parameter(Mandatory)]
        [object]$Started,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy,

        [string]$LifecycleName = 'verification-process',

        [switch]$TerminateProcessTree,

        [switch]$FailureCleanup,

        [switch]$PreserveExistingArtifactOnEmpty,

        [string[]]$InitialCleanupDiagnostics
    )

    [void](New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force)
    $result = Invoke-BoundedProcessLifecycle `
        -Process $Started.Process `
        -StandardOutputTask $Started.StandardOutputTask `
        -StandardErrorTask $Started.StandardErrorTask `
        -RootProcessId $Started.ProcessId `
        -RootProcessIdentity $Started.RootProcessIdentity `
        -CommandIdentity $Started.CommandIdentity `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -ProcessDeadlineUtc $DeadlinePolicy.ExecutionDeadlineUtc `
        -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc `
        -FailureCleanup:$FailureCleanup `
        -TerminateProcessTree:$TerminateProcessTree `
        -LifecycleName $LifecycleName `
        -PreserveExistingArtifactOnEmpty:$PreserveExistingArtifactOnEmpty `
        -InitialCleanupDiagnostics $InitialCleanupDiagnostics
    $Started.StandardOutput = [string]$result.StandardOutput
    $Started.StandardError = [string]$result.StandardError
    $Started.LifecycleResult = $result
    $Started.LifecycleCompleted = $true
    return $result
}

function Stop-VerificationOwnedProcessRecord {
    param(
        [Parameter(Mandatory)]
        [object]$Started,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc,

        [string]$OperationName = 'owned process cleanup',

        [switch]$PreserveExistingArtifactOnEmpty,

        [string[]]$InitialCleanupDiagnostics
    )

    if ([bool]$Started.LifecycleCompleted) {
        return [pscustomobject]@{
            Succeeded = $true
            Diagnostics = @()
            LifecycleResult = $Started.LifecycleResult
        }
    }
    $diagnosticsDirectory = [string]$Started.DiagnosticsDirectory
    if ([string]::IsNullOrWhiteSpace($diagnosticsDirectory)) {
        $diagnosticsDirectory = Join-Path ([IO.Path]::GetTempPath()) ('verification-process-' + [Guid]::NewGuid().ToString('N'))
    }
    $cleanupPolicy = [pscustomobject]@{
        ExecutionDeadlineUtc = [DateTime]::UtcNow
        CleanupDeadlineUtc = $CleanupDeadlineUtc.ToUniversalTime()
        TimeoutSeconds = 1
    }
    $diagnostics = [System.Collections.Generic.List[string]]::new()
    try {
        $result = Complete-VerificationRedirectedProcess `
            -Started $Started `
            -DiagnosticsDirectory $diagnosticsDirectory `
            -DeadlinePolicy $cleanupPolicy `
            -LifecycleName $OperationName `
            -FailureCleanup `
            -TerminateProcessTree `
            -PreserveExistingArtifactOnEmpty:$PreserveExistingArtifactOnEmpty `
            -InitialCleanupDiagnostics $InitialCleanupDiagnostics
        foreach ($diagnostic in @($result.SecondaryDiagnostics)) {
            $diagnostics.Add([string]$diagnostic)
        }
        if (@($result.RemainingOwnedProcessIds).Count -gt 0) {
            $diagnostics.Add(
                "$OperationName left owned process IDs active: $(@($result.RemainingOwnedProcessIds) -join ', ')")
        }
    }
    catch {
        $diagnostics.Add($_.Exception.ToString())
    }
    return [pscustomobject]@{
        Succeeded = $diagnostics.Count -eq 0
        Diagnostics = @($diagnostics)
        LifecycleResult = $Started.LifecycleResult
    }
}

function Stop-VerificationOwnedProcessRecords {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IList]$StartedProcesses,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc
    )

    $diagnostics = [System.Collections.Generic.List[string]]::new()
    foreach ($started in @($StartedProcesses)) {
        $result = Stop-VerificationOwnedProcessRecord `
            -Started $started `
            -CleanupDeadlineUtc $CleanupDeadlineUtc `
            -OperationName ([string]$started.CommandIdentity)
        if (-not $result.Succeeded) {
            foreach ($diagnostic in @($result.Diagnostics)) {
                $diagnostics.Add([string]$diagnostic)
            }
        }
    }
    if ($diagnostics.Count -gt 0) {
        throw [InvalidOperationException]::new(
            "Owned verification process cleanup failed: $($diagnostics -join '; ')")
    }
}

function Invoke-VerificationMonitoredCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [string]$CommandPath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy,

        [hashtable]$Environment,

        [object]$PostStartFaultGuard,

        [switch]$TerminateProcessTree,

        [object]$OwnedProcessRecords
    )

    Write-Host "$Label (timeout $($DeadlinePolicy.TimeoutSeconds)s): $CommandPath $($Arguments -join ' ')"
    $started = Start-VerificationRedirectedProcess `
        -FileName $CommandPath `
        -Arguments $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -Environment $Environment `
        -DeadlinePolicy $DeadlinePolicy `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -PostStartFaultGuard $PostStartFaultGuard `
        -OwnedProcessRecords $OwnedProcessRecords
    $result = Complete-VerificationRedirectedProcess `
        -Started $started `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -DeadlinePolicy $DeadlinePolicy `
        -LifecycleName $Label `
        -TerminateProcessTree:$TerminateProcessTree
    if (-not [string]::IsNullOrEmpty($result.StandardOutput)) {
        Write-Host $result.StandardOutput -NoNewline
    }
    if (-not [string]::IsNullOrEmpty($result.StandardError)) {
        if ($null -eq $result.PrimaryFailureKind) {
            Write-Warning $result.StandardError.TrimEnd()
        }
        else {
            Write-Error $result.StandardError -ErrorAction Continue
        }
    }
    if ($null -ne $result.PrimaryFailureKind) {
        if (@($result.SecondaryDiagnostics).Count -gt 0) {
            Write-Warning "$Label secondary lifecycle diagnostics: $(@($result.SecondaryDiagnostics) -join '; ')"
        }
        throw "$(Get-VerificationLifecycleFailureMessage -Label $Label -Result $result -TimeoutSeconds $DeadlinePolicy.TimeoutSeconds) Diagnostics: $DiagnosticsDirectory"
    }
    if (@($result.SecondaryDiagnostics).Count -gt 0) {
        throw "$(Get-VerificationLifecycleFailureMessage -Label $Label -Result $result -TimeoutSeconds $DeadlinePolicy.TimeoutSeconds) Diagnostics: $DiagnosticsDirectory"
    }
    return $result
}
