using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public partial class BMSPlaylist
{
    /// <summary>参照表示のための有限なheaderだけを同じ正本順で捕捉します。entryは列挙しません。</summary>
    private IReadOnlyList<PlaylistReferenceTableHeader> CaptureReferenceHeaders(IEnumerable<BMSTable> targets)
    {
        using (rwlockBMSTables.GetReaderGuard()) { return CaptureReferenceHeadersUnderCollectionGuard(targets); }
    }
    /// <summary>既に保持する集合reader内で同DB確定のheaderだけを捕捉します。</summary>
    private IReadOnlyList<PlaylistReferenceTableHeader> CaptureReferenceHeadersUnderCollectionGuard(IEnumerable<BMSTable> targets)
    {
        var result = new List<PlaylistReferenceTableHeader>();
        foreach (BMSTable table in targets)
        {
            int position = BMSTables.IndexOf(table);
            if (position < 0) { continue; }
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                result.Add(new(table, table.symbol ?? string.Empty, table.name ?? string.Empty, table.bmt_sort, position));
            }
        }
        return result;
    }

    /// <summary>集合guardを保持して保存した呼出元が、全guard解放後・必要出力前に同じheader捕捉を適用します。</summary>
    internal void ApplyCommittedPlaylistReferenceHeaders(IReadOnlyList<PlaylistReferenceTableHeader> headers)
    {
        try { libraryBindings?.ApplyReferenceCommits([], headers); }
        catch (Exception failure) { throw new PlaylistMutationPostCommitException("playlist_reference_header_apply", failure); }
    }

    /// <summary>同entry/header確定groupを全guard解放後に参照索引へ適用し、失敗時にも必要出力を試みて最初の原因を返します。</summary>
    private void CompleteReferencesAndOutputs(IReadOnlyList<PlaylistEntryCommitReceipt> receipts, Action outputs,
        IEnumerable<BMSTable> headerTables = null)
        => CompleteRequiredReferenceAndOutputs(
            () => libraryBindings?.ApplyReferenceCommits(receipts, headerTables == null ? null : CaptureReferenceHeaders(headerTables)), outputs);

    /// <summary>DB後の必要参照に失敗しても出力・通知終端を試み、先に発生した原因を保持します。</summary>
    private static void CompleteRequiredReferenceAndOutputs(Action references, Action outputs)
    {
        ExceptionDispatchInfo firstFailure = null;
        try { references(); }
        catch (Exception failure) { firstFailure = ExceptionDispatchInfo.Capture(new PlaylistMutationPostCommitException("playlist_reference_apply", failure)); }
        try { outputs(); }
        catch (Exception failure) { firstFailure ??= ExceptionDispatchInfo.Capture(failure); }
        firstFailure?.Throw();
    }

    /// <summary>登録・実表置換の有限なentry入力と、同じ正本順のheaderをguard解放後に一回確定します。</summary>
    private void PublishReferenceTables(IEnumerable<BMSTable> changedTables, IEnumerable<BMSTable> removedTables = null)
    {
        if (libraryBindings == null) { return; }
        var snapshots = new List<PlaylistReferenceTableSnapshot>();
        IReadOnlyList<PlaylistReferenceTableHeader> headers;
        using (rwlockBMSTables.GetReaderGuard())
        {
            foreach (BMSTable table in changedTables.Distinct())
            {
                int position = BMSTables.IndexOf(table);
                if (position < 0) { continue; }
                using (table.ReaderWriterLock.GetReaderGuard())
                {
                    snapshots.Add(new(table, table.symbol, table.name, table.ArePlaylistEntriesLoaded ? table.entries : [], position, libraryBindings.SourceLibrary.PlaylistReferenceWorkObserver));
                }
            }
            var capturedHeaders = new List<PlaylistReferenceTableHeader>();
            foreach (BMSTable table in BMSTables)
            {
                using (table.ReaderWriterLock.GetReaderGuard())
                { capturedHeaders.Add(new(table, table.symbol ?? string.Empty, table.name ?? string.Empty, table.bmt_sort, capturedHeaders.Count)); }
            }
            headers = capturedHeaders;
        }
        libraryBindings.PublishReferenceTables(snapshots, headers, removedTables);
    }

    /// <summary>外部更新の同じ確定receiptを必要出力前に受け、任意UIeventの有無に依存せず参照へ公開します。</summary>
    private void PublishReferenceTableUpdates(IReadOnlyList<PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt> receipts)
    {
        PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt[] changed = [.. receipts.Where(receipt => receipt.Updated || receipt.ReferenceEntriesChanged
            || !ReferenceEquals(receipt.OldTable, receipt.NewTable))];
        if (changed.Length == 0) { return; }
        PublishReferenceTables(changed.Select(receipt => receipt.NewTable).Where(table => table != null),
            changed.Where(receipt => receipt.OldTable != null && !ReferenceEquals(receipt.OldTable, receipt.NewTable)).Select(receipt => receipt.OldTable));
    }
}
