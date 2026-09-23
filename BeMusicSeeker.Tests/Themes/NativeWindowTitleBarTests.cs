using System;
using System.Windows.Media;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class NativeWindowTitleBarTests
{
    [TestMethod]
    public void Attach_AppliesCurrentSemanticAppearanceAndReappliesThemeChanges()
    {
        NativeWindowTitleBarAppearance appearance = DefaultAppearance();
        var themeSource = new RecordingThemeSource(appearance);
        var gateway = new RecordingGateway();
        using var controller = new NativeWindowTitleBarController(gateway, themeSource);
        IntPtr handle = new IntPtr(42);

        controller.Attach(handle);
        themeSource.RaiseThemeChanged();

        Assert.AreEqual(1, themeSource.SubscriberCount);
        Assert.AreEqual(2, gateway.ApplyCount);
        Assert.AreEqual(handle, gateway.LastHandle);
        Assert.AreEqual(appearance, gateway.LastAppearance);
    }

    [TestMethod]
    public void Dispose_DetachesExactlyOnceAndStopsNativeUpdates()
    {
        var themeSource = new RecordingThemeSource(DefaultAppearance());
        var gateway = new RecordingGateway();
        var controller = new NativeWindowTitleBarController(gateway, themeSource);
        controller.Attach(new IntPtr(42));

        controller.Dispose();
        controller.Dispose();
        themeSource.RaiseThemeChanged();

        Assert.AreEqual(1, themeSource.RemoveCount);
        Assert.AreEqual(0, themeSource.SubscriberCount);
        Assert.AreEqual(1, gateway.ApplyCount);
    }

    [TestMethod]
    public void Attach_WithUnexpectedThemeFailure_RollsBackAndCanRetry()
    {
        var themeSource = new RecordingThemeSource(DefaultAppearance()) { ThrowOnRead = true };
        var gateway = new RecordingGateway();
        var controller = new NativeWindowTitleBarController(gateway, themeSource);

        InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(() => controller.Attach(new IntPtr(42)));

        Assert.AreEqual("Theme resources are unavailable.", error.Message);
        Assert.AreEqual(0, themeSource.SubscriberCount);
        Assert.AreEqual(1, themeSource.RemoveCount);
        Assert.AreEqual(0, gateway.ApplyAttemptCount);

        themeSource.ThrowOnRead = false;
        controller.Attach(new IntPtr(84));

        Assert.AreEqual(1, themeSource.SubscriberCount);
        Assert.AreEqual(1, gateway.ApplyAttemptCount);
        Assert.AreEqual(new IntPtr(84), gateway.LastHandle);

        controller.Dispose();
        controller.Dispose();

        Assert.AreEqual(0, themeSource.SubscriberCount);
        Assert.AreEqual(2, themeSource.RemoveCount);
    }

    [TestMethod]
    public void Attach_WithUnexpectedGatewayFailure_RollsBackAndCanRetry()
    {
        var themeSource = new RecordingThemeSource(DefaultAppearance());
        var gateway = new RecordingGateway { ThrowOnApply = true };
        var controller = new NativeWindowTitleBarController(gateway, themeSource);

        InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(() => controller.Attach(new IntPtr(42)));

        Assert.AreEqual("Unexpected native failure.", error.Message);
        Assert.AreEqual(0, themeSource.SubscriberCount);
        Assert.AreEqual(1, themeSource.RemoveCount);
        Assert.AreEqual(1, gateway.ApplyAttemptCount);

        gateway.ThrowOnApply = false;
        controller.Attach(new IntPtr(84));

        Assert.AreEqual(1, themeSource.SubscriberCount);
        Assert.AreEqual(2, gateway.ApplyAttemptCount);
        Assert.AreEqual(new IntPtr(84), gateway.LastHandle);

        controller.Dispose();
        controller.Dispose();
        themeSource.RaiseThemeChanged();

        Assert.AreEqual(0, themeSource.SubscriberCount);
        Assert.AreEqual(2, themeSource.RemoveCount);
        Assert.AreEqual(2, gateway.ApplyAttemptCount);
    }

    [TestMethod]
    public void ThemeChange_WithUnexpectedGatewayFailure_Propagates()
    {
        var themeSource = new RecordingThemeSource(DefaultAppearance());
        var gateway = new RecordingGateway();
        using var controller = new NativeWindowTitleBarController(gateway, themeSource);
        controller.Attach(new IntPtr(42));
        gateway.ThrowOnApply = true;

        InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(themeSource.RaiseThemeChanged);

        Assert.AreEqual("Unexpected native failure.", error.Message);
        Assert.AreEqual(1, themeSource.SubscriberCount);

        gateway.ThrowOnApply = false;
        themeSource.RaiseThemeChanged();

        Assert.AreEqual(3, gateway.ApplyAttemptCount);
        Assert.AreEqual(2, gateway.ApplyCount);
        Assert.AreEqual(1, themeSource.SubscriberCount);
    }

    [TestMethod]
    public void Attach_RejectsUninitializedHandleWithoutSubscribing()
    {
        var themeSource = new RecordingThemeSource(DefaultAppearance());
        var gateway = new RecordingGateway();
        using var controller = new NativeWindowTitleBarController(gateway, themeSource);

        controller.Attach(IntPtr.Zero);

        Assert.AreEqual(0, themeSource.SubscriberCount);
        Assert.AreEqual(0, gateway.ApplyCount);
    }

    [TestMethod]
    public void Attach_WhenAlreadyAttached_DoesNotReplaceHandleOrDuplicateSubscription()
    {
        var themeSource = new RecordingThemeSource(DefaultAppearance());
        var gateway = new RecordingGateway();
        using var controller = new NativeWindowTitleBarController(gateway, themeSource);

        controller.Attach(new IntPtr(42));
        controller.Attach(new IntPtr(84));
        themeSource.RaiseThemeChanged();

        Assert.AreEqual(1, themeSource.SubscriberCount);
        Assert.AreEqual(2, gateway.ApplyCount);
        Assert.AreEqual(new IntPtr(42), gateway.LastHandle);
    }

    private static NativeWindowTitleBarAppearance DefaultAppearance()
    {
        return new NativeWindowTitleBarAppearance(
            false,
            Colors.White,
            Colors.Black,
            Color.FromRgb(0x82, 0x87, 0x90));
    }

    private sealed class RecordingGateway : INativeWindowTitleBarGateway
    {
        internal int ApplyCount { get; private set; }

        internal int ApplyAttemptCount { get; private set; }

        internal IntPtr LastHandle { get; private set; }

        internal NativeWindowTitleBarAppearance LastAppearance { get; private set; }

        internal bool ThrowOnApply { get; set; }

        public void Apply(IntPtr windowHandle, NativeWindowTitleBarAppearance appearance)
        {
            ApplyAttemptCount++;
            if (ThrowOnApply)
            {
                throw new InvalidOperationException("Unexpected native failure.");
            }

            ApplyCount++;
            LastHandle = windowHandle;
            LastAppearance = appearance;
        }
    }

    private sealed class RecordingThemeSource : INativeWindowTitleBarThemeSource
    {
        private EventHandler? themeChanged;
        private readonly NativeWindowTitleBarAppearance appearance;

        internal RecordingThemeSource(NativeWindowTitleBarAppearance appearance)
        {
            this.appearance = appearance;
        }

        public event EventHandler ThemeChanged
        {
            add
            {
                themeChanged += value;
                SubscriberCount++;
            }
            remove
            {
                themeChanged -= value;
                SubscriberCount--;
                RemoveCount++;
            }
        }

        internal int SubscriberCount { get; private set; }

        internal int RemoveCount { get; private set; }

        internal bool ThrowOnRead { get; set; }

        public NativeWindowTitleBarAppearance GetAppearance()
        {
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Theme resources are unavailable.");
            }

            return appearance;
        }

        internal void RaiseThemeChanged()
        {
            themeChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
