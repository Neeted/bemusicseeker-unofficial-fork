using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns playback-panel presentation state while the shell keeps the actual player and UI host operations.
/// </summary>
public sealed class PlaybackPanelViewModel : ViewModel
{
    private string bmsPlayerHeaderTitle = string.Empty;

    private string bmsPlayerHeaderSubtitle = string.Empty;

    private string bmsPlayerHeaderArtist = string.Empty;

    private string moviePlayerHeaderTitle = string.Empty;

    private string moviePlayerHeaderSubtitle = string.Empty;

    private string moviePlayerHeaderArtist = string.Empty;

    /// <summary>
    /// Raised after the user changes the player volume and the active player needs to receive the new value.
    /// </summary>
    internal event EventHandler PlayerVolumeChanged;

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
                PlayerVolumeChanged?.Invoke(this, EventArgs.Empty);
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
}
