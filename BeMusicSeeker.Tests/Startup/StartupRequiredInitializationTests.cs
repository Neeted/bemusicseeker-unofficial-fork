using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupRequiredInitializationTests
{
    /// <summary>実走査・保存済み表・管理領域・BMTを接続し、出力後も必要LR2終端までL/Pを保持します。</summary>
    [TestMethod]
    public async Task RequiredInitialization_HoldsBothAdmissionsUntilLr2TerminalAndWritesSavedOutputs()
    {
        using var fixture = new Fixture(withSavedScore: true);
        using var semaphore = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist);
        StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission);
        var outputProgress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
        fixture.Playlist.CustomFolderOutputRepairProgressReporter = outputProgress.Enqueue;
        fixture.Playlist.BmtOutput.ExportProgressReporter = outputProgress.Enqueue;
        var scoreProgress = new ConcurrentQueue<(OperationProgressRequest Request, bool Running)>();
        fixture.Library.StartupRequestProgressReporter = (request, running) =>
        {
            if (request.Source == "chart_info_hydration") { Assert.AreEqual(request, fixture.Library.ChartInfoHydrationProgressRequest); }
            scoreProgress.Enqueue((request, running));
        };
        fixture.Library.AttachStartupRequestProgressSources();
        var originatingRequest = new OperationProgressRequest(44, 55, "required_initialization", 1);
        Task<StartupRequiredInitializationResult> operation = owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
            new Lr2SongDbSyncWorkflowOwner(runtime), PerformanceInteraction.Start("test_required_startup"), accepted.Capability, originatingRequest, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { });
        try
        {
            await Task.WhenAny(runtime.Entered.Task, operation);
            if (!runtime.Entered.Task.IsCompleted) { await operation; Assert.Fail("必要LR2同期へ到達しませんでした。"); }
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsFalse(fixture.Library.OperationAdmission.TryEnter(out _));
            Assert.IsFalse(fixture.Playlist.TryEnterPlaylistMutation(out _));
            Assert.IsTrue(Directory.EnumerateFiles(fixture.OutputDirectory, "*.lr2folder", SearchOption.AllDirectories).Any());
            string bmtFile = Directory.EnumerateFiles(Path.GetDirectoryName(fixture.BmtPath)!, "*.bmt", SearchOption.AllDirectories)
                .First(path => ReadBmtText(path).Contains(fixture.Sha256, StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(bmtFile));
            Assert.IsNotNull(runtime.Input);
            Assert.IsTrue(runtime.Input.CommittedBmsPaths.Contains(fixture.ChartPath));
            Assert.IsNotNull(runtime.PreparedSurface);
            (OperationProgressRequest Request, bool Running)[] scoreSnapshots = scoreProgress.Where(snapshot => snapshot.Request.Source == "score_hydration_deferred").ToArray();
            Assert.IsTrue(scoreSnapshots.Any(snapshot => snapshot.Running));
            Assert.IsFalse(scoreSnapshots.Last().Running);
            Assert.AreEqual(44L, scoreSnapshots.First().Request.Generation);
            Assert.AreEqual(55L, scoreSnapshots.First().Request.OperationToken);
            Assert.IsTrue(scoreSnapshots.All(snapshot => snapshot.Request == scoreSnapshots.First().Request));
            Assert.AreEqual(123, fixture.Library.GetBMSScores().Single().perfect);
            Assert.AreEqual("Saved", fixture.Library.GetPlaylistReferenceDisplay(
                ChartFileContentReader.ReadSnapshot(fixture.ChartPath).Md5, fixture.Sha256).Names);
            Assert.AreEqual(0, fixture.OptionalRegistrations, "必須coreから任意producerを登録しません。");
            (OperationProgressRequest Request, bool Running)[] chartInfoSnapshots = scoreProgress.Where(snapshot => snapshot.Request.Source == "chart_info_hydration").ToArray();
            Assert.IsTrue(chartInfoSnapshots.Any(snapshot => snapshot.Running));
            Assert.IsFalse(chartInfoSnapshots.Last().Running);
            Assert.AreEqual(44L, chartInfoSnapshots.First().Request.Generation);
            Assert.AreEqual(55L, chartInfoSnapshots.First().Request.OperationToken);
            Assert.IsTrue(chartInfoSnapshots.All(snapshot => snapshot.Request == chartInfoSnapshots.First().Request));
            foreach (string source in new[] { "custom_folder_repair", "bmt" })
            {
                PlaylistSyncProgressSnapshot[] snapshots = outputProgress.Where(snapshot => snapshot.Source == source).ToArray();
                Assert.IsTrue(snapshots.Any(snapshot => snapshot.IsActive), source);
                Assert.IsFalse(snapshots.Last().IsActive, source);
                OperationProgressRequest request = snapshots.First().Request;
                Assert.IsNotNull(request);
                Assert.IsFalse(string.IsNullOrWhiteSpace(request.Source));
                Assert.AreEqual(44L, request.Generation);
                Assert.AreEqual(55L, request.OperationToken);
                Assert.IsTrue(snapshots.All(snapshot => snapshot.Request == request));
            }
            runtime.Release.TrySetResult();
            StartupRequiredInitializationResult result = await operation;
            Assert.IsNull(result.Lr2Failure);
            Assert.AreSame(runtime.Input, result.FileResult);
        }
        finally
        {
            runtime.Release.TrySetResult();
            try { await operation; }
            finally { accepted.Dispose(); }
        }
        Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable nextLibrary));
        nextLibrary.Dispose();
        Assert.IsTrue(fixture.Playlist.TryEnterPlaylistMutation(out IDisposable nextPlaylist));
        nextPlaylist.Dispose();
    }

    /// <summary>必須スコアの実行開始で終了要求を起こし、元の取消・同一要求の終端と両受付解放を確認します。</summary>
    [TestMethod]
    public async Task RequiredInitialization_ScoreCancellationStopsOutputsAndLr2AndPreservesTerminalIdentity()
    {
        using var fixture = new Fixture(withSavedScore: true);
        using var semaphore = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist);
        var progress = new ConcurrentQueue<(OperationProgressRequest Request, bool Running)>();
        fixture.Library.StartupRequestProgressReporter = (request, running) =>
        {
            progress.Enqueue((request, running));
            if (request.Source == "score_hydration_deferred" && running) { fixture.Library.RequestShutdown("controlled_required_score_cancellation"); }
        };
        var origin = new OperationProgressRequest(14, 38, "required_initialization", 1);
        using (StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission))
        {
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
                new Lr2SongDbSyncWorkflowOwner(runtime), PerformanceInteraction.Start("test_required_score_cancellation"), accepted.Capability, origin, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { }));
            Assert.IsFalse(runtime.Entered.Task.IsCompleted);
            Assert.AreEqual(0, fixture.Playlist.PlaylistEntriesHydrationRequestedVersion);
        }
        (OperationProgressRequest Request, bool Running)[] snapshots = progress.Where(snapshot => snapshot.Request.Source == "score_hydration_deferred").ToArray();
        Assert.IsTrue(snapshots.Any(snapshot => snapshot.Running));
        Assert.IsFalse(snapshots.Last().Running);
        Assert.AreEqual(origin.Generation, snapshots.First().Request.Generation);
        Assert.AreEqual(origin.OperationToken, snapshots.First().Request.OperationToken);
        Assert.IsTrue(snapshots.All(snapshot => snapshot.Request == snapshots.First().Request));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        Assert.IsFalse(fixture.Library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
    }

    /// <summary>ローカル出力失敗をLR2単独失敗へ読み替えず、確定したローカルDBを保持します。</summary>
    [TestMethod]
    public async Task RequiredInitialization_LocalOutputFailureDoesNotStartLr2()
    {
        using var fixture = new Fixture(blockBmtOutput: true);
        using var semaphore = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist);
        var outputProgress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
        fixture.Playlist.CustomFolderOutputRepairProgressReporter = outputProgress.Enqueue;
        fixture.Playlist.BmtOutput.ExportProgressReporter = outputProgress.Enqueue;
        var origin = new OperationProgressRequest(12, 34, "required_initialization", 1);
        using (StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission))
        {
            PlaylistMutationPostCommitException failure = await Assert.ThrowsExceptionAsync<PlaylistMutationPostCommitException>(() => owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
                new Lr2SongDbSyncWorkflowOwner(runtime), PerformanceInteraction.Start("test_required_startup"), accepted.Capability, origin, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { }));
            Assert.IsInstanceOfType<IOException>(failure.InnerException);
            Assert.IsFalse(runtime.Entered.Task.IsCompleted);
        }
        foreach (string source in new[] { "custom_folder_repair", "bmt" })
        {
            PlaylistSyncProgressSnapshot[] snapshots = outputProgress.Where(snapshot => snapshot.Source == source).ToArray();
            Assert.IsTrue(snapshots.Length > 0, source);
            Assert.IsFalse(snapshots.Last().IsActive, source);
            OperationProgressRequest request = snapshots.First().Request;
            Assert.IsNotNull(request);
            Assert.IsFalse(string.IsNullOrWhiteSpace(request.Source));
            Assert.AreEqual(origin.Generation, request.Generation);
            Assert.AreEqual(origin.OperationToken, request.OperationToken);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.Request == request));
        }
        using LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.SongDbPath).OpenSongDbReadOnly();
        Assert.IsNotNull(verify.Find<LR2SongDB.song>(fixture.ChartPath));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>実LR2書込みだけを失敗させ、失敗結果・永続記録とローカル確定の保持を確認します。</summary>
    [TestMethod]
    public async Task RequiredInitialization_Lr2FailureReturnsIndependentFailureAndKeepsLocalData()
    {
        using var fixture = new Fixture(failLr2: true);
        using var semaphore = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist);
        runtime.Release.TrySetResult();
        int failurePublications = 0;
        fixture.Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.Lr2SongDbSyncStatusVersion)
                && fixture.Library.GetLr2SongDbSyncStatusSnapshot().Status == Lr2SongDbSyncStatusKind.Failed) { failurePublications++; }
        };
        StartupRequiredInitializationResult result;
        using (StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission))
        {
            result = await owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
                new Lr2SongDbSyncWorkflowOwner(runtime), PerformanceInteraction.Start("test_required_startup"), accepted.Capability, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { });
        }
        Assert.IsNotNull(result.Lr2Failure);
        Assert.AreEqual(1, failurePublications, "下位で確定したFailedを必須ownerが再公開しない。");
        StringAssert.Contains(result.Lr2Failure.Message, "lr2-only-failure");
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Failed, fixture.Library.GetLr2SongDbSyncStatusSnapshot().Status);
        using LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.SongDbPath).OpenSongDbReadOnly();
        Assert.IsNotNull(verify.Find<LR2SongDB.song>(fixture.ChartPath));
        Assert.AreEqual("Failed", verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName).status);
        Assert.AreEqual(1, verify.Table<LR2SongDBExtended.playlist>().Count());
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>LR2受付境界の元の失敗も既存の保存・公開窓口で一度だけFailedにし、ローカル確定と両受付解放を維持します。</summary>
    [TestMethod]
    public async Task RequiredInitialization_Lr2BoundaryFailureIsSavedAndPublishedOnce()
    {
        using var fixture = new Fixture();
        using var semaphore = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var failure = new IOException("controlled LR2 acceptance failure");
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist) { BeforeQueueFailure = failure };
        runtime.Release.TrySetResult();
        int failurePublications = 0;
        fixture.Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.Lr2SongDbSyncStatusVersion)
                && fixture.Library.GetLr2SongDbSyncStatusSnapshot().Status == Lr2SongDbSyncStatusKind.Failed) { failurePublications++; }
        };
        StartupRequiredInitializationResult result;
        using (StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission))
        {
            result = await owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
                new Lr2SongDbSyncWorkflowOwner(runtime), PerformanceInteraction.Start("test_required_lr2_acceptance_failure"), accepted.Capability, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { });
        }
        Assert.AreSame(failure, result.Lr2Failure);
        Assert.AreEqual(1, failurePublications);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Failed, fixture.Library.GetLr2SongDbSyncStatusSnapshot().Status);
        using LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.SongDbPath).OpenSongDbReadOnly();
        LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Failed", row.status);
        Assert.AreEqual(failure.Message, row.last_error);
        Assert.IsNotNull(verify.Find<LR2SongDB.song>(fixture.ChartPath));
        Assert.IsTrue(Directory.EnumerateFiles(fixture.BmtPath, "*.bmt").Any());
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        Assert.IsTrue(fixture.Playlist.TryEnterPlaylistMutation(out IDisposable next));
        next.Dispose();
    }

    /// <summary>実LR2の入力取得を保持して終了し、Incompleteの一回保存・公開、取消伝播と両受付のcleanupを確認します。</summary>
    [TestMethod]
    public async Task RequiredInitialization_Lr2ShutdownCancellationPropagatesAndRetainsIncomplete()
    {
        using var fixture = new Fixture();
        using var semaphore = new SemaphoreSlim(1, 1);
        using var releaseInput = new ManualResetEventSlim();
        var inputEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist);
        runtime.Release.TrySetResult();
        int incompletePublications = 0;
        fixture.Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(BMSLibrary.Lr2SongDbSyncStatusVersion)) { return; }
            Lr2SongDbSyncStatusSnapshot status = fixture.Library.GetLr2SongDbSyncStatusSnapshot();
            if (status.Status == Lr2SongDbSyncStatusKind.Incomplete) { incompletePublications++; }
            if (status.Status == Lr2SongDbSyncStatusKind.Running && status.Stage == "input_surface")
            {
                inputEntered.TrySetResult();
                releaseInput.Wait();
            }
        };
        StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission);
        Task<StartupRequiredInitializationResult> operation = owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
            new Lr2SongDbSyncWorkflowOwner(runtime), PerformanceInteraction.Start("test_required_lr2_shutdown"), accepted.Capability, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { });
        try
        {
            await Task.WhenAny(inputEntered.Task, operation);
            if (!inputEntered.Task.IsCompleted) { await operation; Assert.Fail("実LR2の入力取得へ到達しなかった。"); }
            Assert.IsFalse(fixture.Library.OperationAdmission.TryEnter(out _));
            Assert.IsFalse(fixture.Playlist.TryEnterPlaylistMutation(out _));
            fixture.Library.RequestShutdown("controlled_required_lr2_shutdown");
            releaseInput.Set();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => operation);
        }
        finally
        {
            releaseInput.Set();
            try { await operation; }
            catch (OperationCanceledException) { }
            finally { accepted.Dispose(); }
        }
        Assert.AreEqual(1, incompletePublications);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, fixture.Library.GetLr2SongDbSyncStatusSnapshot().Status);
        using LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.SongDbPath).OpenSongDbReadOnly();
        LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        Assert.AreEqual("shutdown_interrupted", row.last_error);
        Assert.IsNotNull(verify.Find<LR2SongDB.song>(fixture.ChartPath));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        Assert.IsFalse(fixture.Library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
    }

    /// <summary>設定側から借りたL/Pを子の終端で解放せず、次の親要求では新しいファイル入力を取得します。</summary>
    [TestMethod]
    public async Task RequiredInitialization_BorrowsSettingsAuthorityAndNextRequestCapturesNewInput()
    {
        using var fixture = new Fixture();
        using var semaphore = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist);
        runtime.Release.TrySetResult();
        LibraryFileInitializationResult first;
        using (StartupRequiredOperationLease settings = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission))
        {
            using (StartupRequiredOperationLease borrowed = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
                fixture.Library.Lr2Synchronization.PlaylistOperationAdmission, settings.Capability))
            {
                first = (await owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
                    new Lr2SongDbSyncWorkflowOwner(runtime), PerformanceInteraction.Start("test_required_startup"), borrowed.Capability, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { })).FileResult;
            }
            Assert.IsFalse(fixture.Library.OperationAdmission.TryEnter(out _));
            Assert.IsFalse(fixture.Playlist.TryEnterPlaylistMutation(out _));
            settings.Capability.Validate(fixture.Library.OperationAdmission);
        }
        DateTime previousFileTime = File.GetLastWriteTimeUtc(fixture.ChartPath);
        File.WriteAllText(fixture.ChartPath, "#TITLE changed\n#BPM 120\n#00111:01\n");
        File.SetLastWriteTimeUtc(fixture.ChartPath, previousFileTime.AddSeconds(2));
        using StartupRequiredOperationLease next = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission);
        StartupRequiredInitializationResult second = await owner.ReinitializeAsync(fixture.Library, fixture.Playlist,
            new Lr2SongDbSyncWorkflowOwner(runtime), next.Capability, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { });
        Assert.AreNotSame(first, second.FileResult);
        Assert.IsTrue(second.FileResult.CommittedBmsPaths.Contains(fixture.ChartPath));
        Assert.AreEqual("changed", fixture.Library.BmsCharts.Single().Title);
    }

    /// <summary>並列に開始した実見出し読込みが失敗しても、保持中の実走査とcleanupの終端までは両受付を解放しません。</summary>
    [TestMethod]
    public async Task RequiredInitialization_HeaderFailureJoinsStartedFileTaskBeforeReleasingAdmissions()
    {
        using var fixture = new Fixture();
        using var semaphore = new SemaphoreSlim(1, 1);
        using var releaseFiles = new ManualResetEventSlim();
        var filesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headerFailure = new IOException("header read failed");
        fixture.Scanner.ScanObserved = () => { filesEntered.TrySetResult(); releaseFiles.Wait(); };
        fixture.PlaylistScheduler.BeforeInvoke = () => { headersEntered.TrySetResult(); throw headerFailure; };
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist);
        StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission);
        Task<StartupRequiredInitializationResult> operation = owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
            new Lr2SongDbSyncWorkflowOwner(runtime), PerformanceInteraction.Start("test_required_startup"), accepted.Capability, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { });
        try
        {
            await Task.WhenAny(Task.WhenAll(filesEntered.Task, headersEntered.Task), operation);
            if (!filesEntered.Task.IsCompleted || !headersEntered.Task.IsCompleted) { await operation; Assert.Fail("両独立読取りへ到達しませんでした。"); }
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsFalse(fixture.Library.OperationAdmission.TryEnter(out _));
            Assert.IsFalse(fixture.Playlist.TryEnterPlaylistMutation(out _));
            releaseFiles.Set();
            IOException thrown = await Assert.ThrowsExceptionAsync<IOException>(() => operation);
            Assert.AreSame(headerFailure, thrown);
            Assert.IsFalse(runtime.Entered.Task.IsCompleted);
        }
        finally
        {
            releaseFiles.Set();
            try { await operation; }
            catch (IOException exception) when (ReferenceEquals(exception, headerFailure)) { }
            finally { accepted.Dispose(); }
        }
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        Assert.IsTrue(fixture.Playlist.TryEnterPlaylistMutation(out IDisposable next));
        next.Dispose();
    }

    /// <summary>単独動作でも保存済みBMTを完了し、LR2の不要段階とネットワーク待機を起動条件にしません。</summary>
    [TestMethod]
    public async Task RequiredInitialization_StandaloneWritesSavedBmtWithoutLr2Request()
    {
        using var fixture = new Fixture(lr2Enabled: false);
        using var semaphore = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist);
        runtime.Release.TrySetResult();
        using StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission);
        StartupRequiredInitializationResult result = await owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
            new Lr2SongDbSyncWorkflowOwner(runtime), PerformanceInteraction.Start("test_required_startup"), accepted.Capability, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { });
        Assert.IsNull(result.Lr2Failure);
        Assert.AreEqual(0, fixture.Library.Lr2SongDbSyncRequestedVersion);
        Assert.IsTrue(Directory.EnumerateFiles(fixture.BmtPath, "*.bmt", SearchOption.AllDirectories)
            .Any(path => ReadBmtText(path).Contains(fixture.Sha256, StringComparison.Ordinal)));
    }

    /// <summary>同じCompleted署名では全体LR2を増やさず、欠けた保存済み表出力とBMTを同じ必須手続きで修復します。</summary>
    [TestMethod]
    public async Task RequiredInitialization_CompletedSignatureSkipsWholeLr2AndStillRepairsLocalOutputs()
    {
        using var fixture = new Fixture(outputOutsideScanRoot: true);
        using var semaphore = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(semaphore);
        var runtime = new GatedRuntime(fixture.Library, fixture.Playlist);
        runtime.Release.TrySetResult();
        var lr2 = new Lr2SongDbSyncWorkflowOwner(runtime);
        using (StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission))
        {
            StartupRequiredInitializationResult first = await owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
                lr2, PerformanceInteraction.Start("test_required_startup"), accepted.Capability, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { });
            Assert.IsNull(first.Lr2Failure);
        }
        int requestedVersion = fixture.Library.Lr2SongDbSyncRequestedVersion;
        Assert.IsTrue(requestedVersion > 0);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, fixture.Library.GetLr2SongDbSyncStatusSnapshot().Status);
        string generatedFolder = Directory.EnumerateFiles(fixture.OutputDirectory, "*.lr2folder", SearchOption.AllDirectories).First();
        File.Delete(generatedFolder);
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(fixture.BmtPath)!, "*.bmt", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
        using (StartupRequiredOperationLease accepted = owner.AcquireRequiredOperation(fixture.Library.OperationAdmission,
            fixture.Library.Lr2Synchronization.PlaylistOperationAdmission))
        {
            StartupRequiredInitializationResult second = await owner.InitializeRequiredAsync(fixture.Library, fixture.Playlist,
                lr2, PerformanceInteraction.Start("test_required_startup"), accepted.Capability, leapYearRepairApproval: new(fixture.SongDbPath), repairNotificationObserver: _ => { });
            Assert.IsNull(second.Lr2Failure);
        }
        Assert.AreEqual(requestedVersion, fixture.Library.Lr2SongDbSyncRequestedVersion);
        Assert.IsTrue(File.Exists(generatedFolder));
        Assert.IsTrue(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.BmtPath)!, "*.bmt", SearchOption.AllDirectories)
            .Any(path => ReadBmtText(path).Contains(fixture.Sha256, StringComparison.Ordinal)));
    }

    private static string ReadBmtText(string path)
    {
        using FileStream file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    private sealed class GatedRuntime : ILr2SongDbSyncWorkflowRuntime
    {
        private readonly BmsLr2SongDbSyncWorkflowRuntime actual;
        internal GatedRuntime(BMSLibrary library, BMSPlaylist playlist)
            => actual = new(() => library, () => playlist, () => true);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LibraryFileInitializationResult? Input { get; private set; }
        internal Lr2SongDbSyncPreparedDataSurface? PreparedSurface { get; private set; }
        internal Exception? BeforeQueueFailure { get; init; }
        public bool IsLr2ModeEnabled => true;
        public bool IsLibraryAvailable => true;
        public async Task<bool> QueueAsync(string reason, bool force, bool prepareGeneratedData = false, bool allowIncompleteToQueue = true,
            LibraryFileInitializationResult? initializationResult = null, LibraryFileMutationCapability? capability = null,
            bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability? playlistCapability = null,
            Lr2SongDbSyncPreparedDataSurface? preparedSurface = null, OperationProgressRequest? originatingRequest = null, BmsLibraryOptionsSnapshot? optionsSnapshot = null)
        {
            Input = initializationResult;
            PreparedSurface = preparedSurface;
            Entered.TrySetResult();
            await Release.Task;
            if (BeforeQueueFailure != null) { throw BeforeQueueFailure; }
            return await actual.QueueAsync(reason, force, prepareGeneratedData, allowIncompleteToQueue, initializationResult,
                capability, acceptedBackground, includeBuiltinGeneratedData, playlistCapability, preparedSurface, originatingRequest, optionsSnapshot);
        }
        public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason,
            LibraryFileMutationCapability? capability = null, LibraryFileMutationCapability? playlistCapability = null)
            => actual.SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(reason, capability, playlistCapability);
    }

    /// <summary>非WPFモデルの実collection適用へ、到達・失敗を観測する保持点だけを接続します。</summary>
    private sealed class HeaderPublicationScheduler : IUiScheduler
    {
        private readonly TestUiScheduler actual = new(() => null);
        internal Action? BeforeInvoke { get; set; }
        public bool IsAvailable => actual.IsAvailable;
        public bool CanExecuteInline => actual.CanExecuteInline;
        public bool CheckAccess() => actual.CheckAccess();
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => actual.Schedule(action, priority);
        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        { BeforeInvoke?.Invoke(); actual.Invoke(action, priority); }
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        { BeforeInvoke?.Invoke(); return actual.Invoke(action, priority); }
        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => actual.InvokeAsync(action, priority);
        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => actual.InvokeAsync(action, priority);
    }

    /// <summary>要求ごとに今回の物理入力を捕捉し、実Scanの保持点だけをfixtureが制御します。</summary>
    private sealed class FreshChartFileScanner(string chartPath, IReadOnlyList<string> roots) : IChartFileScanner
    {
        internal Action? ScanObserved { get; set; }
        public ChartScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> chartExtensions,
            bool verboseLog = false, bool includeTextSurface = true, bool includeDirectorySurface = false)
        {
            ScanObserved?.Invoke();
            return CapturedChartFileScanner.FromFixture([chartPath],
                roots.ToDictionary(root => root, _ => (IEnumerable<string>)Array.Empty<string>(), StringComparer.OrdinalIgnoreCase), roots)
                .Scan(rootDirectories, chartExtensions, verboseLog, includeTextSurface, includeDirectorySurface);
        }
    }

    /// <summary>専用FSで今回の探索面を取得します。EverythingサービスとDLLを前提にせず、出力後の物理結果も実際に読みます。</summary>
    private sealed class FixtureRootFileEnumerator : IRootFileEnumerator
    {
        public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories,
            IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
        {
            string[] roots = [.. rootDirectories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)];
            string[] files = [.. roots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            string[] directories = [.. roots.Concat(roots.SelectMany(root => Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            var result = new RootFileEnumerationResult { Success = true, IsComplete = true, BackendName = "fixture_fs" };
            foreach (RootFileEnumerationGroup group in groups)
            {
                result.InitializeGroup(group.Name);
                foreach (string path in group.IncludeDirectories ? directories : files)
                {
                    if (group.ExcludedDirectories.Any(excluded => CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(path, excluded))) { continue; }
                    if (!group.IncludeDirectories && !group.IncludeAllFiles
                        && !group.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) { continue; }
                    result.AddEntry(group.Name, new RootFileEnumerationEntry(path,
                        group.IncludeDirectories ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path)));
                }
            }
            return result;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabaseScope scope = TestDatabaseScope.Create();
        internal Fixture(bool blockBmtOutput = false, bool failLr2 = false, bool lr2Enabled = true, bool outputOutsideScanRoot = false, bool withSavedScore = false)
        {
            string root = Path.Combine(scope.DirectoryPath, "Songs");
            Directory.CreateDirectory(root);
            ChartPath = Path.Combine(root, "chart.bms");
            File.WriteAllText(ChartPath, "#TITLE saved chart\n#BPM 120\n#00111:01\n");
            ChartFileSnapshot content = ChartFileContentReader.ReadSnapshot(ChartPath);
            Sha256 = content.Sha256;
            string normalOutputBase = Path.Combine(scope.DirectoryPath, "#Output");
            string rootOutputBase = Path.Combine(scope.DirectoryPath, "RootOutput");
            OutputDirectory = outputOutsideScanRoot ? normalOutputBase : Path.Combine(rootOutputBase, "Saved");
            Directory.CreateDirectory(OutputDirectory);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);
            BmtPath = Path.Combine(scope.DirectoryPath, "Bmt", "tables");
            new BmsLibraryDbGateway(SongDbPath).EnsureAppOwnedSchema();
            PlaylistPersistenceRepository.EnsureSchema(SongDbPath);
            var table = new BMSTable { playlist_id = 9901, name = "Saved", symbol = "S", Output_dir = "Saved", is_root_folder = !outputOutsideScanRoot, Folder_order = ["Folder"] };
            if (blockBmtOutput)
            {
                // 投影開始後の実ファイル置換だけを失敗させ、開始と失敗終端の要求対応を検証します。
                Directory.CreateDirectory(Path.Combine(BmtPath, BmtTableExportService.CreatePlaylistExportMetadata(table).FileName));
            }
            BMSTableEntry entry = BmsPlaylistTestSupport.CreateEntry(content.Md5, "Folder");
            entry.playlist_id = table.playlist_id;
            using (var setup = new LR2SongDBExtended(SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                if (failLr2)
                {
                    setup.Execute("CREATE TRIGGER fail_lr2 BEFORE INSERT ON lr2_song_db_sync_status WHEN NEW.status = 'Running' BEGIN SELECT RAISE(ABORT, 'lr2-only-failure'); END;");
                }
            }
            string configPath = Path.Combine(scope.DirectoryPath, "LR2files", "Config", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, "<config><system><customfolder>0</customfolder><titleflash>0</titleflash></system><jukebox><path>"
                + System.Security.SecurityElement.Escape(root + Path.DirectorySeparatorChar) + "</path></jukebox></config>");
            var config = new LR2Config(configPath);
            string[] scanRoots = outputOutsideScanRoot ? [root] : [root, OutputDirectory];
            config.SetBMSSearchDirectories(scanRoots);
            config.Save();
            string? scorePath = null;
            if (withSavedScore)
            {
                scorePath = Path.Combine(scope.DirectoryPath, "score.db");
                using var scoreDb = new LR2ScoreDBExtended(scorePath);
                scoreDb.CreateTable<LR2ScoreDB.player>();
                scoreDb.CreateTable<LR2ScoreDB.score>();
                scoreDb.Insert(new LR2ScoreDB.player { id = "required-startup", irid = 0 });
                scoreDb.Insert(new LR2ScoreDB.score { hash = content.Md5, perfect = 123 });
            }
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = lr2Enabled,
                LR2RootPath = scope.DirectoryPath,
                LR2CustomFolderOutputBaseDir = normalOutputBase,
                LR2CustomFolderOutputBaseDirRootType = rootOutputBase,
                ScanBmsFilesOnStartup = true
            };
            Scanner = new FreshChartFileScanner(ChartPath, scanRoots);
            Library = new TestBmsLibrary(SongDbPath, () => config, scorePath, null, () => options, TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: Scanner, rootFileEnumerator: new FixtureRootFileEnumerator(), settings: settings, uiScheduler: new TestUiScheduler(() => null))
            { SearchTargets = [.. scanRoots] };
            // 後続保守はこのfixtureで開始しない。必須処理がschedulerを待つとテストが完了しない。
            Library.StartupBackgroundTaskScheduler = (_, _, _, _) => { OptionalRegistrations++; return true; };
            Playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(Library), SongDbPath,
                () => new CustomFolderOutputSettingsSnapshot
                {
                    OperationModeLR2DB = lr2Enabled,
                    LR2RootPath = scope.DirectoryPath,
                    LR2CustomFolderOutputBaseDir = normalOutputBase,
                    LR2CustomFolderOutputBaseDirRootType = rootOutputBase
                }, () => config,
                () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings),
                () => new BeatorajaBmtOptionsSnapshot
                {
                    EnableBeatorajaBmtOutput = true,
                    BeatorajaBmtTablePath = BmtPath,
                    BeatorajaBmtHashOutputMode = "PreferSha256Only"
                }, settings: settings, uiScheduler: PlaylistScheduler);
            Playlist.StartupBackgroundTaskScheduler = (_, _, _, _) => { OptionalRegistrations++; return true; };
        }
        internal int OptionalRegistrations { get; private set; }
        internal FreshChartFileScanner Scanner { get; }
        internal HeaderPublicationScheduler PlaylistScheduler { get; } = new();
        internal string SongDbPath => scope.SongDbPath;
        internal string ChartPath { get; }
        internal string Sha256 { get; }
        internal string OutputDirectory { get; }
        internal string BmtPath { get; }
        internal TestBmsLibrary Library { get; }
        internal TestBmsPlaylist Playlist { get; }
        public void Dispose()
        {
            Playlist.RequestShutdown("test_cleanup");
            Library.RequestShutdown("test_cleanup");
            scope.Dispose();
        }
    }
}
