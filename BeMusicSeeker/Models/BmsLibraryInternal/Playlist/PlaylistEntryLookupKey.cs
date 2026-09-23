namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum PlaylistEntryLookupKeyKind
{
    None,
    Md5,
    Sha256
}

internal readonly struct PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind kind, string hash)
{
    internal PlaylistEntryLookupKeyKind Kind { get; } = kind;

    internal string Hash { get; } = string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();

    internal bool HasValue => Kind != PlaylistEntryLookupKeyKind.None && !string.IsNullOrWhiteSpace(Hash);

    internal static PlaylistEntryLookupKey FromEntry(BMSTableEntry entry)
    {
        if (entry == null || entry.is_removed)
        {
            return default;
        }
        if (!string.IsNullOrWhiteSpace(entry.md5))
        {
            return new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, entry.md5);
        }
        return string.IsNullOrWhiteSpace(entry.sha256)
            ? default
            : new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Sha256, entry.sha256);
    }
}
