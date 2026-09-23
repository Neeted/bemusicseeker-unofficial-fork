using System;
using System.Runtime.InteropServices;
using System.Security;

namespace Ribbit.Windows;

public sealed class ChildWindowHandles : WindowHandles
{
    [SuppressUnmanagedCodeSecurity]
    private static class NativeMethods
    {
        [DllImport("user32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr handle, [MarshalAs(UnmanagedType.FunctionPtr)] EnumWindowsProcDelegate enumProc, IntPtr lParam);
    }

    private readonly IntPtr windowHandle;

    public IntPtr WindowHandle => windowHandle;

    public ChildWindowHandles(IntPtr windowHandle)
    {
        this.windowHandle = windowHandle;
        NativeMethods.EnumChildWindows(windowHandle, base.EnumWindowProc, (IntPtr)0);
    }
}
