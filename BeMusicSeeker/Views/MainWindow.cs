using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using NLog;
using Parago.Windows;
using Ribbit.Logging;
using Ribbit.Windows;

namespace BeMusicSeeker.Views;

/// <summary>
/// アプリケーションのメインウィンドウを表すクラスです。
/// UIの初期化、主要なイベントハンドリング（ドラッグ＆ドロップ、ウィンドウ状態の変更、閉じる処理など）、
/// および非同期のアップデートチェッカー等のグローバルな制御を統括します。
/// </summary>
public partial class MainWindow : Window, IComponentConnector, IStyleConnector, ISettingDialogPresentationPort
{
    public static readonly DependencyProperty PlaybackOverlayVisibilityProperty = DependencyProperty.Register(
        nameof(PlaybackOverlayVisibility),
        typeof(Visibility),
        typeof(MainWindow),
        new PropertyMetadata(Visibility.Collapsed));

    private static readonly Logger installPerformanceLogger = NLogWrapper.GetLogger("InstallPerformance.MainWindow");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private static readonly MethodInfo playlistTreeBringIndexIntoViewMethod = typeof(System.Windows.Controls.VirtualizingStackPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic) ?? typeof(System.Windows.Controls.VirtualizingPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic);

    private int _duplicateMaintenanceSelectionVersion;

    private FrameworkElement activeOverlayDialog;

    private PlaylistPropertyDialog activePlaylistPropertyDialog;

    private PlaylistSummaryBulkEditDialog activePlaylistSummaryBulkEditDialog;

    private Task playlistPropertyDialogCleanupTask = Task.CompletedTask;

    private Task playlistSummaryBulkEditDialogCleanupTask = Task.CompletedTask;

    private int terminalShutdownStarted;

    private bool terminalWindowCloseAuthorized;

    private SettingsWindow settingsWindow;

    private TaskCompletionSource<object> settingsPresentationClosed;

#nullable enable
    private readonly Action<SettingsWindow>? settingsWindowCreated;
#nullable restore

    private readonly MainWindowLibraryReloadMenuTerminal libraryReloadMenuTerminal;

    private readonly MainWindowRegularLibraryTreeTerminal regularLibraryTreeTerminal;

    private readonly MainWindowMaintenanceTreeTerminal maintenanceTreeTerminal;

    private readonly MainWindowInstallTreeTerminal installTreeTerminal;

    private readonly MainWindowZeroNoteRecheckTerminal zeroNoteRecheckTerminal;

    private readonly MainWindowColumnResetTerminal columnResetTerminal;

    private readonly MainWindowRootFolderUnregisterTerminal rootFolderUnregisterTerminal;

    private readonly MainWindowFolderAutoRenameTerminal folderAutoRenameTerminal;

    private readonly MainWindowDuplicateMaintenanceTerminal duplicateMaintenanceTerminal;

    private readonly MainWindowMaintenanceRescanTerminal maintenanceRescanTerminal;

    private readonly MainWindowPackageCatalogTerminal packageCatalogTerminal;

    private readonly MainWindowPendingInstallEstimationTerminal pendingInstallEstimationTerminal;

    private readonly MainWindowPendingInstallationTerminal pendingInstallationTerminal;

    private readonly MainWindowPendingPackageMutationViewTerminal pendingPackageMutationViewTerminal;

    private readonly MainWindowInstalledLocationRepairTerminal installedLocationRepairTerminal;

    private readonly MainWindowPendingBulkMaintenanceTerminal pendingBulkMaintenanceTerminal;

    private readonly MainWindowMainChartCellEditTerminal mainChartCellEditTerminal;

    private readonly MainWindowSelectedChartContextMenuTerminals selectedChartContextMenuTerminals;

    private readonly MainWindowPlaybackTerminal playbackTerminal;

    private readonly MainWindowPlaylistWorkspaceTerminals playlistWorkspaceTerminals;

    private readonly PlaylistLampViewerWindowManager playlistLampViewerWindowManager;

    private readonly MainWindowForegroundTerminal mainWindowForegroundTerminal;

    private readonly MainWindowProgressStatusBarTerminals progressStatusBarTerminals;

    private readonly IUiDialogService playlistWorkspaceDialogService;

    private MainWindowViewModel subscribedViewModel;

    private bool suppressPlaylistLampTreeSelection;

    private long lastNormalLibraryFirstVisibleRequestId;

    private PerformanceInteraction lastPlaylistSummaryFirstVisibleInteraction;

    public Visibility PlaybackOverlayVisibility
    {
        get => (Visibility)GetValue(PlaybackOverlayVisibilityProperty);
        private set => SetValue(PlaybackOverlayVisibilityProperty, value);
    }

    /// <summary>Gets the owner-scoped manager for modeless playlist lamp viewers.</summary>
    internal PlaylistLampViewerWindowManager PlaylistLampViewerWindows => playlistLampViewerWindowManager;

    private static void ThrowIfPickerFailed(UiDialogStatus status, Exception exception, string routeName)
    {
        if (status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + status, exception);
    }

    private static void ThrowIfWindowDialogFailed(UiDialogStatus status, Exception exception, string routeName)
    {
        if (status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + status, exception);
    }

    private static void ThrowIfUiDialogNotShown(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " failed: no result");
        }
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + result.Status, result.Exception);
    }

    private async Task NotifyMainWindowOperationFailureAsync(Exception exception, string routeName)
    {
        if (exception is OperationCanceledException)
        {
            return;
        }
        Task.FromException(exception).ObserveFault(routeName);
        try
        {
            UiDialogResult notification = await playlistWorkspaceDialogService.ShowMessageAsync(
                new UiMessageRequest(
                    BeMusicSeeker.Properties.Resources.Msg_error_unexpected
                        + Environment.NewLine
                        + exception.Message,
                    BeMusicSeeker.Properties.Resources.Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Hand,
                    MessageBoxResult.OK,
                    owner: this));
            if (notification == null
                || notification.Status is not UiDialogStatus.Accepted
                    and not UiDialogStatus.CancelledByUser
                    and not UiDialogStatus.ClosedByUser)
            {
                Exception notificationFailure = notification?.Exception
                    ?? new InvalidOperationException(
                        routeName
                        + " failure notification was not shown ("
                        + (notification?.Status.ToString() ?? "no result")
                        + ").");
                Task.FromException(notificationFailure).ObserveFault(routeName + " failure notification");
            }
        }
        catch (Exception notificationException)
        {
            Task.FromException(notificationException).ObserveFault(routeName + " failure notification");
        }
    }

    internal void ShowOverlayDialog(FrameworkElement dialog)
    {
        if (dialog == null)
        {
            throw new ArgumentNullException(nameof(dialog));
        }
        if (activeOverlayDialog != null
            && !ReferenceEquals(activeOverlayDialog, dialog)
            && activeOverlayDialog.Visibility == Visibility.Visible)
        {
            throw new InvalidOperationException("Another overlay dialog is already visible: " + activeOverlayDialog.GetType().Name);
        }

        activeOverlayDialog = dialog;
        if (HidesPlaybackSurface(dialog))
        {
            PlaybackOverlayVisibility = Visibility.Visible;
        }
        dialog.Visibility = Visibility.Visible;
        dialog.Focus();
    }

    internal void HideOverlayDialog(FrameworkElement dialog)
    {
        if (dialog == null)
        {
            return;
        }

        dialog.Visibility = Visibility.Hidden;
        if (ReferenceEquals(activeOverlayDialog, dialog))
        {
            activeOverlayDialog = null;
            if (HidesPlaybackSurface(dialog))
            {
                PlaybackOverlayVisibility = Visibility.Collapsed;
            }
        }
    }

    private bool HidesPlaybackSurface(FrameworkElement dialog)
    {
        return ReferenceEquals(dialog, loadPlaylistURIDialog);
    }

    private async void addRootFolderMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel { SettingDialog: { } settingDialogViewModel } viewModel)
        {
            return;
        }

        UiFolderPickerResult result = await new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                selectedPath: viewModel.LibraryFolderTree.BMSParentFolderList?.FirstOrDefault(),
                multiselect: false,
                owner: this));
        ThrowIfPickerFailed(result.Status, result.Error, "Main window add root folder picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            try
            {
                await settingDialogViewModel.AddBmsSearchRootPathFromMainWindowPicker(result.FolderPath)
                    .LoggingAndPropagate("addRootFolderMenuItemClick");
            }
            catch (LibraryDirectoryPreflightException)
            {
                // MainWindowViewModel が cleanup 後に warning を表示するため、
                // shell terminal では同じ失敗を再通知しない。
            }
            catch (Exception exception)
            {
                UiDialogResult notification = await new UiDialogCoordinator().ShowMessageAsync(new UiMessageRequest(
                    SettingsFailureMessage.Format(exception), BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand,
                    MessageBoxResult.OK, owner: this));
                UiDialogRoute.ThrowIfNotShown(notification, "Settings persistence failure notification");
            }
        }
    }

    private ContextMenu _lastOpenedContextMenu;

    private PlayHistoryDateSearchTerm playHistoryDateSearchSnapshot;

    // NOTE:
    // TreeView の仮想化 (Recycling) 有効時は、画面外ノードのコンテナが VisualTree から外れる。
    // そのため「VisualTree を再帰して選択状態を判定する」実装は false negative を起こす。
    // ここでは最後に確定した選択ノードの所属セクションを保持し、UIコンテナ有無に依存しない判定を行う。
    private enum TreeSelectionSection
    {
        None,
        Playlist,
        PlayHistory,
        InstallPending,
        InstallInstalled,
        FullScanCheck,
        ChartInfoParseError,
        Other
    }

    private TreeSelectionSection _currentTreeSelectionSection = TreeSelectionSection.None;


    private CancellationTokenSource relatedDocumentRequestCancellation;

    private readonly Storyboard treeViewItemInstantStoryBoardPlaylistTable = new();


    private bool startupInitialSelectionApplied;

    private PropertyChangedEventHandler _startupInitialSelectionReadyHandler;

    private StartupProgressWorkflowOwner _startupInitialSelectionReadyOwner;

    private bool windowStateCapturedForClosing;

    /// <summary>
    /// <see cref="MainWindow"/> クラスの新しいインスタンスを初期化します。
    /// UIコンポーネントの構築、TreeViewのイベントハンドラ登録、
    /// 設定のプロパティ変更リスナの初期化、および非同期のアップデートチェックを開始します。
    /// </summary>
    /// <param name="viewModel">シェルが表示し操作する状態とワークフロー。</param>
    public MainWindow(MainWindowViewModel viewModel)
        : this(viewModel, null)
    {
    }

    /// <summary>
    /// 設定ウィンドウが owner 設定や表示を行う前に、インスタンス固有の構成処理を適用できる
    /// <see cref="MainWindow"/> を初期化します。
    /// </summary>
    /// <param name="viewModel">シェルが表示し操作する状態とワークフロー。</param>
    /// <param name="settingsWindowCreated">
    /// 設定ウィンドウの既存構成後、ダイアログ coordinator へ返す直前に呼び出す任意の処理。
    /// </param>
    /// <param name="playlistWorkspaceDialogService">
    /// playlist Property/Bulk modal routes が使う coordinator。未指定時は既定の production coordinator を使用します。
    /// </param>
    /// <param name="mainWindowForegroundTerminal">
    /// playlist lamp navigation 成功後の shell restore/activation/focus terminal。未指定時は WPF shell に接続します。
    /// </param>
#nullable enable
    internal MainWindow(
        MainWindowViewModel viewModel,
        Action<SettingsWindow>? settingsWindowCreated,
        IUiDialogService? playlistWorkspaceDialogService = null,
        MainWindowForegroundTerminal? mainWindowForegroundTerminal = null)
        : this(
            viewModel,
            settingsWindowCreated,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            playlistWorkspaceDialogService,
            mainWindowForegroundTerminal)
    {
    }

    /// <summary>
    /// Initializes the shell with feature-specific terminals for compiled presentation routes.
    /// </summary>
    /// <param name="viewModel">シェルが表示し操作する状態とワークフロー。</param>
    /// <param name="settingsWindowCreated">設定ウィンドウの既存構成後に呼び出す処理。</param>
    /// <param name="libraryReloadMenuTerminal">通常/ルート library menu reload terminal。</param>
    /// <param name="regularLibraryTreeTerminal">通常 library tree selection terminal。</param>
    /// <param name="maintenanceTreeTerminal">maintenance tree selection terminal。</param>
    /// <param name="installTreeTerminal">install tree selection terminal。</param>
    /// <param name="zeroNoteRecheckTerminal">zero-note recheck terminal。</param>
    /// <param name="columnResetTerminal">column reset terminal。</param>
    /// <param name="rootFolderUnregisterTerminal">library search-root removal terminal。</param>
    internal MainWindow(
        MainWindowViewModel viewModel,
        Action<SettingsWindow>? settingsWindowCreated,
        MainWindowLibraryReloadMenuTerminal? libraryReloadMenuTerminal,
        MainWindowRegularLibraryTreeTerminal? regularLibraryTreeTerminal,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal,
        MainWindowInstallTreeTerminal? installTreeTerminal,
        MainWindowZeroNoteRecheckTerminal? zeroNoteRecheckTerminal,
        MainWindowColumnResetTerminal? columnResetTerminal,
        MainWindowRootFolderUnregisterTerminal? rootFolderUnregisterTerminal)
        : this(
            viewModel,
            settingsWindowCreated,
            libraryReloadMenuTerminal,
            regularLibraryTreeTerminal,
            maintenanceTreeTerminal,
            installTreeTerminal,
            zeroNoteRecheckTerminal,
            columnResetTerminal,
            rootFolderUnregisterTerminal,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null)
    {
    }

    /// <summary>
    /// Initializes the shell with feature-specific terminals for compiled package and maintenance routes.
    /// </summary>
    /// <param name="viewModel">シェルが表示し操作する状態とワークフロー。</param>
    /// <param name="settingsWindowCreated">設定ウィンドウの既存構成後に呼び出す処理。</param>
    /// <param name="libraryReloadMenuTerminal">通常/ルート library menu reload terminal。</param>
    /// <param name="regularLibraryTreeTerminal">通常 library tree selection terminal。</param>
    /// <param name="maintenanceTreeTerminal">maintenance tree selection terminal。</param>
    /// <param name="installTreeTerminal">install tree selection terminal。</param>
    /// <param name="zeroNoteRecheckTerminal">zero-note recheck terminal。</param>
    /// <param name="columnResetTerminal">column reset terminal。</param>
    /// <param name="rootFolderUnregisterTerminal">library search-root removal terminal。</param>
    /// <param name="folderAutoRenameTerminal">chart and folder auto-rename terminal。</param>
    /// <param name="duplicateMaintenanceTerminal">duplicate-folder maintenance terminal。</param>
    /// <param name="maintenanceRescanTerminal">full resource-health rescan terminal。</param>
    /// <param name="packageCatalogTerminal">package-catalog mutation terminal。</param>
    /// <param name="pendingInstallEstimationTerminal">pending install-destination estimation terminal。</param>
    /// <param name="pendingInstallationTerminal">pending package installation terminal。</param>
    /// <param name="pendingPackageMutationViewTerminal">pending package mutation view-application terminal。</param>
    /// <param name="installedLocationRepairTerminal">installed-location repair terminal。</param>
    /// <param name="pendingBulkMaintenanceTerminal">pending-package bulk maintenance terminal。</param>
    /// <param name="mainChartCellEditTerminal">main chart cell-edit lifecycle terminal。</param>
    /// <param name="selectedChartContextMenuTerminals">selected-chart context-menu terminals。</param>
    /// <param name="playbackTerminal">main-table playback selection and activation terminal。</param>
    /// <param name="playlistWorkspaceTerminals">playlist workspace mutation terminals。</param>
    /// <param name="progressStatusBarTerminals">compiled status-bar action terminals。</param>
    /// <param name="playlistWorkspaceDialogService">playlist Property/Bulk modal route の coordinator。未指定時は既定 coordinator を使用します。</param>
    /// <param name="mainWindowForegroundTerminal">successful playlist lamp navigation 後の shell focus terminal。</param>
    internal MainWindow(
        MainWindowViewModel viewModel,
        Action<SettingsWindow>? settingsWindowCreated,
        MainWindowLibraryReloadMenuTerminal? libraryReloadMenuTerminal,
        MainWindowRegularLibraryTreeTerminal? regularLibraryTreeTerminal,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal,
        MainWindowInstallTreeTerminal? installTreeTerminal,
        MainWindowZeroNoteRecheckTerminal? zeroNoteRecheckTerminal,
        MainWindowColumnResetTerminal? columnResetTerminal,
        MainWindowRootFolderUnregisterTerminal? rootFolderUnregisterTerminal,
        MainWindowFolderAutoRenameTerminal? folderAutoRenameTerminal,
        MainWindowDuplicateMaintenanceTerminal? duplicateMaintenanceTerminal,
        MainWindowMaintenanceRescanTerminal? maintenanceRescanTerminal,
        MainWindowPackageCatalogTerminal? packageCatalogTerminal,
        MainWindowPendingInstallEstimationTerminal? pendingInstallEstimationTerminal,
        MainWindowPendingInstallationTerminal? pendingInstallationTerminal,
        MainWindowInstalledLocationRepairTerminal? installedLocationRepairTerminal,
        MainWindowPendingBulkMaintenanceTerminal? pendingBulkMaintenanceTerminal,
        MainWindowMainChartCellEditTerminal? mainChartCellEditTerminal,
        MainWindowSelectedChartContextMenuTerminals? selectedChartContextMenuTerminals = null,
        MainWindowPlaybackTerminal? playbackTerminal = null,
        MainWindowPlaylistWorkspaceTerminals? playlistWorkspaceTerminals = null,
        MainWindowProgressStatusBarTerminals? progressStatusBarTerminals = null,
        MainWindowPendingPackageMutationViewTerminal? pendingPackageMutationViewTerminal = null,
        IUiDialogService? playlistWorkspaceDialogService = null,
        MainWindowForegroundTerminal? mainWindowForegroundTerminal = null)
    {
        if (viewModel == null)
        {
            throw new ArgumentNullException(nameof(viewModel));
        }
        this.settingsWindowCreated = settingsWindowCreated;
        this.libraryReloadMenuTerminal = libraryReloadMenuTerminal ?? MainWindowLibraryReloadMenuTerminal.Create(viewModel);
        this.regularLibraryTreeTerminal = regularLibraryTreeTerminal ?? MainWindowRegularLibraryTreeTerminal.Create(viewModel);
        this.maintenanceTreeTerminal = maintenanceTreeTerminal ?? MainWindowMaintenanceTreeTerminal.Create(viewModel);
        this.installTreeTerminal = installTreeTerminal ?? MainWindowInstallTreeTerminal.Create(viewModel);
        this.zeroNoteRecheckTerminal = zeroNoteRecheckTerminal ?? MainWindowZeroNoteRecheckTerminal.Create(viewModel);
        this.columnResetTerminal = columnResetTerminal ?? MainWindowColumnResetTerminal.Create(viewModel);
        this.rootFolderUnregisterTerminal = rootFolderUnregisterTerminal ?? MainWindowRootFolderUnregisterTerminal.Create(viewModel);
        this.folderAutoRenameTerminal = folderAutoRenameTerminal ?? MainWindowFolderAutoRenameTerminal.Create(viewModel);
        this.duplicateMaintenanceTerminal = duplicateMaintenanceTerminal ?? MainWindowDuplicateMaintenanceTerminal.Create(viewModel);
        this.maintenanceRescanTerminal = maintenanceRescanTerminal ?? MainWindowMaintenanceRescanTerminal.Create(viewModel);
        this.packageCatalogTerminal = packageCatalogTerminal ?? MainWindowPackageCatalogTerminal.Create(viewModel);
        this.pendingInstallEstimationTerminal = pendingInstallEstimationTerminal ?? MainWindowPendingInstallEstimationTerminal.Create(viewModel);
        this.pendingInstallationTerminal = pendingInstallationTerminal ?? MainWindowPendingInstallationTerminal.Create(viewModel);
        this.pendingPackageMutationViewTerminal = pendingPackageMutationViewTerminal
            ?? new MainWindowPendingPackageMutationViewTerminal(
                () => treeViewItemInstallPending?.IsSelected == true,
                () => treeViewItemInstallPending?.Items.Count ?? 0,
                mode => viewModel.RegularChartList.NavigateInstallAsync(mode),
                viewModel.FileDbMutationDialogs);
        this.installedLocationRepairTerminal = installedLocationRepairTerminal ?? MainWindowInstalledLocationRepairTerminal.Create(viewModel);
        this.pendingBulkMaintenanceTerminal = pendingBulkMaintenanceTerminal ?? MainWindowPendingBulkMaintenanceTerminal.Create(viewModel);
        this.mainChartCellEditTerminal = mainChartCellEditTerminal ?? MainWindowMainChartCellEditTerminal.Create(viewModel);
        this.selectedChartContextMenuTerminals = selectedChartContextMenuTerminals
            ?? MainWindowSelectedChartContextMenuTerminals.Create(viewModel);
        this.playbackTerminal = playbackTerminal ?? MainWindowPlaybackTerminal.Create(viewModel);
        this.playlistWorkspaceTerminals = playlistWorkspaceTerminals
            ?? MainWindowPlaylistWorkspaceTerminals.Create(
                viewModel,
                () => newlyInstalledTreeViewItem.IsExpanded = true);
        this.progressStatusBarTerminals = progressStatusBarTerminals
            ?? MainWindowProgressStatusBarTerminals.Create(viewModel);
        this.playlistWorkspaceDialogService = playlistWorkspaceDialogService
            ?? new UiDialogCoordinator();
        this.mainWindowForegroundTerminal = mainWindowForegroundTerminal
            ?? MainWindowForegroundTerminal.Create(this);
        this.playlistLampViewerWindowManager = new(
            this,
            viewModel.PlaylistWorkspace,
            this.playlistWorkspaceDialogService,
            historicalSourceContextFactory: viewModel.ResolvePlaylistLampHistoricalScoreSourceContext);
        DataContext = viewModel;
        InitializeComponent();
        KeywordSearchEditor.Configure(viewModel.ChartFilters.KeywordSearchAssistanceOwner);
        PlaylistSummaryKeywordSearchEditor.Configure(
            viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner);
        viewModel.SettingDialog.AttachPresentationPort(this);
        ApplySavedTreeViewWidth();
        AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(keywordSearchWindowPreviewMouseDown), true);
        Deactivated += MainWindow_Deactivated;
        viewModel.PlaylistWorkspace.PlaylistSummarySelectionRestoreRequested += MainWindowViewModel_PlaylistSummarySelectionRestoreRequested;
        SubscribeViewModelUiInteractions(viewModel);
        Closed += MainWindow_Closed;
        ContentRendered += MainWindow_ContentRendered;

        // Add handler that catches already-handled TreeViewItem.Selected events to synchronize TreeView exclusivity
        gridTreePane.AddHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler(gridTreePane_TreeViewItemSelected), true);

        viewModel.ShellActivationWorkflow.ActivateConstructedShell();
    }
#nullable restore

    private async void MainWindow_ContentRendered(object sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            ApplyStartupInitialSelectionRequest();
            return;
        }

        Task initializationTask = viewModel.ShellActivationWorkflow.ActivateRenderedShell(
            ApplyStartupInitialSelectionRequest,
            action => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, action),
            CanPresentElevatedProcessWarning);
        await initializationTask.LoggingAndPropagate("MainWindow_ContentRendered");
    }

    private bool CanPresentElevatedProcessWarning()
    {
        if (IsShellClosingOrClosed() || !IsLoaded || Visibility != Visibility.Visible)
        {
            return false;
        }

        Application application = Application.Current;
        if (application == null)
        {
            return false;
        }

        Dispatcher applicationDispatcher = application.Dispatcher;
        return applicationDispatcher != null
            && !applicationDispatcher.HasShutdownStarted
            && !applicationDispatcher.HasShutdownFinished
            && !Dispatcher.HasShutdownStarted
            && !Dispatcher.HasShutdownFinished;
    }

    private bool IsShellClosingOrClosed()
    {
        return (base.DataContext as MainWindowViewModel)?.ShellShutdownWorkflow.IsClosingOrClosed == true;
    }

    private bool IsPlaylistUrlDownloadRunning
    {
        get
        {
            return (base.DataContext as MainWindowViewModel)?.PlaylistWorkspace.IsPlaylistUrlDownloadRunning == true;
        }
    }

    private void SubscribeViewModelUiInteractions(MainWindowViewModel viewModel)
    {
        if (viewModel == null || ReferenceEquals(subscribedViewModel, viewModel))
        {
            return;
        }
        UnsubscribeViewModelUiInteractions();
        subscribedViewModel = viewModel;
        viewModel.PropertyChanged += MainWindowViewModel_PropertyChanged;
        playlistWorkspaceTerminals.UrlInstallTreeExpansionEventSource
            .Subscribe(MainWindow_PlaylistUrlInstallTreeExpansionRequested);
        viewModel.PlaylistWorkspace.PlaylistPropertyValidationError += MainWindow_PlaylistPropertyValidationError;
        viewModel.PlaylistWorkspace.PlaylistPropertyExternalSyncConfirmationRequested += MainWindow_PlaylistPropertyExternalSyncConfirmationRequested;
        viewModel.PlaylistWorkspace.PlaylistPropertyInvalidOutputDirectoryRequested += MainWindow_PlaylistPropertyInvalidOutputDirectoryRequested;
        viewModel.PlaylistWorkspace.PlaylistPropertyExternalSyncFailed += MainWindow_PlaylistPropertyExternalSyncFailed;
        viewModel.PlaylistWorkspace.MutationRejected += MainWindow_PlaylistWorkspaceMutationRejected;
        viewModel.PlaylistWorkspace.PlaylistRemovalWorkflow.InvalidOutputDirectoryRequested += MainWindow_PlaylistRemovalWorkflowInvalidOutputDirectoryRequested;
        viewModel.PlaylistWorkspace.PlaylistSummaryBulkInvalidOutputDirectoryRequested += MainWindow_PlaylistWorkspacePlaylistSummaryBulkInvalidOutputDirectoryRequested;
        viewModel.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += MainWindow_PlaylistWorkspacePlaylistOperationNotificationPresentationRequested;
        viewModel.PlaylistWorkspace.ExternalPlaylistImportQueueSummaryReady += MainWindow_PlaylistWorkspaceExternalPlaylistImportQueueSummaryReady;
        viewModel.PlaylistWorkspace.ExternalPlaylistImportSummaryRefreshFailed += MainWindow_PlaylistWorkspaceExternalPlaylistImportSummaryRefreshFailed;
        viewModel.PlaylistWorkspace.BeatorajaTableUrlImportConfirmationRequested += MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportConfirmationRequested;
        viewModel.PlaylistWorkspace.BeatorajaTableUrlImportNotificationRequested += MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportNotificationRequested;
        viewModel.PlaylistWorkspace.BeatorajaTableUrlImportSummaryReady += MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportSummaryReady;
        viewModel.PlaylistWorkspace.PlaylistLampNavigationRequested += MainWindow_PlaylistLampNavigationRequested;
        viewModel.FolderAutoRenameWorkflow.TerminalPublished += MainWindowViewModel_FolderAutoRenameTerminalPublished;
        viewModel.StartupUpdateWorkflow.PresentationRequested += MainWindowViewModel_StartupUpdatePresentationRequested;
        viewModel.StartupUpdateWorkflow.FailurePresentationRequested += MainWindowViewModel_StartupUpdateFailurePresentationRequested;
        viewModel.StartupUpdateWorkflow.ApplicationShutdownRequested += MainWindowViewModel_StartupUpdateApplicationShutdownRequested;
        viewModel.ShellShutdownWorkflow.OperationModeRestartRequested += MainWindowViewModel_OperationModeRestartRequested;
        viewModel.ElevatedProcessWarningWorkflow.PresentationRequested += MainWindowViewModel_ElevatedProcessWarningPresentationRequested;
    }

    private void UnsubscribeViewModelUiInteractions()
    {
        if (subscribedViewModel == null)
        {
            return;
        }
        subscribedViewModel.PropertyChanged -= MainWindowViewModel_PropertyChanged;
        subscribedViewModel.SettingDialog.DetachPresentationPort(this);
        playlistWorkspaceTerminals.UrlInstallTreeExpansionEventSource
            .Unsubscribe(MainWindow_PlaylistUrlInstallTreeExpansionRequested);
        subscribedViewModel.PlaylistWorkspace.PlaylistPropertyValidationError -= MainWindow_PlaylistPropertyValidationError;
        subscribedViewModel.PlaylistWorkspace.PlaylistPropertyExternalSyncConfirmationRequested -= MainWindow_PlaylistPropertyExternalSyncConfirmationRequested;
        subscribedViewModel.PlaylistWorkspace.PlaylistPropertyInvalidOutputDirectoryRequested -= MainWindow_PlaylistPropertyInvalidOutputDirectoryRequested;
        subscribedViewModel.PlaylistWorkspace.PlaylistPropertyExternalSyncFailed -= MainWindow_PlaylistPropertyExternalSyncFailed;
        subscribedViewModel.PlaylistWorkspace.MutationRejected -= MainWindow_PlaylistWorkspaceMutationRejected;
        subscribedViewModel.PlaylistWorkspace.PlaylistRemovalWorkflow.InvalidOutputDirectoryRequested -= MainWindow_PlaylistRemovalWorkflowInvalidOutputDirectoryRequested;
        subscribedViewModel.PlaylistWorkspace.PlaylistSummaryBulkInvalidOutputDirectoryRequested -= MainWindow_PlaylistWorkspacePlaylistSummaryBulkInvalidOutputDirectoryRequested;
        subscribedViewModel.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested -= MainWindow_PlaylistWorkspacePlaylistOperationNotificationPresentationRequested;
        subscribedViewModel.PlaylistWorkspace.ExternalPlaylistImportQueueSummaryReady -= MainWindow_PlaylistWorkspaceExternalPlaylistImportQueueSummaryReady;
        subscribedViewModel.PlaylistWorkspace.ExternalPlaylistImportSummaryRefreshFailed -= MainWindow_PlaylistWorkspaceExternalPlaylistImportSummaryRefreshFailed;
        subscribedViewModel.PlaylistWorkspace.BeatorajaTableUrlImportConfirmationRequested -= MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportConfirmationRequested;
        subscribedViewModel.PlaylistWorkspace.BeatorajaTableUrlImportNotificationRequested -= MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportNotificationRequested;
        subscribedViewModel.PlaylistWorkspace.BeatorajaTableUrlImportSummaryReady -= MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportSummaryReady;
        subscribedViewModel.PlaylistWorkspace.PlaylistLampNavigationRequested -= MainWindow_PlaylistLampNavigationRequested;
        subscribedViewModel.FolderAutoRenameWorkflow.TerminalPublished -= MainWindowViewModel_FolderAutoRenameTerminalPublished;
        subscribedViewModel.StartupUpdateWorkflow.PresentationRequested -= MainWindowViewModel_StartupUpdatePresentationRequested;
        subscribedViewModel.StartupUpdateWorkflow.FailurePresentationRequested -= MainWindowViewModel_StartupUpdateFailurePresentationRequested;
        subscribedViewModel.StartupUpdateWorkflow.ApplicationShutdownRequested -= MainWindowViewModel_StartupUpdateApplicationShutdownRequested;
        subscribedViewModel.ShellShutdownWorkflow.OperationModeRestartRequested -= MainWindowViewModel_OperationModeRestartRequested;
        subscribedViewModel.ElevatedProcessWarningWorkflow.PresentationRequested -= MainWindowViewModel_ElevatedProcessWarningPresentationRequested;
        subscribedViewModel = null;
    }

    private void MainWindowViewModel_FolderAutoRenameTerminalPublished()
    {
        RefreshCustomTableViewDisplayAsync();
    }

    private void MainWindowViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e?.PropertyName != nameof(MainWindowViewModel.IsInitializationCompleted)
            || sender is not MainWindowViewModel viewModel
            || !viewModel.IsInitializationCompleted)
        {
            return;
        }

        Action attachPlaybackPanel = delegate
        {
            if (IsShellClosingOrClosed()
                || !ReferenceEquals(subscribedViewModel, viewModel)
                || !viewModel.IsInitializationCompleted)
            {
                return;
            }
            viewModel.PlaybackPanel.AttachWindowHost(new Win32ExternalPlayerWindowHost(playbackPanelView.PlayerHostHandle));
            playbackPanelView.EnsureSelectedSurfaceAvailable();
        };
        if (Dispatcher.CheckAccess())
        {
            attachPlaybackPanel();
        }
        else
        {
            Dispatcher.BeginInvoke(attachPlaybackPanel);
        }
    }

    private void MainWindow_PlaylistWorkspaceExternalPlaylistImportQueueSummaryReady(
        object sender,
        ExternalPlaylistImportQueueSummaryReadyEventArgs request)
    {
        ShowExternalPlaylistImportQueueSummary(request?.Summary);
    }

    private void MainWindow_PlaylistWorkspaceMutationRejected(
        object sender,
        PlaylistWorkspaceMutationRejectedEventArgs request)
    {
        if (request == null)
        {
            return;
        }
        string message = request.IsBusy
            ? BeMusicSeeker.Properties.Resources.Warn_PlaylistMutationBusy
            : request.IsStale
                ? BeMusicSeeker.Properties.Resources.Warn_PlaylistMutationStale
                : request.Kind switch
                {
                    PlaylistWorkspaceMutationKind.RenameFolder => BeMusicSeeker.Properties.Resources.Msg_failed_rename_playlist_folder,
                    PlaylistWorkspaceMutationKind.RemoveFolder => BeMusicSeeker.Properties.Resources.Msg_failed_remove_playlist_folder,
                    PlaylistWorkspaceMutationKind.CreateFolder => BeMusicSeeker.Properties.Resources.Msg_failed_create_playlist_folder,
                    PlaylistWorkspaceMutationKind.AddEntries => BeMusicSeeker.Properties.Resources.Msg_failed_add_playlist_entry,
                    PlaylistWorkspaceMutationKind.RemoveEntries => BeMusicSeeker.Properties.Resources.Msg_failed_remove_playlist_entry,
                    PlaylistWorkspaceMutationKind.Reload => BeMusicSeeker.Properties.Resources.Msg_failed_load_playlist,
                    _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, null)
                };
        ShowPlaylistWorkspaceUiMessage(
            message,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxImage.Hand,
            "playlist mutation rejection notification");
    }

    /// <summary>
    /// playlist workspace の同期通知を、workspace と同じ owner-scoped dialog service で表示します。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    /// <param name="icon">表示する icon。</param>
    /// <param name="routeName">失敗時に識別する route 名。</param>
    private void ShowPlaylistWorkspaceUiMessage(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        string routeName)
    {
        UiDialogResult result = playlistWorkspaceDialogService
            .ShowMessageAsync(new UiMessageRequest(
                messageBoxText,
                caption,
                MessageBoxButton.OK,
                icon,
                MessageBoxResult.OK,
                owner: this))
            .GetAwaiter()
            .GetResult();
        ThrowIfUiDialogNotShown(result, routeName);
    }

    private void MainWindow_PlaylistRemovalWorkflowInvalidOutputDirectoryRequested(
        object sender,
        PlaylistRemovalInvalidOutputDirectoryEventArgs request)
    {
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Warn_CustomFolderOutputDirInvalid,
            BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
            MessageBoxImage.Exclamation,
            request.RouteName);
    }

    private void MainWindow_PlaylistWorkspacePlaylistSummaryBulkInvalidOutputDirectoryRequested(
        object sender,
        PlaylistSummaryBulkInvalidOutputDirectoryEventArgs request)
    {
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Warn_CustomFolderOutputDirInvalid,
            BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
            MessageBoxImage.Exclamation,
            request.RouteName);
    }

    private void MainWindow_PlaylistWorkspacePlaylistOperationNotificationPresentationRequested(
        object sender,
        PlaylistOperationNotificationPresentationRequestedEventArgs request)
    {
        PresentPlaylistOperationNotifications(request?.Receipt, request?.RouteName);
    }

    private void MainWindow_PlaylistWorkspaceExternalPlaylistImportSummaryRefreshFailed(
        object sender,
        ExternalPlaylistImportSummaryRefreshFailedEventArgs request)
    {
        if (request?.Exception == null)
        {
            return;
        }
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + request.Exception.Message,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxImage.Exclamation,
            "external playlist import summary refresh failure notification");
    }

    private void MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportConfirmationRequested(
        object sender,
        BeatorajaTableUrlImportConfirmationRequestedEventArgs request)
    {
        if (request == null)
        {
            return;
        }
        request.Confirmed = ShowUiConfirmation(
            BeMusicSeeker.Properties.Resources.Confirm_import_beatoraja_table_urls,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxImage.Question,
            MessageBoxButton.OKCancel,
            "beatoraja Table URL import confirmation");
    }

    private void MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportNotificationRequested(
        object sender,
        BeatorajaTableUrlImportNotificationRequestedEventArgs request)
    {
        if (request == null)
        {
            return;
        }
        MessageBoxImage icon = request.Kind switch
        {
            BeatorajaTableUrlImportNotificationKind.Information => MessageBoxImage.Information,
            BeatorajaTableUrlImportNotificationKind.Warning => MessageBoxImage.Exclamation,
            BeatorajaTableUrlImportNotificationKind.Error => MessageBoxImage.Hand,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, null)
        };
        ShowUiMessage(request.Message, request.Caption, icon, request.RouteName);
    }

    private void MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportSummaryReady(
        object sender,
        BeatorajaTableUrlImportSummaryReadyEventArgs request)
    {
        ShowBeatorajaTableUrlImportSummary(request?.Summary);
    }

    private static bool ShowUiConfirmation(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        MessageBoxButton button,
        string routeName)
    {
        UiDialogResult result = new UiDialogCoordinator()
            .ConfirmAsync(new UiConfirmationRequest(messageBoxText, caption, button, icon))
            .GetAwaiter()
            .GetResult();
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " failed: no result");
        }
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes,
            _ => throw new InvalidOperationException(routeName + " failed: " + result.Status, result.Exception)
        };
    }

    private static void ShowUiMessage(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        string routeName)
    {
        UiDialogResult result = new UiDialogCoordinator()
            .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, MessageBoxButton.OK, icon, MessageBoxResult.OK))
            .GetAwaiter()
            .GetResult();
        ThrowIfUiDialogNotShown(result, routeName);
    }

    private void ShowBeatorajaTableUrlImportSummary(BeatorajaTableUrlImportSummary summary)
    {
        if (summary == null)
        {
            return;
        }
        var message = new StringBuilder();
        message.AppendFormat(
            BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_summary_format,
            summary.ExistingCount,
            summary.ImportedCount,
            summary.RestoredFromBmtCount,
            summary.FailedCount,
            summary.WarningCount);
        AppendBeatorajaTableUrlImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_warning_header, summary.WarningOutcomes);
        AppendBeatorajaTableUrlImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_failed_header, summary.FailedOutcomes);
        ShowUiMessage(
            message.ToString(),
            BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_title,
            summary.FailedCount > 0 || summary.WarningCount > 0 ? MessageBoxImage.Exclamation : MessageBoxImage.Information,
            "beatoraja Table URL import summary");
    }

    private static void AppendBeatorajaTableUrlImportOutcomeSamples(StringBuilder message, string header, IReadOnlyList<BeatorajaTableUrlImportOutcome> outcomes)
    {
        const int maxSamples = 5;
        if (message == null || outcomes == null || outcomes.Count == 0)
        {
            return;
        }
        message.AppendLine();
        message.AppendLine();
        message.AppendLine(header);
        foreach (BeatorajaTableUrlImportOutcome outcome in outcomes.Take(maxSamples))
        {
            string nameOrUri = !string.IsNullOrWhiteSpace(outcome.TableName) ? outcome.TableName : (outcome.Uri?.ToString() ?? outcome.RawUrl ?? string.Empty);
            message.AppendLine(outcome.Exception != null && !string.IsNullOrWhiteSpace(outcome.Exception.Message)
                ? "- " + nameOrUri + " (" + outcome.Exception.Message + ")"
                : "- " + nameOrUri);
        }
        if (outcomes.Count > maxSamples)
        {
            message.AppendLine("- ...");
        }
    }

    private void ShowExternalPlaylistImportQueueSummary(ExternalPlaylistImportQueueSummary summary)
    {
        if (summary == null || !summary.HasNotifiableItems)
        {
            return;
        }
        var message = new StringBuilder();
        message.AppendFormat(
            BeMusicSeeker.Properties.Resources.Playlist_import_result_summary_format,
            summary.ImportedCount,
            summary.SkippedDuplicateNameCount,
            summary.FailedCount);
        AppendImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Playlist_import_result_skipped_header, summary.SkippedDuplicateNameOutcomes);
        AppendImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Playlist_import_result_failed_header, summary.FailedOutcomes);
        ShowUiMessage(
            message.ToString(),
            BeMusicSeeker.Properties.Resources.Playlist_import_result_title,
            summary.FailedCount > 0 ? MessageBoxImage.Exclamation : MessageBoxImage.Information,
            "external playlist import summary");
    }

    private static void AppendImportOutcomeSamples(StringBuilder message, string header, IReadOnlyList<ExternalPlaylistImportOutcome> outcomes)
    {
        const int maxSamples = 5;
        if (message == null || outcomes == null || outcomes.Count == 0)
        {
            return;
        }
        message.AppendLine();
        message.AppendLine();
        message.AppendLine(header);
        foreach (ExternalPlaylistImportOutcome outcome in outcomes.Take(maxSamples))
        {
            string nameOrUri = !string.IsNullOrWhiteSpace(outcome.TableName) ? outcome.TableName : (outcome.Uri?.ToString() ?? string.Empty);
            message.AppendLine(outcome.Kind == ExternalPlaylistImportOutcomeKind.Failed && outcome.Exception != null && !string.IsNullOrWhiteSpace(outcome.Exception.Message)
                ? "- " + nameOrUri + " (" + outcome.Exception.Message + ")"
                : "- " + nameOrUri);
        }
        if (outcomes.Count > maxSamples)
        {
            message.AppendLine("- ...");
        }
    }

    private static void PresentPlaylistOperationNotifications(
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt,
        string routeName)
    {
        if (receipt == null)
        {
            return;
        }
        foreach (PlaylistOperationNotificationOwner.OperationNotification notification in receipt.Notifications)
        {
            MessageBoxImage icon = notification.Severity switch
            {
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information => MessageBoxImage.Asterisk,
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning => MessageBoxImage.Exclamation,
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error => MessageBoxImage.Hand,
                _ => MessageBoxImage.None,
            };
            ShowUiMessage(notification.Message, notification.Caption, icon, routeName);
        }
    }

    private void MainWindowViewModel_ElevatedProcessWarningPresentationRequested(ElevatedProcessWarningPresentationRequest request)
    {
        if (request == null)
        {
            return;
        }
        try
        {
            if (!CanPresentElevatedProcessWarning())
            {
                request.Complete(false);
                return;
            }
            UiDialogRoute.ShowMessageBox(
                this,
                BeMusicSeeker.Properties.Resources.Warn_ElevatedProcessDragDropLimited,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
            request.Complete(true);
        }
        catch (Exception exception)
        {
            request.Fail(exception);
        }
    }

    private void MainWindowViewModel_StartupUpdatePresentationRequested(StartupUpdatePresentationRequest request)
    {
        _ = PresentStartupUpdateAsync(request);
    }

    private async Task PresentStartupUpdateAsync(StartupUpdatePresentationRequest request)
    {
        if (request == null)
        {
            return;
        }
        try
        {
            if (IsShellClosingOrClosed())
            {
                request.Complete(null);
                return;
            }
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel
                ?? throw new InvalidOperationException("MainWindowViewModel is required to present the update dialog.");
            UiWindowDialogResult<UpdateAssetInfo> dialogResult = await new UiDialogCoordinator()
                .ShowWindowAsync(new UiWindowDialogRequest<UpdateAvailableDialog, UpdateAssetInfo>(
                    () => new UpdateAvailableDialog(request.Result, viewModel.ProgressHub, viewModel.ExternalShellGateway),
                    dialog => dialog.SelectedAsset,
                    this));
            ThrowIfWindowDialogFailed(dialogResult.Status, dialogResult.Error, "Update available dialog");
            request.Complete(dialogResult.IsAccepted ? dialogResult.Value : null);
        }
        catch (Exception exception)
        {
            request.Fail(exception);
        }
    }

    private void MainWindowViewModel_StartupUpdateFailurePresentationRequested(Exception exception)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        bool updateShutdownPreparationFailed = viewModel?.ShellShutdownWorkflow.ConsumeUpdatePreparationFailure() == true;
        if (ShouldDeferStartupUpdateFailurePresentation(
            IsShellClosingOrClosed(),
            updateShutdownPreparationFailed,
            exception))
        {
            if (exception is UpdateFailureReceiptException receipt)
            {
                receipt.DeferAcknowledge();
            }
            return;
        }
        UiDialogRoute.ShowMessageBox(
            "Failed to download or start the update.\n" + (exception?.Message ?? string.Empty),
            "Update Failed",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    internal static bool ShouldDeferStartupUpdateFailurePresentation(
        bool shellClosing,
        bool updateShutdownPreparationFailed,
        Exception exception)
    {
        return shellClosing
            && !updateShutdownPreparationFailed
            && exception is not UpdaterLaunchFailureException;
    }

    private void MainWindowViewModel_StartupUpdateApplicationShutdownRequested()
    {
        if (base.Dispatcher.HasShutdownStarted || base.Dispatcher.HasShutdownFinished)
        {
            return;
        }
        _ = base.Dispatcher.InvokeAsync((Action)ApplyTerminalShutdown).Task;
    }

    private void MainWindowViewModel_OperationModeRestartRequested()
    {
        if (base.Dispatcher.HasShutdownStarted || base.Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (base.Dispatcher.CheckAccess())
        {
            Close();
            return;
        }
        _ = base.Dispatcher.InvokeAsync((Action)Close).Task;
    }

    void ISettingDialogPresentationPort.OpenSettingsDialog(bool deferPresentation)
    {
        // 失敗後の再表示では、前の保存処理を次のモーダル画面の終了待ちにしない。
        RunOnUiThread(() =>
        {
            if (!IsShellClosingOrClosed())
            {
                ShowSettingsWindow();
            }
        }, deferExecution: deferPresentation);
    }

    void ISettingDialogPresentationPort.OpenInitialSetupLanguageDialog()
    {
        RunOnUiThreadSynchronously(() => ShowOverlayDialog(initialSetupLanguageDialog));
    }

    void ISettingDialogPresentationPort.CloseSettingsDialog()
    {
        RunOnUiThread(() => settingsWindow?.CloseFromPresentation());
    }

    Task ISettingDialogPresentationPort.CloseSettingsDialogAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(CloseSettingsWindowAsync).Task.Unwrap();
        }
        return CloseSettingsWindowAsync();
    }

    private Task CloseSettingsWindowAsync()
    {
        if (settingsWindow == null)
        {
            throw new InvalidOperationException("No settings presentation is active.");
        }
        TaskCompletionSource<object> closed = settingsPresentationClosed ??=
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        settingsWindow.CloseFromPresentation();
        return closed.Task;
    }

    void ISettingDialogPresentationPort.RefreshAppearanceSelection()
    {
        RunOnUiThread(() =>
        {
            if (base.DataContext is MainWindowViewModel viewModel && settingsWindow != null)
            {
                settingsWindow.RefreshAppearanceThemeSelection(viewModel.SettingDialog);
            }
        });
    }

    private void ShowSettingsWindow()
    {
        if (settingsWindow != null)
        {
            settingsWindow.Activate();
            return;
        }
        if (base.DataContext is not MainWindowViewModel)
        {
            throw new InvalidOperationException("Main window view model is unavailable.");
        }
        if (ReferenceEquals(activeOverlayDialog, initialSetupLanguageDialog))
        {
            HideOverlayDialog(initialSetupLanguageDialog);
        }
        if (activeOverlayDialog?.Visibility == Visibility.Visible)
        {
            throw new InvalidOperationException("Another overlay dialog is already visible: " + activeOverlayDialog.GetType().Name);
        }

        Visibility previousPlaybackOverlayVisibility = PlaybackOverlayVisibility;
        Exception presentationFailure = null;
        PlaybackOverlayVisibility = Visibility.Visible;
        try
        {
            UiWindowDialogResult<SettingsWindowCloseReason> result = new UiDialogCoordinator()
                .ShowWindowAsync(new UiWindowDialogRequest<SettingsWindow, SettingsWindowCloseReason>(
                    () => settingsWindow = CreateSettingsWindowForPresentation(),
                    window => window.CloseReason,
                    this))
                .GetAwaiter()
                .GetResult();
            ThrowIfWindowDialogFailed(result.Status, result.Error, "Settings window");
        }
        catch (Exception exception)
        {
            presentationFailure = exception;
            throw;
        }
        finally
        {
            TaskCompletionSource<object> closed = settingsPresentationClosed;
            settingsWindow = null;
            settingsPresentationClosed = null;
            PlaybackOverlayVisibility = previousPlaybackOverlayVisibility;
            // Closed だけでは ShowDialog のモーダル範囲が残るため、シェルの後片付け後に受け渡す。
            if (presentationFailure == null)
            {
                closed?.TrySetResult(null);
            }
            else
            {
                closed?.TrySetException(presentationFailure);
            }
        }
    }

    /// <summary>
    /// Constructs one settings presentation bound to the shell-owned child composition.
    /// </summary>
    /// <returns>An unshown settings window ready for the window-dialog coordinator.</returns>
    internal SettingsWindow CreateSettingsWindowForPresentation()
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            throw new InvalidOperationException("Main window view model is unavailable.");
        }

        SettingsWindow createdWindow = new(CompleteSettingsWindowApplicationExit)
        {
            DataContext = viewModel.SettingDialog,
            PlaybackPanel = viewModel.PlaybackPanel,
            PlaylistWorkspace = viewModel.PlaylistWorkspace
        };
        settingsWindowCreated?.Invoke(createdWindow);
        return createdWindow;
    }

    private void CompleteSettingsWindowApplicationExit(SettingsWindow source)
    {
        if (!Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "The settings window application-exit terminal must run on the MainWindow dispatcher.");
        }

        source.CloseForOwnerShutdown();
        Close();
    }

    private void RunOnUiThread(Action action, bool deferExecution = false)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        if (!deferExecution && Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        try
        {
            // 再表示は旧画面の await 継続より後へ回し、画面側の後処理も先に完了させる。
            Dispatcher.BeginInvoke(
                deferExecution ? DispatcherPriority.Background : DispatcherPriority.Normal,
                action);
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
        }
    }

    private void RunOnUiThreadSynchronously(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        try
        {
            Dispatcher.Invoke(DispatcherPriority.Normal, action);
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
        }
    }

    private void MainWindow_PlaylistUrlInstallTreeExpansionRequested()
    {
        playlistWorkspaceTerminals.UrlInstallTreeExpansion.ExpandInstallTree();
    }

    private void playbackPanelViewPlaybackStarting(object sender, RoutedEventArgs e) => scrollIntoView();

    private void playbackPanelViewPlaybackStarted(object sender, RoutedEventArgs e) => RestorePlaybackSurfaceAndFocusTable();

    private void ApplySavedTreeViewWidth()
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            gridColumn0.Width = new GridLength(viewModel.ViewSettings.TreeViewWidth);
        }
    }

    /// <summary>
    /// アプリケーション起動時に、設定 (StartupSelectInstallPending) に基づいて
    /// プレイリストツリーの「インストール待ち（保留）」ノードを自動的に展開・選択します。
    /// </summary>
    private void ApplyStartupInitialSelectionRequest()
    {
        if (startupInitialSelectionApplied)
        {
            return;
        }
        startupInitialSelectionApplied = true;
        if (DataContext is not MainWindowViewModel viewModel
            || !viewModel.ViewSettings.StartupSelectInstallPending)
        {
            return;
        }
        if (treeViewItemInstallPending != null && treeViewItemInstallPending.IsSelected)
        {
            return;
        }
        if (viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked)
        {
            QueueStartupInitialSelectionUntilOperable(viewModel.ProgressHub.StartupProgress);
            return;
        }
        ApplyStartupInitialSelectionNow();
    }

    private void QueueStartupInitialSelectionUntilOperable(StartupProgressWorkflowOwner startupProgress)
    {
        if (_startupInitialSelectionReadyHandler != null)
        {
            return;
        }
        _startupInitialSelectionReadyOwner = startupProgress;
        _startupInitialSelectionReadyHandler = delegate (object _, PropertyChangedEventArgs args)
        {
            if (args == null
                || args.PropertyName != nameof(StartupProgressWorkflowOwner.IsStartupUiInteractionBlocked)
                || startupProgress.IsStartupUiInteractionBlocked)
            {
                return;
            }
            startupProgress.PropertyChanged -= _startupInitialSelectionReadyHandler;
            _startupInitialSelectionReadyHandler = null;
            _startupInitialSelectionReadyOwner = null;
            ApplyStartupInitialSelectionNow();
        };
        startupProgress.PropertyChanged += _startupInitialSelectionReadyHandler;
    }

    private void ApplyStartupInitialSelectionNow()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)delegate
        {
            if (IsShellClosingOrClosed())
            {
                return;
            }
            if (treeViewItemInstall != null)
            {
                treeViewItemInstall.IsExpanded = true;
            }
            if (treeViewItemInstallPending != null)
            {
                treeViewItemInstallPending.IsExpanded = true;
                treeViewItemInstallPending.IsSelected = true;
            }
        });
    }

    /// <summary>
    /// 指定 Visual 配下に存在する特定型の子要素数を数えます。
    /// </summary>
    /// <typeparam name="T">数えたい Visual 型。</typeparam>
    /// <param name="root">探索開始要素。</param>
    /// <param name="maxCount">上限件数。</param>
    /// <returns>見つかった要素数。</returns>
    private static int CountVisualDescendants<T>(DependencyObject root, int maxCount) where T : DependencyObject
    {
        if (root == null || maxCount <= 0)
        {
            return 0;
        }
        int count = 0;
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(root);
        while (pending.Count > 0 && count < maxCount)
        {
            DependencyObject current = pending.Dequeue();
            int childCount = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < childCount && count < maxCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(current, i);
                if (child is T)
                {
                    count++;
                }
                if (child != null)
                {
                    pending.Enqueue(child);
                }
            }
        }
        return count;
    }

    private void CloseWindow(object sender, ExecutedRoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeWindow(object sender, ExecutedRoutedEventArgs e)
    {
        base.WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow(object sender, ExecutedRoutedEventArgs e)
    {
        base.WindowState = ((base.WindowState != WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal);
    }

    /// <summary>
    /// メインウィンドウに対してファイルやフォルダーがドラッグ＆ドロップされた際の完了処理イベント。
    /// ドロップされたパス一覧を取得し、譜面ファイルのインストール処理を開始します。
    /// </summary>
    private void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var viewModel = base.DataContext as MainWindowViewModel;
        DroppedInstallDropDecision decision = DroppedInstallDropTerminal.Evaluate(
            e.Data,
            IsPlaylistUrlDownloadRunning,
            viewModel == null
                ? null
                : paths => viewModel.PackageInstallWorkflow.AcquireAndTryEnqueueDroppedPaths(paths));
        e.Effects = decision.Effects;

        if (decision.LogUnsupportedFormats)
        {
            LogUnsupportedDropFormats(e.Data);
        }
        if (decision.Exception != null)
        {
            NLogWrapper.FileLogger?.Warn(
                decision.Exception,
                "drop_ingress failed warning=" + decision.WarningKind);
        }
        else if (decision.WarningKind == DroppedInstallDropWarningKind.QueueUnavailable)
        {
            NLogWrapper.FileLogger?.Warn("drop_ingress rejected because the install queue was unavailable");
        }

        switch (decision.WarningKind)
        {
            case DroppedInstallDropWarningKind.PlaylistDownloadBlocked:
                ShowDropInstallWarning(BeMusicSeeker.Properties.Resources.Warn_DropInstallBlockedByPlaylistUrlDownload);
                break;
            case DroppedInstallDropWarningKind.UnsupportedFormat:
                ShowDropInstallWarning(BeMusicSeeker.Properties.Resources.Warn_DropInstallUnsupportedFormat);
                break;
            case DroppedInstallDropWarningKind.IngressFailed:
                ShowDropInstallWarning(BeMusicSeeker.Properties.Resources.Warn_DropInstallIngressFailed);
                break;
            case DroppedInstallDropWarningKind.QueueUnavailable:
                ShowDropInstallWarning(BeMusicSeeker.Properties.Resources.Warn_PackageInstallUnavailable);
                break;
        }

        if (decision.ExpandPendingTree)
        {
            newlyInstalledTreeViewItem.IsExpanded = true;
        }
    }

    private void ShowDropInstallWarning(string message)
    {
        UiDialogRoute.ShowMessageBox(
            this,
            message,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    private static void LogUnsupportedDropFormats(IDataObject data)
    {
        try
        {
            string nativeFormats = string.Join(",", data?.GetFormats(autoConvert: false) ?? []);
            string convertedFormats = string.Join(",", data?.GetFormats(autoConvert: true) ?? []);
            NLogWrapper.FileLogger?.Info(
                "drop_ingress unsupported formats native=" + nativeFormats + " converted=" + convertedFormats);
        }
        catch (Exception exception)
        {
            NLogWrapper.FileLogger?.Warn(exception, "drop_ingress format diagnostics failed");
        }
    }
    /// <summary>
    /// メインウィンドウ内へファイルをドラッグ中（ホバー中）のイベント。
    /// ドロップされたデータがファイル(FileDrop)形式である場合のみ、カーソルエフェクトを「Copy（追加）」に変更します。
    /// </summary>
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (IsPlaylistUrlDownloadRunning)
        {
            e.Effects = DragDropEffects.None;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }
    /// <summary>
    /// メインウィンドウ上でマウスの左ボタンが押し込まれた際の処理。
    /// 一覧やツリーなどの操作可能要素以外をクリックしたと判定された場合、
    /// ウィンドウ全体をドラッグ移動できるようにします (DragMove)。
    /// </summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!(customTableView?.IsMouseOver ?? false) && !(customTablePlaylistSummary?.IsMouseOver ?? false) && !(treeView?.IsMouseOver ?? false))
        {
            DragMove();
        }
    }
    /// <summary>
    /// ウィンドウの表示状態（最大化、最小化、通常）が変更された際に呼び出され、
    /// ウィンドウ境界のマージンを調整します（最大化時の見切れ防止）。
    /// </summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (((Window)sender).WindowState == WindowState.Maximized)
        {
            windowBorder.Margin = new Thickness(8.0);
        }
        else
        {
            windowBorder.Margin = new Thickness(0.0);
        }
    }

    /// <summary>
    /// ウィンドウの内部リソースとHWNDが初期化された直後に呼び出されます。
    /// パネル状態の整合性確認、および前回終了時のウィンドウ配置（最大化状態や座標）の
    /// 復元を行います。
    /// </summary>
    /// <param name="e">イベントデータ。</param>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        playbackPanelView.EnsureSelectedSurfaceAvailable();
        try
        {
            if (DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            Win32API.WINDOWPLACEMENT lpwndpl = Win32WindowPlacementAdapter.ToNative(viewModel.ViewSettings.WindowPlacement);
            lpwndpl.Length = Marshal.SizeOf(typeof(Win32API.WINDOWPLACEMENT));
            lpwndpl.Flags = 0;
            lpwndpl.ShowCmd = ((lpwndpl.ShowCmd == Win32API.ShowWindowCommands.ShowMinimized) ? Win32API.ShowWindowCommands.Normal : lpwndpl.ShowCmd);
            Win32API.SetWindowPlacement(new WindowInteropHelper(this).Handle, ref lpwndpl);
        }
        catch
        {
        }
    }

    private async Task CompleteCloseAfterShellRequestAsync(Task<ShellShutdownWorkflowCompletionReceipt> closeRequest)
    {
        await closeRequest.ConfigureAwait(true);
        await base.Dispatcher.InvokeAsync((Action)ApplyTerminalShutdown).Task.ConfigureAwait(true);
    }

    private void ApplyTerminalShutdown()
    {
        if (Interlocked.CompareExchange(ref terminalShutdownStarted, 1, 0) != 0)
        {
            return;
        }

        ApplyTerminalShutdownAsync().ObserveFault("MainWindow.ApplyTerminalShutdown");
    }

    private async Task ApplyTerminalShutdownAsync()
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        PlaylistPropertyDialog propertyDialog = activePlaylistPropertyDialog;
        PlaylistSummaryBulkEditDialog bulkEditDialog = activePlaylistSummaryBulkEditDialog;
        Task propertyOperationTask = propertyDialog?.WaitForOperationCompletionAsync() ?? Task.CompletedTask;
        Task bulkApplyTask = bulkEditDialog?.WaitForApplyCompletionAsync() ?? Task.CompletedTask;
        Task propertyCleanupTask = playlistPropertyDialogCleanupTask ?? Task.CompletedTask;
        Task bulkCleanupTask = playlistSummaryBulkEditDialogCleanupTask ?? Task.CompletedTask;

        propertyDialog?.CloseForOwnerShutdown();
        bulkEditDialog?.CloseForOwnerShutdown();
        settingsWindow?.CloseForOwnerShutdown();
        playlistLampViewerWindowManager.CloseAll();

        // The dialog owns its operation and session lifetime.  Keep the owner alive until
        // the operation, forced close, DataContext detach, and workspace cleanup have all
        // reached their terminal signals.
        await Task.WhenAll(propertyOperationTask, bulkApplyTask).ConfigureAwait(true);
        await Task.WhenAll(propertyCleanupTask, bulkCleanupTask).ConfigureAwait(true);

        CaptureWindowStateForClosing();
        if (viewModel?.ShellShutdownWorkflow is { } shellShutdownWorkflow)
        {
            await shellShutdownWorkflow.CompleteTerminalShutdownAsync().ConfigureAwait(true);
            terminalWindowCloseAuthorized = true;
            shellShutdownWorkflow.RequestTerminalApplicationShutdown();
        }
        else
        {
            Close();
        }
    }

    private void MainWindow_Closed(object sender, EventArgs e)
    {
        activePlaylistPropertyDialog?.CloseForOwnerShutdown();
        activePlaylistSummaryBulkEditDialog?.CloseForOwnerShutdown();
        playlistLampViewerWindowManager.Dispose();
        CaptureWindowStateForClosing();
        UnsubscribeViewModelUiInteractions();
    }

    /// <summary>
    /// ウィンドウが閉じられる直前に呼び出されます。
    /// 現在のUI状態（TreeViewの幅、ウィンドウの配置や最大化状態など）を
    /// MainWindow の view settings store に反映します。
    /// </summary>
    /// <param name="e">キャンセル可能なイベントデータ。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        MainWindowViewModel closingViewModel = base.DataContext as MainWindowViewModel;
        if (closingViewModel?.ShellShutdownWorkflow is { } shellShutdownWorkflow
            && !terminalWindowCloseAuthorized)
        {
            e.Cancel = true;
            bool closeRequestStarted = shellShutdownWorkflow.TryBeginWindowCloseRequest(out Task<ShellShutdownWorkflowCompletionReceipt> closeRequest);
            CancelRelatedDocumentRequest();
            CloseContextMenuIfOpen(_lastOpenedContextMenu);
            if (closeRequestStarted)
            {
                _ = CompleteCloseAfterShellRequestAsync(closeRequest);
            }
            return;
        }
        var viewModel = closingViewModel;
        if (_startupInitialSelectionReadyOwner != null && _startupInitialSelectionReadyHandler != null)
        {
            _startupInitialSelectionReadyOwner.PropertyChanged -= _startupInitialSelectionReadyHandler;
            _startupInitialSelectionReadyHandler = null;
            _startupInitialSelectionReadyOwner = null;
        }
        if (viewModel != null)
        {
            viewModel.PlaylistWorkspace.PlaylistSummarySelectionRestoreRequested -= MainWindowViewModel_PlaylistSummarySelectionRestoreRequested;
        }
        CancelRelatedDocumentRequest();
        CloseContextMenuIfOpen(_lastOpenedContextMenu);
        activePlaylistPropertyDialog?.CloseForOwnerShutdown();
        activePlaylistSummaryBulkEditDialog?.CloseForOwnerShutdown();
        settingsWindow?.CloseForOwnerShutdown();
        playlistLampViewerWindowManager.CloseAll();
        base.OnClosing(e);
        CaptureWindowStateForClosing();
    }

    private void CaptureWindowStateForClosing()
    {
        if (windowStateCapturedForClosing)
        {
            return;
        }
        windowStateCapturedForClosing = true;
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            viewModel.ViewSettings.CaptureTreeViewWidth(
                gridColumn0.ActualWidth,
                gridColumn0.Width.IsAbsolute ? gridColumn0.Width.Value : double.NaN);
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn("Failed to capture tree view width: " + ex.Message);
        }
        try
        {
            Win32API.WINDOWPLACEMENT lpwndpl = default;
            Win32API.GetWindowPlacement(new WindowInteropHelper(this).Handle, ref lpwndpl);
            viewModel.ViewSettings.CaptureWindowPlacement(Win32WindowPlacementAdapter.FromNative(lpwndpl));
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn("Failed to capture window placement: " + ex.Message);
        }
    }
    private static void CloseContextMenuIfOpen(ContextMenu contextMenu)
    {
        if (contextMenu != null && contextMenu.IsOpen)
        {
            contextMenu.IsOpen = false;
        }
    }

    private bool ShouldBlockStartupUiInteraction(string action)
    {
        if (IsShellClosingOrClosed())
        {
            LogStartupUiBlocked(action, "closing");
            return true;
        }
        if (base.DataContext is MainWindowViewModel viewModel
            && viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked)
        {
            LogStartupUiBlocked(action, "startup");
            return true;
        }
        return false;
    }

    private bool ShouldBlockChartPackageMutationInteraction(string action)
    {
        if (base.DataContext is MainWindowViewModel viewModel && viewModel.ChartMutationActivity.IsActive)
        {
            installPerformanceLogger?.Info("chart_package_mutation_ui_blocked action=" + action);
            return true;
        }
        return false;
    }

    private static void LogStartupUiBlocked(string action, string reason)
    {
        installPerformanceLogger?.Info("startup_ui_blocked action=" + action + " reason=" + reason);
    }

    private void customTableView_FirstRenderCompleted(object sender, CustomTableFirstRenderCompletedEventArgs e)
    {
        if (!installPerformanceLoggingEnabled)
        {
            return;
        }
        var stateLogStopwatch = Stopwatch.StartNew();
        var viewModel = base.DataContext as MainWindowViewModel;
        long sourceGenerationId = viewModel?.PlaylistWorkspace.DetailSourceGenerationId ?? 0L;
        long viewGenerationId = viewModel?.PlaylistWorkspace.DetailViewGenerationId ?? 0L;
        TableFirstVisibleTiming timing = default;
        bool hasPlaylistTiming = viewModel != null
            && viewModel.PlaylistWorkspace.TryCreateDetailOpenVisibleTiming(
                sourceGenerationId,
                viewGenerationId,
                out timing);
        if (!hasPlaylistTiming)
        {
            timing = new TableFirstVisibleTiming(-1, -1L, -1L, e.FirstRenderMs, -1L, e.RowCount);
        }
        long stateLogMs = stateLogStopwatch.ElapsedMilliseconds;
        var metrics = new TableFirstVisibleMetrics(
            "CustomTableView",
            "custom_onrender",
            sourceGenerationId,
            viewGenerationId,
            e.RowCount,
            e.VisibleRowCount,
            e.VisibleColumnCount,
            e.VisibleCellCount,
            e.FirstRenderMs,
            e.RenderWorkMs,
            e.TextCacheHitRate,
            stateLogMs,
            timing,
            e.IsPreparationRender);
        installPerformanceLogger.Info(TableFirstVisibleLogFormatter.Format(metrics));
        if (Net10PerformanceLog.IsEnabled
            && !hasPlaylistTiming
            && viewModel?.MainChartList.LastCompletion.RequestId > 0L)
        {
            MainChartListCompletion completion = viewModel.MainChartList.LastCompletion;
            if (completion.RequestId == lastNormalLibraryFirstVisibleRequestId)
            {
                return;
            }
            lastNormalLibraryFirstVisibleRequestId = completion.RequestId;
            Net10PerformanceLog.Write(
                PerformanceInteraction.Existing("normal_library", completion.RequestId),
                "first_useful_visible",
                "rows=" + e.RowCount
                + " visibleRows=" + e.VisibleRowCount
                + " firstRenderMs=" + e.FirstRenderMs);
        }
        if (hasPlaylistTiming && !(e.IsPreparationRender && timing.ViewCount > 0))
        {
            viewModel.PlaylistWorkspace.TryLogDetailOpenVisibleCompleted(
                "custom_onrender",
                sourceGenerationId,
                viewGenerationId);
        }
    }

    private void customTablePlaylistSummary_FirstRenderCompleted(
        object sender,
        CustomTableFirstRenderCompletedEventArgs e)
    {
        if (!Net10PerformanceLog.IsEnabled
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!viewModel.PlaylistWorkspace.TryGetAppliedPlaylistSummaryPerformanceInteraction(
                out PerformanceInteraction interaction))
        {
            return;
        }
        if (interaction == lastPlaylistSummaryFirstVisibleInteraction)
        {
            return;
        }
        lastPlaylistSummaryFirstVisibleInteraction = interaction;
        Net10PerformanceLog.Write(
            interaction,
            "first_useful_visible",
            "rows=" + e.RowCount
            + " visibleRows=" + e.VisibleRowCount
            + " firstRenderMs=" + e.FirstRenderMs
            + " preparation=" + e.IsPreparationRender.ToString().ToLowerInvariant());
    }

    private void customTableView_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_sort"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        viewModel.MainChartList.RequestSort(e.SortMemberPath, e.Direction);
    }

    private void customTablePlaylistSummary_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_sort"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        viewModel.PlaylistWorkspace.RequestPlaylistSummarySort(e.SortMemberPath, e.Direction);
    }

    private void customTablePlaylistSummary_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!IsPlaylistSummaryBmtSortDropAllowed(e, out _, out _, out int visibleInsertIndex))
        {
            ClearPlaylistSummaryBmtSortDropPreview();
            if (CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
            return;
        }
        customTablePlaylistSummary?.SetRowDropInsertPreview(visibleInsertIndex);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private async void customTablePlaylistSummary_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        try
        {
            if (!IsPlaylistSummaryBmtSortDropAllowed(e, out List<PlaylistSummaryRow> draggedRows, out PlaylistSummaryRow primaryDraggedRow, out int visibleInsertIndex))
            {
                if (CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows))
                {
                    e.Handled = true;
                }
                return;
            }
            e.Handled = true;
            List<PlaylistSummaryRow> visibleRows = GetVisiblePlaylistSummaryRowsSnapshot();
            int? currentPlaylistId = primaryDraggedRow?.PlaylistId ?? draggedRows.FirstOrDefault(row => row?.PlaylistId != null)?.PlaylistId;
            if (base.DataContext is MainWindowViewModel viewModel)
            {
                try
                {
                    await viewModel.PlaylistWorkspace.DropSummaryRowsInBmtOrderAsync(
                        visibleRows,
                        draggedRows,
                        visibleInsertIndex,
                        currentPlaylistId);
                    e.Effects = DragDropEffects.Move;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    await NotifyMainWindowOperationFailureAsync(exception, "customTablePlaylistSummary_Drop");
                }
            }
        }
        finally
        {
            ClearPlaylistSummaryBmtSortDropPreview();
        }
    }

    private void customTablePlaylistSummary_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows))
        {
            ClearPlaylistSummaryBmtSortDropPreview();
        }
    }

    private void ClearPlaylistSummaryBmtSortDropPreview()
    {
        customTablePlaylistSummary?.ClearRowDropInsertPreview();
    }

    private void MainWindow_PlaylistPropertyValidationError(
        object sender,
        PlaylistPropertyValidationErrorEventArgs request)
    {
        string message = request.Error switch
        {
            PlaylistPropertyValidationError.OutputDirectoryChangedByPlaylistName => BeMusicSeeker.Properties.Resources.Error_OutputFolderNameEmptyOrDuplicateChangePlaylist,
            PlaylistPropertyValidationError.InvalidOutputDirectory => BeMusicSeeker.Properties.Resources.Error_OutputFolderNameEmptyOrDuplicateCheckInput,
            PlaylistPropertyValidationError.InvalidPageUri => BeMusicSeeker.Properties.Resources.Error_InvalidPageUriAbsoluteRequired,
            PlaylistPropertyValidationError.InvalidHeaderUri => BeMusicSeeker.Properties.Resources.Error_InvalidHeaderUri,
            PlaylistPropertyValidationError.InvalidDataUri => BeMusicSeeker.Properties.Resources.Error_InvalidDataUri,
            PlaylistPropertyValidationError.InvalidExternalSyncUris => BeMusicSeeker.Properties.Resources.Error_InvalidPageOrHeaderUri,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Error), request.Error, null)
        };
        UiDialogRoute.ShowMessageBox(
            this,
            message,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    private void MainWindow_PlaylistPropertyExternalSyncConfirmationRequested(
        object sender,
        PlaylistPropertyExternalSyncConfirmationRequestedEventArgs request)
    {
        MessageBoxResult result = UiDialogRoute.ShowMessageBox(
            this,
            request.Enable
                ? BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges
                : BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Exclamation,
            MessageBoxResult.None);
        request.Confirmed = result is MessageBoxResult.OK or MessageBoxResult.Yes;
    }

    private void MainWindow_PlaylistPropertyInvalidOutputDirectoryRequested(object sender, EventArgs e)
    {
        UiDialogRoute.ShowMessageBox(
            this,
            BeMusicSeeker.Properties.Resources.Warn_CustomFolderOutputDirInvalid,
            BeMusicSeeker.Properties.Resources.MessageBoxTitle_Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    private void MainWindow_PlaylistPropertyExternalSyncFailed(
        object sender,
        PlaylistPropertyExternalSyncFailedEventArgs request)
    {
        string message = BeMusicSeeker.Properties.Resources.Msg_failed_load_playlist;
        if (request?.Exception != null && !string.IsNullOrWhiteSpace(request.Exception.Message))
        {
            message += Environment.NewLine + request.Exception.Message;
        }
        UiDialogRoute.ShowMessageBox(
            this,
            message,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    private void MainWindowViewModel_PlaylistSummarySelectionRestoreRequested(PlaylistSummarySelectionRestoreRequest request)
    {
        if (request == null)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                new Action(() => ApplyPlaylistSummarySelectionRestoreToView(request)),
                DispatcherPriority.Background);
            return;
        }
        ApplyPlaylistSummarySelectionRestoreToView(request);
    }

    private void ApplyPlaylistSummarySelectionRestoreToView(PlaylistSummarySelectionRestoreRequest request)
    {
        if (customTablePlaylistSummary == null || request == null)
        {
            return;
        }
        HashSet<int> playlistIdSet = new(request.PlaylistIds);
        int? currentPlaylistId = request.CurrentPlaylistId;
        customTablePlaylistSummary.SelectRowsByPredicate(
            row => row is PlaylistSummaryRow playlistSummaryRow
                && playlistSummaryRow.PlaylistId.HasValue
                && playlistIdSet.Contains(playlistSummaryRow.PlaylistId.Value),
            row => currentPlaylistId.HasValue
                && row is PlaylistSummaryRow playlistSummaryRow
                && playlistSummaryRow.PlaylistId == currentPlaylistId);
    }

    private bool IsPlaylistSummaryBmtSortDropAllowed(DragEventArgs e, out List<PlaylistSummaryRow> draggedRows, out PlaylistSummaryRow primaryDraggedRow, out int visibleInsertIndex)
    {
        draggedRows = [];
        primaryDraggedRow = null;
        visibleInsertIndex = -1;
        if (e?.Data == null
            || !CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows)
            || base.DataContext is not MainWindowViewModel viewModel
            || !CustomTableDataTransfer.TryGetSelectedRows(e.Data, out List<object> selectedRows))
        {
            return false;
        }
        draggedRows = [.. selectedRows.OfType<PlaylistSummaryRow>().Where(row => row?.TableRef != null && row.PlaylistId.HasValue)];
        if (!viewModel.PlaylistWorkspace.CanDropSummaryRowsInBmtOrder(draggedRows))
        {
            return false;
        }
        if (CustomTableDataTransfer.TryGetPrimaryRow(e.Data, out object primaryRow)
            && primaryRow is PlaylistSummaryRow primaryPlaylistSummaryRow
            && draggedRows.Contains(primaryPlaylistSummaryRow))
        {
            primaryDraggedRow = primaryPlaylistSummaryRow;
        }
        primaryDraggedRow ??= draggedRows.FirstOrDefault();
        visibleInsertIndex = ResolvePlaylistSummaryVisibleInsertIndex(e);
        return visibleInsertIndex >= 0;
    }

    private int ResolvePlaylistSummaryVisibleInsertIndex(DragEventArgs e)
    {
        IList rows = customTablePlaylistSummary?.ItemsSource;
        if (rows == null)
        {
            return -1;
        }
        if (rows.Count == 0)
        {
            return 0;
        }
        Point point = e.GetPosition(customTablePlaylistSummary);
        CustomTableHitTestResult hit = customTablePlaylistSummary.HitTestTable(point);
        if (hit.Kind == CustomTableHitKind.Header)
        {
            return 0;
        }
        if (hit.Kind != CustomTableHitKind.Cell)
        {
            if (customTablePlaylistSummary == null || point.X < 0d || point.X >= customTablePlaylistSummary.SurfaceWidth)
            {
                return -1;
            }
            return ResolvePlaylistSummaryVisibleInsertIndexFromPointY(point.Y, rows.Count);
        }
        bool insertAfterTarget = point.Y >= hit.CellRect.Top + (hit.CellRect.Height / 2d);
        return Math.Max(0, Math.Min(rows.Count, hit.RowIndex + (insertAfterTarget ? 1 : 0)));
    }

    private int ResolvePlaylistSummaryVisibleInsertIndexFromPointY(double y, int rowCount)
    {
        if (customTablePlaylistSummary == null || rowCount <= 0)
        {
            return 0;
        }
        if (y < customTablePlaylistSummary.HeaderHeight)
        {
            return 0;
        }
        int firstVisibleRowIndex = customTablePlaylistSummary.FirstVisibleRowIndex;
        int drawableRowCapacity = customTablePlaylistSummary.CalculateDrawableRowCapacity();
        int lastVisibleRowExclusive = Math.Min(rowCount, firstVisibleRowIndex + drawableRowCapacity);
        double rowHeight = Math.Max(1d, customTablePlaylistSummary.RowHeight);
        int rowIndex = firstVisibleRowIndex + (int)Math.Floor((y - customTablePlaylistSummary.HeaderHeight) / rowHeight);
        if (rowIndex < firstVisibleRowIndex)
        {
            return firstVisibleRowIndex;
        }
        if (rowIndex >= lastVisibleRowExclusive)
        {
            return rowCount;
        }
        double rowTop = customTablePlaylistSummary.HeaderHeight + (rowIndex - firstVisibleRowIndex) * rowHeight;
        bool insertAfterTarget = y >= rowTop + rowHeight / 2d;
        return Math.Max(0, Math.Min(rowCount, rowIndex + (insertAfterTarget ? 1 : 0)));
    }

    private List<PlaylistSummaryRow> GetVisiblePlaylistSummaryRowsSnapshot()
    {
        return [.. (customTablePlaylistSummary?.ItemsSource?.OfType<PlaylistSummaryRow>() ?? Enumerable.Empty<PlaylistSummaryRow>())
            .Where(row => row != null)];
    }

    private void customTableView_SelectionChanged(object sender, CustomTableSelectionChangedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            if (viewModel.PlaylistWorkspace.IsPlaylistDetailViewActive)
            {
                NLogWrapper.FileLogger?.Info("custom_table_selection_changed selectedIndex=" + e.SelectedIndex + " selectedCount=" + (e.SelectedRows?.Count ?? 0));
            }
            playbackTerminal.HandleTableSelection(e.SelectedRow);
        }
    }

    private void MainWindow_PlaylistLampNavigationRequested(
        object sender,
        PlaylistLampNavigationRequestedEventArgs request)
    {
        if (request?.Selection?.Table == null)
        {
            return;
        }

        suppressPlaylistLampTreeSelection = true;
        bool navigationApplied = false;
        try
        {
            if (!TrySelectPlaylistTreeItem(
                request.Selection.Table,
                out _,
                out _))
            {
                return;
            }
            TreeViewItem tableTreeViewItem = TryGetPlaylistTreeViewItem(
                request.Selection.Table,
                out _,
                out _);
            if (tableTreeViewItem == null)
            {
                return;
            }
            if (request.Request?.Scope == PlaylistLampViewerNavigationScope.Overall)
            {
                // An overall graph category targets the table root.  The typed scope is
                // authoritative; do not reinterpret it as a folder with a sentinel name.
                tableTreeViewItem.IsExpanded = true;
                tableTreeViewItem.UpdateLayout();
                navigationApplied = true;
                return;
            }
            PlaylistFolderNode folderNode = tableTreeViewItem.Items
                .OfType<PlaylistFolderNode>()
                .FirstOrDefault(candidate => candidate != null
                    && candidate.SpecialKind == PlaylistFolderNodeSpecialKind.None
                    && string.Equals(
                        candidate.FolderName,
                        request.Selection.FolderName,
                        StringComparison.Ordinal));
            if (folderNode == null)
            {
                return;
            }
            tableTreeViewItem.IsExpanded = true;
            tableTreeViewItem.UpdateLayout();
            if (!TrySelectChildTreeViewItemByDataContext(
                tableTreeViewItem,
                folderNode,
                "playlist_lamp_navigation"))
            {
                return;
            }
            navigationApplied = true;
        }
        finally
        {
            suppressPlaylistLampTreeSelection = false;
            if (navigationApplied)
            {
                mainWindowForegroundTerminal.FocusMainWindow();
            }
        }
    }

    private void customTableView_RowActivated(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_row_activate"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (playbackTerminal.HandleTableRowActivation(e.RowIndex, e.Row))
        {
            playbackPanelView.TrySelectBmsPlayerSurface();
        }
    }

    private void customTablePlaylistSummary_RowActivated(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_row_activate"))
        {
            return;
        }
        if (e.Row is PlaylistSummaryRow playlistSummaryRow)
        {
            TrySelectPlaylistTreeItemFromSummary(playlistSummaryRow);
        }
    }

    private void customTableView_RowContextMenuRequested(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_row_context_menu"))
        {
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("custom_table_row_context_menu"))
        {
            return;
        }
        if (e.Row == null || !TryGetTableContextMenuResource(e.Row, "custom_table_context_menu_assign", out ContextMenu contextMenu, out _))
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = new CustomTableContextMenuContext(e.Row, e.RowIndex);
        contextMenu.PlacementTarget = customTableView;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void customTablePlaylistSummary_RowContextMenuRequested(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_context_menu"))
        {
            return;
        }
        if (e.Row is not PlaylistSummaryRow)
        {
            return;
        }
        if (TryFindResource("playlistSummaryContextMenu") is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = new CustomTableContextMenuContext(e.Row, e.RowIndex);
        contextMenu.PlacementTarget = customTablePlaylistSummary;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void customTableView_HeaderContextMenuRequested(object sender, CustomTableHeaderRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_column_header_context_menu"))
        {
            return;
        }
        CustomTableColumnSettings columnSettings = (customTableView.DataContext as MainChartListViewModel)?.ColumnsSettings;
        if (TryFindResource(ResolveMainColumnHeaderContextMenuResourceKey(columnSettings)) is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = null;
        contextMenu.PlacementTarget = customTableView;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    internal static string ResolveMainColumnHeaderContextMenuResourceKey(CustomTableColumnSettings columnSettings)
    {
        return columnSettings?.Kind == CustomTableColumnSettings.ViewKind.PLAY_HISTORY
            ? "playHistoryColumnHeaderContextMenu"
            : "tableColumnHeaderContextMenu";
    }

    private void customTablePlaylistSummary_HeaderContextMenuRequested(object sender, CustomTableHeaderRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_column_header_context_menu"))
        {
            return;
        }
        if (TryFindResource("playlistSummaryColumnHeaderContextMenu") is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = null;
        contextMenu.PlacementTarget = customTablePlaylistSummary;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void customTablePlaylistSummary_CellEditBeginning(object sender, CustomTableCellEditBeginningEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_cell_edit_beginning"))
        {
            e.Cancel = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel
            || e.Row is not PlaylistSummaryRow playlistSummaryRow
            || playlistSummaryRow.TableRef == null
            || !viewModel.PlaylistWorkspace.CanBeginSummaryPropertyEdit(playlistSummaryRow, e.EditPropertyName))
        {
            e.Cancel = true;
        }
    }

    private void customTableView_CellEditBeginning(object sender, CustomTableCellEditBeginningEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_cell_edit_beginning"))
        {
            e.Cancel = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = !mainChartCellEditTerminal.TryBegin(e.Row, e.EditPropertyName);
    }

    private void customTableView_CellEditStarted(object sender, CustomTableCellEditStartedEventArgs e)
    {
        mainChartCellEditTerminal.NotifyStarted(e.Row, e.EditPropertyName);
    }

    private async void customTableView_CellActionRequested(object sender, CustomTableCellActionRequestedEventArgs e)
    {
        bool isUrlDiff = string.Equals(e.Column?.Id, "Url2", StringComparison.Ordinal);
        bool isUrl = isUrlDiff || string.Equals(e.Column?.Id, "Url1", StringComparison.Ordinal);
        if (!isUrl)
        {
            return;
        }
        if (ShouldBlockStartupUiInteraction(isUrlDiff ? "custom_table_open_url_diff" : "custom_table_open_url"))
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            Uri url = isUrlDiff ? GridRowResolver.GetUrlDiff(e.Row) : GridRowResolver.GetUrl(e.Row);
            if (url != null && url.IsAbsoluteUri)
            {
                try
                {
                    await playlistWorkspaceTerminals.UrlAcquisition.RunSinglePlaylistUrlAsync(url);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    await NotifyMainWindowOperationFailureAsync(exception, "customTableView_CellActionRequested");
                }
            }
        }
    }

    private async void customTablePlaylistSummary_CellActionRequested(object sender, CustomTableCellActionRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_cell_action"))
        {
            return;
        }
        if (e.Row is not PlaylistSummaryRow playlistSummaryRow)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            try
            {
                await viewModel.PlaylistWorkspace.HandlePlaylistSummaryCellActionAsync(
                    getSelectedPlaylistSummaryRows(playlistSummaryRow),
                    playlistSummaryRow,
                    e.Column?.Id);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "customTablePlaylistSummary_CellActionRequested");
            }
        }
    }

    private async void customTablePlaylistSummary_CellEditEnded(object sender, CustomTableCellEditEndedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        PlaylistSummaryRow playlistSummaryRow = e.Row as PlaylistSummaryRow;
        PlaylistSummaryPropertyEditCompletion completion;
        try
        {
            completion = await viewModel.PlaylistWorkspace.CompleteSummaryPropertyEditAsync(
                playlistSummaryRow,
                e.EditPropertyName,
                e.Text,
                e.Commit);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "customTablePlaylistSummary_CellEditEnded");
            return;
        }
        if (completion.RefreshRequired)
        {
            customTablePlaylistSummary?.RefreshDisplay();
        }
    }

    private void customTableView_CellEditEnded(object sender, CustomTableCellEditEndedEventArgs e)
    {
        mainChartCellEditTerminal.Complete(e.Row, e.EditPropertyName, e.Text, e.Commit);
    }

    private void RefreshCustomTableViewDisplayAsync()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            RefreshCustomTableViewDisplay();
        }, DispatcherPriority.Background);
    }

    private void RefreshCustomTableViewDisplay()
    {
        if (IsShellClosingOrClosed() || customTableView == null)
        {
            return;
        }
        customTableView.RefreshDisplay();
    }
    private void tableInitializeColumnSetting(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("column_setting_initialize"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel)
        {
            e.Handled = true;
            columnResetTerminal.Reset(Window.GetWindow(this));
        }
    }

    private async void playlistSummaryInitializeColumnSetting(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("playlist_summary_column_setting_initialize"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            e.Handled = true;
            await mainWindowViewModel.PlaylistWorkspace.ResetPlaylistSummaryColumnsToDefaultAsync()
                .LoggingAndPropagate("playlistSummaryInitializeColumnSetting");
        }
    }

    public void scrollIntoView()
    {
        customTableView?.ScrollSelectedRowIntoView();
    }

    private void ClearMainGridSelection()
    {
        customTableView?.ClearSelection();
    }

    private static object _getValueOfPropertyPath(object value, string path)
    {
        if (value == null)
        {
            return null;
        }
        Type type = value.GetType();
        string[] array = path.Split('.');
        foreach (string name in array)
        {
            PropertyInfo property = type.GetProperty(name);
            if (property == null)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Property '{name}' not found on type '{type.Name}' in path '{path}'");
                return null;
            }
            value = property.GetValue(value, null);
            if (value == null)
            {
                return null;
            }
            type = property.PropertyType;
        }
        return value;
    }
    private static Action<T> _getSetterOfPropertyPath<T>(object value, string path)
    {
        if (value == null)
        {
            return _ => { };
        }
        Type type = value.GetType();
        PropertyInfo propertyInfo = null;
        object firstArgument = null;
        string[] array = path.Split('.');
        foreach (string name in array)
        {
            propertyInfo = type.GetProperty(name);
            if (propertyInfo == null)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Property '{name}' not found on type '{type.Name}' in path '{path}'");
                return _ => { };
            }
            firstArgument = value;
            value = propertyInfo.GetValue(value, null);
            if (value == null && name != array.Last())
            {
                return _ => { };
            }
            type = propertyInfo.PropertyType;
        }
        MethodInfo setMethod = propertyInfo?.GetSetMethod();
        if (setMethod == null)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Set method for property '{propertyInfo?.Name}' not found in path '{path}'");
            return _ => { };
        }
        return Delegate.CreateDelegate(typeof(Action<T>), firstArgument, setMethod) as Action<T>;
    }
    /// <summary>
    /// 現在 ViewModel で選択されている（再生中の）BMSファイルの情報を取得し、
    /// BMSPlayerコントロールのプレビュー画像やバナーを最新状態に更新します。
    /// </summary>
    private List<object> GetSelectedGridRowsSnapshot()
    {
        try
        {
            return customTableView?.GetSelectedRowsSnapshot().Where(row => row != null).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private List<ChartOperationTarget> GetSelectedChartTargets(bool isPendingSection = false)
    {
        ChartOperationSourceScope sourceScope = isPendingSection
            ? ChartOperationSourceScope.PendingPackage
            : GetCurrentChartOperationSourceScope();
        return ResolveSelectedChartTargets(sourceScope, ChartOperationCapabilities.None);
    }

    private List<ChartOperationTarget> GetSelectedChartTargets(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        ChartOperationSourceScope sourceScope = isPendingSection
            ? ChartOperationSourceScope.PendingPackage
            : GetCurrentChartOperationSourceScope();
        return ResolveSelectedChartTargets(sourceScope, capability);
    }

    private List<ChartOperationTarget> GetSelectedChartTargets(
        ChartOperationSourceScope sourceScope,
        ChartOperationCapabilities capability = ChartOperationCapabilities.None)
    {
        return ResolveSelectedChartTargets(sourceScope, capability);
    }

    private List<ChartOperationTarget> ResolveSelectedChartTargets(ChartOperationSourceScope sourceScope, ChartOperationCapabilities capability)
    {
        return ChartOperationTargetSelectionResolver.Resolve(new ChartOperationTargetSelectionRequest(
            GetSelectedGridRowsSnapshot(),
            sourceScope,
            capability));
    }

    private ChartOperationSourceScope GetCurrentChartOperationSourceScope()
    {
        return GetCurrentMainChartOperationContext().SourceScope;
    }

    private MainViewOperationSection GetCurrentMainViewOperationSection()
    {
        return GetCurrentMainChartOperationContext().OperationSection;
    }

    private MainChartListOperationContext GetCurrentMainChartOperationContext()
    {
        return (base.DataContext as MainWindowViewModel)?.MainChartList.CurrentOperationContext
            ?? MainChartListOperationContext.Library;
    }

    private static bool IsPendingMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.InstallPending;
    }

    private static bool IsInstalledMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.InstallInstalled;
    }

    private static bool IsPlaylistMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.Playlist;
    }

    private static bool IsFullScanMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.FullScanCheck;
    }

    private static bool IsChartInfoParseErrorMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.ChartInfoParseError;
    }

    private List<ChartFile> GetSelectedBmsFormatCharts(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        return [.. GetSelectedChartTargets(capability, isPendingSection)
            .Select(target => target.Chart)
            .Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private List<string> GetSelectedChartInfoParseFailureMd5s()
    {
        return [.. GetSelectedChartTargets()
            .Select(target => target.Chart?.Md5)
            .Where(md5 => !string.IsNullOrWhiteSpace(md5))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static bool ShouldShowChartInfoParseFailureRemovalMenu(bool isChartInfoParseErrorSection, IEnumerable<string> selectedMd5s)
    {
        return isChartInfoParseErrorSection && (selectedMd5s ?? []).Any(md5 => !string.IsNullOrWhiteSpace(md5));
    }

    internal static bool ShouldShowResourceHealthContextMenu(bool isPlaylistContext, IEnumerable<ChartOperationTarget> selectedTargets)
    {
        return ChartContextMenuStateBuilder.ShouldShowResourceHealthContextMenu(isPlaylistContext, selectedTargets);
    }

    internal static bool ShouldUsePlaylistMissingContextMenu(object row, ChartOperationSourceScope sourceScope)
    {
        return TryResolveTableContextMenuPolicy(row, sourceScope, out bool usePlaylistMissingContextMenu)
            && usePlaylistMissingContextMenu;
    }

    internal static bool TryResolveTableContextMenuPolicy(object row, ChartOperationSourceScope sourceScope, out bool usePlaylistMissingContextMenu)
    {
        usePlaylistMissingContextMenu = false;
        if (row == null || row is PlayHistoryRow)
        {
            return false;
        }
        if (!GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target))
        {
            return false;
        }
        usePlaylistMissingContextMenu = target.IsPlaylistMissing;
        return true;
    }

    private static ContextMenu GetOwningContextMenu(object source)
    {
        object current = source;
        while (current != null)
        {
            if (current is ContextMenu contextMenu)
            {
                return contextMenu;
            }
            if (current is MenuItem menuItem)
            {
                current = menuItem.Parent;
                continue;
            }
            if (current is FrameworkElement frameworkElement)
            {
                current = frameworkElement.Parent;
                continue;
            }
            break;
        }
        return null;
    }

    private bool TryGetContextMenuRow(object source, out ContextMenu contextMenu, out object row)
    {
        contextMenu = GetOwningContextMenu(source);
        row = null;
        if (contextMenu?.PlacementTarget is not FrameworkElement placementTarget)
        {
            return false;
        }
        if (contextMenu.Tag is CustomTableContextMenuContext customTableContext)
        {
            row = customTableContext.Row;
            return row != null;
        }
        row = placementTarget.DataContext;
        return row != null;
    }

    private bool TryGetContextMenuRow(object source, out object row)
    {
        return TryGetContextMenuRow(source, out _, out row);
    }

    private bool TryGetPlayHistoryContextMenuState(object row, out PlayHistoryContextMenuState state)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            return viewModel.PlayHistory.TryCreateContextMenuState(row, out state);
        }
        state = null;
        return false;
    }

    private bool TryCreatePlayHistoryDateSearchSnapshot(out PlayHistoryDateSearchTerm snapshot)
    {
        snapshot = null;
        return base.DataContext is MainWindowViewModel viewModel
            && viewModel.PlayHistory.TryCreateDateSearchSnapshot(
                customTableView?.GetSelectedRowsSnapshot(),
                out snapshot);
    }

    private static void ResetPlayHistoryDateSearchMenuItem(ContextMenu contextMenu)
    {
        if (contextMenu == null)
        {
            return;
        }

        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            if (item.Name == "playHistoryContextMenuItemAddDateRangeToSearch")
            {
                item.Visibility = Visibility.Collapsed;
                item.IsEnabled = false;
                break;
            }
        }
    }

    private sealed class ConfiguredExternalActionMenuTag
    {
        internal ConfiguredExternalActionMenuTag(ConfiguredExternalActionKind kind, string actionId)
        {
            Kind = kind;
            ActionId = actionId ?? string.Empty;
        }

        internal ConfiguredExternalActionKind Kind { get; }

        internal string ActionId { get; }
    }

    private static void RemoveGeneratedConfiguredActionItems(ContextMenu contextMenu)
    {
        for (int index = contextMenu.Items.Count - 1; index >= 0; index--)
        {
            if (contextMenu.Items[index] is MenuItem menuItem
                && (menuItem.Tag is ConfiguredExternalActionMenuTag
                    || menuItem.Name == "tableContextMenuItemOpenProgramActions"
                    || menuItem.Name == "playHistoryContextMenuItemOpenProgramActions"))
            {
                contextMenu.Items.RemoveAt(index);
            }
        }
    }

    private static int FindContextMenuItemIndex(ContextMenu contextMenu, string name)
    {
        for (int index = 0; index < contextMenu.Items.Count; index++)
        {
            if (contextMenu.Items[index] is FrameworkElement item
                && string.Equals(item.Name, name, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static MenuItem CreateConfiguredExternalActionMenuItem(
        ConfiguredExternalActionKind kind,
        string actionId,
        string header,
        string name = null)
    {
        return new MenuItem
        {
            Name = name ?? string.Empty,
            Header = header ?? string.Empty,
            Tag = new ConfiguredExternalActionMenuTag(kind, actionId)
        };
    }

    private void MaterializeConfiguredExternalActions(
        ContextMenu contextMenu,
        RightClickActionResolution resolution,
        MainWindowSelectedChartExternalActionsTerminal terminal,
        bool includePrograms,
        string programAnchorName,
        string webAnchorName,
        string programParentName)
    {
        RemoveGeneratedConfiguredActionItems(contextMenu);
        if (!terminal.HasConfiguredActions)
        {
            return;
        }

        resolution ??= new RightClickActionResolution([], []);
        int webIndex = FindContextMenuItemIndex(contextMenu, webAnchorName);
        if (webIndex < 0)
        {
            webIndex = 0;
        }
        foreach (ResolvedRightClickWebAction action in resolution.WebActions)
        {
            MenuItem menuItem = CreateConfiguredExternalActionMenuItem(
                ConfiguredExternalActionKind.Web,
                action.Id,
                terminal.GetDisplayName(action),
                "configuredWebAction_" + SanitizeMenuName(action.Id));
            menuItem.Click += configuredExternalActionMenuItemClick;
            contextMenu.Items.Insert(webIndex++, menuItem);
        }

        if (!includePrograms || resolution.ProgramActions.Count == 0)
        {
            return;
        }

        int programIndex = FindContextMenuItemIndex(contextMenu, programAnchorName);
        if (programIndex < 0)
        {
            return;
        }
        MenuItem parent = new()
        {
            Name = programParentName,
            Header = BeMusicSeeker.Properties.Resources.RightClick_open_with_program,
            IsEnabled = true,
            Visibility = Visibility.Visible
        };
        foreach (ResolvedRightClickProgramAction action in resolution.ProgramActions)
        {
            MenuItem child = CreateConfiguredExternalActionMenuItem(
                ConfiguredExternalActionKind.Program,
                action.Id,
                action.Name,
                "configuredProgramAction_" + SanitizeMenuName(action.Id));
            child.Click += configuredExternalActionMenuItemClick;
            parent.Items.Add(child);
        }
        contextMenu.Items.Insert(programIndex + 1, parent);
    }

    private static string SanitizeMenuName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }
        StringBuilder builder = new();
        foreach (char character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }
        return builder.ToString();
    }

    private void configuredExternalActionMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ConfiguredExternalActionMenuTag tag }
            || !TryGetContextMenuRow(e.Source, out object row)
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        RightClickActionResolutionInput input = null;
        if (row is PlayHistoryRow)
        {
            viewModel.PlayHistory.TryCreateRightClickActionResolutionInput(
                row,
                LongPathFileSystem.FileExists,
                out input);
        }
        else if (GridRowResolver.TryGetChartOperationTarget(
                     row,
                     GetCurrentChartOperationSourceScope(),
                     out ChartOperationTarget target))
        {
            selectedChartContextMenuTerminals.SelectedChartExternalActions.TryCreateResolutionInput(
                target,
                out input);
        }
        if (input == null)
        {
            e.Handled = true;
            ShowConfiguredExternalActionFailure(
                ExternalConfiguredActionResult.Failure(
                    ExternalConfiguredActionFailureKind.ActionUnavailable,
                    BeMusicSeeker.Properties.Resources.RightClick_external_action_unavailable));
            return;
        }

        ExternalConfiguredActionResult result = selectedChartContextMenuTerminals
            .SelectedChartExternalActions
            .ExecuteConfiguredAction(input, tag.Kind, tag.ActionId);
        // A generated item owns the routed click even when the current row or
        // settings became stale.  The terminal reports the failure below;
        // allowing the event to bubble would risk invoking a legacy handler.
        e.Handled = true;
        if (!result.Succeeded)
        {
            ShowConfiguredExternalActionFailure(result);
        }
    }

    internal static string GetConfiguredExternalActionFailureMessage(ExternalConfiguredActionResult result)
    {
        return result?.FailureKind switch
        {
            ExternalConfiguredActionFailureKind.InvalidSettings => BeMusicSeeker.Properties.Resources.RightClick_external_settings_invalid,
            ExternalConfiguredActionFailureKind.ActionUnavailable => BeMusicSeeker.Properties.Resources.RightClick_external_action_unavailable,
            ExternalConfiguredActionFailureKind.WebLaunchFailed => BeMusicSeeker.Properties.Resources.RightClick_external_web_launch_failed,
            ExternalConfiguredActionFailureKind.ProgramExecutableMissing => BeMusicSeeker.Properties.Resources.RightClick_external_program_executable_missing,
            ExternalConfiguredActionFailureKind.ProgramChartMissing => BeMusicSeeker.Properties.Resources.RightClick_external_program_chart_missing,
            ExternalConfiguredActionFailureKind.ProgramLaunchFailed => BeMusicSeeker.Properties.Resources.RightClick_external_program_launch_failed,
            _ => BeMusicSeeker.Properties.Resources.RightClick_external_action_unavailable
        };
    }

    private void ShowConfiguredExternalActionFailure(ExternalConfiguredActionResult result)
    {
        string detail = string.IsNullOrWhiteSpace(result.Diagnostic)
            ? result.FailureKind.ToString()
            : result.Diagnostic;
        NLogWrapper.FileLogger?.Warn(
            result.Exception,
            "configured_external_action failure kind=" + result.FailureKind + " detail=" + detail);
        if (!IsLoaded)
        {
            return;
        }
        UiDialogRoute.ShowMessageBox(
            this,
            GetConfiguredExternalActionFailureMessage(result),
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    private bool TryGetPlayHistoryContextMenuAction(
        object source,
        PlayHistoryContextMenuActionKind actionKind,
        out PlayHistoryContextMenuAction action)
    {
        if (TryGetContextMenuRow(source, out object row)
            && base.DataContext is MainWindowViewModel viewModel)
        {
            return viewModel.PlayHistory.TryCreateContextMenuAction(row, actionKind, out action);
        }
        action = null;
        return false;
    }

    private bool TryGetContextMenuChartTarget(object primarySource, object fallbackSource, out ChartOperationTarget target)
    {
        if (TryGetContextMenuRow(primarySource, out object row)
            && GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out target))
        {
            return true;
        }
        if (!ReferenceEquals(primarySource, fallbackSource)
            && TryGetContextMenuRow(fallbackSource, out row)
            && GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out target))
        {
            return true;
        }
        target = null;
        return false;
    }

    private bool TryGetTableContextMenuResource(object row, string logPrefix, out ContextMenu contextMenu, out bool usePlaylistMissingContextMenu)
    {
        contextMenu = null;
        usePlaylistMissingContextMenu = false;
        if (row == null)
        {
            return false;
        }
        string resourceKey;
        if (TryGetPlayHistoryContextMenuState(row, out _)
            || (row is PlayHistoryRow && TryCreatePlayHistoryDateSearchSnapshot(out _)))
        {
            resourceKey = "playHistoryContextMenu";
            usePlaylistMissingContextMenu = false;
        }
        else if (row is PlayHistoryRow)
        {
            ResetPlayHistoryDateSearchMenuItem(TryFindResource("playHistoryContextMenu") as ContextMenu);
            return false;
        }
        else if (!TryResolveTableContextMenuPolicy(row, GetCurrentChartOperationSourceScope(), out usePlaylistMissingContextMenu))
        {
            return false;
        }
        else
        {
            resourceKey = usePlaylistMissingContextMenu ? "tableContextMenuPlaylistMissing" : "tableContextMenu";
        }
        if (TryFindResource(resourceKey) is not ContextMenu foundContextMenu)
        {
            return false;
        }
        contextMenu = foundContextMenu;
        NLogWrapper.FileLogger?.Info(logPrefix + " rowType=" + row?.GetType().FullName + " missing=" + usePlaylistMissingContextMenu + " resourceKey=" + resourceKey);
        return true;
    }
    private void keywordSearchWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject clickedElement = e.OriginalSource as DependencyObject;
        KeywordSearchEditor.ClearKeyboardFocusIfOutside(clickedElement);
        PlaylistSummaryKeywordSearchEditor.ClearKeyboardFocusIfOutside(clickedElement);
    }

    private void MainWindow_Deactivated(object sender, EventArgs e)
    {
        KeywordSearchEditor.CloseForWindowDeactivation();
        PlaylistSummaryKeywordSearchEditor.CloseForWindowDeactivation();
    }

    private void keywordSearchClearMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        KeywordSearchEditor.ClearInput();
        e.Handled = true;
    }

    private void keywordSearchPlaylistSummaryClearMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PlaylistSummaryKeywordSearchEditor.ClearInput();
        e.Handled = true;
    }

    private static T FindTemplateElement<T>(FrameworkElement source, string elementName) where T : class
    {
        FrameworkElement current = source;
        while (current != null)
        {
            if (current.FindName(elementName) is T found)
            {
                return found;
            }
            current = current.Parent as FrameworkElement;
        }
        return null;
    }
    private static T FindNamedDescendant<T>(FrameworkElement root, string elementName) where T : class
    {
        if (root == null)
        {
            return null;
        }
        if (root.FindName(elementName) is T foundByName)
        {
            return foundByName;
        }
        int childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childrenCount; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child)
            {
                T found = FindNamedDescendant<T>(child, elementName);
                if (found != null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static T FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null)
        {
            return null;
        }
        int childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childrenCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typedChild)
            {
                return typedChild;
            }
            T descendant = FindVisualDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    private void playlistRootSelect(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ShouldBlockStartupUiInteraction("tree_playlist_root_select"))
        {
            return;
        }
        if (e.Source is TreeViewItem)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            viewModel.PlaylistWorkspace.RequestSummarySelection();
        }
    }

    /// <summary>
    /// メインツリーやプレイリストツリー上で左クリックが行われた際の処理をハンドリングします。
    /// Node（TreeViewItem）の選択状態を判定し、クリックされた要素に応じてViewModel側に
    /// イベント（曲一覧の再生成等）を透過処理します。
    /// </summary>
    private void treeViewLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView treeViewControl)
        {
            return;
        }
        // スクロールバーやExpanderトグルのクリックではフォーカス移譲や更新を行わない
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) != null ||
            FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) != null)
        {
            return;
        }
        treeViewControl.Focus();
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        TreeViewItem treeViewItem = FindAncestor<TreeViewItem>(source);
        if (treeViewItem == null || !treeViewItem.IsSelected)
        {
            return;
        }
        if (treeViewControl == treeViewPlaylist)
        {
            ForceRefreshPlaylistTreeSelection(treeViewItem);
        }
        else if (treeViewControl == treeView)
        {
            ForceRefreshMainTreeSelection(treeViewItem);
        }
    }

    private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T result)
            {
                return result;
            }
            current = GetParentObject(current);
        }
        return null;
    }

    /// <summary>
    /// VisualTree を子方向に探索し、最初に見つかった指定型の要素を返します。
    /// </summary>
    /// <typeparam name="T">検索対象の <see cref="DependencyObject"/> 型。</typeparam>
    /// <param name="parent">探索開始位置。</param>
    /// <returns>最初に見つかった要素。見つからない場合は <see langword="null"/>。</returns>
    private static T FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }
        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(parent);
        }
        catch
        {
            return null;
        }
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, childIndex);
            if (child is T result)
            {
                return result;
            }
            T descendant = FindDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    private static DependencyObject GetParentObject(DependencyObject current)
    {
        if (current == null)
        {
            return null;
        }
        if (current is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }
        if (current is ContentElement)
        {
            return LogicalTreeHelper.GetParent(current);
        }
        try
        {
            return VisualTreeHelper.GetParent(current);
        }
        catch
        {
            return LogicalTreeHelper.GetParent(current);
        }
    }

    private static TreeViewItem GetSelectedTreeViewItem(ItemsControl parent)
    {
        if (parent == null)
        {
            return null;
        }
        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem treeViewItem)
            {
                continue;
            }
            if (treeViewItem.IsSelected)
            {
                return treeViewItem;
            }
            TreeViewItem selectedTreeViewItem = GetSelectedTreeViewItem(treeViewItem);
            if (selectedTreeViewItem != null)
            {
                return selectedTreeViewItem;
            }
        }
        return null;
    }

    /// <summary>
    /// UI仮想化などの影響で対象となる子要素(TreeViewItem)のコンテナが未生成の場合でも、
    /// ツリーの選択状態が無選択（LostFocus）で停止してしまわないように、最も近い兄弟ノードまたは
    /// ルートノードへ選択状態をフォールバック遷移させます。
    /// </summary>
    /// <param name="rootTreeViewItem">フォールバックの起点となる親（ルート）要素。</param>
    /// <param name="currentItem">現在選択されていたが削除等により遷移が必要なデータ項目。</param>
    /// <param name="logScope">ログ出力用のスコープ名。</param>
    private PackageCatalogSelectionPlan CaptureNextSiblingOrRoot(
        TreeViewItem rootTreeViewItem,
        object currentItem,
        string logScope)
    {
        if (rootTreeViewItem == null)
        {
            return PackageCatalogSelectionPlan.None;
        }
        int count = rootTreeViewItem.Items.Count;
        if (count == 0)
        {
            return PackageCatalogSelectionPlan.SelectRoot(rootTreeViewItem);
        }
        int currentIndex = rootTreeViewItem.Items.IndexOf(currentItem);
        if (currentIndex == 0 && count == 1)
        {
            return PackageCatalogSelectionPlan.SelectRoot(rootTreeViewItem);
        }
        if (currentIndex == -1)
        {
            NLogWrapper.FileLogger?.Info(logScope + " selection_fallback reason=current_not_found root=" + rootTreeViewItem.Header);
            return PackageCatalogSelectionPlan.SelectRoot(rootTreeViewItem);
        }
        int targetIndex = ((count - 1 == currentIndex) ? (currentIndex - 1) : (currentIndex + 1));
        return PackageCatalogSelectionPlan.SelectTarget(
            rootTreeViewItem,
            rootTreeViewItem.Items[targetIndex],
            logScope);
    }

    private void SelectNextSiblingOrRoot(TreeViewItem rootTreeViewItem, object currentItem, string logScope)
    {
        CaptureNextSiblingOrRoot(rootTreeViewItem, currentItem, logScope).Apply(this);
    }

    private sealed class PackageCatalogSelectionPlan
    {
        private readonly TreeViewItem rootTreeViewItem;
        private readonly object targetDataContext;
        private readonly string logScope;

        private PackageCatalogSelectionPlan(
            TreeViewItem rootTreeViewItem,
            object targetDataContext,
            string logScope)
        {
            this.rootTreeViewItem = rootTreeViewItem;
            this.targetDataContext = targetDataContext;
            this.logScope = logScope;
        }

        internal static PackageCatalogSelectionPlan None { get; } = new(null, null, string.Empty);

        internal static PackageCatalogSelectionPlan SelectRoot(TreeViewItem rootTreeViewItem)
        {
            return new PackageCatalogSelectionPlan(rootTreeViewItem, null, string.Empty);
        }

        internal static PackageCatalogSelectionPlan SelectTarget(
            TreeViewItem rootTreeViewItem,
            object targetDataContext,
            string logScope)
        {
            return new PackageCatalogSelectionPlan(rootTreeViewItem, targetDataContext, logScope);
        }

        internal void Apply(MainWindow owner)
        {
            if (owner == null || rootTreeViewItem == null)
            {
                return;
            }
            if (targetDataContext == null
                || !owner.TrySelectChildTreeViewItemByDataContext(rootTreeViewItem, targetDataContext, logScope))
            {
                SelectTreeViewItemWithFocus(rootTreeViewItem);
            }
        }
    }

    private bool TrySelectChildTreeViewItemByDataContext(TreeViewItem rootTreeViewItem, object targetDataContext, string logScope)
    {
        if (rootTreeViewItem == null || targetDataContext == null)
        {
            return false;
        }
        TreeViewItem treeViewItem = rootTreeViewItem.ItemContainerGenerator
            .ContainerFromItem(targetDataContext) as TreeViewItem;
        if (treeViewItem == null)
        {
            rootTreeViewItem.UpdateLayout();
            treeViewItem = rootTreeViewItem.ItemContainerGenerator.ContainerFromItem(targetDataContext) as TreeViewItem;
        }
        if (treeViewItem == null)
        {
            int targetIndex = rootTreeViewItem.Items.IndexOf(targetDataContext);
            if (TryRealizeVirtualizedTreeItem(rootTreeViewItem, targetIndex))
            {
                treeViewItem = rootTreeViewItem.ItemContainerGenerator
                    .ContainerFromIndex(targetIndex) as TreeViewItem;
            }
        }
        treeViewItem ??= WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(rootTreeViewItem, targetDataContext);
        if (treeViewItem == null)
        {
            NLogWrapper.FileLogger?.Info(logScope + " selection_fallback reason=container_not_realized root=" + rootTreeViewItem.Header + " target=" + targetDataContext);
            return false;
        }
        SelectTreeViewItemWithFocus(treeViewItem);
        return true;
    }

    private static bool TryRealizeVirtualizedTreeItem(TreeViewItem rootTreeViewItem, int targetIndex)
    {
        if (rootTreeViewItem == null
            || targetIndex < 0
            || playlistTreeBringIndexIntoViewMethod == null)
        {
            return false;
        }
        VirtualizingStackPanel itemsHostPanel = TryGetTreeViewItemItemsHostPanel(rootTreeViewItem);
        if (itemsHostPanel == null)
        {
            return false;
        }
        try
        {
            playlistTreeBringIndexIntoViewMethod.Invoke(itemsHostPanel, [targetIndex]);
            rootTreeViewItem.UpdateLayout();
            return rootTreeViewItem.ItemContainerGenerator.ContainerFromIndex(targetIndex) is TreeViewItem;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SelectTreeViewItemWithFocus(TreeViewItem treeViewItem)
    {
        if (treeViewItem == null)
        {
            return;
        }
        treeViewItem.IsSelected = true;
        treeViewItem.Focus();
    }

    /// <summary>
    /// プレイリストツリー項目の <c>DataContext</c> を、
    /// <see cref="PlaylistFolderNode"/> として安全に扱えるか判定します。
    /// </summary>
    /// <param name="dataContext">判定対象の DataContext。</param>
    /// <param name="folderNode">変換に成功した場合のノード。</param>
    /// <returns><see cref="PlaylistFolderNode"/> として扱える場合は <c>true</c>。</returns>
    private static bool TryGetPlaylistFolderNode(object dataContext, out PlaylistFolderNode folderNode)
    {
        folderNode = dataContext as PlaylistFolderNode;
        return folderNode != null;
    }

    private void ForceRefreshPlaylistTreeSelection(TreeViewItem selectedItem)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || selectedItem == null)
        {
            return;
        }
        if (selectedItem == treeViewItemPlaylist)
        {
            viewModel.PlaylistWorkspace.RequestSummarySelection();
            return;
        }
        BMSTable bmsTable = null;
        PlaylistFolderNode selectedFolderNode = null;
        if (selectedItem.DataContext is BMSTable bMSTable2)
        {
            bmsTable = bMSTable2;
        }
        else
        {
            if (!TryGetPlaylistFolderNode(selectedItem.DataContext, out PlaylistFolderNode folderNode))
            {
                return;
            }
            selectedFolderNode = folderNode;
            TreeViewItem ancestor = FindAncestor<TreeViewItem>(VisualTreeHelper.GetParent(selectedItem));
            while (ancestor != null && ancestor.DataContext is not BMSTable)
            {
                ancestor = FindAncestor<TreeViewItem>(VisualTreeHelper.GetParent(ancestor));
            }
            bmsTable = ancestor?.DataContext as BMSTable;
        }
        if (bmsTable != null)
        {
            viewModel.PlaylistWorkspace.RequestDetailSelection(bmsTable, selectedFolderNode);
        }
    }

    private void ForceRefreshMainTreeSelection(TreeViewItem selectedItem)
    {
        if (selectedItem == null)
        {
            return;
        }

        // 下段ツリーはノード種別ごとの Selected ハンドラを再利用して更新する
        // (ライブラリ/インストール/メンテナンスを一律にカバーする)
        selectedItem.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, selectedItem));
    }

    /// <summary>
    /// プレイリストツリー内の「フォルダ」上でキーボード操作が行われた際のイベントハンドラ。
    /// F2キー押下時に、該当フォルダの名称編集モード (EditableTextBlockの切り替え) を起動します。
    /// </summary>
    private void playlistTableFolderkeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && sender is TreeViewItem treeViewItem && treeViewItem.Template.FindName("PART_Header", treeViewItem) is ContentPresenter templatedParent && treeViewItem.HeaderTemplate.FindName("etbPlaylistTableFolder", templatedParent) is EditableTextBlock editableTextBlock)
        {
            if (TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) && !folderNode.IsEditable)
            {
                return;
            }
            editableTextBlock.IsInEditMode = true;
            e.Handled = true;
        }
    }

    private async void playlistTableFolderClicked(object sender, MouseButtonEventArgs e)
    {
        if (!(e.ChangedButton == MouseButton.Left) || !(sender is EditableTextBlock { TemplatedParent: ContentPresenter contentPresenter } editableTextBlock))
        {
            return;
        }
        if (contentPresenter.TemplatedParent is not TreeViewItem ownerTreeViewItem || !ownerTreeViewItem.IsSelected)
        {
            return;
        }
        if (TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) && !folderNode.IsEditable)
        {
            return;
        }

        object originalEditableDataContext = editableTextBlock.DataContext;
        object originalHeaderDataContext = contentPresenter.DataContext;
        await Task.Delay(1000);

        // NOTE:
        // Recycling有効時は待機中にコンテナ再利用が起きるため、編集開始前に同一対象か再検証する。
        // 再利用済みなら誤った行の編集開始を避けるため何もしない。
        if (!ReferenceEquals(editableTextBlock.DataContext, originalEditableDataContext) || !ReferenceEquals(contentPresenter.DataContext, originalHeaderDataContext) || !ownerTreeViewItem.IsSelected)
        {
            return;
        }
        if (TryGetPlaylistFolderNode(editableTextBlock.DataContext, out folderNode) && !folderNode.IsEditable)
        {
            return;
        }
        editableTextBlock.IsInEditMode = true;
    }

    private async void playlistTableFolderNameChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not EditableTextBlock editableTextBlock || !editableTextBlock.IsTextChanged())
        {
            return;
        }
        if (!(editableTextBlock.TemplatedParent is ContentPresenter { TemplatedParent: TreeViewItem templatedParent }))
        {
            return;
        }
        DependencyObject parent = VisualTreeHelper.GetParent(templatedParent);
        while (parent is not TreeViewItem && parent is not TreeView && parent != null)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (parent == null || parent is TreeView)
        {
            return;
        }
        if ((parent as TreeViewItem).DataContext is not BMSTable bmsTable)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            if (!TryGetPlaylistFolderNode(templatedParent.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsEditable)
            {
                return;
            }
            string nameAfter = editableTextBlock.Text;
            try
            {
                await viewModel.PlaylistWorkspace
                    .RenameFolderAsync(bmsTable, folderNode, nameAfter);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "playlistTableFolderNameChanged");
            }
        }
    }

    private void playlistTableSelected(object sender, RoutedEventArgs e)
    {
        if (suppressPlaylistLampTreeSelection)
        {
            e.Handled = true;
            return;
        }
        var treeViewItem3 = e.OriginalSource as TreeViewItem;
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        if (treeViewItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        e.Handled = true;
        PlaylistFolderNode selectedFolderNode;
        if (e.Source is TreeViewItem treeViewItem2)
        {
            selectedFolderNode = null;
        }
        else
        {
            if (!TryGetPlaylistFolderNode(treeViewItem3.DataContext, out PlaylistFolderNode folderNode))
            {
                return;
            }
            selectedFolderNode = folderNode;
        }
        e.Handled = true;
        viewModel.PlaylistWorkspace.RequestDetailSelection(bmsTable, selectedFolderNode);
    }

    private void treeViewItemOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock reference)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(reference);
            while (parent is not TreeViewItem && parent is not TreeView && parent != null)
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            if (parent != null && parent is TreeViewItem && ((TreeViewItem)parent).Focusable && !((TreeViewItem)parent).IsSelected)
            {
                ((TreeViewItem)parent).IsSelected = true;
            }
        }
    }

    private void mouseRightButtonDown(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem treeViewItem)
        {
            treeViewItem.IsSelected = true;
        }
    }

    private void directoryFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_directory_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel
            && e.Source is TreeViewItem treeViewItem)
        {
            regularLibraryTreeTerminal.NavigateTree(
                RegularChartFolderFilterKind.Directory,
                treeViewItem.Header.ToString());
            e.Handled = true;
        }
    }

    private void playHistoryPeriodSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_play_history_period_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel
            || sender is not TreeViewItem treeRoot
            || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        PlayHistoryPeriodRequest request;
        if (treeViewItem.DataContext is PlayHistoryPeriodTreeItem periodTreeItem)
        {
            request = periodTreeItem.Request;
        }
        else if (ReferenceEquals(treeRoot, treeViewItem))
        {
            request = PlayHistoryPeriodRequest.All();
        }
        else if (treeViewItem.Tag is string tag)
        {
            request = PlayHistoryPeriodRequest.FromTag(tag);
        }
        else
        {
            treeRoot.IsSelected = true;
            treeRoot.IsExpanded = true;
            return;
        }
        if (request == null)
        {
            return;
        }
        viewModel.PlayHistory.ActivatePeriod(
            request,
            viewModel.ChartFilters.KeywordFilter);
        treeRoot.IsExpanded = true;
    }

    private void artistFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_artist_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel
            && e.Source is TreeViewItem treeViewItem)
        {
            regularLibraryTreeTerminal.NavigateTree(
                RegularChartFolderFilterKind.Artist,
                treeViewItem.Header.ToString());
            e.Handled = true;
        }
    }

    private void rootFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_root_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel
            && e.OriginalSource is TreeViewItem)
        {
            regularLibraryTreeTerminal.NavigateTree(filterKind: null, filterKey: null);
        }
    }

    private List<PlaylistSummaryRow> getSelectedPlaylistSummaryRows(PlaylistSummaryRow fallback = null)
    {
        List<PlaylistSummaryRow> list = [];
        if (customTablePlaylistSummary != null && customTablePlaylistSummary.IsVisible)
        {
            list = [.. customTablePlaylistSummary.GetSelectedRowsSnapshot().OfType<PlaylistSummaryRow>().Where(r => r != null)];
        }
        if ((list == null || list.Count == 0) && fallback != null)
        {
            list = [fallback];
        }
        return list ?? [];
    }

    private PlaylistSummaryRow resolvePlaylistSummaryRowFromSender(object sender)
    {
        if (TryGetContextMenuRow(sender, out object row) && row is PlaylistSummaryRow contextRow)
        {
            return contextRow;
        }
        if (sender is FrameworkElement { DataContext: PlaylistSummaryRow playlistSummaryRow })
        {
            return playlistSummaryRow;
        }
        return getSelectedPlaylistSummaryRows().FirstOrDefault();
    }

    /// <summary>
    /// プレイリストサマリー行のダブルクリック時に、対応するプレイリストを左ツリーで選択します。
    /// 既存のツリー選択イベントを再利用し、プレイリスト絞り込み表示への遷移も従来の選択経路に委ねます。
    /// </summary>
    /// <summary>
    /// プレイリストサマリー行に対応するプレイリストをプレイリストツリー上で選択します。
    /// 再読み込み後に <see cref="PlaylistSummaryRow.TableRef"/> が古い参照になっていても、既存の再選択補助ロジックで解決を試みます。
    /// </summary>
    /// <param name="playlistSummaryRow">選択元のプレイリストサマリー行。</param>
    /// <returns>プレイリストツリー項目の選択に成功した場合は <see langword="true"/>。</returns>
    private bool TrySelectPlaylistTreeItemFromSummary(PlaylistSummaryRow playlistSummaryRow)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return false;
        }
        BMSTable selectionTarget =
            viewModel.PlaylistWorkspace.ResolveActivePlaylistSummaryTable(playlistSummaryRow);
        string playlistName = selectionTarget?.name ?? playlistSummaryRow.Name ?? string.Empty;
        string playlistId = playlistSummaryRow.PlaylistId?.ToString() ?? string.Empty;
        if (selectionTarget == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=false reason=target_not_found table=" + playlistName + " playlistId=" + playlistId);
            return false;
        }
        bool selected = TrySelectPlaylistTreeItem(selectionTarget, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable);
        if (!selected)
        {
            NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=false reason=container_not_realized table=" + playlistName + " playlistId=" + playlistId + " realize_by_index_available=" + realizeByIndexAvailable);
            return false;
        }
        NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=true table=" + playlistName + " playlistId=" + playlistId + " usedVirtualizationFallback=" + usedVirtualizationFallback + " expanded=true");
        return true;
    }

    /// <summary>
    /// 指定されたプレイリストをプレイリストツリー上で展開・選択します。
    /// 仮想化で未生成のトップレベル項目については、インデックス指定での実体化を試みます。
    /// </summary>
    /// <param name="targetTable">選択対象のトップレベルプレイリスト。</param>
    /// <param name="usedVirtualizationFallback">仮想化回避の実体化経路を使用した場合は <see langword="true"/>。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>プレイリストツリー上で選択できた場合は <see langword="true"/>。</returns>
    private bool TrySelectPlaylistTreeItem(BMSTable targetTable, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable)
    {
        usedVirtualizationFallback = false;
        realizeByIndexAvailable = true;
        if (targetTable == null)
        {
            return false;
        }
        treeViewItemPlaylist.IsExpanded = true;
        treeViewPlaylist.UpdateLayout();
        treeViewItemPlaylist.UpdateLayout();
        TreeViewItem playlistTreeViewItem = TryGetPlaylistTreeViewItem(targetTable, out usedVirtualizationFallback, out realizeByIndexAvailable);
        if (playlistTreeViewItem == null)
        {
            return false;
        }
        playlistTreeViewItem.IsExpanded = true;
        playlistTreeViewItem.UpdateLayout();
        playlistTreeViewItem.BringIntoView();
        playlistTreeViewItem.IsSelected = true;
        playlistTreeViewItem.Focus();
        return true;
    }

    /// <summary>
    /// 指定されたトップレベルプレイリストに対応する <see cref="TreeViewItem"/> を取得します。
    /// </summary>
    /// <param name="targetTable">プレイリストツリー直下の <see cref="BMSTable"/>。</param>
    /// <param name="usedVirtualizationFallback">仮想化回避の実体化経路を使用した場合は <see langword="true"/>。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>対応する <see cref="TreeViewItem"/>。取得できない場合は <see langword="null"/>。</returns>
    private TreeViewItem TryGetPlaylistTreeViewItem(BMSTable targetTable, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable)
    {
        usedVirtualizationFallback = false;
        realizeByIndexAvailable = true;
        if (targetTable == null)
        {
            return null;
        }
        int playlistIndex = treeViewItemPlaylist.Items.IndexOf(targetTable);
        if (playlistIndex < 0)
        {
            return null;
        }
        return TryGetPlaylistTreeViewItemByIndex(playlistIndex, out usedVirtualizationFallback, out realizeByIndexAvailable);
    }

    /// <summary>
    /// 指定インデックスのトップレベルプレイリスト項目コンテナを取得します。
    /// 画面外で未生成の場合は、仮想化回避の実体化を試みます。
    /// </summary>
    /// <param name="playlistIndex">プレイリストルート直下のインデックス。</param>
    /// <param name="usedVirtualizationFallback">仮想化回避の実体化経路を使用した場合は <see langword="true"/>。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>生成済みの <see cref="TreeViewItem"/>。取得できない場合は <see langword="null"/>。</returns>
    private TreeViewItem TryGetPlaylistTreeViewItemByIndex(int playlistIndex, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable)
    {
        usedVirtualizationFallback = false;
        realizeByIndexAvailable = true;
        if (playlistIndex < 0)
        {
            return null;
        }
        if (treeViewItemPlaylist.ItemContainerGenerator.ContainerFromIndex(playlistIndex) is TreeViewItem playlistTreeViewItem)
        {
            return playlistTreeViewItem;
        }
        usedVirtualizationFallback = TryRealizeVirtualizedPlaylistItem(playlistIndex, out realizeByIndexAvailable);
        if (!usedVirtualizationFallback)
        {
            return null;
        }
        treeViewPlaylist.UpdateLayout();
        treeViewItemPlaylist.UpdateLayout();
        return treeViewItemPlaylist.ItemContainerGenerator.ContainerFromIndex(playlistIndex) as TreeViewItem;
    }

    /// <summary>
    /// 仮想化で未生成のトップレベルプレイリスト項目を、インデックス指定で可視領域へ移動して実体化させます。
    /// </summary>
    /// <param name="playlistIndex">実体化したいプレイリストルート直下のインデックス。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>可視化要求を実行できた場合は <see langword="true"/>。</returns>
    private bool TryRealizeVirtualizedPlaylistItem(int playlistIndex, out bool realizeByIndexAvailable)
    {
        realizeByIndexAvailable = playlistTreeBringIndexIntoViewMethod != null;
        if (playlistIndex < 0)
        {
            return false;
        }
        VirtualizingStackPanel playlistItemsHostPanel = TryGetPlaylistItemsHostPanel();
        if (playlistItemsHostPanel == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_tree_item_realize success=false reason=panel_not_found index=" + playlistIndex);
            return false;
        }
        if (playlistTreeBringIndexIntoViewMethod == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_tree_item_realize success=false reason=bring_index_method_not_found index=" + playlistIndex);
            return false;
        }
        try
        {
            // NOTE:
            // TreeView の仮想化が有効だと、画面外のトップレベル項目は ContainerFromIndex で null のままになります。
            // 公開 API にはインデックス単位で実体化を促す手段がないため、WPF 内部の BringIndexIntoView を局所的に利用します。
            playlistTreeBringIndexIntoViewMethod.Invoke(playlistItemsHostPanel, [playlistIndex]);
            treeViewPlaylist.UpdateLayout();
            treeViewItemPlaylist.UpdateLayout();
            return true;
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Info("playlist_tree_item_realize success=false reason=invoke_failed index=" + playlistIndex + " exception=" + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// プレイリストルート配下の items host となる <see cref="VirtualizingStackPanel"/> を取得します。
    /// </summary>
    /// <returns>プレイリストのトップレベル項目を管理する <see cref="VirtualizingStackPanel"/>。取得できない場合は <see langword="null"/>。</returns>
    private VirtualizingStackPanel TryGetPlaylistItemsHostPanel()
    {
        treeViewItemPlaylist.ApplyTemplate();
        treeViewItemPlaylist.UpdateLayout();
        ItemsPresenter playlistItemsPresenter = FindDescendant<ItemsPresenter>(treeViewItemPlaylist);
        if (playlistItemsPresenter == null)
        {
            return null;
        }
        playlistItemsPresenter.ApplyTemplate();
        return FindDescendant<VirtualizingStackPanel>(playlistItemsPresenter);
    }

    private async void playlistSummaryLinkClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel
            || sender is not Button { DataContext: PlaylistSummaryRow playlistSummaryRow })
        {
            return;
        }
        try
        {
            await viewModel.PlaylistWorkspace.OpenPlaylistSummaryUriAsync(playlistSummaryRow.LinkUri);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "playlistSummaryLinkClick");
        }
    }

    private async void playlistSummaryContextMenuResyncClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow == null)
        {
            return;
        }
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (base.DataContext is MainWindowViewModel viewModel && selectedPlaylistSummaryRows.Count > 0)
        {
            try
            {
                await viewModel.PlaylistWorkspace.ResyncPlaylistsAsync(selectedPlaylistSummaryRows);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "playlistSummaryContextMenuResyncClick");
            }
        }
    }

    private async void playlistSummaryContextMenuOpenPageClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (base.DataContext is MainWindowViewModel viewModel && playlistSummaryRow != null)
        {
            try
            {
                await viewModel.PlaylistWorkspace.OpenPlaylistSummaryUriAsync(playlistSummaryRow.LinkUri);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "playlistSummaryContextMenuOpenPageClick");
            }
        }
    }

    /// <summary>Opens a fresh local lamp viewer for the single summary row.</summary>
    private async void playlistSummaryContextMenuOpenLampViewerClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel
            || resolvePlaylistSummaryRowFromSender(sender) is not PlaylistSummaryRow row)
        {
            return;
        }
        e.Handled = true;
        await playlistLampViewerWindowManager.TryOpenAsync(row)
            .LoggingAndPropagate("playlistSummaryContextMenuOpenLampViewerClick");
    }

    private async void playlistSummaryContextMenuApplyCurrentOrderToBmtSortClick(object sender, RoutedEventArgs e)
    {
        List<PlaylistSummaryRow> visibleRows = GetVisiblePlaylistSummaryRowsSnapshot();
        if (visibleRows.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            await viewModel.PlaylistWorkspace.ApplyCurrentVisibleBmtOrderAsync(visibleRows);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "playlistSummaryContextMenuApplyCurrentOrderToBmtSortClick");
        }
    }

    private async void playlistSummaryContextMenuMoveToBmtSortTopClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            await viewModel.PlaylistWorkspace.MoveSummaryRowsToBmtTopAsync(selectedPlaylistSummaryRows);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "playlistSummaryContextMenuMoveToBmtSortTopClick");
        }
    }

    private async void playlistSummaryContextMenuMoveToBmtSortBottomClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            await viewModel.PlaylistWorkspace.MoveSummaryRowsToBmtBottomAsync(selectedPlaylistSummaryRows);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "playlistSummaryContextMenuMoveToBmtSortBottomClick");
        }
    }

    private async void playlistSummaryContextMenuOpenBulkEditClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = [.. getSelectedPlaylistSummaryRows(playlistSummaryRow).Where(row => row?.TableRef != null)];
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel dialog =
                mainWindowViewModel.PlaylistWorkspace.OpenSummaryBulkEditDialog(selectedPlaylistSummaryRows);
            if (dialog == null)
            {
                return;
            }
            await ShowPlaylistSummaryBulkEditDialogAsync(dialog);
        }
    }

    private async void playlistSummaryContextMenuOpenPropertyClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow?.TableRef == null)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            await OpenPlaylistPropertyDialogAsync(
                mainWindowViewModel,
                playlistSummaryRow.TableRef);
        }
    }

    private async Task OpenPlaylistPropertyDialogAsync(
        MainWindowViewModel viewModel,
        BMSTable table)
    {
        PlaylistPropertyDialogViewModel dialog =
            await viewModel.PlaylistWorkspace.OpenPropertyDialogAsync(table);
        if (dialog == null)
        {
            return;
        }
        await ShowPlaylistPropertyDialogAsync(viewModel, dialog);
    }

    private async Task ShowPlaylistPropertyDialogAsync(
        MainWindowViewModel viewModel,
        PlaylistPropertyDialogViewModel dialog)
    {
        PlaylistPropertyDialog window = null;
        Visibility previousPlaybackOverlayVisibility = PlaybackOverlayVisibility;
        var cleanupCompletion = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        playlistPropertyDialogCleanupTask = cleanupCompletion.Task;
        PlaybackOverlayVisibility = Visibility.Visible;
        try
        {
            UiWindowDialogResult<object> result = await playlistWorkspaceDialogService
                .ShowWindowAsync(new UiWindowDialogRequest<PlaylistPropertyDialog, object>(
                    () =>
                    {
                        window = new PlaylistPropertyDialog(dialog);
                        activePlaylistPropertyDialog = window;
                        return window;
                    },
                    _ => null,
                    this));
            ThrowIfWindowDialogFailed(result.Status, result.Error, "Playlist property window");
        }
        finally
        {
            Exception cleanupFailure = null;
            try
            {
                try
                {
                    if (window != null)
                    {
                        await window.WaitForOperationCompletionAsync();
                    }
                    if (window == null || (!window.HasTerminalOutcome && !window.IsOwnerShutdownClose))
                    {
                        await dialog.ResetPropertiesAsync();
                    }
                }
                finally
                {
                    try
                    {
                        if (window != null)
                        {
                            window.DataContext = null;
                        }
                    }
                    finally
                    {
                        try
                        {
                            dialog.Dispose();
                        }
                        finally
                        {
                            try
                            {
                                viewModel.PlaylistWorkspace.ClosePropertyDialog(dialog);
                            }
                            finally
                            {
                                if (ReferenceEquals(activePlaylistPropertyDialog, window))
                                {
                                    activePlaylistPropertyDialog = null;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    RestorePlaylistDialogUiState(previousPlaybackOverlayVisibility, refreshFolderPath: true);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    throw;
                }
                finally
                {
                    if (cleanupFailure != null)
                    {
                        cleanupCompletion.TrySetException(cleanupFailure);
                    }
                    else
                    {
                        cleanupCompletion.TrySetResult(null);
                    }
                    if (ReferenceEquals(playlistPropertyDialogCleanupTask, cleanupCompletion.Task))
                    {
                        playlistPropertyDialogCleanupTask = Task.CompletedTask;
                    }
                }
            }
        }
    }

    private async Task ShowPlaylistSummaryBulkEditDialogAsync(
        PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel dialog)
    {
        PlaylistSummaryBulkEditDialog window = null;
        Visibility previousPlaybackOverlayVisibility = PlaybackOverlayVisibility;
        var cleanupCompletion = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        playlistSummaryBulkEditDialogCleanupTask = cleanupCompletion.Task;
        PlaybackOverlayVisibility = Visibility.Visible;
        try
        {
            UiWindowDialogResult<object> result = await playlistWorkspaceDialogService
                .ShowWindowAsync(new UiWindowDialogRequest<PlaylistSummaryBulkEditDialog, object>(
                    () =>
                    {
                        window = new PlaylistSummaryBulkEditDialog(dialog);
                        activePlaylistSummaryBulkEditDialog = window;
                        return window;
                    },
                    _ => null,
                    this));
            ThrowIfWindowDialogFailed(result.Status, result.Error, "Playlist summary bulk-edit window");
        }
        finally
        {
            Exception cleanupFailure = null;
            try
            {
                try
                {
                    if (window != null)
                    {
                        await window.WaitForApplyCompletionAsync();
                    }
                }
                finally
                {
                    try
                    {
                        if (window != null)
                        {
                            window.DataContext = null;
                        }
                    }
                    finally
                    {
                        try
                        {
                            dialog.OwnerWorkspace.CloseSummaryBulkEditDialog(dialog);
                        }
                        finally
                        {
                            if (ReferenceEquals(activePlaylistSummaryBulkEditDialog, window))
                            {
                                activePlaylistSummaryBulkEditDialog = null;
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    RestorePlaylistDialogUiState(previousPlaybackOverlayVisibility, refreshFolderPath: false);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    throw;
                }
                finally
                {
                    if (cleanupFailure != null)
                    {
                        cleanupCompletion.TrySetException(cleanupFailure);
                    }
                    else
                    {
                        cleanupCompletion.TrySetResult(null);
                    }
                    if (ReferenceEquals(playlistSummaryBulkEditDialogCleanupTask, cleanupCompletion.Task))
                    {
                        playlistSummaryBulkEditDialogCleanupTask = Task.CompletedTask;
                    }
                }
            }
        }
    }

    private void RestorePlaylistDialogUiState(
        Visibility previousPlaybackOverlayVisibility,
        bool refreshFolderPath)
    {
        try
        {
            playbackPanelView.RestoreSelectedSurface();
        }
        finally
        {
            try
            {
                if (refreshFolderPath)
                {
                    BindingOperations.GetMultiBindingExpression(gridBMSPlayerControlsFolderPath, TextBlock.TextProperty)?.UpdateTarget();
                }
            }
            finally
            {
                PlaybackOverlayVisibility = previousPlaybackOverlayVisibility;
            }
        }
    }

    private async void playlistSummaryContextMenuRemoveClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow == null)
        {
            return;
        }
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            try
            {
                await viewModel.PlaylistWorkspace.PlaylistRemovalWorkflow
                    .RemoveSummaryRowsAsync(selectedPlaylistSummaryRows);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "playlistSummaryContextMenuRemoveClick");
            }
        }
    }

    private async void fullScanCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_full_scan_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            try
            {
                await maintenanceTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.FileMissingFilterSelected);
                treeRoot.IsExpanded = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "fullScanCheckFolderSelect");
            }
        }
    }

    private async void fullScanAllChartsFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_full_scan_all_charts_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeViewItem)
        {
            e.Handled = true;
            try
            {
                await maintenanceTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.FullScanAllChartsFilterSelected);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "fullScanAllChartsFolderSelect");
            }
        }
    }

    private async void fullScanCheckIgnoredFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_full_scan_ignored_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeViewItem)
        {
            e.Handled = true;
            try
            {
                await maintenanceTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.FileMissingIgnoredFilterSelected);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "fullScanCheckIgnoredFolderSelect");
            }
        }
    }

    private async void dupulicateFileCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_duplicate_file_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            try
            {
                await maintenanceTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.DuplicateFilterSelected);
                treeRoot.IsExpanded = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "dupulicateFileCheckFolderSelect");
            }
            return;
        }
        object parameter;
        if (treeViewItem.DataContext is string)
        {
            parameter = treeViewItem.DataContext;
        }
        else if (treeViewItem.DataContext is DuplicateGroup)
        {
            parameter = treeViewItem.DataContext;
        }
        else
        {
            return;
        }
        try
        {
            await maintenanceTreeTerminal
                .NavigateAsync(MainViewUpdateMode.DuplicateFilterSelected, parameter);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "dupulicateFileCheckFolderSelect");
        }
    }

    private async void garbledCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_garbled_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            try
            {
                await maintenanceTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.GarbledFilterSelected);
                treeRoot.IsExpanded = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "garbledCheckFolderSelect");
            }
        }
    }

    private async void garbleFixedFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_garble_fixed_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            try
            {
                await maintenanceTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.GarbleFixedFilterSelected);
                treeRoot.IsExpanded = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "garbleFixedFolderSelect");
            }
        }
    }

    private async void unregisteredToDBFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_unregistered_to_db_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        try
        {
            await maintenanceTreeTerminal
                .NavigateAsync(MainViewUpdateMode.UnregisteredFilterSelected);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "unregisteredToDBFolderSelect");
        }
    }

    private async void zeronoteFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_zero_note_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        try
        {
            await maintenanceTreeTerminal
                .NavigateAsync(MainViewUpdateMode.ZeroNoteFilterSelected);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "zeronoteFolderSelect");
        }
    }

    private async void chartInfoParseErrorFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_chart_info_parse_error_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        try
        {
            await maintenanceTreeTerminal
                .NavigateAsync(MainViewUpdateMode.ChartInfoParseErrorFilterSelected);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "chartInfoParseErrorFolderSelect");
        }
    }

    private async void treeViewZeroNoteContextMenuItemRecheckClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            try
            {
                await zeroNoteRecheckTerminal
                    .RecheckAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "treeViewZeroNoteContextMenuItemRecheckClick");
            }
        }
    }

    private async void newlyInstalledFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_newly_installed_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            try
            {
                await installTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.NewlyInstalledFolderSelected);
                treeRoot.IsExpanded = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "newlyInstalledFolderSelect");
            }
            return;
        }
        if (treeViewItem.DataContext is ChartPackage package)
        {
            try
            {
                await installTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.NewlyInstalledFolderSelected, package);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "newlyInstalledFolderSelect");
            }
        }
    }

    private async void pendingInstallFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_pending_install_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            try
            {
                await installTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.PendingInstallFolderSelected);
                treeRoot.IsExpanded = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "pendingInstallFolderSelect");
            }
            return;
        }
        if (treeViewItem.DataContext is ChartPackage package)
        {
            try
            {
                await installTreeTerminal
                    .NavigateAsync(MainViewUpdateMode.PendingInstallFolderSelected, package);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "pendingInstallFolderSelect");
            }
        }
    }

    /// <summary>
    /// プレイリストツリーの「ルート（プレイリスト一覧）」に対するコンテキストメニューが開かれた際の処理。
    /// 現在の書き込みロック状態やバックグラウンド処理状況（IsWriteLockHeld等）に応じ、
    /// 新規作成やURL読み込みなどの各種メニュー項目の有効/無効 (IsEnabled) を動的に制御します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || base.DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        PlaylistRootContextMenuAvailability availability =
            mainWindowViewModel.PlaylistWorkspace.CapturePlaylistRootContextMenuAvailability();
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            string name = item.Name;
            if (name == "treeViewPlaylistRootContextMenuItemCreateNewPlaylist")
            {
                menuItem = item as MenuItem;
            }
            if (item is not MenuItem)
            {
                continue;
            }
            foreach (Control item2 in (IEnumerable)((MenuItem)item).Items)
            {
                switch (item2.Name)
                {
                    case "treeViewPlaylistRootContextMenuItemLoadPlaylistURL":
                        menuItem2 = item2 as MenuItem;
                        break;
                    case "treeViewPlaylistRootContextMenuItemLoadPlaylistCollection":
                        menuItem3 = item2 as MenuItem;
                        break;
                    case "treeViewPlaylistRootContextMenuItemLoadWalkureTable":
                        menuItem4 = item2 as MenuItem;
                        break;
                }
            }
        }
        if (menuItem != null)
        {
            menuItem.IsEnabled = availability.CanCreatePlaylist;
        }
        if (menuItem2 != null)
        {
            menuItem2.IsEnabled = availability.CanLoadPlaylistUri;
        }
        if (menuItem3 != null)
        {
            menuItem3.IsEnabled = availability.CanLoadPlaylistCollection;
        }
        if (menuItem4 != null)
        {
            menuItem4.IsEnabled = availability.CanLoadBuiltInTables;
        }
    }

    private async void treeViewPlaylistRootContextMenuItemReloadClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel || sender is not MenuItem)
        {
            return;
        }
        e.Handled = true;
        await playlistWorkspaceTerminals.TablesReload
            .ReloadAsync()
            .LoggingAndPropagate("treeViewPlaylistRootContextMenuItemReloadClick");
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「新しいプレイリストを作成」がクリックされた際の処理。
    /// 非同期で空のBMSTable（プレイリスト）を生成し、直後にプロパティ変更ダイアログを表示させます。
    /// </summary>
    private async void treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem)
        {
            return;
        }
        PlaylistPropertyDialogViewModel dialog;
        try
        {
            dialog = await viewModel.PlaylistWorkspace
                .CreatePlaylistPropertyDialogAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick");
            return;
        }
        if (dialog != null)
        {
            try
            {
                await ShowPlaylistPropertyDialogAsync(viewModel, dialog);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick");
            }
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「URLからプレイリストを読み込む」がクリックされた際の処理。
    /// カスタムURL入力用のダイアログ (loadPlaylistURIDialog) を画面に表示します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadPlaylistURLClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel
            && viewModel.PlaylistWorkspace != null
            && viewModel.PlaylistWorkspace.CapturePlaylistRootContextMenuAvailability().CanLoadPlaylistUri)
        {
            ShowOverlayDialog(loadPlaylistURIDialog);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「プレイリストコレクションを読み込む」がクリックされた際の処理。
    /// 指定されたコレクションURLをもとに、ViewModelへプレイリスト群の非同期登録を要求します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadPlaylistCollectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            e.Handled = true;
            playlistWorkspaceTerminals.CollectionImport
                .TryEnqueueExternalPlaylistCollectionImport(menuItem.DataContext as BMSTableSimple);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「Walkure/難易度表を読み込む」に関するメニュー項目（各難易度表単位）のアクション。
    /// MenuItemのTagプロパティに格納されたURLへアクセスし、プレイリスト情報を非同期で追加・登録します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadWalkureTableClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            e.Handled = true;
            playlistWorkspaceTerminals.CollectionImport
                .TryEnqueueBuiltInExternalPlaylistImport((string)menuItem.Tag);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニューから「Walkureのおすすめフォルダ」関連のテーブル読み込みが選択された場合の処理。
    /// LR2IDの設定状況のチェックや、更新モード/閲覧モードに応じたユーザー確認ダイアログを挟んだ後、非同期で登録処理へ進みます。
    /// </summary>
    private async void treeViewPlaylistRootContextMenuItemLoadWalkureTableRecommendedClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel
            || viewModel.PlaylistWorkspace == null
            || sender is not MenuItem menuItem)
        {
            return;
        }
        await viewModel.PlaylistWorkspace
            .EnqueueRecommendedPlaylistImportAsync((string)menuItem.Tag)
            .LoggingAndPropagate("treeViewPlaylistRootContextMenuItemLoadWalkureTableRecommendedClick");
    }

    /// <summary>
    /// プレイリスト（難易度表等の直下にある上位階層）のコンテキストメニューが開かれた際の処理。
    /// 現在の選択要素 (BMSTable) の属性（外部同期するか否か、URLの有無など）や
    /// アプリ状態に応じて、メニュー各項目の有効化状態 (IsEnabled) を切り替えます。
    /// </summary>
    private void treeViewPlaylistTableContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || base.DataContext is not MainWindowViewModel mainWindowViewModel || !(contextMenu.PlacementTarget is TreeViewItem { DataContext: BMSTable dataContext }))
        {
            return;
        }
        PlaylistTableContextMenuAvailability availability =
            mainWindowViewModel.PlaylistWorkspace.CapturePlaylistTableContextMenuAvailability(dataContext);
        MenuItem menuItem = null;
        MenuItem lampViewerMenuItem = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        MenuItem menuItem5 = null;
        MenuItem menuItem6 = null;
        MenuItem menuItem7 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "treeViewPlaylistTableContextMenuItemReload":
                    menuItem7 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemOpenPageURI":
                    menuItem = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemOpenLampViewer":
                    lampViewerMenuItem = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemOverwriteLevel":
                    menuItem3 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemCreateNewFolder":
                    menuItem4 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemRemoveTable":
                    menuItem5 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableCcontextMenuItemOpenPropertyDialog":
                    menuItem6 = item as MenuItem;
                    break;
            }
        }
        menuItem7.IsEnabled = availability.CanReload;
        menuItem.IsEnabled = availability.CanOpenPage;
        if (lampViewerMenuItem != null)
        {
            // The shell test/composition path can expose a transient tree table before the
            // persistence owner is attached. Keep the local command inert for that state;
            // resolving the full typed open context is reserved for the click boundary.
            lampViewerMenuItem.IsEnabled = dataContext.playlist_id.HasValue
                && TryCapturePlaylistLampViewerOpenContext(
                    mainWindowViewModel.PlaylistWorkspace,
                    dataContext) != null;
        }
        menuItem4.IsEnabled = availability.CanCreateFolder;
        menuItem3.IsEnabled = availability.CanOverwriteLevel;
        menuItem5.IsEnabled = availability.CanRemoveTable;
        menuItem6.IsEnabled = availability.CanOpenProperty;
    }

    private static PlaylistLampViewerOpenContext TryCapturePlaylistLampViewerOpenContext(
        PlaylistWorkspaceViewModel workspace,
        BMSTable table)
    {
        if (workspace == null || table == null || !table.playlist_id.HasValue)
        {
            return null;
        }
        try
        {
            return workspace.CapturePlaylistLampViewerOpenContext(table);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static BMSTable ResolvePlaylistTableFromMenuItem(object sender)
    {
        if (sender is not MenuItem menuItem)
        {
            return null;
        }
        if (menuItem.DataContext is BMSTable table)
        {
            return table;
        }
        return (menuItem.Parent as ContextMenu)?.PlacementTarget is TreeViewItem treeViewItem
            ? treeViewItem.DataContext as BMSTable
            : null;
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「最新の情報に更新（リロード）」実行時の処理。
    /// 対象プレイリスト単体の同期更新を非同期で実行し、完了後に選択状態の復元を試みます。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemReloadClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || !(sender is MenuItem { DataContext: BMSTable table }))
        {
            return;
        }
        BMSTable selectionTarget;
        try
        {
            selectionTarget = await viewModel.PlaylistWorkspace
                .ResyncPlaylistTableAsync(table);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "treeViewPlaylistTableContextMenuItemReloadClick");
            return;
        }
        RestorePlaylistTableSelectionAfterReload(selectionTarget);
    }

    private void RestorePlaylistTableSelectionAfterReload(BMSTable selectionTarget)
    {
        if (selectionTarget == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload skipped reason=target_not_found");
            return;
        }
        bool restored = TrySelectPlaylistTreeItem(selectionTarget, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable);
        if (!restored)
        {
            NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload restored=false reason=container_not_realized table=" + selectionTarget.name + " realize_by_index_available=" + realizeByIndexAvailable);
            return;
        }
        NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload restored=true table=" + selectionTarget.name + " expanded=true usedVirtualizationFallback=" + usedVirtualizationFallback);
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「配布ページを開く」実行時の処理。
    /// BMSTableに設定されたURL (Page_url または Header_url) を標準ブラウザ等で開きます。
    /// 特殊スキーム（Walkure難易度表等）の場合は専用のURLへ変換してブラウザ起動します。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOpenPageURIClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel
            || sender is not MenuItem { DataContext: BMSTable dataContext })
        {
            return;
        }
        mainWindowViewModel.PlaylistWorkspace.OpenPlaylistTablePage(dataContext);
    }

    /// <summary>Opens a fresh local lamp viewer for the playlist-tree table.</summary>
    private async void treeViewPlaylistTableContextMenuItemOpenLampViewerClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel
            || ResolvePlaylistTableFromMenuItem(sender) is not BMSTable table)
        {
            return;
        }
        e.Handled = true;
        await playlistLampViewerWindowManager.TryOpenAsync(table)
            .LoggingAndPropagate("treeViewPlaylistTableContextMenuItemOpenLampViewerClick");
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「フォルダを作成」実行時の処理。
    /// 選択中のプレイリスト配下に新しいサブフォルダ用BMSTable要素を非同期で追加します（自作プレイリスト用）。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemCreateNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is BMSTable bmsTable && !bmsTable.is_external_sync)
        {
            try
            {
                await viewModel.PlaylistWorkspace
                    .CreateFolderAsync(bmsTable);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await NotifyMainWindowOperationFailureAsync(exception, "treeViewPlaylistTableContextMenuItemCreateNewFolderClick");
            }
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「一覧をエキスポート (JSON)」実行時の処理。
    /// 保存用ファイルダイアログを開き、指定されたパスに header.json と data.json 形式で
    /// プレイリスト情報をファイル書き出し（出力）します。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemExportTableClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        try
        {
            await viewModel.PlaylistWorkspace
                .ExportPlaylistTableAsync(bmsTable);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "treeViewPlaylistTableContextMenuItemExportTableClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「ローカルBMSの難易度をこの表で上書き」実行時の処理。
    /// ユーザー確認ダイアログ表示後、このプレイリストに登録されている各楽曲のレベル情報を用いて
    /// メインDB（ローカルの全BMS情報）の同等楽曲のレベル値を書き換えます。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemOverwriteLevelClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        e.Handled = true;
        try
        {
            await playlistWorkspaceTerminals.TableLevelOverwrite
                .OverwriteAsync(bmsTable);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "treeViewPlaylistTableContextMenuItemOverwriteLevelClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「この表を削除」実行時の処理。
    /// ユーザー確認を取った上で選択中のプレイリストをツリーから除外し、非同期でメイン側からも削除・破棄します。
    /// UI仮想化による選択ロストを防ぐためのフォールバック遷移も併せて行います。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemRemoveTableClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        e.Handled = true;
        try
        {
            await playlistWorkspaceTerminals.TableRemoval
                .RemoveTreeTableAsync(
                    bmsTable,
                    () => SelectNextSiblingOrRoot(
                        treeViewItemPlaylist,
                        bmsTable,
                        "treeViewPlaylistTableContextMenuItemRemoveTableClick"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "treeViewPlaylistTableContextMenuItemRemoveTableClick");
        }
        finally
        {
            if (treeViewItemPlaylist.IsSelected && treeViewItemPlaylist.Items.Count == 0)
            {
                viewModel.PlaylistWorkspace.RequestDetailSelection(null);
            }
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「プロパティ」実行時の処理。
    /// 選択中の難易度表（BMSTable）の詳細情報や同期URLなどを確認・編集できる専用ダイアログを開きます。
    /// </summary>
    private async void treeViewPlaylistTableCcontextMenuItemOpenPropertyDialogClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel
            && sender is MenuItem { DataContext: BMSTable table })
        {
            await OpenPlaylistPropertyDialogAsync(mainWindowViewModel, table);
        }
    }

    /// <summary>
    /// プレイリスト配下の「フォルダ（自作/自動生成）」のコンテキストメニューが開かれた際の処理。
    /// ツリーのVisualTreeを遡り、現在選択しているアイテムが外部同期中のものかを判別して、
    /// 「フォルダ名変更」や「削除」といった編集メニューの有効化状態を制御します。
    /// </summary>
    private void treeViewPlaylistTableFolderContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu
            || base.DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock == null)
        {
            return;
        }
        BMSTable bMSTable = _getUpperBMSTableForContextMenuClickEvent(sender);
        if (bMSTable == null)
        {
            return;
        }
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            string name = item.Name;
            if (!(name == "treeViewPlaylistTableFolderContextMenuItemDeleteFolder"))
            {
                if (name == "treeViewPlaylistTableFolderContextMenuItemChangeFolderName")
                {
                    menuItem2 = item as MenuItem;
                }
            }
            else
            {
                menuItem = item as MenuItem;
            }
        }
        if (!TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode))
        {
            return;
        }
        PlaylistFolderContextMenuAvailability availability =
            mainWindowViewModel.PlaylistWorkspace
                .CapturePlaylistFolderContextMenuAvailability(bMSTable, folderNode);
        menuItem.IsEnabled = availability.CanDelete;
        menuItem2.IsEnabled = availability.CanRename;
    }

    /// <summary>
    /// 右クリックやコンテキストメニュー操作の起点となった要素から、
    /// VisualTreeを親方向へ遡及して、所属する上位の <see cref="BMSTable"/>（プレイリスト大枠）を探索して返却します。
    /// </summary>
    /// <param name="sender">ContextMenu もしくは MenuItem。</param>
    /// <returns>該当する上位の <see cref="BMSTable"/> 要素。見つからない場合は null。</returns>
    private BMSTable _getUpperBMSTableForContextMenuClickEvent(object sender)
    {
        ContextMenu contextMenu = ((sender is MenuItem menuItem) ? (menuItem.Parent as ContextMenu) : (sender as ContextMenu));
        if (contextMenu == null)
        {
            return null;
        }
        if (contextMenu.PlacementTarget is not TreeViewItem reference)
        {
            return null;
        }
        DependencyObject parent = VisualTreeHelper.GetParent(reference);
        while ((parent is not TreeViewItem || (parent as TreeViewItem).DataContext is not BMSTable) && parent != null)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (parent == null)
        {
            return null;
        }
        return (parent as TreeViewItem).DataContext as BMSTable;
    }

    /// <summary>
    /// 右クリックやコンテキストメニュー操作の起点となったTreeViewItem内から、
    /// VisualTreeを子方向へ探索し、フォルダ名編集などに用いられる <see cref="EditableTextBlock"/> を取得します。
    /// </summary>
    /// <param name="sender">ContextMenu もしくは MenuItem。</param>
    /// <returns>該当する <see cref="EditableTextBlock"/> コンポーネント。存在しない場合は null。</returns>
    private EditableTextBlock _getETBFromContextMenuClickEvent(object sender)
    {
        ContextMenu contextMenu = ((sender is MenuItem menuItem) ? (menuItem.Parent as ContextMenu) : (sender as ContextMenu));
        if (contextMenu == null)
        {
            return null;
        }
        if (contextMenu.PlacementTarget is not TreeViewItem treeViewItem)
        {
            return null;
        }
        return _findTypeFromVisualChildren<EditableTextBlock>([treeViewItem]);
    }

    private static Type _findTypeFromVisualChildren<Type>(IEnumerable<DependencyObject> _objs) where Type : class
    {
        IEnumerable<DependencyObject> enumerable = _objs.SelectMany(obj => _getVisualChildren(obj));
        if (enumerable.Count() == 0)
        {
            return null;
        }
        DependencyObject dependencyObject = enumerable.FirstOrDefault(c => c is Type);
        if (dependencyObject == null)
        {
            return _findTypeFromVisualChildren<Type>(enumerable);
        }
        return dependencyObject as Type;
    }

    private static IEnumerable<DependencyObject> _getVisualChildren(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            yield return VisualTreeHelper.GetChild(obj, i);
        }
    }

    /// <summary>
    /// プレイリストフォルダ階層コンテキストメニュー「フォルダを削除」実行時の処理。
    /// 指定されたカスタムフォルダ名を持つ仮想要素を、所属するプレイリスト (BMSTable) 内から抹消します。
    /// </summary>
    private async void treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock == null || !TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode))
        {
            return;
        }
        BMSTable bmsTable = _getUpperBMSTableForContextMenuClickEvent(sender);
        if (bmsTable == null)
        {
            return;
        }
        try
        {
            await viewModel.PlaylistWorkspace.PlaylistRemovalWorkflow
                .RemoveFolderAsync(bmsTable, folderNode);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick");
        }
    }

    /// <summary>
    /// プレイリストフォルダ階層コンテキストメニュー「フォルダ名を変更」実行時の処理。
    /// 該当のTreeViewItem内に存在する EditableTextBlock の編集モードをアクティブ (IsInEditMode = true) にします。
    /// </summary>
    private void treeViewPlaylistTableFolderContextMenuItemChangeFolderNameClick(object sender, RoutedEventArgs e)
    {
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock != null && (!TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) || folderNode.IsEditable))
        {
            editableTextBlock.IsInEditMode = true;
        }
    }

    /// <summary>
    /// メインツリー（ライブラリ/フォルダ等）コンテキストメニュー「エクスプローラで開く」実行時の処理。
    /// Explorer.exe を介して、選択中のローカルファイルシステム上の絶対パスを開きます。
    /// </summary>
    private void treeViewLibraryFolderContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel
            || !(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string text = placementTarget.Header.ToString();
        viewModel.LibraryFolderTree.OpenFolderInExplorer(text);
    }

    private async void treeViewLibraryFolderContextMenuItemReloadClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel && sender is MenuItem)
        {
            try
            {
                await libraryReloadMenuTerminal.ReloadFileDiffAsync()
                    .LoggingAndPropagate("treeViewLibraryFolderContextMenuItemReloadClick");
            }
            catch (LibraryDirectoryPreflightException)
            {
                // owner が cleanup 後に localized warning を表示済み。
            }
        }
    }

    private async void treeViewLibraryFolderContextMenuItemReinitializeClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel && sender is MenuItem)
        {
            try
            {
                await libraryReloadMenuTerminal.ReinitializeLibraryAsync()
                    .LoggingAndPropagate("treeViewLibraryFolderContextMenuItemReinitializeClick");
            }
            catch (LibraryDirectoryPreflightException)
            {
                // owner が cleanup 後に localized warning を表示済み。
            }
        }
    }

    /// <summary>
    /// BMS検索フォルダコンテキストメニュー「BMS検索フォルダから除外」実行時の処理。
    /// ユーザー確認ダイアログ表示後、アプリケーション設定のBMSルートフォルダー一覧から該当のパスを除外して保存します。
    /// </summary>
    private async void treeViewLibraryFolderContextMenuItemUnregisterRootFolder(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string path = placementTarget.Header.ToString();
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            await rootFolderUnregisterTerminal.UnregisterAsync(path)
                .LoggingAndPropagate("treeViewLibraryFolderContextMenuItemUnregisterRootFolder");
        }
        catch (LibraryDirectoryPreflightException)
        {
            // MainWindowViewModel が cleanup 後に warning を表示するため、
            // shell terminal では汎用 persistence failure を重ねない。
        }
        catch (Exception exception)
        {
            UiDialogResult notification = await new UiDialogCoordinator().ShowMessageAsync(new UiMessageRequest(
                SettingsFailureMessage.Format(exception), BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand,
                MessageBoxResult.OK, owner: this));
            UiDialogRoute.ThrowIfNotShown(notification, "Settings persistence failure notification");
        }
    }

    private async void treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_library_auto_rename_all"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string path = placementTarget.Header.ToString();
        e.Handled = true;
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await folderAutoRenameTerminal
                .StartAllAsync(path)
                .LoggingAndPropagate("treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick");
        }
    }

    private async void treeViewInstalledContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_installed_clear_all"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await ObservePackageCatalogMutationAsync(
            packageCatalogTerminal.ClearAllAsync(PackageCatalogSection.Installed),
            "treeViewInstalledContextMenuClearAllClick");
    }

    private async void treeViewInstallPendingContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_clear_all"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await ObservePackageCatalogMutationAsync(
            packageCatalogTerminal.ClearAllAsync(PackageCatalogSection.Pending),
            "treeViewInstallPendingContextMenuClearAllClick");
    }

    private static async Task<PackageCatalogMutationResult> ObservePackageCatalogMutationAsync(
        Task<PackageCatalogMutationResult> operation,
        string routeName)
    {
        PackageCatalogMutationResult result;
        try
        {
            result = await operation;
        }
        catch (Exception exception)
        {
            Task.FromException(exception).ObserveFault(routeName);
            throw;
        }
        if (result.Failure == null)
        {
            return result;
        }

        Task.FromException(result.Failure).ObserveFault(routeName);
        return result;
    }

    private static async Task<PendingPackageMutationResult> ObservePendingPackageMutationAsync(
        Task<PendingPackageMutationResult> operation,
        string routeName)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }
        try
        {
            return await operation;
        }
        catch (Exception exception)
        {
            await Task.FromException(exception).LoggingAndPropagate(routeName);
            throw;
        }
    }

    private async Task ApplyPackageCatalogMutationViewAsync(
        MainWindowViewModel viewModel,
        PackageCatalogMutationResult result,
        PackageCatalogSelectionPlan selectionPlan,
        TreeViewItem sectionRoot,
        PackageCatalogSection section,
        MainViewUpdateMode emptySectionMode,
        string routeName,
        Action prepareView = null)
    {
        if (result == null || !result.ShouldApplyView)
        {
            return;
        }
        bool sectionRootWasSelected = sectionRoot?.IsSelected == true;
        prepareView?.Invoke();
        selectionPlan?.Apply(this);
        if (result.EmptySection == section
            && sectionRootWasSelected
            && sectionRoot?.IsSelected == true
            && sectionRoot.Items.Count == 0)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(emptySectionMode)
                .LoggingAndPropagate(routeName);
        }
    }

    private async void treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_delete_installed_only"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await pendingBulkMaintenanceTerminal
            .DeleteInstalledOnlySourcesAsync()
            .LoggingAndPropagate("treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick");
    }

    private async void treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_rename_zero_note"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await pendingBulkMaintenanceTerminal
            .RenameZeroNoteChartsAsync()
            .LoggingAndPropagate("treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick");
    }

    private async void treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_overwrite_installed_only_resources"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await pendingBulkMaintenanceTerminal
            .OverwriteInstalledOnlyResourcesAsync()
            .LoggingAndPropagate("treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick");
    }

    private void treeViewInstallPackageContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: ChartPackage dataContext } } })
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        viewModel.PendingPackages.OpenPackageSourceInExplorer(dataContext);
    }

    /// <summary>
    /// インストール関連ツリーのコンテキストメニュー「このフォルダの履歴を消去」実行時の処理。
    /// 「インストール保留中」などのリストから対象のパッケージ (ChartPackage) を一つ取り除きます。
    /// </summary>
    private async void treeViewInstallPackageContextMenuClearFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_clear_folder"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            PackageCatalogSelectionPlan selectionPlan = CaptureNextSiblingOrRoot(
                treeViewItemInstallPending,
                pkg,
                "treeViewInstallPackageContextMenuClearFolderClick");
            PackageCatalogMutationResult result = await ObservePackageCatalogMutationAsync(
                packageCatalogTerminal.RemovePackageAsync(
                    PackageCatalogSection.Pending,
                    pkg),
                "treeViewInstallPackageContextMenuClearFolderClick");
            await ApplyPackageCatalogMutationViewAsync(
                viewModel,
                result,
                selectionPlan,
                treeViewItemInstallPending,
                PackageCatalogSection.Pending,
                MainViewUpdateMode.PendingInstallFolderSelected,
                "treeViewInstallPackageContextMenuClearFolderClick");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "treeViewInstallPackageContextMenuClearFolderClick");
        }
    }

    private async void treeViewInstalledFolderContextMenuClearFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_installed_clear_folder"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            PackageCatalogSelectionPlan selectionPlan = CaptureNextSiblingOrRoot(
                newlyInstalledTreeViewItem,
                pkg,
                "treeViewInstalledFolderContextMenuClearFolderClick");
            PackageCatalogMutationResult result = await ObservePackageCatalogMutationAsync(
                packageCatalogTerminal.RemovePackageAsync(
                    PackageCatalogSection.Installed,
                    pkg),
                "treeViewInstalledFolderContextMenuClearFolderClick");
            await ApplyPackageCatalogMutationViewAsync(
                viewModel,
                result,
                selectionPlan,
                newlyInstalledTreeViewItem,
                PackageCatalogSection.Installed,
                MainViewUpdateMode.NewlyInstalledFolderSelected,
                "treeViewInstalledFolderContextMenuClearFolderClick");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "treeViewInstalledFolderContextMenuClearFolderClick");
        }
    }

    private async void treeViewInstallPackageContextMenuRemoveInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_remove_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await pendingInstallEstimationTerminal
                .ClearPackagesAsync([pkg])
                .LoggingAndPropagate("treeViewInstallPackageContextMenuRemoveInstallDestinationClick");
        }
    }

    private async void treeViewInstallPackageContextMenuForceInstallClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_force_install"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        PackageCatalogSelectionPlan selectionPlan = CaptureNextSiblingOrRoot(
            treeViewItemInstallPending,
            pkg,
            "treeViewInstallPackageContextMenuForceInstallClick");
        PendingPackageMutationResult result = await ObservePendingPackageMutationAsync(
            pendingInstallationTerminal.ForceInstallPackagesAsync([pkg]),
            "treeViewInstallPackageContextMenuForceInstallClick");
        await pendingPackageMutationViewTerminal.ApplyAsync(
            result,
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            "treeViewInstallPackageContextMenuForceInstallClick",
            () => selectionPlan.Apply(this));
    }

    private async void treeViewInstallPackageContextMenuManualInstallClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_manual_install"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        PackageCatalogSelectionPlan selectionPlan = CaptureNextSiblingOrRoot(
            treeViewItemInstallPending,
            pkg,
            "treeViewInstallPackageContextMenuManualInstallClick");
        PendingPackageMutationResult result = await ObservePendingPackageMutationAsync(
            pendingInstallationTerminal.ManualInstallPackagesAsync([pkg]),
            "treeViewInstallPackageContextMenuManualInstallClick");
        await pendingPackageMutationViewTerminal.ApplyAsync(
            result,
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            "treeViewInstallPackageContextMenuManualInstallClick",
            () => selectionPlan.Apply(this));
    }

    private async void treeViewInstallPackageContextMenuSearchInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_search_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await pendingInstallEstimationTerminal
                .SearchPackagesAsync(PendingInstallDestinationSearchKind.InstallDestination, [pkg])
                .LoggingAndPropagate("treeViewInstallPackageContextMenuSearchInstallationDirectoryClick");
        }
    }

    private async void treeViewInstallPackageContextMenuSearchMergeDestinationClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_search_merge_destination"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await pendingInstallEstimationTerminal
                .SearchPackagesAsync(PendingInstallDestinationSearchKind.MergeDestination, [pkg])
                .LoggingAndPropagate("treeViewInstallPackageContextMenuSearchMergeDestinationClick");
        }
    }

    private void treeViewDuplicateFolderContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel
            || !(sender is ContextMenu { PlacementTarget: TreeViewItem { DataContext: string dataContext } placementTarget } contextMenu))
        {
            return;
        }
        TreeViewItem treeViewItem = WPFUtil.FindVisualParent<TreeViewItem>(placementTarget);
        if (treeViewItem == null)
        {
            return;
        }
        if (treeViewItem.DataContext is not DuplicateGroup duplicateGroup)
        {
            return;
        }
        MenuItem menuItem = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            string name = item.Name;
            if (name == "treeViewDuplicateFolderContextMenuItemMergeInto")
            {
                menuItem = item as MenuItem;
            }
        }
        if (menuItem != null)
        {
            IReadOnlyList<string> destinations = viewModel.DuplicateMaintenanceWorkflow
                .CaptureDuplicateFolderMergeDestinations(duplicateGroup, dataContext);
            menuItem.IsEnabled = destinations.Count > 0;
            if (menuItem.IsEnabled)
            {
                menuItem.ItemsSource = destinations;
            }
        }
    }

    private void treeViewDuplicateFolderContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel
            || !(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: string dataContext } } }))
        {
            return;
        }
        viewModel.DuplicateMaintenanceWorkflow.OpenDuplicateFolderInExplorer(dataContext);
    }

    private async void treeViewDuplicateFolderContextMenuItemMergeIntoTargetClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_duplicate_merge_into"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Tag: TreeViewItem tag } menuItem))
        {
            return;
        }
        string srcPath = tag.DataContext as string;
        if (string.IsNullOrWhiteSpace(srcPath))
        {
            return;
        }
        string dstPath = menuItem.DataContext as string;
        if (string.IsNullOrWhiteSpace(dstPath))
        {
            return;
        }
        // 親DuplicateGroupを取得
        TreeViewItem groupTreeItem = WPFUtil.FindVisualParent<TreeViewItem>(tag);
        var duplicateGroup = groupTreeItem?.DataContext as DuplicateGroup;
        e.Handled = true;
        if (base.DataContext is not MainWindowViewModel viewModel || duplicateGroup == null)
        {
            return;
        }
        DuplicateMaintenanceMutationResult result = await duplicateMaintenanceTerminal
            .MergeFolderAsync(srcPath, dstPath, duplicateGroup);
        await ApplyDuplicateFolderMergeResultAsync(result, srcPath, dstPath, viewModel);
    }

    private async Task ApplyDuplicateFolderMergeResultAsync(
        DuplicateMaintenanceMutationResult result,
        string sourcePath,
        string destinationPath,
        MainWindowViewModel viewModel)
    {
        if (!result.Succeeded)
        {
            if (result.Failure != null)
            {
                NLogWrapper.FileLogger?.Warn(result.Failure, "duplicate_merge_failed");
            }
            if (result.ManualRecoveryRequired)
            {
                NLogWrapper.FileLogger?.Error(
                    "duplicate_merge_manual_recovery recoveryPaths="
                    + string.Join("|", result.RecoveryPaths ?? []));
            }
            return;
        }
        if (result.CompletedWithCleanupFailure)
        {
            NLogWrapper.FileLogger?.Warn(
                "duplicate_merge_completed_with_cleanup_failure recoveryPaths="
                + string.Join("|", result.RecoveryPaths ?? []));
        }
        NLogWrapper.FileLogger?.Info(string.Format(
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_completed,
            Path.GetFileName(sourcePath),
            Path.GetFileName(destinationPath)));
        await ApplyDuplicateMaintenanceSelectionAsync(result.SelectionHeader, viewModel);
    }

    private async Task ApplyDuplicateMaintenanceSelectionAsync(string header, MainWindowViewModel viewModel)
    {
        if (viewModel == null || IsShellClosingOrClosed())
        {
            return;
        }
        int requestVersion = Interlocked.Increment(ref _duplicateMaintenanceSelectionVersion);
        if (string.IsNullOrWhiteSpace(header))
        {
            return;
        }
        TaskCompletionSource<bool> duplicateGroupsChanged = CreateDuplicateGroupsChangedSignal();
        PropertyChangedEventHandler handler = (sender, args) =>
        {
            if (args.PropertyName == nameof(MaintenanceTreeViewModel.DuplicateChartGroups)
                && requestVersion == Volatile.Read(ref _duplicateMaintenanceSelectionVersion))
            {
                duplicateGroupsChanged.TrySetResult(true);
            }
        };
        viewModel.MaintenanceTree.PropertyChanged += handler;
        try
        {
            string lastReason = string.Empty;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (requestVersion != Volatile.Read(ref _duplicateMaintenanceSelectionVersion))
                {
                    return;
                }
                (bool succeeded, string failReason) = await TryApplyDuplicateMaintenanceSelectionAsync(
                    header,
                    viewModel,
                    requestVersion);
                lastReason = failReason;
                if (succeeded)
                {
                    NLogWrapper.FileLogger?.Info("duplicate_group_autoselect success header=" + header + " attempt=" + attempt);
                    return;
                }
                if (!ShouldRetryDuplicateGroupAutoSelect(lastReason) || IsShellClosingOrClosed() || attempt == 1)
                {
                    break;
                }
                Task signal = duplicateGroupsChanged.Task;
                await Task.WhenAny(signal, Task.Delay(1500));
                if (requestVersion != Volatile.Read(ref _duplicateMaintenanceSelectionVersion))
                {
                    return;
                }
                duplicateGroupsChanged = CreateDuplicateGroupsChangedSignal();
            }
            if (!IsShellClosingOrClosed())
            {
                NLogWrapper.FileLogger?.Warn("duplicate_group_autoselect pending header=" + header + " reason=" + lastReason);
            }
        }
        finally
        {
            viewModel.MaintenanceTree.PropertyChanged -= handler;
        }
    }

    private static TaskCompletionSource<bool> CreateDuplicateGroupsChangedSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<(bool Succeeded, string FailReason)> TryApplyDuplicateMaintenanceSelectionAsync(
        string header,
        MainWindowViewModel viewModel,
        int requestVersion)
    {
        string failReason = string.Empty;
        DispatcherPriority[] retryPriorities = [DispatcherPriority.Loaded, DispatcherPriority.Render, DispatcherPriority.ContextIdle];
        foreach (DispatcherPriority retryPriority in retryPriorities)
        {
            await Dispatcher.Yield(retryPriority);
            if (IsShellClosingOrClosed() || requestVersion != Volatile.Read(ref _duplicateMaintenanceSelectionVersion))
            {
                failReason = "window_closed";
                return (false, failReason);
            }
            if (TrySelectDuplicateGroupByHeader(header, viewModel, out failReason))
            {
                return (true, string.Empty);
            }
            if (!ShouldRetryDuplicateGroupAutoSelect(failReason))
            {
                return (false, failReason);
            }
        }
        return (false, failReason);
    }

    private static bool ShouldRetryDuplicateGroupAutoSelect(string failReason)
    {
        return failReason == "duplicate_tree_items_not_updated" ||
            failReason == "duplicated_list_null" ||
            failReason == "group_not_found" ||
            failReason == "container_not_realized" ||
            failReason == "duplicate_items_host_not_found" ||
            failReason == "bring_index_out_of_range";
    }

    private bool TrySelectDuplicateGroupByHeader(string header, MainWindowViewModel viewModel, out string failReason)
    {
        failReason = string.Empty;
        // XAML上で Name="treeViewItemSearchDuplicated" を持つTreeViewItem
        TreeViewItem duplicateRootItem = treeViewItemSearchDuplicated;
        if (duplicateRootItem == null)
        {
            failReason = "root_not_found";
            return false;
        }

        List<DuplicateGroup> duplicatedList = viewModel.MaintenanceTree.DuplicateChartGroups;
        if (duplicatedList == null)
        {
            failReason = "duplicated_list_null";
            return false;
        }

        DuplicateGroup targetGroup = null;
        for (int i = 0; i < duplicatedList.Count; i++)
        {
            if (duplicatedList[i].Header == header)
            {
                targetGroup = duplicatedList[i];
                break;
            }
        }
        if (targetGroup == null)
        {
            failReason = "group_not_found";
            return false;
        }

        // 親TreeViewItemが展開されていることを確認
        treeViewItemFullScanCheck.IsExpanded = true;
        duplicateRootItem.IsExpanded = true;
        duplicateRootItem.BringIntoView();
        duplicateRootItem.UpdateLayout();

        int targetIndex = FindDuplicateGroupTreeItemIndex(duplicateRootItem, targetGroup);
        if (targetIndex < 0)
        {
            failReason = "duplicate_tree_items_not_updated";
            return false;
        }

        TreeViewItem targetItem = duplicateRootItem.ItemContainerGenerator.ContainerFromIndex(targetIndex) as TreeViewItem;
        if (targetItem == null)
        {
            if (!TryRealizeVirtualizedDuplicateGroupItem(duplicateRootItem, targetIndex, out failReason))
            {
                return false;
            }
            targetItem = duplicateRootItem.ItemContainerGenerator.ContainerFromIndex(targetIndex) as TreeViewItem;
        }

        // コンテナを取得して選択
        if (targetItem != null)
        {
            targetItem.IsSelected = true;
            targetItem.IsExpanded = true;
            targetItem.BringIntoView();
            return true;
        }
        failReason = "container_not_realized";
        return false;
    }

    private static int FindDuplicateGroupTreeItemIndex(TreeViewItem duplicateRootItem, DuplicateGroup targetGroup)
    {
        ItemCollection treeItems = duplicateRootItem.Items;
        for (int i = 0; i < treeItems.Count; i++)
        {
            if (ReferenceEquals(treeItems[i], targetGroup))
            {
                return i;
            }
        }
        return -1;
    }

    private bool TryRealizeVirtualizedDuplicateGroupItem(TreeViewItem duplicateRootItem, int targetIndex, out string failReason)
    {
        failReason = string.Empty;
        if (playlistTreeBringIndexIntoViewMethod == null)
        {
            failReason = "bring_index_method_not_found";
            return false;
        }
        VirtualizingStackPanel duplicateItemsHostPanel = TryGetTreeViewItemItemsHostPanel(duplicateRootItem);
        if (duplicateItemsHostPanel == null)
        {
            failReason = "duplicate_items_host_not_found";
            return false;
        }
        try
        {
            playlistTreeBringIndexIntoViewMethod.Invoke(duplicateItemsHostPanel, [targetIndex]);
            duplicateRootItem.UpdateLayout();
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException)
        {
            failReason = "bring_index_out_of_range";
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            failReason = "bring_index_out_of_range";
            return false;
        }
        catch (Exception ex)
        {
            failReason = "bring_index_failed_" + ex.GetType().Name;
            return false;
        }
    }

    private static VirtualizingStackPanel TryGetTreeViewItemItemsHostPanel(TreeViewItem treeViewItem)
    {
        treeViewItem.ApplyTemplate();
        treeViewItem.UpdateLayout();
        return FindItemsHostPanelForOwner(treeViewItem, treeViewItem);
    }

    private static VirtualizingStackPanel FindItemsHostPanelForOwner(DependencyObject parent, ItemsControl owner)
    {
        if (parent == null)
        {
            return null;
        }
        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(parent);
        }
        catch
        {
            return null;
        }
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, childIndex);
            if (child is VirtualizingStackPanel panel && ReferenceEquals(ItemsControl.GetItemsOwner(panel), owner))
            {
                return panel;
            }
            VirtualizingStackPanel descendant = FindItemsHostPanelForOwner(child, owner);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    /// <summary>
    /// 重複フォルダのキーボードショートカットハンドラ。
    /// Ctrl+G: フォルダが2つの場合、もう一方のフォルダへマージを実行する。
    /// </summary>
    private async void duplicateFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.G || !duplicateMaintenanceTerminal.IsExecuteShortcut)
        {
            return;
        }

        if (sender is not TreeViewItem folderItem)
        {
            return;
        }

        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        string srcPath = folderItem.DataContext as string;
        if (string.IsNullOrWhiteSpace(srcPath))
        {
            return;
        }

        // 親TreeViewItemからDuplicateGroupを取得
        TreeViewItem groupItem = WPFUtil.FindVisualParent<TreeViewItem>(folderItem);
        if (groupItem?.DataContext is not DuplicateGroup duplicateGroup)
        {
            return;
        }

        DuplicateFolderKeyboardAction action = viewModel.DuplicateMaintenanceWorkflow
            .CaptureDuplicateFolderKeyboardAction(duplicateGroup, srcPath);
        if (action.Kind == DuplicateFolderKeyboardActionKind.Merge)
        {
            e.Handled = true;
            if (ShouldBlockChartPackageMutationInteraction("duplicate_merge_execute"))
            {
                return;
            }
            DuplicateMaintenanceMutationResult result = await duplicateMaintenanceTerminal
                .MergeFolderAsync(srcPath, action.DestinationPath, duplicateGroup);
            await ApplyDuplicateFolderMergeResultAsync(
                result,
                srcPath,
                action.DestinationPath,
                viewModel);
        }
        else if (action.Kind == DuplicateFolderKeyboardActionKind.Cleanup)
        {
            // フォルダが1つの場合: ハッシュ重複BMSファイルの整理
            e.Handled = true;
            DuplicateMaintenanceMutationResult result = await duplicateMaintenanceTerminal
                .CleanupHashAsync(duplicateGroup, srcPath);
            await ApplyDuplicateHashCleanupResultAsync(result, srcPath, viewModel);
        }
        else if (action.Kind == DuplicateFolderKeyboardActionKind.OpenContextMenu)
        {
            // フォルダが3つ以上の場合: コンテキストメニューを開いてマージ先を選択
            OpenDuplicateFolderContextMenu(folderItem);
        }

        e.Handled = true;
    }

    /// <summary>
    /// フォルダが1つの重複グループで、ハッシュ重複BMSファイルを整理する。
    /// 各ハッシュグループごとに1つだけ残し、残りをごみ箱へ移動する。
    /// 保持ルール: 更新日時が最も古いものを優先、同日時ならファイル名が最も短いものを優先。
    /// </summary>
    private async Task ApplyDuplicateHashCleanupResultAsync(
        DuplicateMaintenanceMutationResult result,
        string folderPath,
        MainWindowViewModel viewModel)
    {
        if (!result.Succeeded)
        {
            if (result.Failure != null)
            {
                NLogWrapper.FileLogger?.Warn(result.Failure, "duplicate_hash_cleanup_failed");
            }
            return;
        }
        NLogWrapper.FileLogger?.Info(string.Format(
            "Cleaned up {0} duplicate hash BMS file(s) in folder: {1}",
            result.RemovedChartCount,
            Path.GetFileName(folderPath)));
        await ApplyDuplicateMaintenanceSelectionAsync(result.SelectionHeader, viewModel);
    }

    /// <summary>
    /// フォルダが3つ以上の場合にコンテキストメニューをプログラムから開き、
    /// 「マージ先」サブメニューを展開した状態にする。
    /// </summary>
    private void OpenDuplicateFolderContextMenu(TreeViewItem folderItem)
    {
        ContextMenu contextMenu = folderItem.ContextMenu;
        if (contextMenu == null)
        {
            return;
        }

        contextMenu.PlacementTarget = folderItem;
        contextMenu.IsOpen = true;

        // コンテキストメニューのOpenedイベントでマージ先リストが生成されるため、
        // メニューが開いた後にサブメニューを展開する
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsShellClosingOrClosed())
            {
                return;
            }
            foreach (Control item in (IEnumerable)contextMenu.Items)
            {
                if (item.Name == "treeViewDuplicateFolderContextMenuItemMergeInto" && item is MenuItem mergeMenuItem)
                {
                    mergeMenuItem.IsSubmenuOpen = true;
                    break;
                }
            }
        }));
    }

    private void CancelRelatedDocumentRequest()
    {
        if (relatedDocumentRequestCancellation == null)
        {
            return;
        }
        relatedDocumentRequestCancellation.Cancel();
        relatedDocumentRequestCancellation = null;
        NLogWrapper.DebuggerLogger?.Trace("Cancel related document context menu request");
    }

    private CancellationToken BeginRelatedDocumentRequest()
    {
        CancelRelatedDocumentRequest();
        relatedDocumentRequestCancellation = new CancellationTokenSource();
        return relatedDocumentRequestCancellation.Token;
    }

    private async Task PopulateRelatedDocumentsMenuAsync(
        MainWindowSelectedChartExternalActionsTerminal owner,
        MenuItem menuItem,
        ChartOperationTarget target,
        CancellationToken cancellationToken)
    {
        RelatedDocumentQueryReceipt receipt = await owner
            .QueryRelatedDocumentsAsync(target, cancellationToken);
        if (cancellationToken.IsCancellationRequested || IsShellClosingOrClosed())
        {
            return;
        }
        switch (receipt.Status)
        {
            case RelatedDocumentQueryStatus.Available:
                menuItem.ItemsSource = receipt.Paths;
                menuItem.IsEnabled = true;
                break;
            case RelatedDocumentQueryStatus.Unavailable:
            case RelatedDocumentQueryStatus.Failed:
                menuItem.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void tableContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_opened"))
        {
            e.Handled = true;
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("datagrid_context_menu_opened"))
        {
            e.Handled = true;
            if (sender is ContextMenu blockedContextMenu)
            {
                CloseContextMenuIfOpen(blockedContextMenu);
            }
            return;
        }
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out object row))
        {
            NLogWrapper.FileLogger?.Info("playlist_context_menu rowResolve=False sourceType=" + sender?.GetType().FullName);
            return;
        }
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        _lastOpenedContextMenu = contextMenu;
        bool isPlaylistRow = GridRowResolver.IsPlaylistRow(row);
        MainChartListOperationContext operationContext = mainWindowViewModel.MainChartList.CurrentOperationContext;
        MainViewOperationSection effectiveSection = operationContext.OperationSection;
        bool isPendingSelected = IsPendingMainViewSection(effectiveSection);
        bool isInstalledSelected = IsInstalledMainViewSection(effectiveSection);
        bool isPlaylistSelected = IsPlaylistMainViewSection(effectiveSection);
        ChartOperationSourceScope sourceScope = operationContext.SourceScope;
        if (!GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget rowTarget))
        {
            rowTarget = null;
            if (!isPlaylistRow)
            {
                NLogWrapper.FileLogger?.Info("playlist_context_menu rowResolve=True but unsupported rowType=" + row?.GetType().FullName);
                return;
            }
        }
        ChartContextMenuState contextMenuState = ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(
            isPlaylistRow,
            isPendingSelected,
            isInstalledSelected,
            isPlaylistSelected,
            rowTarget,
            GetSelectedChartTargets(sourceScope)));
        bool isInstallListSelected = contextMenuState.IsInstallListSelected;
        bool isPlaylistContext = contextMenuState.IsPlaylistContext;
        string chartPath = rowTarget?.Chart?.Path;
        IReadOnlyList<ChartOperationTarget> selectedTargets = contextMenuState.SelectedTargets;
        bool hasBmsonSelection = contextMenuState.HasBmsonSelection;
        bool hasBmsSelection = contextMenuState.HasBmsSelection;
        bool useConfiguredExternalActions = selectedChartContextMenuTerminals.SelectedChartExternalActions.HasConfiguredActions;
        RightClickActionResolutionInput configuredTargetInput = null;
        RightClickActionResolution configuredResolution = useConfiguredExternalActions
            && selectedChartContextMenuTerminals.SelectedChartExternalActions.TryCreateResolutionInput(
                rowTarget,
                out configuredTargetInput)
                    ? selectedChartContextMenuTerminals.SelectedChartExternalActions.ResolveConfiguredActions(configuredTargetInput)
                    : null;
        MaterializeConfiguredExternalActions(
            contextMenu,
            configuredResolution,
            selectedChartContextMenuTerminals.SelectedChartExternalActions,
            includePrograms: rowTarget?.IsPlaylistMissing != true,
            programAnchorName: "tableContextMenuItemOpenBMSFile",
            webAnchorName: "tableContextMenuItemOpenURL",
            programParentName: "tableContextMenuItemOpenProgramActions");
        bool canOpenExplorer = selectedChartContextMenuTerminals.SelectedChartExternalActions.CanExecute(
            rowTarget,
            SelectedChartExternalActionKind.OpenExplorer);
        bool canOpenFile = selectedChartContextMenuTerminals.SelectedChartExternalActions.CanExecute(
            rowTarget,
            SelectedChartExternalActionKind.OpenFile);
        bool canOpenInstallDestination = mainWindowViewModel.PendingPackages.CanOpenInstallDestination(
            rowTarget,
            selectedTargets,
            isPendingSelected,
            isPlaylistRow);
        CancelRelatedDocumentRequest();
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        MenuItem menuItemFindExternalPackage = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        MenuItem menuItem7 = null;
        MenuItem menuItem8 = null;
        MenuItem menuItem9 = null;
        MenuItem menuItem10 = null;
        MenuItem menuItem11 = null;
        MenuItem menuItem12 = null;
        MenuItem menuItemFullScanAllCharts = null;
        MenuItem menuItem13 = null;
        MenuItem menuItem14 = null;
        MenuItem menuItem15 = null;
        MenuItem menuItem16 = null;
        MenuItem menuItem17 = null;
        MenuItem menuItemOpenInstallDestination = null;
        MenuItem menuItemOpenDocument = null;
        MenuItem menuItem18 = null;
        MenuItem menuItemRenameInvalidExt = null;
        Separator separator = null;
        MenuItem menuItem19 = null;
        Separator separator2 = null;
        MenuItem menuItemRemoveChartInfoParseFailure = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "tableContextMenuItemOpenURL":
                    menuItem = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenURLdiff":
                    menuItem2 = item as MenuItem;
                    break;
                case "tableContextMenuItemFindExternalPackage":
                    menuItemFindExternalPackage = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenExplorer":
                    menuItem3 = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenInstallDestination":
                    menuItemOpenInstallDestination = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenBMSFile":
                    menuItem4 = item as MenuItem;
                    break;
                case "tableContextMenuItemRegisterScore":
                    menuItem18 = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenDocument":
                    menuItemOpenDocument = item as MenuItem;
                    break;
                case "tableContextMenuItemUpdateRankingData":
                    menuItem7 = item as MenuItem;
                    break;
                case "tableContextMenuItemInstall":
                    menuItem8 = item as MenuItem;
                    break;
                case "tableContextMenuItemFixInstall":
                    menuItem9 = item as MenuItem;
                    break;
                case "tableContextMenuItemFullScanCheck":
                    menuItem10 = item as MenuItem;
                    foreach (Control item2 in (IEnumerable)menuItem10.Items)
                    {
                        string name = item2.Name;
                        if (name == "tableContextMenuItemFullScanCheckAllCharts")
                        {
                            menuItemFullScanAllCharts = item2 as MenuItem;
                        }
                        else if (!(name == "tableContextMenuItemIgnoreFileScanCheck"))
                        {
                            if (name == "tableContextMenuItemNotIgnoreFileScanCheck")
                            {
                                menuItem12 = item2 as MenuItem;
                            }
                        }
                        else
                        {
                            menuItem11 = item2 as MenuItem;
                        }
                    }
                    break;
                case "tableContextMenuItemMoveFile":
                    menuItem13 = item as MenuItem;
                    break;
                case "tableContextMenuItemDeleteEntry":
                    menuItem14 = item as MenuItem;
                    break;
                case "tableContextMenuItemDeleteFile":
                    menuItem15 = item as MenuItem;
                    foreach (Control item3 in (IEnumerable)menuItem15.Items)
                    {
                        if (item3.Name == "tableContextMenuItemRenameInvalidExt")
                        {
                            menuItemRenameInvalidExt = item3 as MenuItem;
                        }
                    }
                    break;
                case "tableContextMenuItemAutoRenameFolder":
                    menuItem16 = item as MenuItem;
                    break;
                case "tableContextMenuItemFixEncoding":
                    menuItem17 = item as MenuItem;
                    break;
                case "tableContextMenuSeparatorForFolderview":
                    separator = item as Separator;
                    break;
                case "tableContextMenuItemConvertToAudioFile":
                    menuItem19 = item as MenuItem;
                    break;
                case "tableContextMenuSeparatorForConvert":
                    separator2 = item as Separator;
                    break;
                case "tableContextMenuItemRemoveChartInfoParseFailure":
                    menuItemRemoveChartInfoParseFailure = item as MenuItem;
                    break;
            }
        }
        List<object> effectivePlaylistUrlRows = GetEffectiveContextMenuRows(row);
        PlaylistUrlContextMenuAvailability playlistUrlAvailability =
            playlistWorkspaceTerminals.UrlAcquisition.CaptureAvailability(
                row,
                effectivePlaylistUrlRows);
        if (isPlaylistRow)
        {
            if (menuItem != null)
            {
                menuItem.Header = playlistUrlAvailability.IsBulkContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url : BeMusicSeeker.Properties.Resources.Open_Url;
                menuItem.Visibility = Visibility.Visible;
                menuItem.IsEnabled = playlistUrlAvailability.CanOpenUrl;
            }
            if (menuItem2 != null)
            {
                menuItem2.Header = playlistUrlAvailability.IsBulkContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url_diff : BeMusicSeeker.Properties.Resources.Open_Url_diff;
                menuItem2.Visibility = Visibility.Visible;
                menuItem2.IsEnabled = playlistUrlAvailability.CanOpenDiffUrl;
            }
            if (menuItemFindExternalPackage != null)
            {
                menuItemFindExternalPackage.Visibility = Visibility.Visible;
                menuItemFindExternalPackage.IsEnabled = playlistUrlAvailability.CanFindExternalPackage;
            }
        }
        else
        {
            if (menuItem != null)
            {
                menuItem.Visibility = Visibility.Collapsed;
            }
            if (menuItem2 != null)
            {
                menuItem2.Visibility = Visibility.Collapsed;
            }
            if (menuItemFindExternalPackage != null)
            {
                menuItemFindExternalPackage.Visibility = Visibility.Collapsed;
            }
        }
        if (menuItem3 != null && menuItem4 != null && menuItemOpenDocument != null)
        {
            menuItem3.IsEnabled = canOpenExplorer;
            menuItem4.IsEnabled = canOpenFile;
            menuItemOpenDocument.ItemsSource = null;
            menuItemOpenDocument.IsEnabled = false;
            if (selectedChartContextMenuTerminals.SelectedChartExternalActions.CanQueryRelatedDocuments(rowTarget))
            {
                menuItemOpenDocument.Visibility = Visibility.Visible;
                CancellationToken token = BeginRelatedDocumentRequest();
                PopulateRelatedDocumentsMenuAsync(
                    selectedChartContextMenuTerminals.SelectedChartExternalActions,
                    menuItemOpenDocument,
                    rowTarget,
                    token).ObserveFault("tableContextMenuOpened");
            }
            else
            {
                menuItemOpenDocument.Visibility = Visibility.Collapsed;
            }
        }
        if (menuItem18 != null)
        {
            bool hasScoreViewerTarget = selectedChartContextMenuTerminals.ScoreViewer.HasScoreViewerTarget(selectedTargets);
            menuItem18.Header = selectedTargets.Count > 1
                ? BeMusicSeeker.Properties.Resources.Register_chart_with_viewer
                : BeMusicSeeker.Properties.Resources.Open_chart_viewer;
            menuItem18.Visibility = hasScoreViewerTarget ? Visibility.Visible : Visibility.Collapsed;
            menuItem18.IsEnabled = selectedChartContextMenuTerminals.ScoreViewer.CanRegisterScoreViewer(selectedTargets);
        }
        if (menuItem7 != null)
        {
            bool hasRankingTarget = selectedChartContextMenuTerminals.RankingCache.HasRankingTarget(selectedTargets);
            menuItem7.Visibility = hasRankingTarget ? Visibility.Visible : Visibility.Collapsed;
            menuItem7.IsEnabled = selectedChartContextMenuTerminals.RankingCache.CanRequestRanking(selectedTargets);
        }
        MenuItem menuItemDeleteInstallPackages = contextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "tableContextMenuItemDeleteInstallPackages");
        List<string> selectedChartInfoParseFailureMd5s = GetSelectedChartInfoParseFailureMd5s();
        if (menuItemRemoveChartInfoParseFailure != null)
        {
            bool canRemoveChartInfoParseFailure = ShouldShowChartInfoParseFailureRemovalMenu(IsChartInfoParseErrorMainViewSection(effectiveSection), selectedChartInfoParseFailureMd5s);
            menuItemRemoveChartInfoParseFailure.Visibility = canRemoveChartInfoParseFailure ? Visibility.Visible : Visibility.Collapsed;
            menuItemRemoveChartInfoParseFailure.IsEnabled = canRemoveChartInfoParseFailure;
        }
        bool isNotOwnedPlaylistRow = rowTarget?.IsPlaylistMissing == true;
        NLogWrapper.FileLogger?.Info("playlist_context_menu rowType=" + row?.GetType().FullName + " isPlaylistRow=" + isPlaylistRow + " isPlaylistContext=" + isPlaylistContext + " isNotOwned=" + isNotOwnedPlaylistRow + " section=" + effectiveSection + " treeSection=" + _currentTreeSelectionSection + " sourceScope=" + sourceScope + " kind=" + rowTarget?.Chart.Kind + " path=" + (chartPath ?? string.Empty));
        if (menuItemOpenInstallDestination != null)
        {
            menuItemOpenInstallDestination.Visibility = (canOpenInstallDestination ? Visibility.Visible : Visibility.Collapsed);
            menuItemOpenInstallDestination.IsEnabled = canOpenInstallDestination;
        }
        if (menuItem8 != null)
        {
            menuItem8.Visibility = ((!isPendingSelected || isPlaylistContext) ? Visibility.Collapsed : Visibility.Visible);
            menuItem8.IsEnabled = isPendingSelected && !isPlaylistContext;
        }
        if (menuItem10 != null)
        {
            bool isFullScanMenuVisible = contextMenuState.CanShowResourceHealthMenu;
            menuItem10.Visibility = ((!isFullScanMenuVisible) ? Visibility.Collapsed : Visibility.Visible);
            menuItem10.IsEnabled = isFullScanMenuVisible;
        }
        if (menuItem13 != null)
        {
            bool canMoveSelectedFiles = contextMenuState.CanMoveSelectedFiles;
            menuItem13.Visibility = ((!canMoveSelectedFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem13.IsEnabled = canMoveSelectedFiles && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.MoveInLibrary) && !string.IsNullOrWhiteSpace(target.Chart.Path) && LongPathFileSystem.FileExists(target.Chart.Path));
        }
        if (menuItem14 != null)
        {
            menuItem14.Visibility = ((!isPlaylistContext) ? Visibility.Collapsed : Visibility.Visible);
            menuItem14.IsEnabled = isPlaylistContext;
        }
        if (menuItem15 != null)
        {
            bool canDeleteFiles = contextMenuState.CanDeleteFiles;
            menuItem15.Visibility = ((!canDeleteFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem15.IsEnabled = canDeleteFiles;
            if (menuItemRenameInvalidExt != null)
            {
                bool canRenameInvalidExt = contextMenuState.CanRenameInvalidExtension;
                menuItemRenameInvalidExt.Visibility = canRenameInvalidExt ? Visibility.Visible : Visibility.Collapsed;
                menuItemRenameInvalidExt.IsEnabled = canRenameInvalidExt;
            }
        }
        if (separator != null)
        {
            bool isFolderViewSeparatorVisible = contextMenuState.CanShowFolderViewSeparator;
            separator.Visibility = ((!isFolderViewSeparatorVisible) ? Visibility.Collapsed : Visibility.Visible);
            separator.IsEnabled = isFolderViewSeparatorVisible;
        }
        if (menuItem16 != null)
        {
            bool canAutoRenameFolders = contextMenuState.CanAutoRenameFolders
                && mainWindowViewModel.FolderAutoRenameWorkflow?.IsActive != true;
            menuItem16.Visibility = ((!canAutoRenameFolders) ? Visibility.Collapsed : Visibility.Visible);
            menuItem16.IsEnabled = canAutoRenameFolders;
        }
        if (menuItem17 != null)
        {
            bool canFixEncoding = contextMenuState.CanFixEncoding;
            menuItem17.Visibility = ((!canFixEncoding) ? Visibility.Collapsed : Visibility.Visible);
            menuItem17.IsEnabled = canFixEncoding;
        }
        if (menuItem9 != null)
        {
            bool isInstalledLocationFixVisible = IsFullScanMainViewSection(effectiveSection) && !isPlaylistContext;
            menuItem9.Visibility = ((!isInstalledLocationFixVisible) ? Visibility.Collapsed : Visibility.Visible);
            menuItem9.IsEnabled = isInstalledLocationFixVisible && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        }
        if (menuItem11 != null)
        {
            bool isSelected = treeViewItemFullScanCheck.IsSelected;
            bool hasResourceHealthTarget = contextMenuState.HasResourceHealthTarget;
            menuItem11.Visibility = ((!isSelected) ? Visibility.Collapsed : Visibility.Visible);
            menuItem11.IsEnabled = isSelected && hasResourceHealthTarget;
        }
        if (menuItem12 != null)
        {
            bool isSelected2 = treeViewItemFullScanCheckIgnored.IsSelected;
            bool hasResourceHealthTarget = contextMenuState.HasResourceHealthTarget;
            menuItem12.Visibility = ((!isSelected2) ? Visibility.Collapsed : Visibility.Visible);
            menuItem12.IsEnabled = isSelected2 && hasResourceHealthTarget;
        }
        if (menuItemFullScanAllCharts != null)
        {
            bool canRescanAllCharts = menuItem10?.Visibility == Visibility.Visible
                && mainWindowViewModel.MaintenanceRescanWorkflow?.IsActive != true;
            menuItemFullScanAllCharts.Visibility = Visibility.Visible;
            menuItemFullScanAllCharts.IsEnabled = canRescanAllCharts;
        }
        if (menuItem19 != null && separator2 != null)
        {
            bool canConvertToAudio = contextMenuState.CanConvertToAudio;
            Separator convertSeparator = separator2;
            Visibility visibility = (menuItem19.Visibility = ((!canConvertToAudio) ? Visibility.Collapsed : Visibility.Visible));
            convertSeparator.Visibility = visibility;
            Separator convertSeparator2 = separator2;
            bool isEnabled = (menuItem19.IsEnabled = canConvertToAudio && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.ConvertToAudio) && !string.IsNullOrWhiteSpace(target.Chart.Path) && LongPathFileSystem.FileExists(target.Chart.Path)));
            convertSeparator2.IsEnabled = isEnabled;
        }
        if (hasBmsonSelection)
        {
            if (!hasBmsSelection)
            {
                if (menuItem18 != null)
                {
                    menuItem18.Visibility = Visibility.Collapsed;
                    menuItem18.IsEnabled = false;
                }
                if (menuItem7 != null)
                {
                    menuItem7.Visibility = Visibility.Collapsed;
                    menuItem7.IsEnabled = false;
                }
                if (menuItem19 != null)
                {
                    menuItem19.Visibility = Visibility.Collapsed;
                    menuItem19.IsEnabled = false;
                }
                if (menuItemRenameInvalidExt != null)
                {
                    menuItemRenameInvalidExt.Visibility = Visibility.Collapsed;
                    menuItemRenameInvalidExt.IsEnabled = false;
                }
                if (menuItem17 != null)
                {
                    menuItem17.Visibility = Visibility.Collapsed;
                    menuItem17.IsEnabled = false;
                }
            }
        }
        if (menuItemDeleteInstallPackages != null)
        {
            menuItemDeleteInstallPackages.Visibility = (isInstallListSelected ? Visibility.Visible : Visibility.Collapsed);
            menuItemDeleteInstallPackages.IsEnabled = contextMenuState.CanDeleteInstallPackages;
        }
        if (isNotOwnedPlaylistRow)
        {
            if (menuItem3 != null)
            {
                menuItem3.Visibility = Visibility.Collapsed;
                menuItem3.IsEnabled = false;
            }
            if (menuItem4 != null)
            {
                menuItem4.Visibility = Visibility.Collapsed;
                menuItem4.IsEnabled = false;
            }
            if (menuItemOpenDocument != null)
            {
                menuItemOpenDocument.Visibility = Visibility.Collapsed;
                menuItemOpenDocument.IsEnabled = false;
            }
            if (menuItem18 != null)
            {
                menuItem18.Visibility = Visibility.Collapsed;
                menuItem18.IsEnabled = false;
            }
            if (menuItem7 != null)
            {
                menuItem7.Visibility = Visibility.Collapsed;
                menuItem7.IsEnabled = false;
            }
            if (menuItem8 != null)
            {
                menuItem8.Visibility = Visibility.Collapsed;
                menuItem8.IsEnabled = false;
            }
            if (menuItem9 != null)
            {
                menuItem9.Visibility = Visibility.Collapsed;
                menuItem9.IsEnabled = false;
            }
            if (menuItem10 != null)
            {
                menuItem10.Visibility = Visibility.Collapsed;
                menuItem10.IsEnabled = false;
            }
            if (menuItem13 != null)
            {
                menuItem13.Visibility = Visibility.Collapsed;
                menuItem13.IsEnabled = false;
            }
            if (menuItem15 != null)
            {
                menuItem15.Visibility = Visibility.Collapsed;
                menuItem15.IsEnabled = false;
            }
            if (menuItem16 != null)
            {
                menuItem16.Visibility = Visibility.Collapsed;
                menuItem16.IsEnabled = false;
            }
            if (menuItem17 != null)
            {
                menuItem17.Visibility = Visibility.Collapsed;
                menuItem17.IsEnabled = false;
            }
            if (menuItem19 != null)
            {
                menuItem19.Visibility = Visibility.Collapsed;
                menuItem19.IsEnabled = false;
            }
            if (separator != null)
            {
                separator.Visibility = Visibility.Collapsed;
                separator.IsEnabled = false;
            }
            if (separator2 != null)
            {
                separator2.Visibility = Visibility.Collapsed;
                separator2.IsEnabled = false;
            }
            if (menuItemDeleteInstallPackages != null)
            {
                menuItemDeleteInstallPackages.Visibility = Visibility.Collapsed;
                menuItemDeleteInstallPackages.IsEnabled = false;
            }
        }
    }

    private void tableContextMenuPlaylistMissingOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_playlist_missing_context_menu_opened"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out object row))
        {
            NLogWrapper.FileLogger?.Info("playlist_missing_context_menu rowResolve=False sourceType=" + sender?.GetType().FullName);
            return;
        }
        _lastOpenedContextMenu = contextMenu;
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        BMSTableEntry entry = GridRowResolver.GetPlaylistEntry(row);
        GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget rowTarget);
        bool useConfiguredExternalActions = selectedChartContextMenuTerminals.SelectedChartExternalActions.HasConfiguredActions;
        RightClickActionResolutionInput configuredTargetInput = null;
        RightClickActionResolution configuredResolution = useConfiguredExternalActions
            && selectedChartContextMenuTerminals.SelectedChartExternalActions.TryCreateResolutionInput(
                rowTarget,
                out configuredTargetInput)
                    ? selectedChartContextMenuTerminals.SelectedChartExternalActions.ResolveConfiguredActions(configuredTargetInput)
                    : null;
        MaterializeConfiguredExternalActions(
            contextMenu,
            configuredResolution,
            selectedChartContextMenuTerminals.SelectedChartExternalActions,
            includePrograms: false,
            programAnchorName: "tableContextMenuItemOpenBMSFile",
            webAnchorName: "tableContextMenuItemOpenURL",
            programParentName: "tableContextMenuItemOpenProgramActions");
        bool isBmsonContextRow = rowTarget?.Chart.Kind == ChartFileKind.Bmson;
        bool canOpenScoreViewer = selectedChartContextMenuTerminals.ScoreViewer.HasScoreViewerTarget([rowTarget]);
        bool canUpdateRanking = selectedChartContextMenuTerminals.RankingCache.CanRequestRanking([rowTarget]);
        List<object> effectivePlaylistUrlRows = GetEffectiveContextMenuRows(row);
        PlaylistUrlContextMenuAvailability playlistUrlAvailability =
            playlistWorkspaceTerminals.UrlAcquisition.CaptureAvailability(
                row,
                effectivePlaylistUrlRows);
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "tableContextMenuItemOpenURL":
                    if (item is MenuItem openUrlMenuItem)
                    {
                        openUrlMenuItem.Header = playlistUrlAvailability.IsBulkContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url : BeMusicSeeker.Properties.Resources.Open_Url;
                    }
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = playlistUrlAvailability.CanOpenUrl;
                    break;
                case "tableContextMenuItemOpenURLdiff":
                    if (item is MenuItem openUrlDiffMenuItem)
                    {
                        openUrlDiffMenuItem.Header = playlistUrlAvailability.IsBulkContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url_diff : BeMusicSeeker.Properties.Resources.Open_Url_diff;
                    }
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = playlistUrlAvailability.CanOpenDiffUrl;
                    break;
                case "tableContextMenuItemFindExternalPackage":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = playlistUrlAvailability.CanFindExternalPackage;
                    break;
                case "tableContextMenuItemDeleteEntry":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = entry != null;
                    break;
                case "tableContextMenuItemRegisterScore":
                    item.Visibility = canOpenScoreViewer ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canOpenScoreViewer;
                    break;
                case "tableContextMenuItemUpdateRankingData":
                    item.Visibility = canUpdateRanking ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canUpdateRanking;
                    break;
            }
        }
        NLogWrapper.FileLogger?.Info("playlist_missing_context_menu rowType=" + row?.GetType().FullName + " entryParent=" + entry?.parent?.name + " isBmson=" + isBmsonContextRow + " url=" + playlistUrlAvailability.CanOpenUrl + " urlDiff=" + playlistUrlAvailability.CanOpenDiffUrl + " canOpenScoreViewer=" + canOpenScoreViewer + " canUpdateRanking=" + canUpdateRanking);
    }

    private void playHistoryContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_play_history_context_menu_opened"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out object row)
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        playHistoryDateSearchSnapshot = null;
        bool hasContextMenuState = TryGetPlayHistoryContextMenuState(row, out PlayHistoryContextMenuState state);
        bool hasDateSearchSnapshot = viewModel.PlayHistory.TryCreateDateSearchSnapshot(
            customTableView?.GetSelectedRowsSnapshot(),
            out playHistoryDateSearchSnapshot);
        if (!hasContextMenuState && !hasDateSearchSnapshot)
        {
            // A test or host may reuse the resource instance after a prior open.  Reset
            // the aggregate item before returning so stale visibility cannot advertise
            // an unavailable action for the current selection.
            ResetPlayHistoryDateSearchMenuItem(contextMenu);
            return;
        }

        CancelRelatedDocumentRequest();
        _lastOpenedContextMenu = contextMenu;
        bool useConfiguredExternalActions = selectedChartContextMenuTerminals.SelectedChartExternalActions.HasConfiguredActions;
        RightClickActionResolutionInput configuredInput = null;
        if (useConfiguredExternalActions && hasContextMenuState)
        {
            viewModel.PlayHistory.TryCreateRightClickActionResolutionInput(
                row,
                LongPathFileSystem.FileExists,
                out configuredInput);
        }
        RightClickActionResolution configuredResolution = useConfiguredExternalActions && configuredInput != null
            ? selectedChartContextMenuTerminals.SelectedChartExternalActions.ResolveConfiguredActions(configuredInput)
            : null;
        MaterializeConfiguredExternalActions(
            contextMenu,
            configuredResolution,
            selectedChartContextMenuTerminals.SelectedChartExternalActions,
            includePrograms: configuredInput?.LocalFilePath != null,
            programAnchorName: "playHistoryContextMenuItemOpenAssociated",
            webAnchorName: null,
            programParentName: "playHistoryContextMenuItemOpenProgramActions");
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "playHistoryContextMenuSeparatorLocal":
                    item.Visibility = state?.HasExternalLinkItem == true && state.HasLocalChartItem ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "playHistoryContextMenuItemOpenAssociated":
                    item.Visibility = state?.CanOpenAssociated == true ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state?.CanOpenAssociated == true && LongPathFileSystem.FileExists(state.ChartPath);
                    break;
                case "playHistoryContextMenuItemOpenExplorer":
                    item.Visibility = state?.CanOpenExplorer == true ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state?.CanOpenExplorer == true && LongPathFileSystem.FileExists(state.ChartPath);
                    break;
                case "playHistoryContextMenuItemRegisterScore":
                    item.Visibility = state?.CanOpenScoreViewer == true ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state?.CanOpenScoreViewer == true && LongPathFileSystem.FileExists(state.ChartPath);
                    break;
                case "playHistoryContextMenuSeparatorHash":
                    item.Visibility = state != null
                        && (state.HasExternalLinkItem || state.HasLocalChartItem)
                        && state.HasHashCopyItem
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    break;
                case "playHistoryContextMenuItemCopyMd5":
                    item.Visibility = state?.CanCopyMd5 == true ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state?.CanCopyMd5 == true;
                    break;
                case "playHistoryContextMenuItemCopySha256":
                    item.Visibility = state?.CanCopySha256 == true ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state?.CanCopySha256 == true;
                    break;
                case "playHistoryContextMenuItemAddDateRangeToSearch":
                    item.Visibility = playHistoryDateSearchSnapshot != null ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = playHistoryDateSearchSnapshot != null;
                    break;
            }
        }
    }

    private void playHistoryContextMenuClosed(object sender, RoutedEventArgs e)
    {
        playHistoryDateSearchSnapshot = null;
        ResetPlayHistoryDateSearchMenuItem(sender as ContextMenu);
    }

    private void playHistoryContextMenuItemCopyHashClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string copyKind })
        {
            return;
        }

        PlayHistoryContextMenuActionKind? actionKind = copyKind switch
        {
            PlayHistoryContextMenuState.CopyMd5Kind => PlayHistoryContextMenuActionKind.CopyMd5,
            PlayHistoryContextMenuState.CopySha256Kind => PlayHistoryContextMenuActionKind.CopySha256,
            _ => null
        };
        if (!actionKind.HasValue
            || !TryGetPlayHistoryContextMenuAction(sender, actionKind.Value, out PlayHistoryContextMenuAction action)
            || string.IsNullOrWhiteSpace(action.Value))
        {
            return;
        }

        Clipboard.SetText(action.Value);
        e.Handled = true;
    }

    private void playHistoryContextMenuItemAddDateRangeToSearchClick(object sender, RoutedEventArgs e)
    {
        if (playHistoryDateSearchSnapshot == null
            || base.DataContext is not MainWindowViewModel viewModel
            || !viewModel.PlayHistory.TryAppendDateSearchSnapshot(
                playHistoryDateSearchSnapshot,
                viewModel.ChartFilters.KeywordFilter,
                out string updatedKeywordFilter))
        {
            return;
        }

        viewModel.ChartFilters.KeywordFilter = updatedKeywordFilter;
        e.Handled = true;
    }

    private void playHistoryContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPlayHistoryContextMenuAction(
                e.Source,
                PlayHistoryContextMenuActionKind.OpenExplorer,
                out PlayHistoryContextMenuAction action)
            || string.IsNullOrWhiteSpace(action.Path)
            || !LongPathFileSystem.FileExists(action.Path))
        {
            return;
        }

        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.ExternalShellGateway.OpenFileAndSelect(action.Path);
        e.Handled = true;
    }

    private void playHistoryContextMenuItemOpenAssociatedClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!TryGetPlayHistoryContextMenuAction(
                e.Source,
                PlayHistoryContextMenuActionKind.OpenAssociated,
                out _)
            || !TryGetContextMenuRow(e.Source, out object row)
            || base.DataContext is not MainWindowViewModel viewModel
            || !viewModel.PlayHistory.TryCreateAssociatedChartOperationTarget(
                row,
                out ChartOperationTarget target)
            || !selectedChartContextMenuTerminals.SelectedChartExternalActions.CanExecute(
                target,
                SelectedChartExternalActionKind.OpenFile))
        {
            return;
        }

        selectedChartContextMenuTerminals.SelectedChartExternalActions.Execute(
            target,
            SelectedChartExternalActionKind.OpenFile);
    }

    private async void playHistoryContextMenuItemRegisterScoreViewerClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPlayHistoryContextMenuAction(
                e.Source,
                PlayHistoryContextMenuActionKind.RegisterScoreViewer,
                out PlayHistoryContextMenuAction action)
            || action.ScoreViewerTarget == null)
        {
            return;
        }

        var targets = new List<ScoreViewerTarget> { action.ScoreViewerTarget };
        e.Handled = true;
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await selectedChartContextMenuTerminals.ScoreViewer.RunAsync(
                targets,
                openSingleViewerOnSuccess: true,
                "playHistoryContextMenuItemRegisterScoreViewerClick");
        }
    }

    private void tableContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row)
            || base.DataContext is not MainWindowViewModel viewModel
            || !GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target))
        {
            return;
        }
        if (selectedChartContextMenuTerminals.SelectedChartExternalActions.CanExecute(
                target,
                SelectedChartExternalActionKind.OpenExplorer))
        {
            selectedChartContextMenuTerminals.SelectedChartExternalActions.Execute(target, SelectedChartExternalActionKind.OpenExplorer);
            e.Handled = true;
        }
    }

    private async void tableContextMenuItemOpenInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || !IsPendingMainViewSection(GetCurrentMainViewOperationSection()))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets.Count == 0)
        {
            return;
        }
        await viewModel.PendingPackages
            .OpenInstallDestinationForChartsAsync(targets)
            .LoggingAndPropagate("tableContextMenuItemOpenInstallDestinationClick");
    }

    private async void treeViewInstallPackageContextMenuOpenInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: ChartPackage dataContext } } })
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await viewModel.PendingPackages
            .OpenInstallDestinationForPackageAsync(dataContext)
            .LoggingAndPropagate("treeViewInstallPackageContextMenuOpenInstallDestinationClick");
    }

    private void tableContextMenuItemOpenBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row)
            || base.DataContext is not MainWindowViewModel viewModel
            || !GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target))
        {
            return;
        }
        if (selectedChartContextMenuTerminals.SelectedChartExternalActions.CanExecute(
                target,
                SelectedChartExternalActionKind.OpenFile))
        {
            selectedChartContextMenuTerminals.SelectedChartExternalActions.Execute(target, SelectedChartExternalActionKind.OpenFile);
            e.Handled = true;
        }
    }

    private async void tableContextMenuItemOpenURLClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_open_url")
            || !TryGetContextMenuRow(e.Source, out object contextRow)
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            await playlistWorkspaceTerminals.UrlAcquisition
                .RunPlaylistUrlActionAsync(GetEffectiveContextMenuRows(contextRow), isDiffUrl: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "tableContextMenuItemOpenURLClick");
        }
    }

    private async void tableContextMenuItemOpenURLdiffClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_open_url_diff")
            || !TryGetContextMenuRow(e.Source, out object contextRow)
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            await playlistWorkspaceTerminals.UrlAcquisition
                .RunPlaylistUrlActionAsync(GetEffectiveContextMenuRows(contextRow), isDiffUrl: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "tableContextMenuItemOpenURLdiffClick");
        }
    }

    private async void tableContextMenuItemFindExternalPackageClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_find_external_package")
            || !TryGetContextMenuRow(e.Source, out object contextRow)
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            await playlistWorkspaceTerminals.UrlAcquisition
                .RunExternalPackageLookupAsync(GetEffectiveContextMenuRows(contextRow));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "tableContextMenuItemFindExternalPackageClick");
        }
    }

    private List<object> GetEffectiveContextMenuRows(object contextRow)
    {
        if (contextRow == null)
        {
            return [];
        }
        List<object> selectedRows = GetSelectedGridRowsSnapshot();
        if (selectedRows.Any(row => ReferenceEquals(row, contextRow)))
        {
            return selectedRows;
        }
        return [contextRow];
    }

    private void tableContextMenuItemOpenDocumentFileClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { DataContext: string dataContext }
            && base.DataContext is MainWindowViewModel viewModel)
        {
            selectedChartContextMenuTerminals.SelectedChartExternalActions.OpenRelatedDocument(dataContext);
            e.Handled = true;
        }
    }

    private void cancelDropInstallQueueClick(object sender, RoutedEventArgs e)
    {
        progressStatusBarTerminals.CancelInstallPipeline();
    }

    private void cancelMaintenanceRescanClick(object sender, RoutedEventArgs e)
    {
        progressStatusBarTerminals.CancelMaintenanceRescan();
    }

    private void retryLr2SongDbSyncClick(object sender, RoutedEventArgs e)
    {
        progressStatusBarTerminals.RetryLr2Sync();
    }

    private void tableContextMenuItemUpdateRankingDataClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets();
        if (targets.Count != 0)
        {
            if (selectedChartContextMenuTerminals.RankingCache.Request(targets))
            {
                e.Handled = true;
            }
        }
    }

    private async void tableContextMenuItemRegisterBMSFileToScoreViwer(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets();
        e.Handled = true;
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await selectedChartContextMenuTerminals.ScoreViewer.RunAsync(
                targets,
                "tableContextMenuItemRegisterBMSFileToScoreViwer");
        }
    }

    private async void tableContextMenuItemForceFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck);
        if (ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request))
        {
            e.Handled = true;
            SelectedChartResourceHealthWorkflowResult result = await selectedChartContextMenuTerminals.SelectedChartResourceHealth.RescanAsync(request);
            if (!result.Succeeded && result.Failure != null)
            {
                Task.FromException(result.Failure).ObserveFault("tableContextMenuItemForceFileScanCheckSelectedCharts");
            }
        }
    }

    private async void tableContextMenuRemoveInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("table_context_menu_remove_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        bool isInstalledLocationRepair = IsFullScanMainViewSection(GetCurrentMainViewOperationSection());
        ChartOperationCapabilities capability = isInstalledLocationRepair
            ? ChartOperationCapabilities.RepairInstalledLocation
            : ChartOperationCapabilities.UpdateInstallDestination;
        RepairInstalledLocationRequest repairRequest = null;
        if (isInstalledLocationRepair)
        {
            List<ChartOperationTarget> targets = GetSelectedChartTargets(capability);
            RepairInstalledLocationRequest.TryCreate(targets, out repairRequest);
        }
        PendingInstallDestinationClearRequest pendingInstallRequest = null;
        if (!isInstalledLocationRepair)
        {
            List<ChartOperationTarget> pendingTargets = GetSelectedChartTargets(capability, isPendingSection: true);
            PendingInstallDestinationClearRequest.TryCreate(pendingTargets, out pendingInstallRequest);
        }
        if ((repairRequest?.HasTargets == true) || (pendingInstallRequest?.HasTargets == true))
        {
            e.Handled = true;
            if (isInstalledLocationRepair)
            {
                await installedLocationRepairTerminal
                    .ClearCorrectAsync(repairRequest)
                    .LoggingAndPropagate("tableContextMenuRemoveInstallDestinationClick");
            }
            else
            {
                await pendingInstallEstimationTerminal
                    .ClearPendingAsync(pendingInstallRequest)
                    .LoggingAndPropagate("tableContextMenuRemoveInstallDestinationClick");
            }
        }
    }

    private async void tableContextMenuSearchCorrectInstallationDirectoryChartsClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("table_context_menu_search_correct_installation_directory"))
        {
            e.Handled = true;
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation);
        if (RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request))
        {
            if (base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            e.Handled = true;
            await installedLocationRepairTerminal
                .SearchCorrectAsync(request)
                .LoggingAndPropagate("tableContextMenuSearchCorrectInstallationDirectoryChartsClick");
        }
    }

    private async void tableContextMenuFixInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_fix_installation_directory"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation);
        if (!RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await installedLocationRepairTerminal
            .FixInstalledLocationsAsync(request)
            .LoggingAndPropagate("tableContextMenuFixInstallationDirectoryClick");
    }

    private async void tableContextMenuItemDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        try
        {
            await playlistWorkspaceTerminals.EntryRemoval
                .DeleteSelectedEntriesAsync(GetSelectedGridRowsSnapshot());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "tableContextMenuItemDeleteEntryClick");
        }
    }

    private async void tableContextMenuItemForceFileScanCheckAllCharts(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_full_scan_all_charts"))
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        if (base.DataContext is not MainWindowViewModel viewModel
            || viewModel.MaintenanceRescanWorkflow == null)
        {
            return;
        }
        MaintenanceRescanStartResult result = await maintenanceRescanTerminal.RequestStartAsync();
        if (result.Status == MaintenanceRescanStartStatus.Failed && result.Failure != null)
        {
            Task.FromException(result.Failure).ObserveFault("tableContextMenuItemForceFileScanCheckAllCharts");
        }
    }

    private async void tableContextMenuItemRemoveChartInfoParseFailureClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _) || !IsChartInfoParseErrorMainViewSection(GetCurrentMainViewOperationSection()))
        {
            return;
        }
        List<string> md5s = GetSelectedChartInfoParseFailureMd5s();
        if (base.DataContext is not MainWindowViewModel viewModel || md5s.Count == 0)
        {
            return;
        }
        ChartInfoParseFailureRemovalOperation operation = selectedChartContextMenuTerminals.ChartInfoParseFailureRemoval.BeginRemove(
            new ChartInfoParseFailureRemovalRequest(md5s));
        ChartInfoParseFailureRemovalAcceptance acceptance = await operation.Acceptance;
        if (acceptance.Accepted)
        {
            e.Handled = true;
        }
        ChartInfoParseFailureRemovalResult result = await operation.Completion;
        if (result.Status == ChartInfoParseFailureRemovalStatus.Failed && result.Failure != null)
        {
            Task.FromException(result.Failure).ObserveFault("tableContextMenuItemRemoveChartInfoParseFailureClick");
        }
    }

    private void tableContextMenuItemAutoRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_auto_rename_folder"))
        {
            e.Handled = true;
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.MoveInLibrary);
        e.Handled = true;
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        folderAutoRenameTerminal.StartSelected(targets);
    }

    private async void tableContextMenuItemRenameBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_rename_invalid_extension"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        SelectedChartMutationResult result = await selectedChartContextMenuTerminals.SelectedChartMutation.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest(
                GetSelectedChartTargets(ChartOperationCapabilities.RenameInvalidExtension),
                IsPendingMainViewSection(GetCurrentMainViewOperationSection())));
        if (!result.Succeeded && result.Failure != null)
        {
            Task.FromException(result.Failure).ObserveFault("tableContextMenuItemRenameBMSFileClick");
        }
    }

    private async void tableContextMenuItemRemoveBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_remove_chart"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        MainViewOperationSection section = GetCurrentMainViewOperationSection();
        List<ChartOperationTarget> selectedTargets = GetSelectedChartTargets(IsPendingMainViewSection(section));
        TryGetContextMenuChartTarget(sender, e.Source, out ChartOperationTarget contextTarget);
        e.Handled = true;
        SelectedChartMutationResult result = await selectedChartContextMenuTerminals.SelectedChartMutation.DeleteAsync(
            new SelectedChartDeleteRequest(selectedTargets, contextTarget, section));
        if (!result.Succeeded && result.Failure != null)
        {
            Task.FromException(result.Failure).ObserveFault("tableContextMenuItemRemoveBMSFileClick");
        }
    }

    private async void tableContextMenuItemMoveFileClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_move_chart"))
        {
            e.Handled = true;
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.MoveInLibrary);
        if (sender is not MenuItem menuItem)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel
            || menuItem.DataContext is not string dstDir)
        {
            return;
        }
        e.Handled = true;
        SelectedChartMutationResult result = await selectedChartContextMenuTerminals.SelectedChartMutation.MoveAsync(
            new SelectedChartMoveRequest(targets, dstDir));
        if (!result.Succeeded && result.Failure != null)
        {
            Task.FromException(result.Failure).ObserveFault("tableContextMenuItemMoveFileClick");
        }
    }

    private void fixEncodingSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem menuItem && TryGetContextMenuRow(e.Source, out _))
        {
            SelectedChartEncodingRequest request = new(
                GetSelectedChartTargets(ChartOperationCapabilities.RunBmsEncodingFix),
                menuItem.Tag.ToString());
            if (!request.HasTargets || base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            SelectedChartMutationResult result = selectedChartContextMenuTerminals.SelectedChartMutation.ApplyEncoding(request);
            if (result.Succeeded)
            {
                e.Handled = true;
            }
            else if (result.Failure != null)
            {
                Task.FromException(result.Failure).ObserveFault("fixEncodingSelectedBMS");
            }
        }
    }

    private void ignoreFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out _))
        {
            if (base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck);
            if (ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request))
            {
                e.Handled = true;
                SelectedChartResourceHealthWorkflowResult result = selectedChartContextMenuTerminals.SelectedChartResourceHealth.SetWarningsIgnored(request);
                if (!result.Succeeded && result.Failure != null)
                {
                    Task.FromException(result.Failure).ObserveFault("ignoreFileScanCheckSelectedCharts");
                }
            }
        }
    }

    private void notIgnoredFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out _))
        {
            if (base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck);
            if (ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request))
            {
                e.Handled = true;
                SelectedChartResourceHealthWorkflowResult result = selectedChartContextMenuTerminals.SelectedChartResourceHealth.SetWarningsIgnored(request, unset: true);
                if (!result.Succeeded && result.Failure != null)
                {
                    Task.FromException(result.Failure).ObserveFault("notIgnoredFileScanCheckSelectedCharts");
                }
            }
        }
    }

    private async void forceInstallSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await ForceInstallSelectedPendingChartsAsync(e);
    }

    private async Task ForceInstallSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_force_install_pending"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallPackageOperationRequest request = PendingInstallPackageOperationRequest.CreateForceInstall(targets);
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        PackageCatalogSelectionPlan selectionPlan = treeViewItemInstallPending.IsSelected
            ? PackageCatalogSelectionPlan.None
            : CaptureNextSiblingOrRoot(
                treeViewItemInstallPending,
                treeView.SelectedItem,
                "forceInstallSelectedPendingCharts");
        PendingPackageMutationResult result = await ObservePendingPackageMutationAsync(
            pendingInstallationTerminal.InstallPendingAsync(request),
            "forceInstallSelectedPendingCharts");
        await pendingPackageMutationViewTerminal.ApplyAsync(
            result,
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            "forceInstallSelectedPendingCharts",
            () =>
            {
                ClearMainGridSelection();
                selectionPlan.Apply(this);
            });
    }

    private async void manualInstallSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await ManualInstallSelectedPendingChartsAsync(e);
    }

    private async Task ManualInstallSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_manual_install_pending"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallPackageOperationRequest request = PendingInstallPackageOperationRequest.CreateManualInstall(targets);
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        PackageCatalogSelectionPlan selectionPlan = treeViewItemInstallPending.IsSelected
            ? PackageCatalogSelectionPlan.None
            : CaptureNextSiblingOrRoot(
                treeViewItemInstallPending,
                treeView.SelectedItem,
                "manualInstallSelectedPendingCharts");
        PendingPackageMutationResult result = await ObservePendingPackageMutationAsync(
            pendingInstallationTerminal.InstallPendingAsync(request),
            "manualInstallSelectedPendingCharts");
        await pendingPackageMutationViewTerminal.ApplyAsync(
            result,
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            "manualInstallSelectedPendingCharts",
            () =>
            {
                ClearMainGridSelection();
                selectionPlan.Apply(this);
            });
    }

    private async void searchInstallDestinationSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await SearchInstallDestinationSelectedPendingChartsAsync(e);
    }

    private async Task SearchInstallDestinationSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_search_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallDestinationSearchRequest request = PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch(targets);
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await pendingInstallEstimationTerminal
            .SearchPendingAsync(request);
    }

    private async void tableContextMenuItemDeleteInstallPackagesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RemovePackageCatalogSelectionFromContextMenuAsync(e);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "tableContextMenuItemDeleteInstallPackagesClick");
        }
    }

    private async Task RemovePackageCatalogSelectionFromContextMenuAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_delete_install_package_records"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        MainViewOperationSection section = GetCurrentMainViewOperationSection();
        bool isPendingSelected = IsPendingMainViewSection(section);
        bool isInstalledSelected = IsInstalledMainViewSection(section);
        if (!isPendingSelected && !isInstalledSelected)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!TryCreatePackageCatalogRemovalRequest(isPendingSelected, isInstalledSelected, out PackageCatalogRemovalRequest request))
        {
            return;
        }
        PackageCatalogSelectionPlan selectionPlan = request.IsPending
            ? CaptureNextSiblingOrRoot(
                treeViewItemInstallPending,
                treeView.SelectedItem,
                "tableContextMenuItemDeleteInstallPackagesClick")
            : CaptureNextSiblingOrRoot(
                newlyInstalledTreeViewItem,
                treeView.SelectedItem,
                "tableContextMenuItemDeleteInstallPackagesClick");
        Task<PackageCatalogMutationResult> operation = packageCatalogTerminal.RemoveSelectionAsync(request);
        if (operation.Status == TaskStatus.RanToCompletion)
        {
            PackageCatalogMutationResult immediateResult = operation.GetAwaiter().GetResult();
            if (!immediateResult.Succeeded && !immediateResult.ShouldApplyView)
            {
                await ObservePackageCatalogMutationAsync(
                    operation,
                    "tableContextMenuItemDeleteInstallPackagesClick");
                return;
            }
        }
        e.Handled = true;
        PackageCatalogMutationResult result = await ObservePackageCatalogMutationAsync(
            operation,
            "tableContextMenuItemDeleteInstallPackagesClick");
        await ApplyPackageCatalogMutationViewAsync(
            viewModel,
            result,
            selectionPlan,
            request.IsPending ? treeViewItemInstallPending : newlyInstalledTreeViewItem,
            request.Section,
            request.IsPending
                ? MainViewUpdateMode.PendingInstallFolderSelected
                : MainViewUpdateMode.NewlyInstalledFolderSelected,
            "tableContextMenuItemDeleteInstallPackagesClick",
            ClearMainGridSelection);
    }

    private bool TryCreatePackageCatalogRemovalRequest(bool isPendingSelected, bool isInstalledSelected, out PackageCatalogRemovalRequest request)
    {
        request = null;
        if (isPendingSelected)
        {
            List<ChartOperationTarget> selectedPendingTargets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
            if (selectedPendingTargets.Count == 0)
            {
                return false;
            }

            request = PackageCatalogRemovalRequest.CreatePending(selectedPendingTargets);
            return true;
        }
        if (isInstalledSelected)
        {
            List<ChartOperationTarget> selectedInstalledTargets = GetSelectedChartTargets(ChartOperationCapabilities.None);
            if (selectedInstalledTargets.Count == 0)
            {
                return false;
            }

            request = PackageCatalogRemovalRequest.CreateInstalled(selectedInstalledTargets);
            return true;
        }

        return false;
    }

    private async void searchMergeDestinationSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await SearchMergeDestinationSelectedPendingChartsAsync(e);
    }

    private async Task SearchMergeDestinationSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_search_merge_destination"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallDestinationSearchRequest request = PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch(targets);
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await pendingInstallEstimationTerminal
            .SearchPendingAsync(request)
            .LoggingAndPropagate("searchMergeDestinationSelectedPendingCharts");
    }

    private async void tableContextMenuItemConvertToAudioFileClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _)
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        SelectedChartAudioConversionRequest request = new(
            GetSelectedChartTargets(ChartOperationCapabilities.ConvertToAudio));
        if (!request.HasTargets)
        {
            return;
        }
        e.Handled = true;
        await selectedChartContextMenuTerminals.SelectedChartAudioConversion.RunAsync(request);
    }

    private async void playlistTableDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        treeViewItemInstantStoryBoardPlaylistTable.Stop(this);
        treeViewItemInstantStoryBoardPlaylistTable.Children.Clear();
        var viewModel = base.DataContext as MainWindowViewModel;
        if (!IsPlaylistDropCandidateDrag(e.Data, out List<object> selectedRows))
        {
            return;
        }
        if (sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        if (viewModel == null || treeViewItem.DataContext is not BMSTable table || table.is_external_sync)
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem2 == null)
        {
            return;
        }
        treeViewItem2.Background = Brushes.Transparent;
        PlaylistFolderNode targetFolder;
        if (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out PlaylistFolderNode folderNode))
        {
            targetFolder = null;
        }
        else
        {
            if (folderNode.IsSpecial)
            {
                return;
            }
            targetFolder = folderNode;
        }
        if (!viewModel.PlaylistWorkspace.CanAcceptDrop(selectedRows, table, targetFolder))
        {
            return;
        }
        e.Effects = DragDropEffects.Copy;
        try
        {
            await viewModel.PlaylistWorkspace
                .AddRowsToFolderAsync(selectedRows, table, targetFolder);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Task.FromException(exception).ObserveFault("playlistTableDrop");
        }
    }

    private void playlistTableDragOver(object sender, DragEventArgs e)
    {
        if (!IsPlaylistDropCandidateDrag(e.Data, out List<object> selectedRows))
        {
            return;
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (sender is not TreeViewItem { DataContext: BMSTable table })
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        PlaylistFolderNode targetFolder = null;
        if (treeViewItem2 != null
            && (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out targetFolder) || !targetFolder.IsSpecial)
            && (base.DataContext as MainWindowViewModel)?.PlaylistWorkspace.CanAcceptDrop(selectedRows, table, targetFolder) == true)
        {
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void playlistTableDragEnter(object sender, DragEventArgs e)
    {
        if (!IsPlaylistDropCandidateDrag(e.Data, out List<object> selectedRows))
        {
            return;
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (sender is not TreeViewItem tviTable)
        {
            return;
        }
        TreeViewItem treeViewItem = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem == null || tviTable.DataContext is not BMSTable bMSTable)
        {
            return;
        }
        PlaylistFolderNode targetFolder = null;
        if ((!TryGetPlaylistFolderNode(treeViewItem.DataContext, out targetFolder) || !targetFolder.IsSpecial)
            && (base.DataContext as MainWindowViewModel)?.PlaylistWorkspace.CanAcceptDrop(selectedRows, bMSTable, targetFolder) == true)
        {
            e.Effects = DragDropEffects.Copy;
            treeViewItem.Background = SystemColors.HighlightBrush;
        }
        if (!TryGetPlaylistFolderNode(treeViewItem.DataContext, out _))
        {
            var booleanAnimationUsingKeyFrames = new BooleanAnimationUsingKeyFrames();
            Storyboard.SetTargetProperty(booleanAnimationUsingKeyFrames, new PropertyPath(TreeViewItem.IsExpandedProperty));
            Storyboard.SetTarget(booleanAnimationUsingKeyFrames, tviTable);
            booleanAnimationUsingKeyFrames.KeyFrames.Add(new DiscreteBooleanKeyFrame(!tviTable.IsExpanded, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.0))));
            booleanAnimationUsingKeyFrames.FillBehavior = FillBehavior.Stop;
            booleanAnimationUsingKeyFrames.Completed += delegate
            {
                tviTable.IsExpanded = !tviTable.IsExpanded;
            };
            treeViewItemInstantStoryBoardPlaylistTable.Children.Add(booleanAnimationUsingKeyFrames);
            treeViewItemInstantStoryBoardPlaylistTable.Begin(this, isControllable: true);
        }
    }

    private static bool IsPlaylistDropCandidateDrag(IDataObject dataObject, out List<object> selectedRows)
    {
        selectedRows = null;
        return CustomTableDataTransfer.HasRowDragKind(dataObject, CustomTableRowDragKind.PlaylistDropCandidateRows)
            && CustomTableDataTransfer.TryGetSelectedRows(dataObject, out selectedRows);
    }

    private void playlistTableDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem2 != null && treeViewItem.DataContext is BMSTable bMSTable)
        {
            if (!bMSTable.is_external_sync && (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsSpecial))
            {
                treeViewItem2.Background = Brushes.Transparent;
            }
            if (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out _))
            {
                treeViewItemInstantStoryBoardPlaylistTable.Stop(this);
                treeViewItemInstantStoryBoardPlaylistTable.Children.Clear();
            }
        }
    }

    private void RestorePlaybackSurfaceAndFocusTable()
    {
        if (IsShellClosingOrClosed()) return;
        playbackPanelView.RestoreSelectedSurface();
        IntPtr handle;
        try { handle = new WindowInteropHelper(this).Handle; }
        catch { return; }
        if (handle != Win32API.GetForegroundWindow()) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)async delegate
        {
            if (IsShellClosingOrClosed()) return;
            for (int i = 1; i <= 10; i++)
            {
                if (IsShellClosingOrClosed()) break;
                NLogWrapper.DebuggerLogger?.Trace("try to set focus on custom table");
                IntPtr currentHandle;
                try { currentHandle = new WindowInteropHelper(this).Handle; }
                catch { break; }
                if (currentHandle != Win32API.GetForegroundWindow()) break;
                Keyboard.Focus(customTableView);
                await Task.Delay(100);
            }
        });
    }

    private void dialogIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement overlayDialog && (bool)e.NewValue)
        {
            activeOverlayDialog = overlayDialog;
            if (HidesPlaybackSurface(overlayDialog)) PlaybackOverlayVisibility = Visibility.Visible;
        }
        if (!(bool)e.NewValue && (bool)e.OldValue)
        {
            if (ReferenceEquals(activeOverlayDialog, sender))
            {
                activeOverlayDialog = null;
                if (sender is FrameworkElement hiddenDialog && HidesPlaybackSurface(hiddenDialog)) PlaybackOverlayVisibility = Visibility.Collapsed;
            }
            playbackPanelView.RestoreSelectedSurface();
        }
    }

    private TreeViewItem _lastSelectedTreeViewItem;
    private bool _isCrossTreeDeselecting = false;

    private void gridTreePane_TreeViewItemSelected(object sender, RoutedEventArgs e)
    {
        TreeViewItem tvi = e.OriginalSource as TreeViewItem ?? e.Source as TreeViewItem;
        if (tvi != null && tvi.IsSelected)
        {
            if (_lastSelectedTreeViewItem != null && _lastSelectedTreeViewItem != tvi)
            {
                _isCrossTreeDeselecting = true;
                _lastSelectedTreeViewItem.IsSelected = false;
                _isCrossTreeDeselecting = false;
            }
            _lastSelectedTreeViewItem = tvi;
            TreeSelectionSection previousSection = _currentTreeSelectionSection;
            _currentTreeSelectionSection = ResolveTreeSelectionSection(tvi);
            if (previousSection != _currentTreeSelectionSection)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Info("tree_selection_section_changed section=" + _currentTreeSelectionSection + " header=" + tvi.Header);
            }
        }
    }

    private void treeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is TreeView treeView)
        {
            e.Handled = true;
            if (e.NewValue == null && !_isCrossTreeDeselecting)
            {
                _currentTreeSelectionSection = TreeSelectionSection.None;
            }
            var viewModel = base.DataContext as MainWindowViewModel;
            bool isManualInteraction = treeView.IsKeyboardFocusWithin || treeView.IsMouseOver;
            if (!_isCrossTreeDeselecting && e.NewValue == null && e.OldValue != null && e.OldValue is BMSTable
                && (viewModel?.PlaylistWorkspace?.IsPlaylistUpdating ?? false) && !isManualInteraction)
            {
                if (!treeView.SelectTreeViewItemSearchedByDataContext(e.OldValue))
                {
                    var table = (BMSTable)e.OldValue;
                    bool restoredByHeader = treeView.SelectTreeViewItemSearchedByHeader(table.name);
                    NLogWrapper.FileLogger?.Info("playlist_selection_restore fallback_by_header=" + restoredByHeader + " table=" + table.name);
                }
            }
        }
    }

    private TreeSelectionSection ResolveTreeSelectionSection(TreeViewItem selectedTreeViewItem)
    {
        if (selectedTreeViewItem == null)
        {
            return TreeSelectionSection.None;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemInstallPending))
        {
            return TreeSelectionSection.InstallPending;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, newlyInstalledTreeViewItem))
        {
            return TreeSelectionSection.InstallInstalled;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemPlaylist))
        {
            return TreeSelectionSection.Playlist;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemPlayHistory))
        {
            return TreeSelectionSection.PlayHistory;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemFullScanCheck))
        {
            return TreeSelectionSection.FullScanCheck;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemChartInfoParseError))
        {
            return TreeSelectionSection.ChartInfoParseError;
        }
        return TreeSelectionSection.Other;
    }

    private static bool IsSameOrDescendantOf(TreeViewItem targetTreeViewItem, TreeViewItem ancestorTreeViewItem)
    {
        if (targetTreeViewItem == null || ancestorTreeViewItem == null)
        {
            return false;
        }
        DependencyObject current = targetTreeViewItem;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestorTreeViewItem))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }




}
