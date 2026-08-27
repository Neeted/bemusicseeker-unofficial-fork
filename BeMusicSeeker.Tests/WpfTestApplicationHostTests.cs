using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views.Settings;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parago.Windows;

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
    public void CompiledDialogSurfaces_ResolveSemanticBrushesOnConstructorOnlyControls()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var settingsWindow = new SettingsWindow();
            Grid settingsOperationRoot = (Grid)settingsWindow.FindName("settingDialogOperationGrid");
            AssertBrush(settingsOperationRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            AssertBrush(settingsWindow.Background, "App.DialogBackgroundBrush");
            var warningField = new SettingsField
            {
                Header = "header",
                ValidationMessage = "warning",
                Content = new TextBox(),
                Style = (Style)settingsWindow.FindResource(typeof(SettingsField))
            };
            warningField.ApplyTemplate();
            TextBlock warningText = FindVisualDescendantsInVisualTree<TextBlock>(warningField)
                .Single(textBlock => textBlock.Text == "warning");
            AssertBrush(warningText.Foreground, "App.WarningTextBrush");

            var initialSetupDialog = new InitialSetupLanguageDialog();
            Grid initialSetupRoot = FindVisualDescendants<Grid>(initialSetupDialog).Single();
            Rectangle overlay = FindVisualDescendants<Rectangle>(initialSetupDialog).Single();
            Border initialSetupSurface = FindVisualDescendants<Border>(initialSetupDialog).Single();
            AssertBrush(initialSetupRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            AssertBrush(overlay.Fill, "App.DialogOverlayBrush");
            AssertBrush(initialSetupSurface.Background, "App.DialogBackgroundBrush");
            AssertBrush(initialSetupSurface.BorderBrush, "App.DialogBorderBrush");

            var playlistPropertyDialog = new PlaylistPropertyDialog();
            Grid playlistPropertyRoot = FindVisualDescendants<Grid>(playlistPropertyDialog)
                .Single(grid => FindResourceInScope(grid.Resources, "App.Canonical.NativeWindowContentStyle") is Style);
            AssertBrush(playlistPropertyRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            Border playlistPropertyContent = FindElementWithScopedStyle<Border>(
                playlistPropertyRoot,
                "App.Canonical.NativeWindowContentStyle");
            Assert.IsNull(playlistPropertyContent.Background);
            Assert.IsNull(playlistPropertyContent.BorderBrush);

            var loadPlaylistDialog = new LoadPlaylistURIDialog();
            Grid loadPlaylistRoot = FindVisualDescendants<Grid>(loadPlaylistDialog)
                .Single(grid => FindResourceInScope(grid.Resources, "App.Canonical.DialogContentStyle") is Style);
            AssertBrush(loadPlaylistRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            Rectangle loadPlaylistOverlay = FindElementWithScopedStyle<Rectangle>(
                loadPlaylistRoot,
                "App.Canonical.DialogOverlayStyle");
            Border loadPlaylistContent = FindElementWithScopedStyle<Border>(
                loadPlaylistRoot,
                "App.Canonical.DialogContentStyle");
            AssertBrush(loadPlaylistOverlay.Fill, "App.DialogOverlayBrush");
            AssertBrush(loadPlaylistContent.Background, "App.DialogBackgroundBrush");
            AssertBrush(loadPlaylistContent.BorderBrush, "App.DialogBorderBrush");

            var pendingDeleteDialog = new PendingDeleteConfirmDialog();
            AssertBrush(pendingDeleteDialog.Background, "App.DialogBackgroundBrush");
            AssertBrush(pendingDeleteDialog.Foreground, "App.TextBrush");

            var progressDialog = new ProgressDialog(new ProgressDialogSettings());
            AssertBrush(progressDialog.Background, "App.DialogBackgroundBrush");
            AssertBrush(progressDialog.Foreground, "App.TextBrush");
            Border progressDialogContent = FindElementWithScopedStyle<Border>(
                progressDialog,
                "App.Canonical.NativeWindowContentStyle");
            Assert.IsNull(progressDialogContent.Background);
            Assert.IsNull(progressDialogContent.BorderBrush);
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
            Assert.IsTrue(
                TestWindowPresentationScope.HasNoActivateStyle(windowHandle),
                "The owner HWND must retain WS_EX_NOACTIVATE.");
            Assert.IsTrue(
                TestWindowPresentationScope.HasNoActivateStyle(popupHandle),
                "The popup HWND must retain WS_EX_NOACTIVATE.");
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
            nint childHandle = TestWindowPresentationScope.GetNativeHandle(child);
            Assert.AreNotEqual(0, childHandle);
            Assert.IsTrue(
                TestWindowPresentationScope.HasNoActivateStyle(childHandle),
                "The owned child HWND must retain WS_EX_NOACTIVATE.");
            Assert.IsTrue(TestWindowPresentationScope.IsOutsideAllMonitors(childHandle));
            Assert.AreNotEqual(childHandle, TestWindowPresentationScope.ForegroundWindow);
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

    private static void AssertBrush(object value, string resourceKey)
    {
        Assert.IsInstanceOfType(value, typeof(SolidColorBrush), resourceKey + " must resolve to a SolidColorBrush.");
        Assert.AreSame(
            Application.Current.Resources[resourceKey],
            value,
            resourceKey + " must resolve through the application semantic resource.");
    }

    private static T FindElementWithScopedStyle<T>(FrameworkElement scope, object resourceKey)
        where T : FrameworkElement
    {
        object? resource = FindResourceInScope(scope.Resources, resourceKey);
        Assert.IsInstanceOfType(resource, typeof(Style), resourceKey + " must resolve from the dialog-local resource facade.");
        Style expectedStyle = (Style)resource;

        T[] matches = FindVisualDescendants<T>(scope)
            .Where(element => ReferenceEquals(element.Style, expectedStyle))
            .ToArray();
        Assert.AreEqual(
            1,
            matches.Length,
            resourceKey + " must identify exactly one applied canonical role surface in its dialog scope.");
        return matches[0];
    }

    private static object? FindResourceInScope(ResourceDictionary resources, object key)
    {
        // Keep application fallback out of this lookup so a missing dialog-local facade cannot pass accidentally.
        if (resources.Contains(key))
        {
            return resources[key];
        }

        foreach (ResourceDictionary mergedDictionary in resources.MergedDictionaries.Reverse())
        {
            object? value = FindResourceInScope(mergedDictionary, key);
            if (value != null)
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (object childValue in LogicalTreeHelper.GetChildren(root))
        {
            if (childValue is not DependencyObject child)
            {
                continue;
            }

            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<T> FindVisualDescendantsInVisualTree<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendantsInVisualTree<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
