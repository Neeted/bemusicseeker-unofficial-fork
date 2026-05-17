using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

internal static class PlaylistTableHeaderSpecialFolderExt
{
    private static readonly Dictionary<PlaylistTableHeaderSpecialFolder, Tuple<string, MainWindowViewModel.PlaylistFilterType>> table = new()
    {
    {
        PlaylistTableHeaderSpecialFolder.NOT_OWNED,
        new Tuple<string, MainWindowViewModel.PlaylistFilterType>("[NO SONG]", MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected)
    } };

    private static readonly Dictionary<string, PlaylistTableHeaderSpecialFolder> tableReverse0 = table.ToDictionary(kv => kv.Value.Item1, kv => kv.Key);

    internal static string ToDisplayName(this PlaylistTableHeaderSpecialFolder folder)
    {
        return table[folder].Item1;
    }

    internal static MainWindowViewModel.PlaylistFilterType ToPlaylistFilterType(this PlaylistTableHeaderSpecialFolder folder)
    {
        return table[folder].Item2;
    }

    internal static PlaylistTableHeaderSpecialFolder FromDisplayName(string dname)
    {
        return tableReverse0[dname];
    }
}
