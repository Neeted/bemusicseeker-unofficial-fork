using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SettingsWindowPresentationTests
{
    [TestMethod]
    public void SettingsWindow_IsStandardResizableWindow()
    {
        RunOnStaThread(() =>
        {
            var window = new SettingsWindow();

            Assert.IsInstanceOfType<Window>(window);
            Assert.AreEqual(ResizeMode.CanResize, window.ResizeMode);
            Assert.AreEqual(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
            Assert.IsFalse(window.ShowInTaskbar);
            Assert.AreEqual(920d, window.Width);
            Assert.AreEqual(680d, window.Height);
            Assert.AreEqual(760d, window.MinWidth);
            Assert.AreEqual(500d, window.MinHeight);
        });
    }

    [TestMethod]
    public void SettingsWindow_NavigationHasTenLocalizedCategoriesInStableOrder()
    {
        XDocument document = LoadSettingsWindowXaml();
        XElement navigation = FindNamedElement(document, "settingsNavigation");
        List<XElement> items = navigation.Elements(PresentationName("ListBoxItem")).ToList();
        string[] expectedResourcePaths =
        [
            "Resources.General",
            "Resources.Appearance",
            "Resources.Playback",
            "Resources.Device",
            "Resources.Record",
            "Resources.Playlist",
            "Resources.Install",
            "Resources.Backup",
            "Resources.Details",
            "Resources.Version_info"
        ];

        Assert.AreEqual(10, items.Count);
        CollectionAssert.AreEqual(
            expectedResourcePaths,
            items.Select(item => ExtractResourcePath(item.Attribute("Content")?.Value)).ToArray());
        Assert.AreEqual("0", navigation.Attribute("SelectedIndex")?.Value);
    }

    [TestMethod]
    public void SettingsWindow_SelectionControlsPageVisibilityAndHeader()
    {
        XDocument document = LoadSettingsWindowXaml();
        XElement header = document.Descendants(PresentationName("ContentControl"))
            .Single(element => element.Attribute("Content")?.Value.Contains("SelectedItem.Content", StringComparison.Ordinal) == true);
        string[] navigationNames =
        [
            "navigationGeneral",
            "navigationAppearance",
            "navigationPlayback",
            "navigationDevice",
            "navigationRecord",
            "navigationPlaylist",
            "navigationInstall",
            "navigationBackup",
            "navigationDetails",
            "navigationVersionInfo"
        ];

        StringAssert.Contains(header.Attribute("Content")!.Value, "ElementName=settingsNavigation");
        foreach (string navigationName in navigationNames)
        {
            Assert.AreEqual(
                1,
                document.Descendants().Count(element =>
                    element.Attribute("Visibility")?.Value.Contains("ElementName=" + navigationName, StringComparison.Ordinal) == true),
                navigationName + " must control exactly one settings page.");
        }
    }

    [TestMethod]
    public void SettingsWindow_KeepsNavigationHeaderAndFooterOutsideSinglePageScrollViewer()
    {
        XDocument document = LoadSettingsWindowXaml();
        XElement pageScroller = FindNamedElement(document, "settingsPageScrollViewer");
        XElement saveButton = FindNamedElement(document, "buttonOK");
        XElement cancelButton = FindNamedElement(document, "buttonCancel");

        Assert.IsFalse(document.Descendants(PresentationName("TabControl")).Any());
        Assert.IsFalse(document.Descendants(PresentationName("TabItem")).Any());
        Assert.IsFalse(document.Descendants(PresentationName("ControlTemplate"))
            .Any(element => element.Attribute("TargetType")?.Value.Contains("ListBoxItem", StringComparison.Ordinal) == true));
        Assert.AreEqual("Auto", pageScroller.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.AreEqual("Disabled", pageScroller.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.IsFalse(saveButton.Ancestors(PresentationName("ScrollViewer")).Any());
        Assert.IsFalse(cancelButton.Ancestors(PresentationName("ScrollViewer")).Any());
        Assert.IsFalse(FindNamedElement(document, "settingsNavigation").Ancestors(PresentationName("ScrollViewer")).Any());

        XElement versionDocument = document.Descendants(PresentationName("FlowDocumentScrollViewer")).Single();
        Assert.IsNull(versionDocument.Attribute("Height"));
        Assert.AreEqual("Disabled", versionDocument.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.AreEqual("Disabled", versionDocument.Attribute("HorizontalScrollBarVisibility")?.Value);
    }

    [TestMethod]
    public void SettingsWindow_ActionsAndNavigationExposeLocalizedAccessibleContracts()
    {
        XDocument document = LoadSettingsWindowXaml();
        XElement navigation = FindNamedElement(document, "settingsNavigation");
        XElement saveButton = FindNamedElement(document, "buttonOK");
        XElement cancelButton = FindNamedElement(document, "buttonCancel");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.AreEqual("SettingsCategoryNavigation", navigation.Attribute(presentation + "AutomationProperties.AutomationId")?.Value ?? navigation.Attribute("AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual("SettingsSaveAndClose", saveButton.Attribute("AutomationProperties.AutomationId")?.Value);
        Assert.AreEqual("SettingsCancel", cancelButton.Attribute("AutomationProperties.AutomationId")?.Value);
        StringAssert.Contains(saveButton.Attribute("Content")!.Value, "Resources.Save_and_close");
        StringAssert.Contains(cancelButton.Attribute("Content")!.Value, "Resources.Cancel");
    }

    [TestMethod]
    public void MainWindow_PresentsFreshOwnedModalThroughCoordinator()
    {
        string root = FindRepositoryRoot();
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();

        Assert.IsFalse(mainWindowXaml.Contains("<v:SettingsWindow", StringComparison.Ordinal));
        StringAssert.Contains(mainWindowCode, "new UiWindowDialogRequest<SettingsWindow, SettingsWindowCloseReason>(");
        StringAssert.Contains(mainWindowCode, "settingsWindow = new SettingsWindow");
        StringAssert.Contains(mainWindowCode, "DataContext = viewModel.SettingDialog");
        StringAssert.Contains(mainWindowCode, "PlaybackPanel = viewModel.PlaybackPanel");
        StringAssert.Contains(mainWindowCode, "PlaylistWorkspace = viewModel.PlaylistWorkspace");
        StringAssert.Contains(mainWindowCode, "settingsWindow.Activate();");
        StringAssert.Contains(mainWindowCode, "PlaybackOverlayVisibility = Visibility.Visible;");
        StringAssert.Contains(mainWindowCode, "PlaybackOverlayVisibility = previousPlaybackOverlayVisibility;");
    }

    [TestMethod]
    public void SettingsWindow_CloseLifecycleSeparatesUserAndProgrammaticRoutes()
    {
        string root = FindRepositoryRoot();
        string settingsWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingsWindow.cs"));

        StringAssert.Contains(settingsWindowCode, "if (!settingDialogViewModel.IsEditCancellationEnabled || viewOperationInProgress || userCancellationQueued)");
        StringAssert.Contains(settingsWindowCode, "settingDialogViewModel.CancelCommand.Execute();");
        StringAssert.Contains(settingsWindowCode, "internal void CloseFromPresentation()");
        StringAssert.Contains(settingsWindowCode, "internal void CloseForOwnerShutdown()");
        StringAssert.Contains(settingsWindowCode, "private void CloseForManualResync()");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.Apply");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.Cancel");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.Presentation");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.ManualResync");
        StringAssert.Contains(settingsWindowCode, "SettingsWindowCloseReason.OwnerShutdown");
    }

    [TestMethod]
    public void NativeClose_WhenCancellationIsEnabled_RequestsCancelCompletion()
    {
        RunOnStaThread(() =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var presentation = new RecordingPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = viewModel.SettingDialog };

            Assert.IsTrue(SimulateNativeClose(window));
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

            Assert.AreEqual(1, presentation.CloseRequestCount);
        });
    }

    [TestMethod]
    public void NativeClose_WhenCancellationIsDisabled_DoesNotRequestCancelCompletion()
    {
        RunOnStaThread(() =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var presentation = new RecordingPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            typeof(SettingsDialogViewModel)
                .GetField("scoreReloadPending", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel.SettingDialog, true);
            var window = new SettingsWindow { DataContext = viewModel.SettingDialog };

            Assert.IsTrue(SimulateNativeClose(window));
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

            Assert.AreEqual(0, presentation.CloseRequestCount);
        });
    }

    [TestMethod]
    public void RunViewOperation_BlocksNativeCloseUntilOperationCompletes()
    {
        RunOnStaThread(() =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var presentation = new RecordingPresentationPort();
            viewModel.SettingDialog.AttachPresentationPort(presentation);
            var window = new SettingsWindow { DataContext = viewModel.SettingDialog };
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task operation = window.RunViewOperationAsync(() => operationCompletion.Task);

                Assert.IsTrue(SimulateNativeClose(window));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.AreEqual(0, presentation.CloseRequestCount);

                operationCompletion.SetResult();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.IsTrue(operation.IsCompletedSuccessfully);

                Assert.IsTrue(SimulateNativeClose(window));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.AreEqual(1, presentation.CloseRequestCount);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
    }

    [TestMethod]
    public void ApplyOperation_RejectsNativeCloseUntilPresentationSuccessAuthorizesApplyClose()
    {
        RunOnStaThread(() =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            SettingsDialogViewModel settings = viewModel.SettingDialog;
            var window = new SettingsWindow { DataContext = settings };
            SynchronizationContext previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var applyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool closed = false;
                var closeCancellations = new List<bool>();
                window.Closing += (_, args) => closeCancellations.Add(args.Cancel);
                window.Closed += (_, _) => closed = true;

                Task applyOperation = window.RunApplyOperationAsync(async () =>
                {
                    SetEditCompletionInProgress(settings, true);
                    try
                    {
                        await applyRelease.Task;
                        window.CloseFromPresentation();
                    }
                    finally
                    {
                        SetEditCompletionInProgress(settings, false);
                    }
                });

                Assert.IsFalse(applyOperation.IsCompleted);
                Assert.IsTrue(SimulateNativeClose(window));
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.AreEqual(SettingsWindowCloseReason.None, window.CloseReason);
                Assert.IsFalse(closed);

                applyRelease.SetResult();
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

                Assert.IsTrue(applyOperation.IsCompletedSuccessfully);
                Assert.AreEqual(SettingsWindowCloseReason.Apply, window.CloseReason);
                Assert.IsTrue(closed);
                CollectionAssert.AreEqual(new[] { true, false }, closeCancellations);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
    }

    [TestMethod]
    public void SettingsWindow_PresentationWorkFollowsWindowLifetime()
    {
        string root = FindRepositoryRoot();
        string settingsWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingsWindow.cs"));

        StringAssert.Contains(settingsWindowCode, "protected override void OnContentRendered(EventArgs e)");
        StringAssert.Contains(settingsWindowCode, "settingDialogViewModel.SetPresentationActive(true);");
        StringAssert.Contains(settingsWindowCode, "RefreshAppearanceThemeSelection(settingDialogViewModel);");
        StringAssert.Contains(settingsWindowCode, "settingDialogViewModel.RefreshLr2PlayHistorySchemaStatusPresentation();");
        StringAssert.Contains(settingsWindowCode, "\"settings_dialog_open\"");
        StringAssert.Contains(settingsWindowCode, "protected override void OnClosed(EventArgs e)");
        StringAssert.Contains(settingsWindowCode, "settingDialogViewModel.SetPresentationActive(false);");
    }

    private static void RunOnStaThread(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw failure;
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "BeMusicSeeker.sln")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static XDocument LoadSettingsWindowXaml()
    {
        return XDocument.Load(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingsWindow.xaml"));
    }

    private static XElement FindNamedElement(XDocument document, string name)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == name || element.Attribute("Name")?.Value == name);
    }

    private static XName PresentationName(string localName)
    {
        return XName.Get(localName, "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
    }

    private static string ExtractResourcePath(string binding)
    {
        const string marker = "Path=";
        int start = binding?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (start < 0)
        {
            return string.Empty;
        }
        start += marker.Length;
        int end = binding.IndexOf(',', start);
        return end < 0 ? binding[start..].TrimEnd('}') : binding[start..end];
    }

    private static bool SimulateNativeClose(SettingsWindow window)
    {
        var args = new System.ComponentModel.CancelEventArgs();
        typeof(SettingsWindow)
            .GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [args]);
        return args.Cancel;
    }

    private static void SetEditCompletionInProgress(SettingsDialogViewModel viewModel, bool value)
    {
        typeof(SettingsDialogViewModel)
            .GetField("isEditCompletionInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }

    private sealed class RecordingPresentationPort : ISettingDialogPresentationPort
    {
        internal int CloseRequestCount { get; private set; }

        public void OpenSettingsDialog()
        {
        }

        public void OpenInitialSetupLanguageDialog()
        {
        }

        public void CloseSettingsDialog()
        {
            CloseRequestCount++;
        }

        public void RefreshAppearanceSelection()
        {
        }
    }
}
