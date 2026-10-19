using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public partial class BMSPlaylist
{
    private readonly object entryCommitGate = new();
    private readonly Queue<PlaylistEntryCommitReceipt> entryCommits = new();

    /// <summary>確定済み差分の配送起動です。購読先はキューを一回取得し、同じ結果を利用先へ渡します。</summary>
    internal event EventHandler PlaylistEntryCommitsAvailable;

    /// <summary>登録済みの不変差分を確定順に取得します。配送時に現在値を再捕捉しません。</summary>
    internal ImmutableArray<PlaylistEntryCommitReceipt> DrainPlaylistEntryCommits()
    {
        lock (entryCommitGate)
        {
            ImmutableArray<PlaylistEntryCommitReceipt> result = [.. entryCommits];
            entryCommits.Clear();
            return result;
        }
    }

    private void RegisterEntryCommit(PlaylistEntryCommitReceipt receipt)
    {
        if (receipt.Changes.IsEmpty && receipt.FolderOrder.IsDefault) { return; }
        lock (entryCommitGate) { entryCommits.Enqueue(receipt); }
    }

    private void SignalEntryCommitsAvailable(ISet<BMSTable> publishedHeaderTables = null)
    {
        PlaylistEntryCommitReceipt[] pending;
        lock (entryCommitGate)
        {
            if (entryCommits.Count == 0) { return; }
            pending = [.. entryCommits];
        }
        foreach (IGrouping<BMSTable, PlaylistEntryCommitReceipt> table in pending.GroupBy(receipt => receipt.Table))
        {
            if (publishedHeaderTables?.Contains(table.Key) == true) { continue; }
            try
            {
                table.Key.PublishEntryCommitProperties(table.Any(receipt => !receipt.FolderOrder.IsDefault
                || receipt.Changes.Any(change => change.BeforeOrder.HasValue != change.AfterOrder.HasValue
                    || change.Before?.Folder != change.After?.Folder)));
            }
            catch (Exception failure) { LogEntryCommitDeliveryFailure(failure); }
        }
        foreach (Delegate handler in PlaylistEntryCommitsAvailable?.GetInvocationList() ?? [])
        {
            try { ((EventHandler)handler)(this, EventArgs.Empty); }
            catch (Exception failure)
            {
                // 任意の表示起動が失敗しても、確定した結果と後続購読先を失わせません。
                LogEntryCommitDeliveryFailure(failure);
            }
        }
    }

    private static void LogEntryCommitDeliveryFailure(Exception failure)
    {
        try { NLogWrapper.FileLogger?.Warn(failure, "playlist_entry_commit_delivery_failed"); }
        catch (Exception) { }
    }

    /// <summary>生存表の編集headerとprefixを既存aggregateで一括確定し、guard解放後に同じ差分を配送します。</summary>
    /// <param name="edits">表ごとの編集対象だけを持つ不変要求。entryの保存値はwriter内currentから捕捉します。</param>
    /// <param name="capability">必要出力・公開・通知まで呼出元が保持する同じ生存P。</param>
    /// <returns>DB確定後の同entry・版・順序差分。headerだけの変更ではentry版を増やしません。</returns>
    /// <exception cref="PlaylistMutationPostCommitException">DB確定後のheader通知が失敗しました。値と配送済み差分は保持します。</exception>
    internal IReadOnlyList<PlaylistEntryCommitReceipt> CommitPlaylistHeaderEdits(
        IReadOnlyDictionary<BMSTable, PlaylistHeaderEdit> edits, LibraryFileMutationCapability capability)
    {
        using LibraryFileMutationLease accepted = AcquirePlaylistMutationLease("CommitPlaylistHeaderEdits", capability: capability);
        Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>> prepare = [];
        foreach ((BMSTable table, PlaylistHeaderEdit edit) in edits)
        {
            if ((edit.Fields & PlaylistHeaderFields.CompatPrefix) != 0) { EnsurePlaylistEntriesLoaded(table, "CommitPlaylistHeaderEdits"); }
            prepare.Add(table, observer => table.PrepareHeaderMutation(edit, observer));
        }
        IReadOnlyList<PlaylistEntryCommitReceipt> receipts = playlistAggregatePersistenceOwner.CommitEntryMutations(prepare, RegisterEntryCommit);
        List<Exception> failures = [];
        try
        {
            try { libraryBindings?.ApplyReferenceCommits(receipts, CaptureReferenceHeaders(receipts.Select(receipt => receipt.Table))); }
            catch (Exception failure) { failures.Add(failure); }
            foreach (PlaylistEntryCommitReceipt receipt in receipts)
            {
                try { receipt.Table.PublishHeaderCommitProperties(edits[receipt.Table], receipt); }
                catch (Exception failure) { failures.Add(failure); }
            }
        }
        finally { SignalEntryCommitsAvailable(receipts.Select(receipt => receipt.Table).ToHashSet()); }
        if (failures.Count > 0)
        {
            throw new PlaylistMutationPostCommitException("CommitPlaylistHeaderEdits",
                failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }
        return receipts;
    }

    /// <summary>一属性 draft を DB へ保存し、同じ entry・header・版・差分の確定後に必要 LR2 出力を完了します。</summary>
    /// <param name="entry">表示が識別として保持する、生存する元表の同じ entry 参照。</param>
    /// <param name="propertyName">Level、Url、Url_diff、comment、memo のいずれか。</param>
    /// <param name="text">未保存の入力値。live entry へ先行適用しません。</param>
    /// <param name="capability">BMT・操作通知の実終端まで呼出元が保持する生存 P。</param>
    /// <param name="chart">ローカル表の hash 補完に用いる捕捉済み所持譜面。</param>
    /// <returns>確定した不変差分。同値では版と差分を増やしません。</returns>
    /// <exception cref="PlaylistMutationPostCommitException">DB 確定後の出力が失敗しました。確定値と配送登録は保持されます。</exception>
    internal PlaylistEntryCommitReceipt CommitBMSTableEntry(BMSTableEntry entry, string propertyName,
        string text, LibraryFileMutationCapability capability, ChartFile chart = null)
    {
        using LibraryFileMutationLease accepted = AcquirePlaylistMutationLease("CommitBMSTableEntry", capability: capability);
        using LibraryFileMutationCapability authority = accepted.CreateMutationCapability();
        PlaylistEntryCommitReceipt receipt = playlistAggregatePersistenceOwner.CommitEntry(entry,
            propertyName, text, chart, RegisterEntryCommit, () => rwlockBMSTables.GetReaderGuard());
        try
        {
            CompleteReferencesAndOutputs([receipt], () => { if (!receipt.Changes.IsEmpty) { CompleteEntryOutputs(receipt.Table, authority); } });
            return receipt;
        }
        finally { SignalEntryCommitsAvailable(); }
    }

    private bool ApplyLocalEntryMutation(BMSTable table, string operation,
        IReadOnlyCollection<BMSTableEntry> removals, IReadOnlyList<(BMSTableEntry Entry, string Folder)> additions,
        bool persist, LibraryFileMutationCapability capability)
        => ApplyPreparedLocalEntryMutation(table, operation,
            observer => table.PrepareEntryMutation(removals, additions, observer), persist, capability);

    private bool ApplyPreparedLocalEntryMutation(BMSTable table, string operation,
        Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation> prepare,
        bool persist, LibraryFileMutationCapability capability)
    {
        ArgumentNullException.ThrowIfNull(table);
        using LibraryFileMutationLease accepted = AcquirePlaylistMutationLease(operation, capability: capability);
        using LibraryFileMutationCapability authority = accepted.CreateMutationCapability();
        EnsurePlaylistEntriesLoaded(table, operation);
        using (rwlockBMSTables.GetReaderGuard())
        {
            if (!BMSTables.Contains(table)) { return false; }
        }
        IReadOnlyList<PlaylistEntryCommitReceipt> receipts = playlistAggregatePersistenceOwner.CommitEntryMutations(
            new Dictionary<BMSTable, Func<IPlaylistEntryMutationWorkObserver, BMSTable.EntryMutation>> { [table] = prepare },
            RegisterEntryCommit, persist);
        try
        {
            CompleteReferencesAndOutputs(receipts, () => { if (persist && receipts.Count > 0) { CompleteEntryOutputs(table, authority); } });
            return true;
        }
        finally { SignalEntryCommitsAvailable(); }
    }
}
