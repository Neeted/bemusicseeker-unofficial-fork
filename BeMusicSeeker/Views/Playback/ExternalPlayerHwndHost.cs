using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

/// <summary>外部プレーヤーの親となる、UIスレッド所有の黒い子ウィンドウを保持します。</summary>
public sealed class ExternalPlayerHwndHost : HwndHost
{
    /// <summary>WPFの親ウィンドウへ、標準クラスの黒い表示面を作成します。</summary>
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        IntPtr window = CreateWindowEx(0, "STATIC", null, 0x40000000 | 0x10000000 | 0x04000000 | 0x02000000,
            0, 0, 587, 256, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new HandleRef(this, window);
    }

    /// <summary>所有する子ウィンドウを破棄し、失敗は呼出元へ伝えます。</summary>
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (!DestroyWindow(hwnd.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>予約領域内に、物理587×256ピクセルを上限とする表示領域を配置します。</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return new Size(Math.Min(finalSize.Width, 587d / dpi.DpiScaleX), Math.Min(finalSize.Height, 256d / dpi.DpiScaleY));
    }

    /// <summary>システム配色に依存せず、所有不要の黒stock brushで背景を描画します。</summary>
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0014) // WM_ERASEBKGND
        {
            PaintBlack(hwnd, wParam);
            handled = true;
            return new IntPtr(1);
        }
        if (msg == 0x000F) // WM_PAINT
        {
            IntPtr context = BeginPaint(hwnd, out PaintStruct paint);
            try
            {
                PaintBlack(hwnd, context);
            }
            finally
            {
                EndPaint(hwnd, ref paint);
            }
            handled = true;
            return IntPtr.Zero;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static void PaintBlack(IntPtr hwnd, IntPtr context)
    {
        GetClientRect(hwnd, out NativeRect rectangle);
        // BLACK_BRUSH はシステム所有なので、生成・キャッシュ・DeleteObjectは不要です。
        FillRect(context, ref rectangle, GetStockObject(4));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr context, ref NativeRect rectangle, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PaintStruct
    {
        internal IntPtr DeviceContext;
        internal int Erase;
        internal NativeRect Paint;
        internal int Restore;
        internal int IncrementalUpdate;
        internal fixed byte Reserved[32];
    }
}
