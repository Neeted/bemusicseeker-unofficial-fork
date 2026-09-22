using System;
using System.ComponentModel;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

internal interface IBMSPlayer : INotifyPropertyChanged
{
    string ExePath { get; set; }

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

    /// <summary>
    /// 指定した譜面の再生開始処理を行います。
    /// </summary>
    /// <remarks>
    /// 開始失敗時の開始途中資源の後片付けと、既に確立した外部プレイヤーを保持するかの判断は実装側が所有します。
    /// 呼出側は、この処理の失敗だけを理由に <see cref="CloseProcess"/> を呼びません。
    /// </remarks>
    Task PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null);

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

internal interface IExternalWindowPlayer
{
    void AttachWindowHost(IExternalPlayerWindowHost windowHost);
}
