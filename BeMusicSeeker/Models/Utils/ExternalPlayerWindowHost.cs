using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Ribbit.Util.Extensions;
using Ribbit.Windows;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// Owns the native window operations required to embed an external player in the
/// playback surface.  External-player workflows depend on this narrow port rather
/// than calling the Win32 API directly.
/// </summary>
internal readonly struct ExternalWindowHandle : IEquatable<ExternalWindowHandle>
{
    private readonly IntPtr nativeValue;

    internal ExternalWindowHandle(IntPtr nativeValue)
    {
        this.nativeValue = nativeValue;
    }

    internal bool IsEmpty => nativeValue == IntPtr.Zero;

    internal IntPtr NativeValue => nativeValue;

    public bool Equals(ExternalWindowHandle other)
        => nativeValue == other.nativeValue;

    public override bool Equals(object obj)
        => obj is ExternalWindowHandle other && Equals(other);

    public override int GetHashCode()
        => nativeValue.GetHashCode();

    public static bool operator ==(ExternalWindowHandle left, ExternalWindowHandle right)
        => left.Equals(right);

    public static bool operator !=(ExternalWindowHandle left, ExternalWindowHandle right)
        => !left.Equals(right);
}

public sealed class WindowPlacement
{
    internal WindowPlacement(
        int flags,
        int showCommand,
        int minX,
        int minY,
        int maxX,
        int maxY,
        int left,
        int top,
        int right,
        int bottom)
    {
        Flags = flags;
        ShowCommand = showCommand;
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Flags { get; }
    public int ShowCommand { get; }
    public int MinX { get; }
    public int MinY { get; }
    public int MaxX { get; }
    public int MaxY { get; }
    public int Left { get; }
    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }

}

internal static class Win32WindowPlacementAdapter
{
    internal static WindowPlacement FromNative(Win32API.WINDOWPLACEMENT placement)
    {
        return new WindowPlacement(
            placement.Flags,
            (int)placement.ShowCmd,
            placement.MinPosition.X,
            placement.MinPosition.Y,
            placement.MaxPosition.X,
            placement.MaxPosition.Y,
            placement.NormalPosition.Left,
            placement.NormalPosition.Top,
            placement.NormalPosition.Right,
            placement.NormalPosition.Bottom);
    }

    internal static Win32API.WINDOWPLACEMENT ToNative(WindowPlacement placement)
    {
        if (placement == null)
        {
            throw new ArgumentNullException(nameof(placement));
        }
        return new Win32API.WINDOWPLACEMENT
        {
            Length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32API.WINDOWPLACEMENT)),
            Flags = placement.Flags,
            ShowCmd = (Win32API.ShowWindowCommands)placement.ShowCommand,
            MinPosition = new Win32API.POINT(placement.MinX, placement.MinY),
            MaxPosition = new Win32API.POINT(placement.MaxX, placement.MaxY),
            NormalPosition = new Win32API.RECT(placement.Left, placement.Top, placement.Right, placement.Bottom)
        };
    }

    internal static Win32API.WINDOWPLACEMENT ToNativeForRestore(WindowPlacement placement, int width, int height)
    {
        Win32API.WINDOWPLACEMENT nativePlacement = ToNative(placement);
        nativePlacement.Flags = 0;
        nativePlacement.ShowCmd = Win32API.ShowWindowCommands.Normal;
        nativePlacement.NormalPosition.Width = width;
        nativePlacement.NormalPosition.Height = height;
        return nativePlacement;
    }
}

internal interface IExternalPlayerWindowHost
{
    ExternalWindowHandle ParentHandle { get; }

    bool UsesLegacyWindowEmbedding { get; }

    ExternalWindowHandle GetForegroundWindow();

    bool IsWindow(ExternalWindowHandle window);

    bool SetForegroundWindow(ExternalWindowHandle window);

    void SetFocus(ExternalWindowHandle window);

    IReadOnlyList<ExternalWindowHandle> EnumerateThreadWindows(IReadOnlyList<int> threadIds);

    string GetClassName(ExternalWindowHandle window);

    string GetWindowText(ExternalWindowHandle window);

    void SendKey(short scanCode, bool extended, bool keyDown);

    void AttachBmiIdxWindow(ExternalWindowHandle childWindow);

    void AttachUbmplayWindow(ExternalWindowHandle childWindow, bool legacyWindowStyle);

    void MoveExternalWindowOffscreen(ExternalWindowHandle childWindow);

    void DetachUbmplayWindow(ExternalWindowHandle childWindow);

    bool IsLr2WindowStyleApplied(ExternalWindowHandle childWindow);

    void ApplyLr2WindowStyle(ExternalWindowHandle childWindow);

    void NotifyBmiIdxPlaybackStarted(ExternalWindowHandle childWindow);

    WindowPlacement CaptureWindowPlacement(ExternalWindowHandle childWindow);

    void ApplyWindowPlacement(ExternalWindowHandle childWindow, WindowPlacement placement, int width, int height);
}

internal sealed class Win32ExternalPlayerWindowHost : IExternalPlayerWindowHost
{
    internal Win32ExternalPlayerWindowHost(IntPtr parentHandle)
    {
        ParentHandle = new ExternalWindowHandle(parentHandle);
    }

    public ExternalWindowHandle ParentHandle { get; }

    public bool UsesLegacyWindowEmbedding
        => !Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012);

    public ExternalWindowHandle GetForegroundWindow()
        => new(Win32API.GetForegroundWindow());

    public bool IsWindow(ExternalWindowHandle window)
        => Win32API.IsWindow(window.NativeValue);

    public bool SetForegroundWindow(ExternalWindowHandle window)
        => Win32API.SetForegroundWindow(window.NativeValue);

    public void SetFocus(ExternalWindowHandle window)
    {
        Win32API.SetFocus(window.NativeValue);
    }

    public IReadOnlyList<ExternalWindowHandle> EnumerateThreadWindows(IReadOnlyList<int> threadIds)
    {
        return [.. (threadIds ?? Array.Empty<int>())
            .SelectMany(threadId => new ThreadWindowHandles((uint)threadId))
            .Where(window => window != IntPtr.Zero)
            .Select(window => new ExternalWindowHandle(window))];
    }

    public string GetClassName(ExternalWindowHandle window)
    {
        var value = new StringBuilder(4096);
        Win32API.GetClassName(window.NativeValue, value, value.Capacity);
        return value.ToString();
    }

    public string GetWindowText(ExternalWindowHandle window)
    {
        var value = new StringBuilder(4096);
        Win32API.GetWindowText(window.NativeValue, value, value.Capacity);
        return value.ToString();
    }

    public void SendKey(short scanCode, bool extended, bool keyDown)
    {
        DirectInputSendKey.KEYEVENTF flags = DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE;
        if (extended)
        {
            flags |= DirectInputSendKey.KEYEVENTF.KEYEVENTF_EXTENDEDKEY;
        }
        flags |= keyDown
            ? DirectInputSendKey.KEYEVENTF.KEYEVENTF_KEYDOWN
            : DirectInputSendKey.KEYEVENTF.KEYEVENTF_KEYUP;
        DirectInputSendKey.SendKey(scanCode, flags, 0);
    }

    public void AttachBmiIdxWindow(ExternalWindowHandle childWindow)
    {
        Win32API.SetWindowLong(childWindow.NativeValue, -16, 1342177280u);
        Win32API.SetParent(childWindow.NativeValue, ParentHandle.NativeValue);
        Win32API.SetWindowPos(
            childWindow.NativeValue,
            IntPtr.Zero,
            0,
            0,
            587,
            256,
            Win32API.SetWindowPosFlags.DoNotActivate | Win32API.SetWindowPosFlags.ShowWindow);
    }

    public void AttachUbmplayWindow(ExternalWindowHandle childWindow, bool legacyWindowStyle)
    {
        if (legacyWindowStyle)
        {
            Win32API.SetWindowLong(childWindow.NativeValue, -16, 1342177280u);
            Win32API.SetParent(childWindow.NativeValue, ParentHandle.NativeValue);
            Win32API.SetWindowPos(childWindow.NativeValue, IntPtr.Zero, 0, 0, 587, 256, (Win32API.SetWindowPosFlags)0u);
        }
        else
        {
            Win32API.SetWindowLong(childWindow.NativeValue, -16, 281018368u);
        }
    }

    public void MoveExternalWindowOffscreen(ExternalWindowHandle childWindow)
    {
        Win32API.SetWindowPos(
            childWindow.NativeValue,
            IntPtr.Zero,
            -9999,
            -9999,
            587,
            256,
            (Win32API.SetWindowPosFlags)0u);
    }

    public void DetachUbmplayWindow(ExternalWindowHandle childWindow)
    {
        MoveExternalWindowOffscreen(childWindow);
        Win32API.SetParent(childWindow.NativeValue, IntPtr.Zero);
        Win32API.SetWindowLong(childWindow.NativeValue, -16, 382337024u);
    }

    public bool IsLr2WindowStyleApplied(ExternalWindowHandle childWindow)
    {
        return Win32API.GetWindowLong(childWindow.NativeValue, -16) == 2495610880u
            && Win32API.GetWindowLong(childWindow.NativeValue, -20) == 129u;
    }

    public void ApplyLr2WindowStyle(ExternalWindowHandle childWindow)
    {
        Win32API.SetWindowLong(childWindow.NativeValue, -16, 2495610880u);
        Win32API.SetWindowLong(childWindow.NativeValue, -20, 129u);
    }

    public void NotifyBmiIdxPlaybackStarted(ExternalWindowHandle childWindow)
    {
        Win32API.PostMessage(new HandleRef(this, childWindow.NativeValue), 1127u, IntPtr.Zero, IntPtr.Zero);
    }

    public WindowPlacement CaptureWindowPlacement(ExternalWindowHandle childWindow)
    {
        Win32API.WINDOWPLACEMENT placement = default;
        Win32API.GetWindowPlacement(childWindow.NativeValue, ref placement);
        return Win32WindowPlacementAdapter.FromNative(placement);
    }

    public void ApplyWindowPlacement(ExternalWindowHandle childWindow, WindowPlacement placement, int width, int height)
    {
        Win32API.WINDOWPLACEMENT nativePlacement = Win32WindowPlacementAdapter.ToNativeForRestore(placement, width, height);
        Win32API.SetWindowPlacement(childWindow.NativeValue, ref nativePlacement);
    }
}
