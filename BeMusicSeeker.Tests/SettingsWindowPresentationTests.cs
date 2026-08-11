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
    public void SettingsWindow_UsesScopedThemeAwareVisualHierarchy()
    {
        XDocument document = LoadSettingsWindowXaml();
        XElement operationGrid = FindNamedElement(document, "settingDialogOperationGrid");
        XElement navigation = FindNamedElement(document, "settingsNavigation");
        XElement header = document.Descendants(PresentationName("ContentControl"))
            .Single(element => element.Attribute("Content")?.Value.Contains("SelectedItem.Content", StringComparison.Ordinal) == true);
        XElement saveButton = FindNamedElement(document, "buttonOK");
        XElement cancelButton = FindNamedElement(document, "buttonCancel");
        XElement navigationStyle = FindKeyedStyle(document, "settingsNavigationItemStyle");
        XElement primaryActionStyle = FindKeyedStyle(document, "settingsPrimaryActionButtonStyle");
        XElement quietActionStyle = FindKeyedStyle(document, "settingsQuietActionButtonStyle");
        XElement groupBoxStyle = document.Descendants(PresentationName("Style"))
            .Single(style => style.Attribute("TargetType")?.Value == "{x:Type GroupBox}" && style.Attribute(XamlName("Key")) == null);
        XElement labelStyle = document.Descendants(PresentationName("Style"))
            .Single(style => style.Attribute("TargetType")?.Value == "{x:Type Label}" && style.Attribute(XamlName("Key")) == null);
        XElement checkBoxStyle = document.Descendants(PresentationName("Style"))
            .Single(style => style.Attribute("TargetType")?.Value == "{x:Type CheckBox}" && style.Attribute(XamlName("Key")) == null);
        XElement radioButtonStyle = document.Descendants(PresentationName("Style"))
            .Single(style => style.Attribute("TargetType")?.Value == "{x:Type RadioButton}" && style.Attribute(XamlName("Key")) == null);

        Assert.IsTrue(operationGrid.Descendants(PresentationName("Style")).Contains(navigationStyle));
        Assert.AreEqual("0,16", navigation.Attribute("Padding")?.Value);
        Assert.AreEqual("26", header.Attribute("FontSize")?.Value);
        Assert.AreEqual("{StaticResource settingsPrimaryActionButtonStyle}", saveButton.Attribute("Style")?.Value);
        Assert.AreEqual("{StaticResource settingsQuietActionButtonStyle}", cancelButton.Attribute("Style")?.Value);
        AssertStyleSetter(primaryActionStyle, "Background", "{DynamicResource App.AccentBrush}");
        AssertStyleSetter(primaryActionStyle, "Foreground", "{DynamicResource Table.CurrentCellTextBrush}");
        AssertStyleMultiTriggerSetter(primaryActionStyle, "IsMouseOver", "Foreground", "{DynamicResource App.TextBrush}");
        AssertStyleMultiTriggerSetter(primaryActionStyle, "IsPressed", "Foreground", "{DynamicResource App.TextBrush}");
        XElement[] primaryTriggers = primaryActionStyle
            .Element(PresentationName("Style.Triggers"))!
            .Elements()
            .ToArray();
        XElement disabledPrimaryTrigger = primaryTriggers[^1];
        Assert.AreEqual("Trigger", disabledPrimaryTrigger.Name.LocalName);
        Assert.AreEqual("IsEnabled", disabledPrimaryTrigger.Attribute("Property")?.Value);
        Assert.AreEqual("False", disabledPrimaryTrigger.Attribute("Value")?.Value);
        Assert.IsTrue(disabledPrimaryTrigger.Elements(PresentationName("Setter"))
            .Any(setter => setter.Attribute("Property")?.Value == "Foreground"
                && setter.Attribute("Value")?.Value == "{DynamicResource App.DisabledTextBrush}"));
        AssertStyleSetter(quietActionStyle, "Background", "Transparent");

        XElement navigationPill = navigationStyle.Descendants(PresentationName("Border"))
            .Single(element => element.Attribute(XamlName("Name"))?.Value == "NavigationPill");
        XElement selectionIndicator = navigationStyle.Descendants(PresentationName("Border"))
            .Single(element => element.Attribute(XamlName("Name"))?.Value == "SelectionIndicator");
        Assert.AreEqual("8", navigationPill.Attribute("CornerRadius")?.Value);
        Assert.AreEqual("2", selectionIndicator.Attribute("CornerRadius")?.Value);
        Assert.IsTrue(navigationStyle.Descendants(PresentationName("Trigger"))
            .Any(trigger => trigger.Attribute("Property")?.Value == "IsKeyboardFocusWithin" && trigger.Attribute("Value")?.Value == "True"));
        Assert.IsTrue(navigationStyle.Descendants(PresentationName("Trigger"))
            .Any(trigger => trigger.Attribute("Property")?.Value == "IsEnabled" && trigger.Attribute("Value")?.Value == "False"));

        XElement card = groupBoxStyle.Descendants(PresentationName("Border"))
            .Single(element => element.Attribute(XamlName("Name"))?.Value == "SettingsCard");
        Assert.AreEqual("12", card.Attribute("CornerRadius")?.Value);
        AssertStyleSetter(groupBoxStyle, "Margin", "0,0,0,16");
        AssertStyleSetter(groupBoxStyle, "Padding", "16,12,16,16");
        AssertStyleSetter(groupBoxStyle, "Background", "{DynamicResource App.ControlBackgroundBrush}");
        Assert.IsTrue(groupBoxStyle.Descendants(PresentationName("Trigger"))
            .Any(trigger => trigger.Attribute("Property")?.Value == "IsEnabled" && trigger.Attribute("Value")?.Value == "False"));
        Assert.IsTrue(labelStyle.Descendants(PresentationName("TextBlock"))
            .Any(textBlock => textBlock.Attribute("TextWrapping")?.Value == "Wrap"));
        Assert.IsTrue(checkBoxStyle.Descendants(PresentationName("TextBlock"))
            .Any(textBlock => textBlock.Attribute("TextWrapping")?.Value == "Wrap"));
        Assert.IsTrue(radioButtonStyle.Descendants(PresentationName("TextBlock"))
            .Any(textBlock => textBlock.Attribute("TextWrapping")?.Value == "Wrap"));

        string[] localTemplateTargets = operationGrid.Descendants(PresentationName("ControlTemplate"))
            .Select(template => template.Attribute("TargetType")?.Value ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "{x:Type GroupBox}", "{x:Type ListBoxItem}" }, localTemplateTargets);
        foreach (string standardControlType in new[] { "Button", "TextBox", "ComboBox", "CheckBox", "RadioButton" })
        {
            XElement standardControlStyle = operationGrid.Descendants(PresentationName("Style"))
                .Single(style => style.Attribute("TargetType")?.Value == "{x:Type " + standardControlType + "}" && style.Attribute(XamlName("Key")) == null);
            Assert.AreEqual("{StaticResource {x:Type " + standardControlType + "}}", standardControlStyle.Attribute("BasedOn")?.Value);
        }
    }

    [TestMethod]
    public void SettingsWindow_FieldLayoutsUseFlexibleLabelValueAndActionColumns()
    {
        XDocument document = LoadSettingsWindowXaml();
        List<XElement> fieldGrids = document.Descendants(PresentationName("Grid"))
            .Where(grid => grid.Attribute("Style")?.Value == "{StaticResource settingsFieldGridStyle}")
            .ToList();

        Assert.IsTrue(fieldGrids.Count >= 24, "Every settings category with label/value/action fields must use responsive grids.");
        foreach (XElement fieldGrid in fieldGrids)
        {
            List<XElement> columns = fieldGrid.Elements(PresentationName("Grid.ColumnDefinitions"))
                .SelectMany(definitions => definitions.Elements(PresentationName("ColumnDefinition")))
                .ToList();
            Assert.IsTrue(columns.Count is 2 or 3, fieldGrid.ToString(SaveOptions.DisableFormatting));
            Assert.IsTrue(columns[0].Attribute("Width")?.Value.Contains('*', StringComparison.Ordinal) == true);
            Assert.IsTrue(columns[1].Attribute("Width")?.Value.Contains('*', StringComparison.Ordinal) == true);
        }

        XElement fieldGridStyle = FindKeyedStyle(document, "settingsFieldGridStyle");
        AssertStyleSetter(fieldGridStyle, "Margin", "0,0,0,8");

        Assert.IsFalse(document.Descendants(PresentationName("Label")).Any(label => label.Attribute("Height") != null),
            "Localized labels must use auto height so wrapped content is not clipped.");
        Assert.IsFalse(document.Descendants().Any(element => element.Attribute("Width")?.Value is "229" or "315"),
            "Legacy paired field widths exceed the minimum-size content viewport.");

        string[] responsiveFieldResources =
        [
            "Resources.DirPath_LR2",
            "Resources.Player_uBMplay_desc",
            "Resources.Device_setting_driver",
            "Resources.Record_setting_filetype",
            "Resources.Playlist_output",
            "Resources.Install_Dst",
            "Resources.Backup_lr2backup_saveto"
        ];
        foreach (string resourcePath in responsiveFieldResources)
        {
            XElement fieldContent = document.Descendants()
                .Single(element => element.Attributes().Any(attribute =>
                    attribute.Value.Contains("Path=" + resourcePath + ",", StringComparison.Ordinal)));
            Assert.IsTrue(fieldContent.AncestorsAndSelf(PresentationName("Grid"))
                .Any(grid => grid.Attribute("Style")?.Value == "{StaticResource settingsFieldGridStyle}"),
                resourcePath + " must be hosted by a responsive field grid.");
        }

        Dictionary<string, int> expectedInteractiveElementCounts = new(StringComparer.Ordinal)
        {
            ["Button"] = 32,
            ["CheckBox"] = 46,
            ["RadioButton"] = 9,
            ["TextBox"] = 20,
            ["ComboBox"] = 15,
            ["Slider"] = 7,
            ["ListBox"] = 4,
            ["GroupBox"] = 31,
            ["Expander"] = 1
        };
        foreach ((string elementName, int expectedCount) in expectedInteractiveElementCounts)
        {
            Assert.AreEqual(expectedCount, document.Descendants(PresentationName(elementName)).Count(), elementName);
        }

        foreach (string playerName in new[] { "radioButtonPlayuBMplay", "radioButtonPlayBMIIDXView", "radioButtonPlayLR2body" })
        {
            XElement playerGrid = FindNamedElement(document, playerName)
                .Ancestors(PresentationName("Grid"))
                .First(grid => grid.Attribute("Style")?.Value == "{StaticResource settingsFieldGridStyle}");
            XElement pathTextBox = playerGrid.Descendants(PresentationName("TextBox"))
                .Single(textBox => textBox.Attribute("Grid.Row")?.Value == "0");
            Assert.IsNull(pathTextBox.Attribute("Width"), playerName + " path must not inherit another field's ActualWidth.");
            Assert.AreEqual("Stretch", pathTextBox.Attribute("HorizontalAlignment")?.Value);
        }

        XElement additionalOutputName = document.Descendants(PresentationName("Label"))
            .Single(label => label.Attribute("Content")?.Value.Contains("Path=Resources.Playlist_output_additional_name,", StringComparison.Ordinal) == true);
        XElement renameGrid = additionalOutputName.Parent!;
        Assert.AreEqual("Grid", renameGrid.Name.LocalName);
        Assert.AreEqual("1", renameGrid.Attribute("Grid.Row")?.Value);
        XElement additionalOutputLayout = renameGrid.Parent!;
        Assert.IsFalse(additionalOutputLayout.Elements(PresentationName("Grid.ColumnDefinitions")).Any());
        XElement[] additionalOutputRows = additionalOutputLayout
            .Elements(PresentationName("Grid.RowDefinitions"))
            .Single()
            .Elements(PresentationName("RowDefinition"))
            .ToArray();
        Assert.AreEqual(2, additionalOutputRows.Length);
        CollectionAssert.AreEqual(
            new[] { "Auto", "Auto" },
            additionalOutputRows.Select(row => row.Attribute("Height")?.Value).ToArray());

        XElement additionalOutputListAndActions = additionalOutputLayout.Elements(PresentationName("Grid"))
            .Single(grid => grid.Attribute("Grid.Row")?.Value == "0");
        XElement[] listAndActionColumns = additionalOutputListAndActions
            .Elements(PresentationName("Grid.ColumnDefinitions"))
            .Single()
            .Elements(PresentationName("ColumnDefinition"))
            .ToArray();
        Assert.AreEqual(2, listAndActionColumns.Length);
        CollectionAssert.AreEqual(
            new[] { "*", "Auto" },
            listAndActionColumns.Select(column => column.Attribute("Width")?.Value).ToArray());

        XElement additionalOutputList = additionalOutputListAndActions.Elements(PresentationName("ListBox")).Single();
        XElement additionalOutputActions = additionalOutputListAndActions.Elements(PresentationName("StackPanel")).Single();
        Assert.AreEqual("0", additionalOutputList.Attribute("Grid.Column")?.Value);
        Assert.AreEqual("1", additionalOutputActions.Attribute("Grid.Column")?.Value);
        Assert.AreEqual("92", additionalOutputActions.Attribute("Width")?.Value);
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

    private static XName XamlName(string localName)
    {
        return XName.Get(localName, "http://schemas.microsoft.com/winfx/2006/xaml");
    }

    private static XElement FindKeyedStyle(XDocument document, string key)
    {
        return document.Descendants(PresentationName("Style"))
            .Single(style => style.Attribute(XamlName("Key"))?.Value == key);
    }

    private static void AssertStyleSetter(XElement style, string property, string value)
    {
        Assert.IsTrue(style.Elements(PresentationName("Setter"))
            .Any(setter => setter.Attribute("Property")?.Value == property && setter.Attribute("Value")?.Value == value),
            property + "=" + value);
    }

    private static void AssertStyleMultiTriggerSetter(
        XElement style,
        string stateProperty,
        string setterProperty,
        string setterValue)
    {
        Assert.IsTrue(style.Descendants(PresentationName("MultiTrigger"))
            .Where(trigger =>
            {
                XElement[] conditions = trigger
                    .Element(PresentationName("MultiTrigger.Conditions"))!
                    .Elements(PresentationName("Condition"))
                    .ToArray();
                return conditions.Any(condition => condition.Attribute("Property")?.Value == "IsEnabled" && condition.Attribute("Value")?.Value == "True")
                    && conditions.Any(condition => condition.Attribute("Property")?.Value == stateProperty && condition.Attribute("Value")?.Value == "True");
            })
            .SelectMany(trigger => trigger.Elements(PresentationName("Setter")))
            .Any(setter => setter.Attribute("Property")?.Value == setterProperty && setter.Attribute("Value")?.Value == setterValue),
            "IsEnabled=True + " + stateProperty + "=True, " + setterProperty + "=" + setterValue);
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
