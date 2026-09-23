using System;

namespace Ribbit.Media;

public interface IImageLoader<TEXTURE> : IImageLoader, IDisposable
{
    TEXTURE Image { get; }
}
public interface IImageLoader : IDisposable
{
    string FileName { get; }

    bool CanSeek { get; }

    TimeSpan CurrentTime { get; set; }

    TimeSpan Duration { get; }

    void Attach();

    void Detach();

    void Suspend();
}
