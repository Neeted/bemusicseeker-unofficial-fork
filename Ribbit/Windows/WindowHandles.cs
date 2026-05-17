using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ribbit.Windows;

public abstract class WindowHandles : IEnumerable<IntPtr>, IEnumerable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool EnumWindowsProcDelegate(IntPtr windowHandle, IntPtr lParam);

    internal List<IntPtr> handles;

    public WindowHandles()
    {
        handles = new List<IntPtr>();
    }

    public IEnumerator<IntPtr> GetEnumerator()
    {
        return handles.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return handles.GetEnumerator();
    }

    internal bool EnumWindowProc(IntPtr handle, IntPtr lParam)
    {
        handles.Add(handle);
        return true;
    }
}
