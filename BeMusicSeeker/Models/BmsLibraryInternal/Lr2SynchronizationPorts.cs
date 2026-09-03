using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Supplies the narrow, typed capabilities used by the LR2 synchronization owner.
/// Implementations are composed from canonical owners and application services;
/// they never retain the application facade as a state host.
/// </summary>
internal interface ILr2SynchronizationDataPort
{
    BmsLibraryOptionsSnapshot CurrentOptionsSnapshot { get; }

    EverythingNative EverythingNative { get; }

    int OwnedChartCollectionVersion { get; }

    IReadOnlyList<BMSFile> CaptureBmsFilesSnapshot();

    Lr2SongDbSyncInputRowSnapshot CaptureLr2SynchronizationInputRowSnapshot();

    StorageRowsVersionSnapshot CaptureStorageRowsVersionSnapshot();

    ChartInfoOwnerVersionSnapshot CaptureChartInfoOwnerVersionSnapshot();

    TimeSpan CurrentChartInfoParseTimeout { get; }

    Lr2SearchRootSnapshot CaptureBmsDirectories();

    LR2Config CreateCurrentLr2ConfigOrNull();

    Lr2SongDbSyncAppManagedOutputScope CaptureLr2SongDbSyncAppManagedOutputScope(
        BmsLibraryOptionsSnapshot options);

    Lr2FolderExistingRowsSnapshot CaptureLr2FolderExistingRows(Lr2SongDbSyncRequest request);

    Lr2SongDbSyncStatusSnapshot EvaluateLr2SongDbSyncStatus(
        bool enabled,
        string signature,
        DateTime nowUtc);

    Lr2SongDbSyncStatusSnapshot ApplyLr2SongDbSyncStatusMutation(
        Lr2SongDbSyncStatusMutationRequest request);

    Lr2FolderFileDbSyncResult ApplyLr2FolderFileSync(Lr2FolderFileDbSyncRequest request);

    Lr2NormalFolderDbSyncResult ApplyLr2NormalFolderSync(Lr2NormalFolderDbSyncRequest request);

    Lr2SongDbSyncResult ApplyLr2SongDbSync(Lr2SongDbSyncRequest request);

}

internal sealed class Lr2FolderExistingRowsSnapshot
{
    internal Lr2FolderExistingRowsSnapshot(
        IReadOnlyDictionary<string, LR2SongDB.folder> rowsByPath)
    {
        RowsByPath = rowsByPath ?? new Dictionary<string, LR2SongDB.folder>(StringComparer.Ordinal);
    }

    internal IReadOnlyDictionary<string, LR2SongDB.folder> RowsByPath { get; }
}

internal sealed class Lr2SearchRootSnapshot
{
    internal Lr2SearchRootSnapshot(
        IReadOnlyList<string> roots,
        int requestedRootCount,
        int existingRootCount,
        int excludedCustomOutputRootCount,
        int configuredCustomOutputRootCount)
    {
        Roots = roots ?? [];
        RequestedRootCount = requestedRootCount;
        ExistingRootCount = existingRootCount;
        ExcludedCustomOutputRootCount = excludedCustomOutputRootCount;
        ConfiguredCustomOutputRootCount = configuredCustomOutputRootCount;
    }

    internal IReadOnlyList<string> Roots { get; }

    internal int RequestedRootCount { get; }

    internal int ExistingRootCount { get; }

    internal int ExcludedCustomOutputRootCount { get; }

    internal int ConfiguredCustomOutputRootCount { get; }
}

internal enum Lr2SongDbSyncStatusMutationKind
{
    MarkIncomplete,
    MarkFailed
}

internal sealed class Lr2SongDbSyncStatusMutationRequest
{
    internal Lr2SongDbSyncStatusMutationRequest(
        Lr2SongDbSyncStatusMutationKind kind,
        string signature,
        string runId,
        int? processedCursor,
        int? totalCount,
        string stage,
        string detail,
        DateTime nowUtc)
    {
        Kind = kind;
        Signature = signature;
        RunId = runId;
        ProcessedCursor = processedCursor;
        TotalCount = totalCount;
        Stage = stage;
        Detail = detail;
        NowUtc = nowUtc;
    }

    internal Lr2SongDbSyncStatusMutationKind Kind { get; }

    internal string Signature { get; }

    internal string RunId { get; }

    internal int? ProcessedCursor { get; }

    internal int? TotalCount { get; }

    internal string Stage { get; }

    internal string Detail { get; }

    internal DateTime NowUtc { get; }
}

/// <summary>
/// Exposes lifecycle, scheduling and the scoped dialog boundary needed by LR2 work.
/// </summary>
internal interface ILr2SynchronizationRuntimePort
{
    bool IsShutdownRequested { get; }

    Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; }

    UiDialogDefaultResult ShowOperationDialog(
        string messageBoxText,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult);

    bool TrySkipForShutdown(string operation, string reason);

    void ReportStartupBackgroundTask(string name, string status, long elapsedMs, bool failed, string detail);
}

/// <summary>
/// Provides catalog and presentation operations through canonical owners.
/// </summary>
internal interface ILr2SynchronizationProjectionPort
{
    void EnsureLr2SongDbSyncChartInfoIndexHydrated(string reason);

    Dictionary<string, BMSFile> CreateLr2SongDbSyncCompatibilityProjectionIndex();

    Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2SongDbSyncChartInfoResolverSnapshot();

    HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason);

    void UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<LR2SongDBExtended.chart_info> rows);

    int ApplyLr2SongDbSyncCompatibilityProjection(
        IReadOnlyList<BMSFileMaintenanceInfo> maintenanceInfos,
        string reason,
        IReadOnlyDictionary<string, BMSFile> bmsByPath,
        bool logSummary,
        bool dispatchPresentation);

    void DispatchWarningPresentationChanged(string reason);
}

/// <summary>
/// Narrow chart-info capability used by LR2 synchronization.  The capability
/// keeps only a weak reference to the chart-info owner so LR2 ports cannot
/// prolong the application facade lifetime through its workflow callbacks.
/// </summary>
internal interface ILr2ChartInfoCapability
{
    ChartInfoOwnerVersionSnapshot CaptureOwnerVersionSnapshot();

    TimeSpan CurrentParseTimeout { get; }

    void EnsureHydratedForLr2(string reason);

    Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2ResolverSnapshot();

    Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> LoadCurrentParseFailureMap(
        BmsLibraryDbGateway dbGateway,
        TimeSpan parseTimeout);

    void UpsertIndex(IReadOnlyList<LR2SongDBExtended.chart_info> rows);

    void PublishWarningPresentationChanged(string reason);
}

internal sealed class Lr2ChartInfoCapability : ILr2ChartInfoCapability
{
    private readonly WeakReference<CatalogChartInfoOwner> ownerReference;

    internal Lr2ChartInfoCapability(CatalogChartInfoOwner owner)
    {
        ownerReference = new WeakReference<CatalogChartInfoOwner>(
            owner ?? throw new ArgumentNullException(nameof(owner)));
    }

    public ChartInfoOwnerVersionSnapshot CaptureOwnerVersionSnapshot() =>
        GetOwner().CaptureOwnerVersionSnapshot();

    public TimeSpan CurrentParseTimeout => GetOwner().BuildService.CurrentParseTimeout;

    public void EnsureHydratedForLr2(string reason) =>
        GetOwner().EnsureHydratedForLr2(reason);

    public Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2ResolverSnapshot() =>
        GetOwner().CreateLr2ResolverSnapshot();

    public Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> LoadCurrentParseFailureMap(
        BmsLibraryDbGateway dbGateway,
        TimeSpan parseTimeout) =>
        GetOwner().LoadCurrentParseFailureMap(dbGateway, parseTimeout);

    public void UpsertIndex(IReadOnlyList<LR2SongDBExtended.chart_info> rows) =>
        GetOwner().UpsertIndex(rows, "lr2_song_db_sync_inline_chart_info", dispatchPresentation: false);

    public void PublishWarningPresentationChanged(string reason) =>
        GetOwner().PublishWarningPresentationChanged(reason);

    private CatalogChartInfoOwner GetOwner()
    {
        if (ownerReference.TryGetTarget(out CatalogChartInfoOwner owner))
        {
            return owner;
        }
        throw new InvalidOperationException("Chart-info capability owner is no longer available.");
    }
}

internal sealed class Lr2ConfigSnapshotProvider
{
    private readonly Func<LR2Config> provider;

    internal Lr2ConfigSnapshotProvider(Func<LR2Config> provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    internal LR2Config GetOrNull()
    {
        return TryGet(out LR2Config config) ? config : null;
    }

    internal bool TryGet(out LR2Config config)
    {
        config = null;
        try
        {
            config = provider();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class Lr2SearchRootSnapshotOwner
{
    private readonly Lr2ConfigSnapshotProvider configProvider;
    private readonly Func<BmsLibraryOptionsSnapshot> optionsProvider;

    internal Lr2SearchRootSnapshotOwner(
        Lr2ConfigSnapshotProvider configProvider,
        Func<BmsLibraryOptionsSnapshot> optionsProvider)
    {
        this.configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
    }

    internal List<string> SearchTargets { get; set; } = [];

    internal Lr2SearchRootSnapshot Capture()
    {
        BmsLibraryOptionsSnapshot options = optionsProvider()
            ?? throw new InvalidOperationException("BMS library options snapshot provider returned null.");
        if (options?.OperationModeLR2DB == true)
        {
            try
            {
                bool configRead = configProvider.TryGet(out LR2Config config);
                if (!configRead)
                {
                    // The previous facade route cleared the in-memory roots when LR2
                    // configuration could not be read.  Keep that failure contract
                    // explicit instead of retaining stale search targets.
                    SearchTargets = [];
                }
                else if (config != null)
                {
                    SearchTargets = config.GetBMSSearchDirectories() ?? [];
                }
            }
            catch
            {
                // Configuration extraction is part of the same read boundary;
                // malformed values have the established empty-root contract.
                SearchTargets = [];
            }
        }
        IEnumerable<string> requestedRoots = SearchTargets ?? [];
        HashSet<string> excludedRoots = CreateExcludedCustomOutputRoots(options);
        List<string> existingRoots = [.. requestedRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(LongPathFileSystem.DirectoryExists)];
        List<string> roots = [.. existingRoots
            .Where(path => !IsExcludedCustomOutputSearchRoot(path, excludedRoots))];
        return new Lr2SearchRootSnapshot(
            roots,
            requestedRootCount: requestedRoots.Count(),
            existingRootCount: existingRoots.Count,
            excludedCustomOutputRootCount: existingRoots.Count - roots.Count,
            configuredCustomOutputRootCount: excludedRoots.Count);
    }

    private static HashSet<string> CreateExcludedCustomOutputRoots(BmsLibraryOptionsSnapshot options)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options?.OperationModeLR2DB != true)
        {
            return excluded;
        }
        foreach (string path in options.LR2CustomFolderAdditionalOutputBaseDirs ?? [])
        {
            AddNormalized(excluded, path);
        }
        AddNormalized(excluded, options.LR2CustomFolderOutputBaseDirRootType);
        return excluded;
    }

    private static void AddNormalized(ISet<string> paths, string path)
    {
        string normalized = NormalizeDirectoryPath(path);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            paths.Add(normalized);
        }
    }

    private static bool IsExcludedCustomOutputSearchRoot(
        string directory,
        IReadOnlyCollection<string> excludedRoots)
    {
        string normalizedDirectory = NormalizeDirectoryPath(directory);
        return !string.IsNullOrWhiteSpace(normalizedDirectory)
            && (excludedRoots ?? []).Any(excludedRoot =>
                Lr2FolderPath.IsSameOrDescendant(normalizedDirectory, excludedRoot));
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Lr2FolderPath.NormalizeDirectoryPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>
/// Mutable lifecycle state shared by the composition boundary and the runtime port.
/// The state is not owned by the LR2 workflow and has no facade reference.
/// </summary>
internal sealed class Lr2SynchronizationRuntimeState
{
    private readonly Action<string> log;
    private int shutdownRequested;

    internal Lr2SynchronizationRuntimeState(Action<string> log)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal bool IsShutdownRequested => Volatile.Read(ref shutdownRequested) != 0;

    internal Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; set; }

    internal Action<string, string, long, bool, string> StartupBackgroundTaskReporter { get; set; }

    internal void RequestShutdown() => Interlocked.Exchange(ref shutdownRequested, 1);

    internal bool TrySkipForShutdown(string operation, string reason)
    {
        if (!IsShutdownRequested)
        {
            return false;
        }
        log((operation ?? "background_work")
            + " skipped reason=shutdown_requested requestReason="
            + (reason ?? "unknown"));
        return true;
    }

    internal void ReportStartupBackgroundTask(string name, string status, long elapsedMs, bool failed, string detail)
    {
        try
        {
            StartupBackgroundTaskReporter?.Invoke(
                name ?? "unknown",
                status ?? string.Empty,
                elapsedMs,
                failed,
                detail ?? string.Empty);
        }
        catch
        {
        }
    }
}

internal sealed class Lr2SynchronizationDataPort : ILr2SynchronizationDataPort
{
    private readonly BmsLibraryDbGateway dbGateway;
    private readonly CatalogMutationOwner catalogMutationOwner;
    private readonly ILr2ChartInfoCapability chartInfoCapability;
    private readonly CatalogOwnedCollectionOwner catalogOwnedCollectionOwner;
    private readonly EverythingNative everythingNative;
    private readonly Func<BmsLibraryOptionsSnapshot> currentOptionsSnapshot;
    private readonly Lr2SearchRootSnapshotOwner searchRootSnapshotOwner;
    private readonly Lr2ConfigSnapshotProvider configProvider;

    internal Lr2SynchronizationDataPort(
        BmsLibraryDbGateway dbGateway,
        CatalogMutationOwner catalogMutationOwner,
        ILr2ChartInfoCapability chartInfoCapability,
        CatalogOwnedCollectionOwner catalogOwnedCollectionOwner,
        EverythingNative everythingNative,
        Func<BmsLibraryOptionsSnapshot> currentOptionsSnapshot,
        Lr2SearchRootSnapshotOwner searchRootSnapshotOwner,
        Lr2ConfigSnapshotProvider configProvider)
    {
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.catalogMutationOwner = catalogMutationOwner ?? throw new ArgumentNullException(nameof(catalogMutationOwner));
        this.chartInfoCapability = chartInfoCapability ?? throw new ArgumentNullException(nameof(chartInfoCapability));
        this.catalogOwnedCollectionOwner = catalogOwnedCollectionOwner ?? throw new ArgumentNullException(nameof(catalogOwnedCollectionOwner));
        this.everythingNative = everythingNative ?? throw new ArgumentNullException(nameof(everythingNative));
        this.currentOptionsSnapshot = currentOptionsSnapshot ?? throw new ArgumentNullException(nameof(currentOptionsSnapshot));
        this.searchRootSnapshotOwner = searchRootSnapshotOwner ?? throw new ArgumentNullException(nameof(searchRootSnapshotOwner));
        this.configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public BmsLibraryOptionsSnapshot CurrentOptionsSnapshot => currentOptionsSnapshot()
        ?? throw new InvalidOperationException("BMS library options snapshot provider returned null.");

    public EverythingNative EverythingNative => everythingNative;

    public int OwnedChartCollectionVersion => catalogOwnedCollectionOwner.CollectionVersion;

    public IReadOnlyList<BMSFile> CaptureBmsFilesSnapshot() =>
        catalogMutationOwner.CaptureLr2SynchronizationBmsFilesSnapshot();

    public Lr2SongDbSyncInputRowSnapshot CaptureLr2SynchronizationInputRowSnapshot() =>
        catalogMutationOwner.CaptureLr2SynchronizationInputRowSnapshot();

    public StorageRowsVersionSnapshot CaptureStorageRowsVersionSnapshot() =>
        catalogMutationOwner.CaptureLr2SynchronizationStorageRowsVersionSnapshot();

    public ChartInfoOwnerVersionSnapshot CaptureChartInfoOwnerVersionSnapshot() =>
        chartInfoCapability.CaptureOwnerVersionSnapshot();

    public TimeSpan CurrentChartInfoParseTimeout => chartInfoCapability.CurrentParseTimeout;

    public Lr2SearchRootSnapshot CaptureBmsDirectories() => searchRootSnapshotOwner.Capture();

    public LR2Config CreateCurrentLr2ConfigOrNull() => configProvider.GetOrNull();

    public Lr2SongDbSyncAppManagedOutputScope CaptureLr2SongDbSyncAppManagedOutputScope(
        BmsLibraryOptionsSnapshot options) =>
        catalogMutationOwner.CaptureLr2SongDbSyncAppManagedOutputScope(options);

    public Lr2FolderExistingRowsSnapshot CaptureLr2FolderExistingRows(Lr2SongDbSyncRequest request) =>
        catalogMutationOwner.CaptureLr2FolderExistingRows(request);

    public Lr2SongDbSyncStatusSnapshot EvaluateLr2SongDbSyncStatus(
        bool enabled,
        string signature,
        DateTime nowUtc) =>
        catalogMutationOwner.EvaluateLr2SongDbSyncStatus(enabled, signature, nowUtc);

    public Lr2SongDbSyncStatusSnapshot ApplyLr2SongDbSyncStatusMutation(
        Lr2SongDbSyncStatusMutationRequest request) =>
        catalogMutationOwner.ApplyLr2SongDbSyncStatusMutation(request);

    public Lr2FolderFileDbSyncResult ApplyLr2FolderFileSync(Lr2FolderFileDbSyncRequest request) =>
        catalogMutationOwner.ApplyLr2FolderFileSync(request);

    public Lr2NormalFolderDbSyncResult ApplyLr2NormalFolderSync(Lr2NormalFolderDbSyncRequest request) =>
        catalogMutationOwner.ApplyLr2NormalFolderSync(request);

    public Lr2SongDbSyncResult ApplyLr2SongDbSync(Lr2SongDbSyncRequest request) =>
        catalogMutationOwner.ApplyLr2SongDbSync(request);

}

internal sealed class Lr2SynchronizationRuntimePort : ILr2SynchronizationRuntimePort
{
    private readonly Lr2SynchronizationRuntimeState state;
    private readonly IBmsLibraryDialogService dialogService;

    internal Lr2SynchronizationRuntimePort(
        Lr2SynchronizationRuntimeState state,
        IBmsLibraryDialogService dialogService)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public bool IsShutdownRequested => state.IsShutdownRequested;

    public Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler =>
        state.StartupBackgroundTaskScheduler;

    public UiDialogDefaultResult ShowOperationDialog(
        string messageBoxText,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult) =>
        dialogService.Show(messageBoxText, caption, button, icon, defaultResult);

    public bool TrySkipForShutdown(string operation, string reason) => state.TrySkipForShutdown(operation, reason);

    public void ReportStartupBackgroundTask(string name, string status, long elapsedMs, bool failed, string detail) =>
        state.ReportStartupBackgroundTask(name, status, elapsedMs, failed, detail);
}

internal sealed class Lr2SynchronizationProjectionPort : ILr2SynchronizationProjectionPort
{
    private readonly CatalogStorageRowsOwner storageRowsOwner;
    private readonly ILr2ChartInfoCapability chartInfoCapability;
    private readonly BmsLibraryDbGateway dbGateway;
    private readonly Action<string> log;

    internal Lr2SynchronizationProjectionPort(
        CatalogStorageRowsOwner storageRowsOwner,
        ILr2ChartInfoCapability chartInfoCapability,
        BmsLibraryDbGateway dbGateway,
        Action<string> log)
    {
        this.storageRowsOwner = storageRowsOwner ?? throw new ArgumentNullException(nameof(storageRowsOwner));
        this.chartInfoCapability = chartInfoCapability ?? throw new ArgumentNullException(nameof(chartInfoCapability));
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void EnsureLr2SongDbSyncChartInfoIndexHydrated(string reason) =>
        chartInfoCapability.EnsureHydratedForLr2(reason);

    public Dictionary<string, BMSFile> CreateLr2SongDbSyncCompatibilityProjectionIndex()
    {
        CatalogStorageRowsSnapshot snapshot = storageRowsOwner.CaptureSnapshot();
        return (snapshot.BmsRows ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path))
            .GroupBy(file => file.path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2SongDbSyncChartInfoResolverSnapshot() =>
        chartInfoCapability.CreateLr2ResolverSnapshot();

    public HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> failures =
                chartInfoCapability.LoadCurrentParseFailureMap(dbGateway, chartInfoCapability.CurrentParseTimeout);
            stopwatch.Stop();
            var result = new HashSet<string>(
                (failures?.Keys ?? Enumerable.Empty<string>()).Where(md5 => !string.IsNullOrWhiteSpace(md5)),
                StringComparer.OrdinalIgnoreCase);
            log("lr2_song_db_sync_chart_info_parse_failure_snapshot"
                + " reason=" + (reason ?? "unknown")
                + " count=" + result.Count
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            log("lr2_song_db_sync_chart_info_parse_failure_snapshot_failed"
                + " reason=" + (reason ?? "unknown")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " message=" + ex.Message);
            return [];
        }
    }

    public void UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<LR2SongDBExtended.chart_info> rows) =>
        chartInfoCapability.UpsertIndex(rows);

    public int ApplyLr2SongDbSyncCompatibilityProjection(
        IReadOnlyList<BMSFileMaintenanceInfo> maintenanceInfos,
        string reason,
        IReadOnlyDictionary<string, BMSFile> bmsByPath,
        bool logSummary,
        bool dispatchPresentation)
    {
        List<BMSFileMaintenanceInfo> infoList = [.. (maintenanceInfos ?? [])
            .Where(info => info != null && !string.IsNullOrWhiteSpace(info.path))];
        if (infoList.Count == 0)
        {
            if (logSummary)
            {
                log("lr2_song_db_sync_compatibility_projection skipped"
                    + " reason=" + (reason ?? "unknown")
                    + " input=0 applied=0");
            }
            return 0;
        }

        int applied = 0;
        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            CatalogStorageRowsSnapshot snapshot = storageRowsOwner.CaptureSnapshot();
            bmsByPath ??= (snapshot.BmsRows ?? [])
                .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path))
                .GroupBy(file => file.path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (BMSFileMaintenanceInfo sourceInfo in infoList)
            {
                if (!bmsByPath.TryGetValue(sourceInfo.path, out BMSFile file)
                    || file == null
                    || (!string.IsNullOrWhiteSpace(sourceInfo.hash)
                        && !string.Equals(sourceInfo.hash, file.hash, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                BMSFileMaintenanceInfo targetInfo = file.TryGetMaintenanceInfoWithoutCreating();
                if (targetInfo == null || !file.HasMaintenanceInfoHash(file.hash))
                {
                    targetInfo = new BMSFileMaintenanceInfo(file);
                }
                if (targetInfo.HasSameLr2CompatibilityFacts(sourceInfo))
                {
                    continue;
                }
                targetInfo.ApplyLr2CompatibilityFactsFrom(sourceInfo);
                file.SetMaintenanceInfo(targetInfo, suppressPropertyChanged: true, MaintenanceInfoOrigin.Calculated);
                applied++;
            }
        }

        if (logSummary)
        {
            log("lr2_song_db_sync_compatibility_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " input=" + infoList.Count
                + " applied=" + applied);
        }
        if (dispatchPresentation && applied > 0)
        {
            chartInfoCapability.PublishWarningPresentationChanged("lr2_song_db_sync_compatibility_projection");
        }
        return applied;
    }

    public void DispatchWarningPresentationChanged(string reason) =>
        chartInfoCapability.PublishWarningPresentationChanged(reason);
}
