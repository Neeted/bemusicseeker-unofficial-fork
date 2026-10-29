using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ResourceBearingNestedRestoreTests
{
    /// <summary>実DBの保存済み入力を新libraryで完全復元し、健康度の未使用解放前に禁止と親行警告を再捕捉します。強制導入でも元bytes・保存行・所持を保ちます。</summary>
    [TestMethod]
    public async Task Initialize_ResourceBearingNestedChartsAreRestoredBeforePhysicalInstall()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ResourceBearingNestedRestoreTests), Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "Input");
        string child = Path.Combine(source, "child");
        string libraryRoot = Path.Combine(root, "Library");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(libraryRoot);
        File.WriteAllText(Path.Combine(source, "parent.bms"), "#TITLE Parent\n#WAV01 parent.wav\n");
        File.WriteAllText(Path.Combine(child, "child.pms"), "#TITLE Child\n#BMP01 child.png\n");
        File.WriteAllBytes(Path.Combine(source, "parent.wav"), [1, 2]);
        File.WriteAllBytes(Path.Combine(child, "child.png"), [3, 4]);
        Dictionary<string, byte[]> preserved = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        string songDb = Path.Combine(root, "song.db");
        ExecuteSongDbFixtureTransaction(songDb, db =>
        {
            db.CreateTable<LR2SongDBExtended.install>();
            db.Insert(new LR2SongDBExtended.install { path = source, delete_parent = false });
        });
        var background = new ConcurrentQueue<Func<Task>>();
        TestBmsLibrary? library = null;
        try
        {
            library = new TestBmsLibrary(songDb, null, null, null, new RecordingDialogService(), new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true, BMSInstallDir = libraryRoot },
                CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), [libraryRoot]),
                settings: TestBmsFactory.CreateRootSettings([libraryRoot]))
            { StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; } };
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            ChartPackage restored = library.ChartPackagesPending.Single();
            Assert.IsTrue(restored.HasResourceBearingNestedCharts);
            Assert.IsTrue(restored.ChartEntries.All(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBearingNestedCharts)));

            library.ForceInstallPendingPackages([restored], true);

            Assert.AreSame(restored, library.ChartPackagesPending.Single());
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.AreEqual(0, library.BmsCharts.Count);
            foreach ((string path, byte[] before) in preserved) { CollectionAssert.AreEqual(before, File.ReadAllBytes(path)); }
            using LR2SongDBExtended db = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly();
            Assert.AreEqual(source, db.Table<LR2SongDBExtended.install>().Single().path);
            Assert.IsFalse(library.OperationAdmission.IsActive);
        }
        finally
        {
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            library?.RequestShutdown("nested-restore-fixture");
            Directory.Delete(root, recursive: true);
        }
    }
}
