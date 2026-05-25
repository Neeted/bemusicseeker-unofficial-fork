using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryChartHashChange(
    LibraryChartKind kind,
    string path,
    string oldMd5,
    string oldSha256,
    string newMd5,
    string newSha256)
{
    public LibraryChartKind Kind { get; } = kind;

    public string Path { get; } = path;

    public string OldMd5 { get; } = oldMd5;

    public string OldSha256 { get; } = oldSha256;

    public string NewMd5 { get; } = newMd5;

    public string NewSha256 { get; } = newSha256;

    public bool HasHashChange => !string.Equals(OldMd5, NewMd5, System.StringComparison.OrdinalIgnoreCase)
        || !string.Equals(OldSha256, NewSha256, System.StringComparison.OrdinalIgnoreCase);

    public bool Md5Changed => !string.Equals(OldMd5, NewMd5, System.StringComparison.OrdinalIgnoreCase);

    public bool Sha256Changed => !string.Equals(OldSha256, NewSha256, System.StringComparison.OrdinalIgnoreCase);

    public bool PrimaryHashChanged => !string.Equals(
        SelectPrimaryHash(OldMd5, OldSha256),
        SelectPrimaryHash(NewMd5, NewSha256),
        System.StringComparison.OrdinalIgnoreCase);

    public static LibraryChartHashChange FromBms(BMSFile file, string oldMd5, string oldSha256)
    {
        return file == null
            ? null
            : new LibraryChartHashChange(
                LibraryChartKind.Bms,
                file.path,
                oldMd5,
                oldSha256,
                file.hash,
                file.sha256);
    }

    public static LibraryChartHashChange FromBmson(LR2SongDBExtended.bmson_song song, string oldMd5, string oldSha256)
    {
        return song == null
            ? null
            : new LibraryChartHashChange(
                LibraryChartKind.Bmson,
                song.path,
                oldMd5,
                oldSha256,
                song.md5,
                song.sha256);
    }

    private static string SelectPrimaryHash(string md5, string sha256)
    {
        return !string.IsNullOrWhiteSpace(md5)
            ? md5.Trim()
            : string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
    }
}
