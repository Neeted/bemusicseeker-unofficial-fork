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

    internal InternalBMSAutoPlayerSoundOnly(
        IPlayerSettingsGateway playerSettingsGateway,
        IAudioPlaybackRuntime audioPlaybackRuntime)
    {
        this.playerSettingsGateway = playerSettingsGateway
            ?? throw new ArgumentNullException(nameof(playerSettingsGateway));
        this.audioPlaybackRuntime = audioPlaybackRuntime
            ?? throw new ArgumentNullException(nameof(audioPlaybackRuntime));
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
                }
                break;
            IL_01ff:
                Thread.Sleep(40);
            }
            while (_fastForwarding || _fastBackwarding)
            {
                Thread.Sleep(1);
            }
            _onExitEvent?.Invoke(this, null);
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
            throw new FileNotFoundException("BMS ファイルが見つかりません。", bmsFilePath);
        }
        BMSAutoPlayer bMSAutoPlayer;
        lock (_sharedObjectLock)
        {
            PlayerSettingsSnapshot settings = playerSettingsGateway.CaptureSnapshot();
            AudioPlaybackInitializationResult initialization = audioPlaybackRuntime.Initialize(settings);
            playerSettingsGateway.ApplyNegotiatedAudioSettings(
                initialization.PlayerDriver,
                initialization.PlayerDevice,
                initialization.PlayerDeviceName,
                initialization.PlayerSampleRate,
                initialization.PlayerFormat);
            _fastForwarding = false;
            _fastBackwarding = false;
            Duration = TimeSpan.MinValue;
            CurrentTime = TimeSpan.MinValue;
            MusicDuration = TimeSpan.MinValue;
            BmsDuration = TimeSpan.MinValue;
            _player?.Stop();
            bMSAutoPlayer = new BMSAutoPlayer(new Ribbit.BMS.BMSFile(bmsFilePath));
            bMSAutoPlayer.LoadResources();
            _player?.Dispose();
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
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (Duration == TimeSpan.Zero)
            {
                throw new InvalidDataException("Zero duration BMS file: " + bmsFilePath);
            }
            _onExitEvent = onExitEventHandler;
            _infloopTask ??= Task.Run(_playbackThreadAction).Logging("PlayStart");
        }
        try
        {
            await bMSAutoPlayer.Start();
        }
        catch (OperationCanceledException)
        {
            CloseProcessCore(bMSAutoPlayer);
            throw;
        }
        catch
        {
            CloseProcessCore(bMSAutoPlayer);
            throw;
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
