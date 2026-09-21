using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class NormalLibraryRefreshPublishRequest
{
    public int OwnedCollectionVersion { get; set; }

    public LibraryChartRefreshEffects Effects { get; set; }

    public IReadOnlyList<ChartFile> InstallDestinationChangedCharts { get; set; } = [];

    public bool NotifiesStorageRows { get; set; }

    public bool ResetsPriorNotifications { get; set; }

    public bool NotifiesBmsFiles { get; set; }

    public bool NotifiesBmsonSongs { get; set; }

    public IReadOnlyList<BMSFile> RemovedBmsFiles { get; set; } = [];

    public IReadOnlyList<LR2SongDBExtended.bmson_song> RemovedBmsonSongs { get; set; } = [];

    public bool StorageRowsRemoveDeltaComplete { get; set; }
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
                    notifiesStorageRows: false,
                    resetsPriorNotifications: false);
            }
            bool resetsPriorNotifications = resetIndex >= 0;
            int latestPendingVersion = pendingNotifications[pendingNotifications.Count - 1].Version;
            int ownedCollectionVersion = pendingNotifications[pendingNotifications.Count - 1].OwnedCollectionVersion;
            LibraryChartRefreshEffects effects = pendingNotifications.Aggregate(
                LibraryChartRefreshEffects.None,
                (current, notification) => current | notification.Effects);
            bool notifiesStorageRows = pendingNotifications.Any(notification => notification.NotifiesStorageRows);
            bool notifiesBmsFiles = pendingNotifications.Any(notification => notification.NotifiesBmsFiles);
            bool notifiesBmsonSongs = pendingNotifications.Any(notification => notification.NotifiesBmsonSongs);
            bool storageRowsRemoveDeltaComplete = notifiesStorageRows
                && pendingNotifications
                    .Where(notification => notification.NotifiesStorageRows)
                    .All(notification => notification.StorageRowsRemoveDeltaComplete);
            List<BMSFile> removedBmsFiles = storageRowsRemoveDeltaComplete
                ? [.. pendingNotifications.SelectMany(notification => notification.RemovedBmsFiles ?? []).Where(file => file != null).Distinct()]
                : [];
            List<LR2SongDBExtended.bmson_song> removedBmsonSongs = storageRowsRemoveDeltaComplete
                ? [.. pendingNotifications.SelectMany(notification => notification.RemovedBmsonSongs ?? []).Where(song => song != null).Distinct()]
                : [];
            List<ChartFile> installDestinationChangedCharts = [.. pendingNotifications
                .SelectMany(notification => notification.InstallDestinationChangedCharts ?? [])
                .Where(chart => chart != null)];
            return new NormalLibraryRefreshNotificationBatch(
                latestPendingVersion,
                ownedCollectionVersion,
                effects,
                DistinctChartsByNotificationKey(installDestinationChangedCharts),
                notifiesStorageRows,
                resetsPriorNotifications,
                notifiesBmsFiles,
                notifiesBmsonSongs,
                removedBmsFiles,
                removedBmsonSongs,
                storageRowsRemoveDeltaComplete);
        }
    }

    internal int Publish(NormalLibraryRefreshPublishRequest request)
    {
        if (request == null)
        {
            return 0;
        }
        int version = Interlocked.Increment(ref latestVersion);
        var notification = new NormalLibraryRefreshNotification(
            version,
            request.OwnedCollectionVersion,
            request.Effects,
            request.InstallDestinationChangedCharts,
            request.NotifiesStorageRows,
            request.ResetsPriorNotifications,
            request.NotifiesBmsFiles,
            request.NotifiesBmsonSongs,
            request.RemovedBmsFiles,
            request.RemovedBmsonSongs,
            request.StorageRowsRemoveDeltaComplete);
        lock (syncRoot)
        {
            latestNotification = notification;
            notifications.Add(notification);
        }
        return version;
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
        var resultIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null)
            {
                continue;
            }
            string key = CreateNormalLibraryRefreshNotificationKey(chart);
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

    private static string CreateNormalLibraryRefreshNotificationKey(ChartFile chart)
    {
        string kind = chart?.Kind.ToString() ?? string.Empty;
        string path = chart?.Path ?? string.Empty;
        string md5 = chart?.Md5 ?? string.Empty;
        string sha256 = chart?.Sha256 ?? string.Empty;
        return kind + "\n" + path + "\n" + md5 + "\n" + sha256;
    }
}
