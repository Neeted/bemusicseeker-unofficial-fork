using System.IO;
using System.Reflection;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryMaintenanceServiceTestSupport;
using static BeMusicSeeker.TestSupport.Components.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryMaintenanceServiceTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 現在の所持健全性索引から不足譜面の実パスを返し、保守対象解決が現在索引から外れる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void GetChartsNeedResourceFix_UsesCurrentResourceHealthIndexSnapshot()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            ChartFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            active = active with { Path = @"C:\Library\active.bms" };
            active = ChartFileProjection.WithMaintenance(active, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = active.Path, Hash = active.Md5, WavFilesDefined = 2, WavFilesExisting = 1 });
            ChartFile activeChart = (active);
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = [active],
                BmsonCharts = []
            };
            EnsureCurrentResourceHealthIndex(library);

            List<ChartFile> result = library.GetChartsNeedResourceFix(null);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(activeChart.Path, result[0].Path);
        });
    }

    /// <summary>
    /// 所持hashだけを温めたcold健全性読取りは実保存行writerの保持中にreader待機へ入り、解放後に不足対象を返して保護なしの読取りを検出する。
    /// </summary>
    /// <remarks>実所持の保存行writerとreader待機を観測する。10秒は保護中の到達観測の期限で、通常完了の固定待機ではない。</remarks>
    [TestMethod]
    public void ChartFilesNeedResourceFix_WaitsForCatalogStorageWriterBeforeColdResourceHealthRead()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "missing-resource.bms");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = chartPath };
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 1, WavFilesExisting = 0, FilesWarningIgnored = false });
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = [file],
                BmsonCharts = []
            };

            // 所持 collection だけを通常の hash 読取りで温め、resource health は cold のままにする。
            _ = library.GetOwnedChartHashIndexSnapshot();

            ReaderWriterLockSlimWrapper storageWriteGate = RegularChartListOwnerTestSupport.GetCatalogStorageRowsWriteGate(library);
            List<ChartFile>? result = null;
            Task worker = null!;
            try
            {
                using (storageWriteGate.GetWriterGuard())
                {
                    worker = RegularChartListOwnerTestSupport.StartLongRunning(
                        () => result = [.. library.ChartFilesNeedResourceFix]);
                    bool readerWaitOrEarlyCompletion = SpinWait.SpinUntil(
                        () => storageWriteGate.WaitingReadCount > 0 || worker.IsCompleted,
                        TimeSpan.FromSeconds(10));

                    Assert.IsTrue(
                        readerWaitOrEarlyCompletion,
                        "resource health cold read did not reach the storage reader or complete within the watchdog.");
                    Assert.IsFalse(
                        worker.IsCompleted,
                        "resource health cold read completed while the catalog storage writer was held.");
                }

                worker.GetAwaiter().GetResult();
                Assert.IsNotNull(result);
                Assert.AreEqual(1, result!.Count);
                Assert.AreEqual(ChartFileKind.Bms, result[0].Kind);
                Assert.AreEqual(chartPath, result[0].Path);
            }
            finally
            {
                if (worker != null)
                {
                    worker.GetAwaiter().GetResult();
                }
            }
        });
    }
    /// <summary>
    /// 同一MD5でも大小文字だけ異なる正本パスの不足投影を別に保持し、MD5は大小文字を区別せずパスは完全一致で解決して誤った対象共有を検出する。
    /// </summary>
    [TestMethod]
    public void GetChartsNeedResourceFix_CaseOnlyExactPathsKeepIndependentResourceHealthProjections()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            const string sharedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            ChartFile upperCasePath = CreateFile(sharedHash);
            upperCasePath = upperCasePath with { Path = @"C:\Library\Case.bms" };
            upperCasePath = ChartFileProjection.WithMaintenance(upperCasePath, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = upperCasePath.Path, Hash = sharedHash, WavFilesDefined = 2, WavFilesExisting = 1 });
            ChartFile lowerCasePath = CreateFile(sharedHash);
            lowerCasePath = lowerCasePath with { Path = @"c:\library\case.bms" };
            lowerCasePath = ChartFileProjection.WithMaintenance(lowerCasePath, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lowerCasePath.Path, Hash = sharedHash, BgaFilesDefined = 3, BgaFilesExisting = 1 });
            var library = new TestBmsLibrary(databaseScope, songDbPath)
            {
                BmsCharts = [upperCasePath, lowerCasePath],
                BmsonCharts = []
            };

            ResourceHealthIndexSnapshot snapshot = library.GetResourceHealthIndexSnapshotForView("r2c_exact_full");
            List<ChartFile> result = library.GetChartsNeedResourceFix(null);
            ResourceHealthWarningProjection upperProjection = snapshot.GetProjection(
                ChartFileKind.Bms,
                upperCasePath.Path,
                sharedHash);
            ResourceHealthWarningProjection lowerProjection = snapshot.GetProjection(
                ChartFileKind.Bms,
                lowerCasePath.Path,
                sharedHash);

            Assert.AreEqual(2, snapshot.TargetCount);
            Assert.IsTrue(new HashSet<string>(result.Select(chart => chart.Path), StringComparer.Ordinal).SetEquals(
                [upperCasePath.Path, lowerCasePath.Path]));
            Assert.IsTrue(upperProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(upperProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
            Assert.IsTrue(lowerProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
            Assert.IsFalse(lowerProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(snapshot.GetProjection(
                ChartFileKind.Bms,
                upperCasePath.Path,
                sharedHash.ToUpperInvariant()).Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(snapshot.GetProjection(
                ChartFileKind.Bms,
                @"C:\Library\CASE.bms",
                sharedHash).HasIssues);
        });
    }

    /// <summary>
    /// 実選択保守の保存値を同じ所持現在値へ反映して警告・保守表示を一回通知し、保守markerだけなら集合版とSourceChangedを進めず、不要な全体更新を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・所持現在値・表示通知の接続を確認するため独立したSQLite DBを用いる。</remarks>
    [TestMethod]
    public void RescanResourceHealthCharts_PublishesMaintenanceRefreshThroughOwnedDispatcher()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE maintenance\r\n", Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot
            {
                Origin = MaintenanceInfoOrigin.Calculated,
                Path = file.Path,
                Hash = file.Md5,
                Encoding = "unknown"
            });
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            SetStorageRows(library, [file], []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int ownedVersion = library.CatalogRevision;
            int refreshNotificationChanged = 0;
            System.ComponentModel.PropertyChangedEventHandler handler = delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            library.PropertyChanged += handler;
            try
            {
                MaintenanceWorkflowResult result = library.RescanResourceHealthCharts([file]);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
                Assert.IsTrue(result.HasUpdates);
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
                Assert.AreEqual(1, refreshNotificationChanged);
                Assert.AreEqual(ownedVersion + 1, library.CatalogRevision);
                Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.AreEqual("unknown", file.ResourceHealthMaintenanceSnapshot.Encoding);
                Assert.AreEqual("shift_jis", OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library).Collection
                    .ResolveCurrentChart(LibraryChartRef.FromChartFile(file)).ResourceHealthMaintenanceSnapshot.Encoding);
                ChartFile current = GetOwnedCollectionOwner(library).Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(file));
                ResourceHealthIndexSnapshot index = library.GetResourceHealthIndexSnapshotForView("same-maintenance-before-force");
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    int beforeRepeat = library.NormalLibraryRefreshNotificationVersion;
                    MaintenanceWorkflowResult unchanged = library.RescanResourceHealthCharts([current]);
                    Assert.IsFalse(unchanged.HasUpdates);
                    Assert.AreEqual(0, unchanged.MaintenanceInfoUpsertCount);
                    Assert.AreEqual(0, unchanged.SongMetadataUpdateCount);
                    Assert.AreSame(index, library.TryGetCurrentResourceHealthIndexSnapshotForView());
                    NormalLibraryRefreshNotificationBatch noChange = library.GetNormalLibraryRefreshNotificationsAfter(beforeRepeat);
                    Assert.IsFalse(noChange.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
                    Assert.IsFalse(noChange.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                    Assert.AreEqual(ownedVersion + 1, library.CatalogRevision);
                    Assert.AreSame(current.Token, GetOwnedCollectionOwner(library).Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(current)).Token);
                }
                Assert.AreSame(index, library.GetResourceHealthIndexSnapshotForView("same-maintenance-after-force"));
            }
            finally
            {
                library.PropertyChanged -= handler;
                library.RequestShutdown("maintenance_marker_only_test");
            }
        });
    }

    /// <summary>
    /// 背景16・128件がある二対象の資源追加を順に局所保守し、取得済みsnapshotを保って現在投影だけ更新し、全件警告走査やSourceChangedを起こす誤りを検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・所持現在値・表示通知の接続を確認するため独立したSQLite DBを用いる。</remarks>
    // 16件と128件の背景を使い、二対象の更新を全件列挙で代用する反例を見分ける。
    [TestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void RescanResourceHealthCharts_UpdatesCurrentResourceHealthIndexByDelta(int backgroundCount)
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string firstPath = Path.Combine(root, "target-first.bms");
            string secondPath = Path.Combine(root, "target-second.bms");
            string firstResourcePath = Path.Combine(root, "first.wav");
            string secondResourcePath = Path.Combine(root, "second.wav");
            File.WriteAllText(
                firstPath,
                "#PLAYER 1\r\n#TITLE target first\r\n#WAV01 first.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            File.WriteAllText(
                secondPath,
                "#PLAYER 1\r\n#TITLE target second\r\n#WAV01 second.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstPath));
            ChartFile second = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondPath));
            first = ChartFileProjection.WithMaintenance(first, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = first.Path, Hash = first.Md5, WavFilesDefined = 1, WavFilesExisting = 0 });
            second = ChartFileProjection.WithMaintenance(second, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = second.Path, Hash = second.Md5, WavFilesDefined = 1, WavFilesExisting = 0 });
            ChartFile firstChart = (first);
            ChartFile secondChart = (second);
            List<ChartFile> files = [first, second];
            for (int index = 0; index < backgroundCount; index++)
            {
                ChartFile background = CreateFile(
                    index.ToString("x8") + new string('0', 24));
                background = background with { Path = Path.Combine(root, "background-" + index + ".bms") };
                background = ChartFileProjection.WithMaintenance(background, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = background.Path, Hash = background.Md5, WavFilesDefined = 1, WavFilesExisting = 0 });
                files.Add(background);
            }
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            SetStorageRows(library, files, []);
            EnsureCurrentResourceHealthIndex(library);
            List<string> storeWork = [];
            ResourceHealthIndexSnapshot beforeFirst = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            beforeFirst.StoreWorkObserver = operation => storeWork.Add(operation);
            int initialTargetCount = beforeFirst.TargetCount;
            int ownedVersion = library.CatalogRevision;
            Assert.AreEqual(backgroundCount + 2, initialTargetCount);
            Assert.IsTrue(beforeFirst.GetProjection(firstChart).HasIssues);
            Assert.IsTrue(beforeFirst.GetProjection(secondChart).HasIssues);

            File.WriteAllBytes(firstResourcePath, [1, 2, 3]);
            int firstNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            MaintenanceWorkflowResult firstResult = library.RescanResourceHealthCharts([firstChart]);
            ResourceHealthIndexSnapshot afterFirst = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            ResourceHealthIndexSnapshot firstGetter = library.GetResourceHealthIndexSnapshotForView("test_rescan_first_getter");
            List<ChartFile> firstFixTargets = library.GetChartsNeedResourceFix([firstChart]);
            NormalLibraryRefreshNotificationBatch firstBatch = library.GetNormalLibraryRefreshNotificationsAfter(firstNotificationVersion);

            Assert.IsTrue(firstResult.HasUpdates);
            Assert.AreEqual(initialTargetCount, afterFirst.TargetCount);
            Assert.IsFalse(afterFirst.GetProjection(firstChart).HasIssues);
            Assert.IsTrue(afterFirst.GetProjection(secondChart).HasIssues);
            Assert.AreEqual(afterFirst.Version, firstGetter.Version);
            Assert.IsFalse(firstGetter.GetProjection(firstChart).HasIssues);
            Assert.AreEqual(0, firstFixTargets.Count);
            Assert.IsTrue(firstBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(firstBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.AreEqual(ownedVersion + 1, library.CatalogRevision);
            Assert.IsFalse(firstBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(beforeFirst.GetProjection(firstChart).HasIssues);

            beforeFirst.StoreWorkObserver = null;
            afterFirst.StoreWorkObserver = operation => storeWork.Add(operation);
            File.WriteAllBytes(secondResourcePath, [4, 5, 6]);
            int secondNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            MaintenanceWorkflowResult secondResult = library.RescanResourceHealthCharts([secondChart]);
            ResourceHealthIndexSnapshot afterSecond = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            ResourceHealthIndexSnapshot thirdGetter = library.GetResourceHealthIndexSnapshotForView("test_rescan_second_getter");
            List<ChartFile> secondFixTargets = library.GetChartsNeedResourceFix([secondChart]);
            NormalLibraryRefreshNotificationBatch secondBatch = library.GetNormalLibraryRefreshNotificationsAfter(secondNotificationVersion);

            Assert.IsTrue(secondResult.HasUpdates);
            Assert.AreEqual(initialTargetCount, afterSecond.TargetCount);
            Assert.IsFalse(afterSecond.GetProjection(firstChart).HasIssues);
            Assert.IsFalse(afterSecond.GetProjection(secondChart).HasIssues);
            Assert.AreEqual(afterSecond.Version, thirdGetter.Version);
            Assert.AreEqual(0, secondFixTargets.Count);
            Assert.IsTrue(secondBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(secondBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.AreEqual(ownedVersion + 2, library.CatalogRevision);
            Assert.IsFalse(secondBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(afterFirst.GetProjection(secondChart).HasIssues);
            Assert.IsTrue(storeWork.Contains("entry_lookup"));
            Assert.IsFalse(storeWork.Contains("warning_sequence_enumeration"));
            Assert.IsFalse(storeWork.Contains("warning_sequence_entry_visited"));
        });
    }

    /// <summary>
    /// 実全所持保守で二件の保存・現在値・終端進捗を確定し、更新購読例外でも一回通知した結果を保持して、先行通知失敗による後続反映欠落を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・所持現在値・表示通知の接続を確認するため独立したSQLite DBを用いる。</remarks>
    [TestMethod]
    public void RescanAllOwnedChartMaintenance_PreservesBothCurrentValuesAfterRefreshSubscriberFailure()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string firstPath = Path.Combine(root, "first.bms");
            string secondPath = Path.Combine(root, "second.bms");
            File.WriteAllText(
                firstPath,
                "#PLAYER 1\r\n#TITLE first\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            File.WriteAllText(
                secondPath,
                "#PLAYER 1\r\n#TITLE second\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);

            ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            first = first with { Path = firstPath };
            first = first with { Resources = TestChartResources.ReplaceAudio(first.Resources, ["missing.wav"]) };
            first = ChartFileProjection.WithMaintenance(first, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = first.Path, Hash = first.Md5, WavFilesDefined = 0, WavFilesExisting = 0, BgaFilesDefined = 0, BgaFilesExisting = 0, MovieFilesDefined = 0, MovieFilesExisting = 0, StagefileDefined = false, StagefileExisting = false, BannerDefined = false, BannerExisting = false, BackbmpDefined = false, BackbmpExisting = false, Encoding = "shift_jis", EncodingFixed = true });
            ChartFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            second = second with { Path = secondPath };
            second = second with { Resources = TestChartResources.ReplaceAudio(second.Resources, ["missing.wav"]) };
            second = ChartFileProjection.WithMaintenance(second, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = second.Path, Hash = second.Md5, WavFilesDefined = 0, WavFilesExisting = 0, BgaFilesDefined = 0, BgaFilesExisting = 0, MovieFilesDefined = 0, MovieFilesExisting = 0, StagefileDefined = false, StagefileExisting = false, BannerDefined = false, BannerExisting = false, BackbmpDefined = false, BackbmpExisting = false, Encoding = "shift_jis", EncodingFixed = true });

            var library = new TestBmsLibrary(databaseScope, songDbPath);
            SetStorageRows(library, [first, second], []);
            int handledVersion = library.NormalLibraryRefreshNotificationVersion;
            int failedNotificationCount = 0;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    Interlocked.Increment(ref failedNotificationCount);
                    throw new InvalidOperationException("maintenance refresh subscriber failure");
                }
            };
            var progress = new List<MaintenanceWorkflowProgress>();

            MaintenanceWorkflowResult result = library.RescanAllOwnedChartMaintenance(progress.Add);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, Volatile.Read(ref failedNotificationCount));
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledVersion);
            CollectionAssert.AreEquivalent(new[] { firstPath, secondPath }, batch.ChangedCharts.Select(chart => chart.Path).ToArray());
            Assert.IsTrue(library.BmsCharts.All(chart => chart.ResourceHealthMaintenanceSnapshot.WavFilesExisting == 0));
            Assert.IsTrue(progress.Any(update => update?.IsCompleted == true));
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", firstPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", secondPath));
        });
    }

    /// <summary>
    /// 現在健全性索引がない選択強制検査は選択譜面の不足だけを返し、全所持索引を構築せず、少数要求による全件準備を検出する。
    /// </summary>
    [TestMethod]
    public void GetChartsNeedResourceFix_SubsetForceUpdateDoesNotBuildFullOwnedIndexWhenCurrentIndexUnavailable()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string targetPath = Path.Combine(root, "target-missing.bms");
            string unrelatedPath = Path.Combine(root, "unrelated-missing.bms");
            File.WriteAllText(targetPath, "#PLAYER 1\r\n#TITLE target\r\n#WAV01 missing.wav\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(unrelatedPath, "#PLAYER 1\r\n#TITLE unrelated\r\n#WAV01 missing.wav\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile target = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            target = target with { Path = targetPath };
            ChartFile unrelated = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            unrelated = unrelated with { Path = unrelatedPath };
            ChartFile targetChart = (target);
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            SetStorageRows(library, [target, unrelated], []);
            library.BmsCharts = new List<ChartFile> { target, unrelated };

            List<ChartFile> result = library.GetChartsNeedResourceFix([targetChart], forceUpdate: true);
            ResourceHealthIndexSnapshot currentSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(targetPath, result[0].Path);
            Assert.AreEqual(0, currentSnapshot.TargetCount);
        });
    }

    /// <summary>
    /// 所持譜面の不足を無視すると有効対象から除外対象へ移し、警告表示だけを一回通知して保守・Source更新の誤混入を検出する。
    /// </summary>
    [TestMethod]
    public void SetChartResourceWarningsIgnored_PublishesWarningRefreshThroughOwnedDispatcher()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = @"C:\Library\warning.bms" };
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
            ChartFile chart = (file);
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            SetStorageRows(library, [file], []);
            EnsureCurrentResourceHealthIndex(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            library.SetChartResourceWarningsIgnored([chart], unset: false);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            ResourceHealthIndexSnapshot updatedSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.AreEqual(1, refreshNotificationChanged);
            Assert.AreEqual(0, updatedSnapshot.ActiveTargets.Count);
            Assert.AreEqual(1, updatedSnapshot.IgnoredTargets.Count);
        });
    }

    /// <summary>
    /// 保存保守のhydrate要求を現在schedulerへ渡し、同じ要求識別子で開始・完了を報告し、版と稼働状態を通知して、要求の混線や未終端を検出する。
    /// </summary>
    [TestMethod]
    public void DeferredMaintenanceHydration_UsesCurrentSchedulerAndPublishesState()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            var execution = new List<(OperationProgressRequest Request, bool Running)>();
            library.StartupProgressRequestFactory = (name, version) => new(3, 19, name, version);
            library.StartupRequestProgressReporter = (request, running) => execution.Add((request, running));
            library.AttachStartupRequestProgressSources();
            var changedProperties = new List<string>();
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                changedProperties.Add(args.PropertyName!);
            };
            bool schedulerInvoked = false;
            library.StartupBackgroundTaskScheduler = delegate (string _, string _, string _, Func<System.Threading.Tasks.Task> work)
            {
                schedulerInvoked = true;
                work().GetAwaiter().GetResult();
                return true;
            };

            MethodInfo? queueMethod = typeof(BMSLibrary).GetMethod(
                "QueueDeferredMaintenanceHydration",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(queueMethod);
            queueMethod!.Invoke(library, ["test_scheduler"]);

            Assert.IsTrue(schedulerInvoked);
            var expectedRequest = new OperationProgressRequest(3, 19, "maintenance_hydration", 1);
            Assert.AreEqual(expectedRequest, library.MaintenanceHydrationProgressRequest);
            CollectionAssert.AreEqual(new[] { (expectedRequest, true), (expectedRequest, false) }, execution);
            Assert.AreEqual(1, library.MaintenanceHydrationRequestedVersion);
            Assert.AreEqual(1, library.MaintenanceHydrationCompletedVersion);
            Assert.IsFalse(library.MaintenanceHydrationRunning);
            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.MaintenanceHydrationRequestedVersion));
            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.MaintenanceHydrationCompletedVersion));
            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.MaintenanceHydrationRunning));
        });
    }

    /// <summary>
    /// 保存保守行を現在所持へ反映して古い対象外行を実DBから除き、健全性索引と警告・保守通知へ接続して、保存・現在値・表示の不整合を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・所持現在値・表示通知の接続を確認するため独立したSQLite DBを用いる。</remarks>
    [TestMethod]
    public void DeferredMaintenanceHydration_AttachesRowsRemovesStaleAndPublishesResourceHealth()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string chartPath = Path.Combine(root, "hydrated.bms");
            string stalePath = Path.Combine(root, "stale.bms");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = chartPath };
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 0, WavFilesExisting = 0 });

            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.UpsertMaintenanceInfos(
            [
                new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = chartPath, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 1 },
                new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = stalePath, Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", WavFilesDefined = 1, WavFilesExisting = 0 }
            ]);

            var library = new TestBmsLibrary(databaseScope, songDbPath);
            SetStorageRows(library, [file], []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            library.StartupBackgroundTaskScheduler = delegate (string _, string _, string _, Func<System.Threading.Tasks.Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };
            MethodInfo? queueMethod = typeof(BMSLibrary).GetMethod(
                "QueueDeferredMaintenanceHydration",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(queueMethod);
            queueMethod!.Invoke(library, ["test_hydration"]);

            file = library.BmsCharts.Single();
            Assert.AreEqual(2, file.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(1, file.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            using (LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath))
            {
                Assert.AreEqual(1, verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance;").Count());
                Assert.AreEqual(chartPath, verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance;").Single().path);
            }
            ResourceHealthIndexSnapshot snapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.AreEqual(1, snapshot.TargetCount);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
        });
    }

    /// <summary>
    /// 現在索引がない選択無視指定は対象現在値だけ変更し索引を失効のまま保って、少数変更から全件索引を構築する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void SetChartResourceWarningsIgnored_InvalidatesInsteadOfFullRebuildWhenCurrentIndexUnavailable()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            ChartFile target = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            target = target with { Path = @"C:\Library\target-warning.bms" };
            target = ChartFileProjection.WithMaintenance(target, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = target.Path, Hash = target.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
            ChartFile unrelated = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            unrelated = unrelated with { Path = @"C:\Library\unrelated-warning.bms" };
            unrelated = ChartFileProjection.WithMaintenance(unrelated, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = unrelated.Path, Hash = unrelated.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
            ChartFile targetChart = (target);
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            SetStorageRows(library, [target, unrelated], []);
            library.BmsCharts = new List<ChartFile> { target, unrelated };

            library.SetChartResourceWarningsIgnored([targetChart], unset: false);

            ResourceHealthIndexSnapshot currentSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.AreEqual(0, currentSnapshot.TargetCount);
            Assert.IsTrue(library.BmsCharts.Single(chart => chart.Path == target.Path).ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
        });
    }

    /// <summary>
    /// 無視指定の後に実入力の内容更新を保存してhashを反映しても、累積通知の警告表示効果を保ち、別生産者の更新で効果を取り落とす誤りを検出する。
    /// </summary>
    [TestMethod]
    public void NormalLibraryRefreshNotificationBatch_MixedWarningProducersKeepWarningRefreshEffect()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string chartPath = Path.Combine(root, "warning.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE warning before\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            string oldMd5 = file.Md5;
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE warning after\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile chart = (file);
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            SetStorageRows(library, [file], []);
            EnsureCurrentResourceHealthIndex(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            library.SetChartResourceWarningsIgnored([chart], unset: false);
            ChartInfoInlineBuildResult result = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_warning_property_covered_digest",
                [(file)]);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.AreEqual(1, result.DigestChanges.Count);
            Assert.AreEqual(oldMd5, result.DigestChanges.Single().OldMd5);
            file = library.BmsCharts.Single();
            Assert.AreNotEqual(oldMd5, file.Md5);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
        });
    }

    /// <summary>
    /// 実所持から文字化け・修正済み・LR2非対応の専用一覧を同tokenで返し、親情報が空なだけの正常譜面を未登録へ混ぜる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ChartFileSubsets_ProjectGarbledAndLr2CompatibilityWarningRows()
    {

        WithTemporarySongDb(databaseScope, delegate (string songDbPath)
        {
            ChartFile garbled = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            garbled = garbled with { Path = @"C:\Library\garbled.bms" };
            garbled = ChartFileProjection.WithMaintenance(garbled, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = garbled.Path, Hash = garbled.Md5, Encoding = "unknown", EncodingFixed = false });
            ChartFile fixedGarbled = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            fixedGarbled = fixedGarbled with { Path = @"C:\Library\fixed.bms" };
            fixedGarbled = ChartFileProjection.WithMaintenance(fixedGarbled, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = fixedGarbled.Path, Hash = fixedGarbled.Md5, Encoding = "gb2312", EncodingFixed = true });
            ChartFile lr2Warning = CreateFile("cccccccccccccccccccccccccccccccc");
            lr2Warning = lr2Warning with { Path = @"C:\Library\lr2-warning.bms" };
            lr2Warning = ChartFileProjection.WithMaintenance(lr2Warning, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lr2Warning.Path, Hash = lr2Warning.Md5, Encoding = "shift_jis", EncodingFixed = false });
            lr2Warning = lr2Warning with { Warnings = [.. lr2Warning.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2ResourcePathTooLong), ChartWarning.Create(ChartWarningKind.Lr2ResourcePathTooLong, "resource path is too long for LR2")] };
            ChartFile parentBlankWithoutWarning = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            parentBlankWithoutWarning = parentBlankWithoutWarning with { Path = @"C:\Library\parent-blank.bms" };
            parentBlankWithoutWarning = ChartFileProjection.WithMaintenance(parentBlankWithoutWarning, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = parentBlankWithoutWarning.Path, Hash = parentBlankWithoutWarning.Md5, Encoding = "shift_jis", EncodingFixed = false });
            ChartFile registered = CreateFile("dddddddddddddddddddddddddddddddd");
            registered = registered with { Path = @"C:\Library\registered.bms" };
            registered = ChartFileProjection.WithMaintenance(registered, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = registered.Path, Hash = registered.Md5, Encoding = "shift_jis", EncodingFixed = false });
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            SetStorageRows(library, [garbled, fixedGarbled, lr2Warning, parentBlankWithoutWarning, registered], []);

            garbled = library.BmsCharts.Single(chart => chart.Path == garbled.Path);
            fixedGarbled = library.BmsCharts.Single(chart => chart.Path == fixedGarbled.Path);
            lr2Warning = library.BmsCharts.Single(chart => chart.Path == lr2Warning.Path);
            List<ChartFile> garbledCharts = [.. library.ChartFilesGarbled];
            List<ChartFile> fixedCharts = [.. library.ChartFilesGarbledFixed];
            List<ChartFile> unregisteredCharts = [.. library.ChartFilesUnregistered];

            CollectionAssert.AreEqual(new[] { garbled.Path }, garbledCharts.Select(chart => chart.Path).ToArray());
            CollectionAssert.AreEqual(new[] { fixedGarbled.Path }, fixedCharts.Select(chart => chart.Path).ToArray());
            CollectionAssert.AreEqual(new[] { lr2Warning.Path }, unregisteredCharts.Select(chart => chart.Path).ToArray());
            Assert.AreSame(garbled.Token, garbledCharts[0].Token);
            Assert.AreSame(fixedGarbled.Token, fixedCharts[0].Token);
            Assert.AreSame(lr2Warning.Token, unregisteredCharts[0].Token);
        });
    }

    private static void SetStorageRows(
        BMSLibrary library,
        IReadOnlyList<ChartFile> bmsFiles,
        IReadOnlyList<ChartFile> bmsonSongs)
    {
        library.BmsCharts = bmsFiles;
        library.BmsonCharts = bmsonSongs;
    }

    private static void EnsureCurrentResourceHealthIndex(BMSLibrary library)
    {
        _ = library.GetResourceHealthIndexSnapshotForView("test_resource_health");
    }

}
