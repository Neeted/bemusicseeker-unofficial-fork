using SQLite;

namespace BeMusicSeeker.Desktop.E2E.Tests.Fixtures;

/// <summary>DT01/03の独立したplaylist保存値を、生SQLの読取りで比較します。</summary>
internal static class DesktopPlaylistOracle
{
    /// <summary>専有DBをread-onlyで読み、全entryのMEMOと他保存列を不変snapshotへ写します。</summary>
    internal static Dictionary<string, StoredEntry> ReadEntries(string path)
    {
        using var db = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        // SQLのquoteでNULLと文字列を区別し、MEMO以外の全保存列を一つの不変値にする。
        // 製品のreader/modelを使わず、各呼出しで独立したDB snapshotを読む。
        const string otherColumns = "quote(playlist_id)||','||quote(md5)||','||quote(sha256)||','||quote(level)||','||quote(title)||','||quote(artist)||','||quote(folder)||','||quote(lr2_bmsid)||','||quote(url)||','||quote(url_diff)||','||quote(name_diff)||','||quote(org_md5)||','||quote(adddate)||','||quote(comment)||','||quote(is_removed)";
        return db.Query<RawEntry>("SELECT md5 AS Md5,sha256 AS Sha256,title AS Title,memo AS Memo," + otherColumns
            + " AS OtherValues FROM playlist_entry WHERE playlist_id=?", DesktopPlaylistFixture.PlaylistId)
            .ToDictionary(row => row.Md5, row => new StoredEntry(row.Sha256, row.Title, row.Memo, row.OtherValues), StringComparer.Ordinal);
    }

    /// <summary>製品modelと独立した一行の保存値です。OtherValuesはMEMO以外の全列です。</summary>
    internal sealed record StoredEntry(string Sha256, string Title, string? Memo, string OtherValues);

    // sqlite-netの読取り専用投影。取得後は不変snapshotへ移し、製品型と保存処理を共有しない。
    private sealed class RawEntry
    {
        public RawEntry() { }
        public string Md5 { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Memo { get; set; }
        public string OtherValues { get; set; } = string.Empty;
    }
}
