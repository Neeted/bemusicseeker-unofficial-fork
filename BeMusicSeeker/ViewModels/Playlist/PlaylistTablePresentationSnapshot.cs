using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Settings and play-history presentation data for one playlist.
/// The snapshot prevents settings editing from mutating the playlist owner's live row objects.
/// </summary>
internal sealed class PlaylistTablePresentationSnapshot
{
    internal PlaylistTablePresentationSnapshot(
        int? playlistId,
        string name,
        string originalName,
        string symbol,
        string originalSymbol,
        bool isRootFolder,
        string outputDirectory,
        string customFolderOutputBaseName)
    {
        PlaylistId = playlistId;
        Name = name;
        OriginalName = originalName;
        Symbol = symbol;
        OriginalSymbol = originalSymbol;
        IsRootFolder = isRootFolder;
        OutputDirectory = outputDirectory;
        CustomFolderOutputBaseName = customFolderOutputBaseName;
    }

    internal int? PlaylistId { get; }

    internal string Name { get; }

    internal string OriginalName { get; }

    internal string Symbol { get; }

    internal string OriginalSymbol { get; }

    internal bool IsRootFolder { get; }

    internal string OutputDirectory { get; }

    internal string CustomFolderOutputBaseName { get; }

    internal static PlaylistTablePresentationSnapshot From(BMSTable table)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        return new PlaylistTablePresentationSnapshot(
            table.playlist_id,
            table.name,
            table.org_name,
            table.symbol,
            table.org_symbol,
            table.is_root_folder,
            table.Output_dir,
            table.custom_folder_output_base_name);
    }
}
