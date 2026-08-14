using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfTestApplicationHostTests
{
    [TestMethod]
    public void Invoke_ReusesSingleApplicationDispatcherAndStaThread()
    {
        Application? firstApplication = null;
        Dispatcher? firstDispatcher = null;
        Thread? firstThread = null;
        ApartmentState firstApartmentState = ApartmentState.Unknown;
        TestUiDispatcherHost.Invoke(() =>
        {
            firstApplication = Application.Current;
            firstDispatcher = Dispatcher.CurrentDispatcher;
            firstThread = Thread.CurrentThread;
            firstApartmentState = Thread.CurrentThread.GetApartmentState();
        });

        Application? secondApplication = null;
        Dispatcher? secondDispatcher = null;
        Thread? secondThread = null;
        TestUiDispatcherHost.Invoke(() =>
        {
            secondApplication = Application.Current;
            secondDispatcher = Dispatcher.CurrentDispatcher;
            secondThread = Thread.CurrentThread;
        });

        Assert.IsNotNull(firstApplication);
        Assert.AreSame(firstApplication, secondApplication);
        Assert.AreSame(firstDispatcher, secondDispatcher);
        Assert.AreSame(TestUiDispatcherHost.Dispatcher, firstDispatcher);
        Assert.AreSame(firstThread, secondThread);
        Assert.AreEqual(ApartmentState.STA, firstApartmentState);
    }

    [TestMethod]
    public void Startup_AppliesLightComponentThemeWithSemanticResources()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Assert.IsNotNull(application);
            ResourceDictionary theme = application.Resources.MergedDictionaries.Single(dictionary =>
                string.Equals(
                    dictionary.Source?.OriginalString,
                    "/BeMusicSeeker;component/Themes/Light.xaml",
                    StringComparison.OrdinalIgnoreCase));

            Assert.IsInstanceOfType<SolidColorBrush>(theme["App.BackgroundBrush"]);
            Assert.IsInstanceOfType<SolidColorBrush>(theme["App.TextBrush"]);
            Assert.AreEqual(AppThemeService.Light, BeMusicSeeker.Properties.Settings.Default.AppearanceTheme);
        });
    }

    [TestMethod]
    public void RunWindowTest_NonActivatingPresentationStaysOffscreenAndCleansWindowAndPopupHwnds()
    {
        var closeEvents = new List<string>();
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var target = new Button { Content = "target" };
            var window = new Window
            {
                Width = 320,
                Height = 200,
                Content = target
            };
            window.Closed += (_, _) => closeEvents.Add("owner");
            scope.ShowAndWaitForContentRendered(window);
            nint windowHandle = TestWindowPresentationScope.GetNativeHandle(window);

            var popup = new Popup
            {
                PlacementTarget = target,
                Child = new Border { Width = 80, Height = 40 }
            };
            popup.Closed += (_, _) => closeEvents.Add("popup");
            scope.TrackPopup(popup);
            popup.IsOpen = true;
            TestUiDispatcherHost.Drain();
            nint popupHandle = TestWindowPresentationScope.GetNativeHandle(popup.Child);

            Assert.AreNotEqual(0, windowHandle);
            Assert.AreNotEqual(0, popupHandle);
            Assert.IsTrue(TestWindowPresentationScope.IsOutsideAllMonitors(windowHandle));
            Assert.AreNotEqual(windowHandle, TestWindowPresentationScope.ForegroundWindow);
            Assert.IsTrue(TestWindowPresentationScope.IsOutsideAllMonitors(popupHandle));
            Assert.AreNotEqual(popupHandle, TestWindowPresentationScope.ForegroundWindow);

            var child = new Window
            {
                Owner = window,
                Width = 160,
                Height = 100,
                Content = new Border { Width = 40, Height = 20 }
            };
            child.Closed += (_, _) => closeEvents.Add("child");
            scope.ShowAndWaitForContentRendered(child);
        });

        CollectionAssert.AreEqual(new[] { "popup", "child", "owner" }, closeEvents);
    }

    [TestMethod]
    public void RunWindowTest_FailurePriorityIsBodyThenPresentationThenCleanup()
    {
        var expected = new InvalidOperationException("body failure");
        var expectedPresentation = new InvalidOperationException("presentation failure");
        var expectedCleanup = new InvalidOperationException("cleanup failure");

        InvalidOperationException? actual = null;
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                var target = new Button { Content = "target" };
                var window = new Window { Width = 320, Height = 200, Content = target };
                scope.ShowAndWaitForContentRendered(window);
                Assert.AreNotEqual(0, TestWindowPresentationScope.GetNativeHandle(window));

                var popup = new Popup
                {
                    PlacementTarget = target,
                    Child = new Border { Width = 80, Height = 40 }
                };
                scope.TrackPopup(popup);
                popup.IsOpen = true;
                TestUiDispatcherHost.Drain();
                Assert.AreNotEqual(0, TestWindowPresentationScope.GetNativeHandle(popup.Child));
                scope.RegisterPresentationFailureForTesting(expectedPresentation);
                scope.RegisterCleanupFailureForTesting(expectedCleanup);
                throw expected;
            });
        }
        catch (InvalidOperationException ex)
        {
            actual = ex;
        }

        Assert.IsNotNull(actual);
        Assert.AreSame(expected, actual);
        Assert.AreEqual(
            expectedPresentation.ToString(),
            actual.Data["TestWindowPresentationFailure"]);
        Assert.AreEqual(
            expectedCleanup.ToString(),
            actual.Data["TestWindowPresentationCleanupFailure"]);

        Exception? presentationAndCleanupFailure = null;
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                scope.RegisterPresentationFailureForTesting(expectedPresentation);
                scope.RegisterCleanupFailureForTesting(expectedCleanup);
            });
        }
        catch (Exception ex)
        {
            presentationAndCleanupFailure = ex;
        }

        Assert.IsNotNull(presentationAndCleanupFailure);
        Assert.AreSame(expectedPresentation, presentationAndCleanupFailure);
        Assert.AreEqual(
            expectedCleanup.ToString(),
            presentationAndCleanupFailure.Data["TestWindowPresentationCleanupFailure"]);

        var presentationOnly = new InvalidOperationException("presentation only failure");
        Exception? presentationOnlyFailure = null;
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
                scope.RegisterPresentationFailureForTesting(presentationOnly));
        }
        catch (Exception ex)
        {
            presentationOnlyFailure = ex;
        }

        Assert.IsNotNull(presentationOnlyFailure);
        Assert.AreSame(presentationOnly, presentationOnlyFailure);

        Exception? cleanupOnlyFailure = null;
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                scope.RegisterCleanupFailureForTesting(expectedCleanup);
            });
        }
        catch (Exception ex)
        {
            cleanupOnlyFailure = ex;
        }

        Assert.IsNotNull(cleanupOnlyFailure);
        Assert.AreSame(expectedCleanup, cleanupOnlyFailure);
    }
}
