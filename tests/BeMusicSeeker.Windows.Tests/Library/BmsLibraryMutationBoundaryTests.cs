using System.Collections.ObjectModel;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryMutationBoundaryTests
{
    /// <summary>譜面だけの削除・無効化を実sessionへ通し、独立した固定最終FSの初回索引と六分類・所有境界・三分類の候補逆引きを一致させます。</summary>
    [TestMethod]
    [DataRow("last", false)]
    [DataRow("last", true)]
    [DataRow("remaining", false)]
    [DataRow("parent", false)]
    [DataRow("child", false)]
    public void ChartOnlyMutation_MatchesIndependentFinalResourceIndex(string condition, bool disable)
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(database =>
        {
            string workspace = Path.GetDirectoryName(database) ?? throw new AssertFailedException();
            string actual = Path.Combine(workspace, "Actual"), expected = Path.Combine(workspace, "Expected");
            string selectedRelative = condition == "child" ? Path.Combine("P", "C", "selected.bms") : Path.Combine("P", "selected.bms");
            var survivors = new List<string>();
            if (condition == "remaining") { survivors.Add(Path.Combine("P", "kept.bms")); }
            if (condition == "parent") { survivors.Add(Path.Combine("P", "C", "kept.bms")); }
            if (condition == "child") { survivors.Add(Path.Combine("P", "kept.bms")); }
            foreach (string root in new[] { actual, expected })
            {
                Directory.CreateDirectory(Path.Combine(root, "P", "C"));
                foreach (string relative in survivors.Concat(root == actual ? new[] { selectedRelative } : []))
                {
                    File.WriteAllText(Path.Combine(root, relative), "#TITLE " + relative + "\n#BPM 120\n");
                }
                foreach (string relative in new[] { "P/tone.wav", "P/cover.png", "P/movie.mpg", "P/C/child.wav", "P/C/child.png", "P/C/child.mpg" })
                { File.WriteAllText(Path.Combine(root, relative), "resource"); }
            }
            var library = new TestBmsLibrary(databaseScope, database, null, null, new ResilientFileMutationService(),
                new RecordingDialogService(), new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            {
                BmsCharts = Directory.EnumerateFiles(actual, "*.bms", SearchOption.AllDirectories)
                    .Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path))).ToList(),
                BmsonCharts = []
            };
            try
            {
                BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, database, db =>
                { foreach (ChartFile chart in library.BmsCharts) { db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song)); } });
                LibraryResourceIndexOwner owner = LibraryResourceIndexTestSupport.GetOwner(library);
                owner.Replace(LibraryResourceIndex.CreateFromScanResult(ChartDirectoryScanBuilder.BuildFromRoots([actual])));
                long generation = owner.CaptureSnapshot().Generation;
                uint[][] lookupKeys = [
                    [ChartResourceKeyHash.GetLookupHash("tone.wav"), ChartResourceKeyHash.GetLookupHash("child.wav"), ChartResourceKeyHash.GetLookupHash(Path.Combine("C", "child.wav"))],
                    [ChartResourceKeyHash.GetLookupHash("cover.png"), ChartResourceKeyHash.GetLookupHash("child.png"), ChartResourceKeyHash.GetLookupHash(Path.Combine("C", "child.png"))],
                    [ChartResourceKeyHash.GetLookupHash("movie.mpg"), ChartResourceKeyHash.GetLookupHash("child.mpg"), ChartResourceKeyHash.GetLookupHash(Path.Combine("C", "child.mpg"))]
                ];
                // 消失した所有dirの既存候補bucketも、実sessionから除去されることを確認します。
                Func<uint, IReadOnlyCollection<string>>[] previousLookups = Lookups(owner.CaptureSnapshot().DirectoryLookupCache);
                for (int category = 0; category < lookupKeys.Length; category++)
                {
                    foreach (uint key in lookupKeys[category]) { _ = previousLookups[category](key); }
                }

                ChartFile selected = library.BmsCharts.Single(chart => chart.Path == Path.Combine(actual, selectedRelative));
                if (disable)
                {
                    LibraryMutationSessionReceipt receipt = library.RenameBMSFilesExtensionsWithReceipt(
                        [library.PrepareLibraryFileExtensionRenameBatch([selected], ".disabled")], new LibraryOperationNotices(), unregister: true);
                    Assert.IsFalse(receipt.HasRequiredFailure);
                }
                else
                {
                    LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile(selected)]), false, [], new LibraryOperationNotices());
                    Assert.IsFalse(outcome.HasError);
                }
                LibraryResourceIndexSnapshot current = owner.CaptureSnapshot();
                Assert.AreEqual(generation + (condition == "remaining" ? 0 : 1), current.Generation);
                DirectoryResourceLookupCache expectedCache = LibraryResourceIndex.CreateFromScanResult(ChartDirectoryScanBuilder.BuildFromRoots([expected])).DirectoryLookupCache;
                foreach (string relative in new[] { "P", Path.Combine("P", "C") })
                {
                    DirectoryResourceLookupCache.Entry expectedEntry = expectedCache.GetEntryOrNull(Path.Combine(expected, relative));
                    DirectoryResourceLookupCache.Entry actualEntry = current.DirectoryLookupCache.GetEntryOrNull(Path.Combine(actual, relative));
                    if (expectedEntry == null) { Assert.IsNull(actualEntry, relative); continue; }
                    Assert.IsNotNull(actualEntry, relative);
                    uint[][] expectedValues = [expectedEntry.AudioRelativePathHashArray, expectedEntry.ImageRelativePathHashArray, expectedEntry.MovieRelativePathHashArray,
                        expectedEntry.SelfOwnedAudioRelativePathHashArray, expectedEntry.SelfOwnedImageRelativePathHashArray, expectedEntry.SelfOwnedMovieRelativePathHashArray];
                    uint[][] actualValues = [actualEntry.AudioRelativePathHashArray, actualEntry.ImageRelativePathHashArray, actualEntry.MovieRelativePathHashArray,
                        actualEntry.SelfOwnedAudioRelativePathHashArray, actualEntry.SelfOwnedImageRelativePathHashArray, actualEntry.SelfOwnedMovieRelativePathHashArray];
                    for (int category = 0; category < 6; category++) { CollectionAssert.AreEquivalent(expectedValues[category], actualValues[category], relative); }
                }
                Func<uint, IReadOnlyCollection<string>>[] expectedLookups = Lookups(expectedCache);
                Func<uint, IReadOnlyCollection<string>>[] actualLookups = Lookups(current.DirectoryLookupCache);
                for (int category = 0; category < lookupKeys.Length; category++)
                {
                    foreach (uint key in lookupKeys[category])
                    {
                        CollectionAssert.AreEqual(
                            expectedLookups[category](key).Select(path => Path.GetRelativePath(expected, path)).ToArray(),
                            actualLookups[category](key).Select(path => Path.GetRelativePath(actual, path)).ToArray(),
                            $"candidate category={category} key={key}");
                    }
                }
                Assert.IsTrue(Directory.Exists(Path.Combine(actual, "P", "C")));
            }
            finally { library.RequestShutdown("chart-only-resource-boundary"); }
            static Func<uint, IReadOnlyCollection<string>>[] Lookups(DirectoryResourceLookupCache cache)
                => [cache.GetDirectoriesByAudioRelativeHash, cache.GetDirectoriesByImageRelativeHash, cache.GetDirectoriesByMovieRelativeHash];

        });
    }

    /// <summary>
    /// 保証：モデルのOKだけの警告は、操作の収集先へ重複を除いて加わり、モデルの窓口へは同期にも待たない投函でも届かない。
    /// 実通知には対象パスを保持する。
    /// </summary>
    /// <remarks>実ライブラリのフォルダ変更から警告を生産し、収集先への実接続と重複の除去を確認する。同期の即時表示や投函へ落ちる誤りを検出する。</remarks>
    [TestMethod]
    public void RenameChartFolder_AddsOkNoticeToOperationCollectorWithoutModelDisplay()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService);
            string missingDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MutationBoundary_" + Guid.NewGuid().ToString("N"));
            var notices = new LibraryOperationNotices();

            library.RenameChartFolder(missingDirectoryPath, "RenamedFolder", notices);
            library.RenameChartFolder(missingDirectoryPath, "RenamedFolder", notices);

            Assert.AreEqual(0, dialogService.CallCount, "モデルの窓口へ同期に表示しない。");
            Assert.AreEqual(0, dialogService.Posted.Count, "モデルの窓口へ投函しない。");
            IReadOnlyList<BMSLibrary.OperationDialogMessage> collected = notices.Snapshot();
            Assert.AreEqual(1, collected.Count, "同じ警告は一件へまとめる。");
            StringAssert.Contains(collected[0].MessageBoxText, missingDirectoryPath);
        });
    }

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    /// <summary>
    /// 保証：保存済み通常・追加・ルート形式の管理出力と移動元・移動先・祖先が交差する変更だけをP活動中に拒否し、非交差は確定する。
    /// </summary>
    /// <remarks>実ディレクトリ移動とDB更新を使い、交差拒否時の無副作用およびP解放後の明示再要求成功を観測する。</remarks>
    [TestMethod]
    [DataRow("normal", "source")]
    [DataRow("normal", "destination")]
    [DataRow("normal", "ancestor")]
    [DataRow("normal", "disjoint")]
    [DataRow("root", "source")]
    [DataRow("additional", "source")]
    public void FolderMutation_UsesPlaylistAdmissionOnlyForActualManagedIntersection(string role, string intersection)
    {
        var databaseScope = new ApplicationDatabaseScope();

        OwnedChartCollectionTestSupport.WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string baseDirectory = Path.Combine(root, "Managed");
            string sourceDirectory = intersection switch
            {
                "source" => Path.Combine(baseDirectory, "Pack"),
                "ancestor" => Path.Combine(root, "Container"),
                _ => Path.Combine(root, "Source")
            };
            if (intersection == "ancestor") { baseDirectory = Path.Combine(sourceDirectory, "Managed"); }
            string newName = intersection == "destination" ? "Managed" : "Moved";
            string destinationDirectory = Path.Combine(Path.GetDirectoryName(sourceDirectory) ?? root, newName);
            Directory.CreateDirectory(sourceDirectory);
            string path = Path.Combine(sourceDirectory, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1\n#TITLE Scope\n");
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = role == "normal" ? baseDirectory : Path.Combine(root, "OtherNormal"),
                LR2CustomFolderOutputBaseDirRootType = role == "root" ? baseDirectory : Path.Combine(root, "OtherRoot"),
                LR2CustomFolderAdditionalOutputBaseDirs = role == "additional" ? [baseDirectory] : []
            };
            var dialogs = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, dialogs,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), () => options,
                settings: TestBmsFactory.CreateRootSettings([], lr2: true))
            { BmsCharts = [OwnedChartCollectionTestSupport.CreateFile(new string('a', 32), path)], BmsonCharts = [] };
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs(library.BmsCharts);
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable playlistLease));
            try
            {
                var notices = new LibraryOperationNotices();
                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(sourceDirectory, newName, notices);
                if (intersection == "disjoint")
                {
                    Assert.AreEqual(1, receipt.ConfirmedChangeCount);
                    Assert.IsFalse(File.Exists(path));
                    Assert.IsTrue(File.Exists(Path.Combine(destinationDirectory, "chart.bms")));
                }
                else
                {
                    Assert.AreEqual(0, receipt.ConfirmedChangeCount);
                    Assert.IsTrue(File.Exists(path));
                    Assert.IsFalse(Directory.Exists(destinationDirectory));
                    Assert.IsTrue(notices.Snapshot().Count > 0, "拒否の理由は操作の収集先へ加わる。");
                    Assert.AreEqual(0, dialogs.CallCount, "モデルの窓口へ同期に表示しない。");
                    Assert.AreEqual(0, dialogs.Posted.Count, "モデルの窓口へ投函しない。");
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
                    { Assert.AreEqual(path, db.Table<LR2SongDB.song>().Single().path); }
                    playlistLease.Dispose();
                    receipt = library.RenameChartFolderWithReceipt(sourceDirectory, newName, new LibraryOperationNotices());
                    Assert.AreEqual(1, receipt.ConfirmedChangeCount);
                    Assert.IsFalse(File.Exists(path));
                    Assert.IsTrue(File.Exists(Path.Combine(destinationDirectory, "chart.bms")));
                }
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
                { Assert.AreEqual(Path.Combine(destinationDirectory, "chart.bms"), db.Table<LR2SongDB.song>().Single().path); }
            }
            finally { playlistLease.Dispose(); library.RequestShutdown("managed-intersection-test"); }
        });
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        using var caseWorkspace2 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace2.RootDirectory, "BeMusicSeeker_MutationBoundaryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        private readonly object lockObject = new();

        public List<DialogCall> Calls { get; } = [];

        public MessageBoxResult ResultToReturn { get; set; } = MessageBoxResult.OK;

        public int CallCount
        {
            get
            {
                lock (lockObject)
                {
                    return Calls.Count;
                }
            }
        }

        public DialogCall GetCall(int index)
        {
            lock (lockObject)
            {
                return Calls[index];
            }
        }

        public MessageBoxResult Confirm(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            lock (lockObject)
            {
                Calls.Add(new DialogCall
                {
                    Message = messageBoxText,
                    Caption = caption,
                    Button = button,
                    Icon = icon,
                    DefaultResult = defaultResult
                });
            }
            return ResultToReturn;
        }

        public List<BMSLibrary.OperationDialogMessage> Posted { get; } = [];

        public void Post(IReadOnlyList<BMSLibrary.OperationDialogMessage> messages, Func<bool> shouldSuppress)
        {
            lock (lockObject)
            {
                Posted.AddRange(messages);
            }
        }
    }

    private sealed class DialogCall
    {
        public string Message { get; set; } = string.Empty;

        public string Caption { get; set; } = string.Empty;

        public MessageBoxButton Button { get; set; }

        public MessageBoxImage Icon { get; set; }

        public MessageBoxResult DefaultResult { get; set; }
    }

}
