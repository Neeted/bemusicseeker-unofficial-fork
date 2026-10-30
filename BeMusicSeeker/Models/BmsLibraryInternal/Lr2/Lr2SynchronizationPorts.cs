using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    IReadOnlyList<ChartFile> CaptureBmsFilesSnapshot();

    Lr2SongDbSyncInputRowSnapshot CaptureLr2SynchronizationInputRowSnapshot();

    TimeSpan CurrentChartInfoParseTimeout { get; }

    /// <summary>一操作で捕捉済みの登録と配置を使い、存在確認と管理領域除外を行います。</summary>
    Lr2SearchRootSnapshot CaptureBmsDirectories(BmsLibraryOptionsSnapshot options = null);

    /// <summary>XMLを共有せず、構成寿命の所有者から公開済みの読取り値を捕捉します。</summary>
    Lr2ConfigSnapshot CaptureLr2Config();

    Lr2SongDbSyncAppManagedOutputScope CaptureLr2SongDbSyncAppManagedOutputScope(
        BmsLibraryOptionsSnapshot options, bool throwOnFailure = false);

    Lr2FolderExistingRowsSnapshot CaptureLr2FolderExistingRows(Lr2SongDbSyncRequest request);

    Lr2SongDbSyncStatusSnapshot EvaluateLr2SongDbSyncStatus(
        bool enabled,
        string signature,
        DateTime nowUtc);

    Lr2SongDbSyncStatusSnapshot ApplyLr2SongDbSyncStatusMutation(
        Lr2SongDbSyncStatusMutationRequest request);

    Lr2FolderFileDbSyncResult ApplyLr2FolderFileSync(Lr2FolderFileDbSyncRequest request);

    /// <summary>通常親のprune依存を調べるため、限定範囲のcustom exact pathを捕捉します。</summary>
    IReadOnlyList<string> CaptureLr2NormalFolderCustomDependencyCandidates(
        IEnumerable<string> roots, IEnumerable<string> pruneScopes, IEnumerable<string> pruneExactDirectories);

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
    /// <summary>走査対象と存在確認前の登録を分けて保持し、更新前検査へ渡します。</summary>
    internal Lr2SearchRootSnapshot(
        IReadOnlyList<string> roots,
        int requestedRootCount,
        int existingRootCount,
        int excludedCustomOutputRootCount,
        int configuredCustomOutputRootCount,
        IReadOnlyList<string> requestedRoots = null)
    {
        Roots = roots ?? [];
        RequestedRoots = requestedRoots ?? Roots;
        RequestedRootCount = requestedRootCount;
        ExistingRootCount = existingRootCount;
        ExcludedCustomOutputRootCount = excludedCustomOutputRootCount;
        ConfiguredCustomOutputRootCount = configuredCustomOutputRootCount;
    }

    internal IReadOnlyList<string> Roots { get; }

    /// <summary>存在確認前に capture した登録 root です。</summary>
    internal IReadOnlyList<string> RequestedRoots { get; }

    internal int RequestedRootCount { get; }

    internal int ExistingRootCount { get; }

    internal int ExcludedCustomOutputRootCount { get; }

    internal int ConfiguredCustomOutputRootCount { get; }
}

/// <summary>LR2要求の実終端で保存する状態です。MarkCompletedは必須投影と所有token確認の成功後だけに使います。</summary>
internal enum Lr2SongDbSyncStatusMutationKind
{
    MarkIncomplete,
    MarkFailed,
    MarkCompleted
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

    Dictionary<string, ChartFile> CreateLr2SongDbSyncCompatibilityProjectionIndex();

    Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateLr2SongDbSyncChartInfoResolverSnapshot();

    HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason);

    void UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<BeMusicSeeker.Models.ChartDetails> rows);

    int ApplyLr2SongDbSyncCompatibilityProjection(
        IReadOnlyList<ResourceHealthMaintenanceSnapshot> maintenanceInfos,
        string reason,
        IReadOnlyDictionary<string, ChartFile> bmsByPath,
        bool logSummary,
        bool dispatchPresentation);

    void DispatchWarningPresentationChanged(string reason, IReadOnlyList<string> md5s = null);
}

/// <summary>
/// Narrow chart-info capability used by LR2 synchronization.  The capability
/// keeps only a weak reference to the chart-info owner so LR2 ports cannot
/// prolong the application facade lifetime through its workflow callbacks.
/// </summary>
internal interface ILr2ChartInfoCapability
{
    TimeSpan CurrentParseTimeout { get; }

    void EnsureHydratedForLr2(string reason);

    Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateLr2ResolverSnapshot();

    Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> LoadCurrentParseFailureMap(
        BmsLibraryDbGateway dbGateway,
        TimeSpan parseTimeout);

    void UpsertIndex(IReadOnlyList<BeMusicSeeker.Models.ChartDetails> rows);

    void PublishWarningPresentationChanged(string reason, IReadOnlyList<string> md5s = null);
}

internal sealed class Lr2ChartInfoCapability : ILr2ChartInfoCapability
{
    private readonly WeakReference<CatalogChartInfoOwner> ownerReference;

    internal Lr2ChartInfoCapability(CatalogChartInfoOwner owner)
    {
        ownerReference = new WeakReference<CatalogChartInfoOwner>(
            owner ?? throw new ArgumentNullException(nameof(owner)));
    }

    public TimeSpan CurrentParseTimeout => GetOwner().BuildService.CurrentParseTimeout;

    public void EnsureHydratedForLr2(string reason) =>
        GetOwner().EnsureHydratedForLr2(reason);

    public Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateLr2ResolverSnapshot() =>
        GetOwner().CreateLr2ResolverSnapshot();

    public Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> LoadCurrentParseFailureMap(
        BmsLibraryDbGateway dbGateway,
        TimeSpan parseTimeout) =>
        GetOwner().LoadCurrentParseFailureMap(dbGateway, parseTimeout);

    public void UpsertIndex(IReadOnlyList<BeMusicSeeker.Models.ChartDetails> rows) =>
        GetOwner().UpsertIndex(rows, "lr2_song_db_sync_inline_chart_info", dispatchPresentation: false);

    public void PublishWarningPresentationChanged(string reason, IReadOnlyList<string> md5s = null) =>
        GetOwner().PublishWarningPresentationChanged(reason, md5s: md5s);

    private CatalogChartInfoOwner GetOwner()
    {
        if (ownerReference.TryGetTarget(out CatalogChartInfoOwner owner))
        {
            return owner;
        }
        throw new InvalidOperationException("Chart-info capability owner is no longer available.");
    }
}

/// <summary>共有XML所有者の公開入力または独立構成の確定一覧から、操作ごとの走査対象を作ります。</summary>
internal sealed class Lr2SearchRootSnapshotOwner
{
    private readonly Lr2ConfigOwner configOwner;
    private readonly Func<BmsLibraryOptionsSnapshot> optionsProvider;

    /// <summary>採用済みXMLの読込みを繰り返さず、同じ所有者と設定捕捉元を接続します。</summary>
    internal Lr2SearchRootSnapshotOwner(
        Lr2ConfigOwner configOwner,
        Func<BmsLibraryOptionsSnapshot> optionsProvider)
    {
        this.configOwner = configOwner;
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
    }

    /// <summary>走査の登録と出力配置を一度捕捉し、ファイルの現在の利用可能性だけを検査します。</summary>
    internal Lr2SearchRootSnapshot Capture(BmsLibraryOptionsSnapshot options = null)
    {
        options ??= optionsProvider()
            ?? throw new InvalidOperationException("BMS library options snapshot provider returned null.");
        Lr2ConfigSnapshot captured = options.OperationModeLR2DB ? options.Lr2Input ?? configOwner?.Capture() ?? throw new InvalidOperationException("LR2 root registration owner is unavailable.") : null;
        IEnumerable<string> requestedRoots = captured?.RegisteredRoots ?? options.StandaloneBmsRootPaths;
        options = WithCapturedPlacement(options, captured);
        HashSet<string> excludedRoots = CreateExcludedCustomOutputRoots(options);
        List<string> existingRoots = captured != null ? [.. captured.CaptureExistingSearchRoots()]
            : [.. requestedRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Where(LongPathFileSystem.DirectoryExists)];
        List<string> roots = [.. existingRoots
            .Where(path => !IsExcludedCustomOutputSearchRoot(path, excludedRoots))];
        return new Lr2SearchRootSnapshot(
            roots,
            requestedRootCount: requestedRoots.Count(),
            existingRootCount: existingRoots.Count,
            excludedCustomOutputRootCount: existingRoots.Count - roots.Count,
            configuredCustomOutputRootCount: excludedRoots.Count,
            requestedRoots: requestedRoots.ToArray());
    }

    /// <summary>
    /// 更新操作用に存在確認前の設定 root を capture します。
    /// <see cref="Lr2SearchRootSnapshot.Roots"/> は scanner の既存の
    /// managed output 除外を維持し、<see cref="Lr2SearchRootSnapshot.RequestedRoots"/>
    /// は preflight 用に全登録値を保持します。
    /// </summary>
    internal Lr2SearchRootSnapshot CaptureForUpdate(BmsLibraryOptionsSnapshot options)
    {
        options ??= optionsProvider()
            ?? throw new InvalidOperationException("BMS library options snapshot provider returned null.");
        Lr2ConfigSnapshot captured = options.OperationModeLR2DB ? options.Lr2Input ?? configOwner?.Capture() ?? throw new InvalidOperationException("LR2 root registration owner is unavailable.") : null;
        options = WithCapturedPlacement(options, captured);
        List<string> requestedRoots = [.. (captured?.RegisteredRoots ?? options.StandaloneBmsRootPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim())];
        HashSet<string> excludedRoots = CreateExcludedCustomOutputRoots(options);
        List<string> scanRoots = [.. requestedRoots
            .Where(path => !IsExcludedCustomOutputSearchRoot(path, excludedRoots))];
        return new Lr2SearchRootSnapshot(
            scanRoots,
            requestedRootCount: requestedRoots.Count,
            existingRootCount: requestedRoots.Count,
            excludedCustomOutputRootCount: requestedRoots.Count - scanRoots.Count,
            configuredCustomOutputRootCount: excludedRoots.Count,
            requestedRoots: requestedRoots);
    }

    private static BmsLibraryOptionsSnapshot WithCapturedPlacement(BmsLibraryOptionsSnapshot options, Lr2ConfigSnapshot captured)
    {
        if (captured?.Placement == null) { return options; }
        return options.WithLr2Placement(captured.Placement);
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
    private readonly Lr2ConfigOwner configOwner;

    internal Lr2SynchronizationDataPort(
        BmsLibraryDbGateway dbGateway,
        CatalogMutationOwner catalogMutationOwner,
        ILr2ChartInfoCapability chartInfoCapability,
        CatalogOwnedCollectionOwner catalogOwnedCollectionOwner,
        EverythingNative everythingNative,
        Func<BmsLibraryOptionsSnapshot> currentOptionsSnapshot,
        Lr2SearchRootSnapshotOwner searchRootSnapshotOwner,
        Lr2ConfigOwner configOwner)
    {
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.catalogMutationOwner = catalogMutationOwner ?? throw new ArgumentNullException(nameof(catalogMutationOwner));
        this.chartInfoCapability = chartInfoCapability ?? throw new ArgumentNullException(nameof(chartInfoCapability));
        this.catalogOwnedCollectionOwner = catalogOwnedCollectionOwner ?? throw new ArgumentNullException(nameof(catalogOwnedCollectionOwner));
        this.everythingNative = everythingNative ?? throw new ArgumentNullException(nameof(everythingNative));
        this.currentOptionsSnapshot = currentOptionsSnapshot ?? throw new ArgumentNullException(nameof(currentOptionsSnapshot));
        this.searchRootSnapshotOwner = searchRootSnapshotOwner ?? throw new ArgumentNullException(nameof(searchRootSnapshotOwner));
        this.configOwner = configOwner;
    }

    public BmsLibraryOptionsSnapshot CurrentOptionsSnapshot => currentOptionsSnapshot()
        ?? throw new InvalidOperationException("BMS library options snapshot provider returned null.");

    public EverythingNative EverythingNative => everythingNative;

    public IReadOnlyList<ChartFile> CaptureBmsFilesSnapshot() =>
        catalogMutationOwner.CaptureLr2SynchronizationBmsFilesSnapshot();

    public Lr2SongDbSyncInputRowSnapshot CaptureLr2SynchronizationInputRowSnapshot() =>
        catalogMutationOwner.CaptureLr2SynchronizationInputRowSnapshot();

    public TimeSpan CurrentChartInfoParseTimeout => chartInfoCapability.CurrentParseTimeout;

    /// <inheritdoc />
    public Lr2SearchRootSnapshot CaptureBmsDirectories(BmsLibraryOptionsSnapshot options = null) => searchRootSnapshotOwner.Capture(options);

    /// <inheritdoc />
    public Lr2ConfigSnapshot CaptureLr2Config() => configOwner?.Capture();

    public Lr2SongDbSyncAppManagedOutputScope CaptureLr2SongDbSyncAppManagedOutputScope(
        BmsLibraryOptionsSnapshot options, bool throwOnFailure = false) =>
        catalogMutationOwner.CaptureLr2SongDbSyncAppManagedOutputScope(options, throwOnFailure);

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

    /// <summary>DB読取りを所有する窓口へ、通常親のcustom依存候補捕捉を委譲します。</summary>
    public IReadOnlyList<string> CaptureLr2NormalFolderCustomDependencyCandidates(
        IEnumerable<string> roots, IEnumerable<string> pruneScopes, IEnumerable<string> pruneExactDirectories) =>
        catalogMutationOwner.CaptureLr2NormalFolderCustomDependencyCandidates(roots, pruneScopes, pruneExactDirectories);

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
    private readonly CatalogOwnedCollectionOwner storageRowsOwner;
    private readonly ILr2ChartInfoCapability chartInfoCapability;
    private readonly BmsLibraryDbGateway dbGateway;
    private readonly Action<string> log;

    internal Lr2SynchronizationProjectionPort(
        CatalogOwnedCollectionOwner storageRowsOwner,
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

    public Dictionary<string, ChartFile> CreateLr2SongDbSyncCompatibilityProjectionIndex()
    {
        CatalogChartCollectionSnapshot snapshot = storageRowsOwner.CaptureSnapshot();
        return (snapshot.BmsRows ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.Path))
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateLr2SongDbSyncChartInfoResolverSnapshot() =>
        chartInfoCapability.CreateLr2ResolverSnapshot();

    public HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> failures =
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

    public void UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<BeMusicSeeker.Models.ChartDetails> rows) =>
        chartInfoCapability.UpsertIndex(rows);

    public int ApplyLr2SongDbSyncCompatibilityProjection(
        IReadOnlyList<ResourceHealthMaintenanceSnapshot> maintenanceInfos,
        string reason,
        IReadOnlyDictionary<string, ChartFile> bmsByPath,
        bool logSummary,
        bool dispatchPresentation)
    {
        List<ResourceHealthMaintenanceSnapshot> infoList = [.. (maintenanceInfos ?? [])
            .Where(info => info != null && !string.IsNullOrWhiteSpace(info.Path))];
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
            CatalogChartCollectionSnapshot snapshot = storageRowsOwner.CaptureSnapshot();
            bmsByPath ??= (snapshot.BmsRows ?? [])
                .Where(file => file != null && !string.IsNullOrWhiteSpace(file.Path))
                .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            storageRowsOwner.Commit(state =>
            {
                foreach (ResourceHealthMaintenanceSnapshot sourceInfo in infoList)
                {
                    if (!bmsByPath.TryGetValue(sourceInfo.Path, out ChartFile file)
                        || file == null
                        || (!string.IsNullOrWhiteSpace(sourceInfo.Hash)
                            && !string.Equals(sourceInfo.Hash, file.Md5, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    file = state.ResolveCurrentChart(LibraryChartRef.FromChartFile(file));
                    if (file == null)
                    {
                        continue;
                    }

                    ResourceHealthMaintenanceSnapshot targetInfo = file.ResourceHealthMaintenanceSnapshot;
                    targetInfo ??= new ResourceHealthMaintenanceSnapshot { Path = file.Path, Hash = file.Md5 };
                    ResourceHealthMaintenanceSnapshot next = targetInfo with
                    {
                        Lr2WarningFlags = sourceInfo.Lr2WarningFlags,
                        Lr2ResourceMaxRelativeCp932Bytes = sourceInfo.Lr2ResourceMaxRelativeCp932Bytes,
                        Lr2ResourceHasParentTraversal = sourceInfo.Lr2ResourceHasParentTraversal,
                        Origin = MaintenanceInfoOrigin.Calculated
                    };
                    if (next == targetInfo)
                    {
                        continue;
                    }

                    if (state.ApplyCurrentChartValue(ChartFileProjection.WithMaintenance(file, next)) == CatalogCurrentValueApplyResult.Changed)
                    {
                        applied++;
                    }
                }
            });
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
            chartInfoCapability.PublishWarningPresentationChanged("lr2_song_db_sync_compatibility_projection",
                [.. infoList.Select(value => value.Hash).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)]);
        }
        return applied;
    }

    public void DispatchWarningPresentationChanged(string reason, IReadOnlyList<string> md5s = null) =>
        chartInfoCapability.PublishWarningPresentationChanged(reason, md5s);
}
