using System.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using Microsoft.VisualBasic.FileIO;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class BmsLibraryFolderRenameRefreshTestSupport
{
    /// <summary>日常と大規模条件で同じ実移転後の祖先資源反映を観測します。</summary>
    /// <param name="reportOperationElapsed">指定時だけ準備・表明・回収を除く実操作の経過を終端後に通知します。省略時は時計を追加しません。</param>
    /// <param name="uiScheduler">日常のheadless検査で明示する通知境界。省略時は既存の共有Dispatcherを使います。</param>
    internal static void AssertFinalFilesystemAncestors(ApplicationDatabaseScope databaseScope, bool move, int backgroundEntries, int parentCharts, Action<TimeSpan>? reportOperationElapsed = null, IUiScheduler? uiScheduler = null)
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string common = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "Library");
            string oldParent = Path.Combine(common, "Old");
            string newParent = Path.Combine(common, "New");
            string source = Path.Combine(oldParent, "Child");
            string sibling = Path.Combine(common, "OldBackup");
            string destination = Path.Combine(move ? newParent : oldParent, move ? "Child" : "Renamed");
            string references = "#WAV01 Child/sound.wav\n#BMP01 Child/image.png\n#BMP02 Child/movie.mpg\n#00111:01\n#00104:01\n#00204:02\n";
            var charts = new List<ChartFile>
            {
                LibraryResourceIndexTestSupport.CreateChart(common, "common.bms"),
                LibraryResourceIndexTestSupport.CreateChart(newParent, "new.bms"),
                LibraryResourceIndexTestSupport.CreateChart(sibling, "sibling.bms")
            };
            for (int i = 0; i < parentCharts; i++) { charts.Add(LibraryResourceIndexTestSupport.CreateChart(oldParent, "parent-" + i + ".bms", references)); }
            ChartFile parentChart = charts[3];
            ChartFile target = LibraryResourceIndexTestSupport.CreateChart(source, "target.bms");
            charts.Add(target);
            foreach (string resource in new[] { "sound.wav", "image.png", "movie.mpg" }) { File.WriteAllText(Path.Combine(source, resource), resource); }
            File.WriteAllText(Path.Combine(oldParent, "own.wav"), "parent owned");
            File.WriteAllText(Path.Combine(sibling, "keep.wav"), "sibling resource");
            var port = new AncestorEnumerationPort(common);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
                uiScheduler ?? new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false },
                settings: TestBmsFactory.CreateRootSettings([common]), rootFileEnumerator: port)
            { BmsCharts = charts };
            LibraryResourceIndexOwner owner = LibraryResourceIndexTestSupport.GetOwner(library);
            var index = LibraryResourceIndex.CreateFromScanResult(ChartDirectoryScanBuilder.BuildFromRoots([common]));
            for (int i = 0; i < backgroundEntries; i++) { index.DirectoryLookupCache.AddDir(Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "Background", i.ToString()), new[] { "background.wav" }); }
            owner.Replace(index);
            LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();
            var mutations = new List<string>();
            int visits = 0;
            before.DirectoryLookupCache.EntryStoreMutationObserver = mutations.Add;
            before.DirectoryLookupCache.EntryStoreEntryVisitedObserver = _ => visits++;
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs(charts);
            try
            {
                Stopwatch? operationClock = reportOperationElapsed == null ? null : Stopwatch.StartNew();
                LibraryMutationSessionReceipt receipt = move
                    ? library.MoveLibraryRootFolderWithReceipt([LibraryChartRef.FromChartFile(target)], newParent, reportAtTerminal: true)
                    : library.RenameChartFolderWithReceipt(source, "Renamed", reportAtTerminal: true);

                operationClock?.Stop();
                if (operationClock != null) { reportOperationElapsed?.Invoke(operationClock.Elapsed); }

                LibraryResourceIndexSnapshot after = owner.CaptureSnapshot();
                before.DirectoryLookupCache.EntryStoreMutationObserver = null;
                before.DirectoryLookupCache.EntryStoreEntryVisitedObserver = null;
                after.DirectoryLookupCache.EntryStoreMutationObserver = null;
                after.DirectoryLookupCache.EntryStoreEntryVisitedObserver = null;
                Assert.IsTrue(receipt.DurableCommit);
                Assert.IsNull(receipt.FinalizationFailure);
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.AreEqual(before.Generation + 1, after.Generation);
                Assert.AreEqual(1, receipt.ApplyCounts.ReverseLookupApplyCount);
                Assert.AreEqual(1, port.Calls);
                CollectionAssert.AreEqual(new[] { common }, port.LastRoots);
                Assert.AreEqual(parentCharts + 9, port.TotalFiles);
                Assert.IsTrue(visits <= before.DirectoryLookupCache.Count, "既存の移動key照合以外に全Entry訪問を追加しない");
                Assert.AreEqual(1, mutations.Count(path => path == common));
                Assert.AreEqual(1, mutations.Count(path => path == oldParent));
                Assert.AreEqual(move ? 1 : 0, mutations.Count(path => path == newParent));
                Assert.IsFalse(mutations.Contains(sibling));
                Assert.AreSame(before.DirectoryLookupCache.GetEntryOrNull(source), after.DirectoryLookupCache.GetEntryOrNull(destination));
                Assert.AreSame(before.DirectoryLookupCache.GetEntryOrNull(sibling), after.DirectoryLookupCache.GetEntryOrNull(sibling));
                Assert.IsNull(after.DirectoryLookupCache.GetEntryOrNull(source));
                string newRelative = move ? "New/Child" : "Old/Renamed";
                LibraryResourceIndexTestSupport.AssertEntry(after.DirectoryLookupCache, common,
                    ["Old/own.wav", newRelative + "/sound.wav", "OldBackup/keep.wav"], [newRelative + "/image.png"], [newRelative + "/movie.mpg"], [], [], []);
                LibraryResourceIndexTestSupport.AssertEntry(after.DirectoryLookupCache, oldParent,
                    move ? ["own.wav"] : ["own.wav", "Renamed/sound.wav"], move ? [] : ["Renamed/image.png"], move ? [] : ["Renamed/movie.mpg"], ["own.wav"], [], []);
                LibraryResourceIndexTestSupport.AssertEntry(after.DirectoryLookupCache, newParent,
                    move ? ["Child/sound.wav"] : [], move ? ["Child/image.png"] : [], move ? ["Child/movie.mpg"] : [], [], [], []);
                LibraryResourceIndexTestSupport.AssertEntry(after.DirectoryLookupCache, destination,
                    ["sound.wav"], ["image.png"], ["movie.mpg"], ["sound.wav"], ["image.png"], ["movie.mpg"]);
                LibraryResourceIndexTestSupport.AssertEntry(before.DirectoryLookupCache, oldParent,
                    ["own.wav", "Child/sound.wav"], ["Child/image.png"], ["Child/movie.mpg"], ["own.wav"], [], []);
                string expectedParent = move ? newParent : oldParent;
                string relativeChild = move ? "Child/" : "Renamed/";
                CollectionAssert.AreEquivalent(new[] { expectedParent }, after.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(LibraryResourceIndexTestSupport.ResourceHash(relativeChild + "sound.wav")).ToArray());
                CollectionAssert.AreEquivalent(new[] { expectedParent }, after.DirectoryLookupCache.GetDirectoriesByImageRelativeHash(LibraryResourceIndexTestSupport.ResourceHash(relativeChild + "image.png")).ToArray());
                CollectionAssert.AreEquivalent(new[] { expectedParent }, after.DirectoryLookupCache.GetDirectoriesByMovieRelativeHash(LibraryResourceIndexTestSupport.ResourceHash(relativeChild + "movie.mpg")).ToArray());
                Assert.IsFalse(Directory.Exists(source));
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
                {
                    Assert.IsNull(db.Find<LR2SongDB.song>(target.Path));
                    Assert.IsNotNull(db.Find<LR2SongDB.song>(Path.Combine(destination, "target.bms")));
                }
                // 親の参照文字列を変えず、実libraryの明示保守でも旧Entryで存在扱いへ戻らないことを代表確認します。
                if (!move)
                {
                    library.RescanResourceHealthCharts([parentChart]);
                    ChartFile currentParent = CurrentOwnedChart(library, parentChart);
                    Assert.AreEqual(1, currentParent.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
                    Assert.AreEqual(0, currentParent.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
                    StringAssert.Contains(File.ReadAllText(parentChart.Path), "#WAV01 Child/sound.wav");
                }
            }
            finally
            {
                before.DirectoryLookupCache.EntryStoreMutationObserver = null;
                before.DirectoryLookupCache.EntryStoreEntryVisitedObserver = null;
                owner.CaptureSnapshot().DirectoryLookupCache.EntryStoreMutationObserver = null;
                owner.CaptureSnapshot().DirectoryLookupCache.EntryStoreEntryVisitedObserver = null;
                library.RequestShutdown("ancestor-refresh-test");
            }
        });
    }

    /// <summary>必要な祖先要求だけで故障を返し、それ以外では実FS列挙を使います。Sessionや完成索引は代替しません。</summary>
    internal sealed class AncestorEnumerationPort(string ancestor, int failureKind = -1) : IRootFileEnumerator
    {
        internal int Calls { get; private set; }
        internal int TotalFiles { get; private set; }
        internal string[] LastRoots { get; private set; } = [];
        internal IOException Failure { get; } = new("required ancestor enumeration failed");

        public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
        {
            string[] roots = rootDirectories.ToArray();
            if (roots.Contains(ancestor, StringComparer.OrdinalIgnoreCase))
            {
                Calls++;
                LastRoots = roots;
                if (failureKind == 2) { throw Failure; }
                if (failureKind >= 0) { return new RootFileEnumerationResult { Success = failureKind == 1, IsComplete = failureKind != 1, ErrorReason = failureKind == 0 ? "ancestor-input-fault" : string.Empty, IncompleteReason = failureKind == 1 ? "ancestor-incomplete" : string.Empty }; }
            }
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(roots, groups, verboseLog);
            TotalFiles += result.TotalFileCount;
            return result;
        }
    }

    /// <summary>捕捉後に移転した所持項目の共通現在値を、同じ短命識別で検査します。</summary>
    internal static ChartFile CurrentOwnedChart(BMSLibrary library, ChartFile captured)
        => library.BmsCharts.Concat(library.BmsonCharts).Single(chart => ReferenceEquals(chart.Token, captured.Token));

    /// <summary>呼出元のDB所有者で専用DBを準備し、同期callbackの終了後にケース領域を回収します。</summary>
    internal static void WithTemporarySongDb(ApplicationDatabaseScope databaseScope, Action<string> testAction)
    {
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace1.RootDirectory, "BeMusicSeeker_FolderRenameRefresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            // fixture schemaは初期化処理の検証対象ではないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            });
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

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static BMSTable CreateTable(string name, string symbol, string hash)
    {
        return CreateTableWithHashes(name, symbol, hash, sha256: string.Empty);
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static BMSTable CreateTableWithHashes(string name, string symbol, string md5, string sha256)
    {
        var bMSTable = new BMSTable
        {
            name = name,
            symbol = symbol
        };
        ChartFile testableBmsFile = ChartTestValues.Empty();
        testableBmsFile = testableBmsFile with { Md5 = md5 };
        testableBmsFile = testableBmsFile with { Sha256 = sha256 };
        testableBmsFile = testableBmsFile with { Path = @"C:\Library\chart.bms" };
        var entry = new BMSTableEntry(testableBmsFile)
        {
            is_removed = false
        };
        if (string.IsNullOrWhiteSpace(md5) && !string.IsNullOrWhiteSpace(sha256))
        {
            entry.MarkAsBmsonPlaylistIdentity(sha256);
        }
        bMSTable.entries = [entry];
        return bMSTable;
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        private readonly List<string>? phases;

        internal RecordingDialogService(List<string>? phases = null)
        {
            this.phases = phases;
        }

        internal int CallCount { get; private set; }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            CallCount++;
            phases?.Add("dialog");
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class TestFileMutationService : IFileMutationService
    {
        public string? MoveDirectoryFailureSourcePath { get; set; }

        public HashSet<string> MoveFileFailureSourcePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Action<string>? OperationObserver { get; set; }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            OperationObserver?.Invoke("filesystem");
            if (MoveFileFailureSourcePaths.Contains(sourcePath))
            {
                throw new IOException("Synthetic file move failure for session terminal aggregation test.");
            }
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            OperationObserver?.Invoke("filesystem");
            if (!string.IsNullOrWhiteSpace(MoveDirectoryFailureSourcePath)
                && string.Equals(sourcePath, MoveDirectoryFailureSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Synthetic directory move failure for batch behavior test.");
            }
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string? destinationParentPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            OperationObserver?.Invoke("filesystem");
            if (!string.IsNullOrWhiteSpace(MoveDirectoryFailureSourcePath)
                && string.Equals(sourcePath, MoveDirectoryFailureSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Synthetic directory move failure for batch behavior test.");
            }
            CopyDirectoryTree(sourcePath, destinationPath, overwrite);
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath);
                string? destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }

        private static void CopyDirectoryTree(string sourcePath, string destinationPath, bool overwrite)
        {
            CopyDirectory(sourcePath, destinationPath);
            if (!overwrite)
            {
                return;
            }
        }
    }

}

/// <summary>CoreとWindowsの既存改名ケースが実進捗を任意の観測先へ渡す共通writerです。</summary>
internal sealed class RecordingFolderAutoRenameProgressWriter(
    Action<FolderAutoRenameProgressUpdate>? onUpdate = null) : IFolderAutoRenameProgressWriter
{
    public void TryWrite(FolderAutoRenameProgressUpdate update) => onUpdate?.Invoke(update);
}
