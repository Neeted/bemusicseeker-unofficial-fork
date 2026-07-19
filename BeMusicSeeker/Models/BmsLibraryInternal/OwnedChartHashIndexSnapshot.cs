using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class OwnedChartHashIndexSnapshot
{
    internal HashSet<string> Md5Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> Sha256Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Immutable, versioned catalog hash projection consumed by aggregate summaries.
/// </summary>
internal sealed class OwnedChartHashIndexVersionedSnapshot
{
    private readonly HashSet<string> md5Hashes;

    private readonly HashSet<string> sha256Hashes;

    private readonly IReadOnlyCollection<string> md5HashSnapshot;

    private readonly IReadOnlyCollection<string> sha256HashSnapshot;

    internal OwnedChartHashIndexVersionedSnapshot(
        OwnedChartHashIndexSnapshot source,
        int version,
        long buildElapsedMs,
        int invalidationVersion,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        md5Hashes = new HashSet<string>(source?.Md5Hashes ?? [], StringComparer.OrdinalIgnoreCase);
        sha256Hashes = new HashSet<string>(source?.Sha256Hashes ?? [], StringComparer.OrdinalIgnoreCase);
        md5HashSnapshot = new ReadOnlyCollection<string>([.. md5Hashes]);
        sha256HashSnapshot = new ReadOnlyCollection<string>([.. sha256Hashes]);
        Version = version;
        BuildElapsedMs = buildElapsedMs;
        InvalidationVersion = invalidationVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        BmsRowsVersion = bmsRowsVersion;
        BmsonRowsVersion = bmsonRowsVersion;
    }

    internal int Version { get; }

    internal long BuildElapsedMs { get; }

    internal int InvalidationVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal int BmsRowsVersion { get; }

    internal int BmsonRowsVersion { get; }

    internal IReadOnlyCollection<string> Md5Hashes => md5HashSnapshot;

    internal IReadOnlyCollection<string> Sha256Hashes => sha256HashSnapshot;

    internal int Md5Count => md5Hashes.Count;

    internal int Sha256Count => sha256Hashes.Count;

    internal bool ContainsMd5(string md5)
    {
        return !string.IsNullOrWhiteSpace(md5) && md5Hashes.Contains(md5);
    }

    internal bool ContainsSha256(string sha256)
    {
        return !string.IsNullOrWhiteSpace(sha256) && sha256Hashes.Contains(sha256);
    }
}
