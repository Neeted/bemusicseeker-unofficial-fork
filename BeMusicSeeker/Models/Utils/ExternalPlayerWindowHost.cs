using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Ribbit.Windows;
using Ribbit.Util.Extensions;

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

internal sealed class ExternalWindowPlacement
{
    internal ExternalWindowPlacement(
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

    internal int Flags { get; }
    internal int ShowCommand { get; }
    internal int MinX { get; }
    internal int MinY { get; }
    internal int MaxX { get; }
    internal int MaxY { get; }
    internal int Left { get; }
    internal int Top { get; }
    internal int Right { get; }
    internal int Bottom { get; }

    internal static ExternalWindowPlacement FromNative(Win32API.WINDOWPLACEMENT placement)
    {
        return new ExternalWindowPlacement(
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

    internal Win32API.WINDOWPLACEMENT ToNative()
    {
        return new Win32API.WINDOWPLACEMENT
        {
            Length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32API.WINDOWPLACEMENT)),
            Flags = Flags,
            ShowCmd = (Win32API.ShowWindowCommands)ShowCommand,
            MinPosition = new Win32API.POINT(MinX, MinY),
            MaxPosition = new Win32API.POINT(MaxX, MaxY),
            NormalPosition = new Win32API.RECT(Left, Top, Right, Bottom)
        };
    }

    internal Win32API.WINDOWPLACEMENT ToNativeForRestore(int width, int height)
    {
        Win32API.WINDOWPLACEMENT placement = ToNative();
        placement.Flags = 0;
        placement.ShowCmd = Win32API.ShowWindowCommands.Normal;
        placement.NormalPosition.Width = width;
        placement.NormalPosition.Height = height;
        return placement;
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

    ExternalWindowPlacement CaptureWindowPlacement(ExternalWindowHandle childWindow);

    void ApplyWindowPlacement(ExternalWindowHandle childWindow, ExternalWindowPlacement placement, int width, int height);
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

    public ExternalWindowPlacement CaptureWindowPlacement(ExternalWindowHandle childWindow)
    {
        Win32API.WINDOWPLACEMENT placement = default;
        Win32API.GetWindowPlacement(childWindow.NativeValue, ref placement);
        return new ExternalWindowPlacement(
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

    public void ApplyWindowPlacement(ExternalWindowHandle childWindow, ExternalWindowPlacement placement, int width, int height)
    {
        Win32API.WINDOWPLACEMENT nativePlacement = placement.ToNativeForRestore(width, height);
        Win32API.SetWindowPlacement(childWindow.NativeValue, ref nativePlacement);
    }
}
