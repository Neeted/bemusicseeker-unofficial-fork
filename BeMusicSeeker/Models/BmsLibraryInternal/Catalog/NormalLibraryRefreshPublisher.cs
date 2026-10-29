using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class NormalLibraryRefreshPublishRequest
{
    public int CatalogRevision { get; init; }
    /// <summary>カタログwriterが同時確定した結果です。</summary>
    internal CatalogCommitReceipt Commit { get; init; }
    /// <summary>score正本が同じ確定境界で登録する独立結果です。</summary>
    internal ScoreSnapshotChange ScoreChange { get; init; }
    /// <summary>参照索引が同じ確定境界で登録する独立結果です。</summary>
    internal PlaylistReferenceCommitReceipt ReferenceCommit { get; init; }

    public IReadOnlyList<ChartFile> ChangedCharts { get; init; } = [];

    /// <summary>確定した導入済みentryの所属と旧新基本値です。</summary>
    public IReadOnlyList<InstalledChartCurrentChange> InstalledChartChanges { get; init; } = [];
    public IReadOnlyList<OwnedChartToken> DeletedTokens { get; init; } = [];
    internal IReadOnlyList<string> ChangedDetailMd5s { get; init; } = [];
    internal IReadOnlyList<string> ChangedDetailSha256s { get; init; } = [];

    public LibraryChartRefreshEffects Effects { get; init; }

    public IReadOnlyList<ChartFile> InstallDestinationChangedCharts { get; init; } = [];


    public bool ResetsPriorNotifications { get; init; }

}

internal sealed class NormalLibraryRefreshPublisher
{
    private readonly List<NormalLibraryRefreshNotification> notifications = [];
    private readonly object syncRoot = new();
    private int latestVersion;

    internal int Version => Volatile.Read(ref latestVersion);

    internal NormalLibraryRefreshNotificationBatch GetNotificationsAfter(int handledVersion)
    {
        lock (syncRoot)
        {
            notifications.RemoveAll(notification => notification == null || notification.Version <= handledVersion);
            List<NormalLibraryRefreshNotification> pendingNotifications = [.. notifications
                .Where(notification => notification != null && notification.Version > handledVersion)];
            ScoreSnapshotChange[] scoreChanges = [.. pendingNotifications.SelectMany(notification => notification.ScoreChanges)];
            PlaylistReferenceCommitReceipt[] referenceCommits = [.. pendingNotifications.SelectMany(notification => notification.ReferenceCommits)];
            int resetIndex = pendingNotifications.FindLastIndex(notification => notification.ResetsPriorNotifications);
            if (resetIndex >= 0)
            {
                pendingNotifications = [.. pendingNotifications.Skip(resetIndex)];
            }
            if (pendingNotifications.Count == 0)
            {
                return new NormalLibraryRefreshNotificationBatch(
                    handledVersion,
                    0,
                    LibraryChartRefreshEffects.None,
                    [],
                    resetsPriorNotifications: false);
            }
            bool resetsPriorNotifications = resetIndex >= 0;
            int latestPendingVersion = pendingNotifications[pendingNotifications.Count - 1].Version;
            int catalogRevision = pendingNotifications.Max(notification => notification.CatalogRevision);
            LibraryChartRefreshEffects effects = pendingNotifications.Aggregate(
                LibraryChartRefreshEffects.None,
                (current, notification) => current | notification.Effects);
            List<ChartFile> installDestinationChangedCharts = [.. pendingNotifications
                .SelectMany(notification => notification.InstallDestinationChangedCharts ?? [])
                .Where(chart => chart != null)];
            var deletedTokens = new HashSet<OwnedChartToken>(pendingNotifications.SelectMany(notification => notification.DeletedTokens));
            return new NormalLibraryRefreshNotificationBatch(
                latestPendingVersion,
                catalogRevision,
                effects,
                DistinctChartsByNotificationKey(installDestinationChangedCharts),
                resetsPriorNotifications,
                changedCharts: DistinctChartsByNotificationKey(pendingNotifications.SelectMany(notification => notification.ChangedCharts).Where(chart => chart.Token == null || !deletedTokens.Contains(chart.Token))),
                deletedTokens: [.. deletedTokens],
                changedDetailMd5s: [.. pendingNotifications.SelectMany(value => value.ChangedDetailMd5s).Distinct(System.StringComparer.OrdinalIgnoreCase)],
                changedDetailSha256s: [.. pendingNotifications.SelectMany(value => value.ChangedDetailSha256s).Distinct(System.StringComparer.OrdinalIgnoreCase)],
                installedChartChanges: [.. pendingNotifications.SelectMany(value => value.InstalledChartChanges)],
                catalogCommits: [.. pendingNotifications.SelectMany(value => value.CatalogCommits)],
                scoreChanges: scoreChanges, referenceCommits: referenceCommits);
        }
    }

    /// <summary>writer の確定境界で不変結果を登録します。配送イベントは排他解放後に既存接続が起動します。</summary>
    internal int Register(CatalogCommitReceipt receipt)
    {
        if (receipt?.HasChanges != true)
        {
            return Version;
        }

        return Publish(new NormalLibraryRefreshPublishRequest
        {
            CatalogRevision = receipt.Revision,
            Commit = receipt,
            Effects = receipt.Changes.Aggregate(receipt.OrderChangedTokens.Count > 0 ? LibraryChartRefreshEffects.SourceChanged : LibraryChartRefreshEffects.None,
                (effects, change) => effects | ClassifyEffects(change)),
            ChangedCharts = System.Array.AsReadOnly(receipt.Changes.Where(change => change.After != null).Select(change => change.After).ToArray()),
            DeletedTokens = System.Array.AsReadOnly(receipt.Changes.Where(change => change.After == null).Select(change => change.Token).ToArray()),
            ResetsPriorNotifications = receipt.IsFullReplacement
        });
    }

    /// <summary>scoreの版発行・lookup採用と同じ排他内で結果を登録します。配送起動は呼出元が解放後に行います。</summary>
    internal int Register(ScoreSnapshotChange change)
        => change == null ? Version : Publish(new NormalLibraryRefreshPublishRequest { ScoreChange = change });

    /// <summary>参照索引の確定順を既存キューへ登録します。空の表示差分でも版範囲は省略しません。</summary>
    internal int Register(PlaylistReferenceCommitReceipt receipt)
        => receipt == null ? Version : Publish(new NormalLibraryRefreshPublishRequest { ReferenceCommit = receipt });

    private static LibraryChartRefreshEffects ClassifyEffects(CatalogChartChange change)
    {
        ChartFile before = change.Before, after = change.After;
        if (before == null || after == null)
        {
            return LibraryChartRefreshEffects.SourceChanged | LibraryChartRefreshEffects.WarningPresentationChanged;
        }

        bool maintenanceChanged = before.ResourceHealthMaintenanceSnapshot != after.ResourceHealthMaintenanceSnapshot
            || before.EncodingName != after.EncodingName || before.WAVHealth != after.WAVHealth || before.BGAHealth != after.BGAHealth
            || before.MovieHealth != after.MovieHealth || before.StagefileHealth != after.StagefileHealth
            || before.BannerHealth != after.BannerHealth || before.BackbmpHealth != after.BackbmpHealth;
        bool ignoreOnly = maintenanceChanged && before.ResourceHealthMaintenanceSnapshot != null && after.ResourceHealthMaintenanceSnapshot != null
            && before.ResourceHealthMaintenanceSnapshot with { FilesWarningIgnored = after.ResourceHealthMaintenanceSnapshot.FilesWarningIgnored }
                == after.ResourceHealthMaintenanceSnapshot && before.EncodingName == after.EncodingName;
        bool sourceChanged = !CatalogChartValueEquality.AreEqual(before, after with
        {
            ResourceHealthMaintenanceSnapshot = before.ResourceHealthMaintenanceSnapshot,
            EncodingName = before.EncodingName,
            WAVHealth = before.WAVHealth,
            BGAHealth = before.BGAHealth,
            MovieHealth = before.MovieHealth,
            StagefileHealth = before.StagefileHealth,
            BannerHealth = before.BannerHealth,
            BackbmpHealth = before.BackbmpHealth,
            Warnings = before.Warnings,
            ResourceHealthWarningsIgnored = before.ResourceHealthWarningsIgnored,
            ChartInfo = before.ChartInfo,
            ChartInfoDisplay = before.ChartInfoDisplay,
            Score = before.Score,
            // 資源参照の取得・更新・解放は短命な保守入力であり、通常一覧の基本値の変更にはしません。
            Resources = before.Resources
        });
        LibraryChartRefreshEffects effects = sourceChanged ? LibraryChartRefreshEffects.SourceChanged : LibraryChartRefreshEffects.None;
        if (maintenanceChanged && !ignoreOnly)
        {
            effects |= LibraryChartRefreshEffects.MaintenancePresentationChanged;
        }

        if (maintenanceChanged || before.ResourceHealthWarningsIgnored != after.ResourceHealthWarningsIgnored
            || !before.Warnings.Select(value => (value.Kind, value.Message)).SequenceEqual(after.Warnings.Select(value => (value.Kind, value.Message))))
        {
            effects |= LibraryChartRefreshEffects.WarningPresentationChanged;
        }

        return effects;
    }

    internal int Publish(NormalLibraryRefreshPublishRequest request)
    {
        if (request == null)
        {
            return 0;
        }
        lock (syncRoot)
        {
            int version = Interlocked.Increment(ref latestVersion);
            var notification = new NormalLibraryRefreshNotification(
                version,
                request.CatalogRevision,
                request.Effects,
                request.InstallDestinationChangedCharts,
                request.ResetsPriorNotifications, request.ChangedCharts, request.DeletedTokens, request.ChangedDetailMd5s, request.ChangedDetailSha256s, request.InstalledChartChanges, request.Commit == null ? [] : [request.Commit], request.ScoreChange == null ? [] : [request.ScoreChange], request.ReferenceCommit == null ? [] : [request.ReferenceCommit]);
            notifications.Add(notification);
            return version;
        }
    }

    /// <summary>未公開の補助通知だけを破棄します。ownerが登録したcatalog/score/参照の確定結果は失敗時にも削除しません。</summary>
    internal void ClearLegacyNotification(int notificationVersion)
    {
        if (notificationVersion <= 0)
        {
            return;
        }
        lock (syncRoot)
        {
            notifications.RemoveAll(notification => notification.Version == notificationVersion && notification.CatalogCommits.Count == 0 && notification.ScoreChanges.Count == 0 && notification.ReferenceCommits.Count == 0);
        }
    }

    private static IReadOnlyList<ChartFile> DistinctChartsByNotificationKey(IEnumerable<ChartFile> charts)
    {
        var result = new List<ChartFile>();
        var resultIndexByKey = new Dictionary<object, int>();
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null)
            {
                continue;
            }
            object key = chart.Token is OwnedChartToken token ? token : (chart.Kind, chart.Path);
            if (resultIndexByKey.TryGetValue(key, out int index))
            {
                result[index] = chart;
            }
            else
            {
                resultIndexByKey[key] = result.Count;
                result.Add(chart);
            }
        }
        return result;
    }

}
