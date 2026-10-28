using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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
    /// <summary>単体化する根と資源付き子、後続作品を通常フォルダ・管理ZIP・借用ZIPから実導入し、導入と保留の和に入力全譜面を各一回残します。</summary>
    /// <remarks>実非同期入口からFS・SQLite・所持への一回反映、重なるフォルダ入力と保留元の寿命を確認します。複数子・孫と全体package条件は探索境界、取消・失敗は既存workflow系列へ分担します。</remarks>
    [DataTestMethod]
    [DataRow("folder")]
    [DataRow("managed")]
    [DataRow("borrowed")]
    [DoNotParallelize]
    public async Task AutoInstall_SplitRootKeepsChildrenAndPendingInputsAcrossSources(string kind)
    {
        using var fixture = new ManagedArchiveInstallFixture();
        Assert.IsFalse(fixture.Options.AutoInstallAfterAutomaticEstimation);
        string root = fixture.CreateInput("folder", "Split", resources: false, chart: false);
        string child = Path.Combine(root, "Child");
        string later = fixture.CreateInput("folder", "Later");
        // 非空で互いに異なる未同梱参照を使い、子・後続の音源で直下の不足が解消しない入力にします。
        string first = CreateBmsFile(root, "first.bms", BmsLibraryInitializationTestSupport.CreateValidBmsText("Root A").Replace("sound.wav", "missing_a.wav"));
        string second = CreateBmsFile(root, "second.bms", BmsLibraryInitializationTestSupport.CreateValidBmsText("Root B").Replace("sound.wav", "missing_b.wav"));
        string childChart = CreateBmsFile(child, "child.bms", BmsLibraryInitializationTestSupport.CreateValidBmsText("Child").Replace("sound.wav", "child.wav"));
        byte[] childAudio = [2, 4, 6];
        File.WriteAllBytes(Path.Combine(child, "child.wav"), childAudio);
        var expectedCharts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Root A"] = File.ReadAllBytes(first),
            ["Root B"] = File.ReadAllBytes(second),
            ["Child"] = File.ReadAllBytes(childChart),
            ["Later"] = File.ReadAllBytes(Path.Combine(later, "chart.bms"))
        };
        var expectedAudio = new Dictionary<string, (string Name, byte[] Bytes)>(StringComparer.Ordinal)
        {
            ["Child"] = ("child.wav", childAudio),
            ["Later"] = ("sound.wav", File.ReadAllBytes(Path.Combine(later, "sound.wav")))
        };
        // 管理元の作成と回収は既存fixtureへ集約し、当該展開先もfixtureが日時復元から捕捉します。
        string input = kind == "folder" ? root : fixture.CreateInput(kind, "Split", resources: false, chart: false);
        byte[] archiveBytes = kind == "folder" ? [] : File.ReadAllBytes(input);
        string[] inputs = kind == "folder" ? [root, root, child, later] : [input, later];

        PackageInstallCommandResult result = await fixture.Library.InstallChartPackagesAutoWithProgressAsync(
            inputs, CancellationToken.None, new RecordingPackageInstallProgressWriter());

        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsFalse(result.HasRequiredFailure, result.EstimationFailure?.ToString());
        Assert.AreEqual(0, result.OperationMessages.Count);
        Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
        ChartFile[] installed = result.RegisteredPackages.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart).ToArray();
        CollectionAssert.AreEquivalent(expectedAudio.Keys.ToArray(), installed.Select(chart => chart.Title).ToArray());
        string[] installedPaths = installed.Select(chart => chart.Path).ToArray();
        CollectionAssert.AreEquivalent(installedPaths, fixture.Library.BmsCharts.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEquivalent(installedPaths, fixture.Library.ChartPackagesInstalled.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart.Path).ToArray());
        foreach (ChartFile chart in installed)
        {
            CollectionAssert.AreEqual(expectedCharts[chart.Title], File.ReadAllBytes(chart.Path));
            (string name, byte[] bytes) = expectedAudio[chart.Title];
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(chart.Path) ?? throw new AssertFailedException(), name)));
        }

        ChartFile[] pending = fixture.Library.ChartPackagesPending.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart).ToArray();
        CollectionAssert.AreEquivalent(new[] { "Root A", "Root B" }, pending.Select(chart => chart.Title).ToArray());
        CollectionAssert.AreEquivalent(expectedCharts.Keys.ToArray(), installed.Concat(pending).Select(chart => chart.Title).ToArray());
        foreach (ChartFile chart in pending)
        {
            CollectionAssert.AreEqual(expectedCharts[chart.Title], File.ReadAllBytes(chart.Path));
            Assert.IsTrue((chart.Status & ChartFileStatus.SEARCHING) == 0);
        }
        string pendingRoot = Path.GetDirectoryName(pending[0].Path) ?? throw new AssertFailedException();
        Assert.IsTrue(pending.All(chart => Path.GetDirectoryName(chart.Path) == pendingRoot));
        Assert.IsTrue(Directory.Exists(pendingRoot));
        Assert.IsFalse(Directory.Exists(Path.Combine(pendingRoot, "Child")), "成功した子だけを消費し、根の保留譜面を保ちます。");
        Assert.IsFalse(Directory.Exists(later), "後続の成功フォルダも消費します。");
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        CollectionAssert.AreEquivalent(installedPaths, db.Table<LR2SongDB.song>().ToArray().Select(row => row.path).ToArray());
        CollectionAssert.AreEquivalent(fixture.Library.ChartPackagesPending.Select(package => package.path).ToArray(),
            db.Table<LR2SongDBExtended.install>().ToArray().Select(row => row.path).ToArray());
        if (kind == "folder") { Assert.AreEqual(root, pendingRoot); }
        else
        {
            Assert.IsTrue(fixture.ExtractedRoots.Any(extracted => pending.All(chart => chart.Path.StartsWith(extracted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))));
            if (kind == "managed") { Assert.IsFalse(File.Exists(input), "正常展開した管理元を消費しても、保留の展開先を保ちます。"); }
            else { CollectionAssert.AreEqual(archiveBytes, File.ReadAllBytes(input)); }
        }
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>実ZIPから別々に保留されたa/bのうちaを導入しても、独立所持のあるbを追加整理で消さず、aの通常消費とDB・所持・導入済み表示の確定を保ちます。</summary>
    /// <remarks>実取込みから推定先導入への入力保護の接続を確認します。bを選択しない場合と導入先なしで後続見送りの場合を分担し、残存の種類・設定OFF・確定失敗は既存ケースを維持します。専用DB・設定・固定scannerを使い、初期化の背景処理と本体をawaitし、fixtureが当該展開と所有終端を回収します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallPendingPackagesToEstimatedDestinations_SplitZipKeepsOtherPendingInput(bool requestBoth)
    {
        using var fixture = new ManagedArchiveInstallFixture(knownCandidate: true, deletePendingSource: true, knownInstallDestination: true);
        await fixture.InitializeAsync();
        string ownedPath = Path.Combine(fixture.CandidateDirectory, "existing.bms");
        byte[] ownedBytes = File.ReadAllBytes(ownedPath);
        string targetExistingPath = Path.Combine(fixture.InstallDestinationDirectory, "target-existing.bms");
        byte[] targetExistingBytes = File.ReadAllBytes(targetExistingPath);
        string archiveContent = Path.Combine(fixture.Root, "split-source");
        // 通常探索が子フォルダへ降下して単体化すると親整理対象になる。最上段の単体化は親整理しない。
        string source = Path.Combine(archiveContent, "work");
        string a = CreateBmsFile(source, "a.bms", BmsLibraryInitializationTestSupport.CreateValidBmsText("New A").Replace("sound.wav", "missing_a.wav"));
        byte[] aBytes = File.ReadAllBytes(a);
        File.WriteAllBytes(Path.Combine(source, "b.bms"), ownedBytes);
        string child = Path.Combine(source, "Child");
        CreateBmsFile(child, "c.bms", BmsLibraryInitializationTestSupport.CreateValidBmsText("Child").Replace("sound.wav", "child.wav"));
        string archive = Path.Combine(fixture.Root, "split.zip");
        ZipFile.CreateFromDirectory(archiveContent, archive);
        PackageInstallCommandResult imported = await fixture.Library.InstallChartPackagesAutoWithProgressAsync(
            [archive], CancellationToken.None, new RecordingPackageInstallProgressWriter());
        Assert.IsTrue(imported.HasDurableCommit);
        Assert.IsFalse(imported.HasRequiredFailure, imported.EstimationFailure?.ToString());
        Assert.AreEqual(3, fixture.Library.ChartPackagesPending.Count);
        ChartPackage first = fixture.Library.ChartPackagesPending.Single(package => Path.GetFileName(package.path) == "a.bms");
        ChartPackage other = fixture.Library.ChartPackagesPending.Single(package => Path.GetFileName(package.path) == "b.bms");
        ChartPackage childPackage = fixture.Library.ChartPackagesPending.Single(package => Path.GetFileName(package.path) == "Child");
        string childPath = childPackage.path;
        byte[] childBytes = File.ReadAllBytes(Path.Combine(childPath, "c.bms"));
        string firstSourcePath = first.path;
        string otherSourcePath = other.path;
        Assert.AreEqual(Path.GetDirectoryName(firstSourcePath), Path.GetDirectoryName(otherSourcePath));
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly())
        {
            CollectionAssert.AreEquivalent(new[] { firstSourcePath, otherSourcePath, childPath }, db.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
        }
        string destination = fixture.InstallDestinationDirectory;
        Assert.IsTrue(fixture.Library.SetPendingInstallDestination(other.ChartEntries.Single(), string.Empty, false));
        Assert.IsTrue(fixture.Library.SetPendingInstallDestination(first.ChartEntries.Single(), destination, false));

        PendingInstallBatchResult result = fixture.Library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
            requestBoth ? [first, other] : [first]);

        Assert.IsTrue(result.HasDurableCommit);
        Assert.AreEqual(0, result.FailedPackages.Count);
        string installedPath = Path.Combine(destination, "a.bms");
        Assert.IsFalse(File.Exists(firstSourcePath), "本人の通常消費を維持します。");
        CollectionAssert.AreEqual(aBytes, File.ReadAllBytes(installedPath));
        Assert.IsTrue(File.Exists(otherSourcePath), "追加整理で別保留bの入力を消費しません。");
        CollectionAssert.AreEqual(ownedBytes, File.ReadAllBytes(otherSourcePath));
        CollectionAssert.AreEqual(ownedBytes, File.ReadAllBytes(ownedPath));
        CollectionAssert.AreEqual(targetExistingBytes, File.ReadAllBytes(targetExistingPath));
        CollectionAssert.AreEquivalent(new[] { other, childPackage }, fixture.Library.ChartPackagesPending.ToArray());
        CollectionAssert.AreEqual(childBytes, File.ReadAllBytes(Path.Combine(childPath, "c.bms")));
        Assert.IsTrue(string.IsNullOrWhiteSpace(other.ChartEntries.Single().Chart.InstallDestination));
        Assert.AreEqual(installedPath, fixture.Library.ChartPackagesInstalled.Single().ChartEntries.Single().Chart.Path);
        CollectionAssert.AreEquivalent(new[] { ownedPath, targetExistingPath, installedPath }, fixture.Library.BmsCharts.Select(chart => chart.Path).ToArray());
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly())
        {
            CollectionAssert.AreEquivalent(new[] { otherSourcePath, childPath }, db.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
            CollectionAssert.AreEquivalent(new[] { ownedPath, targetExistingPath, installedPath }, db.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        }
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>混在フォルダを実取り込み・保留DB確定・直接推定へ通し、所持先と未所持群が不一致なら入力・保留・候補・警告を保って受付を解放する。</summary>
    /// <remarks>実SQLiteと本番の取り込み継続を通す代表フローです。Strong条件表はサービス、取消と兄弟Task回収は既存workflowケースへ分担します。</remarks>
    [TestMethod]
    public async Task AutoInstall_MixedFolderHashMetadataMismatchKeepsPendingInputAndCandidate()
    {
        using var fixture = new ManagedArchiveInstallFixture(knownCandidate: true);
        await fixture.InitializeAsync();
        string input = fixture.CreateInput("folder", "Different", resources: false);
        File.Copy(Path.Combine(fixture.CandidateDirectory, "existing.bms"), Path.Combine(input, "owned.bms"));
        PackageInstallCommandResult result = await fixture.Library.InstallChartPackagesAutoWithProgressAsync(
            [input], CancellationToken.None, new RecordingPackageInstallProgressWriter());
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsFalse(result.HasRequiredFailure, result.EstimationFailure?.ToString());
        ChartPackage pending = fixture.Library.ChartPackagesPending.Single();
        PackageChartEntry entry = pending.ChartEntries.Single(item => Path.GetFileName(item.Chart.Path) == "chart.bms");
        Assert.IsTrue(string.IsNullOrWhiteSpace(entry.Chart.InstallDestination));
        Assert.AreEqual("Song", entry.Chart.InstallDestinationTitle);
        Assert.AreEqual("Test", entry.Chart.InstallDestinationArtist);
        CollectionAssert.AreEqual(new[] { fixture.CandidateDirectory }, entry.Chart.InstallDestinationSuggestions.ToArray());
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
        Assert.IsTrue(File.Exists(entry.Chart.Path));
        Assert.IsTrue(Directory.Exists(input));
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual(pending.path, db.Table<LR2SongDBExtended.install>().Single().path);
        Assert.AreEqual(1, fixture.Library.BmsCharts.Count);
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        Assert.IsTrue(pending.ChartEntries.All(item => (item.Chart.Status & ChartFileStatus.SEARCHING) == 0));
    }

    /// <summary>既存の音源不足7z・RAR・LZHを借用入力として実native展開から共通導入へ渡し、三譜面の実bytes、install DBと保留正本、借用元と対象外ファイルの保全を確認します。入れ子構造はZIPの共有経路代表へ分担します。</summary>
    /// <remarks>実アーカイブのnative展開、実FSとSQLiteへの確定を通す。管理入力の消費と借用元の保持は、展開portの成功応答だけでは確認できない。</remarks>
    [DataTestMethod]
    [DataRow("fixture.7z")]
    [DataRow("fixture.rar")]
    [DataRow("fixture.lzh")]
    [DoNotParallelize]
    public async Task AutoInstall_NativeArchiveFormatsReachRealPendingStoreWithoutConsumingBorrowedSource(string fileName)
    {
        using var fixture = new ManagedArchiveInstallFixture();
        string input = Path.Combine(fixture.Root, fileName);
        byte[] source = File.ReadAllBytes(GetArchiveFixturePath(fileName));
        File.WriteAllBytes(input, source);
        string unrelated = Path.Combine(fixture.InstallRoot, "unrelated.txt");
        File.WriteAllBytes(unrelated, [8, 4, 2]);
        var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (ZipArchive reference = ZipFile.OpenRead(GetArchiveFixturePath("fixture.zip")))
        {
            foreach (ZipArchiveEntry entry in reference.Entries)
            {
                using Stream content = entry.Open();
                using var bytes = new MemoryStream();
                content.CopyTo(bytes);
                expected.Add(entry.FullName, bytes.ToArray());
            }
        }
        CollectionAssert.AreEquivalent(new[] { "maybe_D.bms", "maybe_E.bms", "maybe_H.bms" }, expected.Keys.ToArray());
        PackageInstallCommandResult result = await fixture.Library.InstallChartPackagesAutoWithProgressAsync(
            [input], CancellationToken.None, new RecordingPackageInstallProgressWriter());
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsFalse(result.HasRequiredFailure, result.EstimationFailure?.ToString());
        Assert.AreEqual(0, result.OperationMessages.Count);
        Assert.AreEqual(0, result.RegisteredPackages.Count);
        Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
        Assert.AreEqual(0, fixture.Library.BmsCharts.Count);
        ChartPackage pending = fixture.Library.ChartPackagesPending.Single();
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), pending.ChartEntries.Select(entry => Path.GetFileName(entry.Chart.Path)).ToArray());
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual(pending.path, db.Table<LR2SongDBExtended.install>().Single().path);
        Assert.AreEqual(0, db.Table<LR2SongDB.song>().Count());
        foreach (PackageChartEntry entry in pending.ChartEntries)
        {
            CollectionAssert.AreEqual(expected[Path.GetFileName(entry.Chart.Path)], File.ReadAllBytes(entry.Chart.Path));
        }
        CollectionAssert.AreEqual(source, File.ReadAllBytes(input));
        CollectionAssert.AreEqual(new byte[] { 8, 4, 2 }, File.ReadAllBytes(unrelated));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>入れ子ZIPを実native展開と導入へ通し、展開entry名と譜面からの音源相対構造・bytes、実DBと所持正本、借用元を確認します。</summary>
    /// <remarks>実アーカイブのnative展開、実FSとSQLiteへの確定を通す。管理入力の消費と借用元の保持は、展開portの成功応答だけでは確認できない。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task AutoInstall_NestedZipPreservesChartRelativeResourceStructureThroughRealInstall()
    {
        using var fixture = new ManagedArchiveInstallFixture();
        string source = Path.Combine(fixture.Root, "nested-source");
        string chartRoot = Path.Combine(source, "wrapper", "work");
        string resource = Path.Combine(chartRoot, "sound", "sub", "tone.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(resource) ?? throw new AssertFailedException());
        byte[] chartBytes = Encoding.ASCII.GetBytes("#PLAYER 1\r\n#TITLE Nested\r\n#BPM 120\r\n#WAV01 sound/sub/tone.wav\r\n#00111:01\r\n");
        File.WriteAllBytes(Path.Combine(chartRoot, "chart.bms"), chartBytes);
        byte[] resourceBytes = [1, 9, 7, 3];
        File.WriteAllBytes(resource, resourceBytes);
        string archive = Path.Combine(fixture.Root, "nested.zip");
        ZipFile.CreateFromDirectory(source, archive);
        byte[] archiveBytes = File.ReadAllBytes(archive);
        var extracted = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        fixture.Files.BeforeTimestamp = path => extracted.Add(path, File.ReadAllBytes(path));
        PackageInstallCommandResult result = await fixture.Library.InstallChartPackagesAutoWithProgressAsync(
            [archive], CancellationToken.None, new RecordingPackageInstallProgressWriter());
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsFalse(result.HasRequiredFailure, result.EstimationFailure?.ToString());
        Assert.AreEqual(0, result.OperationMessages.Count);
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
        string destination = result.RegisteredPackages.Single().ChartEntries.Single().Chart.Path;
        CollectionAssert.AreEqual(chartBytes, File.ReadAllBytes(destination));
        CollectionAssert.AreEqual(resourceBytes, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(destination) ?? throw new AssertFailedException(), "sound", "sub", "tone.wav")));
        Assert.IsTrue(extracted.Keys.Any(path => path.EndsWith(Path.Combine("wrapper", "work", "chart.bms"), StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(extracted.Keys.Any(path => path.EndsWith(Path.Combine("wrapper", "work", "sound", "sub", "tone.wav"), StringComparison.OrdinalIgnoreCase)));
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        Assert.IsNotNull(db.Find<LR2SongDB.song>(destination));
        Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
        Assert.AreEqual(destination, fixture.Library.BmsCharts.Single().Path);
        CollectionAssert.AreEqual(archiveBytes, File.ReadAllBytes(archive));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>
    /// 同じ譜面・リソースの管理ZIP、借用ZIP、フォルダを実非同期入口へ渡します。
    /// 充足時のFS・DB・所持への導入と、不足時の保留行・入力保持・実候補への推定を確認し、全形態の等価な失敗を成功と扱いません。
    /// </summary>
    /// <remarks>実アーカイブのnative展開、実FSとSQLiteへの確定を通す。管理入力の消費と借用元の保持は、展開portの成功応答だけでは確認できない。</remarks>
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

    /// <summary>管理実ZIPと書庫名の通常フォルダを一要求で導入し、二作品の譜面・音源・DB・所持を一回の確定・反映へ集めます。</summary>
    /// <remarks>実native展開から通常の非同期導入とSQLite確定まで接続し、管理ZIPと成功した通常フォルダの消費も確認します。形式条件の網羅は展開境界に分担します。</remarks>
    [TestMethod]
    public async Task AutoInstall_ManagedArchiveAndIndependentFolderShareOneCommitAndApply()
    {
        using var fixture = new ManagedArchiveInstallFixture();
        string archive = fixture.CreateInput("managed", "Archived");
        string folderSource = fixture.CreateInput("folder", "Independent");
        string folder = Path.Combine(fixture.Root, "Independent.ZIP");
        Directory.Move(folderSource, folder);
        var expected = new Dictionary<string, (byte[] Chart, byte[] Audio)>
        {
            ["Independent"] = (File.ReadAllBytes(Path.Combine(folder, "chart.bms")),
                File.ReadAllBytes(Path.Combine(folder, "sound.wav")))
        };
        using (ZipArchive reference = ZipFile.OpenRead(archive))
        {
            byte[] ReadEntry(string name)
            {
                using Stream content = (reference.GetEntry(name) ?? throw new AssertFailedException("比較元のファイルがありません: " + name)).Open();
                using var bytes = new MemoryStream();
                content.CopyTo(bytes);
                return bytes.ToArray();
            }
            expected["Archived"] = (ReadEntry("chart.bms"), ReadEntry("sound.wav"));
        }
        PackageInstallCommandResult result = await fixture.Library.InstallChartPackagesAutoWithProgressAsync(
            [archive, folder], CancellationToken.None, new RecordingPackageInstallProgressWriter());
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsFalse(result.HasRequiredFailure, result.EstimationFailure?.ToString());
        Assert.AreEqual(0, result.OperationMessages.Count);
        Assert.AreEqual(2, result.RegisteredPackages.Count);
        Assert.AreEqual(2, fixture.Library.BmsCharts.Count);
        Assert.AreEqual(2, fixture.Library.ChartPackagesInstalled.Count);
        Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        Assert.IsFalse(File.Exists(archive));
        Assert.IsFalse(Directory.Exists(folder));
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        ChartFile[] installedCharts = result.RegisteredPackages.Select(package => package.ChartEntries.Single().Chart).ToArray();
        string[] destinations = installedCharts.Select(chart => chart.Path).ToArray();
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), installedCharts.Select(chart => chart.Title).ToArray());
        CollectionAssert.AreEquivalent(destinations, db.Table<LR2SongDB.song>().ToArray().Select(row => row.path).ToArray());
        CollectionAssert.AreEquivalent(destinations, fixture.Library.BmsCharts.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEquivalent(destinations, fixture.Library.ChartPackagesInstalled.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart.Path).ToArray());
        Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
        foreach (ChartFile chart in installedCharts)
        {
            (byte[] chartBytes, byte[] audioBytes) = expected[chart.Title];
            CollectionAssert.AreEqual(chartBytes, File.ReadAllBytes(chart.Path));
            string destinationDirectory = Path.GetDirectoryName(chart.Path) ?? throw new AssertFailedException("導入先フォルダがありません。");
            CollectionAssert.AreEqual(audioBytes, File.ReadAllBytes(Path.Combine(destinationDirectory, "sound.wav")));
        }
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>必須日時復元の未知故障による全失敗と壊れZIP・正常ZIP混在で、対象と原因を終端診断へ残し、非致命的警告のまま正常独立対象だけを確定します。</summary>
    /// <remarks>実アーカイブのnative展開、実FSとSQLiteへの確定を通す。管理入力の消費と借用元の保持は、展開portの成功応答だけでは確認できない。</remarks>
    // trueは必須日時復元の全失敗、falseは壊れZIPと正常ZIPの混在による部分成功。
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AutoInstall_ArchiveWarningsSurviveEmptyOrMixedPreparation(bool timestampFailure)
    {
        using var fixture = new ManagedArchiveInstallFixture();
        string rejected = fixture.CreateInput("managed", "Rejected");
        string marker = "required timestamp failure marker";
        if (timestampFailure) { fixture.Files.BeforeTimestamp = _ => throw new InvalidOperationException(marker); }
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
        Assert.IsTrue(fixture.ExtractedRoots.All(path => !Directory.Exists(path)), "必須日時復元が不成立のprivate展開先は採用せず回収します。");
        Assert.AreEqual(0, fixture.Dialogs.ModelMessages);
        Assert.IsFalse(File.Exists(rejected));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual(timestampFailure ? 0 : 1, db.Table<LR2SongDB.song>().Count());
        Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
    }
}
