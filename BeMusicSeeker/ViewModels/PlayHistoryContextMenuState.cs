namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryContextMenuState
{
    internal const string CopyRawHashKind = "RawHash";
    internal const string CopyMd5Kind = "Md5";
    internal const string CopyRepositorySha256Kind = "RepositorySha256";

    private PlayHistoryContextMenuState(string rawHash, string md5, string repositorySha256)
    {
        RawHash = rawHash ?? string.Empty;
        Md5 = md5 ?? string.Empty;
        RepositorySha256 = repositorySha256 ?? string.Empty;
    }

    internal string RawHash { get; }

    internal string Md5 { get; }

    internal string RepositorySha256 { get; }

    internal bool IsResolved => !string.IsNullOrWhiteSpace(Md5);

    internal bool CanOpenRepository => IsResolved && !string.IsNullOrWhiteSpace(RepositorySha256);

    internal bool CanCopyRawHash => !IsResolved && !string.IsNullOrWhiteSpace(RawHash);

    internal bool CanCopyMd5 => IsResolved && !string.IsNullOrWhiteSpace(Md5);

    internal bool CanCopyRepositorySha256 => IsResolved && !string.IsNullOrWhiteSpace(RepositorySha256);

    internal bool HasVisibleItem => CanOpenRepository || CanCopyRawHash || CanCopyMd5 || CanCopyRepositorySha256;

    internal static bool TryCreate(object row, out PlayHistoryContextMenuState state)
    {
        state = null;
        if (row is not PlayHistoryRow playHistoryRow)
        {
            return false;
        }

        string md5 = playHistoryRow.ResolvedChart != null ? GridRowResolver.GetHash(playHistoryRow) : null;
        string repositorySha256 = playHistoryRow.ResolvedChart != null ? GridRowResolver.GetRepositorySha256(playHistoryRow) : null;
        state = new PlayHistoryContextMenuState(playHistoryRow.RawHash, md5, repositorySha256);
        return state.HasVisibleItem;
    }

    internal string GetCopyValue(string kind)
    {
        return kind switch
        {
            CopyRawHashKind => CanCopyRawHash ? RawHash : null,
            CopyMd5Kind => CanCopyMd5 ? Md5 : null,
            CopyRepositorySha256Kind => CanCopyRepositorySha256 ? RepositorySha256 : null,
            _ => null
        };
    }
}
