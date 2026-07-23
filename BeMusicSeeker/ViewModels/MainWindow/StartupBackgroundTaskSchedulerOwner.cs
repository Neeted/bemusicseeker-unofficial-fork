using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns startup background work scheduling, accounting, and shutdown drain policy.
/// </summary>
internal sealed class StartupBackgroundTaskSchedulerOwner
{
    private sealed class Request
    {
        internal string Name;

        internal string Reason;

        internal string Dependency;

        internal string CoalesceKey;

        internal string Lane;

        internal int Priority;

        internal long Version;

        internal long Generation;

        internal Func<Task> Work;

        internal Action<string> Discard;
    }

    private sealed class Metric
    {
        internal string Name = string.Empty;

        internal string Reason = string.Empty;

        internal string Dependency = string.Empty;

        internal string Lane = string.Empty;

        internal long QueuedCount;

        internal long StartedCount;

        internal long CompletedCount;

        internal long FailedCount;

        internal long TotalElapsedMs;

        internal long LastElapsedMs;

        internal string LastStatus = string.Empty;

        internal string LastDetail = string.Empty;
    }

    private readonly object syncRoot = new();

    private readonly object progressSynchronization;

    private readonly Func<bool> isShutdownRequested;

    private readonly Action<string> logInfo;

    private readonly Action<string> logWarning;

    private readonly Action<string> logShutdown;

    private readonly Func<string, string> formatTextForLog;

    private readonly Action<long, long> schedulerIdleChanged;

    private readonly List<Request> queue = [];

    private readonly Dictionary<string, long> latestRequestVersionByName = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, long> completedRequestVersionByName = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> runningCountByLane = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Metric> metrics = new(StringComparer.OrdinalIgnoreCase);

    private bool started;

    private int runningCount;

    private long version;

    private long generation;

    private long idleRevision;

    internal StartupBackgroundTaskSchedulerOwner(
        Func<bool> isShutdownRequested,
        Action<string> logInfo,
        Action<string> logWarning,
        Action<string> logShutdown,
        Func<string, string> formatTextForLog,
        Action<long, long> schedulerIdleChanged,
        object progressSynchronization)
    {
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        this.logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
        this.logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        this.logShutdown = logShutdown ?? throw new ArgumentNullException(nameof(logShutdown));
        this.formatTextForLog = formatTextForLog ?? throw new ArgumentNullException(nameof(formatTextForLog));
        this.schedulerIdleChanged = schedulerIdleChanged ?? throw new ArgumentNullException(nameof(schedulerIdleChanged));
        this.progressSynchronization = progressSynchronization ?? throw new ArgumentNullException(nameof(progressSynchronization));
    }

    internal object ProgressSynchronization => progressSynchronization;

    internal bool IsStarted
    {
        get
        {
            lock (syncRoot)
            {
                return started;
            }
        }
    }

    internal bool IsIdle
    {
        get
        {
            lock (progressSynchronization)
            {
                lock (syncRoot)
                {
                    return queue.Count == 0 && runningCount == 0;
                }
            }
        }
    }

    internal bool IsCurrentGeneration(long candidateGeneration)
    {
        lock (syncRoot)
        {
            return generation == candidateGeneration;
        }
    }

    internal bool IsCurrentIdleSnapshot(long candidateGeneration, long candidateRevision)
    {
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                return generation == candidateGeneration
                    && idleRevision == candidateRevision
                    && queue.Count == 0
                    && runningCount == 0;
            }
        }
    }

    internal bool Queue(
        string name,
        string reason,
        string dependency,
        Func<Task> work,
        Action<string> discard = null)
    {
        if (work == null)
        {
            return false;
        }
        string normalizedName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        if (isShutdownRequested())
        {
            logInfo("startup_background_task skipped name=" + normalizedName + " reason=" + normalizedReason + " detail=shutdown_requested");
            return false;
        }
        string normalizedDependency = string.IsNullOrWhiteSpace(dependency) ? null : dependency;
        string normalizedLane = GetLane(normalizedName);
        int priority = GetPriority(normalizedName);
        bool shouldStartWorker;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                if (isShutdownRequested())
                {
                    logInfo("startup_background_task skipped name=" + normalizedName + " reason=" + normalizedReason + " detail=shutdown_requested_after_lock");
                    return false;
                }
                RecordQueuedUnsafe(normalizedName, normalizedReason, normalizedDependency, normalizedLane);
                idleRevision++;
                long requestVersion = ++version;
                latestRequestVersionByName[normalizedName] = requestVersion;
                Request existing = queue.LastOrDefault(item => string.Equals(item.CoalesceKey, normalizedName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Reason = normalizedReason;
                    existing.Dependency = normalizedDependency;
                    existing.Lane = normalizedLane;
                    existing.Priority = priority;
                    existing.Version = requestVersion;
                    existing.Work = work;
                    existing.Discard = discard;
                    logInfo("startup_background_task skipped name=" + normalizedName + " version=" + requestVersion + " reason=" + normalizedReason + " coalesceKey=" + normalizedName + " replaced=true");
                }
                else
                {
                    queue.Add(new Request
                    {
                        Name = normalizedName,
                        Reason = normalizedReason,
                        Dependency = normalizedDependency,
                        Lane = normalizedLane,
                        CoalesceKey = normalizedName,
                        Priority = priority,
                        Version = requestVersion,
                        Generation = generation,
                        Work = work,
                        Discard = discard
                    });
                }
                logInfo("startup_background_task queue name=" + normalizedName + " version=" + requestVersion + " reason=" + normalizedReason + " dependency=" + (normalizedDependency ?? "(none)") + " lane=" + normalizedLane + " priority=" + priority);
                shouldStartWorker = started;
            }
        }
        if (shouldStartWorker)
        {
            TryStartWorkers();
        }
        return true;
    }

    internal void Start()
    {
        bool shouldStartWorker;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                if (started)
                {
                    return;
                }
                started = true;
                idleRevision++;
                shouldStartWorker = queue.Count > 0;
            }
        }
        logInfo("startup_background_task scheduler_start");
        if (shouldStartWorker)
        {
            TryStartWorkers();
        }
        else
        {
            NotifyIdleChanged();
        }
    }

    internal void Reset(bool startImmediately)
    {
        bool shouldStartWorker;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                generation++;
                idleRevision++;
                for (int i = 0; i < queue.Count; i++)
                {
                    Request request = queue[i];
                    request.Generation = generation;
                    request.Version = ++version;
                    latestRequestVersionByName[request.Name] = request.Version;
                }
                metrics.Clear();
                foreach (Request request in queue)
                {
                    RecordQueuedUnsafe(request.Name, request.Reason, request.Dependency, request.Lane);
                }
                started = startImmediately;
                shouldStartWorker = started && queue.Count > 0;
            }
        }
        if (shouldStartWorker)
        {
            TryStartWorkers();
        }
    }

    internal void RequestShutdown(string reason)
    {
        bool shouldStartWorker;
        int originalQueuedCount;
        int discardedCount = 0;
        int drainQueuedCount;
        List<Request> discardedRequests = null;
        lock (progressSynchronization)
        {
            lock (syncRoot)
            {
                originalQueuedCount = queue.Count;
                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    Request request = queue[i];
                    if (IsRequiredForShutdown(request.Name))
                    {
                        continue;
                    }
                    queue.RemoveAt(i);
                    RecordDiscardedUnsafe(request, reason);
                    discardedRequests ??= [];
                    discardedRequests.Add(request);
                    discardedCount++;
                }
                drainQueuedCount = queue.Count;
                if (drainQueuedCount > 0)
                {
                    started = true;
                }
                idleRevision++;
                shouldStartWorker = drainQueuedCount > 0;
                logShutdown("startup_background_task drain_queued reason=" + formatTextForLog(reason)
                    + " queued=" + originalQueuedCount
                    + " drainQueued=" + drainQueuedCount
                    + " discarded=" + discardedCount
                    + " running=" + runningCount);
            }
        }
        if (discardedRequests != null)
        {
            foreach (Request request in discardedRequests)
            {
                try
                {
                    request.Discard?.Invoke(reason);
                }
                catch (Exception exception)
                {
                    logWarning("startup_background_task discard cleanup failed name=" + request.Name + " message=" + exception.Message);
                }
            }
        }
        if (shouldStartWorker)
        {
            TryStartWorkers();
        }
        else
        {
            NotifyIdleChanged();
        }
    }

    internal void Report(string name, string status, long elapsedMs, bool failed, string detail)
    {
        lock (syncRoot)
        {
            Metric metric = GetOrCreateMetricUnsafe(name);
            if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                metric.QueuedCount++;
                metric.Reason = detail ?? string.Empty;
                metric.Lane = GetLane(name);
                metric.LastStatus = "queued";
                return;
            }
            if (string.Equals(status, "start", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                metric.StartedCount++;
                if (string.IsNullOrWhiteSpace(metric.Lane))
                {
                    metric.Lane = GetLane(name);
                }
                metric.LastStatus = "running";
                metric.LastDetail = detail ?? string.Empty;
                return;
            }
            if (failed)
            {
                metric.FailedCount++;
            }
            else
            {
                metric.CompletedCount++;
            }
            metric.LastStatus = string.IsNullOrWhiteSpace(status) ? (failed ? "failed" : "done") : status;
            if (string.IsNullOrWhiteSpace(metric.Lane))
            {
                metric.Lane = GetLane(name);
            }
            metric.LastElapsedMs = Math.Max(0L, elapsedMs);
            metric.TotalElapsedMs += Math.Max(0L, elapsedMs);
            metric.LastDetail = detail ?? string.Empty;
        }
    }

    internal string DescribeWaitState()
    {
        lock (syncRoot)
        {
            return "queueCount=" + queue.Count + " runningCount=" + runningCount;
        }
    }

    internal string BuildSummaryLog(long elapsedMs)
    {
        List<Metric> snapshot;
        lock (syncRoot)
        {
            snapshot = [.. metrics.Values
                .OrderBy(metric => metric.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CloneMetric)];
        }
        long queued = snapshot.Sum(metric => metric.QueuedCount);
        long startedCount = snapshot.Sum(metric => metric.StartedCount);
        long completed = snapshot.Sum(metric => metric.CompletedCount);
        long failed = snapshot.Sum(metric => metric.FailedCount);
        string taskSummary = snapshot.Count == 0
            ? "(none)"
            : string.Join(";", snapshot.Select(FormatMetric));
        return "startup_background_summary elapsedMs=" + elapsedMs
            + " queued=" + queued
            + " started=" + startedCount
            + " completed=" + completed
            + " failed=" + failed
            + " tasks=" + taskSummary;
    }

    private void TryStartWorkers()
    {
        while (true)
        {
            Request request = null;
            int laneRunningCount = 0;
            int totalRunningCount = 0;
            lock (progressSynchronization)
            {
                lock (syncRoot)
                {
                    if (!started || runningCount >= GetTotalConcurrency())
                    {
                        return;
                    }
                    int index = FindNextRequestIndexUnsafe();
                    if (index < 0)
                    {
                        return;
                    }
                    request = queue[index];
                    queue.RemoveAt(index);
                    idleRevision++;
                    runningCount++;
                    runningCountByLane.TryGetValue(request.Lane, out int runningInLane);
                    runningCountByLane[request.Lane] = runningInLane + 1;
                    laneRunningCount = runningInLane + 1;
                    totalRunningCount = runningCount;
                }
            }
            StartWorker(request, laneRunningCount, totalRunningCount);
        }
    }

    private int FindNextRequestIndexUnsafe()
    {
        int index = -1;
        int bestPriority = int.MaxValue;
        long bestVersion = long.MaxValue;
        bool shutdownRequestedSnapshot = isShutdownRequested();
        for (int i = 0; i < queue.Count; i++)
        {
            Request candidate = queue[i];
            if (!shutdownRequestedSnapshot && !AreDependenciesCompletedUnsafe(candidate.Dependency))
            {
                continue;
            }
            if (!CanStartInLaneUnsafe(candidate.Lane))
            {
                continue;
            }
            if (candidate.Priority < bestPriority || (candidate.Priority == bestPriority && candidate.Version < bestVersion))
            {
                index = i;
                bestPriority = candidate.Priority;
                bestVersion = candidate.Version;
            }
        }
        return index;
    }

    private bool CanStartInLaneUnsafe(string lane)
    {
        string normalizedLane = string.IsNullOrWhiteSpace(lane) ? "default" : lane;
        runningCountByLane.TryGetValue(normalizedLane, out int runningInLane);
        return runningInLane < GetLaneConcurrency(normalizedLane);
    }

    private void StartWorker(Request request, int laneRunningCount, int totalRunningCount)
    {
        Task.Run(async delegate
        {
            var stopwatch = Stopwatch.StartNew();
            logInfo("startup_background_task start name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " dependency=" + (request.Dependency ?? "(none)") + " lane=" + request.Lane + " laneRunning=" + laneRunningCount + " totalRunning=" + totalRunningCount);
            RecordStarted(request);
            try
            {
                await request.Work().ConfigureAwait(false);
                stopwatch.Stop();
                logInfo("startup_background_task done name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " lane=" + request.Lane + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                RecordCompleted(request, "done", stopwatch.ElapsedMilliseconds, failed: false, detail: "reason=" + request.Reason);
                StartupMemoryPressureService.LogCheckpoint(logInfo, "startup_background_task", request.Name + "_done");
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                logWarning("startup_background_task failed name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " lane=" + request.Lane + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + exception.Message);
                RecordCompleted(request, "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: exception.Message);
                StartupMemoryPressureService.LogCheckpoint(logInfo, "startup_background_task", request.Name + "_failed");
            }
            finally
            {
                lock (progressSynchronization)
                {
                    lock (syncRoot)
                    {
                        if (!completedRequestVersionByName.TryGetValue(request.Name, out long completedVersion)
                            || request.Version > completedVersion)
                        {
                            completedRequestVersionByName[request.Name] = request.Version;
                        }
                        runningCount = Math.Max(0, runningCount - 1);
                        idleRevision++;
                        if (!string.IsNullOrWhiteSpace(request.Lane)
                            && runningCountByLane.TryGetValue(request.Lane, out int runningInLane))
                        {
                            runningInLane = Math.Max(0, runningInLane - 1);
                            if (runningInLane == 0)
                            {
                                runningCountByLane.Remove(request.Lane);
                            }
                            else
                            {
                                runningCountByLane[request.Lane] = runningInLane;
                            }
                        }
                    }
                }
                TryStartWorkers();
                NotifyIdleChanged();
            }
        }).Logging("StartupBackgroundTaskScheduler");
    }

    private void RecordStarted(Request request)
    {
        lock (syncRoot)
        {
            if (request.Generation != generation)
            {
                return;
            }
            Metric metric = GetOrCreateMetricUnsafe(request.Name);
            metric.StartedCount++;
            metric.LastStatus = "running";
        }
    }

    private void RecordCompleted(Request request, string status, long elapsedMs, bool failed, string detail)
    {
        lock (syncRoot)
        {
            if (request.Generation != generation)
            {
                return;
            }
            Metric metric = GetOrCreateMetricUnsafe(request.Name);
            if (failed)
            {
                metric.FailedCount++;
            }
            else
            {
                metric.CompletedCount++;
            }
            metric.LastStatus = status;
            metric.Lane = string.IsNullOrWhiteSpace(metric.Lane) ? GetLane(request.Name) : metric.Lane;
            metric.LastElapsedMs = Math.Max(0L, elapsedMs);
            metric.TotalElapsedMs += Math.Max(0L, elapsedMs);
            metric.LastDetail = detail ?? string.Empty;
        }
    }

    private void RecordQueuedUnsafe(string name, string reason, string dependency, string lane)
    {
        Metric metric = GetOrCreateMetricUnsafe(name);
        metric.QueuedCount++;
        metric.Reason = reason ?? string.Empty;
        metric.Dependency = dependency ?? string.Empty;
        metric.Lane = lane ?? string.Empty;
        metric.LastStatus = "queued";
    }

    private void RecordDiscardedUnsafe(Request request, string reason)
    {
        if (!completedRequestVersionByName.TryGetValue(request.Name, out long completedVersion)
            || request.Version > completedVersion)
        {
            completedRequestVersionByName[request.Name] = request.Version;
        }
        Metric metric = GetOrCreateMetricUnsafe(request.Name);
        metric.CompletedCount++;
        metric.LastStatus = "discarded";
        metric.LastElapsedMs = 0L;
        metric.LastDetail = "shutdown_requested reason=" + formatTextForLog(reason);
        if (string.IsNullOrWhiteSpace(metric.Reason))
        {
            metric.Reason = request.Reason ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(metric.Dependency))
        {
            metric.Dependency = request.Dependency ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(metric.Lane))
        {
            metric.Lane = request.Lane ?? GetLane(request.Name);
        }
        logInfo("startup_background_task discarded name=" + request.Name
            + " version=" + request.Version
            + " reason=" + request.Reason
            + " shutdownReason=" + formatTextForLog(reason));
    }

    private Metric GetOrCreateMetricUnsafe(string name)
    {
        string normalizedName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        if (!metrics.TryGetValue(normalizedName, out Metric metric))
        {
            metric = new Metric { Name = normalizedName };
            metrics[normalizedName] = metric;
        }
        return metric;
    }

    private bool AreDependenciesCompletedUnsafe(string dependency)
    {
        if (string.IsNullOrWhiteSpace(dependency))
        {
            return true;
        }
        string[] dependencies = dependency.Split([','], StringSplitOptions.RemoveEmptyEntries);
        foreach (string item in dependencies)
        {
            string dependencyName = item.Trim();
            if (dependencyName.Length > 0
                && (!latestRequestVersionByName.TryGetValue(dependencyName, out long latestVersion)
                    || !completedRequestVersionByName.TryGetValue(dependencyName, out long completedVersion)
                    || completedVersion < latestVersion))
            {
                return false;
            }
        }
        return true;
    }

    private void NotifyIdleChanged()
    {
        try
        {
            long notificationGeneration;
            long notificationRevision;
            lock (progressSynchronization)
            {
                lock (syncRoot)
                {
                    notificationGeneration = generation;
                    notificationRevision = idleRevision;
                }
            }
            schedulerIdleChanged(notificationGeneration, notificationRevision);
        }
        catch (Exception exception)
        {
            logWarning("startup_background_task idle notification failed message=" + exception.Message);
        }
    }

    private static bool IsRequiredForShutdown(string name)
    {
        return string.Equals(name, "lr2_song_db_sync", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPriority(string name)
    {
        if (string.Equals(name, "playlist_entries_hydration", StringComparison.OrdinalIgnoreCase)) return 10;
        if (string.Equals(name, "playlist_library_index_prewarm", StringComparison.OrdinalIgnoreCase)) return 15;
        if (string.Equals(name, "playlist_url_completion", StringComparison.OrdinalIgnoreCase)) return 20;
        if (string.Equals(name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase)) return 30;
        if (string.Equals(name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase)) return 40;
        if (string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase)) return 50;
        if (string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase)) return 55;
        if (string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase)) return 60;
        if (string.Equals(name, "playlist_custom_folder_output_repair", StringComparison.OrdinalIgnoreCase)) return 70;
        if (string.Equals(name, "lr2_song_db_sync", StringComparison.OrdinalIgnoreCase)) return 90;
        return 100;
    }

    private static string GetLane(string name)
    {
        if (string.Equals(name, "playlist_entries_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase)) return "read_hydration";
        if (string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase)) return "maintenance_hydration";
        if (string.Equals(name, "playlist_url_completion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase)) return "playlist_followup";
        if (string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_custom_folder_output_repair", StringComparison.OrdinalIgnoreCase)) return "dependent_maintenance";
        return "default";
    }

    private static int GetLaneConcurrency(string lane)
    {
        return string.Equals(lane, "read_hydration", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    private static int GetTotalConcurrency() => 4;

    private static Metric CloneMetric(Metric metric)
    {
        return new Metric
        {
            Name = metric.Name,
            Reason = metric.Reason,
            Dependency = metric.Dependency,
            Lane = metric.Lane,
            QueuedCount = metric.QueuedCount,
            StartedCount = metric.StartedCount,
            CompletedCount = metric.CompletedCount,
            FailedCount = metric.FailedCount,
            TotalElapsedMs = metric.TotalElapsedMs,
            LastElapsedMs = metric.LastElapsedMs,
            LastStatus = metric.LastStatus,
            LastDetail = metric.LastDetail
        };
    }

    private static string FormatMetric(Metric metric)
    {
        return Sanitize(metric.Name)
            + "{queued=" + metric.QueuedCount
            + ",started=" + metric.StartedCount
            + ",completed=" + metric.CompletedCount
            + ",failed=" + metric.FailedCount
            + ",lastStatus=" + Sanitize(metric.LastStatus)
            + ",lastMs=" + metric.LastElapsedMs
            + ",totalMs=" + metric.TotalElapsedMs
            + ",reason=" + Sanitize(metric.Reason)
            + ",dependency=" + Sanitize(metric.Dependency)
            + ",lane=" + Sanitize(metric.Lane)
            + ",detail=" + Sanitize(metric.LastDetail)
            + "}";
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return value
            .Replace(Environment.NewLine, " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace(";", ",")
            .Replace("{", "(")
            .Replace("}", ")")
            .Replace(" ", "_");
    }
}
