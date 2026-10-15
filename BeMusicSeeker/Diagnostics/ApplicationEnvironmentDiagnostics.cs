#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Ribbit.Logging;

namespace BeMusicSeeker.Diagnostics;

/// <summary>本体表示後に一回取得する基本環境を、UI所有値と背景で取得できる値に分けます。</summary>
internal static class ApplicationEnvironmentDiagnostics
{
    /// <summary>表示済み本体のHWND情報だけをUI上で捕捉し、残りの取得と出力を背景へ予約します。</summary>
    /// <remarks>返すTaskは起動・終了の待機条件にせず、呼出元で例外を観測します。</remarks>
    internal static Task Start(Window mainWindow)
    {
        mainWindow.Dispatcher.VerifyAccess();
        IntPtr handle = new WindowInteropHelper(mainWindow).Handle;
        string snapshot = "main_window_awareness=" + Capture(() => GetAwareness(handle))
            + " main_window_dpi=" + Capture(() =>
            {
                uint dpi = GetDpiForWindow(handle);
                return dpi == 0 ? "unavailable" : dpi.ToString(CultureInfo.InvariantCulture) + "dpi";
            });
        return Queue(snapshot);
    }

    /// <summary>変更不能なUI情報を受け取り、収集と出力の完了を呼出元へ同期的に要求しません。</summary>
    /// <param name="windowSnapshot">UI上で取得済みのHWND情報。</param>
    /// <param name="collect">背景収集。省略時は基本環境を取得します。</param>
    /// <param name="write">記録先。省略時は通常ログへINFOで渡します。</param>
    internal static Task Queue(string windowSnapshot, Func<string>? collect = null, Action<string>? write = null)
    {
        return Task.Run(() =>
        {
            string environment = Capture(collect ?? Collect);
            (write ?? (message => NLogWrapper.FileLogger?.Info(message)))(
                "application_environment " + windowSnapshot + " " + environment);
        });
    }

    private static string Collect()
    {
        Assembly application = typeof(App).Assembly;
        Assembly wpf = typeof(Window).Assembly;
        return "application_version=" + Capture(() => application.GetName().Version?.ToString() ?? "unavailable")
            + " application_build=" + Capture(() => application.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unavailable")
            + " windows=" + Capture(() => RuntimeInformation.OSDescription)
            + " dotnet=" + Capture(() => RuntimeInformation.FrameworkDescription)
            + " process_architecture=" + RuntimeInformation.ProcessArchitecture
            + " wpf_assembly_version=" + Capture(() => wpf.GetName().Version?.ToString() ?? "unavailable")
            + " wpf_file_version=" + Capture(() => FileVersionInfo.GetVersionInfo(wpf.Location).FileVersion ?? "unavailable")
            + " monitors=" + Capture(CollectMonitors);
    }

    private static string CollectMonitors()
    {
        var monitors = new List<string>();
        bool success = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data) =>
        {
            monitors.Add(Capture(() =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info))
                {
                    return "unavailable(win32=" + Marshal.GetLastWin32Error() + ")";
                }
                string scale = Capture(() =>
                {
                    int result = GetScaleFactorForMonitor(monitor, out int percent);
                    // APIは失敗時にも代替値を返すので、HRESULT成功時だけ実測値として使います。
                    return result == 0 && percent > 0
                        ? percent.ToString(CultureInfo.InvariantCulture) + "%"
                        : "unavailable(hresult=" + result.ToString("X8", CultureInfo.InvariantCulture) + ")";
                });
                return "{primary=" + ((info.Flags & 1) != 0)
                    + " rect_px=" + Format(info.Monitor)
                    + " resolution_px=" + (info.Monitor.Right - info.Monitor.Left) + "x" + (info.Monitor.Bottom - info.Monitor.Top)
                    + " work_px=" + Format(info.Work) + " scale=" + scale + "}";
            }));
            return true;
        }, IntPtr.Zero);
        return (success ? "count=" + monitors.Count : "count=unavailable enumeration_failed")
            + " coordinate_unit=physical_px [" + string.Join(",", monitors) + "]";
    }

    private static string GetAwareness(IntPtr handle)
    {
        IntPtr context = GetWindowDpiAwarenessContext(handle);
        if (context == IntPtr.Zero) { return "unavailable"; }
        if (AreDpiAwarenessContextsEqual(context, new IntPtr(-4))) { return "PerMonitorV2"; }
        if (AreDpiAwarenessContextsEqual(context, new IntPtr(-3))) { return "PerMonitor"; }
        if (AreDpiAwarenessContextsEqual(context, new IntPtr(-2))) { return "System"; }
        if (AreDpiAwarenessContextsEqual(context, new IntPtr(-5))) { return "UnawareGdiScaled"; }
        if (AreDpiAwarenessContextsEqual(context, new IntPtr(-1))) { return "Unaware"; }
        return "unavailable(unknown_context)";
    }

    private static string Capture(Func<string> read)
    {
        try { return read(); }
        catch (Exception exception) { return "unavailable(" + exception.GetType().Name + ")"; }
    }

    private static string Format(NativeRect rect) =>
        FormattableString.Invariant($"({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor, Work;
        internal uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr left, IntPtr right);
}
