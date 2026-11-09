using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>実保存と局所入口を接続し、必要な差分継続の終端を限定して観測します。</summary>
internal sealed class SettingsDialogHarness : IDisposable
{
    private SettingsDialogHarness(SettingsDialogViewModel dialog, RecordingSettingsStatePort state,
        RecordingWorkspacePort workspace, RecordingCustomFolderOutputPort output)
    {
        Dialog = dialog;
        State = state;
        Workspace = workspace;
        Output = output;
    }

    /// <summary>判断と通知を検証する実設定VMです。</summary>
    internal SettingsDialogViewModel Dialog { get; }
    /// <summary>設定から開始される初期化・再読込みの入力と終端を観測します。</summary>
    internal RecordingSettingsStatePort State { get; }
    /// <summary>プレイリストへの要求と既存受付を局所的に観測します。</summary>
    internal RecordingWorkspacePort Workspace { get; }
    /// <summary>出力設定の確定・再生成要求を局所的に観測します。</summary>
    internal RecordingCustomFolderOutputPort Output { get; }

    /// <summary>局所設定の実VMへ指定したportsを接続します。音声専用依存はcomposition未指定の構成で使います。</summary>
    /// <remarks>
    /// <paramref name="dialogs"/> を省略すると既定の <see cref="UndeclaredDialogs"/> の窓口になり、未宣言の要求は項目 <c>D</c> として失敗になります。
    /// <paramref name="composition"/> を渡すと <paramref name="dialogs"/> は使わず、構成側の窓口を使います。
    /// </remarks>
    internal static SettingsDialogHarness Create(Settings values, bool activeLibraryProfile = false,
        Action<Exception>? reportApplyFailure = null, Func<Task>? reloadFileDiff = null,
        ApplicationComposition? composition = null, ISettingsDialogPlayerFactoryPort? playerFactory = null,
        ISettingsDialogPlaybackRuntimePort? playbackRuntime = null, IUiDialogService? dialogs = null,
        Func<OperationModeRestartRequest, Task<bool>>? restart = null, IAudioDeviceCatalog? audioDeviceCatalog = null,
        IReadOnlyList<PlaylistTablePresentationSnapshot>? playlistSnapshots = null,
        ISettingsEditSession? settingsEditSession = null,
        IAudioSettingsGateway? audioSettingsGateway = null,
        AudioDeviceTestWorkflowOwner? audioDeviceTestWorkflow = null)
    {
        var databaseScope = new ApplicationDatabaseScope();
        var state = new RecordingSettingsStatePort(activeLibraryProfile) { ReloadFileDiffHandler = reloadFileDiff };
        var workspace = new RecordingWorkspacePort { Snapshots = playlistSnapshots ?? [] };
        var output = new RecordingCustomFolderOutputPort(() => composition?.CustomFolderOutputSettingsProvider()
            ?? CustomFolderOutputSettingsSnapshot.CreateCurrent(values));
        var history = new RecordingPlayHistoryPort();
        var roots = new RecordingSearchRootRuntimePort();
        ISettingsDialogPlayerFactoryPort selectedFactory = playerFactory ?? new TestSettingsDialogPlayerFactoryPort();
        ISettingsDialogPlaybackRuntimePort playback = playbackRuntime ?? new TestSettingsDialogPlaybackRuntimePort();
        var sync = new Lr2SongDbSyncWorkflowOwner(new NoOpLr2SongDbSyncRuntime());
        Task<bool> UnexpectedRestart(OperationModeRestartRequest _) => throw new AssertFailedException("局所設定の確認から環境再起動しません。");
        IUiDialogService settingsDialogs = dialogs ?? UndeclaredDialogs.Create();
        SettingsDialogViewModel dialog = composition != null
            ? composition.CreateSettingDialogViewModel(state, workspace, output, history, roots, selectedFactory, playback, sync, restart ?? UnexpectedRestart)
            : new SettingsDialogViewModel(databaseScope, state, workspace, output, history, roots, selectedFactory, playback, sync,
                settingsEditSession ?? new SettingsEditSession(values), new ApplicationDataUninstallWorkflowOwner(settingsDialogs, new Lr2ApplicationDataUninstallStore(databaseScope)), applicationLifetime: TestApplicationContext.CreateLifetime(),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(), schemaDialogs: settingsDialogs,
                reportApplyFailure: reportApplyFailure, externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current, audioDeviceCatalog: audioDeviceCatalog ?? new TestAudioDeviceCatalog(),
                audioSettingsGateway: audioSettingsGateway ?? new TestAudioSettingsGateway(), requestOperationModeRestart: restart ?? UnexpectedRestart,
                audioDeviceTestWorkflow: audioDeviceTestWorkflow ?? AudioDeviceTestWorkflowTestFactory.Create());
        return new SettingsDialogHarness(dialog, state, workspace, output);
    }

    /// <summary>実設定VMを破棄して共有リソースと音声通知の購読を解除します。</summary>
    public void Dispose() => Dialog.Dispose();

    /// <summary>設定確定後の初期化・再読込みと必須完了の境界を制御します。</summary>
    internal sealed class RecordingSettingsStatePort : ISettingsDialogStatePort
    {

        internal RecordingSettingsStatePort(bool hasActiveLibraryProfile)
        {
            this.hasActiveLibraryProfile = hasActiveLibraryProfile;
        }

        public bool HasActiveLibraryProfile
        {
            get
            {
                return hasActiveLibraryProfile;
            }
        }

        private bool isLibraryOperationInProgress;

        public bool IsLibraryOperationInProgress
        {
            get
            {
                return isLibraryOperationInProgress;
            }
            set => isLibraryOperationInProgress = value;
        }

        private readonly bool hasActiveLibraryProfile;

        internal int InitializeCount { get; private set; }

        internal int ScoreReloadCount { get; private set; }

        internal int FileDiffReloadCount { get; private set; }

        internal bool ThrowOnScoreReload { get; set; }

        internal Func<Task>? ReloadFileDiffHandler { get; set; }
        internal Func<Task>? ScoreReloadHandler { get; set; }
        internal Func<StartupInitializationResult, Task<StartupInitializationResult>>? CompletionHandler { get; set; }

        internal FileDiffReloadWorkflowOwner? ReloadFileDiffWorkflowOwner { get; set; }


        public Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result)
            => CompletionHandler?.Invoke(result) ?? Task.FromResult(result with { CompletionPublished = result.Outcome == StartupInitializationOutcome.Succeeded });


        public Task<LeapYearFolderRepairApproval?> PrepareLibraryInitializationAsync() => Task.FromResult<LeapYearFolderRepairApproval?>(null);

        public Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification? notification) => Task.CompletedTask;

        public Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability? capability = null,
            LeapYearFolderRepairApproval? leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification>? repairNotificationObserver = null, bool forceBeatorajaBmtProjection = false)
        {
            InitializeCount++;
            return Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded));
        }

        public async Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability)
        {
            ScoreReloadCount++;
            if (ThrowOnScoreReload)
            {
                throw new InvalidOperationException("score reload failed");
            }

            await (ScoreReloadHandler?.Invoke() ?? Task.CompletedTask);
            return new(StartupInitializationOutcome.Succeeded);
        }

        public Task PresentLibraryDirectoryWarningAsync(BeMusicSeeker.Models.BmsLibraryInternal.LibraryDirectoryPreflightException failure) => Task.CompletedTask;

        public void ReevaluatePendingPackagesForSearchRoots(LibraryFileMutationCapability capability, out Action publication) { publication = () => { }; }

        /// <summary>設定が直近の差分要求へ渡した最終実検索範囲。設定側の捕捉と転送を確認します。</summary>
        internal Lr2SearchRootSnapshot? LastSearchRoots { get; private set; }

        public async Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null, Lr2SearchRootSnapshot? searchRoots = null)
        {
            FileDiffReloadCount++;
            LastSearchRoots = searchRoots;
            if (ReloadFileDiffWorkflowOwner != null)
            {
                await ReloadFileDiffWorkflowOwner.ReloadAsync(
                    new FileDiffReloadRequest("SettingsDialog.SearchRoot", 0L, searchRoots: searchRoots));
                return new(StartupInitializationOutcome.Succeeded);
            }
            await (ReloadFileDiffHandler?.Invoke() ?? Task.CompletedTask);
            return new(StartupInitializationOutcome.Succeeded);
        }

#pragma warning disable CS0067 // インターフェイスのイベント面を満たすが、このテストダブルでは発火させない。
        public event EventHandler? LibraryOperationAvailabilityChanged;

        public event Action<Lr2PlayHistorySchemaStatusSnapshot>? Lr2PlayHistorySchemaStatusChanged;
#pragma warning restore CS0067
    }

    /// <summary>設定の局所判断と実ページの代表フローで、通知と確認の到達と完了を記録します。</summary>
    /// <remarks>
    /// 通知・確認だけを数えて結果を返します。ウィンドウ・ピッカー・進捗は宣言された期待がないため、
    /// <see cref="UndeclaredDialogs"/> と同じ窓口へ渡して共有ホストの観測記録の項目 <c>D</c> に残し、ケースの失敗にします。
    /// </remarks>
    internal sealed class RecordingDialogService : IUiDialogService
    {
        private readonly IUiDialogService undeclared = UndeclaredDialogs.Create();

        internal int MessageCount { get; private set; }

        internal int ConfirmationCount { get; private set; }

        internal IList<string>? Sequence { get; set; }

        internal Func<Task>? ConfirmationObservedAsync { get; set; }

        internal UiDialogResult MessageResult { get; set; }
            = UiDialogResult.Failed(new InvalidOperationException("unexpected settings behavior message"));

        internal string? LastMessageText { get; private set; }

        /// <summary>直近の通知の見出しです。</summary>
        internal string? LastMessageCaption { get; private set; }

        internal UiDialogResult ConfirmationResult { get; set; }
            = UiDialogResult.Failed(new InvalidOperationException("unexpected settings behavior confirmation"));

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCount++;
            LastMessageText = request.MessageBoxText;
            LastMessageCaption = request.Caption;
            Sequence?.Add("notify");
            return Task.FromResult(MessageResult);
        }

        public async Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
            Sequence?.Add("confirm");
            if (ConfirmationObservedAsync != null) { await ConfirmationObservedAsync(); }
            return ConfirmationResult;
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
            => undeclared.ShowWindowAsync(request, cancellationToken);

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
            => undeclared.PickFileAsync(request, cancellationToken);

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
            => undeclared.PickFolderAsync(request, cancellationToken);

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
            => undeclared.PickSaveFileAsync(request, cancellationToken);

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default)
            => undeclared.RunWithProgressAsync(request, operation, cancellationToken);
    }

    /// <summary>実encoder探索を行わず、外部encoder欠損とWAVEの利用可能性だけを固定します。</summary>
    internal sealed class WaveOnlyAudioDeviceCatalog : IAudioDeviceCatalog
    {
        public void Refresh() { }
        public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDriver driver) => [];
        public bool IsEncoderAvailable(EncoderType encoder, string encoderDirectory) => encoder == EncoderType.WAVE;
    }

    /// <summary>実操作の受付を保ち、workspaceへの要求と終端を記録します。</summary>
    internal sealed class RecordingWorkspacePort : ISettingsDialogWorkspacePort
    {
        public Task PublishOutputSettingsHeadersAsync(CustomFolderOutputSettingsSnapshot previous) => Task.CompletedTask;

        internal IReadOnlyList<PlaylistTablePresentationSnapshot> Snapshots { get; init; } = [];

        internal bool ThrowOnBmt { get; set; }
        internal Exception? CompletionFailure { get; set; }

        internal List<(BeatorajaBmtOptionsSnapshot Previous, BeatorajaBmtOptionsSnapshot Target)> BmtRequests { get; } = [];

        public bool HasPlaylistTables
        {
            get
            {
                return BeforeOperationAsync != null || Snapshots.Count != 0;
            }
        }

        public long PlaylistCatalogVersion
        {
            get
            {
                return 0;
            }
        }

        public IReadOnlyList<PlaylistTablePresentationSnapshot> CapturePlaylistPresentationSnapshots()
        {
            return Snapshots;
        }

        public bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string beatorajaRootPath)
        {
            return false;
        }

        public void SchedulePlaylistUrlCompletionRefresh(string reason)
        {
        }

        public Task<BeatorajaBmtPreparation> PrepareBeatorajaBmtSettingsAsync(BeatorajaBmtOptionsSnapshot previous, BeatorajaBmtOptionsSnapshot target, LibraryFileMutationCapability capability)
        {
            BmtRequests.Add((previous, target));
            return Task.FromResult(new BeatorajaBmtPreparation(previous, target, [], []));
        }

        public Task CompleteBeatorajaBmtSettingsAsync(BeatorajaBmtPreparation preparation, LibraryFileMutationCapability capability)
        {
            if (CompletionFailure != null) { throw CompletionFailure; }
            if (ThrowOnBmt) { throw new IOException("BMT failed"); }
            return Task.CompletedTask;
        }

        internal Func<Task>? BeforeOperationAsync { get; set; }

        public async Task RunWithPlaylistOperationNotificationsAsync(Func<Task> operation, string operationName)
        {
            if (BeforeOperationAsync != null) { await BeforeOperationAsync(); }
            await operation();
        }

#pragma warning disable CS0067 // インターフェイスのイベント面を満たすが、このテストダブルでは発火させない。
        public event EventHandler<PlaylistCatalogChangedEventArgs>? PlaylistCatalogChanged;
#pragma warning restore CS0067
    }

    /// <summary>出力の実受付と設定snapshotを提供し、backend呼出しを記録します。</summary>
    internal sealed class RecordingCustomFolderOutputPort : ISettingsDialogCustomFolderOutputPort
    {
        internal bool BackendCalled { get; private set; }


        public IReadOnlyList<PlaylistCustomFolderOutputMaintenanceOwner.PreviousOutputLocation> CommitCustomFolderOutputSettings(
            CustomFolderOutputSettingsSnapshot previous, CustomFolderOutputSettingsSnapshot target,
            IReadOnlyDictionary<string, string> renames, LibraryFileMutationCapability capability)
        { BackendCalled = true; return []; }

        public void RegenerateCustomFolderOutputs(IReadOnlyList<PlaylistCustomFolderOutputMaintenanceOwner.PreviousOutputLocation> previousLocations,
            CustomFolderOutputSettingsSnapshot settings, LibraryFileMutationCapability capability)
        { BackendCalled = true; }

        private readonly ChartFileOperationSynchronizer outputAdmission = new();

        public bool CanBeginOutputOperation => outputAdmission.CanEnter;
        public event EventHandler? OutputOperationAvailabilityChanged { add => outputAdmission.AvailabilityChanged += value; remove => outputAdmission.AvailabilityChanged -= value; }

        public LibraryFileMutationLease? TryBeginOutputOperation()
            => outputAdmission.TryEnter(out IDisposable lease) ? (LibraryFileMutationLease)lease : null;

        private readonly Func<CustomFolderOutputSettingsSnapshot> settingsProvider;

        internal RecordingCustomFolderOutputPort(
            Func<CustomFolderOutputSettingsSnapshot> settingsProvider)
        {
            this.settingsProvider = settingsProvider;
        }

        public CustomFolderOutputSettingsSnapshot CustomFolderOutputSettings
        {
            get
            {
                return settingsProvider();
            }
        }





        public void ValidateCustomFolderOutputRegistration(CustomFolderOutputSettingsSnapshot settings,
            IReadOnlyDictionary<string, string> renames)
        { BackendCalled = true; }

        public IReadOnlyList<string> PrepareCustomFolderOutputSearchRoots(string previousRootOutputBaseDirectory,
            CustomFolderOutputSettingsSnapshot settings, IReadOnlyList<string> currentDirectories, IReadOnlyDictionary<string, string>? renames = null)
        { BackendCalled = true; return currentDirectories; }


    }

    private sealed class RecordingPlayHistoryPort : ISettingsDialogPlayHistoryPort
    {

        public void RefreshDisplayTargetSetsFromSettings(string serializedDisplayTargetSets, bool queueRefreshWhenSelectionChanges, string? previousName = null, string? confirmedName = null)
        {
        }
    }

    private sealed class RecordingSearchRootRuntimePort : ISettingsDialogSearchRootRuntimePort
    {
        public bool HasOwnedChartUnderRealPath(string directoryPath) => false;
    }

    private sealed class NoOpLr2SongDbSyncRuntime : ILr2SongDbSyncWorkflowRuntime
    {

        internal bool IsLr2ModeEnabledValue { get; set; }

        internal bool IsLibraryAvailableValue { get; set; } = true;

        internal int QueueCount { get; private set; }

        internal Exception? QueueFailure { get; set; }

        internal Action? QueueObserved { get; set; }

        public bool IsLr2ModeEnabled => IsLr2ModeEnabledValue;

        public bool IsLibraryAvailable => IsLibraryAvailableValue;

        public async Task<bool> QueueAsync(
            string reason,
            bool force,
            bool prepareGeneratedData = false,
            LibraryFileInitializationResult? initializationResult = null, LibraryFileMutationCapability? capability = null, bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability? playlistCapability = null, Lr2SongDbSyncPreparedDataSurface? preparedSurface = null, OperationProgressRequest? originatingRequest = null, BmsLibraryOptionsSnapshot? optionsSnapshot = null, Action<bool>? admissionResult = null)
        {
            admissionResult?.Invoke(true);
            QueueCount++;
            QueueObserved?.Invoke();
            if (QueueFailure != null)
            {
                throw QueueFailure;
            }
            await Task.CompletedTask;
            return true;
        }



        public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason, LibraryFileMutationCapability? capability = null, LibraryFileMutationCapability? playlistCapability = null)
        {
            return Task.CompletedTask;
        }

        public bool Cancel(string reason)
        {
            return false;
        }

    }
}
