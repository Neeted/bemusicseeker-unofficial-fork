using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryChartDigestChange(
    LibraryChartKind kind,
    string path,
    string oldMd5,
    string oldSha256,
    string newMd5,
    string newSha256)
{
    public LibraryChartKind Kind { get; } = kind;

    public string Path { get; } = RequireOwnedPath(path);

    public string OldMd5 { get; } = RequireOwnedMd5(oldMd5);

    public string OldSha256 { get; } = oldSha256;

    public string NewMd5 { get; } = RequireOwnedMd5(newMd5);

    public string NewSha256 { get; } = newSha256;

    public bool HasDigestChange => !string.Equals(OldMd5, NewMd5, System.StringComparison.OrdinalIgnoreCase)
        || !string.Equals(OldSha256, NewSha256, System.StringComparison.OrdinalIgnoreCase);

    public bool Md5Changed => !string.Equals(OldMd5, NewMd5, System.StringComparison.OrdinalIgnoreCase);

    public bool Sha256Changed => !string.Equals(OldSha256, NewSha256, System.StringComparison.OrdinalIgnoreCase);

    public bool PrimaryHashChanged => !string.Equals(OldMd5, NewMd5, System.StringComparison.OrdinalIgnoreCase);

    public static LibraryChartDigestChange FromBms(BMSFile file, string oldMd5, string oldSha256)
    {
        if (file == null
            || string.IsNullOrWhiteSpace(file.path)
            || string.IsNullOrWhiteSpace(oldMd5)
            || string.IsNullOrWhiteSpace(file.hash))
        {
            return null;
        }
        return new LibraryChartDigestChange(
            LibraryChartKind.Bms,
            file.path,
            oldMd5,
            oldSha256,
            file.hash,
            file.sha256);
    }

    public static LibraryChartDigestChange FromBmson(LR2SongDBExtended.bmson_song song, string oldMd5, string oldSha256)
    {
        if (song == null
            || string.IsNullOrWhiteSpace(song.path)
            || string.IsNullOrWhiteSpace(oldMd5)
            || string.IsNullOrWhiteSpace(song.md5))
        {
            return null;
        }
        return new LibraryChartDigestChange(
            LibraryChartKind.Bmson,
            song.path,
            oldMd5,
            oldSha256,
            song.md5,
            song.sha256);
    }

    private static string RequireOwnedPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            ? path
            : throw new System.InvalidOperationException("Owned chart digest changes require a current owner path.");
    }

    private static string RequireOwnedMd5(string md5)
    {
        return !string.IsNullOrWhiteSpace(md5)
            ? md5
            : throw new System.InvalidOperationException("Owned chart digest changes require current owner md5.");
    }
}
