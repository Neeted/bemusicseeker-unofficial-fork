using System.Collections.Concurrent;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ResourceBearingNestedRestoreTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>ケース開始後だけ利用できる、可変ファイル領域の所有者です。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>constructorと既存field準備の成功後にケース専有領域を取得します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>既存finallyと接続・Task終端の後にケース専有領域を回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実DBの親子重複を新libraryで一式へ揃え、健康度の未使用解放前に禁止と親行警告を再捕捉します。強制導入でも元bytes・存続保存行・所持を保ちます。</summary>
    [TestMethod]
    public async Task Initialize_ResourceBearingNestedChartsAreRestoredBeforePhysicalInstall()
    {
        string root = Path.Combine(CaseWorkspace.RootDirectory, nameof(ResourceBearingNestedRestoreTests), Guid.NewGuid().ToString("N"));
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
        ExecuteSongDbFixtureTransaction(databaseScope, songDb, db =>
        {
            db.CreateTable<LR2SongDBExtended.install>();
            db.Insert(new LR2SongDBExtended.install { path = source, delete_parent = false });
            db.Insert(new LR2SongDBExtended.install { path = child, delete_parent = true });
        });
        var background = new ConcurrentQueue<Func<Task>>();
        TestBmsLibrary? library = null;
        try
        {
            library = new TestBmsLibrary(databaseScope, songDb, null, null, null, new RecordingDialogService(), new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true, BMSInstallDir = libraryRoot },
                CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), [libraryRoot]),
                settings: TestBmsFactory.CreateRootSettings([libraryRoot]))
            { StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; } };
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            ChartPackage restored = library.ChartPackagesPending.Single();
            Assert.AreEqual(source, restored.path);
            Assert.IsFalse(restored.delete_parent, "同じ親単位を保持し、旧子のtrueをORしません。");
            Assert.IsTrue(restored.HasResourceBearingNestedCharts);
            Assert.IsTrue(restored.ChartEntries.All(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBearingNestedCharts)));

            library.ForceInstallPendingPackages([restored], true);

            Assert.AreSame(restored, library.ChartPackagesPending.Single());
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.AreEqual(0, library.BmsCharts.Count);
            foreach ((string path, byte[] before) in preserved) { CollectionAssert.AreEqual(before, File.ReadAllBytes(path)); }
            using LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).OpenSongDbReadOnly();
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
