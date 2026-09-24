using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Ribbit.Media.Audio;

/// <summary>一回の曲ロード内で、正規化したパスごとに復号音源を共有します。</summary>
internal sealed class AudioSourceCache
{
    private readonly ConcurrentDictionary<string, Lazy<DecodedAudio>> sources =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>正規化した絶対パスの音源を一度だけ復号して返します。</summary>
    internal DecodedAudio GetOrLoad(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Lazy<DecodedAudio> source = sources.GetOrAdd(
            fullPath,
            static normalizedPath => new Lazy<DecodedAudio>(
                () => AudioSourceLoader.Load(normalizedPath),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return source.Value;
    }
}
