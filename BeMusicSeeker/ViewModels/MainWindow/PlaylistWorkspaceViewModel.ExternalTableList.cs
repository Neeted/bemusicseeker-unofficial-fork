using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private BMSTableSimpleCategorized externalTableListCatalog;

    private bool isLoadingExternalTableList;

    /// <summary>
    /// Gets the table-list catalog shown by the playlist import menu.
    /// </summary>
    public BMSTableSimpleCategorized BMSExternalTableListExt
    {
        get => externalTableListCatalog;
        private set
        {
            if (!ReferenceEquals(externalTableListCatalog, value))
            {
                externalTableListCatalog = value;
                RaisePropertyChanged(nameof(BMSExternalTableListExt));
            }
        }
    }

    /// <summary>
    /// Gets whether the external table-list catalog is being loaded.
    /// </summary>
    private bool IsLoadingExternalCollectionBMSTables
    {
        get => isLoadingExternalTableList;
        set
        {
            if (isLoadingExternalTableList != value)
            {
                isLoadingExternalTableList = value;
                RaisePropertyChanged(nameof(IsLoadingExternalCollectionBMSTables));
            }
        }
    }

    internal PlaylistRootContextMenuAvailability CapturePlaylistRootContextMenuAvailability()
    {
        bool minimumInitializationLockHeld = IsWriteLockHeldBMSTablesInitializeMin;
        return new PlaylistRootContextMenuAvailability(
            canCreatePlaylist: !minimumInitializationLockHeld
                && !IsWriteLockHeldBMSTables
                && !IsWriteLockHeldAnyBMSTable,
            canLoadPlaylistUri: !minimumInitializationLockHeld,
            canLoadPlaylistCollection: !minimumInitializationLockHeld && !IsLoadingExternalCollectionBMSTables,
            canLoadBuiltInTables: !minimumInitializationLockHeld);
    }

    /// <summary>
    /// Loads and projects the external table-list catalog used by playlist import.
    /// The existing startup route intentionally keeps its silent failure behavior.
    /// </summary>
    /// <param name="tableListUrl">The captured table-list API URI.</param>
    internal void LoadExternalTableCollection(Uri tableListUrl)
    {
        try
        {
            IsLoadingExternalCollectionBMSTables = true;
            List<BMSTableSimple> tableInfo = BMSPlaylist.GetBMSTableInfo(tableListUrl);
            BMSExternalTableListExt = BuildExternalTableListCatalog(tableInfo);
            IsLoadingExternalCollectionBMSTables = false;
        }
        catch
        {
        }
    }

    /// <summary>
    /// Builds the two-level table-list menu hierarchy while preserving the API order rules.
    /// </summary>
    internal static BMSTableSimpleCategorized BuildExternalTableListCatalog(
        IEnumerable<BMSTableSimple> tableInfo)
    {
        List<BMSTableSimple> entries = [.. tableInfo];
        var catalog = new BMSTableSimpleCategorized
        {
            Children = [.. entries
                .Select(table => table.tag1)
                .Distinct()
                .Select(name => new BMSTableSimpleCategorized
                {
                    name = name
                })]
        };

        foreach (BMSTableSimpleCategorized group in catalog.Children)
        {
            string tag1 = group.name;
            group.Children = [.. entries
                .Where(table => table.tag1 == tag1 && string.IsNullOrWhiteSpace(table.tag2))
                .Select(table => new BMSTableSimpleCategorized(table))];

            List<BMSTableSimple> nestedEntries = [.. entries
                .Where(table => table.tag1 == tag1 && !string.IsNullOrWhiteSpace(table.tag2))];
            foreach (string tag2 in nestedEntries
                .Select(table => table.tag2)
                .Distinct()
                .OrderBy(value => value))
            {
                group.Children.Add(new BMSTableSimpleCategorized
                {
                    name = tag2,
                    Children = [.. nestedEntries
                        .Where(table => table.tag2 == tag2)
                        .Select(table => new BMSTableSimpleCategorized(table))
                        .OrderBy(table => table.name)]
                });
            }
        }

        return catalog;
    }
}

internal sealed class PlaylistRootContextMenuAvailability
{
    internal PlaylistRootContextMenuAvailability(
        bool canCreatePlaylist,
        bool canLoadPlaylistUri,
        bool canLoadPlaylistCollection,
        bool canLoadBuiltInTables)
    {
        CanCreatePlaylist = canCreatePlaylist;
        CanLoadPlaylistUri = canLoadPlaylistUri;
        CanLoadPlaylistCollection = canLoadPlaylistCollection;
        CanLoadBuiltInTables = canLoadBuiltInTables;
    }

    internal bool CanCreatePlaylist { get; }

    internal bool CanLoadPlaylistUri { get; }

    internal bool CanLoadPlaylistCollection { get; }

    internal bool CanLoadBuiltInTables { get; }
}
