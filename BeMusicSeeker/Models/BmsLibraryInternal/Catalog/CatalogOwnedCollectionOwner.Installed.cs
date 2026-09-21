using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// <see cref="CatalogOwnedCollectionOwner"/> が保持する installed chart lookup の状態です。
/// primary-only lookup と directory を含む full lookup を同じ owner で管理します。
/// </summary>
internal sealed partial class CatalogOwnedCollectionOwner
{
    private readonly object lockInstalledChartLookupIndex = new();

    private InstalledChartLookupIndexState installedChartLookupIndex = new();

    private bool installedChartLookupIndexInitialized;

    private long installedChartLookupGeneration;

    private Action<string> installedChartLookupStoreWorkObserver;

    private readonly object lockInstalledPrimaryHashLookup = new();

    private PrimaryHashLookupState installedPrimaryHashLookup = new();

    private bool installedPrimaryHashLookupInitialized;

    /// <summary>
    /// installed lookup root の実格納処理を記録する任意の内部 observer です。
    /// production では設定せず、設定時も owner へ再入しません。
    /// </summary>
    internal Action<string> InstalledChartLookupStoreWorkObserver
    {
        get => installedChartLookupStoreWorkObserver;
        set
        {
            installedChartLookupStoreWorkObserver = value;
            lock (lockInstalledChartLookupIndex)
            {
                installedChartLookupIndex.StoreWorkObserver = value;
            }
        }
    }

    /// <summary>
    /// installed lookup が指定世代のままかを確認します。
    /// </summary>
    /// <param name="generation">照合する世代。</param>
    /// <returns>指定世代と一致する場合は true。</returns>
    internal bool IsInstalledChartLookupGenerationCurrent(long generation)
    {
        lock (lockInstalledChartLookupIndex)
        {
            return installedChartLookupGeneration == generation;
        }
    }

    /// <summary>
    /// installed primary hash lookup の distinct hash 数を返します。
    /// </summary>
    /// <returns>構築済み primary hash の distinct 数。</returns>
    internal int GetInstalledPrimaryHashCount()
    {
        lock (lockInstalledPrimaryHashLookup)
        {
            return installedPrimaryHashLookup?.DistinctPrimaryHashCount ?? 0;
        }
    }

    /// <summary>
    /// installed full directory lookup が構築済みかを返します。
    /// </summary>
    /// <returns>構築済みなら true。</returns>
    internal bool IsInstalledChartLookupIndexInitialized()
    {
        lock (lockInstalledChartLookupIndex)
        {
            return installedChartLookupIndexInitialized;
        }
    }

    /// <summary>
    /// installed primary hash lookup が構築済みかを返します。
    /// </summary>
    /// <returns>構築済みなら true。</returns>
    internal bool IsInstalledPrimaryHashLookupInitialized()
    {
        lock (lockInstalledPrimaryHashLookup)
        {
            return installedPrimaryHashLookupInitialized;
        }
    }

    /// <summary>
    /// installed lookup の primary/full state を失効させ、次回要求で再構築します。
    /// 呼び出し側の currentness gate 内で実行します。
    /// </summary>
    internal void InvalidateInstalledChartLookup()
    {
        lock (lockInstalledPrimaryHashLookup)
        {
            installedPrimaryHashLookup = new PrimaryHashLookupState();
            installedPrimaryHashLookupInitialized = false;
        }
        lock (lockInstalledChartLookupIndex)
        {
            installedChartLookupIndex = new InstalledChartLookupIndexState
            {
                StoreWorkObserver = InstalledChartLookupStoreWorkObserver
            };
            installedChartLookupIndexInitialized = false;
            installedChartLookupGeneration++;
        }
    }

    /// <summary>
    /// durable mutation の installed lookup facts を primary/full state へ適用します。
    /// 呼び出し側の currentness gate 内で実行し、ログは state lock 解放後に発行します。
    /// </summary>
    /// <param name="mutation">適用する旧新 hash/path facts。</param>
    /// <param name="reason">mutation の理由。</param>
    /// <returns>state lock 解放後に発行するログ facts。</returns>
    internal IReadOnlyList<string> ApplyInstalledChartLookupMutation(
        InstalledChartLookupMutation mutation,
        string reason)
    {
        if (mutation == null || !mutation.HasChanges)
        {
            return Array.Empty<string>();
        }

        var logMessages = new List<string>();
        lock (lockInstalledPrimaryHashLookup)
        {
            if (installedPrimaryHashLookupInitialized)
            {
                if (mutation.RequiresFullInvalidate)
                {
                    installedPrimaryHashLookup = new PrimaryHashLookupState();
                    installedPrimaryHashLookupInitialized = false;
                    logMessages.Add("installed_primary_hash_lookup update mode=full_invalidate reason=" + reason);
                }
                else
                {
                    var stopwatch = Stopwatch.StartNew();
                    foreach (InstalledChartLookupMutationEntry entry in mutation.Removed)
                    {
                        installedPrimaryHashLookup.RemovePrimaryHash(entry.Md5);
                    }
                    foreach (InstalledChartLookupMutationEntry entry in mutation.Added)
                    {
                        installedPrimaryHashLookup.AddPrimaryHash(entry.Md5);
                    }
                    stopwatch.Stop();
                    logMessages.Add("installed_primary_hash_lookup update mode=incremental reason=" + reason
                        + " removed=" + mutation.Removed.Count
                        + " moved=" + mutation.Moved.Count
                        + " added=" + mutation.Added.Count
                        + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                        + " primaryHashes=" + installedPrimaryHashLookup.DistinctPrimaryHashCount);
                }
            }
        }

        lock (lockInstalledChartLookupIndex)
        {
            if (installedChartLookupIndexInitialized)
            {
                if (mutation.RequiresFullInvalidate)
                {
                    installedChartLookupIndex = new InstalledChartLookupIndexState
                    {
                        StoreWorkObserver = InstalledChartLookupStoreWorkObserver
                    };
                    installedChartLookupIndexInitialized = false;
                    logMessages.Add("installed_chart_lookup_index update mode=full_invalidate reason=" + reason);
                }
                else
                {
                    var stopwatch = Stopwatch.StartNew();
                    foreach (InstalledChartLookupMutationEntry entry in mutation.Removed)
                    {
                        installedChartLookupIndex.RemoveChart(entry.Path, entry.Md5, entry.Sha256);
                    }
                    foreach (InstalledChartLookupPathMutationEntry entry in mutation.Moved)
                    {
                        installedChartLookupIndex.MoveChart(entry.OldPath, entry.NewPath, entry.Md5, entry.Sha256);
                    }
                    foreach (InstalledChartLookupMutationEntry entry in mutation.Added)
                    {
                        installedChartLookupIndex.AddChart(entry.Path, entry.Md5, entry.Sha256);
                    }
                    stopwatch.Stop();
                    logMessages.Add("installed_chart_lookup_index update mode=incremental reason=" + reason
                        + " removed=" + mutation.Removed.Count
                        + " moved=" + mutation.Moved.Count
                        + " added=" + mutation.Added.Count
                        + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                        + " hashes=" + installedChartLookupIndex.HashCount
                        + " primaryHashes=" + installedChartLookupIndex.DistinctPrimaryHashCount
                        + " dirRefs=" + installedChartLookupIndex.DirectoryReferenceCount);
                }
            }
            installedChartLookupGeneration++;
        }

        return logMessages;
    }

    /// <summary>
    /// installed primary hash lookup を必要時だけ構築します。
    /// </summary>
    /// <param name="storageRowsOwner">owned collection の canonical source。</param>
    /// <param name="buildMs">今回 build した経過時間。</param>
    /// <param name="bmsCount">build source の BMS 件数。</param>
    /// <param name="bmsonCount">build source の BMSON 件数。</param>
    /// <param name="logOverride">build 完了後に呼び出すログ callback。</param>
    /// <returns>今回 build を行った場合は true。</returns>
    internal bool EnsureInstalledPrimaryHashLookupBuilt(
        CatalogStorageRowsOwner storageRowsOwner,
        out long buildMs,
        out int bmsCount,
        out int bmsonCount,
        Action<string> logOverride = null)
    {
        EnsureCurrent(storageRowsOwner);
        lock (lockInstalledPrimaryHashLookup)
        {
            if (installedPrimaryHashLookupInitialized)
            {
                buildMs = 0L;
                bmsCount = 0;
                bmsonCount = 0;
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            PrimaryHashLookupState state;
            lock (gate)
            {
                state = collection.CreatePrimaryHashLookupState(out bmsCount, out bmsonCount);
            }
            installedPrimaryHashLookup = state ?? new PrimaryHashLookupState();
            installedPrimaryHashLookupInitialized = true;
            stopwatch.Stop();
            buildMs = stopwatch.ElapsedMilliseconds;
            logOverride?.Invoke("installed_primary_hash_lookup build mode=full buildMs=" + buildMs
                + " primaryHashes=" + installedPrimaryHashLookup.DistinctPrimaryHashCount
                + " files=" + bmsCount
                + " bmson=" + bmsonCount
                + " rows=" + (bmsCount + bmsonCount)
                + " source=owned_collection_primary singleFlight=true");
            return true;
        }
    }

    /// <summary>
    /// installed full directory lookup を必要時だけ構築します。
    /// </summary>
    /// <param name="storageRowsOwner">owned collection の canonical source。</param>
    /// <param name="logOverride">build 完了後に呼び出すログ callback。</param>
    internal void EnsureInstalledChartLookupIndexBuilt(
        CatalogStorageRowsOwner storageRowsOwner,
        Action<string> logOverride = null)
    {
        EnsureCurrent(storageRowsOwner);
        lock (lockInstalledChartLookupIndex)
        {
            if (installedChartLookupIndexInitialized)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            InstalledChartLookupIndexState state;
            int bmsCount;
            int bmsonCount;
            lock (gate)
            {
                state = collection.CreateInstalledChartLookupIndexState(
                    out bmsCount,
                    out bmsonCount,
                    InstalledChartLookupStoreWorkObserver);
            }
            installedChartLookupIndex = state ?? new InstalledChartLookupIndexState();
            installedChartLookupIndex.StoreWorkObserver = InstalledChartLookupStoreWorkObserver;
            installedChartLookupIndexInitialized = true;
            stopwatch.Stop();
            logOverride?.Invoke("installed_chart_lookup_index build mode=full buildMs=" + stopwatch.ElapsedMilliseconds
                + " hashes=" + installedChartLookupIndex.HashCount
                + " primaryHashes=" + installedChartLookupIndex.DistinctPrimaryHashCount
                + " dirRefs=" + installedChartLookupIndex.DirectoryReferenceCount
                + " files=" + bmsCount
                + " bmson=" + bmsonCount
                + " rows=" + (bmsCount + bmsonCount)
                + " source=owned_collection_lightweight singleFlight=true");
        }
    }

    /// <summary>
    /// installed full lookup の snapshot と generation を同じ state lock で捕捉します。
    /// </summary>
    /// <param name="storageRowsOwner">owned collection の canonical source。</param>
    /// <param name="logOverride">build 完了後に呼び出すログ callback。</param>
    /// <returns>snapshot と対応する generation。</returns>
    internal (InstalledChartLookupIndexSnapshot Snapshot, long Generation)
        CreateInstalledChartLookupVersionedSnapshot(
            CatalogStorageRowsOwner storageRowsOwner,
            Action<string> logOverride = null)
    {
        EnsureInstalledChartLookupIndexBuilt(storageRowsOwner, logOverride);
        lock (lockInstalledChartLookupIndex)
        {
            return (installedChartLookupIndex.CreateSnapshot(), installedChartLookupGeneration);
        }
    }

    /// <summary>
    /// installed primary-only lookup が指定 chart を含むかを確認します。
    /// </summary>
    /// <param name="chart">照会する chart。</param>
    /// <param name="storageRowsOwner">owned collection の canonical source。</param>
    /// <param name="logOverride">必要な build のログ callback。</param>
    /// <returns>同じ primary hash の installed chart がある場合は true。</returns>
    internal bool ContainsInstalledChart(
        ChartFile chart,
        CatalogStorageRowsOwner storageRowsOwner,
        Action<string> logOverride = null)
    {
        string lookupKey = ChartLookupKey.GetPrimaryHash(chart);
        if (string.IsNullOrWhiteSpace(lookupKey))
        {
            return false;
        }

        EnsureInstalledPrimaryHashLookupBuilt(
            storageRowsOwner,
            out _,
            out _,
            out _,
            logOverride);
        lock (lockInstalledPrimaryHashLookup)
        {
            return installedPrimaryHashLookup.ContainsPrimaryHash(lookupKey);
        }
    }

    /// <summary>
    /// installed chart の既知 directory snapshot を返します。
    /// </summary>
    /// <param name="storageRowsOwner">owned collection の canonical source。</param>
    /// <param name="logOverride">必要な build のログ callback。</param>
    /// <returns>既知 directory の immutable view。</returns>
    internal IReadOnlyCollection<string> CreateInstalledChartKnownDirectorySnapshot(
        CatalogStorageRowsOwner storageRowsOwner,
        Action<string> logOverride = null)
    {
        EnsureInstalledChartLookupIndexBuilt(storageRowsOwner, logOverride);
        lock (lockInstalledChartLookupIndex)
        {
            return installedChartLookupIndex.CreateKnownChartDirectorySnapshot();
        }
    }

    /// <summary>
    /// primary hash に対応する installed directory を返します。
    /// </summary>
    /// <param name="lookupHash">照会する primary hash。</param>
    /// <param name="storageRowsOwner">owned collection の canonical source。</param>
    /// <param name="logOverride">必要な build のログ callback。</param>
    /// <returns>distinct directory の順序付き一覧。</returns>
    internal List<string> GetDistinctInstalledDirectoriesByPrimaryHash(
        string lookupHash,
        CatalogStorageRowsOwner storageRowsOwner,
        Action<string> logOverride = null)
    {
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }

        EnsureInstalledChartLookupIndexBuilt(storageRowsOwner, logOverride);
        lock (lockInstalledChartLookupIndex)
        {
            return [.. installedChartLookupIndex.GetDistinctDirectoriesByPrimaryHash(lookupHash)];
        }
    }

    /// <summary>
    /// 指定された primary hash 群に対応する destination の直下 path を返します。
    /// </summary>
    /// <param name="primaryHashes">照会する primary hash 群。</param>
    /// <param name="destinationDirectory">直下判定する destination。</param>
    /// <param name="storageRowsOwner">owned collection の canonical source。</param>
    /// <param name="logOverride">必要な build のログ callback。</param>
    /// <returns>重複を除いた path 一覧。</returns>
    internal List<string> GetInstalledDirectChildPathsByPrimaryHashes(
        IEnumerable<string> primaryHashes,
        string destinationDirectory,
        CatalogStorageRowsOwner storageRowsOwner,
        Action<string> logOverride = null)
    {
        var hashes = new HashSet<string>(
            (primaryHashes ?? []).Where(hash => !string.IsNullOrWhiteSpace(hash)),
            StringComparer.OrdinalIgnoreCase);
        string destinationDirectoryKey = CreateDirectChildDirectoryComparisonKey(destinationDirectory);
        if (hashes.Count == 0 || string.IsNullOrWhiteSpace(destinationDirectoryKey))
        {
            return [];
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnsureInstalledChartLookupIndexBuilt(storageRowsOwner, logOverride);
        lock (lockInstalledChartLookupIndex)
        {
            foreach (string hash in hashes)
            {
                foreach (string path in installedChartLookupIndex.GetPathsByPrimaryHash(hash))
                {
                    if (IsDirectChildPathOfDirectory(path, destinationDirectoryKey))
                    {
                        paths.Add(path);
                    }
                }
            }
        }

        return [.. paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// chart の primary hash に対応する重複修復候補 path を返します。
    /// </summary>
    /// <param name="chart">修復対象 chart。</param>
    /// <param name="storageRowsOwner">owned collection の canonical source。</param>
    /// <param name="logOverride">必要な build のログ callback。</param>
    /// <returns>対象 chart 自身を除いた候補 path。</returns>
    internal IReadOnlyList<string> GetDuplicateInstallRepairPaths(
        ChartFile chart,
        CatalogStorageRowsOwner storageRowsOwner,
        Action<string> logOverride = null)
    {
        string lookupHash = ChartLookupKey.GetPrimaryHash(chart);
        if (string.IsNullOrWhiteSpace(lookupHash))
        {
            return [];
        }

        EnsureInstalledChartLookupIndexBuilt(storageRowsOwner, logOverride);
        lock (lockInstalledChartLookupIndex)
        {
            return installedChartLookupIndex.GetPathsByPrimaryHash(lookupHash)
                .Where(path => !string.Equals(path, chart.Path, StringComparison.OrdinalIgnoreCase))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }
    }

    /// <summary>
    /// installed primary hash snapshot を除外 chart 付きで作成します。
    /// </summary>
    /// <param name="excluded">除外する chart。</param>
    /// <param name="storageRowsOwner">owned collection の canonical source。</param>
    /// <param name="reason">ログ用の理由。</param>
    /// <param name="operationId">ログ用の operation id。</param>
    /// <param name="logOverride">build / lookup のログ callback。</param>
    /// <returns>除外を反映した primary hash lookup。</returns>
    internal IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingCharts(
        IEnumerable<ChartFile> excluded,
        CatalogStorageRowsOwner storageRowsOwner,
        string reason = null,
        long operationId = 0L,
        Action<string> logOverride = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var excludedKeyCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int excludedChartCount = 0;
        foreach (ChartFile item in excluded ?? [])
        {
            if (item == null)
            {
                continue;
            }
            excludedChartCount++;
            string key = ChartLookupKey.GetPrimaryHash(item);
            if (!string.IsNullOrWhiteSpace(key))
            {
                excludedKeyCount[key] = excludedKeyCount.TryGetValue(key, out int value) ? value + 1 : 1;
            }
        }

        bool coldBuild = EnsureInstalledPrimaryHashLookupBuilt(
            storageRowsOwner,
            out long buildMs,
            out int bmsCount,
            out int bmsonCount,
            logOverride);
        IPrimaryHashLookup result;
        lock (lockInstalledPrimaryHashLookup)
        {
            result = installedPrimaryHashLookup.CreateExcludingLookup(excludedKeyCount);
        }
        stopwatch.Stop();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            logOverride?.Invoke("installed_primary_hash_lookup excluding_snapshot reason=" + reason
                + " op=" + operationId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " coldBuild=" + coldBuild
                + " buildMs=" + buildMs
                + " excludedCharts=" + excludedChartCount
                + " excludedHashes=" + excludedKeyCount.Count
                + " primaryHashes=" + result.DistinctPrimaryHashCount
                + " files=" + bmsCount
                + " bmson=" + bmsonCount);
        }
        return result;
    }

    /// <summary>
    /// 指定 directory の直下判定用に path を正規化します。
    /// </summary>
    private static bool IsDirectChildPathOfDirectory(string path, string destinationDirectoryKey)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(destinationDirectoryKey))
        {
            return false;
        }

        string directory;
        try
        {
            directory = DirectoryExt.GetDirectoryNameSimple(path);
        }
        catch
        {
            return false;
        }
        string directoryKey = CreateDirectChildDirectoryComparisonKey(directory);
        return !string.IsNullOrWhiteSpace(directoryKey)
            && string.Equals(directoryKey, destinationDirectoryKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// path comparison 用の directory key を作成します。
    /// </summary>
    private static string CreateDirectChildDirectoryComparisonKey(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return LongPathFileSystem.TrimTrailingDirectorySeparators(
                LongPathFileSystem.NormalizePathForStorage(directory.Trim()));
        }
        catch
        {
            return directory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
