using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using BeMusicSeeker.Views.Settings;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using ManagedBass;
using Ribbit.Media;
using Ribbit.Media.Audio;

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Views.Settings.Pages;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingsForegroundInteractionTests
{
    [TestMethod]
    public void SettingsWindow_NavigationSupportsKeyboardAutomationAndResetsPageScroll()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow
            {
                DataContext = viewModel.SettingDialog,
                Width = 820,
                Height = 600
            };
            try
            {
                windowTest.ShowAndWaitForContentRendered(
                    window,
                    TestWindowActivation.ForegroundInteraction);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var scroller = (ScrollViewer)window.FindName("settingsPageScrollViewer");
                var header = (ContentControl)window.FindName("settingsPageHeader");
                var pageHost = (ContentControl)window.FindName("settingsPageContent");

                var firstItem = (ListBoxItem)navigation.Items[0];
                Assert.AreEqual(SelectionMode.Single, navigation.SelectionMode);
                Assert.AreEqual(KeyboardNavigationMode.Continue, KeyboardNavigation.GetDirectionalNavigation(navigation));
                Assert.IsTrue(firstItem.Focusable, "Navigation items must participate in keyboard focus traversal.");
                window.Activate();
                Assert.IsTrue(firstItem.Focus(), "The displayed navigation item must accept keyboard focus.");
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(firstItem.IsKeyboardFocusWithin);
                RaiseKey(firstItem, Key.Down);
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(1, navigation.SelectedIndex);
                Assert.IsInstanceOfType<AppearanceSettingsPage>(pageHost.Content);
                Assert.AreEqual(((ListBoxItem)navigation.SelectedItem).Content, header.Content);
                ((FrameworkElement)pageHost.Content).Height = 1200;

                var secondItem = (ListBoxItem)navigation.Items[1];
                Assert.IsTrue(secondItem.IsKeyboardFocusWithin);
                RaiseKey(secondItem, Key.Up);
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(0, navigation.SelectedIndex);
                Assert.IsInstanceOfType<GeneralSettingsPage>(pageHost.Content);
                Assert.AreEqual(((ListBoxItem)navigation.SelectedItem).Content, header.Content);

                foreach (ListBoxItem item in navigation.Items)
                {
                    Assert.IsFalse(
                        string.IsNullOrWhiteSpace(AutomationProperties.GetName(item)),
                        "Every settings category must expose a localized automation name.");
                    StringAssert.StartsWith(AutomationProperties.GetAutomationId(item), "SettingsCategory");
                }

                var navigationPeer = new ListBoxAutomationPeer(navigation);
                var selectionProvider = (ISelectionProvider)navigationPeer.GetPattern(PatternInterface.Selection);
                Assert.IsNotNull(selectionProvider);
                Assert.IsFalse(selectionProvider.CanSelectMultiple);
                AutomationPeer playbackPeer = navigationPeer.GetChildren()
                    .Single(peer => peer.GetAutomationId() == "SettingsCategoryPlayback");
                var playbackSelection = (ISelectionItemProvider)playbackPeer.GetPattern(PatternInterface.SelectionItem);
                Assert.IsNotNull(playbackSelection);
                playbackSelection.Select();
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(2, navigation.SelectedIndex);
                Assert.IsInstanceOfType<PlaybackSettingsPage>(pageHost.Content);
                Assert.AreEqual(((ListBoxItem)navigation.SelectedItem).Content, header.Content);
                Assert.IsTrue(playbackSelection.IsSelected);

                navigation.SelectedIndex = 0;
                var selectedPage = (FrameworkElement)pageHost.Content;
                selectedPage.Height = 1200;
                window.UpdateLayout();
                Assert.IsTrue(scroller.ScrollableHeight > 0, "The shell body must expose overflow through its shared scroller.");
                scroller.ScrollToVerticalOffset(Math.Min(100d, scroller.ScrollableHeight));
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(scroller.VerticalOffset > 0, "The body scroller must accept a non-zero page offset.");
                navigation.SelectedIndex = 1;
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(0d, scroller.VerticalOffset, "Changing categories must reset the shared body scroller.");
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    [TestMethod]
    public void SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            Window window = null;
            try
            {
                Grid host = CreateSettingsControlHost();
                var panel = new StackPanel { Width = 300 };
                var selectionCombo = new ComboBox
                {
                    ItemsSource = new[] { "First", "Second" },
                    SelectedIndex = 0,
                    Width = 300
                };
                var editableCombo = new ComboBox
                {
                    ItemsSource = new[] { "First", "Second" },
                    IsEditable = true,
                    Text = "First",
                    Width = 300,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                panel.Children.Add(selectionCombo);
                panel.Children.Add(editableCombo);
                host.Children.Add(panel);
                window = new Window
                {
                    Content = host,
                    Width = 360,
                    Height = 160
                };
                windowTest.ShowAndWaitForContentRendered(
                    window,
                    TestWindowActivation.ForegroundInteraction);

                selectionCombo.ApplyTemplate();
                editableCombo.ApplyTemplate();
                var selectionToggle = (ToggleButton)selectionCombo.Template.FindName("DropDownToggle", selectionCombo);
                var editableToggle = (ToggleButton)editableCombo.Template.FindName("DropDownToggle", editableCombo);
                var editor = (TextBox)editableCombo.Template.FindName("PART_EditableTextBox", editableCombo);
                var selectionPopup = (Popup)selectionCombo.Template.FindName("PART_Popup", selectionCombo);
                var editablePopup = (Popup)editableCombo.Template.FindName("PART_Popup", editableCombo);
                windowTest.TrackPopup(selectionPopup);
                windowTest.TrackPopup(editablePopup);

                foreach (double x in new[] { 6d, selectionCombo.ActualWidth / 2d, selectionCombo.ActualWidth - 6d })
                {
                    IInputElement hit = selectionCombo.InputHitTest(new Point(x, selectionCombo.ActualHeight / 2d));
                    Assert.IsTrue(IsVisualDescendantOf(hit as DependencyObject, selectionToggle),
                        $"The noneditable ComboBox hit at x={x} did not route to the full-span dropdown toggle.");
                }

                IInputElement editableTextHit = editableCombo.InputHitTest(new Point(12, editableCombo.ActualHeight / 2d));
                IInputElement editableArrowHit = editableCombo.InputHitTest(new Point(editableCombo.ActualWidth - 6d, editableCombo.ActualHeight / 2d));
                Assert.IsTrue(IsVisualDescendantOf(editableTextHit as DependencyObject, editor));
                Assert.IsTrue(IsVisualDescendantOf(editableArrowHit as DependencyObject, editableToggle));
                Assert.AreSame(editor, editableCombo.Template.FindName("PART_EditableTextBox", editableCombo));
                Assert.AreSame(editablePopup, editableCombo.Template.FindName("PART_Popup", editableCombo));

                editor.Focus();
                editor.CaretIndex = editor.Text.Length;
                var composition = new TextComposition(InputManager.Current, editor, "Z");
                editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                {
                    RoutedEvent = TextCompositionManager.TextInputEvent
                });
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(editor.IsKeyboardFocused);
                Assert.AreEqual("FirstZ", editor.Text);
                Assert.AreEqual(editor.Text.Length, editor.CaretIndex);

                var selectionPeer = new ComboBoxAutomationPeer(selectionCombo);
                var selectionProvider = (IExpandCollapseProvider)selectionPeer.GetPattern(PatternInterface.ExpandCollapse);
                Assert.IsNotNull(selectionProvider);
                selectionProvider.Expand();
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(selectionPopup.IsOpen);
                RaiseKey(selectionCombo, Key.Down);
                RaiseKey(selectionCombo, Key.Enter);
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(1, selectionCombo.SelectedIndex);
                Assert.IsFalse(selectionPopup.IsOpen);

                var editablePeer = new ComboBoxAutomationPeer(editableCombo);
                var editableProvider = (IExpandCollapseProvider)editablePeer.GetPattern(PatternInterface.ExpandCollapse);
                Assert.IsNotNull(editableProvider);
                editableProvider.Expand();
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(editablePopup.IsOpen);
                editableProvider.Collapse();
                PumpDispatcher(window.Dispatcher);
                Assert.IsFalse(editablePopup.IsOpen);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [TestMethod]
    public void SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            const string sentinel = "OuterImplicitStyleSentinel";
            Application application = Application.Current;
            Assert.IsNotNull(application);
            Type[] sentinelTypes =
            [
                typeof(ScrollViewer),
                typeof(ScrollBar),
                typeof(ComboBoxItem),
                typeof(ListBox),
                typeof(ListBoxItem),
                typeof(Expander)
            ];
            var previousResources = new Dictionary<Type, object>();
            foreach (Type type in sentinelTypes)
            {
                if (application.Resources.Contains(type))
                {
                    previousResources[type] = application.Resources[type];
                }

                var sentinelStyle = new Style(type);
                sentinelStyle.Setters.Add(new Setter(FrameworkElement.TagProperty, sentinel));
                application.Resources[type] = sentinelStyle;
            }

            Window window = null;
            try
            {
                Grid host = CreateSettingsControlHost();
                Style scrollViewerStyle = (Style)host.Resources["SettingsScrollViewerStyle"];
                Style scrollBarStyle = (Style)host.Resources["SettingsScrollBarStyle"];
                Style comboBoxItemStyle = (Style)host.Resources["SettingsComboBoxItemStyle"];
                Style listBoxStyle = (Style)host.Resources["SettingsListBoxStyle"];
                Style listBoxItemStyle = (Style)host.Resources["SettingsListBoxItemStyle"];
                Style expanderStyle = (Style)host.Resources["SettingsExpanderStyle"];

                var pageScroller = new ScrollViewer
                {
                    Content = new Border { Width = 800, Height = 800 },
                    Width = 180,
                    Height = 100,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Visible
                };
                var textBox = new TextBox { Text = "settings" };
                var comboBox = new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
                var listBox = new ListBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 1, Height = 64 };
                var expander = new Expander { Header = "Details", Content = new TextBlock { Text = "Content" } };
                var slider = new Slider { Minimum = 0, Maximum = 10, TickFrequency = 1, TickPlacement = TickPlacement.TopLeft, Width = 180 };
                var primaryButton = new Button { Content = "Save", Style = (Style)host.Resources["SettingsPrimaryButtonStyle"] };
                var panel = new StackPanel();
                panel.Children.Add(pageScroller);
                panel.Children.Add(textBox);
                panel.Children.Add(comboBox);
                panel.Children.Add(listBox);
                panel.Children.Add(expander);
                panel.Children.Add(slider);
                panel.Children.Add(primaryButton);
                host.Children.Add(panel);

                window = new Window
                {
                    Content = host,
                    Width = 420,
                    Height = 520
                };
                windowTest.ShowAndWaitForContentRendered(
                    window,
                    TestWindowActivation.ForegroundInteraction);

                AssertLocalImplicitStyle(pageScroller.Style, scrollViewerStyle, nameof(ScrollViewer));
                AssertLocalImplicitStyle(listBox.Style, listBoxStyle, nameof(ListBox));
                Assert.AreSame(listBoxItemStyle, listBox.ItemContainerStyle);
                Assert.AreSame(comboBoxItemStyle, comboBox.ItemContainerStyle);
                AssertLocalImplicitStyle(expander.Style, expanderStyle, nameof(Expander));
                foreach (FrameworkElement element in new FrameworkElement[] { pageScroller, comboBox, listBox, expander })
                {
                    Assert.AreNotEqual(sentinel, element.Tag, element.GetType().Name);
                }

                pageScroller.ApplyTemplate();
                var verticalScrollBar = (ScrollBar)pageScroller.Template.FindName("PART_VerticalScrollBar", pageScroller);
                Assert.IsNotNull(pageScroller.Template.FindName("PART_ScrollContentPresenter", pageScroller));
                Assert.AreSame(scrollBarStyle, verticalScrollBar.Style);
                Assert.AreNotEqual(sentinel, verticalScrollBar.Tag);
                verticalScrollBar.ApplyTemplate();
                Assert.IsNotNull(verticalScrollBar.Template.FindName("PART_Track", verticalScrollBar));
                var scrollPeer = new ScrollViewerAutomationPeer(pageScroller);
                var scrollProvider = (IScrollProvider)scrollPeer.GetPattern(PatternInterface.Scroll);
                Assert.IsNotNull(scrollProvider);
                Assert.IsTrue(scrollProvider.VerticallyScrollable);
                double initialOffset = pageScroller.VerticalOffset;
                scrollProvider.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(pageScroller.VerticalOffset > initialOffset);
                Assert.AreEqual(pageScroller.VerticalOffset, verticalScrollBar.Value, 0.01d);

                textBox.ApplyTemplate();
                var textContentHost = (ScrollViewer)textBox.Template.FindName("PART_ContentHost", textBox);
                Assert.AreSame(scrollViewerStyle, textContentHost.Style);
                Assert.AreNotEqual(sentinel, textContentHost.Tag);

                comboBox.ApplyTemplate();
                var popup = (Popup)comboBox.Template.FindName("PART_Popup", comboBox);
                windowTest.TrackPopup(popup);
                comboBox.Focus();
                comboBox.IsDropDownOpen = true;
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(popup.IsOpen);
                Assert.IsTrue(comboBox.IsDropDownOpen);
                ScrollViewer popupScroller = FindDescendant<ScrollViewer>(popup.Child);
                Assert.IsNotNull(popupScroller);
                Assert.AreSame(scrollViewerStyle, popupScroller.Style);
                var firstComboItem = (ComboBoxItem)comboBox.ItemContainerGenerator.ContainerFromIndex(0);
                var secondComboItem = (ComboBoxItem)comboBox.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.IsNotNull(firstComboItem);
                Assert.IsNotNull(secondComboItem);
                Assert.AreSame(comboBoxItemStyle, firstComboItem.Style);
                Assert.AreSame(comboBoxItemStyle, secondComboItem.Style);
                Assert.AreNotEqual(sentinel, firstComboItem.Tag);
                firstComboItem.ApplyTemplate();
                Assert.IsNotNull(firstComboItem.Template.FindName("ItemChrome", firstComboItem));

                var comboPeer = new ComboBoxAutomationPeer(comboBox);
                var comboExpandProvider = (IExpandCollapseProvider)comboPeer.GetPattern(PatternInterface.ExpandCollapse);
                Assert.IsNotNull(comboExpandProvider);
                Assert.AreEqual(ExpandCollapseState.Expanded, comboExpandProvider.ExpandCollapseState);
                RaiseKey(comboBox, Key.Down);
                PumpDispatcher(window.Dispatcher);
                RaiseKey(comboBox, Key.Enter);
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(1, comboBox.SelectedIndex);
                Assert.AreEqual("Second", comboBox.SelectedItem);
                Assert.IsTrue(secondComboItem.IsSelected);
                Assert.IsFalse(comboBox.IsDropDownOpen);
                Assert.IsFalse(popup.IsOpen);
                Assert.AreEqual(ExpandCollapseState.Collapsed, comboExpandProvider.ExpandCollapseState);

                listBox.ApplyTemplate();
                ScrollViewer listScroller = FindDescendant<ScrollViewer>(listBox);
                Assert.IsNotNull(listScroller);
                Assert.AreSame(scrollViewerStyle, listScroller.Style);
                Assert.AreEqual(1, listBox.SelectedIndex);
                var firstListItem = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0);
                var secondListItem = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.IsNotNull(firstListItem);
                Assert.IsNotNull(secondListItem);
                Assert.AreSame(listBoxItemStyle, firstListItem.Style);
                Assert.AreSame(listBoxItemStyle, secondListItem.Style);
                Assert.AreNotEqual(sentinel, secondListItem.Tag);
                Assert.IsTrue(secondListItem.IsSelected);
                secondListItem.ApplyTemplate();
                var selectedListChrome = (Border)secondListItem.Template.FindName("ItemChrome", secondListItem);
                AssertBrushColor(host, "App.ControlSelectedBrush", selectedListChrome.Background);

                var listPeer = new ListBoxAutomationPeer(listBox);
                var selectionProvider = (ISelectionProvider)listPeer.GetPattern(PatternInterface.Selection);
                Assert.IsNotNull(selectionProvider);
                Assert.IsFalse(selectionProvider.CanSelectMultiple);
                Assert.AreEqual(1, selectionProvider.GetSelection().Length);
                AutomationPeer firstListItemPeer = listPeer.GetChildren().Single(peer => peer.GetName() == "First");
                var selectionItemProvider = (ISelectionItemProvider)firstListItemPeer.GetPattern(PatternInterface.SelectionItem);
                Assert.IsNotNull(selectionItemProvider);
                Assert.IsFalse(selectionItemProvider.IsSelected);
                selectionItemProvider.Select();
                PumpDispatcher(window.Dispatcher);
                Assert.AreEqual(0, listBox.SelectedIndex);
                Assert.IsTrue(firstListItem.IsSelected);
                Assert.IsFalse(secondListItem.IsSelected);
                Assert.IsTrue(selectionItemProvider.IsSelected);

                expander.ApplyTemplate();
                var headerSite = (ToggleButton)expander.Template.FindName("HeaderSite", expander);
                headerSite.IsChecked = true;
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                Assert.IsTrue(expander.IsExpanded);
                var expanderPeer = new ExpanderAutomationPeer(expander);
                var expanderProvider = (IExpandCollapseProvider)expanderPeer.GetPattern(PatternInterface.ExpandCollapse);
                Assert.IsNotNull(expanderProvider);
                Assert.AreEqual(ExpandCollapseState.Expanded, expanderProvider.ExpandCollapseState);
                expanderProvider.Collapse();
                PumpDispatcher(window.Dispatcher);
                Assert.IsFalse(expander.IsExpanded);
                Assert.AreEqual(ExpandCollapseState.Collapsed, expanderProvider.ExpandCollapseState);
                expanderProvider.Expand();
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(expander.IsExpanded);

                slider.ApplyTemplate();
                var topTickBar = (TickBar)slider.Template.FindName("TopTickBar", slider);
                var bottomTickBar = (TickBar)slider.Template.FindName("BottomTickBar", slider);
                Assert.AreEqual(Visibility.Visible, topTickBar.Visibility);
                Assert.AreEqual(Visibility.Collapsed, bottomTickBar.Visibility);
                Assert.IsNotNull(slider.Template.FindName("PART_Track", slider));
                AssertBrushColor(host, "App.SliderTickBrush", topTickBar.Fill);
                slider.TickPlacement = TickPlacement.Both;
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                Assert.AreEqual(Visibility.Visible, topTickBar.Visibility);
                Assert.AreEqual(Visibility.Visible, bottomTickBar.Visibility);

                primaryButton.Focus();
                window.Dispatcher.Invoke(DispatcherPriority.Input, new Action(() => { }));
                var focusBorder = (Border)primaryButton.Template.FindName("FocusBorder", primaryButton);
                Assert.AreEqual(Visibility.Visible, focusBorder.Visibility);
                AssertBrushColor(host, "App.AccentFocusRingBrush", focusBorder.BorderBrush);
                Color focusColor = ((SolidColorBrush)focusBorder.BorderBrush).Color;
                foreach (string accentState in new[] { "App.AccentBrush", "App.AccentHoverBrush", "App.AccentPressedBrush" })
                {
                    Color stateColor = ((SolidColorBrush)host.TryFindResource(accentState)).Color;
                    Assert.IsTrue(ContrastRatio(focusColor, stateColor) >= 3d, accentState);
                }

                primaryButton.IsEnabled = false;
                AssertBrushColor(host, "App.AccentBrush", primaryButton.Background);
                AssertBrushColor(host, "App.AccentForegroundBrush", primaryButton.Foreground);
            }
            finally
            {
                window?.Close();
                foreach (Type type in sentinelTypes)
                {
                    application.Resources.Remove(type);
                    if (previousResources.TryGetValue(type, out object previous))
                    {
                        application.Resources[type] = previous;
                    }
                }

            }
        });
    }

    [DataTestMethod]
    [DataRow(nameof(SettingsDialogViewModel.LR2SongDBPath))]
    [DataRow(nameof(SettingsDialogViewModel.LR2ConfigXmlPath))]
    public void Lr2AdvancedPathsDialog_EnterCommitsFocusedEditorBeforeAccepting(string propertyName)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "standard");
            (string originalSong, string originalConfig) = CreateValidLr2Layout(root);
            string candidateSong = Path.Combine(scope, "custom", "database", "songs.db");
            string candidateConfig = Path.Combine(scope, "custom", "configuration", "config.xmh");
            Directory.CreateDirectory(Path.GetDirectoryName(candidateSong)!);
            Directory.CreateDirectory(Path.GetDirectoryName(candidateConfig)!);
            File.WriteAllBytes(candidateSong, []);
            File.WriteAllText(candidateConfig, "<config><system /><jukebox /></config>");

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = originalSong;
            values.LR2ConfigXmlPath = originalConfig;
            var session = new CountingSettingsEditSession(values);
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;
            string label = propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath)
                ? Resources.FilePath_songDB
                : Resources.FilePath_configXml;
            string candidate = propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath)
                ? candidateSong
                : candidateConfig;

            bool? result = null;
            TestUiDispatcherHost.RunWindowTest(windowTest =>
                {
                    var advancedDialog = new Lr2AdvancedPathsDialog(draft);
                    advancedDialog.ContentRendered += (_, _) =>
                    {
                        TextBox editor = GetLr2AdvancedPathEditor(advancedDialog, label);
                        Assert.IsTrue(editor.Focus());
                        editor.Text = candidate;
                        Assert.AreNotEqual(candidate, propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath)
                            ? draft.LR2SongDBPath
                            : draft.LR2ConfigXmlPath);

                        InvokeEnterAccessKey();
                    };
                    windowTest.PrepareForOwnedPresentation(
                        advancedDialog,
                        TestWindowActivation.ForegroundInteraction);
                    result = advancedDialog.ShowDialog();
                });

            Assert.AreEqual(true, result);
            Assert.AreEqual(
                candidate,
                propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath)
                    ? draft.LR2SongDBPath
                    : draft.LR2ConfigXmlPath);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(nameof(SettingsDialogViewModel.LR2SongDBPath))]
    [DataRow(nameof(SettingsDialogViewModel.LR2ConfigXmlPath))]
    public void Lr2AdvancedPathsDialog_EnterKeepsDialogOpenWhenFocusedCandidateIsRejected(string propertyName)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "standard");
            (string originalSong, string originalConfig) = CreateValidLr2Layout(root);
            string invalidSong = Path.Combine(scope, "missing", "song.db");
            string invalidConfig = Path.Combine(scope, "malformed", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(invalidConfig)!);
            File.WriteAllText(invalidConfig, "<config>");

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = originalSong;
            values.LR2ConfigXmlPath = originalConfig;
            var session = new CountingSettingsEditSession(values);
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;
            string label = propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath)
                ? Resources.FilePath_songDB
                : Resources.FilePath_configXml;
            string candidate = propertyName == nameof(SettingsDialogViewModel.LR2SongDBPath)
                ? invalidSong
                : invalidConfig;

            bool stayedOpen = false;
            bool rawDraftWasRetained = false;
            bool rejectionErrorWasSet = false;
            bool failureWasVisible = false;
            TestUiDispatcherHost.RunWindowTest(windowTest =>
                {
                    var advancedDialog = new Lr2AdvancedPathsDialog(draft);
                    advancedDialog.ContentRendered += (_, _) =>
                    {
                        TextBox editor = GetLr2AdvancedPathEditor(advancedDialog, label);
                        Assert.IsTrue(editor.Focus());
                        editor.Text = candidate;

                        InvokeEnterAccessKey();
                        advancedDialog.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                        stayedOpen = advancedDialog.IsVisible;
                        rawDraftWasRetained = string.Equals(originalSong, draft.LR2SongDBPath, StringComparison.Ordinal)
                            && string.Equals(originalConfig, draft.LR2ConfigXmlPath, StringComparison.Ordinal);
                        rejectionErrorWasSet = draft.HasLr2PathSelectionError;
                        failureWasVisible = FindDescendants<SettingsStatusBanner>(advancedDialog)
                            .Any(banner => banner.Visibility == Visibility.Visible
                                && Equals(banner.Content, draft.Lr2PathSelectionError));
                        advancedDialog.Close();
                    };
                    windowTest.PrepareForOwnedPresentation(
                        advancedDialog,
                        TestWindowActivation.ForegroundInteraction);
                    advancedDialog.ShowDialog();
                });

            Assert.IsTrue(stayedOpen);
            Assert.IsTrue(rawDraftWasRetained);
            Assert.IsTrue(rejectionErrorWasSet);
            Assert.IsTrue(failureWasVisible);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public void Lr2AdvancedPathsDialog_InitialInvalidTupleStaysOpenAndFocusesRejectedEditor(
        bool missingInitialSong,
        bool useEnter)
    {
        string scope = CreateTemporaryRoot();
        try
        {
            string root = Path.Combine(scope, "standard");
            (string validSong, string validConfig) = CreateValidLr2Layout(root);
            string initialSong = missingInitialSong
                ? Path.Combine(scope, "missing", "song.db")
                : validSong;
            string initialConfig = validConfig;
            if (!missingInitialSong)
            {
                initialConfig = Path.Combine(scope, "malformed", "config.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(initialConfig)!);
                File.WriteAllText(initialConfig, "<config>");
            }

            Settings values = CreateValidStandaloneSettings(scope);
            values.LR2RootPath = root;
            values.LR2SongDBPath = initialSong;
            values.LR2ConfigXmlPath = initialConfig;
            var session = new CountingSettingsEditSession(values);
            SettingsDialogViewModel draft = CreateViewModel(session, firstStartup: false).SettingDialog;
            string rejectedLabel = missingInitialSong ? Resources.FilePath_songDB : Resources.FilePath_configXml;
            string otherLabel = missingInitialSong ? Resources.FilePath_configXml : Resources.FilePath_songDB;

            bool stayedOpen = false;
            bool rejectedEditorFocused = false;
            bool rejectionErrorWasSet = false;
            TestUiDispatcherHost.RunWindowTest(windowTest =>
                {
                    var advancedDialog = new Lr2AdvancedPathsDialog(draft);
                    advancedDialog.ContentRendered += (_, _) =>
                    {
                        TextBox rejectedEditor = GetLr2AdvancedPathEditor(advancedDialog, rejectedLabel);
                        Assert.IsTrue(GetLr2AdvancedPathEditor(advancedDialog, otherLabel).Focus());
                        if (useEnter)
                        {
                            InvokeEnterAccessKey();
                        }
                        else
                        {
                            FindDescendants<Button>(advancedDialog)
                                .Single(button => button.IsDefault)
                                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        }

                        advancedDialog.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                        stayedOpen = advancedDialog.IsVisible;
                        rejectedEditorFocused = rejectedEditor.IsKeyboardFocusWithin;
                        rejectionErrorWasSet = draft.HasLr2PathSelectionError;
                        advancedDialog.Close();
                    };
                    windowTest.PrepareForOwnedPresentation(
                        advancedDialog,
                        TestWindowActivation.ForegroundInteraction);
                    advancedDialog.ShowDialog();
                });

            Assert.IsTrue(stayedOpen);
            Assert.IsTrue(rejectedEditorFocused);
            Assert.IsTrue(rejectionErrorWasSet);
            Assert.AreEqual(initialSong, draft.LR2SongDBPath);
            Assert.AreEqual(initialConfig, draft.LR2ConfigXmlPath);
            Assert.AreEqual(0, session.SaveCount);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialogEditCompletion_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static (string SongDb, string Config) CreateValidLr2Layout(string root)
    {
        string songDb = Path.Combine(root, "LR2files", "Database", "song.db");
        string config = Path.Combine(root, "LR2files", "Config", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(songDb)!);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllBytes(songDb, []);
        File.WriteAllText(config, "<config><system /><jukebox /></config>");
        File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);
        return (songDb, config);
    }

    private static MainWindowViewModel CreateViewModel(
        CountingSettingsEditSession settingsSession,
        bool firstStartup,
        Func<MainWindowViewModel, Task<bool>>? initializeOwner = null,
        Func<MainWindowViewModel, Task>? reloadScoresOnly = null,
        Action<Exception>? reportSettingsApplyFailure = null,
        Func<MainWindowViewModel, Task>? reloadFileDiff = null,
        ISettingsDialogPlayerFactoryPort? playerFactoryPort = null,
        ISettingsDialogPlaybackRuntimePort? playbackRuntimePort = null,
        IUiDialogService? dialogs = null)
    {
        var composition = new ApplicationComposition(
            settingsEditSession: settingsSession,
            reportSettingsApplyFailure: reportSettingsApplyFailure ?? (_ => { }),
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        if (initializeOwner != null || reloadScoresOnly != null || reloadFileDiff != null)
        {
            SettingsDialogViewModel testDialog = new(
                new TestSettingsDialogStatePort(
                    viewModel,
                    initializeOwner == null
                        ? () => viewModel.InitializeAsync()
                        : () => initializeOwner(viewModel),
                    () =>
                    {
                        SetPrivateField(viewModel, "initializationCompleted", false);
                        SetPrivateField(viewModel, "hasActiveLibraryProfile", false);
                    },
                    reloadScoresOnly: reloadScoresOnly == null
                        ? () => Task.CompletedTask
                        : () => reloadScoresOnly(viewModel),
                    reloadFileDiff: reloadFileDiff == null
                        ? () => Task.CompletedTask
                        : () => reloadFileDiff(viewModel)),
                viewModel.PlaylistWorkspace,
                viewModel.PlaylistWorkspace,
                viewModel.PlayHistory,
                viewModel.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                viewModel.Lr2SongDbSyncWorkflow,
                settingsSession,
                applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                schemaDialogs: dialogs,
                reportApplyFailure: reportSettingsApplyFailure ?? (_ => { }),
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(),
                audioSettingsGateway: new TestAudioSettingsGateway(),
                audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
            typeof(MainWindowViewModel)
                .GetProperty("SettingDialog", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(viewModel, testDialog);
        }
        typeof(SettingsDialogViewModel)
            .GetField("playerFactoryPort", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel.SettingDialog, playerFactoryPort ?? new TestSettingsDialogPlayerFactoryPort());
        typeof(SettingsDialogViewModel)
            .GetField("playbackRuntimePort", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel.SettingDialog, playbackRuntimePort ?? new TestSettingsDialogPlaybackRuntimePort());
        return viewModel;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    private static TextBox GetLr2AdvancedPathEditor(Lr2AdvancedPathsDialog dialog, string label)
    {
        SettingsPathPicker picker = FindDescendants<SettingsPathPicker>(dialog)
            .Single(candidate => candidate.Label == label);
        Assert.IsFalse(picker.IsPathReadOnly);
        TextBox editor = FindDescendants<TextBox>(picker).Single();
        Assert.IsFalse(editor.IsReadOnly);
        return editor;
    }

    private static void InvokeEnterAccessKey()
    {
        AccessKeyManager.ProcessKey(null, "\r", false);
    }

    private static Settings CreateValidStandaloneSettings(string root)
    {
        var settings = new Settings
        {
            OperationModeLR2DB = false,
            BMSRootPath = root,
            StandaloneBmsRootPaths = root,
            BMSInstallDir = root,
            TableListURL = new Uri("http://127.0.0.1:1/table-list.json"),
            EnablePlaylistUrlCompletion = false,
            ScanBmsFilesOnStartup = false,
            SkipInitPlaylistLoad = true,
            UseBeatorajaScoreDb = false,
            EnableBeatorajaBmtOutput = false,
            UseExternalPanelImage = false,
            UsePlayeruBMplay = false,
            UsePlayerLR2body = false,
            UsePlayerBMIIDXView = false,
            IsLR2BackupEnabled = false
        };
        return settings;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }
            if (current is T typedCurrent)
            {
                yield return typedCurrent;
            }

            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
            foreach (object logicalChild in LogicalTreeHelper.GetChildren(current))
            {
                if (logicalChild is DependencyObject dependencyObject)
                {
                    pending.Push(dependencyObject);
                }
            }
        }
    }

    private sealed class CountingSettingsEditSession : ISettingsEditSession
    {
        internal CountingSettingsEditSession(Settings values)
        {
            Values = values;
        }

        internal Action? SaveObserved { get; set; }

        internal Exception? SaveFailure { get; set; }

        internal bool BlockSave { get; set; }

        internal ManualResetEventSlim SaveEntered { get; } = new(false);

        internal ManualResetEventSlim ReleaseSave { get; } = new(false);

        internal int SaveCount { get; private set; }

        public Settings Values { get; }

        public void Reload()
        {
        }

        public void Save()
        {
            SaveCount++;
            SaveObserved?.Invoke();
            if (SaveFailure != null)
            {
                throw SaveFailure;
            }
            if (BlockSave)
            {
                SaveEntered.Set();
                ReleaseSave.Wait();
            }
        }
    }

    private static Grid CreateSettingsControlHost()
    {
        var host = new Grid();
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/BeMusicSeeker;component/Themes/Light.xaml", UriKind.RelativeOrAbsolute)
        });
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/BeMusicSeeker;component/BeMusicSeeker/Views/Settings/SettingsControls.xaml", UriKind.RelativeOrAbsolute)
        });
        return host;
    }

    private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null)
        {
            return null;
        }

        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is T match)
            {
                return match;
            }

            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }

            foreach (object child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject dependencyObject)
                {
                    pending.Push(dependencyObject);
                }
            }
        }

        return null;
    }

    private static bool IsVisualDescendantOf(DependencyObject candidate, DependencyObject ancestor)
    {
        for (DependencyObject current = candidate; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        dispatcher.Invoke(DispatcherPriority.Input, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static void RaiseKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target);
        Assert.IsNotNull(source);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
    }

    private static void AssertLocalImplicitStyle(Style actualStyle, Style localBaseStyle, string controlName)
    {
        Assert.IsNotNull(actualStyle, controlName + " must resolve an implicit Settings-local style.");
        Assert.AreSame(localBaseStyle, actualStyle.BasedOn,
            controlName + " implicit style must be based on the closed Settings style, not an application resource.");
    }

    private static void AssertBrushColor(FrameworkElement resourceOwner, string resourceKey, Brush actual)
    {
        var expected = (SolidColorBrush)resourceOwner.TryFindResource(resourceKey);
        Assert.IsInstanceOfType<SolidColorBrush>(actual, resourceKey);
        Assert.AreEqual(expected.Color, ((SolidColorBrush)actual).Color, resourceKey);
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double Channel(byte value)
        {
            double normalized = value / 255d;
            return normalized <= 0.04045d
                ? normalized / 12.92d
                : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
        }

        static double Luminance(Color color)
        {
            return 0.2126d * Channel(color.R)
                + 0.7152d * Channel(color.G)
                + 0.0722d * Channel(color.B);
        }

        double firstLuminance = Luminance(first);
        double secondLuminance = Luminance(second);
        double lighter = Math.Max(firstLuminance, secondLuminance);
        double darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05d) / (darker + 0.05d);
    }
}
