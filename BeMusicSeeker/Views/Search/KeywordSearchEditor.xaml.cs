using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    private bool interactionActive;

    private bool endingInteraction;

    private bool applyingPresentation;

    private DispatcherOperation pendingActionFocusOperation;

    private DispatcherOperation pendingInputFocusOperation;

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
        AddHandler(Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(EditorLostKeyboardFocus), handledEventsToo: true);
        AssistancePopup.Child.AddHandler(Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(EditorLostKeyboardFocus), handledEventsToo: true);
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
    /// 通常検索またはプレイリスト一覧検索の所有者を接続し、交換時は旧候補と保留focusを破棄します。
    /// </summary>
    /// <param name="owner">候補と保存式を所有する明示的な接続先。</param>
    internal void Configure(KeywordSearchAssistanceOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (ReferenceEquals(assistanceOwner, owner))
        {
            SubscribeOwner();
            ApplyPresentation(owner.Presentation);
            return;
        }

        EndInteraction(commitHistory: false);
        UnsubscribeOwner();
        presentation = null;
        selectedIndex = -1;
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
    /// ウィンドウ非活性時に候補と保留focusを閉じ、Popupの状態をViewModelへ持ち込みません。
    /// </summary>
    internal void CloseForWindowDeactivation()
    {
        EndInteraction(commitHistory: false);
    }

    /// <summary>
    /// 親画面で検出した外側クリックを通常の離脱処理へ接続し、所有するキーボードfocusを解除します。
    /// 候補が既に閉じている場合も、入力欄・Popupからの離脱と同じ一回の履歴確定を使います。
    /// </summary>
    /// <param name="clickedElement">マウスイベントを受け取った元の要素。</param>
    internal void ClearKeyboardFocusIfOutside(DependencyObject clickedElement)
    {
        if (IsOwnedElement(clickedElement))
        {
            return;
        }

        bool clearFocus = Keyboard.FocusedElement is DependencyObject focused && IsOwnedElement(focused);
        EndInteraction(commitHistory: true);
        if (clearFocus)
        {
            Keyboard.ClearFocus();
        }
    }

    private void KeywordSearchEditorLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeOwner();
        SubscribeResourceService();
        SetSearchAccessibilityName();
    }

    private void KeywordSearchEditorUnloaded(object sender, RoutedEventArgs e)
    {
        EndInteraction(commitHistory: false);
        UnsubscribeOwner();
        UnsubscribeResourceService();
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
        // 区画を再利用したボタンはLoadedが再発火しないため、実体化済みの表示だけを更新します。
        foreach (Button button in FindVisualDescendants<Button>(SectionsHost))
        {
            SetActionAccessibility(button);
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
        KeywordSearchAssistanceOwner owner = assistanceOwner;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() =>
                {
                    if (IsLoaded && ownerSubscribed && ReferenceEquals(owner, assistanceOwner))
                    {
                        ApplyPresentation(owner.Presentation);
                    }
                }));
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
        if (interactionActive && IsOwnedElement(e.OldFocus as DependencyObject))
        {
            return;
        }
        interactionActive = true;
        ApplyPresentation(assistanceOwner.Focus(InputTextBox.Text, InputTextBox.CaretIndex));
    }

    private void EditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (applyingPresentation || IsOwnedElement(e.NewFocus as DependencyObject))
        {
            return;
        }
        EndInteraction(commitHistory: true);
    }

    private void EndInteraction(bool commitHistory)
    {
        imeCompositionActive = false;
        if (endingInteraction || !interactionActive)
        {
            InvalidatePendingFocus();
            return;
        }
        interactionActive = false;
        endingInteraction = true;
        try
        {
            InvalidatePendingFocus();
            if (commitHistory)
            {
                CommitCurrentHistory();
            }
        }
        finally
        {
            try
            {
                if (assistanceOwner != null)
                {
                    ApplyPresentation(assistanceOwner.Blur());
                }
            }
            finally
            {
                endingInteraction = false;
            }
        }
    }

    private void InputTextBoxPreviewTextInputStart(object sender, TextCompositionEventArgs e)
        => imeCompositionActive = true;

    private void InputTextBoxPreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
        => imeCompositionActive = true;

    private void InputTextBoxTextInput(object sender, TextCompositionEventArgs e)
        => imeCompositionActive = false;

    private void EditorPreviewKeyDown(object sender, KeyEventArgs e)
        => e.Handled |= HandleEditorKey(
            e.OriginalSource as DependencyObject, e.Key, e.KeyboardDevice.Modifiers, e.ImeProcessedKey);

    /// <summary>
    /// 入力欄と所有Popupのキーを現在の修飾キーで判定し、同じ行内の移動へ接続します。
    /// </summary>
    /// <param name="source">入力元の実部品。</param>
    /// <param name="key">処理するキー。</param>
    /// <param name="modifiers">この入力で現在押されている修飾キー。</param>
    /// <param name="imeProcessedKey">IMEが処理した元のキー。</param>
    /// <returns>検索部品が処理した場合は <see langword="true"/>。</returns>
    internal bool HandleEditorKey(
        DependencyObject source, Key key, ModifierKeys modifiers, Key imeProcessedKey = Key.None)
    {
        if (ReferenceEquals(source, InputTextBox))
        {
            return HandleInputKey(key, modifiers, imeProcessedKey);
        }
        Button button = FindVisualAncestor<Button>(source);
        if (!IsActionButton(button) || IsImeCommitKey(key, imeProcessedKey))
        {
            return false;
        }
        if (key == Key.Tab)
        {
            CycleActionButtons(button, (modifiers & ModifierKeys.Shift) != 0);
            return true;
        }
        if (key == Key.Escape)
        {
            FocusInputTextBox();
            if (assistanceOwner != null)
            {
                ApplyPresentation(assistanceOwner.SuppressCurrentSnapshot());
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// 入力欄の検索支援キーを、WPF の PreviewKeyDown と同じ判定経路で処理します。
    /// 実際のフォーカス遷移は既存の WPF 入力経路が担当します。
    /// </summary>
    /// <param name="key">処理するキー。</param>
    /// <param name="modifiers">キー入力時の修飾キー。</param>
    /// <param name="imeProcessedKey">IME が処理した元のキー。</param>
    /// <returns>検索支援が入力を処理した場合は <see langword="true"/>。</returns>
    internal bool HandleInputKey(Key key, ModifierKeys modifiers, Key imeProcessedKey = Key.None)
    {
        if (key == Key.Escape && imeCompositionActive)
        {
            // The first Escape cancels the active IME composition. Leave the assistance
            // surface untouched; a subsequent Escape can dismiss the unchanged snapshot.
            imeCompositionActive = false;
            return false;
        }
        if (assistanceOwner == null || IsImeCommitKey(key, imeProcessedKey))
        {
            return false;
        }

        if (key == Key.Tab && (modifiers & ModifierKeys.Shift) != 0)
        {
            return AssistancePopup.IsOpen && TryFocusSavedRowAction();
        }

        if ((modifiers & ModifierKeys.Control) != 0 && key == Key.Space)
        {
            ApplyPresentation(assistanceOwner.ForceRefresh());
            SelectFirstItem();
            return true;
        }

        if (key == Key.Escape)
        {
            if (AssistancePopup.IsOpen)
            {
                ApplyPresentation(assistanceOwner.SuppressCurrentSnapshot());
                return true;
            }
            return false;
        }

        if (key is Key.Down or Key.Up)
        {
            if (!AssistancePopup.IsOpen)
            {
                ApplyPresentation(assistanceOwner.ForceRefresh());
            }
            Navigate(key == Key.Down ? 1 : -1);
            return true;
        }

        if ((key is Key.Enter or Key.Tab) && AssistancePopup.IsOpen)
        {
            if (ApplySelectedItem())
            {
                return true;
            }
            return false;
        }

        if (key == Key.Enter)
        {
            CommitCurrentHistory();
        }
        return false;
    }

    private bool IsImeCommitKey(Key key, Key imeProcessedKey)
    {
        return imeCompositionActive
            || key == Key.ImeProcessed
            || imeProcessedKey == Key.Enter
            || imeProcessedKey == Key.Return;
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
        bool equivalent = KeywordSearchAssistanceOwner.PresentationEquivalent(presentation, next);
        presentation = next;
        if (next.IsOpen && !endingInteraction)
        {
            interactionActive = true;
        }
        if (equivalent)
        {
            return;
        }
        selectedIndex = selectedItem == null
            ? -1
            : FindItemIndex(next.VisibleItems, selectedItem);
        if (!next.IsOpen)
        {
            InvalidatePendingFocus();
            selectedIndex = -1;
            AssistancePopup.IsOpen = false;
            SectionsHost.Children.Clear();
            renderedSections.Clear();
            return;
        }
        applyingPresentation = true;
        try
        {
            RenderSections(next);
            AssistancePopup.IsOpen = true;
            ApplySelectionVisuals();
        }
        finally
        {
            applyingPresentation = false;
        }
    }

    private void RenderSections(KeywordSearchPresentationState state)
    {
        KeywordSearchPresentationSection[] sections = state.Sections.Where(section => section.IsVisible).ToArray();
        if (sections.Length == renderedSections.Count
            && sections.Select((section, index) =>
                renderedSections[index].Tag is KeywordSearchPresentationSectionKind kind
                && kind == section.Kind
                && renderedSections[index].Items.Count == section.Items.Count
                && section.Items.Select((item, row) =>
                    item.HasSameRowIdentity((KeywordSearchPresentationItem)renderedSections[index].Items[row])).All(same => same))
                .All(same => same))
        {
            // 純表示変更や同じ候補の版更新では区画を保持し、仮想scrollを先頭へ戻しません。
            for (int index = 0; index < sections.Length; index++)
            {
                ListBox rows = renderedSections[index];
                var items = (ObservableCollection<KeywordSearchPresentationItem>)rows.ItemsSource;
                for (int row = 0; row < items.Count; row++)
                {
                    KeywordSearchPresentationItem nextItem = sections[index].Items[row];
                    if (!items[row].Identity.Equals(nextItem.Identity)
                        || items[row].Actions != nextItem.Actions
                        || !string.Equals(items[row].DisplayText, nextItem.DisplayText, StringComparison.Ordinal))
                    {
                        items[row] = nextItem;
                    }
                }
                if (rows.Parent is StackPanel panel && panel.Children[0] is TextBlock header)
                {
                    header.Text = sections[index].HeaderText;
                }
            }
            return;
        }
        InvalidatePendingFocus();
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
                ItemsSource = new ObservableCollection<KeywordSearchPresentationItem>(section.Items),
                Tag = section.Kind,
                Template = (ControlTemplate)FindResource("KeywordSearchListBoxTemplate"),
                ItemTemplate = (DataTemplate)FindResource("KeywordSearchItemTemplate"),
                ItemContainerStyle = (Style)FindResource("KeywordSearchListBoxItemStyle"),
                ItemsPanel = (ItemsPanelTemplate)FindResource("KeywordSearchVirtualizingItemsPanel"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 4, 16, 4),
                Background = Brushes.Transparent,
                Focusable = false,
                IsTabStop = false,
                SelectionMode = SelectionMode.Single,
                MinHeight = 0d,
                MaxHeight = section.IsScrollable
                    ? (section.Kind is KeywordSearchPresentationSectionKind.Favorites or KeywordSearchPresentationSectionKind.History
                        ? SavedRowHeight * 5 + 8
                        : CompletionRowHeight * 10 + 8)
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

    private void ActionButtonLoaded(object sender, RoutedEventArgs e)
        => SetActionAccessibility(sender);

    private static void SetActionAccessibility(object sender)
    {
        if (sender is not Button button)
        {
            return;
        }
        string text = button.Name switch
        {
            "RemoveFavoriteButton" => AppResources.Keyword_search_remove_favorite_tooltip,
            "AddFavoriteButton" => AppResources.Keyword_search_add_favorite_tooltip,
            "DeleteHistoryButton" => AppResources.Keyword_search_delete_history_tooltip,
            _ => null
        };
        if (text == null)
        {
            return;
        }
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

        pendingInputFocusOperation?.Abort();
        KeywordSearchAssistanceOwner owner = assistanceOwner;
        pendingInputFocusOperation = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                pendingInputFocusOperation = null;
                if (!IsLoaded || !interactionActive || !ReferenceEquals(owner, assistanceOwner)
                    || InputTextBox.IsKeyboardFocusWithin)
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
        pendingActionFocusOperation?.Abort();
        pendingActionFocusOperation = null;
        pendingActionFocusItem = null;
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
        FocusInputTextBox();
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
        FocusInputTextBox();
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
        FocusInputTextBox();
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
            int localIndex = selectedIndex - flattenedIndex;
            rows.SelectedIndex = localIndex >= 0 && localIndex < rows.Items.Count ? localIndex : -1;
            flattenedIndex += rows.Items.Count;
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
        pendingActionFocusOperation?.Abort();
        pendingActionFocusOperation = null;
        pendingActionFocusItem = null;
        foreach (ListBox rows in renderedSections)
        {
            // 同値再計算では表示中のitemを保持するため、新presentationとの参照一致を要求しません。
            KeywordSearchPresentationItem displayedItem = rows.Items.OfType<KeywordSearchPresentationItem>()
                .FirstOrDefault(candidate => candidate.HasSameRowIdentity(item));
            if (displayedItem == null)
            {
                continue;
            }
            pendingActionFocusItem = displayedItem;

            if (rows.ItemContainerGenerator.ContainerFromItem(displayedItem) is ListBoxItem container)
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
                        RetryPendingActionFocus(displayedItem, rows);
                    }
                    return true;
                }
            }

            rows.ScrollIntoView(displayedItem);
            RetryPendingActionFocus(displayedItem, rows);
            return true;
        }
        pendingActionFocusItem = null;
        return false;
    }

    private void RetryPendingActionFocus(
        KeywordSearchPresentationItem displayedItem,
        ListBox rows)
    {
        KeywordSearchAssistanceOwner owner = assistanceOwner;
        pendingActionFocusOperation = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                pendingActionFocusOperation = null;
                if (!IsLoaded || !interactionActive || !AssistancePopup.IsOpen
                    || !ReferenceEquals(owner, assistanceOwner)
                    || !renderedSections.Contains(rows)
                    || !ReferenceEquals(pendingActionFocusItem, displayedItem)
                    || rows.ItemContainerGenerator.ContainerFromItem(displayedItem) is not ListBoxItem container)
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

    private void InvalidatePendingFocus()
    {
        pendingActionFocusOperation?.Abort();
        pendingActionFocusOperation = null;
        pendingInputFocusOperation?.Abort();
        pendingInputFocusOperation = null;
        pendingActionFocusItem = null;
        ClearActionLogicalFocus();
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
        KeywordSearchPresentationItem item)
    {
        if (item == null)
        {
            return -1;
        }
        for (int index = 0; index < (items?.Count ?? 0); index++)
        {
            if (items[index].HasSameRowIdentity(item))
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
        if (IsDescendantOf(element, this)
            || (AssistancePopup.Child is DependencyObject popupChild && IsDescendantOf(element, popupChild)))
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
