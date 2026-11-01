using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BeMusicSeeker.TestSupport.Processes;

/// <summary>Windows の起動時所有境界と、その境界だけの停止を実装します。</summary>
internal sealed class WindowsProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;

    /// <summary>渡された Job ハンドルの所有権を引き受け、破棄時に閉じます。fixture は無効なハンドルで実際の割当失敗を検査できます。</summary>
    internal WindowsProcessJob(SafeFileHandle handle) => this.handle = handle;

    /// <summary>子へ継承しない、最後のハンドル終了で全員を停止する Job を作ります。</summary>
    internal static WindowsProcessJob Create()
    {
        SafeFileHandle handle = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var job = new WindowsProcessJob(handle);
        var limits = new Native.ExtendedLimits();
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!Native.SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<Native.ExtendedLimits>()))
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw failure;
        }
        return job;
    }

    /// <summary>停止した primary thread を Job に割り当ててから再開します。</summary>
    internal Process Start(ProcessStartInfo info, AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr)
    {
        var security = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = true };
        using SafeFileHandle input = Native.CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
        if (input.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attributes = Marshal.AllocHGlobal(size);
        IntPtr inherited = Marshal.AllocHGlobal(IntPtr.Size * 3);
        IntPtr environment = IntPtr.Zero;
        bool initialized = false;
        Native.ProcessInformation created = default;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            initialized = true;
            Marshal.WriteIntPtr(inherited, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, IntPtr.Size, stdout.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, IntPtr.Size * 2, stderr.ClientSafePipeHandle.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, inherited, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new Native.StartupInfoEx
            {
                Startup = new Native.StartupInfo
                {
                    Size = Marshal.SizeOf<Native.StartupInfoEx>(),
                    Flags = 0x100,
                    Input = input.DangerousGetHandle(),
                    Output = stdout.ClientSafePipeHandle.DangerousGetHandle(),
                    Error = stderr.ClientSafePipeHandle.DangerousGetHandle(),
                },
                Attributes = attributes,
            };
            string block = string.Join('\0', info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            var command = new StringBuilder(Quote(info.FileName));
            foreach (string argument in info.ArgumentList)
            {
                command.Append(' ').Append(Quote(argument));
            }
            // HANDLE_LIST に列挙した標準ハンドルだけを継承する。Job はリストへ入れない。
            if (!Native.CreateProcessW(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                0x4 | 0x400 | 0x80000 | 0x08000000, environment, info.WorkingDirectory, ref startup, out created))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!Native.AssignProcessToJobObject(handle, created.Process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var process = Process.GetProcessById(created.ProcessId);
            try
            {
                // managed Process にも生存中の root handle を確保し、PID 再利用へ依存しない。
                _ = process.Handle;
                if (Native.ResumeThread(created.Thread) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
        catch (Exception primary)
        {
            Exception? cleanup = null;
            if (created.Process != IntPtr.Zero)
            {
                if (!Native.TerminateProcess(created.Process, 1))
                {
                    cleanup = new Win32Exception(Marshal.GetLastWin32Error());
                }
                else if (Native.WaitForSingleObject(created.Process, 10000) != 0)
                {
                    cleanup = new TimeoutException("起動失敗したプロセスを回収できません。");
                }
            }
            throw new OwnedProcessStartException(primary, cleanup);
        }
        finally
        {
            if (created.Thread != IntPtr.Zero)
            {
                Native.CloseHandle(created.Thread);
            }

            if (created.Process != IntPtr.Zero)
            {
                Native.CloseHandle(created.Process);
            }

            if (initialized)
            {
                Native.DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(inherited);
            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }

            GC.KeepAlive(input);
            GC.KeepAlive(stdout);
            GC.KeepAlive(stderr);
        }
    }

    /// <summary>Job に属する全プロセスの終端を観測し、回収期限による取消で待機を打ち切ります。</summary>
    internal async Task WaitForEmptyAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Native.QueryInformationJobObject(handle, 1, out Native.Accounting accounting,
                Marshal.SizeOf<Native.Accounting>(), IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (accounting.ActiveProcesses == 0)
            {
                return;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>自分の Job だけを強制終了します。</summary>
    internal void Terminate()
    {
        if (!Native.TerminateJobObject(handle, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Job の最終所有ハンドルを閉じます。</summary>
    public void Dispose() => handle.Dispose();

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfo { internal int Size; internal IntPtr Reserved, Desktop, Title; internal int X, Y, XSize, YSize, XChars, YChars, Fill, Flags; internal short Show, ReservedSize; internal IntPtr ReservedBytes, Input, Output, Error; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits { internal long ProcessTime, JobTime; internal uint LimitFlags; internal UIntPtr MinWorkingSet, MaxWorkingSet; internal uint ActiveProcessLimit; internal UIntPtr Affinity; internal uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits { internal BasicLimits Basic; internal IoCounters Io; internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential)] internal struct Accounting { internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, int length);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out Accounting information, int length, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(SafeFileHandle job, uint code);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr attributes);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
