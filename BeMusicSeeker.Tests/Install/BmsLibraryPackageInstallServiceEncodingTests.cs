using System;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>製品の復号・ハッシュ計算を期待値生成に使わない、導入引継ぎ専用の固定入力です。</summary>
internal static class InstallEncodingTestInput
{
    internal const string Md5 = "c5798b8f3af98af0d76c20f4bb0e0160";
    internal const string Sha256 = "519e4cf6baffa78b545104386e442332181fd400fa6e92b557090335cad4c12a";
    internal static byte[] Bytes => [.. Encoding.ASCII.GetBytes("#TITLE "), 0xa4, 0xa4, 0xa4, 0xe5,
        .. Encoding.ASCII.GetBytes("\r\n#ARTIST artist\r\n#GENRE genre\r\n#BPM 120\r\n#WAV01 tone.wav\r\n#00111:01\r\n")];

    internal static string Write(string directory, string name = "chart.bms")
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Bytes);
        return path;
    }

    internal static void AssertFixed(ChartFile chart, string path)
    {
        Assert.AreEqual(path, chart.Path);
        Assert.AreEqual(Md5, chart.Md5);
        Assert.AreEqual("中文", chart.RawTitle);
        Assert.AreEqual("big5", chart.EncodingName);
        Assert.IsNotNull(chart.ResourceHealthMaintenanceSnapshot);
        Assert.AreEqual(path, chart.ResourceHealthMaintenanceSnapshot.Path);
        Assert.AreEqual(Md5, chart.ResourceHealthMaintenanceSnapshot.Hash);
        Assert.IsTrue(chart.ResourceHealthMaintenanceSnapshot.EncodingFixed);
    }
}

public sealed partial class BmsLibraryPackageInstallServiceTests
{
    /// <summary>保留の実文字コード操作から通常新規・推定先へ導入し、固定Big5をDB・所持・同じlive項目へ保存します。再調査と新しいDB読取りでも保持し、元配置の不足・無視の持越しと、CP932非対応名から対応名への導入・逆方向で同じlive項目が成功先LR2診断へ追従しない誤りを検出します。</summary>
    /// <remarks>期待title/hash/bytesは独立した固定入力。既存の形式別・cold/warm表示系列は複製せず、成功配置と永続保存の接続を担当します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PendingFixedEncoding_InstallPersistsOnlySuccessfulPlacementAndSurvivesRescan(bool estimated)
    {
        WithTemporarySongDb((database, root) =>
        {
            string libraryRoot = Path.Combine(root, estimated ? "Library-🎵" : "Library");
            string source = Path.Combine(root, estimated ? "Pending" : "Pending-🎵");
            string sourcePath = InstallEncodingTestInput.Write(source);
            string destination = Path.Combine(libraryRoot, "中文");
            Directory.CreateDirectory(libraryRoot);
            if (estimated)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllBytes(Path.Combine(destination, "tone.wav"), [1]);
            }
            else { File.WriteAllBytes(Path.Combine(source, "tone.wav"), [1]); }
            DateTime timestamp = new(2026, 6, 9, 2, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(sourcePath, timestamp);
            var entry = PackageChartEntry.FromPath(sourcePath);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
            Assert.AreEqual(!estimated, (entry.Chart.Warnings ?? []).Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            var library = new TestBmsLibrary(database, null, null, new RealFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = libraryRoot,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false
                })
            {
                BmsCharts = [],
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            try
            {
                library.SetBMSFilesEncoding([entry.Chart], "big5", pendingEntries: [entry]);
                InstallEncodingTestInput.AssertFixed(entry.Chart, sourcePath);
                if (estimated) { Assert.AreEqual(0, entry.Chart.WAVHealth); entry.SetInstallDestinationPathOnly(destination); }
                ResourceHealthMaintenanceSnapshot sourceHealth = entry.Chart.ResourceHealthMaintenanceSnapshot with
                {
                    FilesWarningIgnored = true
                };
                entry.ApplyCurrentChart(ChartFileProjection.WithMaintenance(entry.Chart, sourceHealth));
                Assert.AreEqual(!estimated, (entry.Chart.Warnings ?? []).Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
                LibraryMutationSessionReceipt receipt = estimated
                    ? library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([package]).SessionReceipt
                    : library.ForceInstallPendingPackagesWithReceipt([package], approveNormalInstallOverride: true,
                        approvedNormalInstallOverridePackages: null);
                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());
                Assert.IsFalse(receipt.HasRequiredFailure, receipt.PrimaryFailure?.ToString());
                Assert.AreEqual(1, receipt.ApplyCounts.InstalledTargetApplyCount);
                Assert.AreEqual(1, receipt.ApplyCounts.ReverseLookupApplyCount);
                // 必須の導入公開準備を数え、保守・詳細解析も含む一覧更新イベント総数は固定しません。
                Assert.AreEqual(1, receipt.ApplyCounts.RequiredPublicationCount);
                string installedPath = Path.Combine(destination, "chart.bms");
                CollectionAssert.AreEqual(InstallEncodingTestInput.Bytes, File.ReadAllBytes(installedPath));
                Assert.AreSame(entry, library.ChartPackagesInstalled.Single().ChartEntries.Single());
                ChartFile installed = library.BmsCharts.Single();
                InstallEncodingTestInput.AssertFixed(installed, installedPath);
                InstallEncodingTestInput.AssertFixed(entry.Chart, installedPath);
                Assert.AreSame(installed.Token, entry.Chart.Token);
                Assert.AreEqual(InstallEncodingTestInput.Sha256, installed.Sha256);
                Assert.AreEqual(100, installed.WAVHealth);
                Assert.AreEqual(1, installed.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
                Assert.AreEqual(1, installed.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
                Assert.IsFalse(installed.ResourceHealthWarningsIgnored);
                Assert.AreEqual(estimated, (installed.Warnings ?? []).Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
                Assert.AreEqual(estimated, (entry.Chart.Warnings ?? []).Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
                CollectionAssert.AreEquivalent(
                    (installed.Warnings ?? []).Where(warning => warning.Category == ChartWarningCategory.Lr2Compatibility)
                        .Select(warning => (warning.Kind, warning.Message)).ToArray(),
                    (entry.Chart.Warnings ?? []).Where(warning => warning.Category == ChartWarningCategory.Lr2Compatibility)
                        .Select(warning => (warning.Kind, warning.Message)).ToArray());
                library.RescanResourceHealthCharts([installed]);
                InstallEncodingTestInput.AssertFixed(library.BmsCharts.Single(), installedPath);
                InstallEncodingTestInput.AssertFixed(entry.Chart, installedPath);
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(database).OpenSongDbReadOnly();
                LR2SongDB.song song = readback.Find<LR2SongDB.song>(installedPath);
                LR2SongDBExtended.maintenance maintenance = readback.Find<LR2SongDBExtended.maintenance>(installedPath);
                Assert.IsNotNull(song);
                Assert.IsNotNull(maintenance);
                Assert.AreEqual("中文", song.title);
                Assert.AreEqual(InstallEncodingTestInput.Md5, song.hash);
                Assert.AreEqual("big5", maintenance.encoding);
                Assert.IsTrue(maintenance.is_encoding_fixed);
                Assert.AreEqual(1, maintenance.wav_files_existing);
                Assert.AreEqual(1, maintenance.wav_files_defined);
                Assert.AreEqual(false, maintenance.is_files_warning_ignored);
                Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(installedPath));
                Assert.AreEqual(checked((int)new DateTimeOffset(timestamp).ToUnixTimeSeconds()), song.date);
                Assert.IsTrue(song.adddate.HasValue, "新規行の通常採番を観測し、live値との一致は追加保証にしません。");
                Assert.IsNull(readback.Find<LR2SongDBExtended.maintenance>(sourcePath));
                ChartFile loaded = ChartFileProjection.WithMaintenance(ChartSongStorageMapping.FromBmsRow(song),
                    MaintenanceStorageMapping.ToCommon(maintenance, MaintenanceInfoOrigin.DbHydrated));
                InstallEncodingTestInput.AssertFixed(loaded, installedPath);
            }
            finally { library.RequestShutdown("install-fixed-encoding-test"); }
        });
    }

    /// <summary>同hashの既所持へ資源だけを導入しても、保留のBig5指定を別所持や別pathへ拡散せず、既存encoding・修正印・メタデータを保持します。</summary>
    /// <remarks>一つの実資源更新先と同hashの別配置を同じDBへ置き、元入力の明示指定と独立した所持値の不変を照合します。</remarks>
    [TestMethod]
    public void PendingFixedEncoding_ResourceOnlyInstallDoesNotOverwriteOwnedSameHashPlacements()
    {
        WithTemporarySongDb((database, root) =>
        {
            string source = Path.Combine(root, "Pending");
            string path = InstallEncodingTestInput.Write(source);
            File.WriteAllBytes(Path.Combine(source, "tone.wav"), [2]);
            string destination = Path.Combine(root, "Library", "Owned");
            string otherDirectory = Path.Combine(root, "Library", "Other");
            string[] ownedPaths = [InstallEncodingTestInput.Write(destination), InstallEncodingTestInput.Write(otherDirectory)];
            ChartFile[] owned = ownedPaths.Select((ownedPath, index) => ChartFileProjection.WithMaintenance(
                PackageChartEntry.FromPath(ownedPath).Chart with { RawTitle = "Owned " + index, Title = "Owned " + index },
                new ResourceHealthMaintenanceSnapshot
                {
                    Path = ownedPath,
                    Hash = InstallEncodingTestInput.Md5,
                    Encoding = "utf-8",
                    EncodingFixed = index == 0,
                    Origin = MaintenanceInfoOrigin.DbHydrated
                })).ToArray();
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(database, db =>
            {
                foreach (ChartFile chart in owned)
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song));
                    db.InsertOrReplace(MaintenanceStorageMapping.ToStorage(chart.ResourceHealthMaintenanceSnapshot), typeof(LR2SongDBExtended.maintenance));
                }
            });
            var entry = PackageChartEntry.FromPath(path);
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            entry.SetInstallDestinationPathOnly(destination);
            var library = new TestBmsLibrary(database, null, null, new RealFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = Path.Combine(root, "Library"),
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false
                })
            {
                BmsCharts = owned.ToList(),
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            try
            {
                library.SetBMSFilesEncoding([entry.Chart], "big5", pendingEntries: [entry]);
                InstallEncodingTestInput.AssertFixed(entry.Chart, path);
                PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([package]);
                Assert.IsTrue(result.SessionReceipt.DurableCommit, result.SessionReceipt.PrimaryFailure?.ToString());
                Assert.IsFalse(result.SessionReceipt.HasRequiredFailure, result.SessionReceipt.PrimaryFailure?.ToString());
                CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(destination, "tone.wav")));
                Assert.IsFalse(File.Exists(Path.Combine(otherDirectory, "tone.wav")));
                using LR2SongDBExtended db = new BmsLibraryDbGateway(database).OpenSongDbReadOnly();
                Assert.AreEqual(2, db.Table<LR2SongDB.song>().Count());
                for (int index = 0; index < owned.Length; index++)
                {
                    ChartFile current = library.BmsCharts.Single(chart => chart.Path == ownedPaths[index]);
                    Assert.AreEqual("Owned " + index, current.RawTitle);
                    Assert.AreEqual("utf-8", current.EncodingName);
                    Assert.AreEqual(index == 0, current.ResourceHealthMaintenanceSnapshot.EncodingFixed);
                    Assert.AreEqual("Owned " + index, db.Find<LR2SongDB.song>(ownedPaths[index]).title);
                    LR2SongDBExtended.maintenance maintenance = db.Find<LR2SongDBExtended.maintenance>(ownedPaths[index]);
                    Assert.AreEqual("utf-8", maintenance.encoding);
                    Assert.AreEqual(index == 0, maintenance.is_encoding_fixed);
                }
                Assert.IsNull(db.Find<LR2SongDBExtended.maintenance>(path));
            }
            finally { library.RequestShutdown("resource-only-fixed-encoding-test"); }
        });
    }

}
