using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security;

namespace Ribbit.Windows;

public sealed class TopLevelWindowHandles : WindowHandles
{
    [SuppressUnmanagedCodeSecurity]
    private static class NativeMethods
    {
        [DllImport("user32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows([MarshalAs(UnmanagedType.FunctionPtr)] EnumWindowsProcDelegate enumProc, IntPtr lParam);
    }

    public TopLevelWindowHandles()
    {
        handles = [];
        NativeMethods.EnumWindows(base.EnumWindowProc, (IntPtr)0);
    }
}
