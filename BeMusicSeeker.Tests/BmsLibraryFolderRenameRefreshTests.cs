using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryFolderRenameRefreshTests
{
    [TestMethod]
    public void RenameChartFolder_UpdatesFolderCellWithoutRaisingBmsFilesChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RenameRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                int bmsFilesChangedCount = 0;
                int folderChangedCount = 0;
                int pathChangedCount = 0;
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                };
                SetLibraryFilesWithoutNotification(library, [file]);
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSFile.Folder))
                    {
                        Interlocked.Increment(ref folderChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSFile.path))
                    {
                        Interlocked.Increment(ref pathChangedCount);
                    }
                };

                library.RenameChartFolder(sourceDirectoryPath, "Renamed");

                Assert.IsTrue(WaitUntilTrue(() => Volatile.Read(ref folderChangedCount) > 0));
                Assert.IsTrue(WaitUntilTrue(() => Volatile.Read(ref pathChangedCount) > 0));
                Assert.AreEqual(0, Volatile.Read(ref bmsFilesChangedCount));
                Assert.IsTrue(Volatile.Read(ref folderChangedCount) > 0);
                Assert.IsTrue(Volatile.Read(ref pathChangedCount) > 0);
                Assert.AreEqual("Renamed", file.Folder);
                Assert.IsTrue(file.path.Contains(Path.Combine("Renamed", "chart.bms")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void MoveBMSRootFolder_RaisesBmsFilesChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MoveRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string destinationParentPath = Path.Combine(tempRootPath, "DestinationParent");
            string chartPath = Path.Combine(sourceRootPath, "chart.bms");
            Directory.CreateDirectory(sourceRootPath);
            Directory.CreateDirectory(destinationParentPath);
            File.WriteAllText(chartPath, "#PLAYER 1");
            try
            {
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                int bmsFilesChangedCount = 0;
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                };
                SetLibraryFilesWithoutNotification(library, [file]);
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);

                library.MoveBMSRootFolder([file], destinationParentPath);

                Assert.IsTrue(WaitUntilTrue(() => Volatile.Read(ref bmsFilesChangedCount) > 0));
                Assert.IsTrue(file.path.Contains(Path.Combine("DestinationParent", "SourceRoot", "chart.bms")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void RenameBmsonFolder_UpdatesFolderWithoutRaisingCollectionRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonRenameRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Source");
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.WriteAllText(chartPath, "{}");
            try
            {
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var song = new LR2SongDBExtended.bmson_song
                {
                    path = chartPath,
                    folder = sourceDirectoryPath,
                    title = "Chart"
                };
                var row = PendingChartEntry.CreateFromBmsonSong(song);
                int bmsFilesChangedCount = 0;
                int bmsonSongsChangedCount = 0;
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.BmsonSongs))
                    {
                        Interlocked.Increment(ref bmsonSongsChangedCount);
                    }
                };
                SetLibraryBmsonSongsWithoutNotification(library, [song]);
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);
                Interlocked.Exchange(ref bmsonSongsChangedCount, 0);

                library.RenameChartFolder(sourceDirectoryPath, "Renamed");
                row.UpdateFromBmsonSong(song);

                Assert.AreEqual(0, Volatile.Read(ref bmsFilesChangedCount));
                Assert.AreEqual(0, Volatile.Read(ref bmsonSongsChangedCount));
                Assert.AreEqual("Renamed", row.Folder);
                Assert.IsTrue(song.path.Contains(Path.Combine("Renamed", "chart.bmson")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void MoveBmsonRootFolder_RaisesBmsFilesChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonMoveRefresh_" + Guid.NewGuid().ToString("N"));
            string sourceRootPath = Path.Combine(tempRootPath, "SourceRoot");
            string destinationParentPath = Path.Combine(tempRootPath, "DestinationParent");
            string chartPath = Path.Combine(sourceRootPath, "chart.bmson");
            Directory.CreateDirectory(sourceRootPath);
            Directory.CreateDirectory(destinationParentPath);
            File.WriteAllText(chartPath, "{}");
            try
            {
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var song = new LR2SongDBExtended.bmson_song
                {
                    path = chartPath,
                    folder = sourceRootPath,
                    title = "Chart"
                };
                var row = PendingChartEntry.CreateFromBmsonSong(song);
                int bmsFilesChangedCount = 0;
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                    {
                        Interlocked.Increment(ref bmsFilesChangedCount);
                    }
                };
                SetLibraryBmsonSongsWithoutNotification(library, [song]);
                Interlocked.Exchange(ref bmsFilesChangedCount, 0);

                library.MoveBMSRootFolder([row], destinationParentPath);

                Assert.IsTrue(WaitUntilTrue(() => Volatile.Read(ref bmsFilesChangedCount) > 0));
                Assert.IsTrue(song.path.Contains(Path.Combine("DestinationParent", "SourceRoot", "chart.bmson")));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void SetBMSFilesEncoding_UpdatesEncodingCellWithoutRaisingGarbledCollectionsChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_EncodingRefresh_" + Guid.NewGuid().ToString("N"));
            string chartPath = Path.Combine(tempRootPath, "chart.bms");
            Directory.CreateDirectory(tempRootPath);
            File.WriteAllText(
                chartPath,
                "#TITLE Garbled\r\n#ARTIST Artist\r\n#GENRE TEST\r\n",
                Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
            try
            {
                var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                var file = BMSFile.CreateBMSFileFromFile(chartPath);
                file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
                {
                    hash = file.hash,
                    encoding = "unknown",
                    is_encoding_fixed = false
                }, suppressPropertyChanged: true, registerEventHandlers: false);
                int garbledChangedCount = 0;
                int garbledFixedChangedCount = 0;
                int encodingChangedCount = 0;
                library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSLibrary.BMSFilesGarbled))
                    {
                        Interlocked.Increment(ref garbledChangedCount);
                    }
                    if (e.PropertyName == nameof(BMSLibrary.BMSFilesGarbledFixed))
                    {
                        Interlocked.Increment(ref garbledFixedChangedCount);
                    }
                };
                file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(BMSFile.encoding))
                    {
                        Interlocked.Increment(ref encodingChangedCount);
                    }
                };
                SetLibraryFilesWithoutNotification(library, [file]);
                Interlocked.Exchange(ref garbledChangedCount, 0);
                Interlocked.Exchange(ref garbledFixedChangedCount, 0);
                Interlocked.Exchange(ref encodingChangedCount, 0);

                library.SetBMSFilesEncoding([file], "gb2312");

                Thread.Sleep(200);
                Assert.AreEqual(0, Volatile.Read(ref garbledChangedCount));
                Assert.AreEqual(0, Volatile.Read(ref garbledFixedChangedCount));
                Assert.IsTrue(Volatile.Read(ref encodingChangedCount) > 0);
                Assert.AreEqual("gb2312", file.encoding);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void RefreshReferenceDisplayForTable_UpdatesPlaylistCellWithoutRaisingBmsFilesChanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            var table = new BMSTable
            {
                name = "Before",
                symbol = "A"
            };
            file.AddRefTable(table);
            int bmsFilesChangedCount = 0;
            int symbolsChangedCount = 0;
            int namesChangedCount = 0;
            library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    Interlocked.Increment(ref bmsFilesChangedCount);
                }
            };
            file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(BMSFile.RefTablesSymbols))
                {
                    Interlocked.Increment(ref symbolsChangedCount);
                }
                if (e.PropertyName == nameof(BMSFile.RefTablesNames))
                {
                    Interlocked.Increment(ref namesChangedCount);
                }
            };
            SetLibraryFilesWithoutNotification(library, [file]);
            Interlocked.Exchange(ref bmsFilesChangedCount, 0);
            Interlocked.Exchange(ref symbolsChangedCount, 0);
            Interlocked.Exchange(ref namesChangedCount, 0);

            table.symbol = "B";
            table.name = "After";
            Assert.AreEqual("A", file.RefTablesSymbols);
            Assert.AreEqual("Before", file.RefTablesNames);

            library.RefreshReferenceDisplayForTable(table);

            Assert.IsTrue(WaitUntilTrue(() => Volatile.Read(ref symbolsChangedCount) > 0 && Volatile.Read(ref namesChangedCount) > 0));
            Assert.AreEqual(0, Volatile.Read(ref bmsFilesChangedCount));
            Assert.AreEqual("B", file.RefTablesSymbols);
            Assert.AreEqual("After", file.RefTablesNames);
        });
    }

    [TestMethod]
    public void SynchronizeReferenceBMSTables_ReplacesReloadedPlaylistReferenceWithoutAppending()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            BMSTable oldTable = CreateTable("Before", "A", file.hash);
            BMSTable newTable = CreateTable("After", "B", file.hash);
            SetLibraryFilesWithoutNotification(library, [file]);

            library.AddReferenceBMSTables(oldTable);
            Assert.AreEqual(1, file.RefTables.Count);
            Assert.AreEqual("A", file.RefTablesSymbols);
            Assert.AreEqual("Before", file.RefTablesNames);

            library.SynchronizeReferenceBMSTables([newTable], suppressFilePropertyChanged: true);

            Assert.AreEqual(1, file.RefTables.Count);
            Assert.AreSame(newTable, file.RefTables[0]);
            Assert.AreEqual("B", file.RefTablesSymbols);
            Assert.AreEqual("After", file.RefTablesNames);
        });
    }

    private static bool WaitUntilTrue(Func<bool> predicate, int timeoutMs = 2000)
    {
        return SpinWait.SpinUntil(predicate, timeoutMs);
    }

    private static void SetLibraryFilesWithoutNotification(BMSLibrary library, IEnumerable<BMSFile> files)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("_BMSFiles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        fieldInfo.SetValue(library, files.ToList());
    }

    private static void SetLibraryBmsonSongsWithoutNotification(BMSLibrary library, IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("_BmsonSongs", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        fieldInfo.SetValue(library, songs.ToList());
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FolderRenameRefresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            }
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

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }
    }

    private static BMSTable CreateTable(string name, string symbol, string hash)
    {
        var bMSTable = new BMSTable
        {
            name = name,
            symbol = symbol
        };
        var testableBmsFile = new TestableBmsFile();
        testableBmsFile.SetHash(hash);
        testableBmsFile.path = @"C:\Library\chart.bms";
        bMSTable.entries.Add(new BMSTableEntry(testableBmsFile)
        {
            is_removed = false
        });
        return bMSTable;
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
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
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string destinationParentPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
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
                string destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }
    }
}
