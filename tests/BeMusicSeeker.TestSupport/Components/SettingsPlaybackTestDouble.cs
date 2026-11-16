using System.ComponentModel;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.TestSupport.Components;


internal sealed class RecordingPlaybackPlayer : IBMSPlayer
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string ExePath { get; set; } = string.Empty;

    public TimeSpan Duration => TimeSpan.Zero;

    public TimeSpan CurrentTime { get; set; }

    public TimeSpan StopTime => TimeSpan.Zero;

    public TimeSpan BmsDuration => TimeSpan.Zero;

    public TimeSpan MusicDuration => TimeSpan.Zero;

    public int CurrentVoices => 0;

    public int MaxVoices => 0;

    public int NoteDensity => 0;

    public int NoteDensityMax => 0;

    public int Bpm => 0;

    public int MinBpm => 0;

    public int MaxBpm => 0;

    public double Total => 0;

    public int Combo => 0;

    public int Notes => 0;

    public int Measure => 0;

    public int LastMeasure => 0;

    public int VolumeChangedCount { get; private set; }

    public void Raise(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void CloseProcess()
    {
    }

    public Task PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null)
    {
        return Task.CompletedTask;
    }

    public void RestartPlayingBMSfile()
    {
    }

    public void PausePlayingBMSfileToggle()
    {
    }

    public void FastForwardPlayingBMSfileStart()
    {
    }

    public void FastForwardPlayingBMSfileEnd()
    {
    }

    public void FastBackwardPlayingBMSfileStart()
    {
    }

    public void FastBackwardPlayingBMSfileEnd()
    {
    }

    public void ShowInfo()
    {
    }

    public void ShowEffect()
    {
    }

    public void ChangePlayside()
    {
    }

    public void IncreaseHighSpeed()
    {
    }

    public void DecreaseHighSpeed()
    {
    }

    public void VolumeChanged(int volume)
    {
        VolumeChangedCount++;
    }
}
