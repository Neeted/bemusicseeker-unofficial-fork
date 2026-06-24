using OggVorbisDotNet64;
using SevenZipExtractor;

namespace BeMusicSeeker.Models.Utils;

internal static class ManagedDependencyPreloader
{
    internal static void Preload()
    {
        _ = typeof(ArchiveFile).Assembly;
        _ = typeof(OggDecodeStream).Assembly;
    }
}
