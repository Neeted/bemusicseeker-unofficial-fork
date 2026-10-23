using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Livet;
using Livet.Commands;

using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the playback adapter and telemetry exposed by the playback panel.
/// </summary>
public sealed class PlaybackPanelViewModel : ViewModel,
    IChartMutationPlaybackPort,
    ISelectedChartAudioConversionPlaybackPort,
    ISettingsDialogPlaybackRuntimePort
{
    private readonly IPlaybackUiDispatcher uiDispatcher;

    private readonly IPlaybackChartQueue playbackQueue;

    private readonly IPlaybackSettingsStore playbackSettings;

    private readonly IPlaybackDialogService playbackDialogs;
    private readonly Action<Exception> reportSettingsSaveFailure;

    private readonly Action<Exception> warnInvalidChart;

    private readonly ChartFileOperationSynchronizer chartFileOperations;

    private readonly object sessionGate = new();

    private readonly SemaphoreSlim playerOperationGate = new(1, 1);

    private readonly SemaphoreSlim playerReplacementGate = new(1, 1);

    // 曲開始・変更前停止と必要な準備の合流を順序付けます。演奏全体や背景の先読み実行中は保持しません。
    private readonly SemaphoreSlim playbackInputGate = new(1, 1);

    private IBMSPlayer bmsPlayer;

    private long playbackGeneration;

    private volatile bool shutdownStarted;

    private ChartFile nowPlayingBmsFile;

    private ChartFile displayedBmsPlayerFile;

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
            ChartFile file,
            Action<object, EventArgs, long, IBMSPlayer, ChartFile> onExit)
        {
            Generation = generation;
            Player = player;
            File = file;
            OnExit = onExit;
        }

        internal long Generation { get; }

        internal IBMSPlayer Player { get; }

        internal ChartFile File { get; }

        internal Action<object, EventArgs, long, IBMSPlayer, ChartFile> OnExit { get; }

        internal bool StartCompleted { get; set; }

        internal bool StartSucceeded { get; set; }

        internal bool ExitRequested { get; set; }

        internal object ExitSender { get; set; }

        internal EventArgs ExitArgs { get; set; }

        internal bool ExitClaimed { get; set; }
    }

    /// <summary>再生操作・候補設定・通知を接続します。音量の項目保存失敗は再生失敗と分けて通知します。</summary>
    internal PlaybackPanelViewModel(
        IBMSPlayer player,
        IPlaybackUiDispatcher uiDispatcher,
        IPlaybackChartQueue playbackQueue,
        IPlaybackSettingsStore playbackSettings,
        IPlaybackDialogService playbackDialogs,
        Action<Exception> warnInvalidChart,
        ChartFileOperationSynchronizer chartFileOperations,
        Action<Exception> reportSettingsSaveFailure = null)
    {
        this.uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        this.playbackQueue = playbackQueue ?? throw new ArgumentNullException(nameof(playbackQueue));
        this.playbackSettings = playbackSettings ?? throw new ArgumentNullException(nameof(playbackSettings));
        this.playbackDialogs = playbackDialogs ?? throw new ArgumentNullException(nameof(playbackDialogs));
        this.reportSettingsSaveFailure = reportSettingsSaveFailure ?? playbackDialogs.NotifySettingsSaveFailure;
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

    private ViewModelCommand CreateBackgroundCommand(Func<Task> action, string routeName) =>
        new(() => StartBackgroundPlaybackAction(action, routeName).ObserveFault(routeName), () => !shutdownStarted);

    private Task StartBackgroundPlaybackAction(Func<Task> action, string routeName) =>
        shutdownStarted ? Task.CompletedTask : Task.Run(() => ObservePlaybackActionAsync(action, routeName));

    private async Task ObservePlaybackActionAsync(Func<Task> action, string routeName)
    {
        if (shutdownStarted) { return; }
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Task.FromException(exception).ObserveFault(routeName);
            NotifyPlaybackFailureSafely(exception);
        }
    }

    private ViewModelCommand CreateBackgroundCommand(Action action, string routeName) =>
        new(() => StartBackgroundPlaybackAction(action, routeName).ObserveFault(routeName), () => !shutdownStarted);

    private Task StartBackgroundPlaybackAction(Action action, string routeName) =>
        StartBackgroundPlaybackAction(() =>
        {
            action();
            return Task.CompletedTask;
        }, routeName);

    private NextPlaybackPlayerRequest nextPlayerRequest;

    /// <summary>同じ保存済み設定から捕捉したfactoryと形式能力を、実置換成功まで保持します。</summary>
    private sealed record NextPlaybackPlayerRequest(Func<IBMSPlayer> CreatePlayer, bool SupportsBmson);

    /// <summary>試聴同士の受理済み開始は順次実行し、推定・変更中の新しい開始は副作用前に拒否します。</summary>
    private async Task RunPlaybackSelectionAsync(Func<Task> selectAndStart, bool stopIfBusy = false,
        (long Generation, IBMSPlayer Player, ChartFile File)? naturalExit = null)
    {
        // 変更中の入力を予約して後から再生しません。受付済みの開始は変更側の停止が待ちます。
        if (shutdownStarted) { return; }
        if (!naturalExit.HasValue && chartFileOperations.IsActive)
        {
            if (stopIfBusy) { await StopPlayback(closeProcess: true).ConfigureAwait(false); }
            return;
        }
        await playbackInputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (shutdownStarted) { return; }
            if (naturalExit is { } acceptedExit)
            {
                lock (sessionGate)
                {
                    if (acceptedExit.Generation != playbackGeneration
                        || !ReferenceEquals(acceptedExit.Player, bmsPlayer)
                        || !ReferenceEquals(acceptedExit.File, NowPlayingChart)) { return; }
                }
            }
            if (chartFileOperations.IsActive)
            {
                if (naturalExit is { } busyExit)
                {
                    await TryStopPlaybackForGeneration(busyExit.Generation, busyExit.File).ConfigureAwait(false);
                }
                else if (stopIfBusy) { await StopPlayback(closeProcess: true).ConfigureAwait(false); }
                return;
            }
            long previousGeneration;
            lock (sessionGate) { previousGeneration = playbackGeneration; }
            await selectAndStart().ConfigureAwait(false);
            bool started;
            lock (sessionGate) { started = previousGeneration != playbackGeneration; }
            if (started)
            {
                await PrepareNextSongAsync().ConfigureAwait(false);
            }
        }
        finally { playbackInputGate.Release(); }
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
        CompleteVolumeAdjustment();
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
    /// 受付を閉じた後、先行する曲開始・停止へ合流し、現在曲と先読みを解放します。workerから呼びます。
    /// </summary>
    internal async Task CloseForShutdown()
    {
        await playbackInputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseProcessCore(forShutdown: true).ConfigureAwait(false);
        }
        finally { playbackInputGate.Release(); }
    }

    /// <summary>
    /// 一時導入試聴が参照するライブラリを接続します。再構築時の交換は、旧・新ライブラリに共通する
    /// 生存中の論理受付権限の下で行い、再生セッションやプレイヤーの交換は行いません。
    /// </summary>
    /// <param name="library">接続する非nullのライブラリ。</param>
    /// <param name="capability">受理済み再構築の共通受付権限。初回または同じライブラリの接続では省略できます。</param>
    internal void AttachLibrary(BMSLibrary library, LibraryFileMutationCapability capability = null)
    {
        if (library == null)
        {
            throw new ArgumentNullException(nameof(library));
        }
        capability?.Validate(library.OperationAdmission);
        if (this.library != null && !ReferenceEquals(this.library, library))
        {
            if (capability == null)
            {
                throw new InvalidOperationException("Playback library is already attached.");
            }
            capability.Validate(this.library.OperationAdmission);
        }
        this.library = library;
    }

    Task IChartMutationPlaybackPort.StopPlaybackForMutationAsync() => StopPlaybackForMutationAsync();

    Task ISelectedChartAudioConversionPlaybackPort.StopPlayback()
    {
        return StopPlayback(closeProcess: true);
    }

    internal event EventHandler PlaybackStarting;

    internal event EventHandler PlaybackStarted;

    internal void HandleTableSelection(object row)
    {
        if (NowPlayingChart == null && GridRowResolver.TryGetPlaybackChart(row, out ChartFile bmsFile))
        {
            SetBmsPlayerHeader(bmsFile);
        }
    }

    /// <summary>次回要求または現在playerの形式能力で明示行を検査して要求を投入し、入力時に停止・一時停止なら受付後に面選択を一度投稿します。競合・形式拒否では投稿しません。</summary>
    /// <returns>既存のbackground再生要求の実終端。UIへの面要求の配送完了は待ちません。</returns>
    internal Task HandleTableRowActivation(int rowIndex, object row, Action requestPlayerSurface = null)
    {
        if (shutdownStarted || chartFileOperations.IsActive || rowIndex < 0
            || rowIndex >= playbackQueue.Count
            || !GridRowResolver.TryGetPlaybackChart(row, out ChartFile bmsFile)
            || (bmsFile.Kind == ChartFileKind.Bmson && !SupportsBmsonForNextPlayback()))
        {
            return Task.CompletedTask;
        }

        Action acceptedSurfaceRequest = IsStoppedOrPaused ? requestPlayerSurface : null;
        return StartBackgroundPlaybackAction(
            () => ExecuteTableRowActivation(rowIndex, row, acceptedSurfaceRequest),
            "PlaybackPanel.ActivateRow");
    }

    /// <summary>受付後に同一行と次回要求の形式能力を再検査して再生します。拒否した明示行を別行へ読み替えず、終了受付後の待機済み要求は破棄します。</summary>
    internal Task ExecuteTableRowActivation(int rowIndex, object expectedRow, Action acceptedSurfaceRequest = null) =>
        RunPlaybackSelectionAsync(async () =>
        {
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

            await StartAtIndex(rowIndex, currentRow, useInitialRow: true, acceptedSurfaceRequest).ConfigureAwait(false);
        });

    /// <summary>
    /// 準備または再生中の現在譜面です。譜面情報を変更せず、再生状態の対象として保持します。
    /// </summary>
#nullable enable annotations
    internal ChartFile? NowPlayingChart
    {
        get => nowPlayingBmsFile;
        private set
        {
            if (ReferenceEquals(nowPlayingBmsFile, value))
            {
                return;
            }

            nowPlayingBmsFile = value;
            PlaybackStatusChanged?.Invoke();
            RaisePropertyChanged(nameof(NowPlayingChart));
            RaisePropertyChanged(nameof(HasNowPlayingChart));
            RaisePlaybackStatusPropertiesChanged();
        }
    }

    /// <summary>
    /// ヘッダーと表示素材の対象譜面です。対象未設定時はnullです。
    /// </summary>
    internal ChartFile? DisplayedChart
    {
        get => displayedBmsPlayerFile;
        private set
        {
            if (!ReferenceEquals(displayedBmsPlayerFile, value))
            {
                displayedBmsPlayerFile = value;
                RaisePropertyChanged(nameof(DisplayedChart));
            }
        }
    }

#nullable restore annotations

    private ChartFileStatus playbackStatus;

    /// <summary>ボタンbinding用の現行再生bitです。保存・検索・score状態を混在させません。</summary>
    public int PlaybackStatusValue => (int)playbackStatus;

    private ChartFileStatus PlaybackStatus
    {
        get => playbackStatus;
        set
        {
            playbackStatus = value & ChartFileStatus.PLAYALL;
            PlaybackStatusChanged?.Invoke();
            RaisePropertyChanged(nameof(PlaybackStatusValue));
        }
    }

    /// <summary>対象・再生状態の変化を一覧の表示再評価へ渡します。譜面情報と行には状態を保存しません。</summary>
    internal event Action PlaybackStatusChanged;

    /// <summary>種類と実pathで現在の対象を照合し、行のstatus読取りへ再生bitだけを渡します。nullはNONEです。</summary>
#nullable enable annotations
    internal ChartFileStatus GetPlaybackStatus(ChartFile? chart)
    {
        lock (sessionGate)
        {
            return chart != null && NowPlayingChart != null && chart.Kind == NowPlayingChart.Kind
                && string.Equals(chart.Path, NowPlayingChart.Path, StringComparison.OrdinalIgnoreCase)
                ? playbackStatus : ChartFileStatus.NONE;
        }
    }
#nullable restore annotations

    /// <summary>外部プレーヤー表示のbindingへ現在の再生対象の有無を公開します。</summary>
    public bool HasNowPlayingChart => NowPlayingChart != null;

    public bool IsPlaying => NowPlayingChart != null && playbackStatus.HasFlag(ChartFileStatus.PLAY);

    public bool IsPaused => NowPlayingChart != null && playbackStatus.HasFlag(ChartFileStatus.PAUSE);

    public bool IsStoppedOrPaused => NowPlayingChart == null || IsPaused;

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

    /// <summary>現在playerがseek操作を扱えるかを取得します。</summary>
    public bool CanSeek => !UsesLr2Body;

    /// <summary>現在playerがハイスピード変更を扱えるかを取得します。</summary>
    public bool CanChangeHighSpeed => UsesUbMplay || UsesBmiIdxView;

    /// <summary>現在playerが譜面情報表示を扱えるかを取得します。</summary>
    public bool CanShowInfo => !UsesLr2Body && !UsesBmiIdxView;

    /// <summary>現在playerがエフェクト表示を扱えるかを取得します。</summary>
    public bool CanShowEffect => UsesUbMplay;

    /// <summary>現在playerがプレイサイド変更を扱えるかを取得します。</summary>
    public bool CanChangePlayside => UsesUbMplay;

    public bool UseExternalPanelImage => playbackSettings.UseExternalPanelImage;

    public string StagefilePath => playbackSettings.StagefilePath;

    /// <summary>現在接続済みのplayerがuBMplayであるかを取得します。</summary>
    internal bool UsesUbMplay => bmsPlayer is uBMplay;

    /// <summary>現在接続済みのplayerがLR2bodyであるかを取得します。</summary>
    internal bool UsesLr2Body => bmsPlayer is LR2body;

    /// <summary>現在接続済みのplayerがBMIIDXViewであるかを取得します。</summary>
    internal bool UsesBmiIdxView => bmsPlayer is BMIIDXView2015;

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
        var preparedState = PlayerStateSnapshot.Capture(player);
        player.PropertyChanged += BmsPlayerPropertyChanged;
        bmsPlayer = player;
        ApplyPlayerState(preparedState);
        RaisePropertyChanged(nameof(CurrentlyPlayingTime));
    }

    /// <summary>既存playerの停止と置換を実終端まで待ちます。必須起動はhost接続を親受付解放後の画面境界へ委ねます。</summary>
    /// <param name="player">新しいplayer。</param>
    /// <param name="clearPlaybackStatus">置換後に既存の再生状態を消去する場合は真。</param>
    /// <param name="deferWindowHostAttachment">必須起動の実host接続を受付外で行う場合は真。</param>
    internal async Task ReplacePlayerAsync(IBMSPlayer player, bool clearPlaybackStatus = false, bool deferWindowHostAttachment = false)
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

            var preparedState = PlayerStateSnapshot.Capture(player);
            if (!deferWindowHostAttachment && currentWindowHost != null && player is IExternalWindowPlayer externalWindowPlayer)
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
            await Task.Run(async () =>
            {
                await playerOperationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (previousPlayer is INextSongPreloadPlayer preloadPlayer) { await preloadPlayer.CloseAsync().ConfigureAwait(false); }
                    else { previousPlayer?.CloseProcess(); }
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
                finally { playerOperationGate.Release(); }
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
                    NowPlayingChart = null;
                    nowPlayingRowIndex = -1;
                }
                ApplyPlayerState(preparedState);
                RaisePropertyChanged(nameof(CurrentlyPlayingTime));
                NotifyPlayerCapabilitiesChanged();
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
    internal Task CloseProcess() => CloseProcessCore(forShutdown: false);

    private async Task CloseProcessCore(bool forShutdown)
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
        await CloseCapturedPlayer(player, forShutdown).ConfigureAwait(false);
    }

    /// <summary>現行 session の player 開始を試みる。終了受付後や失効済みの要求は false を返す。</summary>
    internal Task<bool> TryPlayStart(long expectedGeneration, string bmsFilePath, Action<object, EventArgs> onExitEventHandler)
    {
        Task<bool> playStartTask = TryPlayStart(
            expectedGeneration,
            bmsFilePath,
            onExitEventHandler == null ? null : (sender, args, _, _, _) => onExitEventHandler(sender, args),
            out PlaybackStartObservation observation,
            waitForLegacyCompletion: true);
        return playStartTask;
    }

    private Task<bool> TryPlayStart(
        long expectedGeneration,
        string bmsFilePath,
        Action<object, EventArgs, long, IBMSPlayer, ChartFile> onExitEventHandler,
        out PlaybackStartObservation observation,
        bool waitForLegacyCompletion = false,
        bool allowPreload = true)
    {
        IBMSPlayer player;
        ChartFile file;
        PlaybackStartObservation currentObservation;
        lock (sessionGate)
        {
            player = RequirePlayer();
            file = NowPlayingChart;
            if (shutdownStarted || file == null || expectedGeneration != playbackGeneration)
            {
                observation = null;
                return Task.FromResult(false);
            }

            PlaybackStatus |= ChartFileStatus.LOADING;
            RaisePlaybackStatusPropertiesChanged();

            currentObservation = new PlaybackStartObservation(
                expectedGeneration,
                player,
                file,
                onExitEventHandler);
            observation = currentObservation;
        }

        if (player is INextSongPreloadPlayer preloadPlayer)
        {
            return StartWithReadyAsync(preloadPlayer, bmsFilePath, currentObservation, allowPreload);
        }

        return StartLegacyAsync(bmsFilePath, currentObservation, waitForLegacyCompletion);
    }

    private async Task<bool> StartLegacyAsync(string bmsFilePath, PlaybackStartObservation currentObservation,
        bool waitForCompletion)
    {
        IBMSPlayer player = currentObservation.Player;
        ChartFile file = currentObservation.File;
        long expectedGeneration = currentObservation.Generation;
        Task playStartTask;
        await playerOperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sessionGate)
            {
                if (!IsCurrentPlaybackObservation(currentObservation))
                {
                    return false;
                }
            }
            playStartTask = player.PlayStart(
                bmsFilePath,
                (sender, e) => HandlePlaybackExit(currentObservation, sender, e));
        }
        finally { playerOperationGate.Release(); }
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
                && ReferenceEquals(file, NowPlayingChart))
            {
                PlaybackStatus &= ~ChartFileStatus.LOADING;
                PlaybackStatus |= ChartFileStatus.PLAY;
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
            return await AwaitPlaybackCompletionAsync(playStartTask).ConfigureAwait(false);
        }
        if (observedTask != null)
        {
            TrackPlaybackStart(observedTask);
            // 選曲側は操作待機とPlayStartの同期呼出しまでを開始境界とします。
            // 返却Taskの終了まで一時コピーや選曲の受付を保持せず、終了・故障の観測は継続します。
            if (!waitForCompletion)
            {
                return true;
            }
            return await observedTask.ConfigureAwait(false);
        }
        throw new InvalidOperationException("Playback start observation was not created.");
    }

    private async Task<bool> StartWithReadyAsync(INextSongPreloadPlayer player, string path,
        PlaybackStartObservation observation, bool allowPreload)
    {
        await playerOperationGate.WaitAsync().ConfigureAwait(false);
        PlaybackStartOperation operation;
        try
        {
            lock (sessionGate)
            {
                if (!IsCurrentPlaybackObservation(observation)) { return false; }
            }
            operation = player.BeginStart(path,
                (sender, args) => HandlePlaybackExit(observation, sender, args), allowPreload,
                failure => HandlePreloadFailure(observation.Generation, observation.Player, observation.File, failure));
            // 開始失敗はReady側で扱い、演奏中の失敗だけを後続のCompletion観測へ渡します。
            operation.Completion.ObserveFault("PlaybackPanel.InternalCompletion");
            await operation.Ready.ConfigureAwait(false);
            lock (sessionGate)
            {
                if (!IsCurrentPlaybackObservation(observation)) { return false; }
                PlaybackStatus &= ~ChartFileStatus.LOADING;
                PlaybackStatus |= ChartFileStatus.PLAY;
                RaisePlaybackStatusPropertiesChanged();
            }
        }
        finally { playerOperationGate.Release(); }
        TrackPlaybackStart(ObservePlaybackStartAsync(operation.Completion, observation, notifyFailure: false));
        return true;
    }

    private void OnNaturalPlaybackExit(object sender, EventArgs args, long generation, IBMSPlayer player, ChartFile file) =>
        ObservePlaybackActionAsync(() => NextCore(sender, args, (generation, player, file)), "PlaybackPanel.NaturalNext")
            .ObserveFault("PlaybackPanel.NaturalNext");

    private static bool NeedsTemporaryInstall(ChartFile chart) =>
        !string.IsNullOrWhiteSpace(chart?.InstallDestination)
        && LongPathFileSystem.DirectoryExists(chart.InstallDestination);

    private async Task PrepareNextSongAsync()
    {
        IBMSPlayer player;
        ChartFile currentFile;
        int currentIndex;
        long generation;
        PlaybackModeSnapshot modes;
        lock (sessionGate)
        {
            player = bmsPlayer;
            currentFile = NowPlayingChart;
            currentIndex = NowPlayingRowIndex;
            generation = playbackGeneration;
            if (shutdownStarted || currentFile == null || (!IsPlaying && !IsPaused)) { return; }
            modes = new PlaybackModeSnapshot(playbackSettings.RepeatPlay, playbackSettings.FolderSkipPlay);
        }
        if (player is not INextSongPreloadPlayer preloadPlayer) { return; }
        int index = FindNextPlaybackIndex(currentIndex, currentFile, modes);
        (int Index, ChartFile File, ChartFile Chart) candidate = FindPlayableChart(index, modes.RepeatPlay, player.SupportsBmson);
        if (candidate.File == null) { return; }
        ChartFile chart = candidate.Chart ?? candidate.File;
        if (NeedsTemporaryInstall(chart)) { return; }
        NextSongPreloadInput input;
        try { input = NextSongPreloadInput.Capture(candidate.File.Path); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return; }

        await playerOperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sessionGate)
            {
                if (shutdownStarted || generation != playbackGeneration
                    || !ReferenceEquals(currentFile, NowPlayingChart) || !ReferenceEquals(player, bmsPlayer)) { return; }
            }
            await preloadPlayer.PrepareNextAsync(input,
                failure => HandlePreloadFailure(generation, player, currentFile, failure)).ConfigureAwait(false);
        }
        finally { playerOperationGate.Release(); }
    }

    private void HandlePreloadFailure(long generation, IBMSPlayer player, ChartFile file, Exception failure)
    {
        // 内蔵playerが準備と現在曲の停止を終えた通知です。遅い通知で新しい曲を停止しません。
        lock (sessionGate)
        {
            if (!shutdownStarted && generation == playbackGeneration
                && ReferenceEquals(player, bmsPlayer) && ReferenceEquals(file, NowPlayingChart))
            {
                ClearPlaybackStateWithoutExternalCall(closeProcess: false);
            }
        }
        // 別の明示再生が先に始まっていても、回収済みの故障自体は隠しません。
        NotifyPlaybackFailureSafely(failure);
    }

    private static async Task<bool> AwaitPlaybackCompletionAsync(Task playStartTask)
    {
        await playStartTask;
        return true;
    }

    private async Task<bool> ObservePlaybackStartAsync(
        Task playStartTask,
        PlaybackStartObservation observation,
        bool notifyFailure = true)
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
            CompletePlaybackStartObservation(observation, succeeded: false, failure: notifyFailure ? exception : null);
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
        Action<object, EventArgs, long, IBMSPlayer, ChartFile> exitCallback = null;
        long exitGeneration = 0;
        object exitSender = null;
        EventArgs exitArgs = null;
        bool stoppedCurrentPlayback = false;
        lock (sessionGate)
        {
            observation.StartCompleted = true;
            observation.StartSucceeded = succeeded;
            if (IsCurrentPlaybackObservation(observation))
            {
                if (!succeeded)
                {
                    ClearPlaybackStateWithoutExternalCall(closeProcess: false);
                    stoppedCurrentPlayback = true;
                }
                else if (observation.ExitRequested && !observation.ExitClaimed)
                {
                    observation.ExitClaimed = true;
                    exitGeneration = ++playbackGeneration;
                    exitCallback = observation.OnExit;
                    exitSender = observation.ExitSender;
                    exitArgs = observation.ExitArgs;
                }
            }
        }
        if (failure != null && (stoppedCurrentPlayback || PlaybackExitFailureException.RequiresNotification(failure)))
        {
            NotifyPlaybackFailureSafely(failure);
        }
        if (exitCallback != null)
        {
            InvokePlaybackExitCallback(exitCallback, exitSender, exitArgs, exitGeneration, observation);
        }
    }

    private void HandlePlaybackExit(PlaybackStartObservation observation, object sender, EventArgs e)
    {
        if (e is PlaybackExitEventArgs failedExit)
        {
            if (failedExit.FailureNotificationOwnedByCaller) { return; }
            lock (sessionGate)
            {
                if (observation.ExitClaimed) { return; }
                observation.ExitClaimed = true;
                if (IsCurrentPlaybackObservation(observation))
                {
                    ClearPlaybackStateWithoutExternalCall(closeProcess: false);
                }
            }
            // 古い世代の表示は消しませんが、復元の実故障は隠しません。
            NotifyPlaybackFailureSafely(failedExit.Failure);
            return;
        }
        Action<object, EventArgs, long, IBMSPlayer, ChartFile> exitCallback = null;
        long exitGeneration = 0;
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
            exitGeneration = ++playbackGeneration;
            exitCallback = observation.OnExit;
        }
        if (exitCallback != null)
        {
            InvokePlaybackExitCallback(exitCallback, sender, e, exitGeneration, observation);
        }
    }

    private bool IsCurrentPlaybackObservation(PlaybackStartObservation observation)
    {
        return !shutdownStarted && observation != null
            && observation.Generation == playbackGeneration
            && ReferenceEquals(observation.Player, bmsPlayer)
            && ReferenceEquals(observation.File, NowPlayingChart);
    }

    private void InvokePlaybackExitCallback(
        Action<object, EventArgs, long, IBMSPlayer, ChartFile> exitCallback,
        object sender,
        EventArgs e,
        long generation,
        PlaybackStartObservation observation)
    {
        try
        {
            exitCallback(sender, e, generation, observation.Player, observation.File);
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
        if (IsPlaybackStartObservationCompleted(observation)) { return; }
        bool clearedCurrentPlayback = TryClearPlaybackForStartObservation(observation);
        // 内蔵playerが引き取った準備Taskのfatalは、Stopによる表示失効後も開始側が報告します。
        // 自然終了から移譲された復元故障も通知しますが、旧世代の表示更新や通常の外部開始・入力不良の遅い通知には広げません。
        if (clearedCurrentPlayback
            || PlaybackExitFailureException.RequiresNotification(failure)
            || (observation?.Player is INextSongPreloadPlayer && !NextSongPreloadOwner.IsInputFailure(failure)))
        {
            NotifyPlaybackFailureSafely(failure);
        }
    }

    private bool TryClearPlaybackForStartObservation(PlaybackStartObservation observation)
    {
        lock (sessionGate)
        {
            if (shutdownStarted || (observation != null && !IsCurrentPlaybackObservation(observation)))
            {
                return false;
            }
            ClearPlaybackStateWithoutExternalCall(closeProcess: false);
        }
        return true;
    }

    private async Task<bool> TryStopPlaybackForGeneration(long generation, ChartFile file)
    {
        IBMSPlayer playerToClose;
        lock (sessionGate)
        {
            if (shutdownStarted || generation != playbackGeneration || !ReferenceEquals(file, NowPlayingChart))
            {
                return false;
            }
            playerToClose = ClearPlaybackStateWithoutExternalCall(closeProcess: true);
        }
        await CloseCapturedPlayer(playerToClose).ConfigureAwait(false);
        return true;
    }

    private void NotifyPlaybackFailureSafely(Exception failure)
    {
        try { NLogWrapper.FileLogger?.Warn(failure, "playback_failed"); }
        catch { /* 診断故障は元原因の通知と再生結果を変更しません。 */ }
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
    internal Task Start(bool forceNewPlay = true, Action requestPlayerSurface = null) =>
        StartCore(forceNewPlay, IsStoppedOrPaused ? requestPlayerSurface : null);

    /// <summary>再生ボタンの入力時条件を保持して既存選曲へ渡し、故障を既存の通知経路で終端します。</summary>
    /// <returns>既存のbackground再生要求の実終端。UIへの面要求の配送完了は待ちません。</returns>
    internal Task HandlePlayButton(Action requestPlayerSurface)
    {
        Action acceptedSurfaceRequest = IsStoppedOrPaused ? requestPlayerSurface : null;
        return StartBackgroundPlaybackAction(() => StartCore(false, acceptedSurfaceRequest), "PlaybackPanel.PlayButton");
    }

    private Task StartCore(bool forceNewPlay, Action acceptedSurfaceRequest) =>
        RunPlaybackSelectionAsync(async () =>
        {
            if (!forceNewPlay && NowPlayingChart != null)
            {
                PostPlayerSurfaceRequest(acceptedSurfaceRequest);
                TogglePause();
                return;
            }

            if (playbackQueue.SelectedIndex >= 0 && playbackQueue.SelectedIndex < playbackQueue.Count)
            {
                await StartAtIndex(playbackQueue.SelectedIndex, null, false, acceptedSurfaceRequest).ConfigureAwait(false);
            }
            else
            {
                // 競合と無関係な空選択でも、入力時の面選択意図を維持します。
                PostPlayerSurfaceRequest(acceptedSurfaceRequest);
                lock (sessionGate) { ClearPlaybackStateWithoutExternalCall(closeProcess: false); }
            }
        });

    /// <summary>次の有効な譜面へ進む。終了受付後の入力と自動送りは操作しない。</summary>
    internal Task Next(object sender = null, EventArgs e = null) => NextCore(sender, e);

    private Task NextCore(object sender, EventArgs e,
        (long Generation, IBMSPlayer Player, ChartFile File)? naturalExit = null) =>
        RunPlaybackSelectionAsync(async () =>
        {
            int index = NowPlayingRowIndex;
            if (index < 0 || index >= playbackQueue.Count)
            {
                await StopPlayback(closeProcess: true).ConfigureAwait(false);
                return;
            }
            if (sender != null && playbackSettings.SinglePlay)
            {
                if (!playbackSettings.RepeatPlay)
                {
                    await StopPlayback(closeProcess: true).ConfigureAwait(false);
                    return;
                }
            }
            else { index = FindNextPlaybackIndex(); }
            if (index < playbackQueue.Count)
            {
                await StartAtIndex(index, null, false).ConfigureAwait(false);
            }
            else if (sender != null)
            {
                await StopPlayback(closeProcess: true).ConfigureAwait(false);
            }
        }, stopIfBusy: sender != null, naturalExit: naturalExit);

    /// <summary>前の有効な譜面へ戻る。終了受付後は操作しない。</summary>
    internal Task Previous(object sender = null, EventArgs e = null) =>
        RunPlaybackSelectionAsync(async () =>
        {
            int index = NowPlayingRowIndex;
            if (index < 0 || index >= playbackQueue.Count)
            {
                await StopPlayback(closeProcess: true).ConfigureAwait(false);
                return;
            }
            if (sender != null && playbackSettings.SinglePlay)
            {
                if (!playbackSettings.RepeatPlay)
                {
                    await StopPlayback(closeProcess: true).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                string currentFolder = GetPlaybackFolderIdentity(NowPlayingChart, index);
                index--;
                if (playbackSettings.RepeatPlay && index == -1)
                {
                    index = playbackQueue.Count - 1;
                }
                while (playbackSettings.FolderSkipPlay && index != -1)
                {
                    ChartFile candidate = playbackQueue.GetPlaybackChart(index);
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
                await StartAtIndex(index, null, false).ConfigureAwait(false);
            }
            else if (sender != null)
            {
                await StopPlayback(closeProcess: true).ConfigureAwait(false);
            }
        });

    private static string GetPlaybackFolderIdentity(ChartFile file, int fallbackIndex)
    {
        return !string.IsNullOrWhiteSpace(file?.Path) && LongPathFileSystem.FileExists(file.Path)
            ? Path.GetDirectoryName(file.Path)
            : fallbackIndex.ToString();
    }

    private int FindNextPlaybackIndex()
    {
        int index;
        ChartFile file;
        PlaybackModeSnapshot modes;
        lock (sessionGate)
        {
            index = NowPlayingRowIndex;
            file = NowPlayingChart;
            modes = new PlaybackModeSnapshot(playbackSettings.RepeatPlay, playbackSettings.FolderSkipPlay);
        }
        return FindNextPlaybackIndex(index, file, modes);
    }

    private readonly record struct PlaybackModeSnapshot(bool RepeatPlay, bool FolderSkipPlay);

    private int FindNextPlaybackIndex(int currentIndex, ChartFile currentFile, PlaybackModeSnapshot modes)
    {
        // 行交換は候補探索と並行します。折返しの件数を途中で変えると、
        // 縮小後の一覧を巡回しても開始時のcurrentIndexへ戻れなくなります。
        int count = playbackQueue.Count;
        if (currentIndex < 0 || currentIndex >= count) { return count; }
        int index = currentIndex;
        string currentFolder = GetPlaybackFolderIdentity(currentFile, index);
        index++;
        if (modes.RepeatPlay && index == count)
        {
            index = 0;
        }
        while (modes.FolderSkipPlay && index >= 0 && index < count)
        {
            ChartFile candidate = playbackQueue.GetPlaybackChart(index);
            if (index == currentIndex)
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
            if (modes.RepeatPlay && index == count)
            {
                index = 0;
            }
        }
        return index;
    }

    private (int Index, ChartFile File, ChartFile Chart) FindPlayableChart(
        int index, bool repeat, bool supportsBmson, object initialRow = null, bool useInitialRow = false)
    {
        int count = playbackQueue.Count;
        for (int remaining = count; remaining > 0 && index >= 0 && index < count; remaining--)
        {
            ChartFile file;
            ChartFile chart;
            if (useInitialRow)
            {
                GridRowResolver.TryGetPlaybackChart(initialRow, out file);
                chart = file;
                useInitialRow = false;
            }
            else { file = chart = playbackQueue.GetPlaybackChart(index); }
            if (file != null && (file.Kind != ChartFileKind.Bmson || supportsBmson)
                && !string.IsNullOrWhiteSpace(file.Path) && LongPathFileSystem.FileExists(file.Path))
            {
                return (index, file, chart);
            }
            index++;
            if (repeat && index == count) { index = 0; }
        }
        return (count, null, null);
    }

    /// <summary>曲選択の受付内で次回player要求と形式能力を一回捕捉し、その能力で指定行以降の候補を再生します。</summary>
    internal Task StartAtIndex(int index) =>
        RunPlaybackSelectionAsync(() => StartAtIndex(index, initialRow: null, useInitialRow: false));

    private async Task StartAtIndex(int index, object initialRow, bool useInitialRow, Action acceptedSurfaceRequest = null)
    {
        int remainingCandidates = playbackQueue.Count;
        NextPlaybackPlayerRequest request;
        bool supportsBmson;
        lock (sessionGate)
        {
            request = nextPlayerRequest;
            supportsBmson = request?.SupportsBmson ?? RequirePlayer().SupportsBmson;
        }
        if (useInitialRow && GridRowResolver.TryGetPlaybackChart(initialRow, out ChartFile explicitTarget)
            && explicitTarget.Kind == ChartFileKind.Bmson && !supportsBmson) { return; }
    ResolveCandidate:
        if (shutdownStarted) { return; }
        (int Index, ChartFile File, ChartFile Chart) candidate = FindPlayableChart(index, playbackSettings.RepeatPlay, supportsBmson, initialRow, useInitialRow);
        useInitialRow = false;
        if (candidate.File == null)
        {
            await StopPlayback(closeProcess: true).ConfigureAwait(false);
            return;
        }
        index = candidate.Index;
        ChartFile bmsFile = candidate.File;
        ChartFile playbackChart = candidate.Chart;

        playbackChart ??= bmsFile;
        if (NeedsTemporaryInstall(playbackChart))
        {
            // 候補の解決後に別の導入入口が先取りできるため、状態変更前に非待機取得します。
            if (!chartFileOperations.TryEnter(out IDisposable operationGate)) { return; }
            await StartTemporarilyInstalledChart(playbackChart, bmsFile, index,
                playbackChart.InstallDestination, operationGate, request, acceptedSurfaceRequest).ConfigureAwait(false);
            return;
        }
        try { await ApplyPlayerForNextPlaybackAsync(request).ConfigureAwait(false); }
        finally { PostPlayerSurfaceRequest(acceptedSurfaceRequest); }
        acceptedSurfaceRequest = null;
        request = null;
        long generation = BeginPlayback(bmsFile, index);
        if (shutdownStarted) { return; }
        playbackQueue.SelectedIndex = index;

        PlaybackStartObservation observation = null;
        try
        {
            Task<bool> playStartTask = TryPlayStart(
                generation,
                bmsFile.Path,
                OnNaturalPlaybackExit,
                out observation);
            if (!await playStartTask.ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsPlaybackStartObservationCompleted(observation))
            {
                TryClearPlaybackForStartObservation(observation);
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
                TryClearPlaybackForStartObservation(observation);
                return;
            }
            remainingCandidates--;
            if (remainingCandidates <= 0)
            {
                TryClearPlaybackForStartObservation(observation);
                return;
            }
            int nextIndex = FindNextPlaybackIndex();
            if (nextIndex < 0 || nextIndex >= playbackQueue.Count)
            {
                TryClearPlaybackForStartObservation(observation);
                return;
            }
            index = nextIndex;
            goto ResolveCandidate;
        }
        catch (Exception ex)
        {
            StopAfterPlaybackStartFailure(observation, ex);
            return;
        }
        NotifyPlaybackStarted(generation);
    }

    /// <summary>パッケージ内の相対配置を保った一時素材で開始し、既存のReadyまたは外部開始境界と保持時間の後に所有物を回収して元の譜面名を復元します。残留は受付解放後に一度だけ通知します。</summary>
    private async Task StartTemporarilyInstalledChart(
        ChartFile playbackChart,
        ChartFile bmsFile,
        int index,
        string installDestination,
        IDisposable operationGate,
        NextPlaybackPlayerRequest request,
        Action acceptedSurfaceRequest)
    {
        long generation = 0;
        Exception primaryFailure = null;
        var temporaryFiles = new temporarilyCopyFiles(wait: 2000);
        try
        {
            using (operationGate)
            {
                try
                {
                    if (library == null)
                    {
                        throw new InvalidOperationException("Playback library must be attached before temporary-install playback.");
                    }
                    await ApplyPlayerForNextPlaybackAsync(request).ConfigureAwait(false);
                }
                finally { PostPlayerSurfaceRequest(acceptedSurfaceRequest); }
                generation = BeginPlayback(bmsFile, index);
                if (shutdownStarted) { return; }
                playbackQueue.SelectedIndex = index;
                ChartPackage chartPackage = library.ChartPackagesPending
                .FirstOrDefault(package => ContainsChartTarget(package, playbackChart));
                if (chartPackage == null)
                {
                    if (UsesLr2Body
                        && playbackSettings.UsesLr2Database
                        && !playbackDialogs.ConfirmTemporaryInstallPlayback())
                    {
                        await TryStopPlaybackForGeneration(generation, bmsFile).ConfigureAwait(false);
                        return;
                    }
                    chartPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(playbackChart)]);
                    chartPackage.delete_parent = false;
                }

                string originalPath = bmsFile.Path;
                string workingPath = originalPath;
                bool directoryPackage = LongPathFileSystem.DirectoryExists(chartPackage.path);
                string sourceRoot = directoryPackage ? chartPackage.path : Path.GetDirectoryName(originalPath);
                Task<bool> playbackStartTask = null;
                PlaybackStartObservation playbackStartObservation = null;
                try
                {
                    while (LongPathFileSystem.EntryExists(Path.Combine(installDestination, Path.GetRelativePath(sourceRoot, workingPath))))
                    {
                        string temporaryName = Path.GetFileNameWithoutExtension(workingPath) + "_" + Path.GetExtension(workingPath);
                        LongPathFileSystem.MoveFile(
                            workingPath,
                            Path.Combine(Path.GetDirectoryName(workingPath), temporaryName),
                            overwrite: false);
                        workingPath = Path.Combine(Path.GetDirectoryName(workingPath), temporaryName);
                    }

                    List<string> sourceFiles = [];
                    if (directoryPackage)
                    {
                        string[] permittedExtensions =
                        [
                            .. ChartFileKindResolver.BmsExtensions,
                            ".bmson",
                        .. ChartResourceExtensions.AudioExtensions,
                        .. ChartResourceExtensions.ImageExtensions,
                    ];
                        sourceFiles = [.. LongPathFileSystem.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                        .Where(file => permittedExtensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))];
                    }
                    else
                    {
                        sourceFiles.Add(workingPath);
                    }

                    using (temporaryFiles)
                    {
                        temporaryFiles.CopyFiles(sourceFiles, sourceRoot, installDestination);
                        string playbackPath = Path.Combine(installDestination, Path.GetRelativePath(sourceRoot, workingPath));
                        try
                        {
                            playbackStartTask = TryPlayStart(
                                generation,
                                playbackPath,
                                OnNaturalPlaybackExit,
                                out playbackStartObservation,
                                allowPreload: false);
                            if (!await playbackStartTask.ConfigureAwait(false))
                            {
                                return;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            if (!IsPlaybackStartObservationCompleted(playbackStartObservation))
                            {
                                TryClearPlaybackForStartObservation(playbackStartObservation);
                            }
                            return;
                        }
                        catch (Exception ex)
                        {
                            primaryFailure = ex;
                            StopAfterPlaybackStartFailure(playbackStartObservation, ex);
                            return;
                        }
                    }
                }
                catch (Exception failure)
                {
                    primaryFailure = failure;
                    throw;
                }
                finally
                {
                    if (!string.Equals(originalPath, workingPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            LongPathFileSystem.MoveFile(workingPath, originalPath, overwrite: false);
                            workingPath = originalPath;
                        }
                        catch (Exception restoreFailure) when (primaryFailure != null)
                        {
                            ReportTemporaryPlaybackCleanupFailure(restoreFailure, "restore", workingPath, originalPath);
                        }
                    }
                }
                if (playbackStartTask != null)
                {
                    NotifyPlaybackStarted(generation);
                }
            }
        }
        catch
        {
            try { await TryStopPlaybackForGeneration(generation, bmsFile).ConfigureAwait(false); }
            catch (Exception stopFailure)
            {
                ReportTemporaryPlaybackCleanupFailure(stopFailure, "stop", bmsFile.Path, null);
            }
            throw;
        }
        finally
        {
            // usingによる全回収、元名復元の試行、変更受付の解放後に操作単位で一度だけ案内します。
            if (temporaryFiles.HasCleanupFailure)
            {
                try { await playbackDialogs.NotifyTemporaryFilesRemainAsync(installDestination).ConfigureAwait(false); }
                catch (Exception notificationFailure)
                {
                    Task.FromException(notificationFailure).ObserveFault("PlaybackPanel.TemporaryFilesRemainNotification");
                }
            }
        }
    }

    /// <summary>元の試聴失敗を保持したまま、復元・停止の二次失敗を対象path付きで局所診断します。</summary>
    private static void ReportTemporaryPlaybackCleanupFailure(Exception failure, string operation, string path, string restorePath)
    {
        try { NLogWrapper.FileLogger?.Warn(failure, $"temporary_preview_{operation}_failed path={path} restorePath={restorePath}"); }
        catch { /* 任意の診断故障で主失敗を変更しません。 */ }
    }

    private static bool ContainsChartTarget(ChartPackage chartPackage, ChartFile chart)
    {
        return chartPackage != null
            && chart != null
            && (chartPackage.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(chart) == true);
    }

    /// <summary>既存の譜面変更受付の内側で、開始済みの選曲と再生・先読みの停止を待ちます。</summary>
    /// <remarks>変更側がChartFileOperationSynchronizerを保持する間、新しい選曲は受け付けません。別の入力scopeは返しません。</remarks>
    internal async Task StopPlaybackForMutationAsync()
    {
        await playbackInputGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopPlayback(closeProcess: true).ConfigureAwait(false);
            // terminal側へ停止を委ねた場合は、変更側を成功として物理書込みへ進めません。
            if (shutdownStarted) { throw new OperationCanceledException(); }
        }
        finally { playbackInputGate.Release(); }
    }

    /// <summary>旧表示を解除し、形式共通の譜面を新しい世代の現行選曲として所有します。終了受付後は状態を変更しません。</summary>
    internal long BeginPlayback(ChartFile bmsFile, int rowIndex)
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
            NowPlayingChart = bmsFile;
            PlaybackStatus = ChartFileStatus.NONE;
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
            NowPlayingChart = null;
            nowPlayingRowIndex = rowIndex;
        }
    }

    internal void NotifyPlaybackStarted(long expectedGeneration)
    {
        DispatchPlaybackEvent(PlaybackStarted, expectedGeneration);
    }

    /// <summary>通常の停止を行う。再開可能な受付を維持し、終了受付後は terminal に解放を委ねる。</summary>
    internal async Task StopPlayback(bool closeProcess = false)
    {
        IBMSPlayer playerToClose;
        lock (sessionGate)
        {
            if (shutdownStarted)
            {
                return;
            }
            playerToClose = ClearPlaybackStateWithoutExternalCall(closeProcess || bmsPlayer is INextSongPreloadPlayer);
        }
        await CloseCapturedPlayer(playerToClose).ConfigureAwait(false);
    }

    private IBMSPlayer ClearPlaybackStateWithoutExternalCall(bool closeProcess)
    {
        IBMSPlayer playerToClose = closeProcess ? bmsPlayer : null;
        if (!closeProcess || playerToClose != null)
        {
            playbackGeneration++;
        }
        ClearCurrentPlaybackStatus();
        NowPlayingChart = null;
        nowPlayingRowIndex = -1;
        return playerToClose;
    }

    private async Task CloseCapturedPlayer(IBMSPlayer player, bool forShutdown = false)
    {
        if (player == null)
        {
            return;
        }
        await playerOperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // capture 済みの通常停止が terminal close 後に player を再度操作しない。
            if (shutdownStarted && !forShutdown)
            {
                return;
            }
            if (player is INextSongPreloadPlayer preloadPlayer) { await preloadPlayer.CloseAsync().ConfigureAwait(false); }
            else { await Task.Run(player.CloseProcess).ConfigureAwait(false); }
        }
        finally { playerOperationGate.Release(); }
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
            if (NowPlayingChart != null)
            {
                if (IsPlaying)
                {
                    PlaybackStatus &= ~ChartFileStatus.PLAYALL;
                    PlaybackStatus |= ChartFileStatus.PAUSE;
                }
                else if (IsPaused)
                {
                    PlaybackStatus &= ~ChartFileStatus.PLAYALL;
                    PlaybackStatus |= ChartFileStatus.PLAY;
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
            if (NowPlayingChart != null)
            {
                PlaybackStatus |= ChartFileStatus.FORWARD;
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
            if (NowPlayingChart != null)
            {
                PlaybackStatus &= ~ChartFileStatus.FORWARD;
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
            if (NowPlayingChart != null)
            {
                PlaybackStatus |= ChartFileStatus.BACKWARD;
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
            if (NowPlayingChart != null)
            {
                PlaybackStatus &= ~ChartFileStatus.BACKWARD;
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
    /// 現在再生へ即時反映する音量候補です。保存済み値は操作終端まで変更しません。
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
                bmsPlayer?.VolumeChanged(value);
            }
        }
    }

    /// <summary>音量操作の最終値だけを保存し、保存失敗では表示と現在再生を最新の確定音量へ戻します。同値の終端通知は保存しません。</summary>
    internal void CompleteVolumeAdjustment()
    {
        try { playbackSettings.CommitPlayerVolume(); }
        catch (Exception failure)
        {
            RaisePropertyChanged(nameof(PlayerVolume));
            try { bmsPlayer?.VolumeChanged(playbackSettings.PlayerVolume); }
            catch (Exception restoreFailure) { Task.FromException(restoreFailure).ObserveFault("PlaybackPanel.VolumeRestore"); }
            if (shutdownStarted) { Task.FromException(failure).ObserveFault("PlaybackPanel.VolumeSaveDuringShutdown"); }
            else
            {
                try { reportSettingsSaveFailure(failure); }
                catch (Exception notificationFailure) { Task.FromException(notificationFailure).ObserveFault("PlaybackPanel.VolumeSaveFailureNotification"); }
            }
        }
    }

    /// <summary>
    /// Updates the BMS player header cache from the currently selected or playing BMS file.
    /// </summary>
    /// <param name="bmsFile">BMS file whose metadata should be displayed.</param>


    /// <summary>共通譜面の既存metadataをヘッダーへ接続します。nullは表示対象を解除します。</summary>
#nullable enable annotations
    internal void SetBmsPlayerHeader(ChartFile? bmsFile)
    {
        DisplayedChart = bmsFile;
        bool changed = SetHeaderValue(ref bmsPlayerHeaderTitle, bmsFile?.RawTitle ?? string.Empty)
            | SetHeaderValue(ref bmsPlayerHeaderSubtitle, bmsFile?.Subtitle ?? string.Empty)
            | SetHeaderValue(ref bmsPlayerHeaderArtist, bmsFile?.Artist ?? string.Empty);
        if (changed)
        {
            RaisePlayerHeaderPropertiesChanged();
        }
    }
#nullable restore annotations

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
        if (NowPlayingChart == null)
        {
            return;
        }

        PlaybackStatus &= ~ChartFileStatus.PLAYALL;
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
            lock (sessionGate) { assign(newValue); }
            RaisePropertyChanged(propertyName);
        }
    }

    /// <summary>保存済みの画像・音量・再生モード設定を通知します。player能力は実置換完了時に通知します。</summary>
    internal void NotifySettingsChanged()
    {
        RaisePropertyChanged(nameof(PlayerPanelState));
        RaisePropertyChanged(nameof(RepeatPlayMode));
        RaisePropertyChanged(nameof(FolderSkipPlayMode));
        RaisePropertyChanged(nameof(SinglePlayMode));
        RaisePropertyChanged(nameof(UseExternalPanelImage));
        RaisePropertyChanged(nameof(StagefilePath));
        RaisePropertyChanged(nameof(PlayerVolume));
        RaisePlayerHeaderPropertiesChanged();
    }

    /// <summary>実置換が完了した現在playerの能力だけを画面へ通知します。</summary>
    private void NotifyPlayerCapabilitiesChanged()
    {
        RaisePropertyChanged(nameof(CanSeek));
        RaisePropertyChanged(nameof(CanChangeHighSpeed));
        RaisePropertyChanged(nameof(CanShowInfo));
        RaisePropertyChanged(nameof(CanShowEffect));
        RaisePropertyChanged(nameof(CanChangePlayside));
        RaisePropertyChanged(nameof(UsesUbMplay));
        RaisePropertyChanged(nameof(UsesLr2Body));
        RaisePropertyChanged(nameof(UsesBmiIdxView));
    }

    private bool SupportsBmsonForNextPlayback()
    {
        lock (sessionGate) { return nextPlayerRequest?.SupportsBmson ?? RequirePlayer().SupportsBmson; }
    }

    /// <summary>候補検査と必要な一時配置の受付を通過した開始で捕捉済み要求を採用します。失敗時と置換中の新保存は保持します。</summary>
    private async Task ApplyPlayerForNextPlaybackAsync(NextPlaybackPlayerRequest request)
    {
        if (request == null) { return; }
        await ReplacePlayerAsync(request.CreatePlayer()).ConfigureAwait(false);
        lock (sessionGate)
        {
            if (ReferenceEquals(nextPlayerRequest, request)) { nextPlayerRequest = null; }
        }
    }

    void ISettingsDialogPlaybackRuntimePort.UsePlayerForNextPlayback(Func<IBMSPlayer> createPlayer, bool supportsBmson)
    {
        ArgumentNullException.ThrowIfNull(createPlayer);
        lock (sessionGate) { nextPlayerRequest = new NextPlaybackPlayerRequest(createPlayer, supportsBmson); }
    }

    void ISettingsDialogPlaybackRuntimePort.NotifySettingsChanged()
        => NotifySettingsChanged();

    Task IAudioDeviceTestPlaybackPort.StopPlayback()
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
                isCurrent = !shutdownStarted && expectedGeneration == playbackGeneration && NowPlayingChart != null;
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

    private void PostPlayerSurfaceRequest(Action request)
    {
        if (request == null || shutdownStarted) { return; }
        try
        {
            uiDispatcher.Dispatch(() =>
            {
                if (shutdownStarted) { return; }
                try { request(); }
                catch (Exception failure) { Task.FromException(failure).ObserveFault("PlaybackPanel.SurfaceRequest"); }
            });
        }
        catch (Exception failure) { Task.FromException(failure).ObserveFault("PlaybackPanel.SurfaceRequestDispatch"); }
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
