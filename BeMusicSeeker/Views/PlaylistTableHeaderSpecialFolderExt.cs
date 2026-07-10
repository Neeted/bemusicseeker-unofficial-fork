using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal static class PlaylistTableHeaderSpecialFolderExt
{
    private static readonly Dictionary<PlaylistTableHeaderSpecialFolder, Tuple<string, PlaylistDetailFilter>> table = new()
    {
    {
        PlaylistTableHeaderSpecialFolder.NOT_OWNED,
        new Tuple<string, PlaylistDetailFilter>("[NO SONG]", PlaylistDetailFilter.PlaylistNotOwnedFilterSelected)
    } };

    private static readonly Dictionary<string, PlaylistTableHeaderSpecialFolder> tableReverse0 = table.ToDictionary(kv => kv.Value.Item1, kv => kv.Key);

    internal static string ToDisplayName(this PlaylistTableHeaderSpecialFolder folder)
    {
        return table[folder].Item1;
    }

    internal static PlaylistDetailFilter ToPlaylistFilterType(this PlaylistTableHeaderSpecialFolder folder)
    {
        return table[folder].Item2;
    }

    internal static PlaylistTableHeaderSpecialFolder FromDisplayName(string dname)
    {
        return tableReverse0[dname];
    }
}
