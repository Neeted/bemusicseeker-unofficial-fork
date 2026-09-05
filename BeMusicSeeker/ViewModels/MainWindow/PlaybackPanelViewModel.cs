using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Livet;
using Livet.Commands;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the playback adapter and telemetry exposed by the playback panel.
/// </summary>
public sealed class PlaybackPanelViewModel : ViewModel,
    IDuplicateMaintenancePlaybackPort,
    ISelectedChartMutationPlaybackPort,
    IFolderAutoRenamePlaybackPort,
    IPendingPackageMutationPlaybackPort,
    ISelectedChartAudioConversionPlaybackPort,
    ISettingsDialogPlaybackRuntimePort
{
    private readonly IPlaybackUiDispatcher uiDispatcher;

    private readonly IPlaybackChartQueue playbackQueue;

    private readonly IPlaybackSettingsStore playbackSettings;

    private readonly IPlaybackDialogService playbackDialogs;

    private readonly Action<Exception> warnInvalidChart;

    private readonly ChartFileOperationSynchronizer chartFileOperations;

    private readonly object sessionGate = new();

    private readonly object playerOperationGate = new();

    private readonly SemaphoreSlim playerReplacementGate = new(1, 1);

    private readonly object workflowGate = new();

    private IBMSPlayer bmsPlayer;

    private long playbackGeneration;

    private volatile bool shutdownStarted;

    private BMSFile nowPlayingBmsFile;

    private BMSFile displayedBmsPlayerFile;

    private int nowPlayingRowIndex = -1;

    private BMSLibrary library;

    private IExternalPlayerWindowHost windowHost;

    private TimeSpan currentlyPlayingDuration;

    private TimeSpan currentlyPlayingStopTime;

    private TimeSpan currentlyPlayingBmsDuration;

    private TimeSpan currentlyPlayingMusicDuration;

    private int currentlyPlayingCurrentVoices;

    private int currentlyPlayingMaxVoices;

    private int currentlyPlayingNoteDensity;

    private int currentlyPlayingNoteDensityMax;

    private int currentlyPlayingBpm;

    private int currentlyPlayingMinBpm;

    private int currentlyPlayingMaxBpm;

    private double currentlyPlayingTotal;

    private int currentlyPlayingCombo;

    private int currentlyPlayingNotes;

    private int currentlyPlayingMeasure;

    private int currentlyPlayingLastMeasure;

    private string bmsPlayerHeaderTitle = string.Empty;

    private string bmsPlayerHeaderSubtitle = string.Empty;

    private string bmsPlayerHeaderArtist = string.Empty;

    private ViewModelCommand nextCommand;
    private ViewModelCommand previousCommand;
    private ViewModelCommand restartCommand;
    private ViewModelCommand startCommand;
    private ViewModelCommand stopCommand;
    private ViewModelCommand fastForwardStartCommand;
    private ViewModelCommand fastForwardEndCommand;
    private ViewModelCommand fastBackwardStartCommand;
    private ViewModelCommand fastBackwardEndCommand;
    private ViewModelCommand showInfoCommand;
    private ViewModelCommand showEffectCommand;
    private ViewModelCommand changePlaysideCommand;
    private ViewModelCommand increaseHighSpeedCommand;
    private ViewModelCommand decreaseHighSpeedCommand;

    private sealed class PlaybackStartObservation
    {
        internal PlaybackStartObservation(
            long generation,
            IBMSPlayer player,
            BMSFile file,
            Action<object, EventArgs> onExit)
        {
            Generation = generation;
            Player = player;
            File = file;
            OnExit = onExit;
        }

        internal long Generation { get; }

        internal IBMSPlayer Player { get; }

        internal BMSFile File { get; }

        internal Action<object, EventArgs> OnExit { get; }

        internal bool StartCompleted { get; set; }

        internal bool StartSucceeded { get; set; }

        internal bool ExitRequested { get; set; }

        internal object ExitSender { get; set; }

        internal EventArgs ExitArgs { get; set; }

        internal bool ExitClaimed { get; set; }
    }

    internal PlaybackPanelViewModel(
        IBMSPlayer player,
        IPlaybackUiDispatcher uiDispatcher,
        IPlaybackChartQueue playbackQueue,
        IPlaybackSettingsStore playbackSettings,
        IPlaybackDialogService playbackDialogs,
        Action<Exception> warnInvalidChart,
        ChartFileOperationSynchronizer chartFileOperations)
    {
        this.uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        this.playbackQueue = playbackQueue ?? throw new ArgumentNullException(nameof(playbackQueue));
        this.playbackSettings = playbackSettings ?? throw new ArgumentNullException(nameof(playbackSettings));
        this.playbackDialogs = playbackDialogs ?? throw new ArgumentNullException(nameof(playbackDialogs));
        this.warnInvalidChart = warnInvalidChart ?? throw new ArgumentNullException(nameof(warnInvalidChart));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        InitializePlayer(player);
    }

    public ViewModelCommand NextCommand => nextCommand ??= CreateBackgroundCommand(() => Next(), "PlaybackPanel.Next");
    public ViewModelCommand PreviousCommand => previousCommand ??= CreateBackgroundCommand(() => Previous(), "PlaybackPanel.Previous");
    public ViewModelCommand RestartCommand => restartCommand ??= CreateBackgroundCommand(RestartPlayingBmsFile, "PlaybackPanel.Restart");
    public ViewModelCommand StartCommand => startCommand ??= CreateBackgroundCommand(() => Start(forceNewPlay: false), "PlaybackPanel.Start");
    public ViewModelCommand StopCommand => stopCommand ??= CreateBackgroundCommand(() => StopPlayback(closeProcess: true), "PlaybackPanel.Stop");
    public ViewModelCommand FastForwardStartCommand => fastForwardStartCommand ??= CreateBackgroundCommand(FastForwardStart, "PlaybackPanel.FastForwardStart");
    public ViewModelCommand FastForwardEndCommand => fastForwardEndCommand ??= CreateBackgroundCommand(FastForwardEnd, "PlaybackPanel.FastForwardEnd");
    public ViewModelCommand FastBackwardStartCommand => fastBackwardStartCommand ??= CreateBackgroundCommand(FastBackwardStart, "PlaybackPanel.FastBackwardStart");
    public ViewModelCommand FastBackwardEndCommand => fastBackwardEndCommand ??= CreateBackgroundCommand(FastBackwardEnd, "PlaybackPanel.FastBackwardEnd");
    public ViewModelCommand ShowInfoCommand => showInfoCommand ??= CreateBackgroundCommand(ShowInfo, "PlaybackPanel.ShowInfo");
    public ViewModelCommand ShowEffectCommand => showEffectCommand ??= CreateBackgroundCommand(ShowEffect, "PlaybackPanel.ShowEffect");
    public ViewModelCommand ChangePlaysideCommand => changePlaysideCommand ??= CreateBackgroundCommand(ChangePlayside, "PlaybackPanel.ChangePlayside");
    public ViewModelCommand IncreaseHighSpeedCommand => increaseHighSpeedCommand ??= CreateBackgroundCommand(IncreaseHighSpeed, "PlaybackPanel.IncreaseHighSpeed");
    public ViewModelCommand DecreaseHighSpeedCommand => decreaseHighSpeedCommand ??= CreateBackgroundCommand(DecreaseHighSpeed, "PlaybackPanel.DecreaseHighSpeed");

    private ViewModelCommand CreateBackgroundCommand(Action action, string routeName)
    {
        return new ViewModelCommand(
            () => StartBackgroundPlaybackAction(action, routeName),
            () => !shutdownStarted);
    }

    private void StartBackgroundPlaybackAction(Action action, string routeName)
    {
        if (shutdownStarted)
        {
            return;
        }
        Task.Run(() =>
        {
            if (!shutdownStarted)
            {
                try
                {
                    action();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    Task.FromException(exception).ObserveFault(routeName);
                    NotifyPlaybackFailureSafely(exception);
                }
            }
        }).ObserveFault(routeName);
    }

    /// <summary>
    /// 終了処理が UI を待機可能にする前に、再生操作の受付と古い終了 callback を失効させる。
    /// 通常の停止では呼ばず、この owner の受付は再開しない。
    /// </summary>
    internal void BeginShutdown()
    {
        if (shutdownStarted)
        {
            return;
        }
        // sessionGate は通常の player control 中にも保持されるため、UI の終了受付では待たない。
        // observation の current 判定もこの flag を見るので、generation の更新は不要。
        shutdownStarted = true;
        ViewModelCommand[] commands = [nextCommand, previousCommand, restartCommand, startCommand,
            stopCommand, fastForwardStartCommand, fastForwardEndCommand, fastBackwardStartCommand,
            fastBackwardEndCommand, showInfoCommand, showEffectCommand, changePlaysideCommand,
            increaseHighSpeedCommand, decreaseHighSpeedCommand];
        foreach (ViewModelCommand command in commands)
        {
            command?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 受付を閉じた後、先行する曲選択処理を回収して player を解放する。worker から呼ぶ。
    /// </summary>
    internal void CloseForShutdown()
    {
        lock (workflowGate)
        {
            CloseProcessCore(forShutdown: true);
        }
    }

    internal void AttachLibrary(BMSLibrary library)
    {
        if (library == null)
        {
            throw new ArgumentNullException(nameof(library));
        }
        if (this.library != null && !ReferenceEquals(this.library, library))
        {
            throw new InvalidOperationException("Playback library is already attached.");
        }
        this.library = library;
    }

    void IDuplicateMaintenancePlaybackPort.StopPlaybackForMerge()
    {
        StopPlayback(closeProcess: true);
    }

    void IDuplicateMaintenancePlaybackPort.StopPlaybackForCharts(IReadOnlyList<ChartFile> charts)
    {
        StopIfPlayingCharts(GetBmsFormatCharts(charts));
    }

    void ISelectedChartMutationPlaybackPort.StopPlaybackForPendingCharts(IReadOnlyList<ChartFile> charts)
    {
        StopIfPlayingCharts(charts);
    }

    void ISelectedChartMutationPlaybackPort.StopPlaybackForLibraryCharts(IReadOnlyList<LibraryChartRef> charts)
    {
        StopIfPlayingLibraryCharts(charts);
    }

    void ISelectedChartMutationPlaybackPort.StopPlaybackForChartDirectories(IReadOnlyList<string> directories)
    {
        StopIfPlayingChartDirectories(directories);
    }

    void IFolderAutoRenamePlaybackPort.StopPlaybackForCharts(IReadOnlyList<ChartFile> charts)
    {
        StopIfPlayingCharts(charts);
    }

    void IFolderAutoRenamePlaybackPort.StopPlaybackForFolderMutation()
        => StopPlayback(closeProcess: true);

    void IPendingPackageMutationPlaybackPort.StopIfPlayingCharts(IReadOnlyList<ChartFile> charts)
    {
        StopIfPlayingCharts(charts);
    }

    void ISelectedChartAudioConversionPlaybackPort.StopPlayback()
    {
        StopPlayback(closeProcess: true);
    }

    private static List<ChartFile> GetBmsFormatCharts(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    internal event EventHandler PlaybackStarting;

    internal event EventHandler PlaybackStarted;

    internal void HandleTableSelection(object row)
    {
        if (NowPlayingBmsFile == null && GridRowResolver.TryGetBmsPlayerFile(row, out BMSFile bmsFile))
        {
            SetBmsPlayerHeader(bmsFile);
        }
    }

    /// <summary>有効な譜面の再生要求を投入する。終了受付後は false を返す。</summary>
    internal bool HandleTableRowActivation(int rowIndex, object row)
    {
        if (shutdownStarted || rowIndex < 0
            || rowIndex >= playbackQueue.Count
            || !GridRowResolver.TryGetBmsPlayerFile(row, out BMSFile bmsFile))
        {
            return false;
        }

        SetBmsPlayerHeader(bmsFile);
        bool shouldSelectBmsPlayerSurface = IsStoppedOrPaused;
        StartBackgroundPlaybackAction(
            () => ExecuteTableRowActivation(rowIndex, row),
            "PlaybackPanel.ActivateRow");
        return shouldSelectBmsPlayerSurface;
    }

    /// <summary>queue の同一行を確認して再生する。終了受付後の待機済み要求は破棄する。</summary>
    internal void ExecuteTableRowActivation(int rowIndex, object expectedRow)
    {
        lock (workflowGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            if (rowIndex < 0 || rowIndex >= playbackQueue.Count)
            {
                return;
            }

            object currentRow;
            try
            {
                currentRow = playbackQueue.GetRow(rowIndex);
            }
            catch
            {
                return;
            }
            if (!ReferenceEquals(currentRow, expectedRow))
            {
                return;
            }

            playbackQueue.SelectedIndex = rowIndex;
            StopPlayback();
            StartAtIndex(rowIndex, currentRow, useInitialRow: true);
        }
    }

    /// <summary>
    /// Gets the chart whose playback session is active or being prepared.
    /// </summary>
    public BMSFile NowPlayingBmsFile
    {
        get => nowPlayingBmsFile;
        private set
        {
            if (ReferenceEquals(nowPlayingBmsFile, value))
            {
                return;
            }

            nowPlayingBmsFile = value;
            RaisePropertyChanged(nameof(NowPlayingBmsFile));
            RaisePlaybackStatusPropertiesChanged();
        }
    }

    /// <summary>
    /// Gets the chart whose metadata is displayed by the BMS player header.
    /// </summary>
    public BMSFile DisplayedBmsPlayerFile
    {
        get => displayedBmsPlayerFile;
        private set
        {
            if (!ReferenceEquals(displayedBmsPlayerFile, value))
            {
                displayedBmsPlayerFile = value;
                RaisePropertyChanged(nameof(DisplayedBmsPlayerFile));
            }
        }
    }

    public bool IsPlaying => NowPlayingBmsFile?.status.HasFlag(BMSFile.BMSFileStatus.PLAY) == true;

    public bool IsPaused => NowPlayingBmsFile?.status.HasFlag(BMSFile.BMSFileStatus.PAUSE) == true;

    public bool IsStoppedOrPaused => NowPlayingBmsFile == null || IsPaused;

    public PlayerPanelState PlayerPanelState
    {
        get => playbackSettings.PlayerPanelState;
        set
        {
            if (playbackSettings.PlayerPanelState != value)
            {
                playbackSettings.PlayerPanelState = value;
                RaisePropertyChanged(nameof(PlayerPanelState));
                RaisePlayerHeaderPropertiesChanged();
            }
        }
    }

    /// <summary>
    /// Gets whether a requested panel state is selectable with the current BMS player surface.
    /// </summary>
    /// <param name="state">Panel state requested by the user or persisted settings.</param>
    /// <param name="bmsPlayerSurfaceAvailable">Whether the BMS player surface is usable.</param>
    internal bool CanSelectPanelState(
        PlayerPanelState state,
        bool bmsPlayerSurfaceAvailable)
    {
        if (state.HasFlag(PlayerPanelState.BMS_PLAYER) && !bmsPlayerSurfaceAvailable)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Selects a panel state when its current BMS player surface is available.
    /// </summary>
    /// <param name="state">Panel state to select.</param>
    /// <param name="bmsPlayerSurfaceAvailable">Whether the BMS player surface is usable.</param>
    /// <returns><see langword="true"/> when the state was selected.</returns>
    internal bool TrySelectPanelState(
        PlayerPanelState state,
        bool bmsPlayerSurfaceAvailable)
    {
        if (!CanSelectPanelState(state, bmsPlayerSurfaceAvailable))
        {
            return false;
        }

        PlayerPanelState = state;
        return true;
    }

    /// <summary>
    /// Cycles between the title and BMS player surfaces while preserving compactness.
    /// </summary>
    /// <param name="bmsPlayerSurfaceAvailable">Whether the BMS player surface is usable.</param>
    internal void RotatePanelState(bool bmsPlayerSurfaceAvailable)
    {
        PlayerPanelState state = PlayerPanelState;
        do
        {
            state = state.HasFlag(PlayerPanelState.BMS_PLAYER)
                ? state & ~PlayerPanelState.BMS_PLAYER
                : state | PlayerPanelState.BMS_PLAYER;
        }
        while (!CanSelectPanelState(state, bmsPlayerSurfaceAvailable));

        PlayerPanelState = state;
    }

    internal void ToggleCompactPanel()
    {
        PlayerPanelState ^= PlayerPanelState.TITLE_SMALL;
    }

    public bool RepeatPlayMode
    {
        get => playbackSettings.RepeatPlay;
        set => SetPlaybackSetting(playbackSettings.RepeatPlay, value, v => playbackSettings.RepeatPlay = v, nameof(RepeatPlayMode));
    }

    public bool FolderSkipPlayMode
    {
        get => playbackSettings.FolderSkipPlay;
        set => SetPlaybackSetting(playbackSettings.FolderSkipPlay, value, v => playbackSettings.FolderSkipPlay = v, nameof(FolderSkipPlayMode));
    }

    public bool SinglePlayMode
    {
        get => playbackSettings.SinglePlay;
        set => SetPlaybackSetting(playbackSettings.SinglePlay, value, v => playbackSettings.SinglePlay = v, nameof(SinglePlayMode));
    }

    public bool CanSeek => !playbackSettings.UsesLr2Body;

    public bool CanChangeHighSpeed => playbackSettings.UsesUbMplay || playbackSettings.UsesBmiIdxView;

    public bool CanShowInfo => !playbackSettings.UsesLr2Body && !playbackSettings.UsesBmiIdxView;

    public bool CanShowEffect => playbackSettings.UsesUbMplay;

    public bool CanChangePlayside => playbackSettings.UsesUbMplay;

    public bool UseExternalPanelImage => playbackSettings.UseExternalPanelImage;

    public string StagefilePath => playbackSettings.StagefilePath;

    internal bool UsesUbMplay => playbackSettings.UsesUbMplay;

    internal bool UsesLr2Body => playbackSettings.UsesLr2Body;

    internal bool UsesBmiIdxView => playbackSettings.UsesBmiIdxView;

    internal int NowPlayingRowIndex => nowPlayingRowIndex;

    /// <summary>
    /// Gets the current player duration used by the progress controls.
    /// </summary>
    public TimeSpan CurrentlyPlayingDuration
    {
        get => currentlyPlayingDuration;
        private set => SetPlaybackValue(ref currentlyPlayingDuration, value, nameof(CurrentlyPlayingDuration));
    }

    /// <summary>
    /// Gets or sets the current player time used by the progress slider.
    /// </summary>
    public TimeSpan CurrentlyPlayingTime
    {
        get
        {
            lock (sessionGate)
            {
                return bmsPlayer?.CurrentTime ?? TimeSpan.MinValue;
            }
        }
        set
        {
            lock (sessionGate)
            {
                if (shutdownStarted || bmsPlayer == null)
                {
                    RaisePropertyChanged(nameof(CurrentlyPlayingTime));
                    return;
                }

                bmsPlayer.CurrentTime = value;
                RaisePropertyChanged(nameof(CurrentlyPlayingTime));
            }
        }
    }

    public TimeSpan CurrentlyPlayingStopTime
    {
        get => currentlyPlayingStopTime;
        private set => SetPlaybackValue(ref currentlyPlayingStopTime, value, nameof(CurrentlyPlayingStopTime));
    }

    public TimeSpan CurrentlyPlayingBmsDuration
    {
        get => currentlyPlayingBmsDuration;
        private set => SetPlaybackValue(ref currentlyPlayingBmsDuration, value, nameof(CurrentlyPlayingBmsDuration));
    }

    public TimeSpan CurrentlyPlayingMusicDuration
    {
        get => currentlyPlayingMusicDuration;
        private set => SetPlaybackValue(ref currentlyPlayingMusicDuration, value, nameof(CurrentlyPlayingMusicDuration));
    }

    public int CurrentlyPlayingCurrentVoices
    {
        get => currentlyPlayingCurrentVoices;
        private set => SetPlaybackValue(ref currentlyPlayingCurrentVoices, value, nameof(CurrentlyPlayingCurrentVoices));
    }

    public int CurrentlyPlayingMaxVoices
    {
        get => currentlyPlayingMaxVoices;
        private set => SetPlaybackValue(ref currentlyPlayingMaxVoices, value, nameof(CurrentlyPlayingMaxVoices));
    }

    public int CurrentlyPlayingNoteDensity
    {
        get => currentlyPlayingNoteDensity;
        private set => SetPlaybackValue(ref currentlyPlayingNoteDensity, value, nameof(CurrentlyPlayingNoteDensity));
    }

    public int CurrentlyPlayingNoteDensityMax
    {
        get => currentlyPlayingNoteDensityMax;
        private set => SetPlaybackValue(ref currentlyPlayingNoteDensityMax, value, nameof(CurrentlyPlayingNoteDensityMax));
    }

    public int CurrentlyPlayingBpm
    {
        get => currentlyPlayingBpm;
        private set => SetPlaybackValue(ref currentlyPlayingBpm, value, nameof(CurrentlyPlayingBpm));
    }

    public int CurrentlyPlayingMinBpm
    {
        get => currentlyPlayingMinBpm;
        private set => SetPlaybackValue(ref currentlyPlayingMinBpm, value, nameof(CurrentlyPlayingMinBpm));
    }

    public int CurrentlyPlayingMaxBpm
    {
        get => currentlyPlayingMaxBpm;
        private set => SetPlaybackValue(ref currentlyPlayingMaxBpm, value, nameof(CurrentlyPlayingMaxBpm));
    }

    public double CurrentlyPlayingTotal
    {
        get => currentlyPlayingTotal;
        private set => SetPlaybackValue(ref currentlyPlayingTotal, value, nameof(CurrentlyPlayingTotal));
    }

    public int CurrentlyPlayingCombo
    {
        get => currentlyPlayingCombo;
        private set => SetPlaybackValue(ref currentlyPlayingCombo, value, nameof(CurrentlyPlayingCombo));
    }

    public int CurrentlyPlayingNotes
    {
        get => currentlyPlayingNotes;
        private set => SetPlaybackValue(ref currentlyPlayingNotes, value, nameof(CurrentlyPlayingNotes));
    }

    public int CurrentlyPlayingMeasure
    {
        get => currentlyPlayingMeasure;
        private set => SetPlaybackValue(ref currentlyPlayingMeasure, value, nameof(CurrentlyPlayingMeasure));
    }

    public int CurrentlyPlayingLastMeasure
    {
        get => currentlyPlayingLastMeasure;
        private set => SetPlaybackValue(ref currentlyPlayingLastMeasure, value, nameof(CurrentlyPlayingLastMeasure));
    }

    /// <summary>
    /// Replaces the adapter after settings changes and rebinds its telemetry events.
    /// </summary>
    private void InitializePlayer(IBMSPlayer player)
    {
        if (player == null)
        {
            throw new ArgumentNullException(nameof(player));
        }
        PlayerStateSnapshot preparedState = PlayerStateSnapshot.Capture(player);
        player.PropertyChanged += BmsPlayerPropertyChanged;
        bmsPlayer = player;
        ApplyPlayerState(preparedState);
        RaisePropertyChanged(nameof(CurrentlyPlayingTime));
    }

    internal async Task ReplacePlayerAsync(IBMSPlayer player, bool clearPlaybackStatus = false)
    {
        if (player == null)
        {
            throw new ArgumentNullException(nameof(player));
        }

        await playerReplacementGate.WaitAsync().ConfigureAwait(false);
        try
        {
            IBMSPlayer previousPlayer;
            IExternalPlayerWindowHost currentWindowHost;
            bool samePlayer;
            lock (sessionGate)
            {
                samePlayer = ReferenceEquals(bmsPlayer, player);
                previousPlayer = bmsPlayer;
                currentWindowHost = windowHost;
            }
            if (samePlayer)
            {
                await uiDispatcher.DispatchAsync(() => RefreshPlayerState(player)).ConfigureAwait(false);
                return;
            }

            PlayerStateSnapshot preparedState = PlayerStateSnapshot.Capture(player);
            if (currentWindowHost != null && player is IExternalWindowPlayer externalWindowPlayer)
            {
                externalWindowPlayer.AttachWindowHost(currentWindowHost);
            }
            player.PropertyChanged += BmsPlayerPropertyChanged;

            long replacementGeneration;
            lock (sessionGate)
            {
                if (!ReferenceEquals(bmsPlayer, previousPlayer))
                {
                    player.PropertyChanged -= BmsPlayerPropertyChanged;
                    throw new InvalidOperationException(
                        "Playback player changed while a settings replacement was being prepared.");
                }
                replacementGeneration = ++playbackGeneration;
            }

            await ReplacePlayerAfterPreparationAsync(
                    player,
                    previousPlayer,
                    preparedState,
                    clearPlaybackStatus,
                    replacementGeneration)
                .ConfigureAwait(false);
        }
        finally
        {
            playerReplacementGate.Release();
        }
    }

    private async Task ReplacePlayerAfterPreparationAsync(
        IBMSPlayer player,
        IBMSPlayer previousPlayer,
        PlayerStateSnapshot preparedState,
        bool clearPlaybackStatus,
        long replacementGeneration)
    {
        try
        {
            await Task.Run(() =>
            {
                lock (playerOperationGate)
                {
                    previousPlayer?.CloseProcess();
                    lock (sessionGate)
                    {
                        if (!ReferenceEquals(bmsPlayer, previousPlayer)
                            || playbackGeneration != replacementGeneration)
                        {
                            throw new InvalidOperationException(
                                "Playback player changed while a settings replacement was in progress.");
                        }
                        if (previousPlayer != null)
                        {
                            previousPlayer.PropertyChanged -= BmsPlayerPropertyChanged;
                        }
                        bmsPlayer = player;
                    }
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            player.PropertyChanged -= BmsPlayerPropertyChanged;
            throw;
        }

        await uiDispatcher.DispatchAsync(() =>
        {
            lock (sessionGate)
            {
                if (!ReferenceEquals(bmsPlayer, player)
                    || playbackGeneration != replacementGeneration)
                {
                    return;
                }
                if (clearPlaybackStatus)
                {
                    ClearCurrentPlaybackStatus();
                    NowPlayingBmsFile = null;
                    nowPlayingRowIndex = -1;
                }
                ApplyPlayerState(preparedState);
                RaisePropertyChanged(nameof(CurrentlyPlayingTime));
            }
        }).ConfigureAwait(false);
    }

    internal void AttachWindowHost(IExternalPlayerWindowHost windowHost)
    {
        if (windowHost == null)
        {
            throw new ArgumentNullException(nameof(windowHost));
        }

        lock (sessionGate)
        {
            this.windowHost = windowHost;
            if (RequirePlayer() is IExternalWindowPlayer externalWindowPlayer)
            {
                externalWindowPlayer.AttachWindowHost(windowHost);
            }
        }
    }

    /// <summary>通常の player close。終了受付後の遅い要求は terminal close に委ねる。</summary>
    internal void CloseProcess() => CloseProcessCore(forShutdown: false);

    private void CloseProcessCore(bool forShutdown)
    {
        IBMSPlayer player;
        lock (sessionGate)
        {
            if (bmsPlayer == null)
            {
                return;
            }

            playbackGeneration++;
            player = bmsPlayer;
        }
        CloseCapturedPlayer(player, forShutdown);
    }

    /// <summary>現行 session の player 開始を試みる。終了受付後や失効済みの要求は false を返す。</summary>
    internal Task<bool> TryPlayStart(long expectedGeneration, string bmsFilePath, Action<object, EventArgs> onExitEventHandler)
    {
        Task<bool> playStartTask = TryPlayStart(
            expectedGeneration,
            bmsFilePath,
            onExitEventHandler,
            out PlaybackStartObservation observation);
        return playStartTask;
    }

    private Task<bool> TryPlayStart(
        long expectedGeneration,
        string bmsFilePath,
        Action<object, EventArgs> onExitEventHandler,
        out PlaybackStartObservation observation)
    {
        IBMSPlayer player;
        BMSFile file;
        PlaybackStartObservation currentObservation;
        lock (sessionGate)
        {
            player = RequirePlayer();
            file = NowPlayingBmsFile;
            if (shutdownStarted || file == null || expectedGeneration != playbackGeneration)
            {
                observation = null;
                return Task.FromResult(false);
            }

            file.status |= BMSFile.BMSFileStatus.LOADING;
            RaisePlaybackStatusPropertiesChanged();

            currentObservation = new PlaybackStartObservation(
                expectedGeneration,
                player,
                file,
                onExitEventHandler);
            observation = currentObservation;
        }

        Task playStartTask;
        lock (playerOperationGate)
        {
            lock (sessionGate)
            {
                if (!IsCurrentPlaybackObservation(currentObservation))
                {
                    return Task.FromResult(false);
                }
            }
            playStartTask = player.PlayStart(
                bmsFilePath,
                (sender, e) => HandlePlaybackExit(currentObservation, sender, e));
        }
        if (playStartTask == null)
        {
            throw new InvalidOperationException("The playback player returned no start task.");
        }
        bool completedSynchronously;
        Task<bool> observedTask = null;
        lock (sessionGate)
        {
            if (!playStartTask.IsFaulted && !playStartTask.IsCanceled
                && expectedGeneration == playbackGeneration
                && ReferenceEquals(player, bmsPlayer)
                && ReferenceEquals(file, NowPlayingBmsFile))
            {
                file.status &= ~BMSFile.BMSFileStatus.LOADING;
                file.status |= BMSFile.BMSFileStatus.PLAY;
                RaisePlaybackStatusPropertiesChanged();
            }

            completedSynchronously = playStartTask.IsCompleted;
            if (!completedSynchronously)
            {
                observedTask = ObservePlaybackStartAsync(playStartTask, currentObservation);
            }
        }
        if (completedSynchronously)
        {
            if (!playStartTask.IsFaulted && !playStartTask.IsCanceled)
            {
                CompletePlaybackStartObservation(currentObservation, succeeded: true, failure: null);
            }
            return AwaitPlaybackCompletionAsync(playStartTask);
        }
        if (observedTask != null)
        {
            TrackPlaybackStart(observedTask);
            return observedTask;
        }
        throw new InvalidOperationException("Playback start observation was not created.");
    }

    private static async Task<bool> AwaitPlaybackCompletionAsync(Task playStartTask)
    {
        await playStartTask;
        return true;
    }

    private async Task<bool> ObservePlaybackStartAsync(
        Task playStartTask,
        PlaybackStartObservation observation)
    {
        try
        {
            await playStartTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompletePlaybackStartObservation(observation, succeeded: false, failure: null);
            throw;
        }
        catch (Exception exception)
        {
            CompletePlaybackStartObservation(observation, succeeded: false, failure: exception);
            throw;
        }
        CompletePlaybackStartObservation(observation, succeeded: true, failure: null);
        return true;
    }

    private static void TrackPlaybackStart(Task<bool> playStartTask)
    {
        ((Task)playStartTask).ObserveFault("PlaybackPanel.PlaybackStartObservation");
    }

    private void CompletePlaybackStartObservation(
        PlaybackStartObservation observation,
        bool succeeded,
        Exception failure)
    {
        Action<object, EventArgs> exitCallback = null;
        object exitSender = null;
        EventArgs exitArgs = null;
        bool stoppedCurrentPlayback = false;
        IBMSPlayer playerToClose = null;
        lock (sessionGate)
        {
            observation.StartCompleted = true;
            observation.StartSucceeded = succeeded;
            if (IsCurrentPlaybackObservation(observation))
            {
                if (!succeeded)
                {
                    playerToClose = ClearPlaybackStateWithoutExternalCall(closeProcess: true);
                    stoppedCurrentPlayback = true;
                }
                else if (observation.ExitRequested && !observation.ExitClaimed)
                {
                    observation.ExitClaimed = true;
                    playbackGeneration++;
                    exitCallback = observation.OnExit;
                    exitSender = observation.ExitSender;
                    exitArgs = observation.ExitArgs;
                }
            }
        }
        CloseCapturedPlayer(playerToClose);
        if (stoppedCurrentPlayback && failure != null)
        {
            NotifyPlaybackFailureSafely(failure);
        }
        if (exitCallback != null)
        {
            InvokePlaybackExitCallback(exitCallback, exitSender, exitArgs);
        }
    }

    private void HandlePlaybackExit(PlaybackStartObservation observation, object sender, EventArgs e)
    {
        Action<object, EventArgs> exitCallback = null;
        lock (sessionGate)
        {
            if (!IsCurrentPlaybackObservation(observation))
            {
                return;
            }

            if (!observation.StartCompleted)
            {
                observation.ExitRequested = true;
                observation.ExitSender = sender;
                observation.ExitArgs = e;
                return;
            }

            if (!observation.StartSucceeded || observation.ExitClaimed)
            {
                return;
            }

            observation.ExitClaimed = true;
            playbackGeneration++;
            exitCallback = observation.OnExit;
        }
        if (exitCallback != null)
        {
            InvokePlaybackExitCallback(exitCallback, sender, e);
        }
    }

    private bool IsCurrentPlaybackObservation(PlaybackStartObservation observation)
    {
        return !shutdownStarted && observation != null
            && observation.Generation == playbackGeneration
            && ReferenceEquals(observation.Player, bmsPlayer)
            && ReferenceEquals(observation.File, NowPlayingBmsFile);
    }

    private void InvokePlaybackExitCallback(
        Action<object, EventArgs> exitCallback,
        object sender,
        EventArgs e)
    {
        try
        {
            exitCallback(sender, e);
        }
        catch (Exception exception)
        {
            Task.FromException(exception).ObserveFault("PlaybackPanel.PlaybackExit");
        }
    }

    private void StopAfterPlaybackStartFailure(
        PlaybackStartObservation observation,
        Exception failure)
    {
        if (!TryStopPlaybackForStartObservation(observation))
        {
            return;
        }
        NotifyPlaybackFailureSafely(failure);
    }

    private bool TryStopPlaybackForStartObservation(PlaybackStartObservation observation)
    {
        IBMSPlayer playerToClose;
        lock (sessionGate)
        {
            if (shutdownStarted || (observation != null && !IsCurrentPlaybackObservation(observation)))
            {
                return false;
            }
            playerToClose = ClearPlaybackStateWithoutExternalCall(closeProcess: true);
        }
        CloseCapturedPlayer(playerToClose);
        return true;
    }

    private bool TryStopPlaybackForGeneration(long generation, BMSFile file)
    {
        IBMSPlayer playerToClose;
        lock (sessionGate)
        {
            if (shutdownStarted || generation != playbackGeneration || !ReferenceEquals(file, NowPlayingBmsFile))
            {
                return false;
            }
            playerToClose = ClearPlaybackStateWithoutExternalCall(closeProcess: true);
        }
        CloseCapturedPlayer(playerToClose);
        return true;
    }

    private void NotifyPlaybackFailureSafely(Exception failure)
    {
        try
        {
            playbackDialogs.NotifyPlaybackFailure(failure);
        }
        catch (Exception notificationFailure)
        {
            Task.FromException(notificationFailure).ObserveFault("PlaybackPanel.PlaybackFailureNotification");
        }
    }

    private bool IsPlaybackStartObservationCompleted(PlaybackStartObservation observation)
    {
        lock (sessionGate)
        {
            return observation?.StartCompleted == true;
        }
    }

    /// <summary>選択譜面の再生または一時停止を行う。終了受付後は操作しない。</summary>
    internal void Start(bool forceNewPlay = true)
    {
        lock (workflowGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            if (!forceNewPlay && NowPlayingBmsFile != null)
            {
                TogglePause();
                return;
            }

            StopPlayback();
            if (playbackQueue.SelectedIndex >= 0 && playbackQueue.SelectedIndex < playbackQueue.Count)
            {
                StartAtIndex(playbackQueue.SelectedIndex);
            }
        }
    }

    /// <summary>次の有効な譜面へ進む。終了受付後の入力と自動送りは操作しない。</summary>
    internal void Next(object sender = null, EventArgs e = null)
    {
        lock (workflowGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            int index = NowPlayingRowIndex;
            if (index < 0 || index >= playbackQueue.Count)
            {
                StopPlayback();
                return;
            }
            if (sender != null && playbackSettings.SinglePlay)
            {
                if (!playbackSettings.RepeatPlay)
                {
                    StopPlayback();
                    return;
                }
            }
            else
            {
                string currentFolder = GetPlaybackFolderIdentity(NowPlayingBmsFile, index);
                index++;
                if (playbackSettings.RepeatPlay && index == playbackQueue.Count)
                {
                    index = 0;
                }
                while (playbackSettings.FolderSkipPlay && index != playbackQueue.Count)
                {
                    GridRowResolver.TryGetBmsPlayerFile(playbackQueue.GetRow(index), out BMSFile candidate);
                    if (index == NowPlayingRowIndex)
                    {
                        break;
                    }
                    string candidateFolder = GetPlaybackFolderIdentity(candidate, index);
                    if (!string.IsNullOrWhiteSpace(candidateFolder) && currentFolder != candidateFolder)
                    {
                        break;
                    }
                    currentFolder = candidateFolder;
                    index++;
                    if (playbackSettings.RepeatPlay && index == playbackQueue.Count)
                    {
                        index = 0;
                    }
                }
            }
            if (index < playbackQueue.Count)
            {
                StopPlayback();
                StartAtIndex(index);
            }
            else if (sender != null)
            {
                StopPlayback();
            }
        }
    }

    /// <summary>前の有効な譜面へ戻る。終了受付後は操作しない。</summary>
    internal void Previous(object sender = null, EventArgs e = null)
    {
        lock (workflowGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            int index = NowPlayingRowIndex;
            if (index < 0 || index >= playbackQueue.Count)
            {
                StopPlayback();
                return;
            }
            if (sender != null && playbackSettings.SinglePlay)
            {
                if (!playbackSettings.RepeatPlay)
                {
                    StopPlayback();
                    return;
                }
            }
            else
            {
                string currentFolder = GetPlaybackFolderIdentity(NowPlayingBmsFile, index);
                index--;
                if (playbackSettings.RepeatPlay && index == -1)
                {
                    index = playbackQueue.Count - 1;
                }
                while (playbackSettings.FolderSkipPlay && index != -1)
                {
                    GridRowResolver.TryGetBmsPlayerFile(playbackQueue.GetRow(index), out BMSFile candidate);
                    if (index == NowPlayingRowIndex)
                    {
                        break;
                    }
                    string candidateFolder = GetPlaybackFolderIdentity(candidate, index);
                    if (!string.IsNullOrWhiteSpace(candidateFolder) && currentFolder != candidateFolder)
                    {
                        break;
                    }
                    currentFolder = candidateFolder;
                    index--;
                    if (playbackSettings.RepeatPlay && index == -1)
                    {
                        index = playbackQueue.Count - 1;
                    }
                }
            }
            if (index >= 0)
            {
                StopPlayback();
                StartAtIndex(index);
            }
            else if (sender != null)
            {
                StopPlayback();
            }
        }
    }

    private static string GetPlaybackFolderIdentity(BMSFile file, int fallbackIndex)
    {
        return !string.IsNullOrWhiteSpace(file?.path) && LongPathFileSystem.FileExists(file.path)
            ? Path.GetDirectoryName(file.path)
            : fallbackIndex.ToString();
    }

    private int FindNextPlaybackIndex()
    {
        int index = NowPlayingRowIndex;
        string currentFolder = GetPlaybackFolderIdentity(NowPlayingBmsFile, index);
        index++;
        if (playbackSettings.RepeatPlay && index == playbackQueue.Count)
        {
            index = 0;
        }
        while (playbackSettings.FolderSkipPlay && index != playbackQueue.Count)
        {
            GridRowResolver.TryGetBmsPlayerFile(playbackQueue.GetRow(index), out BMSFile candidate);
            if (index == NowPlayingRowIndex)
            {
                break;
            }
            string candidateFolder = GetPlaybackFolderIdentity(candidate, index);
            if (!string.IsNullOrWhiteSpace(candidateFolder) && currentFolder != candidateFolder)
            {
                break;
            }
            currentFolder = candidateFolder;
            index++;
            if (playbackSettings.RepeatPlay && index == playbackQueue.Count)
            {
                index = 0;
            }
        }
        return index;
    }

    /// <summary>曲選択の所有境界で指定行を再生する。終了受付後は操作しない。</summary>
    internal void StartAtIndex(int index)
    {
        lock (workflowGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            StartAtIndex(index, initialRow: null, useInitialRow: false);
        }
    }

    private void StartAtIndex(int index, object initialRow, bool useInitialRow)
    {
        BMSFile bmsFile = null;
        ChartFile playbackChart = null;
        int remainingCandidates = playbackQueue.Count;
    ResolveCandidate:
        while (remainingCandidates > 0)
        {
            if (shutdownStarted)
            {
                return;
            }
            object row;
            try
            {
                row = useInitialRow ? initialRow : playbackQueue.GetRow(index);
                useInitialRow = false;
                playbackChart = null;
                bmsFile = null;
                GridRowResolver.TryGetChartFile(row, out playbackChart);
                GridRowResolver.TryGetBmsPlayerFile(row, out bmsFile);
            }
            catch
            {
                return;
            }
            if (bmsFile != null
                && !string.IsNullOrWhiteSpace(bmsFile.path)
                && LongPathFileSystem.FileExists(bmsFile.path))
            {
                break;
            }

            SkipUnavailablePlaybackCandidate(index);
            remainingCandidates--;
            index++;
            if (index >= playbackQueue.Count)
            {
                if (playbackSettings.RepeatPlay)
                {
                    index = 0;
                }
                else
                {
                    StopPlayback();
                    return;
                }
            }
            if (remainingCandidates == 0)
            {
                StopPlayback();
                return;
            }
        }

        if (shutdownStarted)
        {
            return;
        }
        long generation = BeginPlayback(bmsFile, index);
        if (shutdownStarted)
        {
            return;
        }
        playbackQueue.SelectedIndex = index;
        playbackChart ??= ChartFileProjection.FromBmsFile(bmsFile, includeResourceReferences: false);
        string installDestination = playbackChart?.InstallDestination;
        if (!string.IsNullOrWhiteSpace(installDestination) && LongPathFileSystem.DirectoryExists(installDestination))
        {
            try
            {
                StartTemporarilyInstalledChart(playbackChart, bmsFile, generation, installDestination);
            }
            catch
            {
                TryStopPlaybackForGeneration(generation, bmsFile);
                throw;
            }
            return;
        }

        PlaybackStartObservation observation = null;
        try
        {
            Task<bool> playStartTask = TryPlayStart(
                generation,
                bmsFile.path,
                Next,
                out observation);
            if (playStartTask.IsCompleted && !playStartTask.GetAwaiter().GetResult())
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsPlaybackStartObservationCompleted(observation))
            {
                TryStopPlaybackForStartObservation(observation);
            }
            return;
        }
        catch (InvalidDataException value)
        {
            if (IsPlaybackStartObservationCompleted(observation)
                || (observation != null && !IsCurrentPlaybackObservation(observation)))
            {
                return;
            }
            warnInvalidChart(value);
            if (playbackSettings.RepeatPlay && (playbackSettings.SinglePlay || index == 0))
            {
                TryStopPlaybackForStartObservation(observation);
                return;
            }
            remainingCandidates--;
            if (remainingCandidates <= 0)
            {
                TryStopPlaybackForStartObservation(observation);
                return;
            }
            int nextIndex = FindNextPlaybackIndex();
            if (nextIndex < 0 || nextIndex >= playbackQueue.Count)
            {
                TryStopPlaybackForStartObservation(observation);
                return;
            }
            index = nextIndex;
            goto ResolveCandidate;
        }
        catch (Exception ex)
        {
            if (IsPlaybackStartObservationCompleted(observation))
            {
                return;
            }
            StopAfterPlaybackStartFailure(observation, ex);
            return;
        }
        NotifyPlaybackStarted(generation);
    }

    private void StartTemporarilyInstalledChart(
        ChartFile playbackChart,
        BMSFile bmsFile,
        long generation,
        string installDestination)
    {
        if (library == null)
        {
            throw new InvalidOperationException("Playback library must be attached before temporary-install playback.");
        }
        if (!chartFileOperations.TryEnter(out IDisposable operationGate))
        {
            return;
        }

        using (operationGate)
        {
            ChartPackage chartPackage = library.ChartPackagesPending
            .FirstOrDefault(package => ContainsChartTarget(package, playbackChart));
            if (chartPackage == null)
            {
                if (playbackSettings.UsesLr2Body
                    && playbackSettings.UsesLr2Database
                    && !playbackDialogs.ConfirmTemporaryInstallPlayback())
                {
                    TryStopPlaybackForGeneration(generation, bmsFile);
                    return;
                }
                chartPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(playbackChart)]);
                chartPackage.delete_parent = false;
            }

            string originalPath = bmsFile.path;
            Task<bool> playbackStartTask = null;
            PlaybackStartObservation playbackStartObservation = null;
            try
            {
                while (LongPathFileSystem.EntryExists(Path.Combine(installDestination, Path.GetFileName(bmsFile.path))))
                {
                    string temporaryName = Path.GetFileNameWithoutExtension(bmsFile.path) + "_" + Path.GetExtension(bmsFile.path);
                    LongPathFileSystem.MoveFile(
                        bmsFile.path,
                        Path.Combine(Path.GetDirectoryName(bmsFile.path), temporaryName),
                        overwrite: false);
                    bmsFile.path = Path.Combine(Path.GetDirectoryName(bmsFile.path), temporaryName);
                }

                List<string> sourceFiles = [];
                if (LongPathFileSystem.DirectoryExists(chartPackage.path))
                {
                    string[] permittedExtensions =
                    [
                        .. ChartFileKindResolver.BmsExtensions,
                    .. ChartResourceExtensions.AudioExtensions,
                    .. ChartResourceExtensions.ImageExtensions,
                ];
                    sourceFiles = [.. LongPathFileSystem.EnumerateFiles(chartPackage.path, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => permittedExtensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))];
                }
                else
                {
                    sourceFiles.Add(bmsFile.path);
                }

                using (new temporarilyCopyFiles(sourceFiles, installDestination, 2000))
                {
                    string playbackPath = Path.Combine(installDestination, Path.GetFileName(bmsFile.path));
                    try
                    {
                        playbackStartTask = TryPlayStart(
                            generation,
                            playbackPath,
                            Next,
                            out playbackStartObservation);
                        if (playbackStartTask.IsCompleted
                            && !playbackStartTask.GetAwaiter().GetResult())
                        {
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (!IsPlaybackStartObservationCompleted(playbackStartObservation))
                        {
                            TryStopPlaybackForStartObservation(playbackStartObservation);
                        }
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!IsPlaybackStartObservationCompleted(playbackStartObservation))
                        {
                            StopAfterPlaybackStartFailure(playbackStartObservation, ex);
                        }
                        return;
                    }
                }
            }
            finally
            {
                if (!string.Equals(originalPath, bmsFile.path, StringComparison.OrdinalIgnoreCase))
                {
                    LongPathFileSystem.MoveFile(
                        bmsFile.path,
                        originalPath,
                        overwrite: false);
                    bmsFile.path = originalPath;
                }
            }
            if (playbackStartTask != null)
            {
                NotifyPlaybackStarted(generation);
            }
        }
    }

    private static bool ContainsChartTarget(ChartPackage chartPackage, ChartFile chart)
    {
        return chartPackage != null
            && chart != null
            && (chartPackage.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(chart) == true);
    }

    internal void StopIfPlayingCharts(IEnumerable<ChartFile> charts)
    {
        if (!string.IsNullOrWhiteSpace(NowPlayingBmsFile?.path))
        {
            string playingDirectory = Path.GetDirectoryName(NowPlayingBmsFile.path);
            if ((charts ?? []).Any(chart => IsChartDirectorySameOrUnder(playingDirectory, chart?.Path)))
            {
                StopPlayback(closeProcess: true);
            }
        }
    }

    internal void StopIfPlayingLibraryCharts(IEnumerable<LibraryChartRef> charts)
    {
        if (!string.IsNullOrWhiteSpace(NowPlayingBmsFile?.path))
        {
            string playingDirectory = Path.GetDirectoryName(NowPlayingBmsFile.path);
            if ((charts ?? []).Any(chart => IsChartDirectorySameOrUnder(playingDirectory, chart?.Path)))
            {
                StopPlayback(closeProcess: true);
            }
        }
    }

    internal void StopIfPlayingChartDirectories(IEnumerable<string> directories)
    {
        if (!string.IsNullOrWhiteSpace(NowPlayingBmsFile?.path)
            && (directories ?? []).Any(directory => IsChartDirectorySameOrUnder(directory, NowPlayingBmsFile.path)))
        {
            StopPlayback(closeProcess: true);
        }
    }

    private static bool IsChartDirectorySameOrUnder(string parentDirectory, string chartPath)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(chartPath))
        {
            return false;
        }
        string chartDirectory = Path.GetDirectoryName(chartPath);
        if (string.IsNullOrWhiteSpace(chartDirectory))
        {
            return false;
        }
        string normalizedParent = NormalizeDirectoryForPrefixCheck(parentDirectory);
        string normalizedChartDirectory = NormalizeDirectoryForPrefixCheck(chartDirectory);
        return string.Equals(normalizedParent, normalizedChartDirectory, StringComparison.OrdinalIgnoreCase)
            || normalizedChartDirectory.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryForPrefixCheck(string directoryPath)
    {
        try
        {
            return Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    /// <summary>譜面の準備 session を作成する。終了受付後は状態を変更しない。</summary>
    internal long BeginPlayback(BMSFile bmsFile, int rowIndex)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException(nameof(bmsFile));
        }

        long generation;
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return playbackGeneration;
            }
            ClearCurrentPlaybackStatus();
            playbackGeneration++;
            nowPlayingRowIndex = rowIndex;
            NowPlayingBmsFile = bmsFile;
            SetBmsPlayerHeader(bmsFile);
            generation = playbackGeneration;
        }
        DispatchPlaybackEvent(PlaybackStarting, generation);
        return generation;
    }

    /// <summary>利用できない候補を session から外す。終了受付後は状態を変更しない。</summary>
    internal void SkipUnavailablePlaybackCandidate(int rowIndex)
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            ClearCurrentPlaybackStatus();
            playbackGeneration++;
            NowPlayingBmsFile = null;
            nowPlayingRowIndex = rowIndex;
        }
    }

    internal void NotifyPlaybackStarted(long expectedGeneration)
    {
        DispatchPlaybackEvent(PlaybackStarted, expectedGeneration);
    }

    /// <summary>通常の停止を行う。再開可能な受付を維持し、終了受付後は terminal に解放を委ねる。</summary>
    internal void StopPlayback(bool closeProcess = false)
    {
        IBMSPlayer playerToClose;
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            playerToClose = ClearPlaybackStateWithoutExternalCall(closeProcess);
        }
        CloseCapturedPlayer(playerToClose);
    }

    private IBMSPlayer ClearPlaybackStateWithoutExternalCall(bool closeProcess)
    {
        IBMSPlayer playerToClose = closeProcess ? bmsPlayer : null;
        if (!closeProcess || playerToClose != null)
        {
            playbackGeneration++;
        }
        ClearCurrentPlaybackStatus();
        NowPlayingBmsFile = null;
        nowPlayingRowIndex = -1;
        return playerToClose;
    }

    private void CloseCapturedPlayer(IBMSPlayer player, bool forShutdown = false)
    {
        if (player == null)
        {
            return;
        }
        lock (playerOperationGate)
        {
            // capture 済みの通常停止が terminal close 後に player を再度操作しない。
            if (shutdownStarted && !forShutdown)
            {
                return;
            }
            player.CloseProcess();
        }
        DispatchToUi(() =>
        {
            lock (sessionGate)
            {
                if (ReferenceEquals(player, bmsPlayer))
                {
                    RefreshPlayerState(player);
                }
            }
        });
    }

    /// <summary>一時停止を切り替える。終了受付後は player を操作しない。</summary>
    internal void TogglePause()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            if (NowPlayingBmsFile != null)
            {
                if (IsPlaying)
                {
                    NowPlayingBmsFile.status &= ~BMSFile.BMSFileStatus.PLAYALL;
                    NowPlayingBmsFile.status |= BMSFile.BMSFileStatus.PAUSE;
                }
                else if (IsPaused)
                {
                    NowPlayingBmsFile.status &= ~BMSFile.BMSFileStatus.PLAYALL;
                    NowPlayingBmsFile.status |= BMSFile.BMSFileStatus.PLAY;
                }
                RaisePlaybackStatusPropertiesChanged();
            }
            RequirePlayer().PausePlayingBMSfileToggle();
        }
    }

    /// <summary>再生位置を先頭へ戻す。終了受付後は player を操作しない。</summary>
    internal void RestartPlayingBmsFile()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            RequirePlayer().RestartPlayingBMSfile();
        }
    }

    /// <summary>早送りを開始する。終了受付後は player を操作しない。</summary>
    internal void FastForwardStart()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            if (NowPlayingBmsFile != null)
            {
                NowPlayingBmsFile.status |= BMSFile.BMSFileStatus.FORWARD;
                RaisePlaybackStatusPropertiesChanged();
            }
            RequirePlayer().FastForwardPlayingBMSfileStart();
        }
    }

    /// <summary>早送りを終える。終了受付後は player を操作しない。</summary>
    internal void FastForwardEnd()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            if (NowPlayingBmsFile != null)
            {
                NowPlayingBmsFile.status &= ~BMSFile.BMSFileStatus.FORWARD;
                RaisePlaybackStatusPropertiesChanged();
            }
            RequirePlayer().FastForwardPlayingBMSfileEnd();
        }
    }

    /// <summary>巻き戻しを開始する。終了受付後は player を操作しない。</summary>
    internal void FastBackwardStart()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            if (NowPlayingBmsFile != null)
            {
                NowPlayingBmsFile.status |= BMSFile.BMSFileStatus.BACKWARD;
                RaisePlaybackStatusPropertiesChanged();
            }
            RequirePlayer().FastBackwardPlayingBMSfileStart();
        }
    }

    /// <summary>巻き戻しを終える。終了受付後は player を操作しない。</summary>
    internal void FastBackwardEnd()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            if (NowPlayingBmsFile != null)
            {
                NowPlayingBmsFile.status &= ~BMSFile.BMSFileStatus.BACKWARD;
                RaisePlaybackStatusPropertiesChanged();
            }
            RequirePlayer().FastBackwardPlayingBMSfileEnd();
        }
    }

    /// <summary>player の情報表示を切り替える。終了受付後は操作しない。</summary>
    internal void ShowInfo()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            RequirePlayer().ShowInfo();
        }
    }

    /// <summary>player の effect 表示を切り替える。終了受付後は操作しない。</summary>
    internal void ShowEffect()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            RequirePlayer().ShowEffect();
        }
    }

    /// <summary>player のプレイ側を切り替える。終了受付後は操作しない。</summary>
    internal void ChangePlayside()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            RequirePlayer().ChangePlayside();
        }
    }

    /// <summary>player のハイスピードを上げる。終了受付後は操作しない。</summary>
    internal void IncreaseHighSpeed()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            RequirePlayer().IncreaseHighSpeed();
        }
    }

    /// <summary>player のハイスピードを下げる。終了受付後は操作しない。</summary>
    internal void DecreaseHighSpeed()
    {
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            RequirePlayer().DecreaseHighSpeed();
        }
    }

    /// <summary>
    /// Gets the active playback title shown in the panel header.
    /// </summary>
    public string PlayerHeaderTitle => bmsPlayerHeaderTitle;

    /// <summary>
    /// Gets the active playback subtitle shown in the panel header.
    /// </summary>
    public string PlayerHeaderSubtitle => bmsPlayerHeaderSubtitle;

    /// <summary>
    /// Gets the active playback artist shown in the panel header.
    /// </summary>
    public string PlayerHeaderArtist => bmsPlayerHeaderArtist;

    /// <summary>
    /// Gets the BMS player title cache used when the BMS panel is active.
    /// </summary>
    internal string BmsPlayerHeaderTitle => bmsPlayerHeaderTitle;

    /// <summary>
    /// Gets the BMS player subtitle cache used when the BMS panel is active.
    /// </summary>
    internal string BmsPlayerHeaderSubtitle => bmsPlayerHeaderSubtitle;

    /// <summary>
    /// Gets the BMS player artist cache used when the BMS panel is active.
    /// </summary>
    internal string BmsPlayerHeaderArtist => bmsPlayerHeaderArtist;

    /// <summary>
    /// Gets or sets the internal player volume stored in application settings.
    /// </summary>
    public int PlayerVolume
    {
        get => playbackSettings.PlayerVolume;
        set
        {
            if (!shutdownStarted && playbackSettings.PlayerVolume != value)
            {
                playbackSettings.PlayerVolume = value;
                RaisePropertyChanged(nameof(PlayerVolume));
                bmsPlayer?.VolumeChanged();
            }
        }
    }

    /// <summary>
    /// Updates the BMS player header cache from the currently selected or playing BMS file.
    /// </summary>
    /// <param name="bmsFile">BMS file whose metadata should be displayed.</param>
    internal void SetBmsPlayerHeader(BMSFile bmsFile)
    {
        DisplayedBmsPlayerFile = bmsFile;
        bool changed = SetHeaderValue(ref bmsPlayerHeaderTitle, GridRowResolver.GetBmsPlayerDisplayTitle(bmsFile))
            | SetHeaderValue(ref bmsPlayerHeaderSubtitle, GridRowResolver.GetBmsPlayerDisplaySubtitle(bmsFile))
            | SetHeaderValue(ref bmsPlayerHeaderArtist, GridRowResolver.GetBmsPlayerDisplayArtist(bmsFile));
        if (changed)
        {
            RaisePlayerHeaderPropertiesChanged();
        }
    }

    private static bool SetHeaderValue(ref string storage, string value)
    {
        value ??= string.Empty;
        if (storage == value)
        {
            return false;
        }

        storage = value;
        return true;
    }

    private void RaisePlayerHeaderPropertiesChanged()
    {
        RaisePropertyChanged(nameof(PlayerHeaderTitle));
        RaisePropertyChanged(nameof(PlayerHeaderSubtitle));
        RaisePropertyChanged(nameof(PlayerHeaderArtist));
    }

    private void ClearCurrentPlaybackStatus()
    {
        if (NowPlayingBmsFile == null)
        {
            return;
        }

        NowPlayingBmsFile.status &= ~BMSFile.BMSFileStatus.PLAYALL;
        RaisePlaybackStatusPropertiesChanged();
    }

    private void RaisePlaybackStatusPropertiesChanged()
    {
        RaisePropertyChanged(nameof(IsPlaying));
        RaisePropertyChanged(nameof(IsPaused));
        RaisePropertyChanged(nameof(IsStoppedOrPaused));
    }

    private void SetPlaybackSetting(bool currentValue, bool newValue, Action<bool> assign, string propertyName)
    {
        if (currentValue != newValue)
        {
            assign(newValue);
            RaisePropertyChanged(propertyName);
        }
    }

    internal void NotifySettingsChanged()
    {
        RaisePropertyChanged(nameof(PlayerPanelState));
        RaisePropertyChanged(nameof(RepeatPlayMode));
        RaisePropertyChanged(nameof(FolderSkipPlayMode));
        RaisePropertyChanged(nameof(SinglePlayMode));
        RaisePropertyChanged(nameof(CanSeek));
        RaisePropertyChanged(nameof(CanChangeHighSpeed));
        RaisePropertyChanged(nameof(CanShowInfo));
        RaisePropertyChanged(nameof(CanShowEffect));
        RaisePropertyChanged(nameof(CanChangePlayside));
        RaisePropertyChanged(nameof(UseExternalPanelImage));
        RaisePropertyChanged(nameof(StagefilePath));
        RaisePropertyChanged(nameof(UsesUbMplay));
        RaisePropertyChanged(nameof(UsesLr2Body));
        RaisePropertyChanged(nameof(UsesBmiIdxView));
        RaisePropertyChanged(nameof(PlayerVolume));
        RaisePlayerHeaderPropertiesChanged();
    }

    Task ISettingsDialogPlaybackRuntimePort.ApplyPlayerSettingsAsync(IBMSPlayer replacementPlayer)
        => ReplacePlayerAsync(replacementPlayer, clearPlaybackStatus: true);

    void ISettingsDialogPlaybackRuntimePort.NotifySettingsChanged()
        => NotifySettingsChanged();

    void IAudioDeviceTestPlaybackPort.StopPlayback()
        => StopPlayback(closeProcess: true);

    private sealed class PlayerStateSnapshot
    {
        private PlayerStateSnapshot(
            TimeSpan duration,
            TimeSpan stopTime,
            TimeSpan bmsDuration,
            TimeSpan musicDuration,
            int currentVoices,
            int maxVoices,
            int noteDensity,
            int noteDensityMax,
            int bpm,
            int minBpm,
            int maxBpm,
            double total,
            int combo,
            int notes,
            int measure,
            int lastMeasure)
        {
            Duration = duration;
            StopTime = stopTime;
            BmsDuration = bmsDuration;
            MusicDuration = musicDuration;
            CurrentVoices = currentVoices;
            MaxVoices = maxVoices;
            NoteDensity = noteDensity;
            NoteDensityMax = noteDensityMax;
            Bpm = bpm;
            MinBpm = minBpm;
            MaxBpm = maxBpm;
            Total = total;
            Combo = combo;
            Notes = notes;
            Measure = measure;
            LastMeasure = lastMeasure;
        }

        internal TimeSpan Duration { get; }

        internal TimeSpan StopTime { get; }

        internal TimeSpan BmsDuration { get; }

        internal TimeSpan MusicDuration { get; }

        internal int CurrentVoices { get; }

        internal int MaxVoices { get; }

        internal int NoteDensity { get; }

        internal int NoteDensityMax { get; }

        internal int Bpm { get; }

        internal int MinBpm { get; }

        internal int MaxBpm { get; }

        internal double Total { get; }

        internal int Combo { get; }

        internal int Notes { get; }

        internal int Measure { get; }

        internal int LastMeasure { get; }

        internal static PlayerStateSnapshot Capture(IBMSPlayer player)
        {
            return new PlayerStateSnapshot(
                player.Duration,
                player.StopTime,
                player.BmsDuration,
                player.MusicDuration,
                player.CurrentVoices,
                player.MaxVoices,
                player.NoteDensity,
                player.NoteDensityMax,
                player.Bpm,
                player.MinBpm,
                player.MaxBpm,
                player.Total,
                player.Combo,
                player.Notes,
                player.Measure,
                player.LastMeasure);
        }
    }

    private void DispatchPlaybackEvent(EventHandler handler, long expectedGeneration)
    {
        DispatchToUi(() =>
        {
            bool isCurrent;
            lock (sessionGate)
            {
                isCurrent = !shutdownStarted && expectedGeneration == playbackGeneration && NowPlayingBmsFile != null;
            }
            if (isCurrent)
            {
                handler?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void BmsPlayerPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        DispatchToUi(() =>
        {
            if (!ReferenceEquals(sender, bmsPlayer))
            {
                return;
            }

            RefreshPlayerState(bmsPlayer, e?.PropertyName);
        });
    }

    private void ApplyPlayerState(PlayerStateSnapshot state)
    {
        CurrentlyPlayingDuration = state.Duration;
        CurrentlyPlayingStopTime = state.StopTime;
        CurrentlyPlayingBmsDuration = state.BmsDuration;
        CurrentlyPlayingMusicDuration = state.MusicDuration;
        CurrentlyPlayingCurrentVoices = state.CurrentVoices;
        CurrentlyPlayingMaxVoices = state.MaxVoices;
        CurrentlyPlayingNoteDensity = state.NoteDensity;
        CurrentlyPlayingNoteDensityMax = state.NoteDensityMax;
        CurrentlyPlayingBpm = state.Bpm;
        CurrentlyPlayingMinBpm = state.MinBpm;
        CurrentlyPlayingMaxBpm = state.MaxBpm;
        CurrentlyPlayingTotal = state.Total;
        CurrentlyPlayingCombo = state.Combo;
        CurrentlyPlayingNotes = state.Notes;
        CurrentlyPlayingMeasure = state.Measure;
        CurrentlyPlayingLastMeasure = state.LastMeasure;
    }

    private void RefreshPlayerState(IBMSPlayer player, string propertyName = null)
    {
        if (player == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.Duration))
        {
            CurrentlyPlayingDuration = player.Duration;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.CurrentTime))
        {
            RaisePropertyChanged(nameof(CurrentlyPlayingTime));
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.StopTime))
        {
            CurrentlyPlayingStopTime = player.StopTime;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.BmsDuration))
        {
            CurrentlyPlayingBmsDuration = player.BmsDuration;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.MusicDuration))
        {
            CurrentlyPlayingMusicDuration = player.MusicDuration;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.CurrentVoices))
        {
            CurrentlyPlayingCurrentVoices = player.CurrentVoices;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.MaxVoices))
        {
            CurrentlyPlayingMaxVoices = player.MaxVoices;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.NoteDensity))
        {
            CurrentlyPlayingNoteDensity = player.NoteDensity;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.NoteDensityMax))
        {
            CurrentlyPlayingNoteDensityMax = player.NoteDensityMax;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.Bpm))
        {
            CurrentlyPlayingBpm = player.Bpm;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.MinBpm))
        {
            CurrentlyPlayingMinBpm = player.MinBpm;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.MaxBpm))
        {
            CurrentlyPlayingMaxBpm = player.MaxBpm;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.Total))
        {
            CurrentlyPlayingTotal = player.Total;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.Combo))
        {
            CurrentlyPlayingCombo = player.Combo;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.Notes))
        {
            CurrentlyPlayingNotes = player.Notes;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.Measure))
        {
            CurrentlyPlayingMeasure = player.Measure;
        }
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName == nameof(IBMSPlayer.LastMeasure))
        {
            CurrentlyPlayingLastMeasure = player.LastMeasure;
        }
    }

    private void DispatchToUi(Action action)
    {
        uiDispatcher.Dispatch(action);
    }

    private IBMSPlayer RequirePlayer()
    {
        return bmsPlayer ?? throw new InvalidOperationException("PlaybackPanelViewModel player is not initialized.");
    }

    private void SetPlaybackValue<T>(ref T storage, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return;
        }

        storage = value;
        RaisePropertyChanged(propertyName);
    }
}
