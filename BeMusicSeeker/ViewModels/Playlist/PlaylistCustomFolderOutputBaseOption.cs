using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed class PlaylistCustomFolderOutputBaseOption
{
    public PlaylistCustomFolderOutputBaseOption(string label, string baseName, bool isNoChange = false)
    {
        Label = label ?? string.Empty;
        BaseName = string.IsNullOrWhiteSpace(baseName) ? null : baseName;
        IsNoChange = isNoChange;
    }

    public string Label { get; }

    public string BaseName { get; }

    public bool IsNoChange { get; }
}

internal static class PlaylistCustomFolderOutputBaseOptions
{
    internal static IReadOnlyList<PlaylistCustomFolderOutputBaseOption> Create(
        string defaultOutputBaseDirectory = null,
        IEnumerable<string> additionalOutputBaseDirectories = null,
        bool includeNoChange = false)
    {
        List<PlaylistCustomFolderOutputBaseOption> options = [];
        if (includeNoChange)
        {
            options.Add(new PlaylistCustomFolderOutputBaseOption(
                BeMusicSeeker.Properties.Resources.Playlist_summary_bulk_no_change,
                null,
                isNoChange: true));
        }

        string defaultLabel = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(defaultOutputBaseDirectory);
        options.Add(new PlaylistCustomFolderOutputBaseOption(
            string.IsNullOrWhiteSpace(defaultLabel)
                ? BeMusicSeeker.Properties.Resources.Playlist_output?.TrimEnd(':', ' ')
                : defaultLabel,
            null));

        foreach (CustomFolderOutputBaseEntry entry in CustomFolderOutputBaseRegistry.CreateAdditionalEntries(
            additionalOutputBaseDirectories ?? []))
        {
            if (!options.Any(option =>
                    !option.IsNoChange
                    && string.Equals(option.Label, entry.Name, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new PlaylistCustomFolderOutputBaseOption(entry.Name, entry.Name));
            }
        }

        return options;
    }
}
