using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryFileScanPipelineOwnerTests
{
    private string? ownedDirectoryPath;

    /// <summary>runnerから注入される任意contextへ、終端を確認できず保持した専用fixtureの場所を記録します。</summary>
    public TestContext? TestContext { get; set; }

    /// <summary>管理主体の準備で所有した専用DBとディレクトリを各ケースの終端で回収します。</summary>
    [TestCleanup]
    public void CleanupOwnedDirectory()
    {
        if (ownedDirectoryPath != null && Directory.Exists(ownedDirectoryPath))
        {
            Directory.Delete(ownedDirectoryPath, recursive: true);
        }
    }

    /// <summary>接続した譜面情報ownerの警告更新をcallbackへ一回渡し、理由を取り違えたり通知を失ったりしないことを確認します。</summary>
    [TestMethod]
    public void CatalogChartInfoOwner_PublishWarningPresentationChangedPublishesWarningEvent()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        CreateOwner(callbacks);

        callbacks.CatalogChartInfoOwner.PublishWarningPresentationChanged("test_warning");

        CatalogChartInfoOwnerEvent ownerEvent = callbacks.CatalogChartInfoEvents.Single();
        Assert.AreEqual(CatalogChartInfoOwnerEventKind.WarningPresentationChanged, ownerEvent.Kind);
        Assert.AreEqual("test_warning", ownerEvent.Reason);
    }

    /// <summary>実差分読込みで譜面情報解析が失敗すると失敗記録を保存し、警告更新を残余通知より先に公開することを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyFileScanDiff_InlineChartInfoFailurePublishesOwnerWarningBeforeResidual()
    {

        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "bad.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Bad\r\n#00111:01\r\n");
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
            Lr2FolderFileDiffPreparationResult scan = owner.ApplyFileScanDiff(
                new BmsLibraryOptionsSnapshot(),
                [directoryPath],
                new ChartScanPrefetchInfo
                {
                    ScanResult = new ChartScanExecutionResult
                    {
                        Success = true,
                        Result = new ChartScanResult
                        {
                            ChartFilePaths = new HashSet<string>([bmsPath], StringComparer.Ordinal),
                            ChartDirectories = new HashSet<string>([directoryPath], StringComparer.OrdinalIgnoreCase)
                        }
                    }
                },
                null!,
                trackLibraryFileCheckProgress: true,
                reason: "test_inline_chart_info_failure",
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
            SongTableFileCheckResult result = scan.FileCheckResult;

            Assert.AreEqual(1, result.InlineChartInfoParseFailedCount, "parse_failed");
            Assert.AreEqual(1, result.InlineChartInfoFailurePersistedCount, "failure_persisted");
            CollectionAssert.AreEqual(
                new[] { "WarningPresentationChanged", "Residual" },
                callbacks.EventOrder.ToArray());
            CatalogChartInfoOwnerEvent warning = callbacks.CatalogChartInfoEvents.Single();
            Assert.AreEqual(CatalogChartInfoOwnerEventKind.WarningPresentationChanged, warning.Kind);
            Assert.AreEqual("file_diff_inline_chart_info_parse_failure", warning.Reason);
            Assert.IsNotNull(callbacks.LastCatalogResidual);
            Assert.AreEqual(0, callbacks.LastCatalogResidual.InstallDestinationChangedCharts.Count);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>実差分の保存後にobserverが失敗しても元例外を返し、確定済み所持versionを巻き戻さず後続入力の取得を維持することを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyFileScanDiff_LatePostLeaseObserverFailurePropagatesFailureWithoutReturningInput()
    {

        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "committed.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Committed\r\n#BPM 120\r\n#00111:01\r\n");
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            LibraryFileScanPipelineOwner owner = CreateOwner(callbacks, lr2ModeEnabled: true);
            var injectedException = new InvalidOperationException("injected post-lease observer failure");
            int initialOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.OwnedCollectionVersion;

            InvalidOperationException thrown = Assert.ThrowsException<InvalidOperationException>(
                () => owner.ApplyFileScanDiff(
                    new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true
                    },
                    [directoryPath],
                    new ChartScanPrefetchInfo
                    {
                        ScanResult = new ChartScanExecutionResult
                        {
                            Success = true,
                            Result = new ChartScanResult
                            {
                                ChartFilePaths = new HashSet<string>([bmsPath], StringComparer.Ordinal),
                                ChartDirectories = new HashSet<string>([directoryPath], StringComparer.OrdinalIgnoreCase)
                            }
                        }
                    },
                    null!,
                    trackLibraryFileCheckProgress: true,
                    reason: "test_late_receipt_failure",
                    installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty,
                    postLeaseEffectObserver: _ => throw injectedException));

            Assert.AreSame(injectedException, thrown);
            Assert.IsNotNull(callbacks.LastCatalogReplacement);
            int committedOwnedCollectionVersion = callbacks.LastCatalogReplacement.Receipt.OwnedCollectionVersion;
            Assert.AreEqual(initialOwnedCollectionVersion + 1, committedOwnedCollectionVersion);
            Assert.AreEqual(committedOwnedCollectionVersion, callbacks.CatalogOwnedCollectionOwner.OwnedCollectionVersion);
            Lr2SongDbSyncInput input = callbacks.Lr2Synchronization.CreateLr2SongDbSyncInput();


            Assert.AreEqual(committedOwnedCollectionVersion, callbacks.CatalogOwnedCollectionOwner.OwnedCollectionVersion);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>同じ実ファイルを再走査した差分なし要求では再解析の進捗と変更確定を行わず、保存済み所持versionを進めないことを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyActiveFileScan_NoDiffPropagatesFailureWithoutReturningInputWithoutAdvancingVersion()
    {

        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "unchanged.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Unchanged\r\n#BPM 120\r\n#00111:01\r\n");
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [directoryPath] = []
                },
                [directoryPath]);
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            LibraryFileScanPipelineOwner owner = CreateOwner(callbacks, lr2ModeEnabled: true, chartFileScanner: chartFileScanner);
            BmsLibraryOptionsSnapshot options = new() { OperationModeLR2DB = true };
            int initialOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.OwnedCollectionVersion;

            long firstGeneration = owner.BeginFileScanRequest(options, [directoryPath], "test_initial_commit");
            Lr2FolderFileDiffPreparationResult first = owner.ApplyActiveFileScan(
                firstGeneration,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
            Assert.IsTrue(first.FileCheckResult.HasDbDiff);
            Assert.AreEqual(1, first.FileCheckResult.BmsPathCount);
            Assert.AreEqual(1, first.FileCheckResult.BmsAddedTargetCount);
            Assert.AreEqual(1, first.FileCheckResult.DbCommitBmsChangedCount);
            Assert.IsTrue(callbacks.ParseProgress.Any(progress => progress.Total == 1 && progress.Processed == 1));
            Assert.IsTrue(first.SynchronizationInput.CommittedBmsPaths.Contains(bmsPath));
            int committedOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.OwnedCollectionVersion;
            Assert.AreEqual(initialOwnedCollectionVersion + 1, committedOwnedCollectionVersion);

            callbacks.ParseProgress.Clear();
            long secondGeneration = owner.BeginFileScanRequest(options, [directoryPath], "test_no_diff");
            Lr2FolderFileDiffPreparationResult second = owner.ApplyActiveFileScan(
                secondGeneration,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);

            Assert.IsFalse(second.FileCheckResult.HasDbDiff);
            Assert.AreEqual(1, second.FileCheckResult.BmsPathCount);
            Assert.AreEqual(0, second.FileCheckResult.BmsAddedTargetCount);
            Assert.AreEqual(0, second.FileCheckResult.DbCommitBmsChangedCount);
            Assert.IsFalse(callbacks.ParseProgress.Any(progress => progress.Total > 0));
            Assert.IsNotNull(callbacks.LastCatalogReplacement);
            Assert.IsFalse(callbacks.LastCatalogReplacement.Receipt.Applied);
            Assert.AreEqual(committedOwnedCollectionVersion, callbacks.CatalogOwnedCollectionOwner.OwnedCollectionVersion);


        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>未採用scanの失敗を警告し、mtime実Taskの回収後に保存済み目録を保って次の明示要求を受理します。</summary>
    /// <remarks>先行Task fault→Resolveの旧null再走査を識別するため既存進捗口を代替に使います。通知耐性は保証せず、scanner取得例外は上位の新規DB起動ケースが保証します。</remarks>
    [DataTestMethod]
    [DataRow(false)] // bridge不備の不完全結果を非採用にします。
    [DataRow(true)] // 失敗した実先行Taskも再走査せず非採用にします。
    public async Task ApplyActiveFileScan_IncompletePrefetchWarnsAndJoinsWithoutApplyingBeforeNextRequest(bool prefetchThrows)
    {

        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? mtime = null;
        Task<Lr2FolderFileDiffPreparationResult>? apply = null;
        LibraryFileScanPipelineOwner? owner = null;
        long generation = 0;
        try
        {
            string bmsPath = Path.Combine(directoryPath, "committed.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Committed\r\n#BPM 120\r\n#00111:01\r\n");
            ChartScanExecutionResult completeScan = new()
            {
                Success = true,
                IsComplete = true,
                Result = new ChartScanResult
                {
                    ChartFilePaths = new HashSet<string>([bmsPath], StringComparer.Ordinal),
                    ChartDirectories = new HashSet<string>([directoryPath], StringComparer.OrdinalIgnoreCase)
                }
            };
            ChartScanExecutionResult incompleteScan = new()
            {
                Success = false,
                IsComplete = false,
                ErrorReason = "bridge_dll_not_found:test_incomplete_after_commit"
            };
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks
            {
                PerformanceObserved = message =>
                {
                    if (message.StartsWith("lr2_normal_folder_mtime_snapshot_prefetch", StringComparison.Ordinal))
                    {
                        readEntered.TrySetResult();
                        readRelease.Task.GetAwaiter().GetResult();
                    }
                },
                EverythingObserved = message =>
                {
                    if (message.StartsWith("song_tbl_file_check skipped reason=incomplete_file_scan", StringComparison.Ordinal)
                        || message.StartsWith("chart_scan_prefetch failed", StringComparison.Ordinal)) { scanFailed.TrySetResult(); }
                }
            };
            SequenceChartFileScanner scanner = prefetchThrows
                ? new SequenceChartFileScanner(completeScan, completeScan)
                : new SequenceChartFileScanner(completeScan, incompleteScan, completeScan);
            owner = CreateOwner(
                callbacks,
                lr2ModeEnabled: true,
                chartFileScanner: scanner, directoryPath: directoryPath);
            BmsLibraryOptionsSnapshot options = new() { OperationModeLR2DB = true };
            int initialOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.OwnedCollectionVersion;

            long firstGeneration = owner.BeginFileScanRequest(options, [directoryPath], "test_initial_commit");
            Lr2FolderFileDiffPreparationResult first = owner.ApplyActiveFileScan(
                firstGeneration,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
            Assert.IsTrue(first.FileCheckResult.HasDbDiff);
            Assert.IsTrue(first.SynchronizationInput.CommittedBmsPaths.Contains(bmsPath));
            int committedOwnedCollectionVersion = callbacks.CatalogOwnedCollectionOwner.OwnedCollectionVersion;
            Assert.AreEqual(initialOwnedCollectionVersion + 1, committedOwnedCollectionVersion);

            using (var db = new LR2SongDBExtended(Path.Combine(directoryPath, "song.db"))) { db.CreateTable<LR2SongDB.folder>(); }
            generation = owner.BeginFileScanRequest(options, [directoryPath], "test_incomplete",
                reportScanner: _ => { if (prefetchThrows) { throw new IOException("test_prefetch_failed"); } });
            mtime = owner.StartActiveNormalFolderMtimeSnapshot(generation);
            await Task.WhenAny(readEntered.Task, mtime);
            if (!readEntered.Task.IsCompleted) { await mtime; Assert.Fail("実mtime読取りの制御点へ到達しませんでした。"); }
            apply = Task.Run(() => owner.ApplyActiveFileScan(generation, true, InstallDestinationCleanupSnapshot.Empty));
            await Task.WhenAny(scanFailed.Task, apply);
            if (!scanFailed.Task.IsCompleted) { await apply; Assert.Fail("実scan失敗へ到達しませんでした。"); }
            Assert.IsFalse(apply.IsCompleted, "開始済みmtime Taskの回収前に実scan失敗を返しません。");
            readRelease.TrySetResult();
            Lr2FolderFileDiffPreparationResult skipped = await apply;
            await mtime;
            Assert.IsFalse(skipped.FileCheckResult.HasDbDiff);
            Assert.AreSame(options, skipped.SynchronizationInput.Options);
            Assert.IsNull(skipped.SynchronizationInput.ScanSurface);
            Assert.AreEqual(0, skipped.SynchronizationInput.CommittedBmsPaths.Count);
            Assert.AreEqual(prefetchThrows ? 1 : 2, scanner.ScanCount, "失敗した同じ要求を再走査しません。");
            Assert.AreEqual(1, callbacks.IncompleteWarningCount);
            StringAssert.Contains(callbacks.LastIncompleteWarningReason, prefetchThrows ? "test_prefetch_failed" : incompleteScan.ErrorReason);

            Assert.IsTrue(callbacks.LastCatalogReplacement.Receipt.Applied);
            Assert.AreEqual(committedOwnedCollectionVersion, callbacks.CatalogOwnedCollectionOwner.OwnedCollectionVersion);
            using (var db = new LR2SongDBExtended(Path.Combine(directoryPath, "song.db")))
            { Assert.AreEqual(bmsPath, db.Table<LR2SongDB.song>().Single().path); }
            long nextGeneration = owner.BeginFileScanRequest(options, [directoryPath], "test_next_explicit_request");
            owner.ApplyActiveFileScan(nextGeneration, true, InstallDestinationCleanupSnapshot.Empty);
            Assert.AreEqual(prefetchThrows ? 2 : 3, scanner.ScanCount);

        }
        finally
        {
            readRelease.TrySetResult();
            try
            {
                if (mtime != null) { await mtime; }
                if (apply != null)
                {
                    await apply;
                }
                else if (generation != 0) { owner?.AbortActiveFileScan(generation); }
            }
            finally { Directory.Delete(directoryPath, recursive: true); }
        }
    }

    /// <summary>Everythingを利用できなくても実managed走査が完全なら、小さい実譜面を採用してDBと所持集合へ反映します。</summary>
    /// <remarks>不完全なnative入力と実filesystemの代替取得を、通常の走査要求・解析・保存へ接続します。</remarks>
    [TestMethod]
    public void ApplyActiveFileScan_EverythingUnavailableAppliesCompleteManagedFallback()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "managed.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\n#TITLE Managed\n#BPM 120\n#00111:01\n");
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            var scanner = new SequenceChartFileScanner(new ChartScanExecutionResult
            { Success = false, IsComplete = false, ErrorReason = "everything_not_running" });
            LibraryFileScanPipelineOwner owner = CreateOwner(callbacks, chartFileScanner: scanner, directoryPath: directoryPath);
            long generation = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [directoryPath], "test_managed_fallback");
            Lr2FolderFileDiffPreparationResult result = owner.ApplyActiveFileScan(generation, true, InstallDestinationCleanupSnapshot.Empty);
            Assert.IsTrue(result.FileCheckResult.HasDbDiff);
            Assert.IsTrue(result.FileCheckResult.ScanFallbackUsed);
            Assert.AreEqual("everything_not_running", result.FileCheckResult.ScanFallbackReason);
            Assert.AreEqual(0, callbacks.IncompleteWarningCount);
            Assert.AreEqual(1, scanner.ScanCount);
            using var db = new LR2SongDBExtended(Path.Combine(directoryPath, "song.db"));
            Assert.AreEqual(bmsPath, db.Table<LR2SongDB.song>().Single().path);
            Assert.AreEqual(1, callbacks.CatalogOwnedCollectionOwner.BmsRows.Count);
        }
        finally { Directory.Delete(directoryPath, recursive: true); }
    }

    /// <summary>
    /// 実DB確定が失敗しても開始済みLR2準備の終端まで受付を保持し、準備の成功・二次故障によらず
    /// 元SQLite原因と先行確定したDB・所持集合を保ち、失敗要求の目録・譜面情報・準備surfaceを公開しません。
    /// </summary>
    /// <remarks>実走査・保存とLR2準備を接続し、専用FSを列挙する依存だけで準備を保持します。falseは準備成功、trueは解放後の準備故障です。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplyActiveFileScan_CommitFailureJoinsLr2PreparationBeforeReleasingRequest(bool failPreparationAfterRelease)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationDependencyExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitFailureObserved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootFileEnumerator = new GatedPreparationRootFileEnumerator(
            preparationEntered, preparationRelease, preparationDependencyExited, failPreparationAfterRelease);
        Task<Lr2FolderFileDiffPreparationResult>? apply = null;
        bool canDeleteDirectory = false;
        LibraryFileScanPipelineOwner? owner = null;
        long generation = 0;
        long overlappingGeneration = 0;
        long nextGeneration = 0;
        const string commitFailureMarker = "scan-preparation-primary-commit-failure";
        const string failedReason = "test_commit_failure_preparation_lifetime";
        try
        {
            string committedPath = Path.Combine(directoryPath, "committed.bms");
            File.WriteAllText(committedPath, "#PLAYER 1\r\n#TITLE Committed\r\n#BPM 120\r\n#00111:01\r\n");
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks
            {
                PerformanceWarnObserved = message =>
                {
                    if (message.StartsWith("song_tbl_file_check db_commit_chunk_failed", StringComparison.Ordinal))
                    {
                        commitFailureObserved.TrySetResult(message);
                    }
                }
            };
            owner = CreateOwner(
                callbacks,
                lr2ModeEnabled: true,
                chartFileScanner: new FastDirectoryFileScanner(),
                directoryPath: directoryPath,
                rootFileEnumerator: rootFileEnumerator);
            BmsLibraryOptionsSnapshot options = new() { OperationModeLR2DB = true };
            generation = owner.BeginFileScanRequest(options, [directoryPath], "test_initial_commit");
            Lr2FolderFileDiffPreparationResult first = owner.ApplyActiveFileScan(
                generation, true, InstallDestinationCleanupSnapshot.Empty);
            generation = 0;
            Assert.IsTrue(first.FileCheckResult.HasDbDiff);
            Assert.IsTrue(first.SynchronizationInput.CommittedBmsPaths.Contains(committedPath));
            CatalogChartCollectionSnapshot committedCatalog = callbacks.CatalogOwnedCollectionOwner.CaptureSnapshot();
            Assert.AreEqual(1, committedCatalog.BmsRows.Count);
            FileScanCatalogReplacementEvent committedReplacement = callbacks.LastCatalogReplacement;
            FileScanCatalogResidualEvent committedResidual = callbacks.LastCatalogResidual;
            CatalogChartInfoOwnerEvent[] committedChartInfoEvents = callbacks.CatalogChartInfoEvents.ToArray();
            string[] committedEventOrder = callbacks.EventOrder.ToArray();
            string songDbPath = Path.Combine(directoryPath, "song.db");
            LR2SongDB.song committedSong;
            using (LR2SongDBExtended baseline = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                committedSong = baseline.Query<LR2SongDB.song>("SELECT * FROM song;").Single();
            }

            string rejectedPath = Path.Combine(directoryPath, "rejected.bms");
            File.WriteAllText(rejectedPath, "#PLAYER 1\r\n#TITLE Rejected\r\n#BPM 150\r\n#00111:01\r\n");
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.Execute("CREATE TRIGGER fail_scan_preparation_commit BEFORE INSERT ON song WHEN NEW.path = '"
                    + rejectedPath.Replace("'", "''", StringComparison.Ordinal)
                    + "' BEGIN SELECT RAISE(ABORT, '" + commitFailureMarker + "'); END;");
            }
            rootFileEnumerator.Arm();
            generation = owner.BeginFileScanRequest(options, [directoryPath], failedReason);
            apply = Task.Run(() => owner.ApplyActiveFileScan(generation, true, InstallDestinationCleanupSnapshot.Empty));
            await Task.WhenAny(preparationEntered.Task, apply);
            if (!preparationEntered.Task.IsCompleted)
            {
                await apply;
                Assert.Fail("実LR2準備の制御点へ到達する前に走査要求が終端しました。");
            }
            await Task.WhenAny(commitFailureObserved.Task, apply);
            if (!commitFailureObserved.Task.IsCompleted)
            {
                await apply;
                Assert.Fail("実DB確定の失敗を観測する前に走査要求が終端しました。");
            }
            StringAssert.Contains(await commitFailureObserved.Task, commitFailureMarker);
            // 通常完了の期限ではなく、閉じた準備gate中の禁止された早期終端だけを短く観測します。
            try
            {
                await apply.WaitAsync(TimeSpan.FromMilliseconds(100));
                Assert.Fail("開始済みLR2準備の終端前に走査要求を解放しました。");
            }
            catch (TimeoutException)
            {
            }
            bool overlapRejected = false;
            try
            {
                overlappingGeneration = owner.BeginFileScanRequest(options, [], "test_overlapping_request");
            }
            catch (InvalidOperationException)
            {
                overlapRejected = true;
            }
            Assert.IsTrue(overlapRejected, "LR2準備を保持した失敗要求の受付を、新しい要求へ渡しません。");

            preparationRelease.TrySetResult();
            Exception? failure = null;
            try
            {
                await apply;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Assert.IsNotNull(failure);
            Exception cause = failure.GetBaseException();
            Assert.IsInstanceOfType(cause, typeof(SQLite.SQLiteException));
            StringAssert.Contains(cause.Message, commitFailureMarker);
            Assert.IsNotNull(cause.StackTrace);
            StringAssert.Contains(cause.StackTrace, nameof(BmsLibraryDbGateway.CommitFileScanDiffChunk));
            // 依存のexitは準備Task全体の終端と別です。実applyの終端を先に待ってから両方を確認します。
            await preparationDependencyExited.Task;
            CatalogChartCollectionSnapshot currentCatalog = callbacks.CatalogOwnedCollectionOwner.CaptureSnapshot();
            Assert.AreEqual(committedCatalog.OwnedCollectionVersion, currentCatalog.OwnedCollectionVersion);
            Assert.AreEqual(1, currentCatalog.BmsRows.Count);
            Assert.AreSame(committedCatalog.BmsRows.Single(), currentCatalog.BmsRows.Single());
            Assert.AreSame(committedReplacement, callbacks.LastCatalogReplacement);
            Assert.AreSame(committedResidual, callbacks.LastCatalogResidual);
            CollectionAssert.AreEqual(committedChartInfoEvents, callbacks.CatalogChartInfoEvents.ToArray());
            CollectionAssert.AreEqual(committedEventOrder, callbacks.EventOrder.ToArray());
            lock (callbacks.PerformanceMessages)
            {
                Assert.IsFalse(callbacks.PerformanceMessages.Any(message =>
                    message.StartsWith("lr2folder_file_diff_prepare reason=" + failedReason + " ", StringComparison.Ordinal)));
            }
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                LR2SongDB.song retainedSong = verify.Query<LR2SongDB.song>("SELECT * FROM song;").Single();
                Assert.AreEqual(committedSong.path, retainedSong.path);
                Assert.AreEqual(committedSong.hash, retainedSong.hash);
                Assert.AreEqual(committedSong.title, retainedSong.title);
                Assert.AreNotEqual(rejectedPath, retainedSong.path);
            }
            nextGeneration = owner.BeginFileScanRequest(options, [], "test_request_after_preparation_terminal");
            Assert.IsTrue(nextGeneration > generation, "開始済み準備を回収した終端後は新しい明示要求を受理します。");
            // 本文のgate・実apply終端・全表明を完遂した場合だけ、専用FSの削除を許可します。
            canDeleteDirectory = true;
        }
        finally
        {
            preparationRelease.TrySetResult();
            if (apply != null)
            {
                try
                {
                    await apply;
                }
                catch (Exception)
                {
                    // 期待失敗を観測し、本文の表明を後片付けで隠しません。
                }
            }
            bool preparationEntryObserved = preparationEntered.Task.IsCompleted;
            // 壊れた本番joinの早期終端では、遅い準備Taskがこの後に専用FSへ入る可能性があります。
            // ここでの保持決定は後続通知で削除へ戻さず、依存exitと子Task全体の終端を同一視しません。
            bool retainDirectory = apply != null && !canDeleteDirectory;
            if (preparationEntryObserved)
            {
                await preparationDependencyExited.Task;
            }
            if (generation != 0)
            {
                owner?.AbortActiveFileScan(generation);
            }
            if (overlappingGeneration != 0)
            {
                owner?.AbortActiveFileScan(overlappingGeneration);
            }
            if (nextGeneration != 0)
            {
                owner?.AbortActiveFileScan(nextGeneration);
            }
            if (retainDirectory)
            {
                try
                {
                    TestContext?.WriteLine("準備子Taskを含む期待した終端検証を完遂できないため保持: " + directoryPath);
                }
                catch (Exception)
                {
                    // 保持場所の診断失敗で、元のtest失敗を置き換えません。
                }
            }
            else
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>受付時の事前検査を実scan前に再確認しても、受理済み要求の出力probeを繰り返し作成・読取り・削除しないことを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyActiveFileScan_RechecksSameRequestWithoutRepeatingOutputProbe()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            nameof(LibraryFileScanPipelineOwnerTests),
            Guid.NewGuid().ToString("N"));
        string outputBase = Path.Combine(directoryPath, "output");
        Directory.CreateDirectory(directoryPath);
        Directory.CreateDirectory(outputBase);
        try
        {
            string bmsPath = Path.Combine(directoryPath, "recheck.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Recheck\r\n#BPM 120\r\n#00111:01\r\n");
            var fileSystem = new RecordingPreflightFileSystem();
            var preflightService = new LibraryDirectoryPreflightService(fileSystem);
            var preflightOptions = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBase
            };
            LibraryDirectoryPreflightRequest request = preflightService.CreateRequest(
                [directoryPath],
                [directoryPath],
                preflightOptions);
            preflightService.EnsureAvailable(request, probeOutputBases: true);
            int createdProbeCount = fileSystem.CreatedProbePaths.Count;
            int openedPathCount = fileSystem.OpenedPaths.Count;
            int deletedPathCount = fileSystem.DeletedPaths.Count;

            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [directoryPath] = []
                },
                [directoryPath]);
            var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
            LibraryFileScanPipelineOwner owner = CreateOwner(
                callbacks,
                chartFileScanner: chartFileScanner,
                directoryPreflightService: preflightService);
            long generation = owner.BeginFileScanRequest(
                new BmsLibraryOptionsSnapshot(),
                [directoryPath],
                "test_preflight_recheck",
                directoryPreflightRequest: request);

            Lr2FolderFileDiffPreparationResult result = owner.ApplyActiveFileScan(
                generation,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);

            Assert.IsNotNull(result);
            Assert.AreEqual(createdProbeCount, fileSystem.CreatedProbePaths.Count);
            Assert.AreEqual(openedPathCount, fileSystem.OpenedPaths.Count);
            Assert.AreEqual(deletedPathCount, fileSystem.DeletedPaths.Count);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>空配下とnull配下の要求を続けて処理しても各走査・差分進捗を終端し、理由付き診断をそれぞれ公開することを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyFileScanDiff_EmptyDirectoryRequestCompletesProgressForRepeatedRequests()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);

        Lr2FolderFileDiffPreparationResult first = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            [],
            null!,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_first",
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        Lr2FolderFileDiffPreparationResult second = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            null!,
            null!,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_second",
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(2, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(2, callbacks.DiffCompletedCount);
        Assert.AreEqual(2, callbacks.PerformanceMessages.Count);
        StringAssert.Contains(callbacks.PerformanceMessages[0], "reason=no_bms_directories");
        StringAssert.Contains(callbacks.PerformanceMessages[1], "reason=no_bms_directories");
    }

    /// <summary>不完全な先行走査では元の理由で警告し、走査・差分の進捗を終端して不完全入力を通常適用へ進めないことを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyFileScanDiff_IncompletePrefetchSkipsStorageAndReportsWarning()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
        var prefetch = new ChartScanPrefetchInfo
        {
            ScanResult = new ChartScanExecutionResult
            {
                Success = false,
                IsComplete = false,
                ErrorReason = "test_incomplete_scan"
            }
        };

        Lr2FolderFileDiffPreparationResult scan = owner.ApplyFileScanDiff(
            new BmsLibraryOptionsSnapshot(),
            ["C:\\charts"],
            prefetch,
            null!,
            trackLibraryFileCheckProgress: true,
            reason: "test_incomplete",
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        SongTableFileCheckResult result = scan.FileCheckResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(1, callbacks.IncompleteWarningCount);
        Assert.AreEqual("test_incomplete_scan", callbacks.LastIncompleteWarningReason);
        Assert.AreEqual(1, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(1, callbacks.DiffCompletedCount);
    }

    /// <summary>検索rootがない受理済み要求もownerの適用経路で走査・差分進捗を終端し、空配下の理由を公開することを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyActiveFileScan_NoRootsUsesOwnedApplyRoute()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
        long generation = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_request");
        Lr2FolderFileDiffPreparationResult scan = owner.ApplyActiveFileScan(
            generation,
            trackLibraryFileCheckProgress: true,
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        SongTableFileCheckResult result = scan.FileCheckResult;

        Assert.IsNotNull(result);
        Assert.AreEqual(1, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(1, callbacks.DiffCompletedCount);
        StringAssert.Contains(callbacks.PerformanceMessages[0], "reason=no_bms_directories");
    }

    /// <summary>進行中要求への重複受付を拒否し、その実適用終端後だけ新しい世代の要求を受け付けることを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void BeginFileScanRequest_RejectsOverlapUntilTerminal()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
        long firstGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_first");
        Assert.ThrowsException<InvalidOperationException>(
            () => owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_overlap"));

        Lr2FolderFileDiffPreparationResult scan = owner.ApplyActiveFileScan(
            firstGeneration,
            trackLibraryFileCheckProgress: true,
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        SongTableFileCheckResult result = scan.FileCheckResult;
        Assert.IsNotNull(result);
        long secondGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_second");
        Assert.IsTrue(secondGeneration > firstGeneration);
        owner.AbortActiveFileScan(secondGeneration);
    }

    /// <summary>取消済み世代の適用は通知を出さず拒否し、次に受理した世代だけ走査・差分進捗を終端することを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void AbortActiveFileScan_PreventsApplyAndAllowsNextRequest()
    {
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks();
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
        long abortedGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_abort");

        owner.AbortActiveFileScan(abortedGeneration);
        long nextGeneration = owner.BeginFileScanRequest(new BmsLibraryOptionsSnapshot(), [], "test_after_abort");
        Assert.ThrowsException<InvalidOperationException>(
            () => owner.ApplyActiveFileScan(
                abortedGeneration,
                trackLibraryFileCheckProgress: true,
                installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty));
        Assert.AreEqual(0, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(0, callbacks.DiffCompletedCount);

        Lr2FolderFileDiffPreparationResult scan = owner.ApplyActiveFileScan(
            nextGeneration,
            trackLibraryFileCheckProgress: true,
            installDestinationCleanupSnapshot: InstallDestinationCleanupSnapshot.Empty);
        SongTableFileCheckResult result = scan.FileCheckResult;
        Assert.IsNotNull(result);
        Assert.AreEqual(1, callbacks.EnumerationCompletedCount);
        Assert.AreEqual(1, callbacks.DiffCompletedCount);
    }

    /// <summary>走査時snapshotを基に削除・追加を投影し、現集合が交換されても元の維持対象と新しい対象を取り違えないことを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyCatalogProjection_ReplacesDeletedAndAddedCatalogItems()
    {
        ChartFile keepFile = (ChartTestValues.Empty() with { Path = "keep.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile replacedFile = (ChartTestValues.Empty() with { Path = "replace.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile replacementFile = (ChartTestValues.Empty() with { Path = "replace.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsFiles = [keepFile, replacedFile]
        };
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        result.DeletedPaths.Add(replacedFile.Path);
        result.AddedFiles.Add(replacementFile);
        CatalogChartCollectionSnapshot capturedRows = callbacks.CatalogOwnedCollectionOwner.CaptureSnapshot();
        callbacks.CatalogOwnedCollectionOwner.ReplaceCharts([((ChartTestValues.Empty() with { Path = "live-replacement.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }))], null, replaceBmson: false);

        owner.ApplyCatalogProjection(result, [], capturedRows);

        Assert.AreEqual(2, result.NextFiles.Count);
        Assert.AreEqual(keepFile.Path, result.NextFiles[0].Path);
        Assert.AreSame(capturedRows.BmsRows[0].Token, result.NextFiles[0].Token);
        Assert.AreSame(replacementFile, result.NextFiles[1]);
    }

    /// <summary>両形式の集合交換後も先に捕捉したsnapshotを保持し、現在snapshotと状態件数だけを新しい集合へ更新することを確認します。</summary>
    [TestMethod]
    public void CatalogChartCollectionSnapshot_CapturesBothKindsAndRemainsStableAcrossReplacement()
    {
        ChartFile firstBms = (ChartTestValues.Empty() with { Path = "first.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile secondBms = (ChartTestValues.Empty() with { Path = "second.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile firstBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = "first.bmson", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        ChartFile secondBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = "second.bmson", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        ChartFile replacementBms = (ChartTestValues.Empty() with { Path = "replacement.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile replacementBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = "replacement.bmson", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        var owner = new CatalogOwnedCollectionOwner();

        CatalogChartCollectionSnapshot captured = owner.ReplaceChartsAndCaptureSnapshot(
            [firstBms, secondBms],
            [firstBmson, secondBmson]);
        owner.ReplaceCharts([replacementBms], null, replaceBmson: false);
        owner.ReplaceCharts(null, [replacementBmson], replaceBms: false);

        CollectionAssert.AreEqual(new[] { firstBms, secondBms }.Select(chart => chart.Path).ToArray(), captured.BmsRows.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(new[] { firstBmson, secondBmson }.Select(chart => chart.Path).ToArray(), captured.BmsonRows.Select(chart => chart.Path).ToArray());

        CatalogChartCollectionSnapshot current = owner.CaptureSnapshot();
        CatalogChartCollectionStateSnapshot state = owner.CaptureStateSnapshot();
        Assert.AreEqual(current.OwnedCollectionVersion, state.OwnedCollectionVersion);
        Assert.AreEqual(current.BmsRows.Count, state.BmsRowCount);
        Assert.AreEqual(current.BmsonRows.Count, state.BmsonRowCount);
        CollectionAssert.AreEqual(new[] { replacementBms }.Select(chart => chart.Path).ToArray(), current.BmsRows.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(new[] { replacementBmson }.Select(chart => chart.Path).ToArray(), current.BmsonRows.Select(chart => chart.Path).ToArray());
    }

    /// <summary>切り離したBMS準備snapshotのstale導入先を現在所有へ解決し、結果側のprojectionで導入先が解除されることを確認します。</summary>
    /// <remarks>管理主体に準備snapshotと現在の所持snapshotを渡し、返る導入先解除projectionを観測します。</remarks>
    [TestMethod]
    public void ApplyCatalogProjection_ClearsStaleInstallDestinationFromDetachedBmsSnapshot()
    {
        ChartFile keepFile = (ChartTestValues.Empty() with { Path = "C:\\Library\\chart.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsFiles = [keepFile]
        };
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        ChartFile chart = ChartFileProjection.WithPackageState(
            (keepFile),
            "C:\\Install\\stale",
            string.Empty,
            string.Empty,
            []);
        var snapshot = InstallDestinationCleanupSnapshot.FromCharts([chart]);

        owner.ApplyCatalogProjection(result, snapshot.Charts, callbacks.CatalogOwnedCollectionOwner.CaptureSnapshot());

        ChartFile change = result.ClearedInstallDestinationCharts.Single();
        Assert.AreEqual("C:\\Library\\chart.bms", change.Path);
        Assert.IsTrue(string.IsNullOrWhiteSpace(change.InstallDestination));
    }

    /// <summary>切り離したbmson準備snapshotのstale導入先も現在所有へ解決して解除し、BMSだけへ処理を限定しないことを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyCatalogProjection_ClearsStaleInstallDestinationFromDetachedBmsonSnapshot()
    {
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Library\\chart.bmson",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsonCharts = [bmsonSong]
        };
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        ChartFile chart = ChartFileProjection.WithPackageState(
            (bmsonSong),
            "C:\\Install\\stale",
            string.Empty,
            string.Empty,
            []);
        var snapshot = InstallDestinationCleanupSnapshot.FromCharts([chart]);

        owner.ApplyCatalogProjection(result, snapshot.Charts, callbacks.CatalogOwnedCollectionOwner.CaptureSnapshot());

        ChartFile change = result.ClearedInstallDestinationCharts.Single();
        Assert.AreEqual("C:\\Library\\chart.bmson", change.Path);
        Assert.IsTrue(string.IsNullOrWhiteSpace(change.InstallDestination));
    }

    /// <summary>同じpathでもBMS準備snapshotをbmson所有へ適用せず、別形式の導入先を誤って解除しないことを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyCatalogProjection_DetachedBmsSnapshotDoesNotMatchSamePathBmsonOwner()
    {
        string sharedPath = "C:\\Library\\same-path.chart";
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = sharedPath,
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsonCharts = [bmsonSong]
        };
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache()
        };
        ChartFile bmsFile = (ChartTestValues.Empty() with { Path = sharedPath, Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile bmsChart = ChartFileProjection.WithPackageState(
            (bmsFile),
            "C:\\Install\\stale",
            string.Empty,
            string.Empty,
            []);
        var snapshot = InstallDestinationCleanupSnapshot.FromCharts([bmsChart]);

        owner.ApplyCatalogProjection(result, snapshot.Charts, callbacks.CatalogOwnedCollectionOwner.CaptureSnapshot());

        Assert.AreEqual(0, result.ClearedInstallDestinationCharts.Count);
    }

    /// <summary>保存済み差分の集合交換を所持ownerへ渡し、二件の現在集合と適用済みreceiptを公開することを確認します。</summary>
    /// <remarks>実走査の管理主体を保存・所持owner・通知callbackへ接続し、管理主体の入口から終端までの受渡しを観測します。</remarks>
    [TestMethod]
    public void ApplyCatalogStorageReplacement_UsesCatalogOwnerAndPublishesReceipt()
    {
        ChartFile keptFile = (ChartTestValues.Empty() with { Path = "keep.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        ChartFile addedFile = (ChartTestValues.Empty() with { Path = "added.bms", Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        var callbacks = new RecordingLibraryFileScanPipelineCallbacks
        {
            BmsFiles = [keptFile]
        };
        LibraryFileScanPipelineOwner owner = CreateOwner(callbacks);
        var result = new SongTableFileCheckResult
        {
            HasDbDiff = true
        };
        result.NextFiles.AddRange([keptFile, addedFile]);
        result.AddedFiles.Add(addedFile);

        owner.ApplyCatalogStorageReplacement(
            result,
            "test_storage_replacement");

        Assert.IsNotNull(callbacks.LastCatalogReplacement);
        Assert.IsTrue(callbacks.LastCatalogReplacement.Receipt.Applied);
        Assert.AreEqual(2, callbacks.CatalogOwnedCollectionOwner.CaptureSnapshot().BmsRows.Count);
    }

    private LibraryFileScanPipelineOwner CreateOwner(
        RecordingLibraryFileScanPipelineCallbacks callbacks,
        bool lr2ModeEnabled = false,
        IChartFileScanner? chartFileScanner = null,
        LibraryDirectoryPreflightService? directoryPreflightService = null, string? directoryPath = null,
        IRootFileEnumerator? rootFileEnumerator = null)
    {
        directoryPath ??= ownedDirectoryPath ??= Path.Combine(Path.GetTempPath(), nameof(LibraryFileScanPipelineOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        string songDbPath = Path.Combine(directoryPath, "song.db");
        using (new LR2SongDBExtended(songDbPath))
        {
        }
        TestBmsLibrary library = lr2ModeEnabled
            ? new TestBmsLibrary(
                songDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true
                })
            : new TestBmsLibrary(songDbPath);
        var dbGateway = new BmsLibraryDbGateway(songDbPath);
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        callbacks.CatalogOwnedCollectionOwner = storageRowsOwner;
        storageRowsOwner.ReplaceCharts([.. callbacks.BmsFiles], null, replaceBmson: false);
        storageRowsOwner.ReplaceCharts(null, [.. callbacks.BmsonCharts], replaceBms: false);
        callbacks.Lr2Synchronization = library.Lr2Synchronization;
        var catalogMutationOwner = new CatalogMutationOwner(storageRowsOwner, dbGateway);
        var catalogChartInfoOwner = new CatalogChartInfoOwner(
            _ => { },
            () => false,
            (_, _) => false,
            null,
            _ => { });
        callbacks.CatalogChartInfoOwner = catalogChartInfoOwner;
        catalogChartInfoOwner.ConfigureWorkflow(dbGateway, catalogMutationOwner, storageRowsOwner, _ => { }, ownerEvent =>
            {
                callbacks.CatalogChartInfoEvents.Add(ownerEvent);
                callbacks.EventOrder.Add(ownerEvent.Kind.ToString());
            });
        var resourceHealthOwner = new ResourceHealthIndexOwner(
            new BmsLibraryMaintenanceService(),
            _ => { },
            () => new ResourceHealthIndexCurrentVersion(
                new OwnedChartCollectionVersionSnapshot(0),
                0,
                0));
        return new LibraryFileScanPipelineOwner(
            dbGateway,
            storageRowsOwner,
            new BmsLibraryDialogService(),
            () => false,
            callbacks.ReportLibraryInitializationProgress,
            callbacks.CompleteLibraryFileEnumerationProgress,
            callbacks.CompleteLibraryFileDiffProgress,
            callbacks.LogInstallPerformance,
            callbacks.LogInstallPerformanceWarn,
            callbacks.LogEverythingScan,
            callbacks.LogStartupMemoryCheckpoint,
            callbacks.GetDisplayedExceptionMessage,
            () => { },
            callbacks.QueueEverythingFallbackWarning,
            callbacks.QueueFileScanSkippedIncompleteWarning,
            callbacks.QueueEmptyScanWithExistingDbWarning,
            library.Lr2Synchronization,
            catalogMutationOwner,
            catalogChartInfoOwner,
            resourceHealthOwner,
            callbacks.PublishCatalogReplacement,
            callbacks.PublishCatalogResidual,
            new BmsLibraryInitializationService(),
            new EverythingNative(ApplicationPathPolicy.Current),
            chartFileScanner,
            rootFileEnumerator,
            directoryPreflightService: directoryPreflightService);
    }

    private sealed class GatedPreparationRootFileEnumerator(
        TaskCompletionSource entered,
        TaskCompletionSource release,
        TaskCompletionSource dependencyExited,
        bool failAfterRelease) : IRootFileEnumerator
    {
        private readonly FastRootFileEnumerator inner = new();
        private int armed;
        private int lr2EnumerationCount;

        /// <summary>先行scanの終端後、次のscan内の二回目のLR2候補列挙だけを準備gateへ接続します。</summary>
        internal void Arm()
        {
            Interlocked.Exchange(ref lr2EnumerationCount, 0);
            Volatile.Write(ref armed, 1);
        }

        /// <summary>実FS列挙へ委譲し、背景準備の候補列挙だけを明示解放まで保持して依存のexitを通知します。</summary>
        public RootFileEnumerationResult EnumerateFiles(
            IEnumerable<string> rootDirectories,
            IEnumerable<RootFileEnumerationGroup> groups,
            bool verboseLog = false)
        {
            RootFileEnumerationGroup[] requestedGroups = groups.ToArray();
            if (Volatile.Read(ref armed) == 0
                || !requestedGroups.Any(group => group.Extensions.Contains(".lr2folder", StringComparer.OrdinalIgnoreCase))
                || Interlocked.Increment(ref lr2EnumerationCount) != 2)
            {
                return inner.EnumerateFiles(rootDirectories, requestedGroups, verboseLog);
            }
            entered.TrySetResult();
            try
            {
                release.Task.GetAwaiter().GetResult();
                if (failAfterRelease)
                {
                    throw new IOException("scan-preparation-secondary-enumeration-failure");
                }
                return inner.EnumerateFiles(rootDirectories, requestedGroups, verboseLog);
            }
            finally
            {
                dependencyExited.TrySetResult();
            }
        }
    }

    private sealed class SequenceChartFileScanner(params ChartScanExecutionResult[] results) : IChartFileScanner
    {
        private readonly IReadOnlyList<ChartScanExecutionResult> results = results ?? throw new ArgumentNullException(nameof(results));
        private int nextResultIndex;
        internal int ScanCount => nextResultIndex;

        public ChartScanExecutionResult Scan(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> chartExtensions,
            bool verboseLog = false,
            bool includeTextSurface = true,
            bool includeDirectorySurface = false)
        {
            int scanCount = Interlocked.Increment(ref nextResultIndex);
            int resultIndex = Math.Min(scanCount - 1, results.Count - 1);
            return results[resultIndex];
        }
    }

    private sealed class RecordingPreflightFileSystem : ILibraryDirectoryPreflightFileSystem
    {
        internal List<string> CreatedProbePaths { get; } = [];

        internal List<string> OpenedPaths { get; } = [];

        internal List<string> DeletedPaths { get; } = [];

        public FileAttributes GetAttributes(string path) => LongPathFileSystem.GetAttributes(path);

        public IEnumerable<string> EnumerateDirectoryEntries(string path) =>
            LongPathFileSystem.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly);

        public string CreateOwnedProbePath(string directoryPath, string purpose)
        {
            string path = Path.Combine(
                directoryPath,
                ".bemusicseeker-" + purpose + "-" + Guid.NewGuid().ToString("N") + ".tmp");
            CreatedProbePaths.Add(path);
            return path;
        }

        public Stream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            OpenedPaths.Add(path);
            return LongPathFileSystem.Open(path, mode, access, share);
        }

        public void DeleteFile(string path)
        {
            DeletedPaths.Add(path);
            LongPathFileSystem.DeleteFile(path);
        }
    }

    private sealed class RecordingLibraryFileScanPipelineCallbacks
    {
        public IReadOnlyList<ChartFile> BmsFiles { get; set; } = [];

        public IReadOnlyList<ChartFile> BmsonCharts { get; set; } = [];

        public int EnumerationCompletedCount { get; private set; }

        public int DiffCompletedCount { get; private set; }

        public int IncompleteWarningCount { get; private set; }

        public string LastIncompleteWarningReason { get; private set; } = string.Empty;

        public List<string> PerformanceMessages { get; } = [];

        public List<string> EverythingMessages { get; } = [];

        public List<(int Total, int Processed)> ParseProgress { get; } = [];

        public CatalogOwnedCollectionOwner CatalogOwnedCollectionOwner { get; set; } = null!;

        public CatalogChartInfoOwner CatalogChartInfoOwner { get; set; } = null!;

        public BMSLibrary.Lr2SynchronizationOwner Lr2Synchronization { get; set; } = null!;

        public List<CatalogChartInfoOwnerEvent> CatalogChartInfoEvents { get; } = [];

        public List<string> EventOrder { get; } = [];

        public FileScanCatalogReplacementEvent LastCatalogReplacement { get; private set; } = null!;

        public FileScanCatalogResidualEvent LastCatalogResidual { get; private set; } = null!;

        public void ReportLibraryInitializationProgress(
            BMSLibrary.LibraryInitializationProgressStage stage,
            string scannerLabel = null!,
            int totalCount = 0,
            int processedCount = 0,
            string currentPath = null!,
            bool force = false)
        {
            if (stage == BMSLibrary.LibraryInitializationProgressStage.FileDiff)
            {
                lock (ParseProgress)
                {
                    ParseProgress.Add((totalCount, processedCount));
                }
            }
        }

        public void CompleteLibraryFileEnumerationProgress()
        {
            EnumerationCompletedCount++;
        }

        public void CompleteLibraryFileDiffProgress()
        {
            DiffCompletedCount++;
        }

        internal Action<string>? PerformanceObserved { get; init; }

        /// <summary>既存の警告診断を観測し、対象処理の失敗自体は生成しません。</summary>
        internal Action<string>? PerformanceWarnObserved { get; init; }
        internal Action<string>? EverythingObserved { get; init; }

        public void LogInstallPerformance(string message)
        {
            PerformanceObserved?.Invoke(message);
            lock (PerformanceMessages) { PerformanceMessages.Add(message); }
        }

        public void LogInstallPerformanceWarn(string message)
        {
            PerformanceWarnObserved?.Invoke(message);
        }

        public void LogEverythingScan(string message)
        {
            EverythingObserved?.Invoke(message);
            EverythingMessages.Add(message);
        }

        public void LogStartupMemoryCheckpoint(string phase, string point)
        {
        }

        public string GetDisplayedExceptionMessage(Exception exception)
        {
            return exception?.Message ?? string.Empty;
        }

        public void QueueEverythingFallbackWarning(string fallbackReason)
        {
        }

        public void QueueFileScanSkippedIncompleteWarning(string failureReason)
        {
            IncompleteWarningCount++;
            LastIncompleteWarningReason = failureReason;
        }

        public void QueueEmptyScanWithExistingDbWarning(string failureReason)
        {

        }

        public Action PublishCatalogReplacement(FileScanCatalogReplacementEvent replacementEvent)
        {
            LastCatalogReplacement = replacementEvent;
            return null!;
        }

        public Action PublishCatalogResidual(FileScanCatalogResidualEvent residualEvent)
        {
            LastCatalogResidual = residualEvent;
            return () => EventOrder.Add("Residual");
        }

    }

}
