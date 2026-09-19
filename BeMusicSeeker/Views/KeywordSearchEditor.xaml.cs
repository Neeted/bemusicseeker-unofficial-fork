using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.ViewModels;
using AppResources = BeMusicSeeker.Properties.Resources;

namespace BeMusicSeeker.Views;

/// <summary>
/// Owns the WPF terminal behavior for one keyword-search editor.
/// Candidate calculation and saved-query mutation stay in the injected immutable owner;
/// this control owns popup visibility, selection, scrolling, and routed input.
/// </summary>
public partial class KeywordSearchEditor : UserControl
{
    /// <summary>
    /// Identifies the text dependency property kept compatible with the delayed outer
    /// chart-filter binding. The editor itself observes the inner TextBox immediately.
    /// </summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(KeywordSearchEditor),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private const double SavedRowHeight = 26d;

    private const double CompletionRowHeight = 26d;

    private readonly List<ListBox> renderedSections = [];

    private KeywordSearchAssistanceOwner assistanceOwner;

    private KeywordSearchPresentationState presentation;

    private int selectedIndex = -1;

    private bool applyingText;

    private bool imeCompositionActive;

    private bool ownerSubscribed;

    private bool resourceServiceSubscribed;

    private bool shiftKeyDown;

    private KeywordSearchPresentationItem pendingActionFocusItem;

    private DependencyObject actionFocusScope;

    private Button actionLogicalFocusTarget;

    /// <summary>
    /// Initializes an editor with no feature owner. The host configures the owner after
    /// the MainWindow composition has constructed both normal and summary scopes.
    /// </summary>
    public KeywordSearchEditor()
    {
        InitializeComponent();
        presentation = null;
        Loaded += KeywordSearchEditorLoaded;
        Unloaded += KeywordSearchEditorUnloaded;
        TextCompositionManager.AddPreviewTextInputStartHandler(
            InputTextBox,
            InputTextBoxPreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(
            InputTextBox,
            InputTextBoxPreviewTextInputUpdate);
        // TextBox handles the bubbling TextInput event before an ordinary handler can
        // observe it. Register with handled-events-too so a committed composition always
        // releases the terminal input guard.
        InputTextBox.AddHandler(
            TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler(InputTextBoxTextInput),
            handledEventsToo: true);
    }

    /// <summary>
    /// Gets or sets the raw search text. The containing toolbar may bind this property
    /// with its existing 500 ms filter delay; assistance never uses that delayed value.
    /// </summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? string.Empty);
    }

    /// <summary>
    /// Gets the editor-owned TextBox for host-level clear and deterministic presentation tests.
    /// </summary>
    internal TextBox InputTextBoxControl => InputTextBox;

    /// <summary>
    /// Gets the popup whose open state is owned entirely by this editor.
    /// </summary>
    internal Popup AssistancePopupControl => AssistancePopup;

    /// <summary>
    /// Gets the last immutable presentation applied to this editor.
    /// </summary>
    internal KeywordSearchPresentationState Presentation => presentation;

    /// <summary>
    /// Injects the owner for this editor's normal or playlist-summary search scope.
    /// </summary>
    /// <param name="owner">The explicit immutable-presentation owner.</param>
    internal void Configure(KeywordSearchAssistanceOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (ReferenceEquals(assistanceOwner, owner))
        {
            SubscribeOwner();
            ApplyPresentation(owner.Presentation);
            return;
        }

        UnsubscribeOwner();
        assistanceOwner = owner;
        SubscribeOwner();
        ApplyPresentation(owner.Presentation);
        SetSearchAccessibilityName();
    }

    /// <summary>
    /// Clears the raw input while preserving keyboard focus and immediate assistance refresh.
    /// </summary>
    internal void ClearInput()
    {
        imeCompositionActive = false;
        applyingText = true;
        try
        {
            InputTextBox.SetCurrentValue(TextBox.TextProperty, string.Empty);
            InputTextBox.CaretIndex = 0;
        }
        finally
        {
            applyingText = false;
        }
        FocusInputTextBox();
        RefreshFromInput();
    }

    /// <summary>
    /// Closes the local surface for a window deactivation without writing popup state to a ViewModel.
    /// </summary>
    internal void CloseForWindowDeactivation()
    {
        imeCompositionActive = false;
        if (assistanceOwner != null)
        {
            ApplyPresentation(assistanceOwner.Blur());
        }
        else
        {
            AssistancePopup.IsOpen = false;
            SectionsHost.Children.Clear();
        }
    }

    /// <summary>
    /// Clears keyboard focus when a host-level mouse route observes an outside click.
    /// The TextBox lost-focus handler remains the single owner-blur and history-commit
    /// lifecycle so the route also works when assistance is already closed.
    /// </summary>
    /// <param name="clickedElement">The original element receiving the mouse event.</param>
    internal void ClearKeyboardFocusIfOutside(DependencyObject clickedElement)
    {
        if (IsOwnedElement(clickedElement) || !InputTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        Keyboard.ClearFocus();
    }

    private void KeywordSearchEditorLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeOwner();
        SubscribeResourceService();
        SetSearchAccessibilityName();
    }

    private void KeywordSearchEditorUnloaded(object sender, RoutedEventArgs e)
    {
        imeCompositionActive = false;
        UnsubscribeOwner();
        UnsubscribeResourceService();
        if (assistanceOwner != null)
        {
            ApplyPresentation(assistanceOwner.Blur());
        }
    }

    private void SubscribeResourceService()
    {
        if (resourceServiceSubscribed)
        {
            return;
        }
        ResourceService.Current.PropertyChanged += ResourceServicePropertyChanged;
        resourceServiceSubscribed = true;
    }

    private void UnsubscribeResourceService()
    {
        if (!resourceServiceSubscribed)
        {
            return;
        }
        ResourceService.Current.PropertyChanged -= ResourceServicePropertyChanged;
        resourceServiceSubscribed = false;
    }

    private void ResourceServicePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e?.PropertyName)
            && !string.Equals(e.PropertyName, nameof(ResourceService.Resources), StringComparison.Ordinal))
        {
            return;
        }
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.DataBind,
                new Action(RefreshLocalizedSurface));
            return;
        }
        RefreshLocalizedSurface();
    }

    private void RefreshLocalizedSurface()
    {
        SetSearchAccessibilityName();
        if (presentation?.IsOpen == true)
        {
            ApplyPresentation(assistanceOwner?.Refresh(InputTextBox.Text, InputTextBox.CaretIndex));
        }
    }

    private void SubscribeOwner()
    {
        if (assistanceOwner == null || ownerSubscribed)
        {
            return;
        }
        assistanceOwner.PresentationChanged += AssistanceOwnerPresentationChanged;
        ownerSubscribed = true;
    }

    private void UnsubscribeOwner()
    {
        if (assistanceOwner == null || !ownerSubscribed)
        {
            return;
        }
        assistanceOwner.PresentationChanged -= AssistanceOwnerPresentationChanged;
        ownerSubscribed = false;
    }

    private void AssistanceOwnerPresentationChanged(object sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => ApplyPresentation(assistanceOwner?.Presentation)));
            return;
        }
        ApplyPresentation(assistanceOwner?.Presentation);
    }

    private void InputTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!applyingText)
        {
            RefreshFromInput();
        }
    }

    private void InputTextBoxSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!applyingText)
        {
            RefreshFromInput();
        }
    }

    private void InputTextBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (assistanceOwner == null)
        {
            return;
        }
        ApplyPresentation(assistanceOwner.Focus(InputTextBox.Text, InputTextBox.CaretIndex));
    }

    private void InputTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        imeCompositionActive = false;
        if (assistanceOwner == null
            || pendingActionFocusItem != null
            || IsOwnedElement(e.NewFocus as DependencyObject))
        {
            return;
        }
        CommitCurrentHistory();
        ApplyPresentation(assistanceOwner.Blur());
    }

    private void InputTextBoxPreviewTextInputStart(object sender, TextCompositionEventArgs e)
        => imeCompositionActive = true;

    private void InputTextBoxPreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
        => imeCompositionActive = true;

    private void InputTextBoxTextInput(object sender, TextCompositionEventArgs e)
        => imeCompositionActive = false;

    private void EditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            shiftKeyDown = true;
            return;
        }

        var originalSource = e.OriginalSource as DependencyObject;
        if (e.Key == Key.Tab && IsActionButton(originalSource))
        {
            CycleActionButtons((Button)originalSource, IsShiftPressed(e));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab
            && ReferenceEquals(originalSource, InputTextBox)
            && IsShiftPressed(e)
            && AssistancePopup.IsOpen
            && TryFocusSavedRowAction())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && IsActionButton(originalSource))
        {
            FocusInputTextBox();
            if (assistanceOwner != null)
            {
                ApplyPresentation(assistanceOwner.SuppressCurrentSnapshot());
            }
            e.Handled = true;
        }
    }

    private void EditorPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            shiftKeyDown = false;
        }
    }

    private void InputTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && imeCompositionActive)
        {
            // The first Escape cancels the active IME composition. Leave the assistance
            // surface untouched; a subsequent Escape can dismiss the unchanged snapshot.
            imeCompositionActive = false;
            return;
        }
        if (assistanceOwner == null || IsImeCommitKey(e))
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Space)
        {
            ApplyPresentation(assistanceOwner.ForceRefresh());
            SelectFirstItem();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (AssistancePopup.IsOpen)
            {
                ApplyPresentation(assistanceOwner.SuppressCurrentSnapshot());
                e.Handled = true;
            }
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            if (!AssistancePopup.IsOpen)
            {
                ApplyPresentation(assistanceOwner.ForceRefresh());
            }
            Navigate(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if ((e.Key is Key.Enter or Key.Tab) && AssistancePopup.IsOpen)
        {
            if (ApplySelectedItem())
            {
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitCurrentHistory();
        }
    }

    private bool IsImeCommitKey(KeyEventArgs e)
    {
        return imeCompositionActive
            || e.Key == Key.ImeProcessed
            || e.ImeProcessedKey == Key.Enter
            || e.ImeProcessedKey == Key.Return;
    }

    private void RefreshFromInput()
    {
        if (assistanceOwner == null)
        {
            return;
        }
        ApplyPresentation(assistanceOwner.Refresh(InputTextBox.Text, InputTextBox.CaretIndex));
    }

    private void CommitCurrentHistory()
    {
        if (assistanceOwner == null)
        {
            return;
        }
        KeywordSearchSavedQueryMutationResult result = assistanceOwner.SavedQueryOwner.TryCommitHistory(InputTextBox.Text);
        if (!result.Succeeded)
        {
            throw result.Exception ?? new InvalidOperationException("Keyword search history persistence failed.");
        }
    }

    private void ApplyPresentation(KeywordSearchPresentationState next)
    {
        if (next == null)
        {
            return;
        }

        KeywordSearchPresentationItem selectedItem = SelectedItem;
        presentation = next;
        selectedIndex = selectedItem == null
            ? Math.Min(selectedIndex, next.VisibleItems.Count - 1)
            : FindItemIndex(next.VisibleItems, selectedItem.Identity);
        if (selectedIndex < 0 && next.VisibleItems.Count > 0 && AssistancePopup.IsOpen)
        {
            selectedIndex = -1;
        }
        if (!next.IsOpen)
        {
            AssistancePopup.IsOpen = false;
            SectionsHost.Children.Clear();
            renderedSections.Clear();
            pendingActionFocusItem = null;
            ClearActionLogicalFocus();
            return;
        }

        RenderSections(next);
        AssistancePopup.IsOpen = true;
        ApplySelectionVisuals();
    }

    private void RenderSections(KeywordSearchPresentationState state)
    {
        SectionsHost.Children.Clear();
        renderedSections.Clear();
        foreach (KeywordSearchPresentationSection section in state.Sections)
        {
            if (!section.IsVisible)
            {
                continue;
            }

            var sectionPanel = new StackPanel();
            string headerText = section.HeaderText;
            if (!string.IsNullOrWhiteSpace(headerText))
            {
                var header = new TextBlock
                {
                    Text = headerText,
                    FontFamily = new FontFamily("Meiryo"),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(4, 2, 4, 4)
                };
                header.SetResourceReference(TextBlock.ForegroundProperty, "App.SubtleTextBrush");
                sectionPanel.Children.Add(header);
            }

            var rows = new ListBox
            {
                ItemsSource = section.Items,
                Template = (ControlTemplate)FindResource("KeywordSearchListBoxTemplate"),
                ItemTemplate = (DataTemplate)FindResource("KeywordSearchItemTemplate"),
                ItemContainerStyle = (Style)FindResource("KeywordSearchListBoxItemStyle"),
                ItemsPanel = (ItemsPanelTemplate)FindResource("KeywordSearchVirtualizingItemsPanel"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Focusable = false,
                IsTabStop = false,
                SelectionMode = SelectionMode.Single,
                MinHeight = 0d,
                MaxHeight = section.IsScrollable
                    ? (section.Kind is KeywordSearchPresentationSectionKind.Favorites or KeywordSearchPresentationSectionKind.History
                        ? SavedRowHeight * 5
                        : CompletionRowHeight * 10)
                    : double.PositiveInfinity
            };
            rows.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
            rows.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
            rows.SetValue(ScrollViewer.CanContentScrollProperty, true);
            rows.SetValue(
                ScrollViewer.HorizontalScrollBarVisibilityProperty,
                ScrollBarVisibility.Disabled);
            rows.SetValue(
                ScrollViewer.VerticalScrollBarVisibilityProperty,
                section.IsScrollable ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
            rows.Loaded += SectionRowsLoaded;
            sectionPanel.Children.Add(rows);
            SectionsHost.Children.Add(sectionPanel);
            renderedSections.Add(rows);
        }
    }

    private void SectionRowsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox rows)
        {
            return;
        }

        ScrollViewer viewer = FindVisualDescendants<ScrollViewer>(rows).FirstOrDefault();
        if (viewer == null)
        {
            return;
        }
        viewer.MaxHeight = rows.MaxHeight;
        viewer.HorizontalScrollBarVisibility = (ScrollBarVisibility)rows.GetValue(
            ScrollViewer.HorizontalScrollBarVisibilityProperty);
        viewer.VerticalScrollBarVisibility = (ScrollBarVisibility)rows.GetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty);
        viewer.CanContentScroll = (bool)rows.GetValue(ScrollViewer.CanContentScrollProperty);
    }

    private void ApplyButtonPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button)
        {
            e.Handled = true;
            ApplyItem(button.Tag as KeywordSearchPresentationItem);
        }
    }

    private void ApplyButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ApplyItem(button.Tag as KeywordSearchPresentationItem);
        }
    }

    private void ActionButtonPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button)
        {
            e.Handled = true;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            FocusInputTextBox();
        }
    }

    private void RemoveFavoriteButtonClick(object sender, RoutedEventArgs e)
        => HandleActionButton(sender, KeywordSearchEditorAction.RemoveFavorite);

    private void AddFavoriteButtonClick(object sender, RoutedEventArgs e)
        => HandleActionButton(sender, KeywordSearchEditorAction.AddFavorite);

    private void DeleteHistoryButtonClick(object sender, RoutedEventArgs e)
        => HandleActionButton(sender, KeywordSearchEditorAction.DeleteHistory);

    private void RemoveFavoriteButtonLoaded(object sender, RoutedEventArgs e)
        => SetActionAccessibility(sender, AppResources.Keyword_search_remove_favorite_tooltip);

    private void AddFavoriteButtonLoaded(object sender, RoutedEventArgs e)
        => SetActionAccessibility(sender, AppResources.Keyword_search_add_favorite_tooltip);

    private void DeleteHistoryButtonLoaded(object sender, RoutedEventArgs e)
        => SetActionAccessibility(sender, AppResources.Keyword_search_delete_history_tooltip);

    private static void SetActionAccessibility(object sender, string accessibleText)
    {
        if (sender is not Button button)
        {
            return;
        }
        string text = accessibleText ?? string.Empty;
        button.ToolTip = text;
        AutomationProperties.SetName(button, text);
    }

    private void HandleActionButton(object sender, KeywordSearchEditorAction action)
    {
        if (sender is not Button button
            || button.Tag is not KeywordSearchPresentationItem item
            || assistanceOwner == null)
        {
            return;
        }

        bool restoreFocusAfterKeyboardActivation = button.IsKeyboardFocusWithin;
        KeywordSearchSavedQueryMutationResult result = action switch
        {
            KeywordSearchEditorAction.RemoveFavorite => assistanceOwner.TryRemoveFavorite(item.Query),
            KeywordSearchEditorAction.AddFavorite => assistanceOwner.TryAddFavorite(item.Query),
            KeywordSearchEditorAction.DeleteHistory => assistanceOwner.TryDeleteHistory(item.Query),
            _ => KeywordSearchSavedQueryMutationResult.Success(changed: false)
        };
        if (!result.Succeeded)
        {
            RestoreInputFocusAfterKeyboardAction(restoreFocusAfterKeyboardActivation);
            return;
        }
        selectedIndex = -1;
        FocusInputTextBox();
        RefreshFromInput();
        RestoreInputFocusAfterKeyboardAction(restoreFocusAfterKeyboardActivation);
    }

    private void RestoreInputFocusAfterKeyboardAction(bool restoreFocus)
    {
        if (!restoreFocus)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!IsLoaded || InputTextBox.IsKeyboardFocusWithin)
                {
                    return;
                }

                if (Keyboard.FocusedElement is DependencyObject focusedElement
                    && !IsOwnedElement(focusedElement))
                {
                    return;
                }

                FocusInputTextBox();
            }));
    }

    private void FocusInputTextBox()
    {
        ClearActionLogicalFocus();
        FocusManager.SetFocusedElement(
            FocusManager.GetFocusScope(InputTextBox),
            InputTextBox);
        if (!InputTextBox.Focus())
        {
            Keyboard.Focus(InputTextBox);
        }
    }

    private void ApplyItem(KeywordSearchPresentationItem item)
    {
        if (item == null || assistanceOwner == null || presentation == null)
        {
            return;
        }
        KeywordSearchApplyResult result = assistanceOwner.TryApply(
            item,
            InputTextBox.Text,
            InputTextBox.CaretIndex,
            presentation.Context,
            presentation.CatalogRevision);
        if (!result.Succeeded)
        {
            ApplyPresentation(assistanceOwner.Presentation);
            return;
        }

        applyingText = true;
        try
        {
            InputTextBox.SetCurrentValue(TextBox.TextProperty, result.Text);
            InputTextBox.CaretIndex = Math.Max(0, Math.Min(result.CaretIndex, InputTextBox.Text.Length));
        }
        finally
        {
            applyingText = false;
        }
        InputTextBox.Focus();
        RefreshFromInput();
    }

    private bool ApplySelectedItem()
    {
        KeywordSearchPresentationItem item = SelectedItem
            ?? presentation?.VisibleItems.FirstOrDefault();
        if (item == null)
        {
            return false;
        }
        ApplyItem(item);
        return true;
    }

    private void SelectFirstItem()
    {
        if (!AssistancePopup.IsOpen || presentation?.VisibleItems.Count is not > 0)
        {
            return;
        }
        selectedIndex = 0;
        ApplySelectionVisuals();
        ScrollSelectedRowIntoView();
        InputTextBox.Focus();
    }

    private void Navigate(int delta)
    {
        if (!AssistancePopup.IsOpen || presentation?.VisibleItems.Count is not > 0)
        {
            return;
        }
        int count = presentation.VisibleItems.Count;
        selectedIndex = selectedIndex < 0
            ? (delta >= 0 ? 0 : count - 1)
            : (selectedIndex + delta + count) % count;
        ApplySelectionVisuals();
        ScrollSelectedRowIntoView();
        InputTextBox.Focus();
    }

    private KeywordSearchPresentationItem SelectedItem
        => presentation?.VisibleItems is { Count: > 0 } items
            && selectedIndex >= 0
            && selectedIndex < items.Count
            ? items[selectedIndex]
            : null;

    private void ApplySelectionVisuals()
    {
        int flattenedIndex = 0;
        foreach (ListBox rows in renderedSections)
        {
            for (int itemIndex = 0; itemIndex < rows.Items.Count; itemIndex++)
            {
                if (rows.ItemContainerGenerator.ContainerFromIndex(itemIndex) is not ListBoxItem container)
                {
                    flattenedIndex++;
                    continue;
                }

                container.SetCurrentValue(
                    ListBoxItem.IsSelectedProperty,
                    flattenedIndex == selectedIndex);
                flattenedIndex++;
            }
        }
    }

    private void ScrollSelectedRowIntoView()
    {
        if (selectedIndex < 0 || presentation?.VisibleItems is not { Count: > 0 })
        {
            return;
        }

        int offset = 0;
        foreach (ListBox rows in renderedSections)
        {
            if (selectedIndex < offset + rows.Items.Count)
            {
                rows.ScrollIntoView(rows.Items[selectedIndex - offset]);
                return;
            }
            offset += rows.Items.Count;
        }
    }

    private bool TryFocusSavedRowAction()
    {
        KeywordSearchPresentationItem item = SelectedItem;
        if (item != null && item.IsSavedQuery && HasAction(item))
        {
            return FocusFirstAction(item);
        }

        item = presentation?.VisibleItems.FirstOrDefault(candidate =>
            candidate.IsSavedQuery && HasAction(candidate));
        return item != null && FocusFirstAction(item);
    }

    private bool FocusFirstAction(KeywordSearchPresentationItem item)
    {
        pendingActionFocusItem = item;
        foreach (ListBox rows in renderedSections)
        {
            if (!rows.Items.Contains(item))
            {
                continue;
            }

            if (rows.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            {
                Button button = GetActionButtons(container).FirstOrDefault();
                if (button != null)
                {
                    if (TryFocusActionButton(button))
                    {
                        pendingActionFocusItem = null;
                    }
                    else
                    {
                        RetryPendingActionFocus(item, rows);
                    }
                    return true;
                }
            }

            rows.ScrollIntoView(item);
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (pendingActionFocusItem == null
                        || !pendingActionFocusItem.Identity.Equals(item.Identity)
                        || rows.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                    {
                        pendingActionFocusItem = null;
                        return;
                    }

                    Button button = GetActionButtons(container).FirstOrDefault();
                    if (button != null)
                    {
                        _ = TryFocusActionButton(button);
                    }
                    pendingActionFocusItem = null;
                }));
            return true;
        }
        pendingActionFocusItem = null;
        return false;
    }

    private void RetryPendingActionFocus(
        KeywordSearchPresentationItem item,
        ListBox rows)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (pendingActionFocusItem == null
                    || !pendingActionFocusItem.Identity.Equals(item.Identity)
                    || rows.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                {
                    pendingActionFocusItem = null;
                    return;
                }

                Button button = GetActionButtons(container).FirstOrDefault();
                if (button != null)
                {
                    _ = TryFocusActionButton(button);
                }
                pendingActionFocusItem = null;
            }));
    }

    private bool TryFocusActionButton(Button button)
    {
        actionFocusScope = FocusManager.GetFocusScope(button);
        actionLogicalFocusTarget = button;
        FocusManager.SetFocusedElement(actionFocusScope, button);
        if (button.Focus() && ReferenceEquals(Keyboard.FocusedElement, button))
        {
            return true;
        }

        return ReferenceEquals(Keyboard.Focus(button), button);
    }

    private void CycleActionButtons(Button current, bool backwards)
    {
        ListBoxItem container = FindVisualAncestor<ListBoxItem>(current);
        if (container == null)
        {
            FocusInputTextBox();
            return;
        }

        Button[] buttons = GetActionButtons(container).ToArray();
        int currentIndex = Array.IndexOf(buttons, current);
        int nextIndex = backwards ? currentIndex - 1 : currentIndex + 1;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= buttons.Length)
        {
            FocusInputTextBox();
            return;
        }
        _ = TryFocusActionButton(buttons[nextIndex]);
    }

    private void ClearActionLogicalFocus()
    {
        if (actionFocusScope == null)
        {
            return;
        }
        if (ReferenceEquals(
            FocusManager.GetFocusedElement(actionFocusScope),
            actionLogicalFocusTarget))
        {
            FocusManager.SetFocusedElement(actionFocusScope, null);
        }
        actionFocusScope = null;
        actionLogicalFocusTarget = null;
    }

    private static IEnumerable<Button> GetActionButtons(ListBoxItem container)
        => FindVisualDescendants<Button>(container)
            .Where(button => button.Focusable
                && button.IsTabStop
                && button.Visibility == Visibility.Visible);

    private static bool HasAction(KeywordSearchPresentationItem item)
        => item != null
            && (item.CanRemoveFavorite || item.CanAddFavorite || item.CanDeleteHistory);

    private bool IsActionButton(DependencyObject element)
        => element is Button button
            && button.Focusable
            && button.IsTabStop
            && AssistancePopup.Child is DependencyObject popupChild
            && IsDescendantOf(button, popupChild);

    private bool IsShiftPressed(KeyEventArgs e)
        => shiftKeyDown
            || (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0
            || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private static T FindVisualAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        DependencyObject current = element;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root == null)
        {
            yield break;
        }
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
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

    private static int FindItemIndex(
        IReadOnlyList<KeywordSearchPresentationItem> items,
        KeywordSearchCandidateIdentity identity)
    {
        if (identity == null)
        {
            return -1;
        }
        for (int index = 0; index < (items?.Count ?? 0); index++)
        {
            if (items[index].Identity.Equals(identity))
            {
                return index;
            }
        }
        return -1;
    }

    private void SetSearchAccessibilityName()
    {
        string key = assistanceOwner?.Context == GridKeywordSearchContext.PlaylistSummary
            ? AppResources.Keyword_search_summary_editor_name
            : AppResources.Keyword_search_editor_name;
        AutomationProperties.SetName(InputTextBox, key ?? string.Empty);
    }

    private bool IsOwnedElement(DependencyObject element)
    {
        if (element == null)
        {
            return false;
        }
        if (element is Button { Tag: KeywordSearchPresentationItem actionItem }
            && HasAction(actionItem))
        {
            return true;
        }
        if (IsDescendantOf(element, this)
            || (AssistancePopup.Child is DependencyObject popupChild && IsDescendantOf(element, popupChild))
            || AssistancePopup.IsMouseOver)
        {
            return true;
        }
        return false;
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        DependencyObject current = child;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private enum KeywordSearchEditorAction
    {
        RemoveFavorite,
        AddFavorite,
        DeleteHistory
    }
}
