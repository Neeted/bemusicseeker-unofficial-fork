using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private BMSTableSimpleCategorized externalTableListCatalog;

    private bool isLoadingExternalTableList;

    private readonly object externalTableListLoadSyncRoot = new();

    private CancellationTokenSource externalTableListLoadCancellation;

    private long externalTableListLoadVersion;

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
    /// Loads and projects the optional external table-list catalog after startup becomes operable.
    /// </summary>
    /// <param name="tableListUrl">The captured table-list API URI.</param>
    /// <param name="fetchTableInfoAsync">The asynchronous external-table fetch boundary.</param>
    internal async Task LoadExternalTableCollectionAsync(
        Uri tableListUrl,
        Func<Uri, CancellationToken, Task<IReadOnlyList<BMSTableSimple>>> fetchTableInfoAsync)
    {
        if (tableListUrl == null)
        {
            throw new ArgumentNullException(nameof(tableListUrl));
        }
        if (fetchTableInfoAsync == null)
        {
            throw new ArgumentNullException(nameof(fetchTableInfoAsync));
        }

        CancellationTokenSource previousCancellation;
        CancellationTokenSource currentCancellation = new();
        CancellationToken currentToken = currentCancellation.Token;
        long requestVersion;
        lock (externalTableListLoadSyncRoot)
        {
            previousCancellation = externalTableListLoadCancellation;
            externalTableListLoadCancellation = currentCancellation;
            requestVersion = ++externalTableListLoadVersion;
        }
        CancelExternalTableListLoad(previousCancellation);

        try
        {
            await ApplyExternalTableListLoadingStateAsync(
                requestVersion,
                currentCancellation,
                isLoading: true).ConfigureAwait(false);
            IReadOnlyList<BMSTableSimple> tableInfo = await fetchTableInfoAsync(
                tableListUrl,
                currentToken).ConfigureAwait(false);
            currentToken.ThrowIfCancellationRequested();
            BMSTableSimpleCategorized catalog = BuildExternalTableListCatalog(tableInfo);
            await ApplyExternalTableListCatalogAsync(
                requestVersion,
                currentCancellation,
                catalog).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (currentToken.IsCancellationRequested)
        {
            await ApplyExternalTableListLoadingStateAsync(
                requestVersion,
                currentCancellation,
                isLoading: false).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            externalPlaylistImportWarningLog(
                exception,
                "External table-list catalog load failed.");
            await ApplyExternalTableListLoadingStateAsync(
                requestVersion,
                currentCancellation,
                isLoading: false).ConfigureAwait(false);
        }
        finally
        {
            lock (externalTableListLoadSyncRoot)
            {
                if (ReferenceEquals(externalTableListLoadCancellation, currentCancellation))
                {
                    externalTableListLoadCancellation = null;
                }
            }
            currentCancellation.Dispose();
        }
    }

    /// <summary>
    /// Cancels the optional external table-list request during coordinated shutdown.
    /// </summary>
    internal void CancelExternalTableCollectionLoadForShutdown()
    {
        CancellationTokenSource cancellation;
        lock (externalTableListLoadSyncRoot)
        {
            cancellation = externalTableListLoadCancellation;
            externalTableListLoadCancellation = null;
            externalTableListLoadVersion++;
        }
        CancelExternalTableListLoad(cancellation);
        dispatchPresentation(() => IsLoadingExternalCollectionBMSTables = false);
    }

    private async Task ApplyExternalTableListCatalogAsync(
        long requestVersion,
        CancellationTokenSource requestCancellation,
        BMSTableSimpleCategorized catalog)
    {
        await playlistRestoreUiApplyScheduler(() =>
        {
            if (!IsCurrentExternalTableListLoad(requestVersion, requestCancellation))
            {
                return;
            }
            BMSExternalTableListExt = catalog;
            IsLoadingExternalCollectionBMSTables = false;
        }).ConfigureAwait(false);
    }

    private async Task ApplyExternalTableListLoadingStateAsync(
        long requestVersion,
        CancellationTokenSource requestCancellation,
        bool isLoading)
    {
        await playlistRestoreUiApplyScheduler(() =>
        {
            if (IsCurrentExternalTableListLoad(requestVersion, requestCancellation))
            {
                IsLoadingExternalCollectionBMSTables = isLoading;
            }
        }).ConfigureAwait(false);
    }

    private bool IsCurrentExternalTableListLoad(
        long requestVersion,
        CancellationTokenSource requestCancellation)
    {
        lock (externalTableListLoadSyncRoot)
        {
            return requestVersion == externalTableListLoadVersion
                && ReferenceEquals(externalTableListLoadCancellation, requestCancellation)
                && !requestCancellation.IsCancellationRequested;
        }
    }

    private static void CancelExternalTableListLoad(CancellationTokenSource cancellation)
    {
        if (cancellation == null)
        {
            return;
        }
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
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
