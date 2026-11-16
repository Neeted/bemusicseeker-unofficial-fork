using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

/// <summary>物理ピクセルの作業領域と基準矩形から、初回の外形矩形を計算します。</summary>
internal static class WindowPlacementCalculator
{
    /// <summary>超過した軸だけ縮小し、縮小時は作業領域左上、それ以外は基準中央を領域内へ補正します。</summary>
    /// <param name="workArea">整数物理ピクセルの作業領域。</param>
    /// <param name="reference">中央配置の基準矩形。</param>
    /// <param name="desiredSize">希望外形寸法（DIP）。</param>
    /// <param name="dpiScale">表示先の実DPI倍率。</param>
    /// <returns>丸め後も作業領域に含まれる整数物理ピクセルの外形矩形。</returns>
    internal static Rect Calculate(Rect workArea, Rect reference, Size desiredSize, double dpiScale)
    {
        Validate(workArea, dpiScale);
        Validate(reference, dpiScale);
        if (!double.IsFinite(desiredSize.Width) || !double.IsFinite(desiredSize.Height)
            || desiredSize.Width <= 0 || desiredSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredSize));
        }
        double desiredWidth = Math.Round(desiredSize.Width * dpiScale, MidpointRounding.AwayFromZero);
        double desiredHeight = Math.Round(desiredSize.Height * dpiScale, MidpointRounding.AwayFromZero);
        double width = Math.Min(Math.Max(1, desiredWidth), workArea.Width);
        double height = Math.Min(Math.Max(1, desiredHeight), workArea.Height);
        bool shrink = desiredWidth > workArea.Width || desiredHeight > workArea.Height;
        // 半整数の位置も同じ向きへ丸め、符号をまたぐ整数平行移動で位置が1pxずれるのを防ぎます。
        double left = shrink ? workArea.Left : Math.Clamp(
            Math.Floor(reference.Left + (reference.Width - width) / 2 + 0.5d),
            workArea.Left, workArea.Right - width);
        double top = shrink ? workArea.Top : Math.Clamp(
            Math.Floor(reference.Top + (reference.Height - height) / 2 + 0.5d),
            workArea.Top, workArea.Bottom - height);
        return new Rect(left, top, width, height);
    }

    /// <summary>現在の作業領域へ収まる軸だけ通常の最小DIP寸法を緩和します。大きい領域では通常値へ戻ります。</summary>
    /// <param name="workArea">現在の物理ピクセル作業領域。</param>
    /// <param name="normalMinimum">通常の最小外形寸法（DIP）。</param>
    /// <param name="dpiScale">現在の実DPI倍率。</param>
    /// <returns>現在の表示先へ適用する最小寸法（DIP）。</returns>
    internal static Size MinimumSize(Rect workArea, Size normalMinimum, double dpiScale)
    {
        Validate(workArea, dpiScale);
        return new Size(Math.Min(normalMinimum.Width, workArea.Width / dpiScale),
            Math.Min(normalMinimum.Height, workArea.Height / dpiScale));
    }

    private static void Validate(Rect rectangle, double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0
            || rectangle.IsEmpty || !double.IsFinite(rectangle.Left) || !double.IsFinite(rectangle.Top)
            || !double.IsFinite(rectangle.Right) || !double.IsFinite(rectangle.Bottom)
            || rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rectangle));
        }
    }
}

/// <summary>ランプビューアの未表示HWNDを、Ownerから選んだモニターの実DPIへ同期して配置します。</summary>
internal static class PlaylistLampWindowPlacement
{
    private const uint PlacementFlags = 0x0004 | 0x0010 | 0x0200; // NOZORDER・NOACTIVATE・NOOWNERZORDER

    /// <summary>表示直前のOwnerを基準に、未表示HWNDのDPI更新と最終外形配置を同期して完了します。</summary>
    /// <param name="window">まだ表示されていないビューア。</param>
    /// <param name="desiredSize">仮配置で変更されない希望DIP寸法。</param>
    /// <param name="normalMinimum">通常の最小DIP寸法。</param>
    internal static void Prepare(Window window, Size desiredSize, Size normalMinimum)
    {
        window.VerifyAccess();
        Window owner = window.Owner ?? throw new InvalidOperationException("初期配置のOwnerがありません。");
        nint ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
        Rect work = WorkArea(ownerHandle);
        Rect reference = owner.WindowState == WindowState.Normal ? WindowRect(ownerHandle) : work;
        nint handle = new WindowInteropHelper(window).EnsureHandle();

        // 未表示のRootVisualは未接続です。位置だけでなく外形も領域内へ移し、対象モニターを確定します。
        window.MinWidth = 0;
        window.MinHeight = 0;
        SetRectangle(handle, new Rect(work.Left, work.Top, Math.Min(320, work.Width), Math.Min(240, work.Height)));
        double scale = DpiScale(handle);
        HwndSource source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("配置対象のHwndSourceがありません。");
        Matrix transform = source.CompositionTarget?.TransformToDevice
            ?? throw new InvalidOperationException("配置対象のDPI変換がありません。");
        if (Math.Abs(transform.M11 - scale) > 0.0001 || Math.Abs(transform.M22 - scale) > 0.0001)
        {
            throw new InvalidOperationException("HWNDとWPFのDPIが一致しません。");
        }
        ApplyMinimum(window, work, normalMinimum, scale);
        SetRectangle(handle, WindowPlacementCalculator.Calculate(work, reference, desiredSize, scale));
        // WPFはWM_SIZEでWidth/Heightを同期します。DIP寸法を重ねて代入すると外形が再変更されます。
    }

    /// <summary>移動後のモニターと実DPIに応じて最小寸法だけを更新し、位置・現在寸法は変更しません。</summary>
    /// <param name="window">表示中のビューア。</param>
    /// <param name="normalMinimum">通常の最小DIP寸法。</param>
    internal static void UpdateMinimum(Window window, Size normalMinimum)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle != 0) { ApplyMinimum(window, WorkArea(handle), normalMinimum, DpiScale(handle)); }
    }

    private static void ApplyMinimum(Window window, Rect work, Size normalMinimum, double scale)
    {
        Size minimum = WindowPlacementCalculator.MinimumSize(work, normalMinimum, scale);
        window.MinWidth = minimum.Width;
        window.MinHeight = minimum.Height;
    }

    private static Rect WorkArea(nint handle)
    {
        nint monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var work = info.Work.ToRect();
        if (work.Width <= 0 || work.Height <= 0) { throw new InvalidOperationException("モニターの作業領域が無効です。"); }
        return work;
    }

    private static Rect WindowRect(nint handle)
    {
        if (!GetWindowRect(handle, out NativeRect rectangle)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return rectangle.ToRect();
    }

    private static double DpiScale(nint handle)
    {
        uint dpi = GetDpiForWindow(handle);
        if (dpi == 0) { throw new InvalidOperationException("配置対象のDPIを取得できません。"); }
        return dpi / 96d;
    }

    private static void SetRectangle(nint handle, Rect rectangle)
    {
        if (!SetWindowPos(handle, 0, checked((int)rectangle.Left), checked((int)rectangle.Top),
            checked((int)rectangle.Width), checked((int)rectangle.Height), PlacementFlags))
        { throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor, Work;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rectangle);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
