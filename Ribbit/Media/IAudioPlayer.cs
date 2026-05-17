using System;

namespace Ribbit.Media;

public interface IAudioPlayer : IDisposable
{
    bool CanSeek { get; }

    TimeSpan CurrentTime { get; set; }

    TimeSpan Duration { get; }

    PlayState PlayState { get; }

    float Volume { get; set; }

    string FileName { get; }

    bool IsMuted { get; set; }

    float PlaybackRate { get; set; }

    void Pause();

    void Play(PlayWith with = PlayWith.RESTART);

    void Stop();
}
