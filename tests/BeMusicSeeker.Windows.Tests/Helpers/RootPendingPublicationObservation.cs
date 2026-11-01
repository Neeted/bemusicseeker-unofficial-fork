using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

/// <summary>root確定の実保留通知を受信し、実親受付とモデル保護の解放・DB/current/画面入力一致を本体で判定する専用観測です。</summary>
internal sealed class RootPendingPublicationObservation : IDisposable
{
    private readonly BMSLibrary library;
    private readonly string database;
    private readonly string remainingPackage;
    private readonly ChartFileOperationSynchronizer? playlistAdmission;
    private readonly string unrelatedChart;
    private readonly Dictionary<string, byte[]> preservedBytes;
    private readonly ReaderWriterLockSlimWrapper[] guards;
    private readonly List<Snapshot> snapshots = [];
    private readonly List<Exception> failures = [];
    private readonly TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly InstallTreeViewModel tree = new();

    private sealed record Snapshot(bool Dispatcher, bool LibraryReleased, bool PlaylistReleased, bool GuardsReleased,
        string[] Current, string[] Database, string[] Presented, string[] ReadPaths);

    private RootPendingPublicationObservation(BMSLibrary library, string database, string remainingPackage,
        string unrelatedChart, IEnumerable<string> preservedPaths, ChartFileOperationSynchronizer? playlistAdmission)
    {
        this.library = library;
        this.database = database;
        this.remainingPackage = remainingPackage;
        this.unrelatedChart = unrelatedChart;
        this.playlistAdmission = playlistAdmission;
        preservedBytes = preservedPaths.ToDictionary(path => path, File.ReadAllBytes);
        guards = new[] { "rwlockBMSFilesInitializedAll", "rwlockPendingInstallCharts", "rwlockSongDBInstall" }
            .Select(name => typeof(BMSLibrary).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(library)
                as ReaderWriterLockSlimWrapper ?? throw new InvalidOperationException("Missing model guard: " + name)).ToArray();
        tree.AttachLibrary(library);
        tree.PresentationChanged += OnPresentationChanged;
    }

    /// <summary>残る実走査root内とroot外へ独立した保留を登録し、実通知の受信だけを観測します。</summary>
    internal static RootPendingPublicationObservation Seed(BMSLibrary library, string database, string retainedRoot,
        string unrelatedRoot, ChartFileOperationSynchronizer? playlistAdmission = null)
    {
        string pendingPath = Path.Combine(Directory.CreateDirectory(retainedRoot).FullName, "pending-only.bms");
        string unrelatedPath = Path.Combine(Directory.CreateDirectory(unrelatedRoot).FullName, "unrelated-pending.bms");
        File.WriteAllText(pendingPath, "#TITLE Root pending\n#BPM 120\n");
        File.WriteAllText(unrelatedPath, "#TITLE Unrelated pending\n#BPM 120\n");
        ChartPackage Package(string path)
        {
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))]);
            package.path = Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
            return package;
        }
        ChartPackage removed = Package(pendingPath);
        ChartPackage remaining = Package(unrelatedPath);
        library.ChartPackagesPending = new ObservableCollection<ChartPackage>([removed, remaining]);
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(library.DatabaseScope, database, db =>
        {
            db.CreateTable<LR2SongDBExtended.install>();
            db.InsertOrReplace(removed, typeof(LR2SongDBExtended.install));
            db.InsertOrReplace(remaining, typeof(LR2SongDBExtended.install));
        });
        return new(library, database, remaining.path, unrelatedPath, [pendingPath, unrelatedPath], playlistAdmission);
    }

    internal Task Received => received.Task;
    internal int Count => snapshots.Count;

    private void OnPresentationChanged(object? sender, InstallTreePresentationChangedEventArgs args)
    {
        if ((args.Sections & InstallTreePresentationSection.Pending) == 0) { return; }
        try
        {
            bool guardsReleased = guards.All(guard => guard.LockingReadCount == 0 && guard.LockingWriteCount == 0 && !guard.IsUpgradeableReadLockHeld);
            using LR2SongDBExtended db = library.DatabaseScope.OpenSongReadOnly(database);
            snapshots.Add(new(TestUiDispatcherHost.Dispatcher.CheckAccess(), !library.OperationAdmission.IsActive,
                playlistAdmission?.IsActive != true, guardsReleased,
                library.ChartPackagesPending.Select(package => package.path).ToArray(),
                db.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray(),
                tree.ChartPackagesPending.Select(package => package.path).ToArray(),
                guardsReleased ? library.GetPendingBmsFormatChartFilesSnapshot().Select(chart => chart.Path).ToArray() : []));
        }
        catch (Exception failure) { failures.Add(failure); }
        finally { received.TrySetResult(); }
    }

    /// <summary>callback内の失敗をbest-effortに隠さず、操作Task終端後の本体で記録を検査します。</summary>
    internal void AssertPublishedAfterParentRelease()
    {
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        Assert.IsTrue(snapshots.Count > 0, "保留一覧getterの直読ではなく実通知を受信します。");
        foreach (Snapshot snapshot in snapshots)
        {
            Assert.IsTrue(snapshot.Dispatcher);
            Assert.IsTrue(snapshot.LibraryReleased);
            Assert.IsTrue(snapshot.PlaylistReleased);
            Assert.IsTrue(snapshot.GuardsReleased);
            CollectionAssert.AreEqual(new[] { remainingPackage }, snapshot.Current);
            CollectionAssert.AreEqual(new[] { remainingPackage }, snapshot.Database);
            CollectionAssert.AreEqual(new[] { remainingPackage }, snapshot.Presented);
            CollectionAssert.AreEqual(new[] { unrelatedChart }, snapshot.ReadPaths);
        }
        foreach ((string path, byte[] bytes) in preservedBytes) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
    }

    public void Dispose()
    {
        tree.PresentationChanged -= OnPresentationChanged;
        tree.DetachLibrary();
        tree.Dispose();
    }
}
