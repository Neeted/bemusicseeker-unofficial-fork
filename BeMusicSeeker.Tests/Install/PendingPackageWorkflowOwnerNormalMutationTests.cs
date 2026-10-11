using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class PendingPackageWorkflowOwnerTests
{
    /// <summary>全所持folderと単体の元を実owner・storeで削除し、混在・未所持の保留元とinstall行、所持コピー、単体の無関係な兄弟ファイルを保ちます。</summary>
    /// <remarks>実解析結果から対象を分類し、本番storeを介した物理変更・SQLite・公開集合への反映を観測する。衝突先や対象外のbytesは実FSで保全を確認する。</remarks>
    [TestMethod]
    public async Task DeleteInstalledOnlyPendingPackageSourcesAsync_RealStoreDeletesOnlyAllOwnedSources()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TestBmsLibrary? library = null;
        try
        {
            ChartFile WriteChart(string directory, string name, string title)
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, name);
                File.WriteAllText(path, "#PLAYER 1\r\n#TITLE " + title + "\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
                return BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
            }
            ChartPackage Package(string path, params ChartFile[] charts)
            {
                var package = ChartPackage.FromChartEntries(charts.Select(PackageChartEntry.FromChart));
                package.path = path;
                package.delete_parent = false;
                return package;
            }
            string installed = Path.Combine(root, "installed");
            ChartFile ownedA = WriteChart(installed, "a.bms", "owned A");
            ChartFile ownedB = WriteChart(installed, "b.bms", "owned B");
            ChartFile ownedC = WriteChart(installed, "c.bms", "owned C");
            string allRoot = Path.Combine(root, "all-owned");
            ChartPackage all = Package(allRoot, WriteChart(allRoot, "a.bms", "owned A"), WriteChart(allRoot, "b.bms", "owned B"));
            string singleRoot = Path.Combine(root, "single");
            ChartFile singleChart = WriteChart(singleRoot, "c.bms", "owned C");
            ChartPackage single = Package(singleChart.Path, singleChart);
            string sibling = Path.Combine(singleRoot, "unrelated.txt");
            File.WriteAllBytes(sibling, [4, 8, 2]);
            string mixedRoot = Path.Combine(root, "mixed");
            ChartPackage mixed = Package(mixedRoot, WriteChart(mixedRoot, "owned.bms", "owned A"), WriteChart(mixedRoot, "new.bms", "unowned mixed"));
            string unownedRoot = Path.Combine(root, "unowned");
            ChartPackage unowned = Package(unownedRoot, WriteChart(unownedRoot, "new.bms", "unowned standalone"));
            ChartPackage[] packages = [all, single, mixed, unowned];
            var preserved = new[] { ownedA.Path, ownedB.Path, ownedC.Path, sibling }
                .Concat(mixed.ChartEntries.Select(entry => entry.Chart.Path)).Concat(unowned.ChartEntries.Select(entry => entry.Chart.Path))
                .ToDictionary(path => path, File.ReadAllBytes);
            library = CreateNormalPendingLibrary(root, packages, [ownedA, ownedB, ownedC]);
            PendingPackageWorkflowOwner owner = CreateOwner(() => library, [], new BmsLibraryPendingPackageStore(), AcceptedDialogs(),
                chartFileOperations: library.OperationAdmission, playback: new NoOpChartMutationPlaybackPort());

            await owner.DeleteInstalledOnlyPendingPackageSourcesAsync();

            Assert.IsFalse(Directory.Exists(allRoot));
            Assert.IsFalse(File.Exists(singleChart.Path));
            CollectionAssert.AreEquivalent(new[] { mixed.path, unowned.path }, library.ChartPackagesPending.Select(package => package.path).ToArray());
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(Path.Combine(root, "song.db")).OpenSongDbReadOnly();
            CollectionAssert.AreEquivalent(new[] { mixed.path, unowned.path }, verify.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
            foreach ((string path, byte[] bytes) in preserved) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
            CollectionAssert.AreEquivalent(new[] { ownedA.Path, ownedB.Path, ownedC.Path }, library.BmsCharts.Select(chart => chart.Path).ToArray());
            Assert.IsFalse(library.OperationAdmission.IsActive);
        }
        finally { library?.RequestShutdown("normal-pending-delete-fixture"); Directory.Delete(root, recursive: true); }
    }

    /// <summary>実0note BMS/PMSだけをowner・store経由で無効化し、異内容ファイル・directoryとの衝突先を保ちます。非0note・BMSON・既無効のbytesと保留・install行は維持します。</summary>
    /// <remarks>実解析結果から対象を分類し、本番storeを介した物理変更・SQLite・公開集合への反映を観測する。衝突先や対象外のbytesは実FSで保全を確認する。</remarks>
    [TestMethod]
    public async Task RenamePendingZeroNoteChartsAsync_RealStoreInvalidatesOnlyZeroNoteBmsAndPms()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PendingPackageWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TestBmsLibrary? library = null;
        try
        {
            string pendingRoot = Path.Combine(root, "pending");
            Directory.CreateDirectory(pendingRoot);
            ChartFile WriteBms(string name, bool notes)
            {
                string path = Path.Combine(pendingRoot, name);
                File.WriteAllText(path, "#PLAYER 1\r\n#TITLE " + name + "\r\n#BPM 120\r\n" + (notes ? "#00111:01\r\n" : string.Empty), Encoding.ASCII);
                return BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
            }
            ChartFile zeroBms = WriteBms("zero.bms", false);
            ChartFile zeroPms = WriteBms("zero.pms", false);
            ChartFile nonzero = WriteBms("nonzero.bms", true);
            ChartFile disabled = WriteBms("disabled.bmx", false);
            string bmsonPath = Path.Combine(pendingRoot, "zero.bmson");
            File.WriteAllText(bmsonPath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"zero BMSON\",\"init_bpm\":120},\"sound_channels\":[]}");
            ChartFile bmson = ChartTestValues.ReadBmson(bmsonPath);
            string collision = Path.Combine(pendingRoot, "zero.bmx");
            File.WriteAllBytes(collision, [9, 3, 7]);
            string directoryCollision = Path.Combine(pendingRoot, "zero.pmx");
            Directory.CreateDirectory(directoryCollision);
            string directoryMarker = Path.Combine(directoryCollision, "kept.txt");
            File.WriteAllBytes(directoryMarker, [5, 1]);
            var package = ChartPackage.FromChartEntries(new[] { zeroBms, zeroPms, nonzero, disabled, bmson }.Select(PackageChartEntry.FromChart));
            package.path = pendingRoot;
            package.delete_parent = false;
            string[] unchanged = [nonzero.Path, disabled.Path, bmsonPath, collision, directoryMarker];
            var bytes = unchanged.Concat(new[] { zeroBms.Path, zeroPms.Path }).ToDictionary(path => path, File.ReadAllBytes);
            library = CreateNormalPendingLibrary(root, [package], []);
            PendingPackageWorkflowOwner owner = CreateOwner(() => library, [], new BmsLibraryPendingPackageStore(), AcceptedDialogs(),
                chartFileOperations: library.OperationAdmission, playback: new NoOpChartMutationPlaybackPort());

            await owner.RenamePendingZeroNoteChartsAsync();

            Assert.IsFalse(File.Exists(zeroBms.Path));
            Assert.IsFalse(File.Exists(zeroPms.Path));
            CollectionAssert.AreEqual(bytes[zeroBms.Path], File.ReadAllBytes(Path.Combine(pendingRoot, "zero(1).bmx")));
            CollectionAssert.AreEqual(bytes[zeroPms.Path], File.ReadAllBytes(Path.Combine(pendingRoot, "zero(1).pmx")));
            foreach (string path in unchanged) { CollectionAssert.AreEqual(bytes[path], File.ReadAllBytes(path)); }
            Assert.IsTrue(Directory.Exists(directoryCollision));
            string[] expected = [nonzero.Path, disabled.Path, bmsonPath];
            CollectionAssert.AreEquivalent(expected, library.ChartPackagesPending.SelectMany(item => item.ChartEntries).Select(entry => entry.Chart.Path).ToArray());
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(Path.Combine(root, "song.db")).OpenSongDbReadOnly();
            Assert.AreEqual(package.path, verify.Table<LR2SongDBExtended.install>().Single().path);
            Assert.AreEqual(0, library.BmsCharts.Count);
            Assert.AreEqual(0, library.BmsonCharts.Count);
            Assert.IsFalse(library.OperationAdmission.IsActive);
        }
        finally { library?.RequestShutdown("normal-pending-rename-fixture"); Directory.Delete(root, recursive: true); }
    }

    /// <summary>高度操作の正常代表が所有する実song・install DBと保留集合、独立した所持入力を準備します。</summary>
    private static TestBmsLibrary CreateNormalPendingLibrary(string root, ChartPackage[] packages, ChartFile[] owned)
    {
        string songDb = Path.Combine(root, "song.db");
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDb, db =>
        {
            db.CreateTable<LR2SongDB.song>();
            db.CreateTable<LR2SongDB.folder>();
            db.CreateTable<LR2SongDBExtended.maintenance>();
            db.CreateTable<LR2SongDBExtended.bmson_song>();
            db.CreateTable<LR2SongDBExtended.install>();
            foreach (ChartPackage package in packages)
            { db.Insert(new LR2SongDBExtended.install { path = package.path, delete_parent = false }); }
        });
        return new TestBmsLibrary(songDb, null, null, new ResilientFileMutationService(), new RecordingLibraryDialogService(),
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true })
        {
            BmsCharts = [.. owned],
            BmsonCharts = [],
            ChartPackagesPending = [.. packages],
            ChartPackagesInstalled = []
        };
    }
}
