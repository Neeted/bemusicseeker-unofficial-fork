using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SelectedChartResourceHealthWorkflowOwnerTests
{
    /// <summary>再検査storeの呼出後に一回だけ完了通知し、呼出の欠落や余計な警告表示を検出します。</summary>
    [TestMethod]
    public async Task RescanAsync_UsesStoreAndPublishesCompletionAfterSuccess()
    {
        var store = new RecordingStore();
        var dialogs = new RecordingDialogService();
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(store, dialogs);
        int completionCalls = 0;
        owner.RescanCompleted += (_, _) =>
        {
            Assert.AreEqual(1, store.RescanCalls);
            completionCalls++;
        };
        ChartResourceHealthRequest request = CreateRequest();

        SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(request);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, store.RescanCalls);
        Assert.AreEqual(1, completionCalls);
        Assert.AreEqual(0, dialogs.MessageCalls);
    }

    /// <summary>変更拒否は取消とbusy警告で返し、完了通知や成功への読み替えを検出します。</summary>
    [TestMethod]
    public async Task RescanAsync_WhenLibraryBlocksMutation_ShowsWarningWithoutRefresh()
    {
        var store = new RecordingStore { RescanResult = new MaintenanceWorkflowResult { Canceled = true } };
        var dialogs = new RecordingDialogService
        {
            MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(store, dialogs);
        int completionCalls = 0;
        owner.RescanCompleted += (_, _) => completionCalls++;

        SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(CreateRequest());

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Canceled);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(1, store.RescanCalls);
        Assert.AreEqual(0, completionCalls);
        Assert.AreEqual(1, dialogs.MessageCalls);
        StringAssert.Contains(dialogs.LastMessage, BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
    }

    /// <summary>拒否警告が表示できない場合は失敗を返し、通常取消として表示失敗を隠す誤りを検出します。</summary>
    [TestMethod]
    public async Task RescanAsync_WhenBlockedWarningCannotBeShown_ReturnsFailure()
    {
        var store = new RecordingStore { RescanResult = new MaintenanceWorkflowResult { Canceled = true } };
        var dialogs = new RecordingDialogService
        {
            MessageResult = UiDialogResult.Failed(new InvalidOperationException("dialog unavailable"))
        };
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(store, dialogs);
        int completionCalls = 0;
        owner.RescanCompleted += (_, _) => completionCalls++;

        SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(CreateRequest());

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Canceled);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(0, completionCalls);
    }

    /// <summary>再検査後の通知先例外を元の失敗として返し、成功結果だけを返す誤りを検出します。</summary>
    [TestMethod]
    public async Task RescanAsync_WhenCompletionObserverFails_ReturnsFailure()
    {
        var store = new RecordingStore();
        var failure = new InvalidOperationException("resource health presentation failed");
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(store, new RecordingDialogService());
        owner.RescanCompleted += (_, _) => throw failure;

        SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(CreateRequest());

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Canceled);
        Assert.AreSame(failure, result.Failure);
        Assert.AreEqual(1, store.RescanCalls);
    }

    /// <summary>無視・解除の要求値を保存へ渡し、保存失敗後に再試行や成功への代替を行う誤りを検出します。</summary>
    [TestMethod]
    public void SetWarningsIgnored_PassesUnsetAndDoesNotFallbackAfterFailure()
    {
        var store = new RecordingStore();
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(
            store,
            new RecordingDialogService());

        SelectedChartResourceHealthWorkflowResult ignored = owner.SetWarningsIgnored(CreateRequest());
        SelectedChartResourceHealthWorkflowResult unignored = owner.SetWarningsIgnored(CreateRequest(), unset: true);

        Assert.IsTrue(ignored.Succeeded);
        Assert.IsTrue(unignored.Succeeded);
        CollectionAssert.AreEqual(new[] { false, true }, store.UnsetValues);

        store.Failure = new IOException("warning persistence failed");
        SelectedChartResourceHealthWorkflowResult failed = owner.SetWarningsIgnored(CreateRequest());

        Assert.IsFalse(failed.Succeeded);
        Assert.IsNotNull(failed.Failure);
        CollectionAssert.AreEqual(new[] { false, true, false }, store.UnsetValues);
    }

    /// <summary>DB起動後の強制再調査は、再復号の文字列差分あり・同値とも生メタデータとmtimeだけを保存し、他のsong全列・背景行を保持します。本番owner・store・hydrate・SQLiteを通します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RescanAsync_RealStoreAfterDbLoadPreservesUnchangedSongColumns(bool matchingMetadata)
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            using var fixture = new MaintenanceMetadataSongFixture(songDbPath, matchingMetadata);
            Assert.IsTrue(ChartResourceHealthRequest.TryCreate([new ChartOperationTarget(fixture.Chart, null,
                ChartOperationSourceScope.Library, true, false, false, ChartOperationCapabilities.RunResourceHealthCheck)], out ChartResourceHealthRequest request));
            var owner = new SelectedChartResourceHealthWorkflowOwner(() => fixture.Library, new RecordingDialogService());
            SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(request);
            Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
            fixture.AssertSaved();
        });
    }

    /// <summary>
    /// DB読込み後の選択・全所持再スキャンで確定UTF-8を保持して5項目を復号し、資源増減だけならmaintenanceだけを保存します。
    /// ASCIIのShift_JISへの再判定、BOMなし非ASCIIの文字化け、hash・song対象外全列の改変、同値song更新を検出します。
    /// </summary>
    /// <remarks>実owner/storeまたは全所持入口から、実SQLiteと現在一覧への反映まで確認します。Windowは生成しません。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Rescan_FixedUtf8AfterDbLoadPreservesMetadataAndOnlyPersistsResourceChanges(bool allOwned, bool asciiMetadata)
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            using var fixture = new MaintenanceMetadataSongFixture(songDbPath, fixedEncoding: true, asciiMetadata: asciiMetadata);
            var owner = new SelectedChartResourceHealthWorkflowOwner(() => fixture.Library, new RecordingDialogService());
            async Task Rescan()
            {
                if (allOwned)
                {
                    MaintenanceWorkflowResult result = await Task.Run(() => fixture.Library.RescanAllOwnedChartMaintenance());
                    Assert.IsFalse(result.Canceled);
                }
                else
                {
                    Assert.IsTrue(ChartResourceHealthRequest.TryCreate([new ChartOperationTarget(fixture.Chart, null,
                        ChartOperationSourceScope.Library, true, false, false, ChartOperationCapabilities.RunResourceHealthCheck)], out ChartResourceHealthRequest request));
                    SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(request);
                    Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
                }
            }
            await Rescan();
            fixture.AssertCurrentAndResourceHealth(0);
            fixture.RejectSongUpdates();
            string resourcePath = Path.Combine(Path.GetDirectoryName(fixture.Path) ?? throw new InvalidOperationException("譜面の親が必要です。"), "missing.wav");
            File.WriteAllBytes(resourcePath, [1]);
            await Rescan();
            fixture.AssertCurrentAndResourceHealth(1);
            File.Delete(resourcePath);
            await Rescan();
            fixture.AssertCurrentAndResourceHealth(0);
        });
    }

    /// <summary>実警告無視・再検査を本番adapterとDBへ転送し、拒否要求が保存を始めないことを確認します。</summary>
    [TestMethod]
    public async Task RealStore_WarningIgnoreAndRescanBorrowAcceptedLibraryAuthority()
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string path = Path.Combine(Path.GetDirectoryName(songDbPath)!, "health.bms");
            File.WriteAllText(path, "#TITLE health\n#WAV01 missing.wav\n#00111:01");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [CreateFile(new string('d', 32), path)], BmsonCharts = [] };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(library.BmsCharts);
            ChartFile chart = library.BmsCharts.Single();
            Assert.IsTrue(ChartResourceHealthRequest.TryCreate([new ChartOperationTarget(chart, null,
                ChartOperationSourceScope.Library, true, false, false, ChartOperationCapabilities.RunResourceHealthCheck)], out ChartResourceHealthRequest request));
            var owner = new SelectedChartResourceHealthWorkflowOwner(() => library, new RecordingDialogService());
            try
            {
                Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable busy));
                using (busy)
                {
                    Assert.IsTrue(owner.SetWarningsIgnored(request).Busy);
                    Assert.IsTrue((await owner.RescanAsync(request)).Busy);
                    using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                    Assert.IsFalse(read.Table<LR2SongDBExtended.maintenance>().Any(row => row.path == path));
                }
                Assert.IsTrue(owner.SetWarningsIgnored(request).Succeeded);
                using (LR2SongDBExtended read = gateway.OpenSongDbReadOnly())
                { Assert.IsTrue(read.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == path).is_files_warning_ignored); }
                Assert.IsTrue(owner.SetWarningsIgnored(request, unset: true).Succeeded);
                Assert.IsTrue((await owner.RescanAsync(request)).Succeeded);
                using (LR2SongDBExtended read = gateway.OpenSongDbReadOnly())
                {
                    LR2SongDBExtended.maintenance row = read.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == path);
                    Assert.IsFalse(row.is_files_warning_ignored);
                    Assert.AreEqual(1, row.wav_files_defined);
                    Assert.AreEqual(0, row.wav_files_existing);
                }
                Assert.IsFalse(library.OperationAdmission.IsActive);
            }
            finally { library.RequestShutdown("health-entry-test"); }
        });
    }

    /// <summary>保留のみ・所持混在で実再調査から既存表示行までの反映と、入力元100%・配置先0%の分離を確認します。ASCIIの自動判定と異なる指定文字コード、非CP932入力、修正印の真偽を交差させ、保留のsong非新設と所持の再復号保存、元配置・所属・非選択項目の保持を保証します。</summary>
    [TestMethod]
    [DataRow(false, false, false, true)]
    [DataRow(false, true, false, false)]
    [DataRow(true, false, false, true)]
    [DataRow(true, true, true, false)]
    public async Task RescanAsync_RealStoreUpdatesPendingCurrentAndExistingRow(bool hasDestination, bool includeOwned, bool nonCp932, bool encodingFixed)
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string path = Path.Combine(source, "pending.bms");
            string unrelatedPath = Path.Combine(source, "unselected.bms");
            string ownedPath = Path.Combine(root, "owned.bms");
            // CP932として読んだUTF-8末尾の先行バイトが改行を消費し、次の資源定義まで失う入力を避けます。
            string title = nonCp932 ? "after 日本語 title" : "rescan";
            string beforeTitle = nonCp932 ? "before 日本語 title" : "rescan";
            // BOMの優先復号で候補文字コードの検査を迂回せず、内容からUTF-8と判定する入力にします。
            var fileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            string beforeText = "#PLAYER 1\n#TITLE " + beforeTitle + "\n#WAV01 missing.wav\n#WAV02 obsolete.wav\n#WAV03 "
                + new string('x', 300) + ".wav\n#00111:0102\n";
            string afterText = "#PLAYER 1\n#TITLE " + title + "\n#WAV01 tone.wav\n#00111:01\n";
            File.WriteAllText(path, beforeText, fileEncoding);
            File.WriteAllText(unrelatedPath, beforeText, fileEncoding);
            File.WriteAllText(ownedPath, beforeText, fileEncoding);
            var entry = PackageChartEntry.FromPath(path);
            var unrelated = PackageChartEntry.FromPath(unrelatedPath);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(unrelated);
            ResourceHealthMaintenanceSnapshot original = entry.GetOriginalResourceHealth();
            ChartFile unrelatedBefore = unrelated.Chart;
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = path;
            var unrelatedPackage = ChartPackage.FromChartEntries([unrelated]);
            unrelatedPackage.path = unrelatedPath;
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(destination, []);
            if (hasDestination)
            {
                entry.ApplyCurrentChart(entry.Chart with { InstallDestinationSuggestions = [destination] });
                entry.ApplyInstallDestinationMetadata(destination, "配置先", "作者");
                BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], package, cache);
            }
            ResourceHealthMaintenanceSnapshot marked = entry.Chart.ResourceHealthMaintenanceSnapshot with
            { Encoding = "utf-8", EncodingFixed = encodingFixed };
            entry.ApplyCurrentChart(ChartFileProjection.WithMaintenance(entry.Chart, marked));
            entry.ReplaceResourceHealthProjection(marked, BmsLibraryMaintenanceService.BuildResourceHealthWarnings(marked), entry.ResourcePlacement);
            ChartFile before = entry.Chart;
            Assert.AreEqual(0, before.WAVHealth);
            Assert.AreEqual(2, before.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.IsTrue(before.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathTooLong));
            LibraryChartRow row = new MainChartRowProjectionOwner().CreatePackageRow(null, entry);
            Assert.AreEqual(0, row.WAVHealth);
            Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathTooLong));
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            {
                BmsCharts = includeOwned ? [PackageChartEntry.FromPath(ownedPath).Chart] : [],
                BmsonCharts = [],
                ChartPackagesPending = [package, unrelatedPackage]
            };
            LibraryResourceIndexTestSupport.GetOwner(library).Replace(new LibraryResourceIndex().DeriveWithDirectoryLookupCache(cache));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(library.BmsCharts);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                db.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
                db.InsertOrReplace(unrelatedPackage, typeof(LR2SongDBExtended.install));
            });
            var targets = new List<ChartOperationTarget>
            {
                new(before, null, ChartOperationSourceScope.PendingPackage, false, true, false,
                    ChartOperationCapabilities.RunResourceHealthCheck, entry)
            };
            if (includeOwned)
            {
                targets.Add(new(library.BmsCharts.Single(), null, ChartOperationSourceScope.Library, true, false, false,
                    ChartOperationCapabilities.RunResourceHealthCheck));
            }
            Assert.IsTrue(ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request));
            var owner = new SelectedChartResourceHealthWorkflowOwner(() => library, new RecordingDialogService());
            try
            {
                File.WriteAllText(path, afterText, fileEncoding);
                File.WriteAllText(Path.Combine(source, "tone.wav"), "audio");
                File.WriteAllText(ownedPath, afterText, fileEncoding);
                Assert.AreEqual(nonCp932 ? 65001 : 932, Encoding.GetEncoding(BmsEncodingDetector.Detect(File.ReadAllBytes(path)).EncodingName).CodePage);
                if (nonCp932) { Assert.AreNotEqual(title, library.BmsCharts.Single().RawTitle); }
                File.WriteAllText(Path.Combine(root, "tone.wav"), "audio");
                SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(request);
                Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
                using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                LR2SongDBExtended.maintenance saved = read.Table<LR2SongDBExtended.maintenance>().Single(info => info.path == path);
                Assert.AreEqual(1, saved.wav_files_defined);
                Assert.AreEqual(1, saved.wav_files_existing);
                ChartFile current = entry.Chart;
                Assert.AreEqual(hasDestination ? 0 : 100, current.WAVHealth);
                Assert.AreEqual(hasDestination ? 0 : 100, row.WAVHealth);
                Assert.AreEqual(1, current.ResourceHealthMaintenanceSnapshot.WavFilesDefined, "再調査後の配置評価にも新しい参照集合を使います。");
                Assert.AreEqual(hasDestination, row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.IsFalse(current.Warnings.Any(warning => warning.Category == ChartWarningCategory.Lr2Compatibility));
                Assert.IsFalse(row.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.Lr2Compatibility));
                Assert.AreEqual("utf-8", saved.encoding);
                Assert.AreEqual(encodingFixed, saved.is_encoding_fixed);
                Assert.AreEqual(saved.encoding, current.EncodingName);
                Assert.AreEqual(encodingFixed, current.ResourceHealthMaintenanceSnapshot.EncodingFixed);
                Assert.AreEqual(before.RawTitle, current.RawTitle, "資源だけの再調査は保留のメタデータを再復号しません。");
                Assert.AreSame(original, entry.GetOriginalResourceHealth());
                Assert.AreEqual(0, original.GetWavHealth());
                Assert.AreEqual(before.InstallDestination, current.InstallDestination);
                CollectionAssert.AreEqual(before.InstallDestinationSuggestions.ToArray(), current.InstallDestinationSuggestions.ToArray());
                Assert.AreSame(unrelatedBefore.ResourceHealthMaintenanceSnapshot, unrelated.Chart.ResourceHealthMaintenanceSnapshot);
                Assert.AreEqual(unrelatedBefore.WAVHealth, unrelated.Chart.WAVHealth);
                CollectionAssert.AreEqual(unrelatedBefore.Warnings.ToArray(), unrelated.Chart.Warnings.ToArray());
                Assert.AreEqual(2, library.ChartPackagesPending.Count);
                CollectionAssert.AreEquivalent(new[] { path, unrelatedPath }, read.Table<LR2SongDBExtended.install>().Select(item => item.path).ToArray());
                Assert.AreSame(entry, package.ChartEntries.Single());
                Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
                Assert.IsNull(current.Token);
                Assert.AreEqual(includeOwned ? 1 : 0, library.BmsCharts.Count);
                Assert.AreEqual(includeOwned ? 1 : 0, read.Table<LR2SongDB.song>().Count());
                if (includeOwned)
                {
                    Assert.AreEqual(100, library.BmsCharts.Single().WAVHealth);
                    Assert.AreEqual(1, read.Table<LR2SongDBExtended.maintenance>().Single(info => info.path == ownedPath).wav_files_existing);
                    if (nonCp932)
                    {
                        Assert.AreEqual(title, library.BmsCharts.Single().RawTitle);
                        Assert.AreEqual(title, read.Table<LR2SongDB.song>().Single().title);
                    }
                }
            }
            finally { library.RequestShutdown("pending-resource-rescan-test"); }
        });
    }

    /// <summary>同じ長さの音声参照だけを書き換え、入力元の保存値が同値でも候補先の不足を既存行へ反映します。DB書込みを拒否するfixtureで同値書込みの抑止も観測し、所持版・文字コード・元配置・所属を保持します。</summary>
    [TestMethod]
    public async Task RescanAsync_WhenOnlyResourceReferencesChange_ReevaluatesPendingDestinationWithoutWritingSameValues()
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string path = Path.Combine(source, "pending.bms");
            string beforeText = "#TITLE rescan\n#WAV01 old.wav\n#00111:01\n";
            string afterText = beforeText.Replace("old.wav", "new.wav", StringComparison.Ordinal);
            Assert.AreEqual(beforeText.Length, afterText.Length);
            File.WriteAllText(path, beforeText);
            File.WriteAllText(Path.Combine(source, "old.wav"), "audio");
            File.WriteAllText(Path.Combine(destination, "old.wav"), "audio");
            var entry = PackageChartEntry.FromPath(path);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
            ResourceHealthMaintenanceSnapshot original = entry.GetOriginalResourceHealth();
            ResourceHealthMaintenanceSnapshot sourceInfo = entry.Chart.ResourceHealthMaintenanceSnapshot with
            { Encoding = "utf-8", EncodingFixed = false };
            entry.ApplyCurrentChart(ChartFileProjection.WithMaintenance(entry.Chart, sourceInfo));
            entry.ReplaceResourceHealthProjection(sourceInfo, BmsLibraryMaintenanceService.BuildResourceHealthWarnings(sourceInfo), entry.ResourcePlacement);
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = path;
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(destination, ["old.wav"]);
            entry.ApplyCurrentChart(entry.Chart with { InstallDestinationSuggestions = [destination] });
            entry.ApplyInstallDestinationMetadata(destination, "配置先", "作者");
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], package, cache);
            ChartFile before = entry.Chart;
            Assert.AreEqual(100, before.WAVHealth);
            Assert.AreEqual("old.wav", before.Resources.Single().RawPath);
            LibraryChartRow row = new MainChartRowProjectionOwner().CreatePackageRow(null, entry);
            Assert.AreEqual(100, row.WAVHealth);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [], BmsonCharts = [], ChartPackagesPending = [package] };
            LibraryResourceIndexTestSupport.GetOwner(library).Replace(new LibraryResourceIndex().DeriveWithDirectoryLookupCache(cache));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertMaintenanceInfos([sourceInfo]);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                db.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
                db.Execute("CREATE TRIGGER reject_same_maintenance_insert BEFORE INSERT ON maintenance BEGIN SELECT RAISE(ABORT, 'unexpected maintenance insert'); END;");
                db.Execute("CREATE TRIGGER reject_same_maintenance_update BEFORE UPDATE ON maintenance BEGIN SELECT RAISE(ABORT, 'unexpected maintenance update'); END;");
            });
            Assert.IsTrue(ChartResourceHealthRequest.TryCreate([new ChartOperationTarget(before, null,
                ChartOperationSourceScope.PendingPackage, false, true, false,
                ChartOperationCapabilities.RunResourceHealthCheck, entry)], out ChartResourceHealthRequest request));
            var owner = new SelectedChartResourceHealthWorkflowOwner(() => library, new RecordingDialogService());
            int catalogRevision = library.CatalogRevision;
            try
            {
                File.WriteAllText(path, afterText);
                File.WriteAllText(Path.Combine(source, "new.wav"), "audio");
                SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(request);
                Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
                using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                ResourceHealthMaintenanceSnapshot saved = MaintenanceStorageMapping.ToCommon(read.Table<LR2SongDBExtended.maintenance>().Single());
                Assert.AreEqual(sourceInfo, saved, "入力元の健康度・LR2診断・文字コードは同値であり、書き直しません。");
                Assert.AreEqual(1, saved.WavFilesDefined);
                Assert.AreEqual(1, saved.WavFilesExisting);
                ChartFile current = entry.Chart;
                Assert.AreEqual("new.wav", current.Resources.Single().RawPath);
                Assert.AreEqual(0, current.WAVHealth);
                Assert.AreEqual(0, row.WAVHealth);
                Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.AreEqual("utf-8", current.EncodingName);
                Assert.IsFalse(current.ResourceHealthMaintenanceSnapshot.EncodingFixed);
                Assert.AreEqual(before.InstallDestination, current.InstallDestination);
                CollectionAssert.AreEqual(before.InstallDestinationSuggestions.ToArray(), current.InstallDestinationSuggestions.ToArray());
                Assert.AreSame(original, entry.GetOriginalResourceHealth());
                Assert.AreEqual(100, original.GetWavHealth());
                Assert.AreEqual(catalogRevision, library.CatalogRevision);
                Assert.AreSame(entry, package.ChartEntries.Single());
                Assert.AreSame(package, library.ChartPackagesPending.Single());
                Assert.AreEqual(path, read.Table<LR2SongDBExtended.install>().Single().path);
                Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
                Assert.IsNull(current.Token);
                Assert.AreEqual(0, library.BmsCharts.Count);
                Assert.AreEqual(0, read.Table<LR2SongDB.song>().Count());
            }
            finally { library.RequestShutdown("pending-resource-reference-rescan-test"); }
        });
    }

    /// <summary>入力元の保存前行・今回検査・配置表示を独立させ、削除・追加・保存同値・未登録・別hashを実再調査で確認します。同値では書込み拒否triggerを使い、全条件で非選択の所持索引をTryGetから先に観測して同じsnapshotの維持を保証します。</summary>
    [TestMethod]
    [DataRow("same", 1, 0, 0, true)]
    [DataRow("same", 0, 1, 1, false)]
    [DataRow("same", 0, 1, 0, true)]
    [DataRow("absent", 1, 1, 1, true)]
    [DataRow("different", 1, 1, 1, true)]
    public async Task RescanAsync_UsesStoredSourceValuesAndKeepsOwnedIndex(string storedState, int storedExisting, int destinationExisting, int sourceExisting, bool initialSourceExists)
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string path = Path.Combine(source, "pending.bms");
            string unrelatedPath = Path.Combine(source, "unselected.bms");
            string backgroundPath = Path.Combine(root, "background.bms");
            string audioPath = Path.Combine(source, "tone.wav");
            File.WriteAllText(path, "#TITLE pending\n#WAV01 tone.wav\n#00111:01\n");
            File.WriteAllText(unrelatedPath, "#TITLE unrelated\n#WAV01 untouched.wav\n#00111:01\n");
            File.WriteAllText(backgroundPath, "#TITLE background\n#WAV01 background.wav\n#00111:01\n");
            if (initialSourceExists) { File.WriteAllText(audioPath, "audio"); }
            if (destinationExisting == 1) { File.WriteAllText(Path.Combine(destination, "tone.wav"), "audio"); }
            var entry = PackageChartEntry.FromPath(path);
            var unrelated = PackageChartEntry.FromPath(unrelatedPath);
            var backgroundEntry = PackageChartEntry.FromPath(backgroundPath);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(unrelated);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(backgroundEntry);
            ResourceHealthMaintenanceSnapshot original = entry.GetOriginalResourceHealth();
            Assert.AreEqual(1, original.WavFilesDefined);
            Assert.AreEqual(initialSourceExists ? 1 : 0, original.WavFilesExisting);
            ChartFile unrelatedBefore = unrelated.Chart;
            ResourceHealthMaintenanceSnapshot marked = entry.Chart.ResourceHealthMaintenanceSnapshot with { Encoding = "utf-8", EncodingFixed = false };
            entry.ApplyCurrentChart(ChartFileProjection.WithMaintenance(entry.Chart, marked));
            entry.ReplaceResourceHealthProjection(marked, BmsLibraryMaintenanceService.BuildResourceHealthWarnings(marked), entry.ResourcePlacement);
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = path;
            var unrelatedPackage = ChartPackage.FromChartEntries([unrelated]);
            unrelatedPackage.path = unrelatedPath;
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(destination, destinationExisting == 1 ? ["tone.wav"] : []);
            entry.ApplyCurrentChart(entry.Chart with { InstallDestinationSuggestions = [destination] });
            entry.ApplyInstallDestinationMetadata(destination, "配置先", "作者");
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], package, cache);
            ChartFile before = entry.Chart;
            Assert.AreEqual(1, before.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(destinationExisting, before.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            LibraryChartRow row = new MainChartRowProjectionOwner().CreatePackageRow(null, entry);
            Assert.AreEqual(destinationExisting * 100, row.WAVHealth);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [backgroundEntry.Chart], BmsonCharts = [], ChartPackagesPending = [package, unrelatedPackage] };
            LibraryResourceIndexTestSupport.GetOwner(library).Replace(new LibraryResourceIndex().DeriveWithDirectoryLookupCache(cache));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(library.BmsCharts);
            var savedBefore = new ResourceHealthMaintenanceSnapshot
            {
                Path = path,
                Hash = storedState == "different" ? new string('f', 32) : before.Md5.ToUpperInvariant(),
                Encoding = "utf-8",
                EncodingFixed = false,
                WavFilesDefined = 1,
                WavFilesExisting = storedExisting,
                BgaFilesDefined = 0,
                BgaFilesExisting = 0,
                MovieFilesDefined = 0,
                MovieFilesExisting = 0,
                StagefileDefined = false,
                BannerDefined = false,
                BackbmpDefined = false,
                Lr2WarningFlags = 0,
                Lr2ResourceMaxRelativeCp932Bytes = 8,
                Lr2ResourceHasParentTraversal = false,
                Origin = MaintenanceInfoOrigin.Calculated
            };
            if (storedState != "absent") { gateway.UpsertMaintenanceInfos([savedBefore]); }
            bool noWrite = storedState == "same" && storedExisting == sourceExisting;
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                db.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
                db.InsertOrReplace(unrelatedPackage, typeof(LR2SongDBExtended.install));
                if (noWrite)
                {
                    db.Execute("CREATE TRIGGER reject_unchanged_source_insert BEFORE INSERT ON maintenance BEGIN SELECT RAISE(ABORT, 'unexpected source insert'); END;");
                    db.Execute("CREATE TRIGGER reject_unchanged_source_update BEFORE UPDATE ON maintenance BEGIN SELECT RAISE(ABORT, 'unexpected source update'); END;");
                }
            });
            Assert.IsTrue(ChartResourceHealthRequest.TryCreate([new ChartOperationTarget(before, null,
                ChartOperationSourceScope.PendingPackage, false, true, false,
                ChartOperationCapabilities.RunResourceHealthCheck, entry)], out ChartResourceHealthRequest request));
            var owner = new SelectedChartResourceHealthWorkflowOwner(() => library, new RecordingDialogService());
            try
            {
                EnsureCurrentResourceHealthIndex(library);
                ResourceHealthIndexSnapshot indexBefore = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, indexBefore);
                Assert.AreEqual(1, indexBefore.TargetCount);
                ChartFile backgroundBefore = library.BmsCharts.Single();
                ResourceHealthWarningProjection backgroundProjection = indexBefore.GetProjection(backgroundBefore.Kind, backgroundBefore.Path, backgroundBefore.Md5);
                Assert.IsTrue(backgroundProjection.HasIssues);
                int catalogRevision = library.CatalogRevision;
                if (sourceExisting == 1) { File.WriteAllText(audioPath, "audio"); }
                else { File.Delete(audioPath); }
                SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(request);
                Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
                ResourceHealthIndexSnapshot indexAfter = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                Assert.AreSame(indexBefore, indexAfter, "再構築しない取得で、保留だけの更新後も既存の所持索引を維持します。");
                Assert.AreSame(indexBefore, library.GetResourceHealthIndexSnapshotForView("pending_source_comparison_test"));
                Assert.AreEqual(1, indexAfter.TargetCount);
                Assert.AreSame(backgroundProjection, indexAfter.GetProjection(backgroundBefore.Kind, backgroundBefore.Path, backgroundBefore.Md5));
                Assert.AreEqual(catalogRevision, library.CatalogRevision);
                using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                ResourceHealthMaintenanceSnapshot saved = MaintenanceStorageMapping.ToCommon(read.Table<LR2SongDBExtended.maintenance>().Single(info => info.path == path));
                Assert.AreEqual(1, saved.WavFilesDefined);
                Assert.AreEqual(sourceExisting, saved.WavFilesExisting);
                Assert.AreEqual(noWrite ? savedBefore.Hash : before.Md5, saved.Hash);
                Assert.AreEqual(path, saved.Path);
                Assert.AreEqual("utf-8", saved.Encoding);
                Assert.IsFalse(saved.EncodingFixed);
                if (noWrite) { Assert.AreEqual(savedBefore, saved); }
                ChartFile current = entry.Chart;
                Assert.AreEqual(1, current.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
                Assert.AreEqual(destinationExisting, current.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
                Assert.AreEqual(destinationExisting * 100, current.WAVHealth);
                Assert.AreEqual(destinationExisting * 100, row.WAVHealth);
                Assert.AreEqual(destinationExisting == 0, row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.AreEqual("utf-8", current.EncodingName);
                Assert.IsFalse(current.ResourceHealthMaintenanceSnapshot.EncodingFixed);
                Assert.AreSame(original, entry.GetOriginalResourceHealth());
                Assert.AreEqual(initialSourceExists ? 1 : 0, original.WavFilesExisting);
                Assert.AreEqual(before.InstallDestination, current.InstallDestination);
                CollectionAssert.AreEqual(before.InstallDestinationSuggestions.ToArray(), current.InstallDestinationSuggestions.ToArray());
                Assert.AreSame(unrelatedBefore.ResourceHealthMaintenanceSnapshot, unrelated.Chart.ResourceHealthMaintenanceSnapshot);
                CollectionAssert.AreEqual(unrelatedBefore.Warnings.ToArray(), unrelated.Chart.Warnings.ToArray());
                Assert.AreSame(backgroundBefore.ResourceHealthMaintenanceSnapshot, library.BmsCharts.Single().ResourceHealthMaintenanceSnapshot);
                CollectionAssert.AreEqual(backgroundBefore.Warnings.ToArray(), library.BmsCharts.Single().Warnings.ToArray());
                Assert.AreSame(entry, package.ChartEntries.Single());
                Assert.AreEqual(2, library.ChartPackagesPending.Count);
                CollectionAssert.AreEquivalent(new[] { path, unrelatedPath }, read.Table<LR2SongDBExtended.install>().Select(item => item.path).ToArray());
                Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
                Assert.IsNull(current.Token);
                Assert.AreEqual(1, library.BmsCharts.Count);
                Assert.AreEqual(backgroundPath, read.Table<LR2SongDB.song>().Single().path);
            }
            finally { library.RequestShutdown("pending-source-comparison-rescan-test"); }
        });
    }

    /// <summary>子階層譜面の再調査で入力元の音源削除をDBへ保存し、初回に捕捉した同梱音源は最終相対配置で既存行へ反映します。packageなしの再評価でも相対位置・同梱完全性・先頭候補・元配置を維持します。</summary>
    [TestMethod]
    public async Task RescanAsync_NestedPendingChartKeepsCapturedBundledPlacement()
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Source");
            string sourceSub = Path.Combine(source, "sub");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(sourceSub);
            Directory.CreateDirectory(destination);
            string path = Path.Combine(sourceSub, "chart.bms");
            string audioPath = Path.Combine(sourceSub, "tone.wav");
            File.WriteAllText(path, "#TITLE nested\n#WAV01 tone.wav\n#00111:01\n");
            File.WriteAllText(audioPath, "audio");
            var entry = PackageChartEntry.FromPath(path);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
            ResourceHealthMaintenanceSnapshot original = entry.GetOriginalResourceHealth();
            Assert.AreEqual(1, original.WavFilesDefined);
            Assert.AreEqual(1, original.WavFilesExisting);
            ResourceHealthMaintenanceSnapshot marked = entry.Chart.ResourceHealthMaintenanceSnapshot with { Encoding = "utf-8", EncodingFixed = false };
            entry.ApplyCurrentChart(ChartFileProjection.WithMaintenance(entry.Chart, marked));
            entry.ReplaceResourceHealthProjection(marked, BmsLibraryMaintenanceService.BuildResourceHealthWarnings(marked), entry.ResourcePlacement);
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(destination, []);
            entry.ApplyCurrentChart(entry.Chart with { InstallDestinationSuggestions = [destination] });
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], package, cache);
            ChartResourcePlacement placementBefore = entry.ResourcePlacement;
            Assert.AreEqual(ChartResourcePlacementBasis.FirstCandidate, placementBefore.Basis);
            Assert.AreEqual("sub", placementBefore.RelativeDirectory);
            Assert.AreEqual(Path.Combine(destination, "sub"), placementBefore.Directory);
            Assert.IsNotNull(placementBefore.BundledResources);
            Assert.IsTrue(placementBefore.BundledResourcesComplete);
            ChartFile before = entry.Chart;
            Assert.AreEqual(100, before.WAVHealth);
            LibraryChartRow row = new MainChartRowProjectionOwner().CreatePackageRow(null, entry);
            Assert.AreEqual(100, row.WAVHealth);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [], BmsonCharts = [], ChartPackagesPending = [package] };
            LibraryResourceIndexTestSupport.GetOwner(library).Replace(new LibraryResourceIndex().DeriveWithDirectoryLookupCache(cache));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                db.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
            });
            Assert.IsTrue(ChartResourceHealthRequest.TryCreate([new ChartOperationTarget(before, null,
                ChartOperationSourceScope.PendingPackage, false, true, false,
                ChartOperationCapabilities.RunResourceHealthCheck, entry)], out ChartResourceHealthRequest request));
            var owner = new SelectedChartResourceHealthWorkflowOwner(() => library, new RecordingDialogService());
            try
            {
                File.Delete(audioPath);
                SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(request);
                Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
                using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                LR2SongDBExtended.maintenance saved = read.Table<LR2SongDBExtended.maintenance>().Single(info => info.path == path);
                Assert.AreEqual(1, saved.wav_files_defined);
                Assert.AreEqual(0, saved.wav_files_existing);
                Assert.AreEqual(before.Md5, saved.hash);
                Assert.AreEqual("utf-8", saved.encoding);
                Assert.IsFalse(saved.is_encoding_fixed);
                Assert.AreEqual(1, entry.Chart.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
                Assert.AreEqual(1, entry.Chart.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
                Assert.AreEqual(100, entry.Chart.WAVHealth);
                Assert.AreEqual(100, row.WAVHealth);
                Assert.IsFalse(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.AreEqual("utf-8", entry.Chart.EncodingName);
                Assert.IsFalse(entry.Chart.ResourceHealthMaintenanceSnapshot.EncodingFixed);
                Assert.AreEqual(Path.Combine(destination, "sub"), entry.ResourcePlacement.Directory);
                Assert.AreEqual(placementBefore.RelativeDirectory, entry.ResourcePlacement.RelativeDirectory);
                Assert.AreEqual(placementBefore.Basis, entry.ResourcePlacement.Basis);
                Assert.AreSame(placementBefore.BundledResources, entry.ResourcePlacement.BundledResources);
                Assert.AreEqual(placementBefore.BundledResourcesComplete, entry.ResourcePlacement.BundledResourcesComplete);
                Assert.AreSame(original, entry.GetOriginalResourceHealth());
                Assert.AreEqual(1, original.WavFilesExisting);
                Assert.AreEqual(before.InstallDestination, entry.Chart.InstallDestination);
                CollectionAssert.AreEqual(new[] { destination }, entry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.AreSame(entry, package.ChartEntries.Single());
                Assert.AreSame(package, library.ChartPackagesPending.Single());
                Assert.AreEqual(source, read.Table<LR2SongDBExtended.install>().Single().path);
                Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
                Assert.IsNull(entry.Chart.Token);
                Assert.AreEqual(0, library.BmsCharts.Count);
                Assert.AreEqual(0, read.Table<LR2SongDB.song>().Count());
            }
            finally { library.RequestShutdown("nested-pending-resource-rescan-test"); }
        });
    }

    private static SelectedChartResourceHealthWorkflowOwner CreateOwner(
        RecordingStore store,
        RecordingDialogService dialogs)
    {
        return new SelectedChartResourceHealthWorkflowOwner(
            () => (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary)),
            dialogs,
            store, new ChartFileOperationSynchronizer());
    }

    private static ChartResourceHealthRequest CreateRequest()
    {
        ChartOperationTarget target = new(
            CreateChart(@"C:\Songs\resource-health.bms"),
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.RunResourceHealthCheck);
        Assert.IsTrue(ChartResourceHealthRequest.TryCreate([target], out ChartResourceHealthRequest request));
        return request;
    }

    private static ChartFile CreateChart(string path)
    {
        return new ChartFile(ChartFileKind.Bms, path, "resource-health-hash", null, "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    private sealed class RecordingStore : ISelectedChartResourceHealthStore
    {
        internal int RescanCalls { get; private set; }

        internal MaintenanceWorkflowResult RescanResult { get; set; } = new();

        internal Exception Failure { get; set; } = null!;

        internal List<bool> UnsetValues { get; } = [];

        public MaintenanceWorkflowResult RescanResourceHealthCharts(
            BMSLibrary library,
            IReadOnlyList<ChartFile> charts, LibraryFileMutationCapability capability,
            IReadOnlyList<PackageChartEntry> pendingEntries)
        {
            RescanCalls++;
            ThrowIfConfigured();
            return RescanResult;
        }

        public void SetChartResourceWarningsIgnored(
            BMSLibrary library,
            IReadOnlyList<ChartFile> charts,
            bool unset, LibraryFileMutationCapability capability)
        {
            UnsetValues.Add(unset);
            ThrowIfConfigured();
        }

        private void ThrowIfConfigured()
        {
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal int MessageCalls { get; private set; }

        internal string LastMessage { get; private set; } = string.Empty;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCalls++;
            LastMessage = request.MessageBoxText;
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
