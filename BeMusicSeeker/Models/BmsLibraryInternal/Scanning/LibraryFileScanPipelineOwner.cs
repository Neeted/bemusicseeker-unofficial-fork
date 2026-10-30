using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartScanPrefetchInfo
{
    internal ChartScanExecutionResult ScanResult { get; set; }

    internal long ElapsedMs { get; set; }
}

/// <summary>
/// 譜面走査の取得・差分・解析・保存・終端反映を一つの要求で接続します。
/// </summary>
internal sealed class LibraryFileScanPipelineOwner
{
    private sealed class ActiveFileScan
    {
        internal long Generation { get; init; }

        internal BmsLibraryOptionsSnapshot Options { get; init; }

        internal BmsLibraryOptionsSnapshot RegistrationInput { get; init; }

        internal List<string> RootDirectories { get; init; }

        internal LibraryDirectoryPreflightRequest DirectoryPreflightRequest { get; init; }

        internal string Reason { get; init; }

        internal Action<LibraryScanWarning> WarningObserver { get; init; }

        internal Task<ChartScanPrefetchInfo> ChartScanPrefetchTask { get; set; }

        internal bool Applying { get; set; }
    }

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly CatalogOwnedCollectionOwner catalogOwnedCollectionOwner;


    private readonly IBmsLibraryDialogService dialogService;

    private readonly Func<bool> everythingScanLoggingEnabled;

    private readonly Action<BMSLibrary.LibraryInitializationProgressStage, string, int, int, string, bool> reportLibraryInitializationProgress;

    private readonly Action completeLibraryFileEnumerationProgress;

    private readonly Action completeLibraryFileDiffProgress;

    private readonly Action<string> logInstallPerformance;

    private readonly Action<string> logInstallPerformanceWarn;

    private readonly Action<string> logEverythingScan;

    private readonly Action<string, string> logStartupMemoryCheckpoint;

    private readonly Func<Exception, string> getDisplayedExceptionMessage;

    private readonly Action markCatalogPathConvergenceCompleted;

    private readonly Action<string> queueEverythingFallbackWarning;

    private readonly Action<string> queueFileScanSkippedIncompleteWarning;

    private readonly Action<string> queueEmptyScanWithExistingDbWarning;

    private readonly BMSLibrary.Lr2SynchronizationOwner lr2Synchronization;

    private readonly CatalogMutationOwner catalogMutationOwner;

    private readonly CatalogChartInfoOwner catalogChartInfoOwner;

    private readonly ResourceHealthIndexOwner resourceHealthOwner;

    private readonly Func<FileScanCatalogReplacementEvent, Action> publishCatalogReplacement;

    private readonly Func<FileScanCatalogResidualEvent, Action> publishCatalogResidual;

    private readonly Lr2FolderFileDiffOwner lr2FolderFileDiffOwner;

    private readonly BmsLibraryInitializationService initializationService;

    private readonly EverythingNative everythingNative;

    private readonly IChartFileScanner chartFileScanner;

    private readonly IRootFileEnumerator rootFileEnumerator;

    private readonly LibraryDirectoryPreflightService directoryPreflightService;

    private readonly BmsRootRegistrationStore bmsRootRegistrations;

    private readonly object fileScanGate = new();

    private ActiveFileScan activeFileScan;

    private long fileScanGeneration;

    /// <summary>
    /// 一つの走査要求から保存・投影・直接LR2入力までを接続し、開始した読取りを全終端で回収します。
    /// 列挙依存は構成時に捕捉し、通常構成ではEverythingと既存の限定fallbackを使います。
    /// </summary>
    /// <param name="chartFileScanner">構成時に捕捉した譜面列挙窓口。nullは通常のEverything走査です。</param>
    /// <param name="rootFileEnumerator">構成時に捕捉したroot列挙窓口。nullは通常のLR2列挙です。</param>
    /// <param name="directoryPreflightService">更新前検査を共有する service。null の場合は通常構成を作成します。</param>
    /// <param name="markCatalogPathConvergenceCompleted">正本の差分と目録差替え完了後に、このモデルのpath収束を公開します。</param>
    internal LibraryFileScanPipelineOwner(
        BmsLibraryDbGateway dbGateway,
        CatalogOwnedCollectionOwner catalogOwnedCollectionOwner,
        IBmsLibraryDialogService dialogService,
        Func<bool> everythingScanLoggingEnabled,
        Action<BMSLibrary.LibraryInitializationProgressStage, string, int, int, string, bool> reportLibraryInitializationProgress,
        Action completeLibraryFileEnumerationProgress,
        Action completeLibraryFileDiffProgress,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        Action<string> logEverythingScan,
        Action<string, string> logStartupMemoryCheckpoint,
        Func<Exception, string> getDisplayedExceptionMessage,
        Action markCatalogPathConvergenceCompleted,
        Action<string> queueEverythingFallbackWarning,
        Action<string> queueFileScanSkippedIncompleteWarning,
        Action<string> queueEmptyScanWithExistingDbWarning,
        BMSLibrary.Lr2SynchronizationOwner lr2Synchronization,
        CatalogMutationOwner catalogMutationOwner,
        CatalogChartInfoOwner catalogChartInfoOwner,
        ResourceHealthIndexOwner resourceHealthOwner,
        Func<FileScanCatalogReplacementEvent, Action> publishCatalogReplacement,
        Func<FileScanCatalogResidualEvent, Action> publishCatalogResidual,
        BmsLibraryInitializationService initializationService,
        EverythingNative everythingNative,
        IChartFileScanner chartFileScanner = null,
        IRootFileEnumerator rootFileEnumerator = null,
        LibraryDirectoryPreflightService directoryPreflightService = null, BmsRootRegistrationStore bmsRootRegistrations = null)
    {
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.catalogOwnedCollectionOwner = catalogOwnedCollectionOwner ?? throw new ArgumentNullException(nameof(catalogOwnedCollectionOwner));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.everythingScanLoggingEnabled = everythingScanLoggingEnabled ?? throw new ArgumentNullException(nameof(everythingScanLoggingEnabled));
        this.reportLibraryInitializationProgress = reportLibraryInitializationProgress ?? throw new ArgumentNullException(nameof(reportLibraryInitializationProgress));
        this.completeLibraryFileEnumerationProgress = completeLibraryFileEnumerationProgress ?? throw new ArgumentNullException(nameof(completeLibraryFileEnumerationProgress));
        this.completeLibraryFileDiffProgress = completeLibraryFileDiffProgress ?? throw new ArgumentNullException(nameof(completeLibraryFileDiffProgress));
        this.logInstallPerformance = logInstallPerformance ?? throw new ArgumentNullException(nameof(logInstallPerformance));
        this.logInstallPerformanceWarn = logInstallPerformanceWarn ?? throw new ArgumentNullException(nameof(logInstallPerformanceWarn));
        this.logEverythingScan = logEverythingScan ?? throw new ArgumentNullException(nameof(logEverythingScan));
        this.logStartupMemoryCheckpoint = logStartupMemoryCheckpoint ?? throw new ArgumentNullException(nameof(logStartupMemoryCheckpoint));
        this.getDisplayedExceptionMessage = getDisplayedExceptionMessage ?? throw new ArgumentNullException(nameof(getDisplayedExceptionMessage));
        this.markCatalogPathConvergenceCompleted = markCatalogPathConvergenceCompleted ?? throw new ArgumentNullException(nameof(markCatalogPathConvergenceCompleted));
        this.queueEverythingFallbackWarning = queueEverythingFallbackWarning ?? throw new ArgumentNullException(nameof(queueEverythingFallbackWarning));
        this.queueFileScanSkippedIncompleteWarning = queueFileScanSkippedIncompleteWarning ?? throw new ArgumentNullException(nameof(queueFileScanSkippedIncompleteWarning));
        this.queueEmptyScanWithExistingDbWarning = queueEmptyScanWithExistingDbWarning ?? throw new ArgumentNullException(nameof(queueEmptyScanWithExistingDbWarning));
        this.lr2Synchronization = lr2Synchronization ?? throw new ArgumentNullException(nameof(lr2Synchronization));
        this.catalogMutationOwner = catalogMutationOwner ?? throw new ArgumentNullException(nameof(catalogMutationOwner));
        this.catalogChartInfoOwner = catalogChartInfoOwner ?? throw new ArgumentNullException(nameof(catalogChartInfoOwner));
        this.resourceHealthOwner = resourceHealthOwner ?? throw new ArgumentNullException(nameof(resourceHealthOwner));
        this.publishCatalogReplacement = publishCatalogReplacement ?? throw new ArgumentNullException(nameof(publishCatalogReplacement));
        this.publishCatalogResidual = publishCatalogResidual ?? throw new ArgumentNullException(nameof(publishCatalogResidual));
        lr2FolderFileDiffOwner = new Lr2FolderFileDiffOwner(
            logInstallPerformance,
            logInstallPerformanceWarn,
            getDisplayedExceptionMessage,
            logEverythingScan,
            lr2Synchronization,
            everythingNative,
            rootFileEnumerator);
        this.initializationService = initializationService ?? throw new ArgumentNullException(nameof(initializationService));
        this.everythingNative = everythingNative ?? throw new ArgumentNullException(nameof(everythingNative));
        this.chartFileScanner = chartFileScanner;
        this.rootFileEnumerator = rootFileEnumerator;
        this.directoryPreflightService = directoryPreflightService ?? new LibraryDirectoryPreflightService();
        this.bmsRootRegistrations = bmsRootRegistrations;
    }

    /// <summary>
    /// scanner に渡す root と、同じ操作で後段再検査する immutable request を登録します。
    /// </summary>
    /// <param name="directoryPreflightRequest">初回検査と後段再検査で共有する request。</param>
    internal long BeginFileScanRequest(
        BmsLibraryOptionsSnapshot options,
        List<string> rootDirectories,
        string reason,
        Action<string> reportScanner = null,
        LibraryDirectoryPreflightRequest directoryPreflightRequest = null, Action<LibraryScanWarning> warningObserver = null)
    {
        BmsLibraryOptionsSnapshot registrationInput = options;
        rootDirectories ??= [];
        var confirmedRoots = (rootDirectories ?? []).Select(LongPathFileSystem.ConfirmDirectoryPathSpelling).ToList();
        IReadOnlyList<string> ReplaceRoots(IEnumerable<string> roots) => roots.Select(path =>
            BmsLibraryOptionsSnapshot.ApplyConfirmedRootPrefixes(path, rootDirectories, confirmedRoots)).ToArray();
        options = options.WithConfirmedRootPrefixes(rootDirectories, confirmedRoots);
        if (directoryPreflightRequest != null)
        {
            directoryPreflightRequest = new LibraryDirectoryPreflightRequest(ReplaceRoots(directoryPreflightRequest.BmsRootDirectories),
                ReplaceRoots(directoryPreflightRequest.ScanRootDirectories), directoryPreflightRequest.OutputBaseTargets);
        }
        ActiveFileScan scan;
        lock (fileScanGate)
        {
            if (activeFileScan != null)
            {
                throw new InvalidOperationException(
                    "A library file scan is already active. Complete or abort it before starting another scan.");
            }

            scan = new ActiveFileScan
            {
                Generation = checked(++fileScanGeneration),
                Options = options,
                RegistrationInput = registrationInput,
                RootDirectories = confirmedRoots,
                Reason = reason ?? string.Empty,
                DirectoryPreflightRequest = directoryPreflightRequest,
                WarningObserver = warningObserver
            };
            activeFileScan = scan;
        }

        if (scan.RootDirectories.Count == 0)
        {
            return scan.Generation;
        }

        try
        {
            scan.ChartScanPrefetchTask = Task.Run(() =>
            {
                var stopwatchPrefetch = Stopwatch.StartNew();
                ChartScanExecutionResult scanResult = ExecuteChartScanWithManagedFallback(
                    scan.RootDirectories,
                    BMSLibrary.ShouldIncludeLr2TextSurface(scan.Options),
                    BMSLibrary.ShouldIncludeLr2DirectorySurface(scan.Options),
                    reportScanner,
                    () => IsActiveGeneration(scan.Generation),
                    reason => QueueFallbackWarningForGeneration(scan.Generation, reason));
                stopwatchPrefetch.Stop();
                return new ChartScanPrefetchInfo
                {
                    ScanResult = scanResult,
                    ElapsedMs = stopwatchPrefetch.ElapsedMilliseconds
                };
            });
            return scan.Generation;
        }
        catch
        {
            lock (fileScanGate)
            {
                if (ReferenceEquals(activeFileScan, scan))
                {
                    activeFileScan = null;
                }
            }
            throw;
        }
    }

    /// <summary>
    /// 準備済み走査の完全性を確認し、全開始子Taskとcleanupを回収して今回の変更不能LR2入力を返します。
    /// 通常の取得失敗とBMS root再検査失敗は警告して非反映にし、出力base・保存・必須反映の失敗は伝播します。
    /// 呼出元の生存する共通権限を借用し、元失敗をcleanupの副次障害で隠しません。
    /// </summary>
    internal Lr2FolderFileDiffPreparationResult ApplyActiveFileScan(
        long generation,
        bool trackLibraryFileCheckProgress,
        InstallDestinationCleanupSnapshot installDestinationCleanupSnapshot,
        Action<Action> postLeaseEffectObserver = null, LibraryFileMutationCapability capability = null,
        Lr2FolderTableSnapshot folderTableSnapshot = null, DateTime? generatedAtUtc = null)
    {
        if (installDestinationCleanupSnapshot == null)
        {
            throw new ArgumentNullException(nameof(installDestinationCleanupSnapshot));
        }
        ActiveFileScan scan = GetActiveFileScan(generation);
        lock (fileScanGate)
        {
            if (!ReferenceEquals(activeFileScan, scan))
            {
                throw new InvalidOperationException("The active library file scan is no longer available.");
            }
            if (scan.Applying)
            {
                throw new InvalidOperationException("The active library file scan is already being applied.");
            }
            scan.Applying = true;
        }

        try
        {
            ChartScanPrefetchInfo chartScanPrefetchInfo = ResolveChartScanPrefetch(scan);
            if (scan.DirectoryPreflightRequest != null)
            {
                LibraryDirectoryPreflightRequest request = scan.DirectoryPreflightRequest;
                // rootの消失でも必須の出力base検査を省略しないよう、用途別に同じ検査を行います。
                directoryPreflightService.EnsureAvailable(
                    new LibraryDirectoryPreflightRequest([], request.ScanRootDirectories, request.OutputBaseTargets),
                    probeOutputBases: false);
                try
                {
                    directoryPreflightService.EnsureAvailable(
                        new LibraryDirectoryPreflightRequest(request.BmsRootDirectories, request.ScanRootDirectories, []),
                        probeOutputBases: false);
                }
                catch (LibraryDirectoryPreflightException failure) when (
                    failure.Use == LibraryDirectoryPreflightUse.BmsRoot && IsOrdinaryScanFailure(failure))
                {
                    chartScanPrefetchInfo = new ChartScanPrefetchInfo
                    {
                        ScanResult = CreateFailedChartScanResult("bms_root_recheck_failed:" + failure.Message)
                    };
                }
            }
            List<string> confirmedRoots = ConfirmScanRootSpelling(scan.RootDirectories, chartScanPrefetchInfo?.ScanResult);
            BmsLibraryOptionsSnapshot confirmedOptions = scan.Options.WithConfirmedRootPrefixes(scan.RootDirectories, confirmedRoots);
            bool registrationAdopted = false;
            Lr2FolderFileDiffPreparationResult result = ApplyFileScanDiff(
                confirmedOptions,
                confirmedRoots,
                chartScanPrefetchInfo,
                folderTableSnapshot,
                trackLibraryFileCheckProgress,
                scan.Reason,
                installDestinationCleanupSnapshot,
                postLeaseEffectObserver, capability, scan.WarningObserver,
                () =>
                {
                    bmsRootRegistrations?.SaveRootChanges(scan.RegistrationInput, confirmedOptions);
                    registrationAdopted = true;
                }, generatedAtUtc, scan.RegistrationInput.Lr2Input?.RegisteredRoots);
            if (!registrationAdopted)
            {
                return result.WithFileCheckResult(result.FileCheckResult,
                    new LibraryFileInitializationResult(options: scan.RegistrationInput));
            }
            string previousBase = scan.RegistrationInput.LR2CustomFolderOutputBaseDir;
            string confirmedBase = confirmedOptions.LR2CustomFolderOutputBaseDir;
            return confirmedOptions.OperationModeLR2DB
                && !string.Equals(previousBase, confirmedBase, StringComparison.Ordinal)
                && CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(previousBase, confirmedBase)
                ? result.WithFileCheckResult(result.FileCheckResult, result.SynchronizationInput with
                { NormalOutputBasePathChange = (previousBase, confirmedBase) })
                : result;
        }
        finally
        {
            JoinChartScanPrefetch(scan);
            CompleteFileScan(scan);
        }
    }

    /// <summary>完全な今回の返却面が示す同所prefixを採用します。入力由来のFast root metadataと先行DB行は根拠にしません。</summary>
    internal static List<string> ConfirmScanRootSpelling(IReadOnlyList<string> roots, ChartScanExecutionResult scan)
    {
        if (!IsAuthoritativeChartScan(scan)) { return roots.ToList(); }
        ChartScanResult surface = scan.Result;
        bool native = scan.ScanSource == ChartScanSource.Everything;
        IEnumerable<string> observed = native ? surface.ChartDirectories : [];
        // Fastのroot自身のEntryは入力から作られます。実際に列挙した子要素だけを補助の根拠にします。
        observed = observed.Concat(surface.DirectoryEntriesByPath.Values.Select(entry => entry.Path)
            .Where(path => (native && scan.DirectoryQueryHitCount > 0)
                || !roots.Any(root => CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(path, root))))
            .Concat(surface.ChartFilePaths).Concat(surface.TextFileEntriesByPath.Values.Select(entry => entry.Path));
        var confirmed = new List<string>(roots.Count);
        foreach (string root in roots)
        {
            int length = root.TrimEnd('\\', '/').Length;
            string fact = observed.FirstOrDefault(path => path != null && path.Length >= length
                && path.AsSpan(0, length).Equals(root.AsSpan(0, length), StringComparison.OrdinalIgnoreCase)
                && (path.Length == length || path[length] == '\\' || path[length] == '/'));
            confirmed.Add(fact == null ? root : string.Concat(fact.AsSpan(0, length), root.AsSpan(length)));
        }
        return confirmed;
    }

    internal void AbortActiveFileScan(long generation)
    {
        ActiveFileScan scan;
        lock (fileScanGate)
        {
            scan = activeFileScan;
            if (scan == null || scan.Generation != generation || scan.Applying)
            {
                return;
            }
            activeFileScan = null;
        }
        JoinChartScanPrefetch(scan);
    }

    /// <summary>
    /// 準備済みLR2 folder/file要求を、呼出元の同じ生存権限で確定します。
    /// 通常走査と目録処理のために権限を保存したり別受付を取得したりしません。
    /// </summary>
    /// <param name="progressReporter">確定処理の任意の進捗通知先。</param>
    internal void ApplyPreparedLr2FolderFileDiffForFileMutation(
        BmsLibraryOptionsSnapshot options,
        string reason,
        Lr2FolderFileDiffPreparationResult preparation,
        LibraryFileMutationCapability mutationCapability,
        Action<int, int, string> progressReporter = null)
    {
        lr2FolderFileDiffOwner.ApplyPrepared(
            preparation?.SynchronizationInput.Options ?? options,
            preparation,
            reason,
            mutationCapability,
            progressReporter);
    }

    /// <summary>不成立・中断でも開始済みchart走査Taskを回収し、既存の非致命的扱いと元失敗を保持します。</summary>
    private static void JoinChartScanPrefetch(ActiveFileScan scan)
    {
        if (scan.ChartScanPrefetchTask == null) { return; }
        try { scan.ChartScanPrefetchTask.GetAwaiter().GetResult(); }
        catch (Exception failure) { NLogWrapper.FileLogger?.Warn(failure, "library_scan_child_cleanup_failed"); }
    }

    private void CompleteFileScan(ActiveFileScan scan)
    {
        lock (fileScanGate)
        {
            if (ReferenceEquals(activeFileScan, scan))
            {
                activeFileScan = null;
            }
        }
    }

    private ActiveFileScan GetActiveFileScan(long generation)
    {
        lock (fileScanGate)
        {
            if (activeFileScan == null)
            {
                throw new InvalidOperationException("No active library file scan exists.");
            }
            if (activeFileScan.Generation != generation)
            {
                throw new InvalidOperationException("The active library file scan generation is stale.");
            }
            return activeFileScan;
        }
    }

    private bool IsActiveGeneration(long generation)
    {
        lock (fileScanGate)
        {
            return activeFileScan?.Generation == generation;
        }
    }

    private void QueueFallbackWarningForGeneration(long generation, string reason)
    {
        Action<LibraryScanWarning> observer;
        lock (fileScanGate)
        {
            if (activeFileScan?.Generation != generation) { return; }
            observer = activeFileScan.WarningObserver;
        }
        if (observer == null) { queueEverythingFallbackWarning(reason); }
        else { observer(new(LibraryScanWarningKind.EverythingFallback, reason)); }
    }

    private ChartScanPrefetchInfo ResolveChartScanPrefetch(ActiveFileScan scan)
    {
        if (scan?.ChartScanPrefetchTask == null)
        {
            return null;
        }

        try
        {
            return scan.ChartScanPrefetchTask.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsOrdinaryScanFailure(ex))
        {
            logEverythingScan("chart_scan_prefetch failed message=" + ex.Message);
            return new ChartScanPrefetchInfo { ScanResult = CreateFailedChartScanResult("chart_scan_prefetch_failed:" + ex.Message) };
        }
    }

    /// <summary>走査の通常失敗を不完全結果として返します。Everything不能時の完全なmanaged代替結果は採用します。</summary>
    internal ChartScanExecutionResult ExecuteChartScanWithManagedFallback(
        List<string> bmsDirectories,
        bool includeTextSurface,
        bool includeDirectorySurface,
        Action<string> reportScanner = null)
    {
        return ExecuteChartScanWithManagedFallback(
            bmsDirectories,
            includeTextSurface,
            includeDirectorySurface,
            reportScanner,
            () => true,
            reason => queueEverythingFallbackWarning(reason));
    }

    private ChartScanExecutionResult ExecuteChartScanWithManagedFallback(
        List<string> bmsDirectories,
        bool includeTextSurface,
        bool includeDirectorySurface,
        Action<string> reportScanner,
        Func<bool> isActive,
        Action<string> queueFallbackWarning)
    {
        IChartFileScanner scanner = chartFileScanner ?? new EverythingFileScanner(everythingNative);
        void ReportScanner(string label)
        {
            if (isActive())
            {
                reportScanner?.Invoke(label);
            }
        }

        void LogEverythingScan(string message)
        {
            if (isActive())
            {
                logEverythingScan(message);
            }
        }

        void QueueEverythingFallbackWarning(string reason)
        {
            queueFallbackWarning(reason);
        }

        ReportScanner("Native");
        bool verboseLog = everythingScanLoggingEnabled();
        ChartScanExecutionResult scanResult;
        try
        {
            scanResult = scanner.Scan(
                bmsDirectories,
                ChartDirectoryScanBuilder.ChartExtensions,
                verboseLog,
                includeTextSurface,
                includeDirectorySurface);
        }
        catch (Exception failure) when (IsOrdinaryScanFailure(failure))
        {
            return CreateFailedChartScanResult("chart_file_scan_failed:" + failure.Message);
        }
        if (IsAuthoritativeChartScan(scanResult))
        {
            return scanResult;
        }

        string nativeFailureReason = scanResult?.ErrorReason ?? "unknown";
        if (IsNativeBridgeContractFailure(nativeFailureReason))
        {
            LogEverythingScan("chart native file scan failed reason=" + nativeFailureReason);
            return scanResult;
        }

        LogEverythingScan("chart native file scan unavailable reason=" + nativeFailureReason + " fallback=managed");
        QueueEverythingFallbackWarning(nativeFailureReason);
        ReportScanner("Fallback");
        ChartScanExecutionResult fallbackResult;
        try
        {
            fallbackResult = new FastDirectoryFileScanner().Scan(
                bmsDirectories,
                ChartDirectoryScanBuilder.ChartExtensions,
                verboseLog,
                includeTextSurface,
                includeDirectorySurface);
        }
        catch (Exception failure) when (IsOrdinaryScanFailure(failure))
        {
            fallbackResult = CreateFailedChartScanResult("chart_fallback_file_scan_failed:" + failure.Message);
        }
        if (!IsAuthoritativeChartScan(fallbackResult))
        {
            string fallbackFailureReason = GetChartScanFailureReason(fallbackResult);
            LogEverythingScan("chart fallback file scan failed nativeReason=" + nativeFailureReason + " fallbackReason=" + fallbackFailureReason);
            return new ChartScanExecutionResult
            {
                ScanSource = ChartScanSource.Fallback,
                Success = false,
                IsComplete = false,
                ErrorReason = "chart_fallback_file_scan_failed:" + fallbackFailureReason + " (native: " + nativeFailureReason + ")",
                IncompleteReason = fallbackFailureReason,
                FallbackUsed = true,
                FallbackReason = nativeFailureReason
            };
        }
        fallbackResult.ScanSource = ChartScanSource.Fallback;
        fallbackResult.FallbackUsed = true;
        fallbackResult.FallbackReason = nativeFailureReason;
        LogEverythingScan("chart fallback file scan succeeded nativeReason=" + nativeFailureReason + " charts=" + fallbackResult.Result.ChartFilePaths.Count + " dirs=" + fallbackResult.Result.ChartDirectories.Count);
        return fallbackResult;
    }

    /// <summary>
    /// 完全なファイル差分を適用し、目録反映後の変更不能LR2入力を呼出元へ返します。
    /// 開始済みLR2準備Taskは全終端で回収し、失敗した差分へ準備結果を遅れて適用しません。
    /// 公開効果は任意observerへ渡し、準備の二次失敗で元の保存・解析・公開失敗を隠しません。
    /// </summary>
    /// <param name="previousRootDirectories">先行補正前の登録root。同所の旧表記だけを外部folderの追加読取りへ渡し、変更対象は現在rootのままにします。</param>
    internal Lr2FolderFileDiffPreparationResult ApplyFileScanDiff(
        BmsLibraryOptionsSnapshot options,
        List<string> bmsDirectories,
        ChartScanPrefetchInfo chartScanPrefetchInfo,
        Lr2FolderTableSnapshot folderTableSnapshot,
        bool trackLibraryFileCheckProgress,
        string reason,
        InstallDestinationCleanupSnapshot installDestinationCleanupSnapshot,
        Action<Action> postLeaseEffectObserver = null, LibraryFileMutationCapability capability = null, Action<LibraryScanWarning> warningObserver = null, Action saveConfirmedRoots = null, DateTime? generatedAtUtc = null,
        IReadOnlyList<string> previousRootDirectories = null)
    {
        if (installDestinationCleanupSnapshot == null)
        {
            throw new ArgumentNullException(nameof(installDestinationCleanupSnapshot));
        }
        Action postLeaseEffects = null;
        void AddPostLeaseEffect(Action effect, string effectReason)
        {
            if (effect == null)
            {
                return;
            }
            Action guardedEffect = () =>
            {
                try
                {
                    effect();
                }
                catch (Exception exception)
                {
                    logInstallPerformanceWarn(
                        "library_file_scan_post_lease_publication_failed reason="
                        + (effectReason ?? "unknown")
                        + " message="
                        + getDisplayedExceptionMessage(exception).Replace(Environment.NewLine, " | "));
                }
            };
            Action previousEffect = postLeaseEffects;
            postLeaseEffects = previousEffect == null
                ? guardedEffect
                : () =>
                {
                    previousEffect();
                    guardedEffect();
                };
        }

        void PublishPostLeaseEffects()
        {
            if (postLeaseEffects == null)
            {
                return;
            }
            if (postLeaseEffectObserver != null)
            {
                postLeaseEffectObserver(postLeaseEffects);
            }
            else
            {
                postLeaseEffects();
            }
        }
        var emptyResult = new SongTableFileCheckResult();
        if (bmsDirectories == null || bmsDirectories.Count == 0)
        {
            if (trackLibraryFileCheckProgress)
            {
                completeLibraryFileEnumerationProgress();
                completeLibraryFileDiffProgress();
            }
            logInstallPerformance("song_tbl_file_check skipped reason=no_bms_directories operation=" + (reason ?? string.Empty));
            return Lr2FolderFileDiffPreparationResult.FromFileCheckResult(emptyResult, new LibraryFileInitializationResult(options: options));
        }

        logStartupMemoryCheckpoint("file_diff", "before");
        bool fileEnumerationCompleted = false;
        void completeFileEnumerationOnce()
        {
            if (fileEnumerationCompleted)
            {
                return;
            }
            fileEnumerationCompleted = true;
            if (trackLibraryFileCheckProgress)
            {
                completeLibraryFileEnumerationProgress();
            }
        }
        ChartScanPrefetchInfo resolvedChartScanPrefetchInfo = chartScanPrefetchInfo;
        if (resolvedChartScanPrefetchInfo?.ScanResult == null)
        {
            var stopwatchResolveScan = Stopwatch.StartNew();
            ChartScanExecutionResult resolvedScanResult = ExecuteChartScanWithManagedFallback(
                bmsDirectories,
                BMSLibrary.ShouldIncludeLr2TextSurface(options),
                BMSLibrary.ShouldIncludeLr2DirectorySurface(options),
                scannerLabel =>
                {
                    if (trackLibraryFileCheckProgress)
                    {
                        reportLibraryInitializationProgress(
                            BMSLibrary.LibraryInitializationProgressStage.FileEnumeration,
                            scannerLabel,
                            0,
                            0,
                            null,
                            true);
                    }
                }, () => true, fallbackReason =>
                {
                    if (warningObserver == null) { queueEverythingFallbackWarning(fallbackReason); }
                    else { warningObserver(new(LibraryScanWarningKind.EverythingFallback, fallbackReason)); }
                });
            stopwatchResolveScan.Stop();
            resolvedChartScanPrefetchInfo = new ChartScanPrefetchInfo
            {
                ScanResult = resolvedScanResult,
                ElapsedMs = stopwatchResolveScan.ElapsedMilliseconds
            };
        }
        if (!IsAuthoritativeChartScan(resolvedChartScanPrefetchInfo?.ScanResult))
        {
            string failureReason = GetChartScanFailureReason(resolvedChartScanPrefetchInfo?.ScanResult);
            logEverythingScan("song_tbl_file_check skipped reason=incomplete_file_scan operation=" + (reason ?? string.Empty) + " detail=" + failureReason);
            if (warningObserver == null) { queueFileScanSkippedIncompleteWarning(failureReason); }
            else { warningObserver(new(LibraryScanWarningKind.Incomplete, failureReason)); }
            completeFileEnumerationOnce();
            if (trackLibraryFileCheckProgress)
            {
                completeLibraryFileDiffProgress();
            }
            return Lr2FolderFileDiffPreparationResult.FromFileCheckResult(emptyResult, new LibraryFileInitializationResult(options: options));
        }
        List<BeMusicSeeker.Models.ChartDetails> committedInlineChartInfoRows = [];
        IReadOnlyList<ChartFile> currentInstallDestinationCharts = installDestinationCleanupSnapshot.Charts;
        Lr2SongDbSyncAppManagedOutputScope initialAppManagedOutputScope = lr2Synchronization.CreateLr2SongDbSyncAppManagedOutputScope(options);
        if (options.OperationModeLR2DB && !initialAppManagedOutputScope.IsComplete)
        {
            throw new Lr2FolderTableProjectionIncompleteException("LR2 managed output scope could not be captured before file synchronization.");
        }
        Task<Lr2FolderFileDiffPreparationResult> lr2FolderFileDiffPreparationTask = null;
        try
        {
            bool protectExistingBmsRowsFromLr2SongDbSyncMigration = lr2Synchronization.ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(options);
            void SaveRegistrationAndStartLr2Preparation(SongTableFileCheckResult partialResult)
            {
                saveConfirmedRoots?.Invoke();
                if (lr2FolderFileDiffPreparationTask != null
                    || !lr2FolderFileDiffOwner.CanPrepare(options, partialResult))
                {
                    return;
                }

                lr2FolderFileDiffPreparationTask = Task.Run(() =>
                    lr2FolderFileDiffOwner.Prepare(options, bmsDirectories, partialResult, reason, previousRootDirectories))
                    .LoggingAndPropagate("Lr2FolderFileDiffPrepare");
            }

            IReadOnlyList<ChartFile> currentBmsRows = catalogOwnedCollectionOwner.BmsRows;
            IReadOnlyList<ChartFile> currentBmsonRows = catalogOwnedCollectionOwner.BmsonRows;
            SongTableFileCheckResult fileCheckResult = initializationService.ApplyFileScanDiff(
                dbGateway,
                everythingNative,
                options,
                currentBmsRows,
                resolvedChartScanPrefetchInfo.ScanResult,
                resolvedChartScanPrefetchInfo.ElapsedMs,
                null,
                dialogService,
                logInstallPerformance,
                logEverythingScan,
                currentBmsonRows,
                null,
                completeFileEnumerationOnce,
                () =>
                {
                    if (trackLibraryFileCheckProgress)
                    {
                        reportLibraryInitializationProgress(BMSLibrary.LibraryInitializationProgressStage.FileDiff, null, 0, 0, null, true);
                    }
                },
                (total, processed, path) =>
                {
                    if (trackLibraryFileCheckProgress)
                    {
                        reportLibraryInitializationProgress(
                            BMSLibrary.LibraryInitializationProgressStage.FileDiff,
                            null,
                            total,
                            processed,
                            path,
                            processed >= total);
                    }
                },
                logInstallPerformanceWarn,
                rows =>
                {
                    if (rows == null || rows.Count == 0)
                    {
                        return;
                    }
                    committedInlineChartInfoRows.AddRange(rows.Where(row => row != null));
                },
                bmsDirectories,
                bmsDirectories,
                lr2Synchronization.CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow, options),
                folderTableSnapshot,
                SaveRegistrationAndStartLr2Preparation,
                protectExistingBmsRowsFromLr2SongDbSyncMigration: protectExistingBmsRowsFromLr2SongDbSyncMigration,
                lr2FolderExcludedDirectories: initialAppManagedOutputScope.Directories,
                catalogProjectionApplied: null,
                enterNormalFolderMutation: (scope, targets) =>
                {
                    if (!lr2Synchronization.TryEnterManagedOutputMutation(targets.Concat(scope.PruneExactDirectories),
                        recursive: false, out LibraryFileMutationLease playlistLease, capability,
                        recursivePaths: scope.PruneScopeDirectories))
                    { throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy); }
                    return playlistLease;
                },
                rootFileEnumerator: rootFileEnumerator, generatedAtUtc: generatedAtUtc);
            if (fileCheckResult.EmptyScanWithExistingDbSkipped)
            {
                string skipReason = string.IsNullOrWhiteSpace(fileCheckResult.EmptyScanWithExistingDbSkipReason)
                    ? "empty_scan_with_existing_db"
                    : fileCheckResult.EmptyScanWithExistingDbSkipReason;
                logEverythingScan("song_tbl_file_check skipped reason=empty_scan_with_existing_db operation=" + (reason ?? string.Empty) + " detail=" + skipReason);
                if (warningObserver == null) { queueEmptyScanWithExistingDbWarning(skipReason); }
                else { warningObserver(new(LibraryScanWarningKind.EmptyWithExistingData, skipReason)); }
                completeFileEnumerationOnce();
                if (trackLibraryFileCheckProgress)
                {
                    completeLibraryFileDiffProgress();
                }
                return Lr2FolderFileDiffPreparationResult.FromFileCheckResult(fileCheckResult, new LibraryFileInitializationResult(options: options));
            }
            // Keep the catalog projection in this owner so the ordinary scan path
            // completes its catalog work before returning the immutable LR2 bridge
            // input.  The initialization service callback is intentionally unused:
            // it would let a generic parse owner apply catalog state mid-pipeline.
            ApplyCatalogProjection(
                fileCheckResult,
                currentBmsRows,
                currentBmsonRows,
                currentInstallDestinationCharts);
            LogFileScanFailures(fileCheckResult, reason);
            Lr2FolderFileDiffPreparationResult lr2FolderFileDiffPreparation = null;
            if (lr2FolderFileDiffOwner.CanPrepare(options, fileCheckResult))
            {
                lr2FolderFileDiffPreparation = lr2FolderFileDiffOwner.WaitForPreparation(
                    lr2FolderFileDiffPreparationTask,
                    options,
                    bmsDirectories,
                    fileCheckResult,
                    reason, previousRootDirectories);
                lr2FolderFileDiffOwner.ApplyPreparedSurface(
                    fileCheckResult,
                    lr2FolderFileDiffPreparation,
                    reason);
            }
            completeFileEnumerationOnce();
            AddPostLeaseEffect(
                ApplyCatalogStorageReplacement(fileCheckResult, reason),
                "catalog_replacement");
            if (committedInlineChartInfoRows.Count > 0)
            {
                int committedInlineChartInfoRowCount = committedInlineChartInfoRows.Count;
                ChartInfoIndexUpdateResult indexUpdateResult = catalogChartInfoOwner.UpsertIndex(
                    committedInlineChartInfoRows,
                    "file_diff_inline",
                    dispatchPresentation: true,
                    publishEffects: false);
                AddPostLeaseEffect(
                    () => catalogChartInfoOwner.PublishIndexUpsertEffects(
                        indexUpdateResult,
                        committedInlineChartInfoRowCount,
                        "file_diff_inline",
                        dispatchPresentation: true),
                    "chart_info_index");
                committedInlineChartInfoRows.Clear();
            }
            if (fileCheckResult.InlineChartInfoParseFailureRows.Count > 0
                || fileCheckResult.InlineChartInfoParseFailureDeleteMd5s.Count > 0
                || fileCheckResult.InlineChartInfoFailurePersistedCount > 0
                || fileCheckResult.InlineChartInfoFailureClearedCount > 0)
            {
                AddPostLeaseEffect(
                    () => catalogChartInfoOwner.PublishWarningPresentationChanged(
                        "file_diff_inline_chart_info_parse_failure"),
                    "chart_info_warning");
            }
            AddPostLeaseEffect(
                publishCatalogResidual(
                    FileScanCatalogResidualEvent.Create(
                        fileCheckResult.ClearedInstallDestinationCharts,
                        reason)),
                "catalog_residual");
            Lr2SongDbSyncScanSurfaceSnapshot scanSurface = lr2Synchronization.CreateLr2SongDbSyncScanSurface(options, bmsDirectories, fileCheckResult);
            var synchronizationInput = new LibraryFileInitializationResult(scanSurface, fileCheckResult.CommittedLr2SongDbSyncBmsPaths, options);
            lr2Synchronization.MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(options, fileCheckResult);
            if (trackLibraryFileCheckProgress && lr2FolderFileDiffPreparation?.Request == null)
            {
                completeLibraryFileDiffProgress();
            }
            fileCheckResult.ReleasePostApplyTransientBuffers();
            logStartupMemoryCheckpoint("file_diff", "after_release");
            logInstallPerformance("library_file_scan_pipeline completed operation=" + (reason ?? string.Empty));
            PublishPostLeaseEffects();
            // 譜面単体の解析失敗は全体必須失敗へ格上げせず、正本確定と必要公開の完了を返す。
            markCatalogPathConvergenceCompleted();
            return (lr2FolderFileDiffPreparation ?? Lr2FolderFileDiffPreparationResult.FromFileCheckResult(fileCheckResult))
                .WithFileCheckResult(fileCheckResult, synchronizationInput);
        }
        finally
        {
            if (lr2FolderFileDiffPreparationTask != null)
            {
                try
                {
                    lr2FolderFileDiffPreparationTask.GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // 準備Task自身の診断はLoggingAndPropagateが回収します。
                    // 成功経路のfallbackを再実行せず、元失敗と確定済みfactsを保持します。
                }
            }
        }
    }

    internal Action ApplyCatalogStorageReplacement(
        SongTableFileCheckResult fileCheckResult,
        string reason)
    {
        CatalogFileScanStorageReplacementRequest request = null;
        FileScanCatalogReplacementEvent replacementEvent;
        using (ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = resourceHealthOwner.BeginInputMutation())
        {
            request = catalogMutationOwner.CreateFileScanStorageReplacementRequest(
                fileCheckResult.HasDbDiff,
                fileCheckResult.NextFiles,
                fileCheckResult.NextBmsonSongs,
                fileCheckResult.DeletedPaths,
                fileCheckResult.DeletedBmsonPaths,
                fileCheckResult.AddedFiles,
                fileCheckResult.AddedBmsonSongs);
            CatalogFileScanStorageReplacementReceipt receipt = catalogMutationOwner.ApplyFileScanStorageReplacement(request);
            replacementEvent = new FileScanCatalogReplacementEvent(
                request,
                receipt,
                fileCheckResult.NextResourceIndex,
                resourceHealthMutation.BaseIndexCurrent,
                reason);
        }
        return publishCatalogReplacement(replacementEvent);
    }

    internal void ApplyCatalogProjection(
        SongTableFileCheckResult fileCheckResult,
        IEnumerable<ChartFile> currentInstallDestinationCharts,
        CatalogChartCollectionSnapshot storageRowsSnapshot)
    {
        if (storageRowsSnapshot == null)
        {
            throw new ArgumentNullException(nameof(storageRowsSnapshot));
        }
        ApplyCatalogProjection(
            fileCheckResult,
            storageRowsSnapshot.BmsRows,
            storageRowsSnapshot.BmsonRows,
            currentInstallDestinationCharts);
    }

    internal static void ApplyCatalogProjection(
        SongTableFileCheckResult fileCheckResult,
        IEnumerable<ChartFile> currentFiles,
        IEnumerable<ChartFile> currentBmsonSongs,
        IEnumerable<ChartFile> currentInstallDestinationCharts)
    {
        if (fileCheckResult == null)
        {
            throw new ArgumentNullException(nameof(fileCheckResult));
        }

        var stopwatchApply = Stopwatch.StartNew();
        var deletedPathSet = new HashSet<string>(fileCheckResult.DeletedPaths, StringComparer.Ordinal);
        foreach (ChartFile addedFile in fileCheckResult.AddedFiles)
        {
            if (addedFile != null && !string.IsNullOrWhiteSpace(addedFile.Path))
            {
                deletedPathSet.Add(addedFile.Path);
            }
        }

        var updatedCharts = (fileCheckResult.UpdatedCharts ?? [])
            .Where(chart => chart?.Token != null).ToDictionary(chart => chart.Token);
        fileCheckResult.NextFiles.Clear();
        fileCheckResult.NextFiles.AddRange((currentFiles ?? [])
            .Where(file => file != null && !deletedPathSet.Contains(file.Path))
            .Select(file => file.Token != null && updatedCharts.TryGetValue(file.Token, out ChartFile updated) ? updated : file));
        fileCheckResult.NextFiles.AddRange(fileCheckResult.AddedFiles);

        var removedBmsonPaths = new HashSet<string>(fileCheckResult.DeletedBmsonPaths, StringComparer.Ordinal);
        foreach (ChartFile addedSong in fileCheckResult.AddedBmsonSongs)
        {
            if (addedSong != null && !string.IsNullOrWhiteSpace(addedSong.Path))
            {
                removedBmsonPaths.Add(addedSong.Path);
            }
        }

        List<ChartFile> nextBmsonSongs = [.. (currentBmsonSongs ?? [])
            .Where(song => song != null
                && !string.IsNullOrWhiteSpace(song.Path)
                && !removedBmsonPaths.Contains(song.Path))];
        nextBmsonSongs.AddRange(fileCheckResult.AddedBmsonSongs);

        var directoryKeys = new HashSet<string>(
            fileCheckResult.NextDirectoryResourceLookupCache?.Keys ?? [],
            StringComparer.OrdinalIgnoreCase);
        var stopwatchInstlDstCleanup = Stopwatch.StartNew();
        var nextFileOwners = new HashSet<OwnedChartToken>(fileCheckResult.NextFiles.Where(file => file?.Token != null).Select(file => file.Token));
        var nextFilePaths = new HashSet<string>(
            fileCheckResult.NextFiles.Select(file => file?.Path).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        var nextBmsonOwners = new HashSet<OwnedChartToken>(nextBmsonSongs.Where(song => song?.Token != null).Select(song => song.Token));
        var nextBmsonPaths = new HashSet<string>(
            nextBmsonSongs.Select(song => song?.Path).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        foreach (ChartFile chart in (currentInstallDestinationCharts ?? [])
            .Where(IsCurrentChartOwner)
            .Where(chart => !string.IsNullOrWhiteSpace(chart.InstallDestination)))
        {
            if (!directoryKeys.Contains(chart.InstallDestination))
            {
                fileCheckResult.ClearedInstallDestinationCharts.Add(
                    ChartFileProjection.WithPackageState(
                        chart,
                        null,
                        string.Empty,
                        string.Empty,
                        [],
                        [.. (chart.Warnings ?? [])
                            .Where(warning => warning?.Category != ChartWarningCategory.InstallEstimation)]));
            }
        }

        stopwatchInstlDstCleanup.Stop();
        fileCheckResult.InstlDstCleanupMs = stopwatchInstlDstCleanup.ElapsedMilliseconds;
        stopwatchApply.Stop();
        fileCheckResult.ApplyMs = stopwatchApply.ElapsedMilliseconds;
        fileCheckResult.NextBmsonSongs.Clear();
        fileCheckResult.NextBmsonSongs.AddRange(nextBmsonSongs);

        bool IsCurrentChartOwner(ChartFile chart)
        {
            if (chart == null)
            {
                return false;
            }

            return chart.Kind == ChartFileKind.Bms
                ? (chart.Token != null && nextFileOwners.Contains(chart.Token)) || nextFilePaths.Contains(chart.Path)
                : (chart.Token != null && nextBmsonOwners.Contains(chart.Token)) || nextBmsonPaths.Contains(chart.Path);
        }
    }

    /// <summary>未採用の取得失敗だけを不完全結果へ変換し、OOMと取消は直接の既知wrapperも含めて伝播します。</summary>
    private static bool IsOrdinaryScanFailure(Exception failure)
        => failure is not OutOfMemoryException and not OperationCanceledException
            && failure.InnerException is not OutOfMemoryException and not OperationCanceledException;

    private static ChartScanExecutionResult CreateFailedChartScanResult(string reason)
        => new() { Success = false, IsComplete = false, ErrorReason = reason };

    private static bool IsAuthoritativeChartScan(ChartScanExecutionResult scanResult)
    {
        return scanResult?.Success == true
            && scanResult.IsComplete
            && scanResult.Result != null;
    }

    private static string GetChartScanFailureReason(ChartScanExecutionResult scanResult)
    {
        if (scanResult == null)
        {
            return "scan_result_missing";
        }
        if (!string.IsNullOrWhiteSpace(scanResult.IncompleteReason))
        {
            return scanResult.IncompleteReason;
        }
        if (!string.IsNullOrWhiteSpace(scanResult.ErrorReason))
        {
            return scanResult.ErrorReason;
        }
        if (scanResult.Result == null)
        {
            return "scan_result_missing";
        }
        return scanResult.Success ? "scan_incomplete" : "scan_failed";
    }

    private static bool IsNativeBridgeContractFailure(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }
        if (reason.StartsWith("directory_surface_failed:", StringComparison.OrdinalIgnoreCase))
        {
            return RootFileEnumerationService.IsBridgeContractFailure(reason.Substring("directory_surface_failed:".Length));
        }
        return RootFileEnumerationService.IsBridgeContractFailure(reason);
    }

    private void LogFileScanFailures(SongTableFileCheckResult result, string reason)
    {
        if (result?.FileScanFailures == null || result.FileScanFailures.Count == 0)
        {
            return;
        }

        logInstallPerformanceWarn("song_tbl_file_check_failures reason=" + (reason ?? string.Empty) + " count=" + result.FileScanFailures.Count);
        foreach (ChartFileScanFailure failure in result.FileScanFailures)
        {
            if (failure == null)
            {
                continue;
            }

            logInstallPerformanceWarn(
                "song_tbl_file_check_file_failed kind=" + failure.ChartKind
                + " stage=" + failure.Stage
                + " exception=" + failure.ExceptionType
                + " path=" + (failure.Path ?? string.Empty)
                + " message=" + (failure.Message ?? string.Empty).Replace(Environment.NewLine, " | "));
        }
    }
}
