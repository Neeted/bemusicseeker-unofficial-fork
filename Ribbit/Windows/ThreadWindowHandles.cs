using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Permissions;

namespace Ribbit.Windows;

[SecurityPermission(SecurityAction.Demand, UnmanagedCode = true)]
public sealed class ThreadWindowHandles : WindowHandles
{
    [SuppressUnmanagedCodeSecurity]
    private static class NativeMethods
    {
        [DllImport("user32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumThreadWindows(uint threadId, [MarshalAs(UnmanagedType.FunctionPtr)] EnumWindowsProcDelegate enumProc, IntPtr lParam);
    }

    private uint threadId;

    public uint ThreadID => threadId;

    public ThreadWindowHandles(uint threadId)
    {
        this.threadId = threadId;
        NativeMethods.EnumThreadWindows(threadId, base.EnumWindowProc, (IntPtr)0);
    }
}
