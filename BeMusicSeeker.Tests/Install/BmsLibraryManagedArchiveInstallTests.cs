using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsLibraryPackageInstallServiceTests
{
    /// <summary>
    /// 同じ譜面・リソースの管理ZIP、借用ZIP、フォルダを実非同期入口へ渡します。
    /// 充足時のFS・DB・所持への導入と、不足時の保留行・入力保持・実候補への推定を確認し、全形態の等価な失敗を成功と扱いません。
    /// </summary>
    [DataTestMethod]
    [DataRow("managed", true)]
    [DataRow("borrowed", true)]
    [DataRow("folder", true)]
    [DataRow("managed", false)]
    [DataRow("borrowed", false)]
    [DataRow("folder", false)]
    public async Task AutoInstall_ArchiveAndFolderInputsReachInstallOrPendingEstimation(string kind, bool resources)
    {
        using var fixture = new ManagedArchiveInstallFixture(knownCandidate: !resources);
        if (!resources) { await fixture.InitializeAsync(); }
        string input = fixture.CreateInput(kind, resources: resources);
        PackageInstallCommandResult result = await fixture.Library.InstallChartPackagesAutoWithProgressAsync(
            [input], CancellationToken.None, new RecordingPackageInstallProgressWriter());
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsFalse(result.HasRequiredFailure, result.EstimationFailure?.ToString());
        Assert.AreEqual(0, result.OperationMessages.Count);
        if (kind == "managed") { Assert.IsFalse(File.Exists(input), "正常展開済みの管理元ZIPは消費します。"); }
        if (kind == "borrowed") { Assert.IsTrue(File.Exists(input), "借用元ZIPは保持します。"); }
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        if (resources)
        {
            Assert.AreEqual(1, result.RegisteredPackages.Count);
            Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
            Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
            Assert.AreEqual(1, fixture.Library.BmsCharts.Count);
            string destination = result.RegisteredPackages.Single().ChartEntries.Single().Chart.Path;
            Assert.IsTrue(File.Exists(destination));
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(destination) ?? throw new AssertFailedException(), "sound.wav")));
            Assert.IsNotNull(db.Find<LR2SongDB.song>(destination));
            Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
            Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
            if (kind == "folder") { Assert.IsFalse(Directory.Exists(input), "導入成功した通常フォルダの移動消費を維持します。"); }
        }
        else
        {
            Assert.AreEqual(0, result.RegisteredPackages.Count);
            Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
            Assert.AreEqual(1, fixture.Library.BmsCharts.Count);
            ChartPackage pending = fixture.Library.ChartPackagesPending.Single();
            ChartFile chart = pending.ChartEntries.Single().Chart;
            Assert.IsTrue(File.Exists(chart.Path));
            Assert.AreEqual(fixture.CandidateDirectory, chart.InstallDestination);
            Assert.AreEqual(1, db.Table<LR2SongDBExtended.install>().Count());
            Assert.IsTrue(Directory.Exists(pending.path));
            if (kind == "folder") { Assert.AreEqual(input, pending.path); }
            else { Assert.IsTrue(fixture.ExtractedRoots.Contains(pending.path)); }
        }
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>管理実ZIPと独立フォルダを一要求で正常導入し、複数物理成功を一回の確定・所持反映へ集めます。</summary>
    [TestMethod]
    public async Task AutoInstall_ManagedArchiveAndIndependentFolderShareOneCommitAndApply()
    {
        using var fixture = new ManagedArchiveInstallFixture();
        string archive = fixture.CreateInput("managed", "Archived");
        string folder = fixture.CreateInput("folder", "Independent");
        PackageInstallCommandResult result = await fixture.Library.InstallChartPackagesAutoWithProgressAsync(
            [archive, folder], CancellationToken.None, new RecordingPackageInstallProgressWriter());
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsFalse(result.HasRequiredFailure, result.EstimationFailure?.ToString());
        Assert.AreEqual(2, result.RegisteredPackages.Count);
        Assert.AreEqual(2, fixture.Library.BmsCharts.Count);
        Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        Assert.IsFalse(File.Exists(archive));
        Assert.IsFalse(Directory.Exists(folder));
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual(2, db.Table<LR2SongDB.song>().Count());
        foreach (ChartPackage package in result.RegisteredPackages)
        {
            string destination = package.ChartEntries.Single().Chart.Path;
            Assert.IsTrue(File.Exists(destination));
            Assert.IsNotNull(db.Find<LR2SongDB.song>(destination));
        }
    }

    /// <summary>必須日時復元の全失敗と壊れZIP・正常ZIP混在で、対象と原因を終端診断へ残し、非致命的警告のまま正常独立対象だけを確定します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AutoInstall_ArchiveWarningsSurviveEmptyOrMixedPreparation(bool timestampFailure)
    {
        using var fixture = new ManagedArchiveInstallFixture();
        string rejected = fixture.CreateInput("managed", "Rejected");
        string marker = "required timestamp failure marker";
        if (timestampFailure) { fixture.Files.BeforeTimestamp = _ => throw new IOException(marker); }
        else { File.WriteAllText(rejected, "broken zip"); }
        string[] paths = timestampFailure ? [rejected] : [rejected, fixture.CreateInput("managed", "Normal")];
        PackageInstallCommandResult result = await fixture.Library.InstallChartPackagesAutoWithProgressAsync(
            paths, CancellationToken.None, new RecordingPackageInstallProgressWriter());
        Assert.IsFalse(result.HasRequiredFailure);
        BMSLibrary.OperationDialogMessage message = result.OperationMessages.Single();
        StringAssert.Contains(message.MessageBoxText, rejected);
        if (timestampFailure)
        {
            StringAssert.Contains(message.MessageBoxText, marker);
            StringAssert.Contains(message.MessageBoxText, "chart.bms");
            Assert.IsFalse(result.HasDurableCommit);
            Assert.AreEqual(0, fixture.Library.BmsCharts.Count);
        }
        else
        {
            Assert.IsTrue(result.HasDurableCommit);
            Assert.AreEqual(1, result.RegisteredPackages.Count);
            Assert.AreEqual("Normal", result.RegisteredPackages.Single().ChartEntries.Single().Chart.Title);
            Assert.AreNotEqual(string.Format(Resources.Warn_ArchiveExtractFailed, rejected, string.Empty), message.MessageBoxText,
                "抽出警告には対象だけでなく実抽出失敗の原因も保持します。");
            Assert.AreNotEqual(Resources.Warn_InstallAbortedFilesNotFound, message.MessageBoxText);
            Assert.AreEqual(1, fixture.Library.BmsCharts.Count);
        }
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        Assert.AreEqual(0, fixture.Dialogs.ModelMessages);
        Assert.IsFalse(File.Exists(rejected));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual(timestampFailure ? 0 : 1, db.Table<LR2SongDB.song>().Count());
        Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
    }
}
