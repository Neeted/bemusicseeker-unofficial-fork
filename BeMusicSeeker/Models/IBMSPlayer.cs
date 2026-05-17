using System;
using System.ComponentModel;

namespace BeMusicSeeker.Models;

internal interface IBMSPlayer : INotifyPropertyChanged
{
    string ExePath { get; set; }

    IntPtr ParentHandle { set; }

    TimeSpan Duration { get; }

    TimeSpan CurrentTime { get; set; }

    TimeSpan StopTime { get; }

    TimeSpan BmsDuration { get; }

    TimeSpan MusicDuration { get; }

    int CurrentVoices { get; }

    int MaxVoices { get; }

    int NoteDensity { get; }

    int NoteDensityMax { get; }

    int Bpm { get; }

    int MinBpm { get; }

    int MaxBpm { get; }

    double Total { get; }

    int Combo { get; }

    int Notes { get; }

    int Measure { get; }

    int LastMeasure { get; }

    void CloseProcess();

    void PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null);

    void RestartPlayingBMSfile();

    void PausePlayingBMSfileToggle();

    void FastForwardPlayingBMSfileStart();

    void FastForwardPlayingBMSfileEnd();

    void FastBackwardPlayingBMSfileStart();

    void FastBackwardPlayingBMSfileEnd();

    void ShowInfo();

    void ShowEffect();

    void ChangePlayside();

    void IncreaseHighSpeed();

    void DecreaseHighSpeed();

    void VolumeChanged();
}
