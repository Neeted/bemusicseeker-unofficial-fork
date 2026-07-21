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

if ($null -eq ('MonitoredProcessJob' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public sealed class MonitoredProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private IntPtr handle;

    public MonitoredProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the monitored process job.");
        }
        var limits = new JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(
            handle,
            JobObjectInformationClass.ExtendedLimitInformation,
            ref limits,
            (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(handle);
            handle = IntPtr.Zero;
            throw new Win32Exception(error, "Unable to configure the monitored process job.");
        }
    }

    public Process StartProcess(
        string applicationPath,
        string[] arguments,
        string workingDirectory,
        string standardOutputPath,
        string standardErrorPath)
    {
        if (string.IsNullOrWhiteSpace(applicationPath))
        {
            throw new ArgumentException("An application path is required.", nameof(applicationPath));
        }
        ThrowIfDisposed();
        var securityAttributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        IntPtr standardOutput = IntPtr.Zero;
        IntPtr standardError = IntPtr.Zero;
        IntPtr standardInput = IntPtr.Zero;
        var processInformation = new ProcessInformation();
        bool processCreated = false;
        try
        {
            standardOutput = CreateFile(
                standardOutputPath,
                GenericWrite,
                FileShareRead | FileShareWrite,
                ref securityAttributes,
                CreateAlways,
                FileAttributeNormal,
                IntPtr.Zero);
            ThrowIfInvalidFileHandle(standardOutput, "standard output");
            standardError = CreateFile(
                standardErrorPath,
                GenericWrite,
                FileShareRead | FileShareWrite,
                ref securityAttributes,
                CreateAlways,
                FileAttributeNormal,
                IntPtr.Zero);
            ThrowIfInvalidFileHandle(standardError, "standard error");
            standardInput = CreateFile(
                "NUL",
                GenericRead,
                FileShareRead | FileShareWrite,
                ref securityAttributes,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);
            ThrowIfInvalidFileHandle(standardInput, "standard input");

            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles,
                StandardInput = standardInput,
                StandardOutput = standardOutput,
                StandardError = standardError
            };
            var commandLine = new StringBuilder(BuildCommandLine(applicationPath, arguments ?? Array.Empty<string>()));
            processCreated = CreateProcess(
                applicationPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                CreateSuspended | CreateNoWindow,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out processInformation);
            if (!processCreated)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the suspended test root process.");
            }
            if (!AssignProcessToJobObject(handle, processInformation.ProcessHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to assign the suspended test root process to its job.");
            }
            Process process = Process.GetProcessById((int)processInformation.ProcessId);
            if (ResumeThread(processInformation.ThreadHandle) == uint.MaxValue)
            {
                process.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resume the monitored test root process.");
            }
            return process;
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = new List<Exception>();
            if (processCreated && processInformation.ProcessHandle != IntPtr.Zero)
            {
                if (!TerminateProcess(processInformation.ProcessHandle, 1))
                {
                    cleanupFailures.Add(new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to terminate the unassigned suspended test root process."));
                }
                uint waitResult = WaitForSingleObject(processInformation.ProcessHandle, 5000);
                if (waitResult == WaitTimeout)
                {
                    cleanupFailures.Add(new TimeoutException(
                        "The unassigned suspended test root process did not exit within 5 seconds."));
                }
                else if (waitResult == WaitFailed)
                {
                    cleanupFailures.Add(new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to wait for the unassigned suspended test root process."));
                }
                else if (waitResult != WaitObject0)
                {
                    cleanupFailures.Add(new InvalidOperationException(
                        "Waiting for the unassigned suspended test root process returned unexpected result " + waitResult + "."));
                }
            }
            if (cleanupFailures.Count > 0)
            {
                var failures = new List<Exception> { primaryFailure };
                failures.AddRange(cleanupFailures);
                throw new AggregateException(
                    "Starting the monitored test root failed and cleanup did not complete cleanly.",
                    failures);
            }
            throw;
        }
        finally
        {
            CloseIfValid(processInformation.ThreadHandle);
            CloseIfValid(processInformation.ProcessHandle);
            CloseIfValid(standardInput);
            CloseIfValid(standardError);
            CloseIfValid(standardOutput);
        }
    }

    public ulong[] GetProcessIds()
    {
        ThrowIfDisposed();
        int bufferSize = 4096;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (QueryInformationJobObject(
                    handle,
                    JobObjectInformationClass.BasicProcessIdList,
                    buffer,
                    (uint)bufferSize,
                    IntPtr.Zero))
                {
                    uint count = (uint)Marshal.ReadInt32(buffer, sizeof(uint));
                    var processIds = new ulong[count];
                    int offset = sizeof(uint) * 2;
                    for (int index = 0; index < count; index++)
                    {
                        processIds[index] = IntPtr.Size == sizeof(long)
                            ? (ulong)Marshal.ReadInt64(buffer, offset + (index * IntPtr.Size))
                            : (uint)Marshal.ReadInt32(buffer, offset + (index * IntPtr.Size));
                    }
                    return processIds;
                }
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorMoreData)
                {
                    throw new Win32Exception(error, "Unable to query the monitored process job members.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            bufferSize *= 2;
        }
    }

    public bool IsMember(Process process)
    {
        if (process == null)
        {
            throw new ArgumentNullException(nameof(process));
        }
        ThrowIfDisposed();
        if (!IsProcessInJob(process.Handle, handle, out bool isMember))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to verify monitored process job membership.");
        }
        return isMember;
    }

    public bool WaitForEmpty(int timeoutMilliseconds)
    {
        ThrowIfDisposed();
        var stopwatch = Stopwatch.StartNew();
        do
        {
            if (GetActiveProcessCount() == 0)
            {
                return true;
            }
            Thread.Sleep(50);
        }
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds);
        return GetActiveProcessCount() == 0;
    }

    public bool TerminateAndWait(int timeoutMilliseconds)
    {
        ThrowIfDisposed();
        if (!TerminateJobObject(handle, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to terminate the monitored process job.");
        }
        return WaitForEmpty(timeoutMilliseconds);
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    private uint GetActiveProcessCount()
    {
        if (!QueryInformationJobObject(
            handle,
            JobObjectInformationClass.BasicAccountingInformation,
            out JobObjectBasicAccountingInformation accounting,
            (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
            IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the monitored process job.");
        }
        return accounting.ActiveProcesses;
    }

    private void ThrowIfDisposed()
    {
        if (handle == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(MonitoredProcessJob));
        }
    }

    private static string BuildCommandLine(string applicationPath, IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(QuoteArgument(applicationPath));
        foreach (string argument in arguments)
        {
            commandLine.Append(' ').Append(QuoteArgument(argument ?? string.Empty));
        }
        return commandLine.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return argument;
        }
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char value in argument)
        {
            if (value == '\\')
            {
                backslashes++;
                continue;
            }
            if (value == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
                continue;
            }
            quoted.Append('\\', backslashes).Append(value);
            backslashes = 0;
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    private static void ThrowIfInvalidFileHandle(IntPtr fileHandle, string streamName)
    {
        if (fileHandle == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the test process " + streamName + " stream.");
        }
    }

    private static void CloseIfValid(IntPtr nativeHandle)
    {
        if (nativeHandle != IntPtr.Zero && nativeHandle != InvalidHandleValue)
        {
            CloseHandle(nativeHandle);
        }
    }

    private enum JobObjectInformationClass
    {
        BasicAccountingInformation = 1,
        BasicProcessIdList = 3,
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved;
        public string Desktop;
        public string Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const int ErrorMoreData = 234;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xffffffff;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr job,
        JobObjectInformationClass informationClass,
        out JobObjectBasicAccountingInformation information,
        uint informationLength,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr job,
        JobObjectInformationClass informationClass,
        IntPtr information,
        uint informationLength,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
'@
}

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

function Test-ProcessStartTimeMatches {
    param(
        [Parameter(Mandatory)]
        [datetime]$Expected,

        [Parameter(Mandatory)]
        [datetime]$Actual
    )

    $expectedMilliseconds = ([DateTimeOffset]$Expected).ToUnixTimeMilliseconds()
    $actualMilliseconds = ([DateTimeOffset]$Actual).ToUnixTimeMilliseconds()
    return $expectedMilliseconds -eq $actualMilliseconds
}

function Save-TestProcessTreeSnapshot {
    param(
        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [datetime]$RootProcessStartTime,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [int]$ElapsedSeconds
    )

    $snapshotPath = Join-Path $DiagnosticsDirectory "process-tree-at-$ElapsedSeconds-seconds.json"
    $processes = @(Get-CimInstance Win32_Process -OperationTimeoutSec 5)
    $rootCandidate = $processes | Where-Object { [int]$_.ProcessId -eq $RootProcessId } | Select-Object -First 1
    $rootIdentityMatched = $null -ne $rootCandidate `
        -and (Test-ProcessStartTimeMatches -Expected $RootProcessStartTime -Actual ([datetime]$rootCandidate.CreationDate))
    $processIds = [System.Collections.Generic.HashSet[int]]::new()
    if ($rootIdentityMatched) {
        [void]$processIds.Add($RootProcessId)
        do {
            $added = $false
            foreach ($candidate in $processes) {
                if ($processIds.Contains([int]$candidate.ParentProcessId) -and ($processIds.Add([int]$candidate.ProcessId))) {
                    $added = $true
                }
            }
        } while ($added)
    }
    $processTree = @($processes |
        Where-Object { $processIds.Contains([int]$_.ProcessId) } |
        Select-Object ProcessId, ParentProcessId, Name, CommandLine, CreationDate,
            KernelModeTime, UserModeTime, WorkingSetSize)
    $snapshot = [pscustomobject]@{
        RootProcessId = $RootProcessId
        ExpectedRootCreationDate = $RootProcessStartTime
        ObservedRootCreationDate = $rootCandidate?.CreationDate
        RootIdentityMatched = $rootIdentityMatched
        Processes = $processTree
    }
    $snapshot |
        ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath $snapshotPath -Encoding utf8
    return $snapshot
}

function Start-TestHangDiagnostics {
    param(
        [Parameter(Mandatory)]
        [int]$RootProcessId,

        [Parameter(Mandatory)]
        [datetime]$RootProcessStartTime,

        [Parameter(Mandatory)]
        [MonitoredProcessJob]$ProcessJob,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [int]$ElapsedSeconds
    )

    $terminationPath = Join-Path $DiagnosticsDirectory "testhost-termination-at-$ElapsedSeconds-seconds.json"
    $diagnosticStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $diagnosticDeadline = [TimeSpan]::FromSeconds(20)
    try {
        $snapshot = Save-TestProcessTreeSnapshot `
            -RootProcessId $RootProcessId `
            -RootProcessStartTime $RootProcessStartTime `
            -DiagnosticsDirectory $DiagnosticsDirectory `
            -ElapsedSeconds $ElapsedSeconds
        $jobProcessIds = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($jobProcessId in $ProcessJob.GetProcessIds()) {
            [void]$jobProcessIds.Add([int]$jobProcessId)
        }
        $jobProcesses = @(Get-CimInstance Win32_Process -OperationTimeoutSec 5 |
            Where-Object { $jobProcessIds.Contains([int]$_.ProcessId) })
        $testHosts = @($jobProcesses | Where-Object { $_.Name -like 'testhost*' })
        if ($testHosts.Count -eq 0) {
            throw 'No testhost process was found in the monitored test job.'
        }
        $terminationAttempts = foreach ($testHost in $testHosts) {
            if ($diagnosticStopwatch.Elapsed -ge $diagnosticDeadline) {
                [pscustomobject]@{
                    ProcessId = $testHost.ProcessId
                    Name = $testHost.Name
                    Process = $null
                    TerminationRequested = $false
                    HasExited = $false
                    AlreadyExited = $false
                    Failure = 'The shared 20-second diagnostic deadline elapsed before the termination request.'
                }
                continue
            }
            $testHostProcess = $null
            try {
                $testHostProcess = [System.Diagnostics.Process]::GetProcessById([int]$testHost.ProcessId)
                $testHostProcess.Refresh()
                if ($testHostProcess.HasExited) {
                    $testHostProcess.Dispose()
                    [pscustomobject]@{
                        ProcessId = $testHost.ProcessId
                        Name = $testHost.Name
                        Process = $null
                        TerminationRequested = $false
                        HasExited = $true
                        AlreadyExited = $true
                        Failure = $null
                    }
                    continue
                }
                $snapshotStartTime = ([datetime]$testHost.CreationDate).ToUniversalTime()
                $currentTestHost = Get-CimInstance Win32_Process -Filter "ProcessId = $($testHost.ProcessId)" -OperationTimeoutSec 5
                $currentJobProcessIds = [System.Collections.Generic.HashSet[int]]::new()
                foreach ($currentJobProcessId in $ProcessJob.GetProcessIds()) {
                    [void]$currentJobProcessIds.Add([int]$currentJobProcessId)
                }
                if ($null -eq $currentTestHost `
                    -or -not $currentJobProcessIds.Contains([int]$testHost.ProcessId) `
                    -or -not (Test-ProcessStartTimeMatches -Expected $snapshotStartTime -Actual ([datetime]$currentTestHost.CreationDate))) {
                    $testHostProcess.Dispose()
                    [pscustomobject]@{
                        ProcessId = $testHost.ProcessId
                        Name = $testHost.Name
                        Process = $null
                        TerminationRequested = $false
                        HasExited = $true
                        AlreadyExited = $true
                        Failure = $null
                    }
                    continue
                }
                $actualStartTime = $testHostProcess.StartTime.ToUniversalTime()
                if (-not (Test-ProcessStartTimeMatches -Expected $snapshotStartTime -Actual $actualStartTime)) {
                    $testHostProcess.Dispose()
                    [pscustomobject]@{
                        ProcessId = $testHost.ProcessId
                        Name = $testHost.Name
                        Process = $null
                        TerminationRequested = $false
                        HasExited = $true
                        AlreadyExited = $true
                        Failure = $null
                    }
                    continue
                }
                if (-not $ProcessJob.IsMember($testHostProcess)) {
                    $testHostProcess.Dispose()
                    [pscustomobject]@{
                        ProcessId = $testHost.ProcessId
                        Name = $testHost.Name
                        Process = $null
                        TerminationRequested = $false
                        HasExited = $true
                        AlreadyExited = $true
                        Failure = $null
                    }
                    continue
                }
                $testHostProcess.Kill($true)
                [pscustomobject]@{
                    ProcessId = $testHost.ProcessId
                    Name = $testHost.Name
                    Process = $testHostProcess
                    TerminationRequested = $true
                    HasExited = $false
                    AlreadyExited = $false
                    Failure = $null
                }
            }
            catch [System.ArgumentException] {
                $testHostProcess?.Dispose()
                [pscustomobject]@{
                    ProcessId = $testHost.ProcessId
                    Name = $testHost.Name
                    Process = $null
                    TerminationRequested = $false
                    HasExited = $true
                    AlreadyExited = $true
                    Failure = $null
                }
            }
            catch [System.InvalidOperationException] {
                $testHostProcess?.Dispose()
                [pscustomobject]@{
                    ProcessId = $testHost.ProcessId
                    Name = $testHost.Name
                    Process = $null
                    TerminationRequested = $false
                    HasExited = $true
                    AlreadyExited = $true
                    Failure = $null
                }
            }
            catch {
                $testHostProcess?.Dispose()
                [pscustomobject]@{
                    ProcessId = $testHost.ProcessId
                    Name = $testHost.Name
                    Process = $null
                    TerminationRequested = $false
                    HasExited = $false
                    AlreadyExited = $false
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
                AlreadyExited = $termination.AlreadyExited
                Stopped = $termination.HasExited
                Failure = $failure
            }
        }
        @($terminations) |
            ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath $terminationPath -Encoding utf8
        $terminationFailures = @($terminations | Where-Object { -not $_.Stopped })
        if ($terminationFailures.Count -gt 0) {
            throw "One or more testhost processes could not be stopped: $($terminationFailures.Failure -join '; ')"
        }
        return $true
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
        throw "Test hang diagnostics failed at $ElapsedSeconds seconds: $($diagnosticFailure.Message); diagnostics: $DiagnosticsDirectory"
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

    $observationSeconds = 180
    $diagnosticSeconds = 300
    $observationThreshold = [TimeSpan]::FromSeconds($observationSeconds)
    $diagnosticThreshold = [TimeSpan]::FromSeconds($diagnosticSeconds)
    $terminationFallbackThreshold = [TimeSpan]::FromSeconds($diagnosticSeconds + 30)
    $commandInfo = Get-Command $Command -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $commandPath = $commandInfo.Source
    if ([string]::IsNullOrWhiteSpace($commandPath)) {
        $commandPath = $commandInfo.Path
    }
    if ([string]::IsNullOrWhiteSpace($commandPath)) {
        throw "Unable to resolve the test command executable: $Command"
    }

    $standardOutputPath = Join-Path $DiagnosticsDirectory 'test-process.stdout.log'
    $standardErrorPath = Join-Path $DiagnosticsDirectory 'test-process.stderr.log'
    $process = $null
    $processJob = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $observationRecorded = $false
    $diagnosticStarted = $false
    $exitCode = $null
    $failures = [System.Collections.Generic.List[System.Exception]]::new()
    try {
        $processJob = [MonitoredProcessJob]::new()
        $process = $processJob.StartProcess(
            $commandPath,
            $Arguments,
            $repoRoot,
            $standardOutputPath,
            $standardErrorPath)
        $rootProcessStartTime = $process.StartTime
        while (-not $process.WaitForExit(1000)) {
            if (-not $observationRecorded -and $stopwatch.Elapsed -ge $observationThreshold) {
                $observationRecorded = $true
                try {
                    [void](Save-TestProcessTreeSnapshot `
                        -RootProcessId $process.Id `
                        -RootProcessStartTime $rootProcessStartTime `
                        -DiagnosticsDirectory $DiagnosticsDirectory `
                        -ElapsedSeconds $observationSeconds)
                    Write-Warning "Test execution reached $observationSeconds seconds. The monitored process tree was captured and execution continues toward the evidence-based $diagnosticSeconds-second diagnostic threshold: $DiagnosticsDirectory"
                }
                catch {
                    Write-Warning "Test execution reached $observationSeconds seconds, but its process snapshot could not be captured: $($_.Exception.Message)"
                }
            }
            if (-not $diagnosticStarted -and $stopwatch.Elapsed -ge $diagnosticThreshold) {
                $diagnosticStarted = $true
                $failures.Add([TimeoutException]::new(
                    "Test execution reached the evidence-based $diagnosticSeconds-second diagnostic threshold."))
                try {
                    $testHostStopped = Start-TestHangDiagnostics `
                        -RootProcessId $process.Id `
                        -RootProcessStartTime $rootProcessStartTime `
                        -ProcessJob $processJob `
                        -DiagnosticsDirectory $DiagnosticsDirectory `
                        -ElapsedSeconds $diagnosticSeconds
                }
                catch {
                    $failures.Add($_.Exception)
                    try {
                        if (-not $processJob.TerminateAndWait(5000)) {
                            $failures.Add([InvalidOperationException]::new(
                                'Test diagnostics failed and the monitored process job did not become empty.'))
                        }
                    }
                    catch {
                        $failures.Add($_.Exception)
                    }
                    break
                }
                if ($testHostStopped) {
                    Write-Warning "Test execution reached $diagnosticSeconds seconds. The testhost was stopped to finalize the blame sequence; diagnostics: $DiagnosticsDirectory"
                }
                else {
                    Write-Warning "Test execution reached $diagnosticSeconds seconds, but the original monitored root had already exited. No PID-reused process was stopped; blame output will be checked independently: $DiagnosticsDirectory"
                }
            }
            if ($stopwatch.Elapsed -lt $terminationFallbackThreshold) {
                continue
            }
            $failures.Add([TimeoutException]::new(
                "Test diagnostics did not complete within 30 seconds after the $diagnosticSeconds-second threshold."))
            try {
                if (-not $processJob.TerminateAndWait(5000)) {
                    $failures.Add([InvalidOperationException]::new(
                        'The monitored process job did not become empty after the diagnostic fallback termination.'))
                }
            }
            catch {
                $failures.Add($_.Exception)
            }
            break
        }
        if (-not $process.HasExited -and -not $process.WaitForExit(5000)) {
            $failures.Add([InvalidOperationException]::new(
                'The monitored test root did not exit after its process job was stopped.'))
        }
        if (-not $processJob.WaitForEmpty(5000)) {
            $failures.Add([InvalidOperationException]::new(
                'The test root exited while child processes were still active.'))
            try {
                if (-not $processJob.TerminateAndWait(5000)) {
                    $failures.Add([InvalidOperationException]::new(
                        'The monitored process job did not become empty after lingering-child termination.'))
                }
            }
            catch {
                $failures.Add($_.Exception)
            }
        }
        if ($process.HasExited) {
            $exitCode = $process.ExitCode
        }
    }
    catch {
        $failures.Add($_.Exception)
    }
    finally {
        $stopwatch.Stop()
        if ($null -ne $processJob) {
            try {
                if (-not $processJob.WaitForEmpty(0) -and -not $processJob.TerminateAndWait(5000)) {
                    $failures.Add([InvalidOperationException]::new(
                        'The monitored process job did not become empty during final cleanup.'))
                }
            }
            catch {
                $failures.Add($_.Exception)
            }
            $processJob.Dispose()
        }
        if ($null -ne $process) {
            if ($null -eq $exitCode) {
                try {
                    $process.Refresh()
                    if ($process.HasExited) {
                        $exitCode = $process.ExitCode
                    }
                }
                catch {
                    $failures.Add($_.Exception)
                }
            }
            $process.Dispose()
        }
    }

    try {
        if (Test-Path -LiteralPath $standardOutputPath -PathType Leaf) {
            $standardOutput = Get-Content -LiteralPath $standardOutputPath -Raw
            if (-not [string]::IsNullOrEmpty($standardOutput)) {
                Write-Host $standardOutput -NoNewline
            }
        }
        if (Test-Path -LiteralPath $standardErrorPath -PathType Leaf) {
            $standardError = Get-Content -LiteralPath $standardErrorPath -Raw
            if (-not [string]::IsNullOrEmpty($standardError)) {
                Write-Error $standardError -ErrorAction Continue
            }
        }
    }
    catch {
        $failures.Add($_.Exception)
    }

    if ($diagnosticStarted) {
        try {
            $sequenceFiles = @(Get-ChildItem -LiteralPath $DiagnosticsDirectory -Filter 'Sequence*.xml' -Recurse -File)
            if ($sequenceFiles.Count -eq 0) {
                $failures.Add([InvalidOperationException]::new(
                    "Test diagnostics produced no blame sequence: $DiagnosticsDirectory"))
            }
        }
        catch {
            $failures.Add($_.Exception)
        }
    }

    if ($null -ne $exitCode -and $exitCode -ne 0) {
        $failures.Add([InvalidOperationException]::new(
            "Command failed with exit code ${exitCode}: $Command $($Arguments -join ' ')"))
    }
    if ($failures.Count -eq 1) {
        throw $failures[0]
    }
    if ($failures.Count -gt 1) {
        throw [AggregateException]::new(
            'Monitored test execution failed. See inner exceptions in primary-first order.',
            $failures.ToArray())
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
