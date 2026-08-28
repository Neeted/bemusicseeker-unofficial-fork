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
    [TestInitialize]
    public void MaterializeCanonicalApplicationResources()
    {
        TestUiDispatcherHost.Invoke(EnsureCanonicalApplicationResources);
    }

    [TestMethod]
    public void SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var runtime = new RecordingManualResyncRuntime();
            SettingsDialogViewModel settings = CreateManualResyncSettingsDialog(owner, runtime);
            var ownerWindow = new Window
            {
                Width = 320,
                Height = 200,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            var window = new SettingsWindow
            {
                DataContext = settings,
                PlaybackPanel = owner.PlaybackPanel,
                PlaylistWorkspace = owner.PlaylistWorkspace
            };
            Window confirmationWindow = null;
            Exception confirmationFailure = null;

            try
            {
                windowTest.PrepareForOwnedPresentation(ownerWindow);
                ownerWindow.Show();
                ownerWindow.UpdateLayout();
                window.Owner = ownerWindow;
                windowTest.ShowAndWaitForContentRendered(window);
                GeneralSettingsPage page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                Button resyncButton = FindDescendants<Button>(page)
                    .Single(candidate => Equals(candidate.Content, Resources.Lr2_song_db_sync_data_resync));
                Assert.IsTrue(settings.CanRequestLr2SongDbSyncDataResync);
                Assert.IsTrue(resyncButton.IsEnabled);

                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    (Action)(() =>
                    {
                        try
                        {
                            confirmationWindow = Application.Current.Windows
                                .OfType<Window>()
                                .Single(candidate => candidate.IsVisible
                                    && ReferenceEquals(candidate.Owner, window));
                            Assert.IsInstanceOfType<ThemedWindow>(confirmationWindow);
                            Assert.AreEqual(Resources.Confirm, confirmationWindow.Title);
                            Button acceptButton = FindDescendants<Button>(confirmationWindow)
                                .Single(candidate => Equals(candidate.Content, "OK"));
                            acceptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, acceptButton));
                        }
                        catch (Exception exception)
                        {
                            confirmationFailure = exception;
                            confirmationWindow?.Close();
                        }
                    }));

                resyncButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, resyncButton));
                PumpUntil(
                    window.Dispatcher,
                    () => runtime.QueueCount == 1,
                    "Manual LR2 resync did not queue its runtime request within the bounded UI pump.");

                Assert.IsNull(confirmationFailure);
                Assert.IsNotNull(confirmationWindow);
                Assert.IsFalse(window.IsVisible);
                Assert.AreEqual(SettingsWindowCloseReason.ManualResync, window.CloseReason);
                Assert.IsNull(window.DataContext);
                RecordingManualResyncRuntime.QueueCall call = runtime.LastQueueCall;
                Assert.AreEqual("setting_dialog_manual_resync", call.Reason);
                Assert.IsTrue(call.Force);
            }
            finally
            {
                if (confirmationWindow?.IsVisible == true)
                {
                    confirmationWindow.Close();
                }
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
                if (ownerWindow.IsVisible)
                {
                    ownerWindow.Close();
                }
            }
        });
    }

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
                AssertPopupWidth(selectionCombo, selectionPopup);
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
                AssertPopupWidth(editableCombo, editablePopup);
                editableProvider.Collapse();
                PumpDispatcher(window.Dispatcher);
                Assert.IsFalse(editablePopup.IsOpen);

                selectionCombo.Width = 240;
                editableCombo.Width = 268;
                PumpDispatcher(window.Dispatcher);

                selectionProvider.Expand();
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(selectionPopup.IsOpen);
                AssertPopupWidth(selectionCombo, selectionPopup);
                selectionProvider.Collapse();
                PumpDispatcher(window.Dispatcher);
                Assert.IsFalse(selectionPopup.IsOpen);

                editableProvider.Expand();
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(editablePopup.IsOpen);
                AssertPopupWidth(editableCombo, editablePopup);
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
            Window sectionWindow = null;
            try
            {
                Grid host = CreateSettingsControlHost();
                Style comboBoxItemStyle = (Style)host.Resources["SettingsComboBoxItemStyle"];
                Style listBoxStyle = (Style)host.Resources["SettingsListBoxStyle"];
                Style listBoxItemStyle = (Style)host.Resources["SettingsListBoxItemStyle"];
                Style expanderStyle = (Style)host.Resources["SettingsExpanderStyle"];
                Style canonicalComboBoxItemStyle = (Style)host.Resources["App.Canonical.ComboBoxItemStyle"];
                Style canonicalListBoxStyle = (Style)host.Resources["App.Canonical.ListBoxStyle"];
                Style canonicalListBoxItemStyle = (Style)host.Resources["App.Canonical.ListBoxItemStyle"];
                Style canonicalExpanderStyle = (Style)host.Resources["App.Canonical.ExpanderStyle"];
                AssertEffectiveTemplateRole(comboBoxItemStyle, canonicalComboBoxItemStyle, nameof(ComboBoxItem));
                AssertEffectiveTemplateRole(listBoxStyle, canonicalListBoxStyle, nameof(ListBox));
                AssertEffectiveTemplateRole(listBoxItemStyle, canonicalListBoxItemStyle, nameof(ListBoxItem));
                AssertEffectiveTemplateRole(expanderStyle, canonicalExpanderStyle, nameof(Expander));

                Grid sectionHost = CreateSettingsControlHost();
                var constrainedContent = new Border
                {
                    MinHeight = 32,
                    Child = new TextBlock { Text = "Constrained section content" }
                };
                var constrainedSection = new SettingsSection
                {
                    Header = "Constrained section",
                    Description = "Its content must receive the finite host's remaining height.",
                    Content = constrainedContent
                };
                var constrainedContainer = new Grid { Height = 220 };
                constrainedContainer.Children.Add(constrainedSection);

                var naturalContent = new Border
                {
                    MinHeight = 32,
                    Child = new TextBlock { Text = "Natural section content" }
                };
                var naturalSection = new SettingsSection
                {
                    Header = "Natural section",
                    Description = "An auto-sized host must retain natural desired height.",
                    Content = naturalContent
                };
                var sectionPanel = new StackPanel();
                sectionPanel.Children.Add(constrainedContainer);
                sectionPanel.Children.Add(naturalSection);
                sectionHost.Children.Add(sectionPanel);
                sectionWindow = new Window
                {
                    Content = sectionHost,
                    Width = 420,
                    Height = 420
                };
                windowTest.ShowAndWaitForContentRendered(
                    sectionWindow,
                    TestWindowActivation.ForegroundInteraction);
                sectionWindow.UpdateLayout();

                Assert.IsTrue(
                    constrainedContent.ActualHeight > naturalContent.ActualHeight + 50d,
                    "A SettingsSection in a finite-height host must allocate its remaining height to content.");
                Assert.IsTrue(
                    naturalContent.ActualHeight <= naturalContent.DesiredSize.Height + 0.5d,
                    "An auto-sized SettingsSection must leave content at its natural desired height.");
                Assert.IsTrue(
                    naturalSection.ActualHeight < constrainedSection.ActualHeight - 50d,
                    "An auto-sized SettingsSection must not inherit artificial blank height from constrained hosts.");

                var pageScroller = new ScrollViewer
                {
                    Content = new Border { Width = 800, Height = 800 },
                    Width = 180,
                    Height = 100,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Visible
                };
                var textBox = new TextBox
                {
                    Text = new string('x', 240),
                    Width = 140,
                    Height = 48,
                    TextWrapping = TextWrapping.NoWrap,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                var comboBox = new ComboBox
                {
                    ItemsSource = new[] { "First", "Second" }
                        .Concat(Enumerable.Range(2, 10).Select(index => $"Combo item {index}"))
                        .ToArray(),
                    SelectedIndex = 0,
                    MaxDropDownHeight = 64
                };
                var listBox = new ListBox
                {
                    ItemsSource = new[] { "First", "Second" }
                        .Concat(Enumerable.Range(2, 10).Select(index => $"List item {index}"))
                        .ToArray(),
                    SelectedIndex = 1,
                    Height = 64
                };
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

                AssertEffectiveTemplateRole(listBox.Style, canonicalListBoxStyle, nameof(ListBox));
                AssertEffectiveTemplateRole(listBox.ItemContainerStyle, canonicalListBoxItemStyle, nameof(ListBoxItem));
                AssertEffectiveTemplateRole(comboBox.ItemContainerStyle, canonicalComboBoxItemStyle, nameof(ComboBoxItem));
                AssertEffectiveTemplateRole(expander.Style, canonicalExpanderStyle, nameof(Expander));
                foreach (FrameworkElement element in new FrameworkElement[] { pageScroller, comboBox, listBox, expander })
                {
                    Assert.AreNotEqual(sentinel, element.Tag, element.GetType().Name);
                }

                pageScroller.ApplyTemplate();
                var verticalScrollBar = (ScrollBar)pageScroller.Template.FindName("PART_VerticalScrollBar", pageScroller);
                Assert.IsNotNull(verticalScrollBar);
                Assert.IsNotNull(pageScroller.Template.FindName("PART_ScrollContentPresenter", pageScroller));
                Assert.AreNotEqual(sentinel, verticalScrollBar.Tag);
                verticalScrollBar.ApplyTemplate();
                Assert.IsNotNull(verticalScrollBar.Template.FindName("PART_Track", verticalScrollBar));
                var horizontalScrollBar = (ScrollBar)pageScroller.Template.FindName("PART_HorizontalScrollBar", pageScroller);
                Assert.IsNotNull(horizontalScrollBar);
                Assert.AreEqual(Orientation.Vertical, verticalScrollBar.Orientation);
                Assert.AreEqual(Orientation.Horizontal, horizontalScrollBar.Orientation);
                Assert.AreEqual(pageScroller.ViewportHeight, verticalScrollBar.ViewportSize, 0.01d);
                Assert.AreEqual(pageScroller.ViewportWidth, horizontalScrollBar.ViewportSize, 0.01d);
                Assert.AreEqual(pageScroller.ScrollableHeight, verticalScrollBar.Maximum, 0.01d);
                Assert.AreEqual(pageScroller.ScrollableWidth, horizontalScrollBar.Maximum, 0.01d);
                horizontalScrollBar.ApplyTemplate();
                var horizontalTrack = (Track)horizontalScrollBar.Template.FindName("PART_Track", horizontalScrollBar);
                Assert.IsNotNull(horizontalTrack);
                Assert.AreEqual(Orientation.Horizontal, horizontalTrack.Orientation);
                var scrollPeer = new ScrollViewerAutomationPeer(pageScroller);
                var scrollProvider = (IScrollProvider)scrollPeer.GetPattern(PatternInterface.Scroll);
                Assert.IsNotNull(scrollProvider);
                Assert.IsTrue(scrollProvider.VerticallyScrollable);
                Assert.IsTrue(scrollProvider.HorizontallyScrollable);
                Assert.IsFalse(pageScroller.CanContentScroll);
                double initialOffset = pageScroller.VerticalOffset;
                double initialHorizontalOffset = pageScroller.HorizontalOffset;
                scrollProvider.Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(pageScroller.VerticalOffset > initialOffset);
                Assert.AreEqual(pageScroller.VerticalOffset, verticalScrollBar.Value, 0.01d);
                scrollProvider.Scroll(ScrollAmount.SmallIncrement, ScrollAmount.NoAmount);
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(pageScroller.HorizontalOffset > initialHorizontalOffset);
                Assert.AreEqual(pageScroller.HorizontalOffset, horizontalScrollBar.Value, 0.01d);
                AssertMaterializedScrollViewerConsumer(
                    pageScroller,
                    Orientation.Horizontal,
                    "Representative ScrollViewer horizontal content");
                verticalScrollBar.IsEnabled = false;
                PumpDispatcher(window.Dispatcher);
                Assert.IsTrue(verticalScrollBar.Opacity < 1d);

                textBox.ApplyTemplate();
                var textContentHost = (ScrollViewer)textBox.Template.FindName("PART_ContentHost", textBox);
                Assert.IsNotNull(textContentHost);
                Assert.AreNotEqual(sentinel, textContentHost.Tag);
                AssertMaterializedScrollViewerConsumer(textContentHost, Orientation.Horizontal, "TextBox content host");

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
                Assert.AreNotEqual(sentinel, popupScroller.Tag);
                AssertMaterializedScrollViewerConsumer(popupScroller, Orientation.Vertical, "ComboBox popup");
                var firstComboItem = (ComboBoxItem)comboBox.ItemContainerGenerator.ContainerFromIndex(0);
                var secondComboItem = (ComboBoxItem)comboBox.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.IsNotNull(firstComboItem);
                Assert.IsNotNull(secondComboItem);
                AssertEffectiveTemplateRole(firstComboItem.Style, canonicalComboBoxItemStyle, nameof(ComboBoxItem));
                AssertEffectiveTemplateRole(secondComboItem.Style, canonicalComboBoxItemStyle, nameof(ComboBoxItem));
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
                Assert.AreNotEqual(sentinel, listScroller.Tag);
                AssertMaterializedScrollViewerConsumer(listScroller, Orientation.Vertical, "ListBox content");
                Assert.AreEqual(1, listBox.SelectedIndex);
                var firstListItem = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0);
                var secondListItem = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.IsNotNull(firstListItem);
                Assert.IsNotNull(secondListItem);
                AssertEffectiveTemplateRole(firstListItem.Style, canonicalListBoxItemStyle, nameof(ListBoxItem));
                AssertEffectiveTemplateRole(secondListItem.Style, canonicalListBoxItemStyle, nameof(ListBoxItem));
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

                // Keep the navigation primitive in its own presentation scope. The
                // existing control-system assertions intentionally use a crowded
                // host, and adding a second tall fixture there changes Expander
                // layout/selection behavior without exercising the navigation role.
                Grid topNavigationHost = CreateSettingsControlHost();
                var topNavigation = new ListBox
                {
                    Width = 360,
                    Height = 56,
                    ItemsSource = new[] { "General", "Folder", "Custom Folder" },
                    SelectedIndex = 0,
                    Style = (Style)topNavigationHost.Resources["App.Canonical.TopNavigationStyle"]
                };
                var topNavigationContentText = new TextBlock { Text = "General content" };
                var topNavigationContent = new ContentControl
                {
                    Height = 80,
                    Content = topNavigationContentText,
                    Style = (Style)topNavigationHost.Resources["App.Canonical.TopNavigationContentStyle"]
                };
                var topNavigationPanel = new StackPanel();
                topNavigationPanel.Children.Add(topNavigation);
                topNavigationPanel.Children.Add(topNavigationContent);
                topNavigationHost.Children.Add(topNavigationPanel);
                var topNavigationWindow = new Window
                {
                    Content = topNavigationHost,
                    Width = 420,
                    Height = 180
                };
                windowTest.ShowAndWaitForContentRendered(
                    topNavigationWindow,
                    TestWindowActivation.ForegroundInteraction);

                topNavigation.ApplyTemplate();
                topNavigationWindow.UpdateLayout();
                Assert.AreEqual(SelectionMode.Single, topNavigation.SelectionMode);
                Assert.AreEqual(
                    KeyboardNavigationMode.Continue,
                    KeyboardNavigation.GetDirectionalNavigation(topNavigation));
                StackPanel topNavigationItems = FindDescendant<StackPanel>(topNavigation);
                Assert.AreEqual(Orientation.Horizontal, topNavigationItems.Orientation);
                var topNavigationItemsByIndex = topNavigation.Items
                    .Cast<object>()
                    .Select((_, index) => (ListBoxItem)topNavigation.ItemContainerGenerator.ContainerFromIndex(index))
                    .ToArray();
                Assert.IsTrue(topNavigationItemsByIndex.All(item => item is not null));
                foreach (ListBoxItem item in topNavigationItemsByIndex)
                {
                    item.ApplyTemplate();
                    Assert.IsTrue(item.Focusable);
                    Assert.IsNotNull(item.Template.FindName("SelectionIndicator", item));
                    Assert.IsNotNull(item.Template.FindName("FocusBorder", item));
                }
                Assert.AreEqual(
                    Visibility.Visible,
                    ((Border)topNavigationItemsByIndex[0].Template.FindName("SelectionIndicator", topNavigationItemsByIndex[0])).Visibility);
                Assert.AreEqual(
                    Visibility.Collapsed,
                    ((Border)topNavigationItemsByIndex[1].Template.FindName("SelectionIndicator", topNavigationItemsByIndex[1])).Visibility);
                Assert.IsTrue(topNavigationItemsByIndex[0].Focus());
                PumpDispatcher(topNavigationWindow.Dispatcher);
                Assert.AreEqual(
                    Visibility.Visible,
                    ((Border)topNavigationItemsByIndex[0].Template.FindName("FocusBorder", topNavigationItemsByIndex[0])).Visibility);
                RaiseKey(topNavigationItemsByIndex[0], Key.End);
                PumpDispatcher(topNavigationWindow.Dispatcher);
                Assert.AreEqual(2, topNavigation.SelectedIndex);
                Assert.IsTrue(topNavigationItemsByIndex[2].IsKeyboardFocusWithin);
                RaiseKey(topNavigationItemsByIndex[2], Key.Home);
                PumpDispatcher(topNavigationWindow.Dispatcher);
                Assert.AreEqual(0, topNavigation.SelectedIndex);
                Assert.IsTrue(topNavigationItemsByIndex[0].IsKeyboardFocusWithin);
                var topNavigationPeer = new ListBoxAutomationPeer(topNavigation);
                var topNavigationSelection = (ISelectionProvider)topNavigationPeer.GetPattern(PatternInterface.Selection);
                Assert.IsNotNull(topNavigationSelection);
                Assert.IsFalse(topNavigationSelection.CanSelectMultiple);
                Assert.AreEqual(1, topNavigationSelection.GetSelection().Length);
                AutomationPeer topFolderPeer = topNavigationPeer.GetChildren()
                    .Single(peer => peer.GetName() == "Folder");
                var topFolderSelection = (ISelectionItemProvider)topFolderPeer.GetPattern(PatternInterface.SelectionItem);
                Assert.IsNotNull(topFolderSelection);
                topFolderSelection.Select();
                PumpDispatcher(topNavigationWindow.Dispatcher);
                Assert.AreEqual(1, topNavigation.SelectedIndex);
                Assert.IsTrue(topFolderSelection.IsSelected);
                Assert.IsTrue(topNavigationItemsByIndex[1].Focus());
                PumpDispatcher(topNavigationWindow.Dispatcher);
                RaiseKey(topNavigationItemsByIndex[1], Key.Right);
                PumpDispatcher(topNavigationWindow.Dispatcher);
                Assert.AreEqual(2, topNavigation.SelectedIndex);
                Assert.IsTrue(topNavigationItemsByIndex[2].IsKeyboardFocusWithin);
                RaiseKey(topNavigationItemsByIndex[2], Key.Left);
                PumpDispatcher(topNavigationWindow.Dispatcher);
                Assert.AreEqual(1, topNavigation.SelectedIndex);
                Assert.IsTrue(topNavigationItemsByIndex[1].IsKeyboardFocusWithin);
                topNavigationItemsByIndex[2].IsEnabled = false;
                PumpDispatcher(topNavigationWindow.Dispatcher);
                var disabledNavigationChrome = (Border)topNavigationItemsByIndex[2].Template.FindName("NavigationItemChrome", topNavigationItemsByIndex[2]);
                Assert.IsTrue(disabledNavigationChrome.Opacity < 1d);
                AutomationPeer disabledNavigationPeer = topNavigationPeer.GetChildren()
                    .Single(peer => !peer.IsEnabled());
                var disabledNavigationSelection = (ISelectionItemProvider)disabledNavigationPeer.GetPattern(PatternInterface.SelectionItem);
                Assert.IsNotNull(disabledNavigationSelection);
                int selectedBeforeDisabledAttempt = topNavigation.SelectedIndex;
                bool selectionRejected = false;
                try
                {
                    disabledNavigationSelection.Select();
                }
                catch (ElementNotEnabledException)
                {
                    selectionRejected = true;
                }

                PumpDispatcher(topNavigationWindow.Dispatcher);
                Assert.AreEqual(selectedBeforeDisabledAttempt, topNavigation.SelectedIndex);
                Assert.IsTrue(
                    selectionRejected || !disabledNavigationSelection.IsSelected,
                    "A disabled top-navigation item must reject or ignore public Automation selection.");
                topNavigationContent.ApplyTemplate();
                topNavigationContent.UpdateLayout();
                Assert.IsFalse(topNavigationContent.Focusable);
                Rect topNavigationContentBounds = new(
                    0d,
                    0d,
                    topNavigationContent.ActualWidth,
                    topNavigationContent.ActualHeight);
                Border? visibleEnclosingSurface = FindDescendants<Border>(topNavigationContent)
                    .FirstOrDefault(border =>
                    {
                        Rect borderBounds = border
                            .TransformToAncestor(topNavigationContent)
                            .TransformBounds(new Rect(0d, 0d, border.ActualWidth, border.ActualHeight));
                        bool coversContent = borderBounds.Left <= topNavigationContentBounds.Left + 0.5d
                            && borderBounds.Top <= topNavigationContentBounds.Top + 0.5d
                            && borderBounds.Right >= topNavigationContentBounds.Right - 0.5d
                            && borderBounds.Bottom >= topNavigationContentBounds.Bottom - 0.5d;
                        bool hasVisibleBorder = (border.BorderThickness.Left > 0d
                                || border.BorderThickness.Top > 0d
                                || border.BorderThickness.Right > 0d
                                || border.BorderThickness.Bottom > 0d)
                            && border.BorderBrush is Brush borderBrush
                            && borderBrush.Opacity > 0d
                            && (borderBrush is not SolidColorBrush borderColor || borderColor.Color.A > 0);
                        bool hasRoundedVisibleSurface = (border.CornerRadius.TopLeft > 0d
                                || border.CornerRadius.TopRight > 0d
                                || border.CornerRadius.BottomRight > 0d
                                || border.CornerRadius.BottomLeft > 0d)
                            && ((border.Background is Brush background
                                    && background.Opacity > 0d
                                    && (background is not SolidColorBrush backgroundColor || backgroundColor.Color.A > 0))
                                || hasVisibleBorder);
                        return coversContent && (hasVisibleBorder || hasRoundedVisibleSurface);
                    });
                Assert.IsNull(
                    visibleEnclosingSurface,
                    "The shared top-navigation content role must not render a visible enclosing surface.");
                Point topNavigationContentOrigin = topNavigationContentText
                    .TransformToAncestor(topNavigationContent)
                    .Transform(new Point());
                Assert.AreEqual(topNavigationContent.Padding.Left, topNavigationContentOrigin.X, 0.5);
                Assert.AreEqual(topNavigationContent.Padding.Top, topNavigationContentOrigin.Y, 0.5);
                Assert.AreEqual(
                    topNavigationContent.ActualWidth - topNavigationContent.Padding.Left - topNavigationContent.Padding.Right,
                    topNavigationContentText.ActualWidth,
                    0.5,
                    "The unframed content role must continue to own canonical content padding and stretch.");
            }
            finally
            {
                sectionWindow?.Close();
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

    private static SettingsDialogViewModel CreateManualResyncSettingsDialog(
        MainWindowViewModel owner,
        RecordingManualResyncRuntime runtime)
    {
        var settingsSession = new ManualResyncSettingsEditSession(
            new Settings { OperationModeLR2DB = true });
        var composition = new ApplicationComposition(
            settingsEditSession: settingsSession,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var workflow = new Lr2SongDbSyncWorkflowOwner(
            runtime,
            new UiDialogCoordinator(),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            (_, _) => { });
        return new SettingsDialogViewModel(
            new ManualResyncStatePort(),
            owner.PlaylistWorkspace,
            owner.PlaylistWorkspace,
            owner.PlayHistory,
            owner.LibraryFolderTree,
            composition,
            owner.PlaybackPanel,
            workflow,
            settingsSession,
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            externalShellGateway: ExternalShellGatewayPolicy.Current,
            applicationPathSnapshot: ApplicationPathPolicy.Current,
            audioDeviceCatalog: new TestAudioDeviceCatalog(),
            audioSettingsGateway: new TestAudioSettingsGateway(),
            audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create());
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

    private sealed class ManualResyncStatePort : ISettingsDialogStatePort
    {
        public bool HasActiveLibraryProfile => true;

        public bool IsLibraryOperationInProgress => false;

        public Task<bool> InitializeLibraryAsync() => Task.FromResult(true);

        public Task ReloadScoresOnlyAsync() => Task.CompletedTask;

        public Task ReloadFileDiffAsync() => Task.CompletedTask;

        public event EventHandler LibraryOperationAvailabilityChanged
        {
            add { }
            remove { }
        }

        public event Action<Lr2PlayHistorySchemaStatusSnapshot> Lr2PlayHistorySchemaStatusChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class ManualResyncSettingsEditSession : ISettingsEditSession
    {
        internal ManualResyncSettingsEditSession(Settings values)
        {
            Values = values;
        }

        public Settings Values { get; }

        public void Reload()
        {
        }

        public void Save()
        {
        }
    }

    private sealed class RecordingManualResyncRuntime : ILr2SongDbSyncWorkflowRuntime
    {
        private readonly object gate = new();

        private readonly List<QueueCall> queueCalls = [];

        bool ILr2SongDbSyncWorkflowRuntime.IsLr2ModeEnabled => true;

        bool ILr2SongDbSyncWorkflowRuntime.IsLibraryAvailable => true;

        internal int QueueCount
        {
            get
            {
                lock (gate)
                {
                    return queueCalls.Count;
                }
            }
        }

        internal QueueCall LastQueueCall
        {
            get
            {
                lock (gate)
                {
                    return queueCalls.Single();
                }
            }
        }

        public void Queue(
            string reason,
            bool force,
            Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData = null,
            bool allowIncompleteToQueue = true)
        {
            lock (gate)
            {
                queueCalls.Add(new QueueCall(reason, force, allowIncompleteToQueue));
            }
            prepareGeneratedData?.Invoke();
        }

        public bool TryRunDataPreparation(
            string reason,
            Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData,
            Action queueAfterPreparation = null)
        {
            prepareGeneratedData?.Invoke();
            queueAfterPreparation?.Invoke();
            return true;
        }

        public Lr2SongDbSyncPreparedDataSurface PreparePlaylistGeneratedData(string reason) =>
            Lr2SongDbSyncPreparedDataSurface.Empty;

        public Lr2SongDbSyncPreparedDataSurface PrepareBuiltinGeneratedData(string reason) =>
            Lr2SongDbSyncPreparedDataSurface.Empty;

        public void SyncExternalFolderRowsForCustomFolderOutputBaseChange(string reason)
        {
        }

        public bool Cancel(string reason) => true;

        public Lr2StartupScanBlockerCleanupResult CleanupStartupScanBlockerFolderRows(string reason) => null;

        internal sealed class QueueCall
        {
            internal QueueCall(string reason, bool force, bool allowIncompleteToQueue)
            {
                Reason = reason;
                Force = force;
                AllowIncompleteToQueue = allowIncompleteToQueue;
            }

            internal string Reason { get; }

            internal bool Force { get; }

            internal bool AllowIncompleteToQueue { get; }
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

    private static void EnsureCanonicalApplicationResources()
    {
        Application application = Application.Current;
        Assert.IsNotNull(application);
        AddCanonicalResourceIfMissing(
            application,
            "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalDialogStyles.xaml");
    }

    private static void AddCanonicalResourceIfMissing(Application application, string source)
    {
        if (application.Resources.MergedDictionaries.Any(dictionary =>
            string.Equals(dictionary.Source?.OriginalString, source, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(source, UriKind.RelativeOrAbsolute)
        });
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

    private static void AssertPopupWidth(ComboBox comboBox, Popup popup)
    {
        Assert.IsNotNull(popup.Child, "The open ComboBox popup must materialize its presentation root.");
        Assert.IsInstanceOfType<FrameworkElement>(popup.Child);
        var popupRoot = (FrameworkElement)popup.Child;
        Assert.AreEqual(
            comboBox.ActualWidth,
            popupRoot.ActualWidth,
            0.01d,
            "The popup presentation root width must match the open ComboBox ActualWidth.");
    }

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> predicate, string failureMessage)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate() && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            PumpDispatcher(dispatcher);
        }
        Assert.IsTrue(predicate(), failureMessage);
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

    private static void AssertEffectiveTemplateRole(Style? actualStyle, Style? canonicalStyle, string controlName)
    {
        Assert.IsNotNull(actualStyle, controlName + " must resolve a style.");
        Assert.IsNotNull(canonicalStyle, controlName + " must resolve its canonical style in the same host.");

        object? actualTemplate = ResolveEffectiveStyleValue(actualStyle!, Control.TemplateProperty);
        object? canonicalTemplate = ResolveEffectiveStyleValue(canonicalStyle!, Control.TemplateProperty);
        Assert.IsNotNull(actualTemplate, controlName + " must resolve an effective control template.");
        Assert.IsNotNull(canonicalTemplate, controlName + " canonical style must resolve an effective control template.");
        Assert.AreSame(
            canonicalTemplate,
            actualTemplate,
            controlName + " must resolve the host-local canonical template role through its style chain.");
    }

    private static void AssertMaterializedScrollViewerConsumer(
        ScrollViewer viewer,
        Orientation expectedOrientation,
        string consumerName)
    {
        viewer.ApplyTemplate();
        viewer.UpdateLayout();
        Assert.IsTrue(
            expectedOrientation == Orientation.Vertical
                ? viewer.ScrollableHeight > 0d
                : viewer.ScrollableWidth > 0d,
            consumerName + " must expose overflow for scrollbar adoption coverage.");

        string partName = expectedOrientation == Orientation.Vertical
            ? "PART_VerticalScrollBar"
            : "PART_HorizontalScrollBar";
        ScrollBar scrollbar = viewer.Template.FindName(partName, viewer) as ScrollBar
            ?? throw new AssertFailedException(consumerName + " must materialize its expected scrollbar part.");
        Assert.AreEqual(Visibility.Visible, scrollbar.Visibility, consumerName + " scrollbar must be visible for overflow.");
        Assert.IsTrue(scrollbar.ActualWidth > 0d && scrollbar.ActualHeight > 0d, consumerName + " scrollbar must render.");
        Assert.AreEqual(expectedOrientation, scrollbar.Orientation, consumerName + " scrollbar orientation.");

        scrollbar.ApplyTemplate();
        Track track = scrollbar.Template.FindName("PART_Track", scrollbar) as Track
            ?? throw new AssertFailedException(consumerName + " scrollbar must materialize PART_Track.");
        Assert.AreEqual(expectedOrientation, track.Orientation, consumerName + " scrollbar track orientation.");

        FrameworkElement lineStart;
        FrameworkElement lineEnd;
        RoutedCommand lineStartCommand;
        RoutedCommand lineEndCommand;
        RoutedCommand pageStartCommand;
        RoutedCommand pageEndCommand;
        if (expectedOrientation == Orientation.Vertical)
        {
            lineStartCommand = ScrollBar.LineUpCommand;
            lineEndCommand = ScrollBar.LineDownCommand;
            pageStartCommand = ScrollBar.PageUpCommand;
            pageEndCommand = ScrollBar.PageDownCommand;
        }
        else
        {
            lineStartCommand = ScrollBar.LineLeftCommand;
            lineEndCommand = ScrollBar.LineRightCommand;
            pageStartCommand = ScrollBar.PageLeftCommand;
            pageEndCommand = ScrollBar.PageRightCommand;
        }

        lineStart = FindScrollCommandAffordance(scrollbar, lineStartCommand, consumerName + " line-start");
        lineEnd = FindScrollCommandAffordance(scrollbar, lineEndCommand, consumerName + " line-end");
        FrameworkElement pageStart = FindScrollCommandAffordance(track, pageStartCommand, consumerName + " page-start");
        FrameworkElement pageEnd = FindScrollCommandAffordance(track, pageEndCommand, consumerName + " page-end");

        IScrollProvider scrollProvider = (new ScrollViewerAutomationPeer(viewer).GetPattern(PatternInterface.Scroll) as IScrollProvider)
            ?? throw new AssertFailedException(consumerName + " must expose Scroll automation.");
        Assert.IsTrue(
            expectedOrientation == Orientation.Vertical
                ? scrollProvider.VerticallyScrollable
                : scrollProvider.HorizontallyScrollable,
            consumerName + " must expose the expected scroll Automation direction.");
        double initialOffset = expectedOrientation == Orientation.Vertical
            ? viewer.VerticalOffset
            : viewer.HorizontalOffset;
        scrollProvider.Scroll(
            expectedOrientation == Orientation.Vertical ? ScrollAmount.NoAmount : ScrollAmount.SmallIncrement,
            expectedOrientation == Orientation.Vertical ? ScrollAmount.SmallIncrement : ScrollAmount.NoAmount);
        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        double transitionedOffset = expectedOrientation == Orientation.Vertical
            ? viewer.VerticalOffset
            : viewer.HorizontalOffset;
        Assert.IsTrue(transitionedOffset > initialOffset, consumerName + " must perform an actual scroll transition.");
        Assert.AreEqual(transitionedOffset, scrollbar.Value, 0.01d, consumerName + " scrollbar value must follow its viewer offset.");
        Assert.AreEqual(
            expectedOrientation == Orientation.Vertical ? viewer.ViewportHeight : viewer.ViewportWidth,
            scrollbar.ViewportSize,
            0.01d,
            consumerName + " scrollbar viewport must follow its viewer viewport.");
        Assert.AreEqual(
            expectedOrientation == Orientation.Vertical ? viewer.ScrollableHeight : viewer.ScrollableWidth,
            scrollbar.Maximum,
            0.01d,
            consumerName + " scrollbar maximum must follow its viewer extent.");

        double lineStartOffset = SetScrollOffsetToInterior(viewer, expectedOrientation, consumerName);
        InvokeScrollButton(lineStart, consumerName + " line-start");
        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, expectedOrientation) < lineStartOffset,
            consumerName + " line-start command must decrease the viewer offset.");

        double lineEndOffset = SetScrollOffsetToInterior(viewer, expectedOrientation, consumerName);
        InvokeScrollButton(lineEnd, consumerName + " line-end");
        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, expectedOrientation) > lineEndOffset,
            consumerName + " line-end command must increase the viewer offset.");

        double pageStartOffset = SetScrollOffsetToInterior(viewer, expectedOrientation, consumerName);
        InvokeScrollButton(pageStart, consumerName + " page-start");
        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, expectedOrientation) < pageStartOffset,
            consumerName + " page-start command must decrease the viewer offset.");

        double pageEndOffset = SetScrollOffsetToInterior(viewer, expectedOrientation, consumerName);
        InvokeScrollButton(pageEnd, consumerName + " page-end");
        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, expectedOrientation) > pageEndOffset,
            consumerName + " page-end command must increase the viewer offset.");
    }

    private static FrameworkElement FindScrollCommandAffordance(
        DependencyObject root,
        RoutedCommand command,
        string description)
    {
        FrameworkElement[] candidates = FindDescendants<FrameworkElement>(root)
            .Where(element => element is ICommandSource source && ReferenceEquals(source.Command, command))
            .ToArray();
        Assert.AreEqual(
            1,
            candidates.Length,
            description + " must expose exactly one materialized command affordance.");
        FrameworkElement affordance = candidates[0];
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(affordance)
            ?? throw new AssertFailedException(description + " command affordance must expose an Automation peer.");
        Assert.IsNotNull(
            peer.GetPattern(PatternInterface.Invoke),
            description + " command affordance must expose Invoke automation.");
        return affordance;
    }

    private static double SetScrollOffsetToInterior(
        ScrollViewer viewer,
        Orientation orientation,
        string consumerName)
    {
        double maximum = orientation == Orientation.Vertical
            ? viewer.ScrollableHeight
            : viewer.ScrollableWidth;
        Assert.IsTrue(maximum > 0d, consumerName + " must expose a positive scroll extent.");
        double target = maximum / 2d;
        if (orientation == Orientation.Vertical)
        {
            viewer.ScrollToVerticalOffset(target);
        }
        else
        {
            viewer.ScrollToHorizontalOffset(target);
        }

        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        double offset = GetScrollOffset(viewer, orientation);
        Assert.IsTrue(
            offset > 0d && offset < maximum,
            consumerName + " must invoke affordances from a proven non-boundary offset.");
        return offset;
    }

    private static double GetScrollOffset(ScrollViewer viewer, Orientation orientation)
        => orientation == Orientation.Vertical ? viewer.VerticalOffset : viewer.HorizontalOffset;

    private static void InvokeScrollButton(FrameworkElement button, string description)
    {
        Assert.IsTrue(button.IsEnabled, description + " command affordance must be enabled at the interior offset.");
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(button)
            ?? throw new AssertFailedException(description + " button must expose an Automation peer.");
        IInvokeProvider invokeProvider = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new AssertFailedException(description + " button must expose Invoke automation.");
        invokeProvider.Invoke();
    }

    private static object? ResolveEffectiveStyleValue(Style style, DependencyProperty property)
    {
        for (Style? current = style; current != null; current = current.BasedOn)
        {
            Setter? setter = current.Setters
                .OfType<Setter>()
                .LastOrDefault(candidate => candidate.Property == property);
            if (setter != null)
            {
                return setter.Value;
            }
        }

        return null;
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
