using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the playback adapter and telemetry exposed by the playback panel.
/// </summary>
public sealed class PlaybackPanelViewModel : ViewModel
{
    private readonly Func<Dispatcher> uiDispatcherProvider;

    private IBMSPlayer bmsPlayer;

    private IntPtr? parentHandle;

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

    private string moviePlayerHeaderTitle = string.Empty;

    private string moviePlayerHeaderSubtitle = string.Empty;

    private string moviePlayerHeaderArtist = string.Empty;

    internal PlaybackPanelViewModel(IBMSPlayer player, Func<Dispatcher> uiDispatcherProvider)
    {
        this.uiDispatcherProvider = uiDispatcherProvider ?? throw new ArgumentNullException(nameof(uiDispatcherProvider));
        ReplacePlayer(player);
    }

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
        get => bmsPlayer?.CurrentTime ?? TimeSpan.MinValue;
        set
        {
            if (bmsPlayer == null)
            {
                RaisePropertyChanged(nameof(CurrentlyPlayingTime));
                return;
            }

            bmsPlayer.CurrentTime = value;
            RaisePropertyChanged(nameof(CurrentlyPlayingTime));
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
    internal void ReplacePlayer(IBMSPlayer player)
    {
        if (player == null)
        {
            throw new ArgumentNullException(nameof(player));
        }

        if (ReferenceEquals(bmsPlayer, player))
        {
            RefreshPlayerState(player);
            return;
        }

        if (bmsPlayer != null)
        {
            bmsPlayer.PropertyChanged -= BmsPlayerPropertyChanged;
            bmsPlayer.CloseProcess();
        }

        bmsPlayer = player;
        bmsPlayer.PropertyChanged += BmsPlayerPropertyChanged;
        if (parentHandle.HasValue)
        {
            bmsPlayer.ParentHandle = parentHandle.Value;
        }
        RefreshPlayerState(bmsPlayer);
        RaisePropertyChanged(nameof(CurrentlyPlayingTime));
    }

    internal void AttachParentHandle(IntPtr parentHandle)
    {
        this.parentHandle = parentHandle;
        RequirePlayer().ParentHandle = parentHandle;
    }

    internal void CloseProcess()
    {
        if (bmsPlayer == null)
        {
            return;
        }

        IBMSPlayer player = bmsPlayer;
        player.CloseProcess();
        DispatchToUi(() =>
        {
            if (ReferenceEquals(player, bmsPlayer))
            {
                RefreshPlayerState(player);
            }
        });
    }

    internal void PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler)
    {
        RequirePlayer().PlayStart(bmsFilePath, onExitEventHandler);
    }

    internal void PausePlayingBmsFileToggle()
    {
        RequirePlayer().PausePlayingBMSfileToggle();
    }

    internal void RestartPlayingBmsFile()
    {
        RequirePlayer().RestartPlayingBMSfile();
    }

    internal void FastForwardStart()
    {
        RequirePlayer().FastForwardPlayingBMSfileStart();
    }

    internal void FastForwardEnd()
    {
        RequirePlayer().FastForwardPlayingBMSfileEnd();
    }

    internal void FastBackwardStart()
    {
        RequirePlayer().FastBackwardPlayingBMSfileStart();
    }

    internal void FastBackwardEnd()
    {
        RequirePlayer().FastBackwardPlayingBMSfileEnd();
    }

    internal void ShowInfo()
    {
        RequirePlayer().ShowInfo();
    }

    internal void ShowEffect()
    {
        RequirePlayer().ShowEffect();
    }

    internal void ChangePlayside()
    {
        RequirePlayer().ChangePlayside();
    }

    internal void IncreaseHighSpeed()
    {
        RequirePlayer().IncreaseHighSpeed();
    }

    internal void DecreaseHighSpeed()
    {
        RequirePlayer().DecreaseHighSpeed();
    }

    /// <summary>
    /// Gets the active playback title shown in the panel header.
    /// </summary>
    public string PlayerHeaderTitle => IsMoviePlayerHeaderActive ? moviePlayerHeaderTitle : bmsPlayerHeaderTitle;

    /// <summary>
    /// Gets the active playback subtitle shown in the panel header.
    /// </summary>
    public string PlayerHeaderSubtitle => IsMoviePlayerHeaderActive ? moviePlayerHeaderSubtitle : bmsPlayerHeaderSubtitle;

    /// <summary>
    /// Gets the active playback artist shown in the panel header.
    /// </summary>
    public string PlayerHeaderArtist => IsMoviePlayerHeaderActive ? moviePlayerHeaderArtist : bmsPlayerHeaderArtist;

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
    /// Gets the movie player title cache used when the movie panel is active.
    /// </summary>
    internal string MoviePlayerHeaderTitle => moviePlayerHeaderTitle;

    /// <summary>
    /// Gets the movie player subtitle cache used when the movie panel is active.
    /// </summary>
    internal string MoviePlayerHeaderSubtitle => moviePlayerHeaderSubtitle;

    /// <summary>
    /// Gets the movie player artist cache used when the movie panel is active.
    /// </summary>
    internal string MoviePlayerHeaderArtist => moviePlayerHeaderArtist;

    /// <summary>
    /// Gets or sets the internal player volume stored in application settings.
    /// </summary>
    public int PlayerVolume
    {
        get => Settings.Default.uBMplayVolume;
        set
        {
            if (Settings.Default.uBMplayVolume != value)
            {
                Settings.Default.uBMplayVolume = value;
                RaisePropertyChanged(nameof(PlayerVolume));
                bmsPlayer?.VolumeChanged();
            }
        }
    }

    private bool IsMoviePlayerHeaderActive =>
        Settings.Default.PlayerPanelState == MainWindowViewModel.PanelState.MOVIE_PLAYER
        && (!string.IsNullOrWhiteSpace(moviePlayerHeaderTitle)
            || !string.IsNullOrWhiteSpace(moviePlayerHeaderSubtitle)
            || !string.IsNullOrWhiteSpace(moviePlayerHeaderArtist));

    /// <summary>
    /// Updates the BMS player header cache from the currently selected or playing BMS file.
    /// </summary>
    /// <param name="bmsFile">BMS file whose metadata should be displayed.</param>
    internal void SetBmsPlayerHeader(BMSFile bmsFile)
    {
        bool changed = SetHeaderValue(ref bmsPlayerHeaderTitle, GridRowResolver.GetBmsPlayerDisplayTitle(bmsFile))
            | SetHeaderValue(ref bmsPlayerHeaderSubtitle, GridRowResolver.GetBmsPlayerDisplaySubtitle(bmsFile))
            | SetHeaderValue(ref bmsPlayerHeaderArtist, GridRowResolver.GetBmsPlayerDisplayArtist(bmsFile));
        if (changed || !IsMoviePlayerHeaderActive)
        {
            RaisePlayerHeaderPropertiesChanged();
        }
    }

    /// <summary>
    /// Updates the movie player header cache from the selected row while keeping BMS metadata available for fallback display.
    /// </summary>
    /// <param name="row">Row whose display metadata should be used for the movie panel.</param>
    internal void SetMoviePlayerHeader(object row)
    {
        bool changed = SetHeaderValue(ref moviePlayerHeaderTitle, GridRowResolver.GetDisplayRawTitle(row))
            | SetHeaderValue(ref moviePlayerHeaderSubtitle, GridRowResolver.GetDisplaySubtitle(row))
            | SetHeaderValue(ref moviePlayerHeaderArtist, GridRowResolver.GetDisplayArtist(row));
        if (changed || IsMoviePlayerHeaderActive)
        {
            RaisePlayerHeaderPropertiesChanged();
        }
    }

    /// <summary>
    /// Re-raises active header bindings when an external setting changes which panel header source is visible.
    /// </summary>
    internal void NotifyPlayerHeaderSourceChanged()
    {
        RaisePlayerHeaderPropertiesChanged();
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
        Dispatcher dispatcher = uiDispatcherProvider();
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.DataBind, action);
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
