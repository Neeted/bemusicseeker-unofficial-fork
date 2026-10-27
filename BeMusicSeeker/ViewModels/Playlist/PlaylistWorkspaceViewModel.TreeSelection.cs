using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly ObservableCollection<BMSTable> emptyPlaylistTreeTables;

    private ObservableCollection<BMSTable> playlistTreeTables;

    private BMSPlaylist playlistTreeStore;

    private ObservableCollection<BMSTable> observedPlaylistTreeTables;

    private long playlistTreeNotificationGeneration;

    private long playlistCatalogVersion;

    private int playlistCatalogNotificationQueued;

    private PlaylistHydrationCompletionReceipt playlistHydrationCompletionReceipt;

    private PlaylistEntriesHydrationVersionChangedEventArgs pendingPlaylistHydrationCompletion;

    private readonly object playlistHydrationNotificationDispatchLock = new();

    private readonly object playlistTreeStoreSyncRoot = new();

    private bool isPlaylistTreeExpanded = true;

    private readonly object playlistDetailSelectionSyncRoot = new();

    private PlaylistDetailSelection playlistDetailSelection;

    private long playlistDetailSelectionRevision;

    internal event EventHandler<PlaylistTreeSelectionActivatedEventArgs> TreeSelectionActivated;

    /// <summary>有効な明示選択の版を更新した後、UIへ配送する前に同期通知します。依存更新や選択解除は新しい画面受付にしません。</summary>
    internal event EventHandler<PlaylistTreeSelectionActivatedEventArgs> TreeSelectionAccepted;

    internal event EventHandler PlaylistTablesPresentationChanged;

    internal event EventHandler<PlaylistCatalogChangedEventArgs> PlaylistCatalogChanged;

    internal event EventHandler PlaylistKeywordValueCandidatesChanged;

    private void DispatchPlaylistKeywordValueCandidatesChanged()
    {
        dispatchPresentation(() => PlaylistKeywordValueCandidatesChanged?.Invoke(this, EventArgs.Empty));
    }

    internal event EventHandler<PlaylistEntriesHydrationVersionChangedEventArgs> PlaylistEntriesHydrationRequested;

    internal event EventHandler<PlaylistEntriesHydrationVersionChangedEventArgs> PlaylistEntriesHydrationCompleted;

    /// <summary>
    /// プレイリストツリーが表示するテーブル source です。起動前は注入された空のコレクションを返し、起動後は <see cref="BMSPlaylist.BMSTables" /> と同じコレクション identity、順序、階層を保持します。
    /// </summary>
    public ObservableCollection<BMSTable> PlaylistTreeTables => playlistTreeTables;

    /// <summary>
    /// Captures the playlist tree source while holding the playlist reader lock.
    /// </summary>
    internal List<BMSTable> CapturePlaylistTreeTablesSnapshot()
    {
        List<BMSTable> tableSnapshot = [];
        BMSPlaylist playlistStore = getPlaylistStore();
        bool readerLockAcquired = false;
        try
        {
            if (playlistStore != null)
            {
                playlistStore.AcquireReaderLockBMSTables();
                readerLockAcquired = true;
            }
            if (PlaylistTreeTables != null)
            {
                tableSnapshot.AddRange(PlaylistTreeTables);
            }
        }
        finally
        {
            if (playlistStore != null && readerLockAcquired)
            {
                playlistStore.FreeReaderLockBMSTables();
            }
        }
        return tableSnapshot;
    }

    internal void RefreshPlaylistTreeTables(BMSPlaylist playlistStore)
    {
        RefreshPlaylistTreeTables(playlistStore, getPlaylistLibrary());
    }

    internal void RefreshPlaylistTreeTables(BMSPlaylist playlistStore, BMSLibrary playlistLibrary)
    {
        AttachPlaylistTreeStore(playlistStore, playlistLibrary);
        ApplyPlaylistTreeTablesSource();
        PublishPlaylistCatalogChanged();
    }

    internal void RefreshPlaylistTreePresentation()
    {
        ApplyPlaylistTreeTablesSource(raiseWhenUnchanged: true);
    }

    private void PublishPlaylistCatalogChanged()
    {
        Interlocked.Increment(ref playlistCatalogVersion);
        if (Interlocked.CompareExchange(ref playlistCatalogNotificationQueued, 1, 0) == 0)
        {
            try
            {
                QueuePlaylistCatalogNotification();
            }
            catch
            {
                Interlocked.Exchange(ref playlistCatalogNotificationQueued, 0);
                throw;
            }
        }
    }

    private void DrainPlaylistCatalogChanged()
    {
        long publishedVersion = Interlocked.Read(ref playlistCatalogVersion);
        Exception publishFailure = null;
        try
        {
            PlaylistCatalogChanged?.Invoke(
                this,
                new PlaylistCatalogChangedEventArgs(publishedVersion));
        }
        catch (Exception ex)
        {
            publishFailure = ex;
        }
        finally
        {
            Interlocked.Exchange(ref playlistCatalogNotificationQueued, 0);
            if (publishedVersion != Interlocked.Read(ref playlistCatalogVersion)
                && Interlocked.CompareExchange(ref playlistCatalogNotificationQueued, 1, 0) == 0)
            {
                try
                {
                    QueuePlaylistCatalogNotification();
                }
                catch
                {
                    Interlocked.Exchange(ref playlistCatalogNotificationQueued, 0);
                    throw;
                }
            }
        }

        if (publishFailure != null)
        {
            ExceptionDispatchInfo.Capture(publishFailure).Throw();
        }
    }

    private void QueuePlaylistCatalogNotification()
    {
        Action<Action> queueAction = Volatile.Read(ref queueCatalogNotification)
            ?? throw new InvalidOperationException("Playlist catalog notification queue is not configured.");
        queueAction(DrainPlaylistCatalogChanged);
    }

    private void AttachPlaylistTreeStore(BMSPlaylist nextStore, BMSLibrary nextLibrary)
    {
        SetDetailEntryCommitStore(nextStore);
        SetPlaylistExternalSyncReceiptSubscription(nextStore, nextLibrary);
        bool changed = MutatePlaylistTreeAfterInvalidatingHydrationReceipt(
            () => !ReferenceEquals(playlistTreeStore, nextStore)
                || !ReferenceEquals(observedPlaylistTreeTables, nextStore?.BMSTables),
            () =>
            {
                if (!ReferenceEquals(playlistTreeStore, nextStore))
                {
                    if (playlistTreeStore != null)
                    {
                        playlistTreeStore.PlaylistTablesReplaced -= PlaylistTreeStoreTablesReplaced;
                        playlistTreeStore.PlaylistEntriesHydrationRequested -= PlaylistTreeStoreHydrationRequested;
                        playlistTreeStore.PlaylistEntriesHydrationCompleted -= PlaylistTreeStoreHydrationCompleted;
                        playlistTreeStore.PlaylistEntriesHydrationReceiptPublished -= PlaylistTreeStoreHydrationReceiptPublished;
                    }
                    if (observedPlaylistTreeTables != null)
                    {
                        observedPlaylistTreeTables.CollectionChanged -= PlaylistTreeTablesCollectionChanged;
                    }

                    playlistTreeStore = nextStore;
                    Interlocked.Increment(ref playlistTreeNotificationGeneration);
                    observedPlaylistTreeTables = null;
                    if (playlistTreeStore != null)
                    {
                        playlistTreeStore.PlaylistTablesReplaced += PlaylistTreeStoreTablesReplaced;
                        playlistTreeStore.PlaylistEntriesHydrationRequested += PlaylistTreeStoreHydrationRequested;
                        playlistTreeStore.PlaylistEntriesHydrationCompleted += PlaylistTreeStoreHydrationCompleted;
                        playlistTreeStore.PlaylistEntriesHydrationReceiptPublished += PlaylistTreeStoreHydrationReceiptPublished;
                    }
                }
                AttachObservedPlaylistTreeTablesUnsafe(nextStore?.BMSTables);
                PlaylistReferenceApplyWorkflow.AttachContext(
                    nextStore,
                    nextLibrary,
                    Volatile.Read(ref playlistTreeNotificationGeneration));
            });
        if (!changed)
        {
            lock (playlistTreeStoreSyncRoot)
            {
                PlaylistReferenceApplyWorkflow.AttachContext(
                    nextStore,
                    nextLibrary,
                    Volatile.Read(ref playlistTreeNotificationGeneration));
            }
        }
    }

    private void AttachObservedPlaylistTreeTablesUnsafe(
        ObservableCollection<BMSTable> nextTables)
    {
        if (ReferenceEquals(observedPlaylistTreeTables, nextTables))
        {
            return;
        }

        if (observedPlaylistTreeTables != null)
        {
            observedPlaylistTreeTables.CollectionChanged -= PlaylistTreeTablesCollectionChanged;
        }
        observedPlaylistTreeTables = nextTables;
        long generation = Interlocked.Increment(ref playlistTreeNotificationGeneration);
        PlaylistReferenceApplyWorkflow.AttachContext(
            playlistTreeStore,
            subscribedPlaylistExternalSyncLibrary,
            generation);
        if (observedPlaylistTreeTables != null)
        {
            observedPlaylistTreeTables.CollectionChanged += PlaylistTreeTablesCollectionChanged;
        }
    }

    private PlaylistHydrationCompletionReceipt DetachPlaylistHydrationCompletionReceiptUnsafe()
    {
        PlaylistHydrationCompletionReceipt receipt = playlistHydrationCompletionReceipt;
        playlistHydrationCompletionReceipt = null;
        pendingPlaylistHydrationCompletion = null;
        return receipt;
    }

    private void ApplyPlaylistTreeTablesSource(bool raiseWhenUnchanged = false)
    {
        bool presentationChanged = false;
        MutatePlaylistTreeAfterInvalidatingHydrationReceipt(
            () =>
            {
                ObservableCollection<BMSTable> currentTables = playlistTreeStore == null
                    ? emptyPlaylistTreeTables
                        ?? throw new InvalidOperationException("Playlist tree source is not configured.")
                    : playlistTreeStore.BMSTables;
                return !ReferenceEquals(observedPlaylistTreeTables, currentTables)
                    || !ReferenceEquals(playlistTreeTables, currentTables);
            },
            () =>
            {
                ObservableCollection<BMSTable> nextTables = playlistTreeStore == null
                ? emptyPlaylistTreeTables
                    ?? throw new InvalidOperationException("Playlist tree source is not configured.")
                : playlistTreeStore.BMSTables;
                AttachObservedPlaylistTreeTablesUnsafe(nextTables);
                presentationChanged = !ReferenceEquals(playlistTreeTables, nextTables);
                playlistTreeTables = nextTables;
            });
        if (presentationChanged || raiseWhenUnchanged)
        {
            RaisePropertyChanged(nameof(PlaylistTreeTables));
        }
    }

    private bool MutatePlaylistTreeAfterInvalidatingHydrationReceipt(
        Func<bool> mutationRequired,
        Action mutation)
    {
        while (true)
        {
            PlaylistHydrationCompletionReceipt receipt;
            lock (playlistTreeStoreSyncRoot)
            {
                if (!mutationRequired())
                {
                    return false;
                }
                receipt = playlistHydrationCompletionReceipt;
            }

            receipt?.Invalidate();

            lock (playlistTreeStoreSyncRoot)
            {
                if (!ReferenceEquals(receipt, playlistHydrationCompletionReceipt))
                {
                    continue;
                }
                if (!mutationRequired())
                {
                    return false;
                }
                DetachPlaylistHydrationCompletionReceiptUnsafe();
                mutation();
                return true;
            }
        }
    }

    public bool IsPlaylistTreeExpanded
    {
        get => isPlaylistTreeExpanded;
        set
        {
            if (isPlaylistTreeExpanded == value)
            {
                return;
            }
            isPlaylistTreeExpanded = value;
            RaisePropertyChanged(nameof(IsPlaylistTreeExpanded));
        }
    }

    internal void RequestSummarySelection()
    {
        long selectionRevision = ClearPlaylistDetailSelection();
        var accepted = PlaylistTreeSelectionActivatedEventArgs.Summary(selectionRevision, summaryModeChanged: false);
        TreeSelectionAccepted?.Invoke(this, accepted);
        PlaylistSourceRetirementRequest detailSourceRetirement =
            PrepareDetailSourceRetirementForRegularView();
        RegisterPlaylistSummaryDetailSourceRetirement(detailSourceRetirement);
        dispatchPresentation(
            () =>
            {
                if (accepted.Navigation != null && !accepted.Navigation.IsCurrent) { return; }
                bool isCurrent;
                lock (playlistDetailSelectionSyncRoot)
                {
                    isCurrent = IsCurrentPlaylistSummarySelectionWithoutLock(selectionRevision);
                }
                if (!isCurrent)
                {
                    return;
                }
                bool summaryModeChanged = RequestPlaylistSummaryMode(enabled: true);
                lock (playlistDetailSelectionSyncRoot)
                {
                    if (!IsCurrentPlaylistSummarySelectionWithoutLock(selectionRevision))
                    {
                        return;
                    }
                }
                TreeSelectionActivated?.Invoke(
                    this,
                    PlaylistTreeSelectionActivatedEventArgs.Summary(
                        selectionRevision,
                        summaryModeChanged, accepted.Navigation));
                lock (playlistDetailSelectionSyncRoot)
                {
                    if (!IsCurrentPlaylistSummarySelectionWithoutLock(selectionRevision))
                    {
                        return;
                    }
                }
                RequestPlaylistSummaryPresentationRefresh();
            });
    }

    internal bool RegisterPlaylistSummaryDetailSourceRetirement(
        PlaylistSourceRetirementRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        lock (playlistSummaryTransitionLock)
        {
            if (pendingPlaylistSummaryDetailSourceRetirement != null
                && pendingPlaylistSummaryDetailSourceRetirement.RequestVersion
                    >= request.RequestVersion)
            {
                return false;
            }
            pendingPlaylistSummaryDetailSourceRetirement = request;
            return true;
        }
    }

    internal long ClearPlaylistDetailSelection()
    {
        lock (playlistDetailSelectionSyncRoot)
        {
            playlistDetailSelection = null;
            return ++playlistDetailSelectionRevision;
        }
    }

    /// <summary>表示された構成を出自として明示選択します。構成なしの要求を現在構成で補完しません。</summary>
    internal void RequestDetailSelection(BMSTable table, PlaylistFolderNode folderNode = null,
        IReadOnlyList<PlaylistFolderNode> folderNodesAtSelection = null)
    {
        PlaylistDetailSelectionScope selectionScope = folderNode == null
            ? PlaylistDetailSelectionScope.OrdinaryRoot
            : PlaylistDetailSelectionScope.Folder;
        string folderName = folderNode == null || folderNode.IsSpecial
            ? null
            : folderNode.FolderName;
        PlaylistDetailFilter filter = folderNode?.SpecialKind == PlaylistFolderNodeSpecialKind.NotOwned
            ? PlaylistDetailFilter.PlaylistNotOwnedFilterSelected
            : PlaylistDetailFilter.PlaylistFilter;
        PlaylistDetailSelection selection = new(table, selectionScope, folderName, filter, folderNodesAtSelection);
        long selectionRevision;
        lock (playlistDetailSelectionSyncRoot)
        {
            playlistDetailSelection = selection;
            selectionRevision = ++playlistDetailSelectionRevision;
        }
        var accepted = PlaylistTreeSelectionActivatedEventArgs.CreateDetail(selection, selectionRevision, summaryModeChanged: false);
        if (table != null) { TreeSelectionAccepted?.Invoke(this, accepted); }
        dispatchPresentation(
            () =>
            {
                if (accepted.Navigation != null && !accepted.Navigation.IsCurrent) { return; }
                PlaylistDetailSelection currentSelection;
                long currentRevision;
                lock (playlistDetailSelectionSyncRoot)
                {
                    // 同じ選択参照で表やフォルダが置換された場合は、現在の対象を解決する。旧選択の版や受付は再適用しない。
                    if (accepted.Navigation == null && !IsCurrentPlaylistDetailSelectionWithoutLock(selection, selectionRevision)) { return; }
                    currentSelection = playlistDetailSelection?.Clone();
                    currentRevision = playlistDetailSelectionRevision;
                }
                if (currentSelection == null) { return; }
                bool summaryModeChanged = RequestPlaylistSummaryMode(enabled: false);
                lock (playlistDetailSelectionSyncRoot)
                {
                    if (!IsCurrentPlaylistDetailSelectionWithoutLock(currentSelection, currentRevision)) { return; }
                }
                TreeSelectionActivated?.Invoke(this,
                    PlaylistTreeSelectionActivatedEventArgs.CreateDetail(currentSelection, currentRevision,
                        summaryModeChanged, accepted.Navigation));
            });
    }

    internal PlaylistDetailSelection CapturePlaylistDetailSelection()
    {
        return CapturePlaylistDetailSelection(out _);
    }

    internal PlaylistDetailSelection CapturePlaylistDetailSelection(out long selectionRevision)
    {
        lock (playlistDetailSelectionSyncRoot)
        {
            selectionRevision = playlistDetailSelectionRevision;
            return playlistDetailSelection?.Clone();
        }
    }

    internal bool IsCurrentPlaylistDetailSelection(PlaylistDetailSelection selection, long selectionRevision)
    {
        lock (playlistDetailSelectionSyncRoot)
        {
            return IsCurrentPlaylistDetailSelectionWithoutLock(selection, selectionRevision);
        }
    }

    internal bool IsCurrentPlaylistSummarySelection(long selectionRevision)
    {
        lock (playlistDetailSelectionSyncRoot)
        {
            return IsCurrentPlaylistSummarySelectionWithoutLock(selectionRevision);
        }
    }

    private bool IsCurrentPlaylistSummarySelectionWithoutLock(long selectionRevision)
    {
        return selectionRevision == playlistDetailSelectionRevision
            && playlistDetailSelection == null;
    }

    internal bool ReplaceCurrentPlaylistDetailSelectionTable(BMSTable oldTable, BMSTable newTable)
    {
        if (oldTable == null || newTable == null || ReferenceEquals(oldTable, newTable))
        {
            return false;
        }
        lock (playlistDetailSelectionSyncRoot)
        {
            if (playlistDetailSelection?.Table != oldTable)
            {
                return false;
            }
            playlistDetailSelection = playlistDetailSelection.WithTable(newTable);
            playlistDetailSelectionRevision++;
            return true;
        }
    }

    /// <summary>確定したフォルダ名の対応を選択へ反映したことを通知し、ツリーの表示同期を促します。</summary>
    internal event EventHandler DetailFolderSelectionRemapped;

    /// <summary>選択中の通常フォルダを確定した対応先へ移し、移動先ノードがなければ表本体を選択します。</summary>
    internal bool RemapCurrentPlaylistDetailFolderSelection(
        BMSTable table,
        IReadOnlyDictionary<string, string> rewrittenFolders)
    {
        if (table == null || rewrittenFolders == null || rewrittenFolders.Count == 0)
        {
            return false;
        }
        lock (playlistDetailSelectionSyncRoot)
        {
            string currentFolderName = playlistDetailSelection?.FolderName;
            if (playlistDetailSelection?.Table != table
                || currentFolderName == null
                || playlistDetailSelection.HasCurrentFolderNodes
                || !rewrittenFolders.TryGetValue(currentFolderName, out string rewrittenFolder))
            {
                return false;
            }
            playlistDetailSelection = table.FolderNodes.Any(node => !node.IsSpecial && node.FolderName == rewrittenFolder)
                ? playlistDetailSelection.WithFolderName(rewrittenFolder)
                : new PlaylistDetailSelection(table, PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter);
            playlistDetailSelectionRevision++;
        }
        dispatchPresentation(() => DetailFolderSelectionRemapped?.Invoke(this, EventArgs.Empty));
        return true;
    }

    private bool IsCurrentPlaylistDetailSelectionWithoutLock(
        PlaylistDetailSelection selection,
        long selectionRevision)
    {
        return selection != null
            && selectionRevision == playlistDetailSelectionRevision
            && playlistDetailSelection != null
            && playlistDetailSelection.Matches(selection);
    }
}

internal sealed class PlaylistDetailSelection
{
    // 構成更新後の同キー明示選択と旧構成の選択を区別する、一つの不変集合参照です。
    // Cloneと表参照置換では保ち、改名対応を採用した後は新しい構成へ更新します。
    private readonly IReadOnlyList<PlaylistFolderNode> folderNodesAtSelection;

    /// <summary>選択キーとその構成を捕捉します。構成履歴を蓄積せず、選択の寿命だけ旧構成を参照します。</summary>
    internal PlaylistDetailSelection(
        BMSTable table,
        PlaylistDetailSelectionScope scope,
        string folderName,
        PlaylistDetailFilter filter,
        IReadOnlyList<PlaylistFolderNode> folderNodesAtSelection = null)
    {
        Table = table;
        Scope = scope;
        FolderName = folderName;
        Filter = filter;
        this.folderNodesAtSelection = folderNodesAtSelection;
    }

    internal BMSTable Table { get; }

    internal PlaylistDetailSelectionScope Scope { get; }

    internal string FolderName { get; }

    internal PlaylistDetailFilter Filter { get; }

    /// <summary>既に確定後の構成から明示選択された場合、旧構成の改名対応を再適用しません。</summary>
    internal bool HasCurrentFolderNodes => ReferenceEquals(folderNodesAtSelection, Table?.FolderNodes);

    /// <summary>捕捉した構成の出自を保持して要求を複製します。</summary>
    internal PlaylistDetailSelection Clone()
    {
        return new PlaylistDetailSelection(Table, Scope, FolderName, Filter, folderNodesAtSelection);
    }

    /// <summary>構成の出自を再捕捉せず、参照先の表だけを差し替えます。</summary>
    internal PlaylistDetailSelection WithTable(BMSTable table)
    {
        return new PlaylistDetailSelection(table, Scope, FolderName, Filter, folderNodesAtSelection);
    }

    /// <summary>確定した対応を適用し、対応先の公開構成を採用します。</summary>
    internal PlaylistDetailSelection WithFolderName(string folderName)
    {
        return new PlaylistDetailSelection(Table, Scope, folderName, Filter, Table.FolderNodes);
    }

    internal bool Matches(PlaylistDetailSelection other)
    {
        return other != null
            && ReferenceEquals(Table, other.Table)
            && Scope == other.Scope
            && string.Equals(FolderName, other.FolderName, StringComparison.Ordinal)
            && Filter == other.Filter;
    }
}

internal sealed class PlaylistTreeSelectionActivatedEventArgs : EventArgs
{
    private PlaylistTreeSelectionActivatedEventArgs(
        bool isSummary,
        PlaylistDetailSelection detail,
        long selectionRevision,
        bool summaryModeChanged, RegularChartNavigationRequest navigation = null)
    {
        Navigation = navigation;
        IsSummary = isSummary;
        Detail = detail;
        SelectionRevision = selectionRevision;
        SummaryModeChanged = summaryModeChanged;
    }

    /// <summary>同期受付で接続した同じ要求です。UI配送まで保持し、古いサマリー解除・選択を抑止します。</summary>
    internal RegularChartNavigationRequest Navigation { get; set; }

    internal bool IsSummary { get; }

    internal PlaylistDetailSelection Detail { get; }

    internal long SelectionRevision { get; }

    internal bool SummaryModeChanged { get; }

    /// <summary>サマリーの選択版と、それに対応する受付参照をUI配送まで保持します。</summary>
    internal static PlaylistTreeSelectionActivatedEventArgs Summary(
        long selectionRevision,
        bool summaryModeChanged, RegularChartNavigationRequest navigation = null)
        => new(true, null, selectionRevision, summaryModeChanged, navigation);

    /// <summary>詳細一覧の固定選択と選択版、それに対応する受付参照をUI配送まで保持します。</summary>
    internal static PlaylistTreeSelectionActivatedEventArgs CreateDetail(
        PlaylistDetailSelection detail,
        long selectionRevision,
        bool summaryModeChanged, RegularChartNavigationRequest navigation = null)
    {
        return new PlaylistTreeSelectionActivatedEventArgs(
            isSummary: false,
            detail ?? throw new ArgumentNullException(nameof(detail)),
            selectionRevision,
            summaryModeChanged, navigation);
    }
}
