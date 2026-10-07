using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class NormalLibraryRefreshPublishRequest
{
    public int OwnedCollectionVersion { get; init; }

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
    private NormalLibraryRefreshNotification latestNotification = NormalLibraryRefreshNotification.Empty;
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
            int ownedCollectionVersion = pendingNotifications[pendingNotifications.Count - 1].OwnedCollectionVersion;
            LibraryChartRefreshEffects effects = pendingNotifications.Aggregate(
                LibraryChartRefreshEffects.None,
                (current, notification) => current | notification.Effects);
            List<ChartFile> installDestinationChangedCharts = [.. pendingNotifications
                .SelectMany(notification => notification.InstallDestinationChangedCharts ?? [])
                .Where(chart => chart != null)];
            var deletedTokens = new HashSet<OwnedChartToken>(pendingNotifications.SelectMany(notification => notification.DeletedTokens));
            return new NormalLibraryRefreshNotificationBatch(
                latestPendingVersion,
                ownedCollectionVersion,
                effects,
                DistinctChartsByNotificationKey(installDestinationChangedCharts),
                resetsPriorNotifications,
                changedCharts: DistinctChartsByNotificationKey(pendingNotifications.SelectMany(notification => notification.ChangedCharts).Where(chart => chart.Token == null || !deletedTokens.Contains(chart.Token))),
                deletedTokens: [.. deletedTokens],
                changedDetailMd5s: [.. pendingNotifications.SelectMany(value => value.ChangedDetailMd5s).Distinct(System.StringComparer.OrdinalIgnoreCase)],
                changedDetailSha256s: [.. pendingNotifications.SelectMany(value => value.ChangedDetailSha256s).Distinct(System.StringComparer.OrdinalIgnoreCase)],
                installedChartChanges: [.. pendingNotifications.SelectMany(value => value.InstalledChartChanges)]);
        }
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
                request.OwnedCollectionVersion,
                request.Effects,
                request.InstallDestinationChangedCharts,
                request.ResetsPriorNotifications, request.ChangedCharts, request.DeletedTokens, request.ChangedDetailMd5s, request.ChangedDetailSha256s, request.InstalledChartChanges);
            latestNotification = notification;
            notifications.Add(notification);
            return version;
        }
    }

    internal void Clear(int notificationVersion)
    {
        if (notificationVersion <= 0)
        {
            return;
        }
        lock (syncRoot)
        {
            if (latestNotification.Version == notificationVersion)
            {
                latestNotification = NormalLibraryRefreshNotification.Empty;
            }
            notifications.RemoveAll(notification => notification.Version == notificationVersion);
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
