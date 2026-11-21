using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    private readonly object splitPlacementGate = new();

    private List<DuplicateGroup> splitPlacementGroups;

    private long splitPlacementGeneration;

    /// <summary>現在の結果を探索したときの探索条件です。結果と一緒に格納し、取消・失敗では変えません。</summary>
    private SplitPlacementSearchCriteria splitPlacementCriteria;

    /// <summary>失効した結果を含む、直近に格納した結果です。次の探索がグループのインスタンスを引き継ぐ基底で、失効では消しません。</summary>
    private List<DuplicateGroup> splitPlacementBase;

    /// <summary>
    /// 分裂配置のグループの現在の結果です。結果がない状態は <c>null</c>、0件は空の一覧です。保存しません。
    /// </summary>
    /// <remarks>
    /// 重複検索の結果が失効する契機で失効し、探索に使ったリソース索引の世代が現在と異なる結果は有効として扱いません。
    /// </remarks>
    internal List<DuplicateGroup> SplitPlacementGroups
    {
        get
        {
            lock (splitPlacementGate)
            {
                return splitPlacementGroups;
            }
        }
    }

    /// <summary>
    /// 結果があり、その結果を探索したリソース索引の世代が現在の世代と等しく、探索条件が <paramref name="criteria"/> と値として等しいかを返します。
    /// 画面が探索の要否を決めるための読取り専用の判定で、受付もライブラリのロックも取りません。0件の結果も有効です。
    /// </summary>
    /// <param name="criteria">有効性を判定する探索条件。</param>
    internal bool HasCurrentSplitPlacementGroups(SplitPlacementSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        long currentGeneration = libraryResourceIndexOwner.CaptureSnapshot().Generation;
        lock (splitPlacementGate)
        {
            return splitPlacementGroups != null
                && splitPlacementGeneration == currentGeneration
                && criteria.Equals(splitPlacementCriteria);
        }
    }

    /// <summary>
    /// 分裂配置の結果を失効させ、直前まで結果があった場合だけ変更を非同期に一回通知します。
    /// 重複検索の結果の有無と通知の抑止には従いません。基底は保持します。
    /// </summary>
    private void InvalidateSplitPlacementGroups()
    {
        bool changed;
        lock (splitPlacementGate)
        {
            changed = splitPlacementGroups != null;
            splitPlacementGroups = null;
        }
        if (changed)
        {
            Task.Run(delegate
            {
                RaisePropertyChanged(nameof(SplitPlacementGroups));
            }).ObserveFault("SplitPlacementGroups");
        }
    }

    /// <summary>
    /// 同じ楽曲が複数のフォルダへ分かれた配置（分裂配置）のグループを探索し、<see cref="SplitPlacementGroups"/> へ格納します。
    /// </summary>
    /// <remarks>
    /// 呼出し側が共通のライブラリ変更受付を保持している間に呼びます。受付はこのメソッドでは取得・確認しません。
    /// 有効な結果（現在のリソース索引の世代で、<paramref name="criteria"/> と等しい探索条件で探索した結果）があれば、探索も通知もせずに戻ります。
    /// 世代が同じでも探索条件が異なれば探索し直します。
    /// 所持集合の行は既存の重複検索と同じロックの下で、リソース索引は一回だけ捕捉し、その一回の値を有効性の判定・探索の入力・結果の世代に使います。
    /// 捕捉の後はロックを保持せずに計算し、カタログを変更しません。新しい結果を格納したら、ロックの外・呼出しスレッドで変更を一回通知します。
    /// 取消・失敗では保持している状態（結果・世代・探索条件・基底）を変えず、通知もしません。
    /// グループのインスタンスは、直近に格納した結果（基底）から、失効した後の探索でも探索条件だけを変えた探索でも引き継ぎます。
    /// </remarks>
    /// <param name="criteria">探索条件。</param>
    /// <param name="progress">進捗の受け手。探索を実行するスレッドから同期に呼ばれます。</param>
    /// <param name="cancellationToken">取消の合図。<see cref="OperationCanceledException"/> を送出します。</param>
    internal void SearchSplitPlacementGroups(
        SplitPlacementSearchCriteria criteria, IProgress<SplitPlacementSearchProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var totalStopwatch = Stopwatch.StartNew();
        LibraryResourceIndexSnapshot resourceSnapshot;
        OwnedDuplicateChartRowSnapshot rowSnapshot;
        List<DuplicateGroup> baseGroups;
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                using (rwlockDuplicateChartGroups.GetWriterGuard())
                {
                    resourceSnapshot = libraryResourceIndexOwner.CaptureSnapshot();
                    lock (splitPlacementGate)
                    {
                        if (splitPlacementGroups != null && splitPlacementGeneration == resourceSnapshot.Generation
                            && criteria.Equals(splitPlacementCriteria))
                        {
                            LogInstallPerformance("SearchSplitPlacementGroups: cacheHit=true totalMs=" + totalStopwatch.ElapsedMilliseconds
                                + " Groups=" + splitPlacementGroups.Count);
                            return;
                        }
                        baseGroups = splitPlacementBase;
                    }
                    rowSnapshot = CreateOwnedDuplicateChartRowSnapshotUnsafe();
                }
            }
        }
        long captureMs = totalStopwatch.ElapsedMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        // 以降はロックを保持しない。入力は不変の捕捉だけを読む。
        long matchStartMs = -1;
        var observed = new SplitPlacementProgressObserver(progress, () =>
        {
            if (matchStartMs < 0)
            {
                matchStartMs = totalStopwatch.ElapsedMilliseconds;
            }
        });
        progress?.Report(new SplitPlacementSearchProgress(SplitPlacementSearchStage.Prepare, 0, 0));

        List<string> folderNames = [];
        List<List<ChartFile>> folderCharts = [];
        List<List<string>> folderHashes = [];
        var folderIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateChartRow row in rowSnapshot.Rows)
        {
            string directory = row.DirectoryPath;
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }
            ChartFile chart = row.CreateChart();
            if (chart == null)
            {
                continue;
            }
            if (!folderIds.TryGetValue(directory, out int id))
            {
                id = folderNames.Count;
                folderIds[directory] = id;
                folderNames.Add(directory);
                folderCharts.Add([]);
                folderHashes.Add([]);
            }
            folderCharts[id].Add(chart);
            if (!string.IsNullOrWhiteSpace(row.LookupHash))
            {
                folderHashes[id].Add(row.LookupHash);
            }
        }

        DirectoryResourceLookupCache cache = resourceSnapshot.DirectoryLookupCache;
        var inputs = new SplitPlacementFolderInput[folderNames.Count];
        for (int i = 0; i < inputs.Length; i++)
        {
            // 索引に項目のないフォルダは空の集合として扱う。
            inputs[i] = new SplitPlacementFolderInput(
                cache.GetEntryOrNull(folderNames[i])?.SelfOwnedAudioRelativePathHashMemory ?? default,
                [.. folderHashes[i]]);
        }

        SplitPlacementSearchResult result = SplitPlacementSearch.Search(
            inputs, SplitPlacementSearch.PreviewKeys, criteria, observed, cancellationToken);
        long groupStartMs = totalStopwatch.ElapsedMilliseconds;
        if (matchStartMs < 0)
        {
            matchStartMs = groupStartMs;
        }

        // フォルダはパスの昇順（大文字小文字を無視する序数比較）、譜面はフォルダの順・捕捉した行の順に並べる。
        List<DuplicateGroup> groups = [];
        foreach (int[] members in result.Groups)
        {
            int[] orderedMembers = [.. members.OrderBy(member => folderNames[member], StringComparer.OrdinalIgnoreCase)];
            groups.Add(new DuplicateGroup(
                [.. orderedMembers.SelectMany(member => folderCharts[member])],
                [.. orderedMembers.Select(member => folderNames[member])]));
        }
        int inherited = InheritSplitPlacementGroups(groups, baseGroups);
        groups.Sort(CompareSplitPlacementGroups);

        lock (splitPlacementGate)
        {
            splitPlacementGroups = groups;
            splitPlacementGeneration = resourceSnapshot.Generation;
            splitPlacementCriteria = criteria;
            splitPlacementBase = groups;
        }
        totalStopwatch.Stop();
        LogInstallPerformance("SearchSplitPlacementGroups: cacheHit=false"
            + " sharePercent=" + criteria.SharePercent
            + " genericFolderLimit=" + criteria.GenericFolderLimit
            + " shareBasis=" + criteria.Basis
            + " totalMs=" + totalStopwatch.ElapsedMilliseconds
            + " captureMs=" + captureMs
            + " prepareMs=" + (matchStartMs - captureMs)
            + " matchMs=" + (groupStartMs - matchStartMs)
            + " groupMs=" + (totalStopwatch.ElapsedMilliseconds - groupStartMs)
            + " targetFolders=" + result.TargetFolderCount
            + " candidateFolders=" + result.CandidateFolderCount
            + " registeredKeys=" + result.RegisteredKeyCount
            + " genericKeys=" + result.GenericKeyCount
            + " relations=" + result.RelationCount
            + " Groups=" + groups.Count
            + " inheritedGroups=" + inherited);

        TryInvokePostLeaseNotification(
            () => RaisePropertyChanged(nameof(SplitPlacementGroups)),
            "split_placement_groups_notification_failed");
    }

    /// <summary>見出しの序数順、同じ見出しは先頭フォルダのパスの昇順（大文字小文字を無視する序数比較）。</summary>
    private static int CompareSplitPlacementGroups(DuplicateGroup x, DuplicateGroup y)
    {
        int byHeader = string.CompareOrdinal(x.Header, y.Header);
        return byHeader != 0 ? byHeader : StringComparer.OrdinalIgnoreCase.Compare(x.Folders[0], y.Folders[0]);
    }

    /// <summary>
    /// 新しいグループのうち、基底のグループとフォルダの集合（大文字小文字を無視）、譜面の所持項目の識別・パス（完全一致）・順序が一致するものを、
    /// 基底のインスタンスへ置き換えます。引き継いだグループは捕捉時の見出しと譜面値を保ちます。引き継いだ数を返します。
    /// </summary>
    private static int InheritSplitPlacementGroups(List<DuplicateGroup> groups, List<DuplicateGroup> baseGroups)
    {
        if (baseGroups == null || baseGroups.Count == 0)
        {
            return 0;
        }
        // 基底のグループのフォルダは互いに素なので、先頭フォルダで引ける。
        var byFirstFolder = new Dictionary<string, DuplicateGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateGroup previous in baseGroups)
        {
            if (previous.Folders.Count > 0)
            {
                byFirstFolder[previous.Folders[0]] = previous;
            }
        }
        int inherited = 0;
        for (int i = 0; i < groups.Count; i++)
        {
            if (byFirstFolder.TryGetValue(groups[i].Folders[0], out DuplicateGroup previous) && HasSameMembership(groups[i], previous))
            {
                groups[i] = previous;
                inherited++;
            }
        }
        return inherited;
    }

    private static bool HasSameMembership(DuplicateGroup current, DuplicateGroup previous)
    {
        if (current.Folders.Count != previous.Folders.Count || current.ChartFiles.Count != previous.ChartFiles.Count)
        {
            return false;
        }
        for (int i = 0; i < current.Folders.Count; i++)
        {
            if (!string.Equals(current.Folders[i], previous.Folders[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        for (int i = 0; i < current.ChartFiles.Count; i++)
        {
            ChartFile now = current.ChartFiles[i];
            ChartFile before = previous.ChartFiles[i];
            if (now.Token == null || !ReferenceEquals(now.Token, before.Token)
                || !string.Equals(now.Path, before.Path, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>照合段階の開始を記録して、進捗を受け手へ転送します。</summary>
    private sealed class SplitPlacementProgressObserver(IProgress<SplitPlacementSearchProgress> inner, Action onMatchStarted)
        : IProgress<SplitPlacementSearchProgress>
    {
        public void Report(SplitPlacementSearchProgress value)
        {
            if (value.Stage == SplitPlacementSearchStage.Match)
            {
                onMatchStarted();
            }
            inner?.Report(value);
        }
    }
}
