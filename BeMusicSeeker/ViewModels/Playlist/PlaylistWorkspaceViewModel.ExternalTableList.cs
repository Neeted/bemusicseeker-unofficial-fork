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

    private PlaylistImportCatalogState externalTableListState;

    private readonly object externalTableListLoadSyncRoot = new();

    private CancellationTokenSource externalTableListLoadCancellation;

    private long externalTableListLoadVersion;

    private bool externalTableListLoadShutdownRequested;

    /// <summary>
    /// インポート画面の候補に使う外部カタログを取得します。
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

    /// <summary>外部カタログの準備・取得・成功・失敗を区別する状態です。</summary>
    internal PlaylistImportCatalogState ExternalTableListState
    {
        get => externalTableListState;
        private set
        {
            if (externalTableListState != value)
            {
                externalTableListState = value;
                RaisePropertyChanged(nameof(ExternalTableListState));
            }
        }
    }

    /// <summary>新規プレイリストを作成できるかを、既存の書込み状態から取得します。</summary>
    internal bool CanCreatePlaylist => !IsWriteLockHeldBMSTablesInitializeMin
        && !IsWriteLockHeldBMSTables && !IsWriteLockHeldAnyBMSTable;

    /// <summary>
    /// 起動後の任意カタログを取得し、成功・空・失敗を画面へ通知します。終了受付の閉鎖後は通信・表示を開始しません。
    /// </summary>
    /// <param name="tableListUrl">取得先として捕捉したカタログURI。</param>
    /// <param name="fetchTableInfoAsync">非同期カタログ取得の境界。</param>
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
        CancellationTokenSource currentCancellation;
        CancellationToken currentToken;
        long requestVersion;
        lock (externalTableListLoadSyncRoot)
        {
            if (externalTableListLoadShutdownRequested) { return; }
            currentCancellation = new();
            currentToken = currentCancellation.Token;
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
                PlaylistImportCatalogState.Loading).ConfigureAwait(false);
            currentToken.ThrowIfCancellationRequested();
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
                PlaylistImportCatalogState.NotStarted).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            externalPlaylistImportWarningLog(
                exception,
                "External table-list catalog load failed.");
            await ApplyExternalTableListLoadingStateAsync(
                requestVersion,
                currentCancellation,
                PlaylistImportCatalogState.Failed).ConfigureAwait(false);
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
    /// 協調終了時に同じ画面寿命での取得受付を不可逆に閉じ、登録済み取得へ取消を届けます。未開始仕事の後着登録も拒否します。
    /// </summary>
    internal void CancelExternalTableCollectionLoadForShutdown()
    {
        CancellationTokenSource cancellation;
        lock (externalTableListLoadSyncRoot)
        {
            externalTableListLoadShutdownRequested = true;
            cancellation = externalTableListLoadCancellation;
            externalTableListLoadCancellation = null;
            externalTableListLoadVersion++;
        }
        CancelExternalTableListLoad(cancellation);
        dispatchPresentation(() => ExternalTableListState = PlaylistImportCatalogState.NotStarted);
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
            ExternalTableListState = PlaylistImportCatalogState.Loaded;
        }).ConfigureAwait(false);
    }

    private async Task ApplyExternalTableListLoadingStateAsync(
        long requestVersion,
        CancellationTokenSource requestCancellation,
        PlaylistImportCatalogState state)
    {
        await playlistRestoreUiApplyScheduler(() =>
        {
            if (IsCurrentExternalTableListLoad(requestVersion, requestCancellation))
            {
                ExternalTableListState = state;
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
    /// 既存の一覧順序規則を維持し、二段階の分類階層を構築します。
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
