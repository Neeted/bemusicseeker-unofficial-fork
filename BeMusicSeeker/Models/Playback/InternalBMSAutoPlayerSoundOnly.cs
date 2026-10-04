using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.BMS;

namespace BeMusicSeeker.Models;

public class InternalBMSAutoPlayerSoundOnly : ObservableObject, IBMSPlayer, INotifyPropertyChanged, INextSongPreloadPlayer
{
    /// <summary>内蔵parserと音声経路はbmsonを受理します。</summary>
    public bool SupportsBmson => true;

    private readonly IPlayerSettingsGateway playerSettingsGateway;

    private readonly IAudioPlaybackRuntime audioPlaybackRuntime;

    private readonly Action<Action<object, EventArgs>, object> exitEventDispatcher;

    private readonly Func<string, BMSAutoPlayer> autoPlayerFactory;

    private readonly TimeSpan outputReadyTimeout;

    private readonly SemaphoreSlim startStopGate = new(1, 1);
    private readonly NextSongPreloadOwner preload;

    private readonly Stopwatch _timer = Stopwatch.StartNew();

    private Task _infloopTask;

    private BMSAutoPlayer _player;

    private readonly object _sharedObjectLock = new();

    private readonly Action _playbackThreadAction;

    private Action<object, EventArgs> _onExitEvent;

    private bool _playbackTaskCompletedSuccessfully;

    private bool _fastForwarding;

    private bool _fastBackwarding;

    private TimeSpan _StopTime;

    private TimeSpan _BmsDuration;

    private TimeSpan _MusicDuration;

    private int _CurrentVoices;

    private int _MaxVoices;

    private int _NoteDensity;

    private int _NoteDensityMax;

    private int _Bpm;

    private int _MinBpm;

    private int _MaxBpm;

    private double _Total;

    private int _Combo;

    private int _Notes;

    private int _Measure;

    private int _LastMeasure;

    private TimeSpan _duration = TimeSpan.MinValue;

    /// <summary>音声設定、再生runtime、終了callbackのdispatch境界を使って内部playerを作成します。</summary>
    /// <param name="playerSettingsGateway">再生設定のsnapshotを取得するgatewayです。</param>
    /// <param name="audioPlaybackRuntime">音声出力runtimeです。</param>
    /// <param name="exitEventDispatcher">指定時は捕捉した終了callbackをdispatchします。未指定なら直接呼び出します。</param>
    /// <param name="songPreparation">解析と復号の準備境界です。未指定なら現在のsessionで共通loaderを使います。</param>
    /// <param name="autoPlayerFactory">指定時は曲pathからplayerを作成します。未指定なら通常のBMS parserとplayerを使います。</param>
    /// <param name="outputReadyTimeout">初回出力観測だけの待機上限です。通常は10秒、期限失敗の検査でのみ差し替えます。</param>
    internal InternalBMSAutoPlayerSoundOnly(
        IPlayerSettingsGateway playerSettingsGateway,
        IAudioPlaybackRuntime audioPlaybackRuntime,
        Action<Action<object, EventArgs>, object> exitEventDispatcher = null,
        Func<string, BMSAutoPlayer> autoPlayerFactory = null,
        Func<NextSongPreloadInput, CancellationToken, PreparedBmsSong> songPreparation = null,
        TimeSpan? outputReadyTimeout = null)
    {
        this.playerSettingsGateway = playerSettingsGateway
            ?? throw new ArgumentNullException(nameof(playerSettingsGateway));
        this.audioPlaybackRuntime = audioPlaybackRuntime
            ?? throw new ArgumentNullException(nameof(audioPlaybackRuntime));
        this.exitEventDispatcher = exitEventDispatcher;
        this.outputReadyTimeout = outputReadyTimeout ?? TimeSpan.FromSeconds(10);
        this.autoPlayerFactory = autoPlayerFactory
            ?? new Func<string, BMSAutoPlayer>(path => new BMSAutoPlayer(PlaybackChart.Load(path)));
        preload = new NextSongPreloadOwner(songPreparation ?? ((input, token) =>
        {
            Ribbit.Media.Audio.BassAudioSession session = BmsAudioResourceLoader.CaptureActiveSession();
            token.ThrowIfCancellationRequested();
            var chart = PlaybackChart.Load(input.Path);
            return PreparedBmsSong.Prepare(chart, Ribbit.Media.BassAudioPlayer.DefaultVolume, token, session);
        }));
        _playbackThreadAction = CreatePlaybackThreadAction();
    }

    public TimeSpan StopTime
    {
        get
        {
            return _StopTime;
        }
        private set
        {
            if (!(_StopTime == value))
            {
                _StopTime = value;
                RaisePropertyChanged("StopTime");
            }
        }
    }

    public TimeSpan BmsDuration
    {
        get
        {
            return _BmsDuration;
        }
        private set
        {
            if (!(_BmsDuration == value))
            {
                _BmsDuration = value;
                RaisePropertyChanged("BmsDuration");
            }
        }
    }

    public TimeSpan MusicDuration
    {
        get
        {
            return _MusicDuration;
        }
        private set
        {
            if (!(_MusicDuration == value))
            {
                _MusicDuration = value;
                RaisePropertyChanged("MusicDuration");
            }
        }
    }

    public int CurrentVoices
    {
        get
        {
            return _CurrentVoices;
        }
        private set
        {
            if (_CurrentVoices != value)
            {
                _CurrentVoices = value;
                RaisePropertyChanged("CurrentVoices");
            }
        }
    }

    public int MaxVoices
    {
        get
        {
            return _MaxVoices;
        }
        private set
        {
            if (_MaxVoices != value)
            {
                _MaxVoices = value;
                RaisePropertyChanged("MaxVoices");
            }
        }
    }

    public int NoteDensity
    {
        get
        {
            return _NoteDensity;
        }
        private set
        {
            if (_NoteDensity != value)
            {
                _NoteDensity = value;
                RaisePropertyChanged("NoteDensity");
            }
        }
    }

    public int NoteDensityMax
    {
        get
        {
            return _NoteDensityMax;
        }
        private set
        {
            if (_NoteDensityMax != value)
            {
                _NoteDensityMax = value;
                RaisePropertyChanged("NoteDensityMax");
            }
        }
    }

    public int Bpm
    {
        get
        {
            return _Bpm;
        }
        private set
        {
            if (_Bpm != value)
            {
                _Bpm = value;
                RaisePropertyChanged("Bpm");
            }
        }
    }

    public int MinBpm
    {
        get
        {
            return _MinBpm;
        }
        private set
        {
            if (_MinBpm != value)
            {
                _MinBpm = value;
                RaisePropertyChanged("MinBpm");
            }
        }
    }

    public int MaxBpm
    {
        get
        {
            return _MaxBpm;
        }
        private set
        {
            if (_MaxBpm != value)
            {
                _MaxBpm = value;
                RaisePropertyChanged("MaxBpm");
            }
        }
    }

    public double Total
    {
        get
        {
            return _Total;
        }
        private set
        {
            if (_Total != value)
            {
                _Total = value;
                RaisePropertyChanged("Total");
            }
        }
    }

    public int Combo
    {
        get
        {
            return _Combo;
        }
        private set
        {
            if (_Combo != value)
            {
                _Combo = value;
                RaisePropertyChanged("Combo");
            }
        }
    }

    public int Notes
    {
        get
        {
            return _Notes;
        }
        private set
        {
            if (_Notes != value)
            {
                _Notes = value;
                RaisePropertyChanged("Notes");
            }
        }
    }

    public int Measure
    {
        get
        {
            return _Measure;
        }
        private set
        {
            if (_Measure != value)
            {
                _Measure = value;
                RaisePropertyChanged("Measure");
            }
        }
    }

    public int LastMeasure
    {
        get
        {
            return _LastMeasure;
        }
        private set
        {
            if (_LastMeasure != value)
            {
                _LastMeasure = value;
                RaisePropertyChanged("LastMeasure");
            }
        }
    }

    public string ExePath
    {
        get
        {
            throw new NotImplementedException();
        }
        set
        {
            throw new NotImplementedException();
        }
    }

    public TimeSpan Duration
    {
        get
        {
            return _duration;
        }
        private set
        {
            if (!(_duration == value))
            {
                _duration = value;
                RaisePropertyChanged("Duration");
            }
        }
    }

    public TimeSpan CurrentTime
    {
        get
        {
            return _player?.CurrentTime ?? TimeSpan.MinValue;
        }
        set
        {
            try
            {
                if (!(_player.CurrentTime == value))
                {
                    _player.CurrentTime = value;
                    RaisePropertyChanged(nameof(CurrentTime));
                }
            }
            catch
            {
            }
        }
    }

    private void NotifyCurrentTimeChanged()
    {
        try
        {
            RaisePropertyChanged(nameof(CurrentTime));
        }
        catch
        {
            // 表示通知の例外は、CurrentTime setterと同じく再生制御へ伝えません。
        }
    }

    private Action CreatePlaybackThreadAction()
    {
        return delegate
        {
            Action<object, EventArgs> onExitEvent = null;
            lock (_sharedObjectLock)
            {
                _timer.Restart();
            }
            double num = 0.2;
            double value = 5.0;
            int num2 = 5;
            var timeSpan = TimeSpan.FromSeconds(0.0 - num);
            while (true)
            {
                lock (_sharedObjectLock)
                {
                    // cleanup後のseekでCurrentTimeは戻り得るため、完了後は時計値を再判定しません。
                    if (_player != null && !_playbackTaskCompletedSuccessfully)
                    {
                        TimeSpan elapsed = _timer.Elapsed;
                        if (_fastForwarding || _fastBackwarding)
                        {
                            if (elapsed - timeSpan > TimeSpan.FromSeconds(num))
                            {
                                if (elapsed - timeSpan > TimeSpan.FromSeconds(value))
                                {
                                    num2 = 10;
                                }
                                if (_fastForwarding)
                                {
                                    _player.CurrentTime += TimeSpan.FromSeconds((double)num2 * num);
                                }
                                else
                                {
                                    _player.CurrentTime -= TimeSpan.FromSeconds((double)num2 * num);
                                }
                                timeSpan = elapsed;
                                continue;
                            }
                        }
                        else
                        {
                            num2 = 5;
                            timeSpan = elapsed;
                        }
                        RaisePropertyChanged(() => CurrentTime);
                        StopTime = _player.StopTime;
                        CurrentVoices = audioPlaybackRuntime.CurrentVoices;
                        MaxVoices = audioPlaybackRuntime.MaxVoices;
                        NoteDensity = (int)_player.NoteDensity;
                        NoteDensityMax = (int)_player.NoteDensityMax;
                        Combo = _player.Combo;
                        Bpm = (int)_player.CurrentBpm;
                        Measure = _player.CurrentMeasure;
                        goto IL_01ff;
                    }
                    _timer.Reset();
                    _infloopTask = null;
                    onExitEvent = _playbackTaskCompletedSuccessfully ? _onExitEvent : null;
                    _onExitEvent = null;
                }
                break;
            IL_01ff:
                Thread.Sleep(40);
            }
            while (_fastForwarding || _fastBackwarding)
            {
                Thread.Sleep(1);
            }
            if (onExitEvent != null)
            {
                if (exitEventDispatcher == null)
                {
                    onExitEvent(this, null);
                }
                else
                {
                    exitEventDispatcher(onExitEvent, this);
                }
            }
        };
    }

    public void ChangePlayside()
    {
    }

    /// <summary>既存の同期player契約です。UI経路はCloseAsyncで停止終端を待ち、同期入口はworkerでのみ使います。</summary>
    public void CloseProcess()
    {
        CloseAsync().GetAwaiter().GetResult();
    }

    /// <summary>先読みの後片付けと旧曲の停止を待ってからruntimeを解放します。</summary>
    public async Task CloseAsync()
    {
        await startStopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Exception failure = null;
            try { await preload.InvalidateAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = exception; }
            try { await Task.Run(() => CloseProcessCore(null)).ConfigureAwait(false); }
            catch (Exception exception) { failure = failure == null ? exception : new AggregateException(failure, exception); }
            if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        }
        finally { startStopGate.Release(); }
    }

    /// <summary>現在曲の開始後に一件だけ準備します。曲の途中では候補を差し替えません。</summary>
    async Task INextSongPreloadPlayer.PrepareNextAsync(NextSongPreloadInput input, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onFailure);
        await startStopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sharedObjectLock) { if (_player == null) { return; } }
            Task<PreparedBmsSong> preparation = preload.Request(input);
            ObservePreloadFailureAsync(preparation, onFailure).ObserveFault("InternalPlayer.NextSong");
        }
        finally { startStopGate.Release(); }
    }

    private async Task ObservePreloadFailureAsync(Task<PreparedBmsSong> preparation, Action<Exception> onFailure)
    {
        Exception failure;
        try { await preparation.ConfigureAwait(false); return; }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (NextSongPreloadOwner.IsInputFailure(exception)) { return; }
        catch (Exception exception) { failure = exception; }

        await startStopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 先に実開始や停止が引き取った失敗は、その呼出元だけが報告します。
            if (!preload.Owns(preparation)) { return; }
            try { await preload.InvalidateAsync().ConfigureAwait(false); }
            catch (Exception exception) when (ReferenceEquals(exception, failure)) { }
            try { await Task.Run(() => CloseProcessCore(null)).ConfigureAwait(false); }
            catch (Exception cleanupFailure) { failure = new AggregateException(failure, cleanupFailure); }
        }
        finally { startStopGate.Release(); }

        Task.FromException(failure).ObserveFault("InternalPlayer.NextSong");
        // 通知は開始停止gateの外です。呼出元は捕捉した曲の状態だけを更新します。
        onFailure(failure);
    }

    private void CloseProcessCore(BMSAutoPlayer expectedPlayer)
    {
        BMSAutoPlayer releasedPlayer;
        lock (_sharedObjectLock)
        {
            if (expectedPlayer != null && !ReferenceEquals(_player, expectedPlayer))
            {
                return;
            }

            Duration = TimeSpan.MinValue;
            NotifyCurrentTimeChanged();
            _playbackTaskCompletedSuccessfully = false;
            StopTime = _player?.StopTime ?? TimeSpan.Zero;
            CurrentVoices = 0;
            audioPlaybackRuntime.ClearMaxVoices();
            MaxVoices = audioPlaybackRuntime.MaxVoices;
            NoteDensity = (int)(_player?.NoteDensity ?? 0.0);
            NoteDensityMax = (int)(_player?.NoteDensityMax ?? 0.0);
            Bpm = (int)(_player?.Chart.DisplayBpm ?? 0.0);
            MinBpm = (int)(_player?.Chart.DisplayMinBpm ?? 0.0);
            MaxBpm = (int)(_player?.Chart.DisplayMaxBpm ?? 0.0);
            Total = _player?.Chart.Total ?? 0.0;
            Combo = _player?.Combo ?? 0;
            Notes = _player?.Chart.TotalNoteCount ?? 0;
            Measure = _player?.CurrentMeasure ?? 0;
            LastMeasure = _player?.Chart.LastMeasure ?? 0;
            releasedPlayer = _player;
            _player = null;
            _fastForwarding = false;
            _fastBackwarding = false;
        }
        // 停止合流は表示monitor外で行い、再生終了側の状態確認を妨げません。
        Exception disposalFailure = null;
        try { releasedPlayer?.Dispose(); }
        catch (Exception failure) { disposalFailure = failure; }
        try { audioPlaybackRuntime.Free(); }
        catch (Exception failure)
        {
            if (disposalFailure != null) { throw new AggregateException(disposalFailure, failure); }
            throw;
        }
        if (disposalFailure != null) { ExceptionDispatchInfo.Capture(disposalFailure).Throw(); }
    }

    public void DecreaseHighSpeed()
    {
    }

    public void FastBackwardPlayingBMSfileEnd()
    {
        lock (_sharedObjectLock)
        {
            _fastForwarding = false;
            _fastBackwarding = false;
        }
    }

    public void FastBackwardPlayingBMSfileStart()
    {
        lock (_sharedObjectLock)
        {
            _fastForwarding = false;
            _fastBackwarding = true;
            if (_player != null)
            {
                _player.CurrentTime -= TimeSpan.FromSeconds(1.0);
            }
        }
    }

    public void FastForwardPlayingBMSfileEnd()
    {
        lock (_sharedObjectLock)
        {
            _fastForwarding = false;
            _fastBackwarding = false;
        }
    }

    public void FastForwardPlayingBMSfileStart()
    {
        lock (_sharedObjectLock)
        {
            _fastBackwarding = false;
            _fastForwarding = true;
            if (_player != null)
            {
                _player.CurrentTime += TimeSpan.FromSeconds(1.0);
            }
        }
    }

    public void IncreaseHighSpeed()
    {
    }

    public void PausePlayingBMSfileToggle()
    {
        lock (_sharedObjectLock)
        {
            _player?.Pause();
        }
    }

    /// <summary>既存APIは準備だけでなく演奏と後片付けの終端まで待ちます。</summary>
    public Task PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null) =>
        BeginStart(bmsFilePath, onExitEventHandler, allowPreload: true).Completion;

    /// <summary>準備・開始の成功と、演奏の終了を独立して観測できる開始操作です。</summary>
    PlaybackStartOperation INextSongPreloadPlayer.BeginStart(string path, Action<object, EventArgs> onExit, bool allowPreload,
        Action<Exception> onPlaybackFailure) => BeginStart(path, onExit, allowPreload, onPlaybackFailure);

    /// <summary>同じ対象の先読みTaskを一回採用し、開始成功と演奏終端を別々に返します。</summary>
    /// <param name="onPlaybackFailure">Ready成功後の停止をこの開始が所有した場合だけ、通常取消以外の終端故障を通知します。</param>
    internal PlaybackStartOperation BeginStart(string path, Action<object, EventArgs> onExit, bool allowPreload,
        Action<Exception> onPlaybackFailure = null)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ready.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        Task completion = StartCoreAsync(path, onExit, allowPreload, ready, onPlaybackFailure);
        return new PlaybackStartOperation(ready.Task, completion);
    }

    private async Task StartCoreAsync(string bmsFilePath, Action<object, EventArgs> onExitEventHandler,
        bool allowPreload, TaskCompletionSource ready, Action<Exception> onPlaybackFailure)
    {
        BMSAutoPlayer bMSAutoPlayer = null;
        Task playbackTask = null;
        await startStopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!LongPathFileSystem.FileExists(bmsFilePath))
            {
                throw new FileNotFoundException(BeMusicSeeker.Properties.Resources.Error_BmsFileNotFound, bmsFilePath);
            }
            // 停止を先に要求し、同じ対象の復号が終わるまでは旧sessionを維持します。
            BMSAutoPlayer previousPlayer;
            lock (_sharedObjectLock)
            {
                previousPlayer = _player;
                _player = null;
                _onExitEvent = null;
                _playbackTaskCompletedSuccessfully = false;
                _fastForwarding = false;
                _fastBackwarding = false;
            }
            await Task.Run(() => previousPlayer?.DisposeBeforeNextSong()).ConfigureAwait(false);
            PreparedBmsSong prepared = await preload.TakeAsync(
                allowPreload ? NextSongPreloadInput.Capture(bmsFilePath) : null).ConfigureAwait(false);
            await Task.Run(() =>
            {
                PlayerSettingsSnapshot settings = playerSettingsGateway.CaptureSnapshot();
                Duration = TimeSpan.MinValue;
                MusicDuration = TimeSpan.MinValue;
                BmsDuration = TimeSpan.MinValue;
                NotifyCurrentTimeChanged();
                _ = audioPlaybackRuntime.Initialize(settings);
                if (prepared == null)
                {
                    bMSAutoPlayer = autoPlayerFactory(bmsFilePath);
                    bMSAutoPlayer.LoadResources();
                }
                else
                {
                    bMSAutoPlayer = new BMSAutoPlayer(prepared.Chart);
                    bMSAutoPlayer.AdoptPreparedSong(prepared);
                }
            }).ConfigureAwait(false);
            // native Startと復号は完了済みです。出力・表示lockを保持せず実pullの一回観測を待ちます。
            await audioPlaybackRuntime.WaitForOutputReadyAsync().WaitAsync(outputReadyTimeout).ConfigureAwait(false);
            await Task.Run(() =>
            {
                lock (_sharedObjectLock)
                {
                    _player = bMSAutoPlayer;
                    Duration = bMSAutoPlayer.Duration;
                    CurrentTime = TimeSpan.Zero;
                    MusicDuration = bMSAutoPlayer.MusicDuration;
                    BmsDuration = bMSAutoPlayer.BmsDuration;
                    StopTime = bMSAutoPlayer.StopTime;
                    CurrentVoices = 0;
                    audioPlaybackRuntime.ClearMaxVoices();
                    MaxVoices = audioPlaybackRuntime.MaxVoices;
                    NoteDensity = (int)bMSAutoPlayer.NoteDensity;
                    NoteDensityMax = (int)bMSAutoPlayer.NoteDensityMax;
                    Bpm = (int)bMSAutoPlayer.CurrentBpm;
                    MinBpm = (int)(bMSAutoPlayer.Chart.DisplayMinBpm);
                    MaxBpm = (int)(bMSAutoPlayer.Chart.DisplayMaxBpm);
                    Total = bMSAutoPlayer.Chart.Total;
                    Combo = bMSAutoPlayer.Combo;
                    Notes = bMSAutoPlayer.Chart.TotalNoteCount;
                    Measure = bMSAutoPlayer.CurrentMeasure;
                    LastMeasure = bMSAutoPlayer.Chart.LastMeasure;
                    if (Duration == TimeSpan.Zero)
                    {
                        throw new InvalidDataException("Zero duration BMS file: " + bmsFilePath);
                    }
                    _onExitEvent = onExitEventHandler;
                }
                playbackTask = bMSAutoPlayer.Start();
                if (playbackTask.IsFaulted || playbackTask.IsCanceled)
                {
                    playbackTask.GetAwaiter().GetResult();
                }
                lock (_sharedObjectLock)
                {
                    if (_infloopTask == null)
                    {
                        _infloopTask = Task.Run(_playbackThreadAction);
                        _infloopTask.ObserveFault("PlayStart");
                    }
                }
            }).ConfigureAwait(false);
            ready.TrySetResult();
        }
        catch (Exception failure)
        {
            // 旧曲の解放・準備は順に待ちます。途中で失敗しても残った先読みを終えてからFreeします。
            try { await preload.InvalidateAsync().ConfigureAwait(false); }
            catch (Exception preparationFailure) { failure = new AggregateException(failure, preparationFailure); }
            try
            {
                await Task.Run(() => CleanupFailedStart(bMSAutoPlayer, failure, closeCurrentPlayer: true)).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) { failure = cleanupFailure; }
            ready.TrySetException(failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally { startStopGate.Release(); }
        try
        {
            await playbackTask.ConfigureAwait(false);
            lock (_sharedObjectLock)
            {
                if (ReferenceEquals(_player, bMSAutoPlayer))
                {
                    _playbackTaskCompletedSuccessfully = true;
                }
            }
        }
        catch (Exception failure)
        {
            bool ownsFailure;
            await startStopGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_sharedObjectLock) { ownsFailure = ReferenceEquals(_player, bMSAutoPlayer); }
                if (ownsFailure)
                {
                    // 演奏が故障しても、同じsessionで先読み中の入力・decoder終端より先にFreeしません。
                    try { await preload.InvalidateAsync().ConfigureAwait(false); }
                    catch (Exception preparationFailure) { failure = new AggregateException(failure, preparationFailure); }
                    try { await Task.Run(() => CleanupFailedStart(bMSAutoPlayer, failure)).ConfigureAwait(false); }
                    catch (Exception cleanupFailure) { failure = cleanupFailure; }
                }
                // 外されたplayerのDispose故障は、Close・背景監視・切替の所有者だけが報告します。
            }
            finally { startStopGate.Release(); }
            if (ownsFailure && onPlaybackFailure != null && !IsOnlyCancellation(failure))
            {
                try { onPlaybackFailure(failure); }
                catch (Exception notificationFailure)
                {
                    Task.FromException(notificationFailure).ObserveFault("InternalPlayer.PlaybackFailureNotification");
                }
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static bool IsOnlyCancellation(Exception failure) => failure is OperationCanceledException
        || (failure is AggregateException aggregate && aggregate.InnerExceptions.Count > 0
            && aggregate.InnerExceptions.All(IsOnlyCancellation));

    private void CleanupFailedStart(
        BMSAutoPlayer failedPlayer,
        Exception startupFailure,
        bool closeCurrentPlayer = false)
    {
        Exception cleanupFailure = null;
        bool disposeUnpublished;
        lock (_sharedObjectLock)
        {
            disposeUnpublished = closeCurrentPlayer && failedPlayer != null && !ReferenceEquals(_player, failedPlayer);
        }
        if (disposeUnpublished)
        {
            try { failedPlayer.Dispose(); }
            catch (Exception exception) { cleanupFailure = exception; }
        }

        try
        {
            CloseProcessCore(closeCurrentPlayer ? null : failedPlayer);
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure == null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }
        if (cleanupFailure != null)
        {
            throw new AggregateException(
                "Internal player startup failed and its cleanup could not be completed.",
                startupFailure,
                cleanupFailure);
        }
    }

    public void RestartPlayingBMSfile()
    {
        lock (_sharedObjectLock)
        {
            if (_player != null)
            {
                _player.CurrentTime = TimeSpan.Zero;
            }
        }
    }

    public void ShowEffect()
    {
    }

    public void ShowInfo()
    {
    }

    public void VolumeChanged()
    {
        PlayerSettingsSnapshot settings = playerSettingsGateway.CaptureSnapshot();
        audioPlaybackRuntime.SetVolume(settings.PlayerVolume);
    }
}
