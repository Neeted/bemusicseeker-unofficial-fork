using OggVorbisDotNet64;

namespace BeMusicSeeker.Models.Utils;

internal static class ManagedDependencyPreloader
{
    internal static void Preload()
    {
        _ = typeof(OggDecodeStream).Assembly;
    }
}
