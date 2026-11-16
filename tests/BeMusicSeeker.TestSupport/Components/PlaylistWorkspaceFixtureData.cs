namespace BeMusicSeeker.TestSupport.Components;

/// <summary>設定とプレイリストの実媒体・workspace接続で共用する既存fixtureを保持します。</summary>
internal static class PlaylistWorkspaceFixtureData
{
    internal static string CreatePlaylistRestoreDump(int playlistId, string name, string symbol)
    {
        string playlistSql = "INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) VALUES ("
            + playlistId
            + ", '"
            + name
            + "', '"
            + symbol
            + "', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);";
        return string.Join("\v" + Environment.NewLine, [playlistSql, string.Empty, string.Empty]);
    }
}
