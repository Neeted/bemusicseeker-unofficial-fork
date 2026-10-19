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
        if (receipt.Changes.IsEmpty && !receipt.IsFullReplacement && receipt.FolderOrder.IsDefault) { return; }
        lock (entryCommitGate) { entryCommits.Enqueue(receipt); }
    }

    private void SignalEntryCommitsAvailable()
    {
        PlaylistEntryCommitReceipt[] pending;
        lock (entryCommitGate)
        {
            if (entryCommits.Count == 0) { return; }
            pending = [.. entryCommits];
        }
        foreach (IGrouping<BMSTable, PlaylistEntryCommitReceipt> table in pending.GroupBy(receipt => receipt.Table))
        {
            try
            {
                table.Key.PublishEntryCommitProperties(table.Any(receipt => receipt.IsFullReplacement || !receipt.FolderOrder.IsDefault
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
            if (!receipt.Changes.IsEmpty) { CompleteEntryOutputs(receipt.Table, authority); }
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
            if (persist && receipts.Count > 0) { CompleteEntryOutputs(table, authority); }
            return true;
        }
        finally { SignalEntryCommitsAvailable(); }
    }
}
