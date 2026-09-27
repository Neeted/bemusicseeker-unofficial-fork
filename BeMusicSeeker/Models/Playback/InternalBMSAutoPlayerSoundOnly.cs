using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.BMS;

namespace BeMusicSeeker.Models;

public class InternalBMSAutoPlayerSoundOnly : ObservableObject, IBMSPlayer, INotifyPropertyChanged
{
    private readonly IPlayerSettingsGateway playerSettingsGateway;

    private readonly IAudioPlaybackRuntime audioPlaybackRuntime;

    private readonly Action<Action<object, EventArgs>, object> exitEventDispatcher;

    private readonly Func<string, BMSAutoPlayer> autoPlayerFactory;

    private readonly Stopwatch _timer = Stopwatch.StartNew();

    private Task _infloopTask;

    private BMSAutoPlayer _player;

    private readonly object _sharedObjectLock = new();

    private readonly Action _playbackThreadAction;

    private Action<object, EventArgs> _onExitEvent;

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
    /// <param name="autoPlayerFactory">指定時は曲pathからplayerを作成します。未指定なら通常のBMS parserとplayerを使います。</param>
    internal InternalBMSAutoPlayerSoundOnly(
        IPlayerSettingsGateway playerSettingsGateway,
        IAudioPlaybackRuntime audioPlaybackRuntime,
        Action<Action<object, EventArgs>, object> exitEventDispatcher = null,
        Func<string, BMSAutoPlayer> autoPlayerFactory = null)
    {
        this.playerSettingsGateway = playerSettingsGateway
            ?? throw new ArgumentNullException(nameof(playerSettingsGateway));
        this.audioPlaybackRuntime = audioPlaybackRuntime
            ?? throw new ArgumentNullException(nameof(audioPlaybackRuntime));
        this.exitEventDispatcher = exitEventDispatcher;
        this.autoPlayerFactory = autoPlayerFactory
            ?? new Func<string, BMSAutoPlayer>(path => new BMSAutoPlayer(new Ribbit.BMS.BMSFile(path)));
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
                    RaisePropertyChanged("CurrentTime");
                }
            }
            catch
            {
            }
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
                    if (_player != null && _player.CurrentTime < _player.Duration)
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
                    onExitEvent = _onExitEvent;
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

    public void CloseProcess()
    {
        CloseProcessCore(expectedPlayer: null);
    }

    private void CloseProcessCore(BMSAutoPlayer expectedPlayer)
    {
        lock (_sharedObjectLock)
        {
            if (expectedPlayer != null && !ReferenceEquals(_player, expectedPlayer))
            {
                return;
            }

            Duration = TimeSpan.MinValue;
            CurrentTime = TimeSpan.MinValue;
            StopTime = _player?.StopTime ?? TimeSpan.Zero;
            CurrentVoices = 0;
            audioPlaybackRuntime.ClearMaxVoices();
            MaxVoices = audioPlaybackRuntime.MaxVoices;
            NoteDensity = (int)(_player?.NoteDensity ?? 0.0);
            NoteDensityMax = (int)(_player?.NoteDensityMax ?? 0.0);
            Bpm = (int)(_player?.Bms.Bpm?.ToDouble() ?? 0.0);
            MinBpm = (int)(_player?.Bms.MinBpm?.ToDouble() ?? 0.0);
            MaxBpm = (int)(_player?.Bms.MaxBpm?.ToDouble() ?? 0.0);
            Total = _player?.Bms.Total ?? 0.0;
            Combo = _player?.Combo ?? 0;
            Notes = _player?.Bms.TotalNoteCount ?? 0;
            Measure = _player?.CurrentMeasure ?? 0;
            LastMeasure = _player?.Bms.Measures.LastIndex ?? 0;
            _player?.Dispose();
            _player = null;
            _fastForwarding = false;
            _fastBackwarding = false;
            audioPlaybackRuntime.Free();
        }
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

    public async Task PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null)
    {
        if (!LongPathFileSystem.FileExists(bmsFilePath))
        {
            throw new FileNotFoundException(BeMusicSeeker.Properties.Resources.Error_BmsFileNotFound, bmsFilePath);
        }
        BMSAutoPlayer bMSAutoPlayer = null;
        lock (_sharedObjectLock)
        {
            try
            {
                PlayerSettingsSnapshot settings = playerSettingsGateway.CaptureSnapshot();
                _fastForwarding = false;
                _fastBackwarding = false;
                Duration = TimeSpan.MinValue;
                CurrentTime = TimeSpan.MinValue;
                MusicDuration = TimeSpan.MinValue;
                BmsDuration = TimeSpan.MinValue;
                if (_player != null)
                {
                    BMSAutoPlayer previousPlayer = _player;
                    try
                    {
                        previousPlayer.DisposeBeforeNextSong();
                    }
                    finally
                    {
                        _player = null;
                    }
                }
                _ = audioPlaybackRuntime.Initialize(settings);
                bMSAutoPlayer = autoPlayerFactory(bmsFilePath);
                bMSAutoPlayer.LoadResources();
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
                MinBpm = (int)(bMSAutoPlayer.Bms.MinBpm?.ToDouble() ?? 0.0);
                MaxBpm = (int)(bMSAutoPlayer.Bms.MaxBpm?.ToDouble() ?? 0.0);
                Total = bMSAutoPlayer.Bms.Total ?? 0.0;
                Combo = bMSAutoPlayer.Combo;
                Notes = bMSAutoPlayer.Bms.TotalNoteCount;
                Measure = bMSAutoPlayer.CurrentMeasure;
                LastMeasure = bMSAutoPlayer.Bms.Measures.LastIndex;
                if (Duration == TimeSpan.Zero)
                {
                    throw new InvalidDataException("Zero duration BMS file: " + bmsFilePath);
                }
                _onExitEvent = onExitEventHandler;
                if (_infloopTask == null)
                {
                    _infloopTask = Task.Run(_playbackThreadAction);
                    _infloopTask.ObserveFault("PlayStart");
                }
            }
            catch (Exception startupFailure)
            {
                CleanupFailedStart(bMSAutoPlayer, startupFailure, closeCurrentPlayer: true);
                throw;
            }
        }
        try
        {
            await bMSAutoPlayer.Start();
        }
        catch (Exception startupFailure)
        {
            CleanupFailedStart(bMSAutoPlayer, startupFailure);
            throw;
        }
    }

    private void CleanupFailedStart(
        BMSAutoPlayer failedPlayer,
        Exception startupFailure,
        bool closeCurrentPlayer = false)
    {
        Exception cleanupFailure = null;
        lock (_sharedObjectLock)
        {
            if (failedPlayer != null && !ReferenceEquals(_player, failedPlayer))
            {
                try
                {
                    failedPlayer.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }
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
