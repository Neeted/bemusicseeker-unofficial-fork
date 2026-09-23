using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable lookup facts used to select chart snapshots for playlist-reference apply.
/// It deliberately contains no live playlist-table references.
/// </summary>
internal sealed class PlaylistReferenceLookupKeys
{
    internal PlaylistReferenceLookupKeys(
        IEnumerable<string> md5Hashes,
        IEnumerable<string> sha256Hashes)
    {
        string[] md5Snapshot = [..
            (md5Hashes ?? [])
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .Select(hash => hash.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        string[] sha256Snapshot = [..
            (sha256Hashes ?? [])
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .Select(hash => hash.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        Md5Hashes = Array.AsReadOnly(md5Snapshot);
        Sha256Hashes = Array.AsReadOnly(sha256Snapshot);
        md5Lookup = new HashSet<string>(md5Snapshot, StringComparer.OrdinalIgnoreCase);
        sha256Lookup = new HashSet<string>(sha256Snapshot, StringComparer.OrdinalIgnoreCase);
    }

    private readonly HashSet<string> md5Lookup;

    private readonly HashSet<string> sha256Lookup;

    internal IReadOnlyList<string> Md5Hashes { get; }

    internal IReadOnlyList<string> Sha256Hashes { get; }

    internal bool HasAny => Md5Hashes.Count > 0 || Sha256Hashes.Count > 0;

    internal bool ContainsMd5(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash) && md5Lookup.Contains(hash.Trim());
    }

    internal bool ContainsSha256(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash) && sha256Lookup.Contains(hash.Trim());
    }
}
