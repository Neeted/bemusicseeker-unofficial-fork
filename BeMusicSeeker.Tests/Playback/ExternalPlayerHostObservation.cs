using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>再生面の所有・寿命・物理配置を、読取り専用のWin32呼出しで観測します。</summary>
internal static class ExternalPlayerHostObservation
{
    /// <summary>表示済みWPFウィンドウの子が、現在のプロセスとUIスレッドで有効なことを確認します。</summary>
    internal static void AssertOwnedChild(IntPtr handle, Window window)
    {
        Assert.AreNotEqual(IntPtr.Zero, handle);
        Assert.IsTrue(IsWindow(handle));
        Assert.AreEqual(new WindowInteropHelper(window).Handle, GetParent(handle));
        uint thread = GetWindowThreadProcessId(handle, out uint process);
        Assert.AreEqual((uint)Environment.ProcessId, process);
        Assert.AreEqual(GetCurrentThreadId(), thread);
    }

    /// <summary>画面座標のウィンドウ矩形を物理ピクセルで返します。</summary>
    internal static Rect GetRectangle(IntPtr handle)
    {
        Assert.IsTrue(GetWindowRect(handle, out NativeRect rectangle));
        return new Rect(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
    }

    /// <summary>指定されたHWNDが現在有効かを読み取ります。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
