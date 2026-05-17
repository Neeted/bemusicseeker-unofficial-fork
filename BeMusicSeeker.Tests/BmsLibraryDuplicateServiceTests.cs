using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryDuplicateServiceTests
{
    [TestMethod]
    public void Analyze_GroupsDirectoriesConnectedByDuplicateHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryDuplicateService service = new BmsLibraryDuplicateService();
        List<BMSFile> files = new List<BMSFile>
        {
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms")),
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirB", "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "DirB", "b.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\BMS", "DirC", "b.bms"))
        };

        DuplicateAnalysisResult result = service.Analyze(service.BuildSnapshot(files, null));

        Assert.AreEqual(1, result.DuplicateGroups.Count);
        CollectionAssert.AreEquivalent(new[] { "C:\\BMS\\DirA", "C:\\BMS\\DirB", "C:\\BMS\\DirC" }, result.DuplicateGroups[0].Folders);
        Assert.AreEqual(4, result.DuplicateFiles.Count);
    }

    [TestMethod]
    public void ApplyDuplicateWarnings_SetsStructuredWarningWithoutDuplicates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryDuplicateService service = new BmsLibraryDuplicateService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));

        service.ApplyDuplicateWarnings(new[] { file }, Resources.Warning_DuplicateBmsFile);
        service.ApplyDuplicateWarnings(new[] { file }, Resources.Warning_DuplicateBmsFile);

        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
        Assert.IsTrue(file.HasHighlightedWarning);
        Assert.AreEqual("[1] 重複譜面", file.WarningDigestText);
    }

    [TestMethod]
    public void ClearDuplicateState_RemovesStructuredDuplicateWarningsOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryDuplicateService service = new BmsLibraryDuplicateService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"));
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
        file.SetWarning(ChartWarningKind.NestedChartFileInPackage, Resources.Warning_NestedChartFileInPackage);

        service.ClearDuplicateState(new[] { file });

        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.DuplicateChart));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
        Assert.AreEqual("[1] サブフォルダ譜面", file.WarningDigestText);
    }

    [TestMethod]
    public void Analyze_BuildSnapshotIncludesBmsonAndUsesPrimaryLookupHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryDuplicateService service = new BmsLibraryDuplicateService();
        List<BMSFile> bmsFiles = new List<BMSFile>
        {
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", Path.Combine("C:\\BMS", "DirB", "b.bms"), "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
            CreateFile(null, Path.Combine("C:\\BMS", "DirE", "e.bms"), "9999999999999999999999999999999999999999999999999999999999999999")
        };
        List<LR2SongDBExtended.bmson_song> bmsonSongs = new List<LR2SongDBExtended.bmson_song>
        {
            new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine("C:\\BMS", "DirC", "c.bmson"),
                title = "bmson md5 duplicate",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
            },
            new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine("C:\\BMS", "DirD", "d.bmson"),
                title = "sha256 only duplicate",
                sha256 = "9999999999999999999999999999999999999999999999999999999999999999"
            },
            new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine("C:\\BMS", "DirF", "f.bmson"),
                title = "md5 wins over sha256",
                md5 = "ffffffffffffffffffffffffffffffff",
                sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            }
        };

        DuplicateAnalysisResult result = service.Analyze(service.BuildSnapshot(bmsFiles, bmsonSongs));

        Assert.AreEqual(2, result.DuplicateGroups.Count);
        Assert.IsTrue(result.DuplicateGroups.Any((DuplicateGroup group) => group.Folders.Count == 2 && group.Folders.Contains("C:\\BMS\\DirA") && group.Folders.Contains("C:\\BMS\\DirC")));
        Assert.IsTrue(result.DuplicateGroups.Any((DuplicateGroup group) => group.Folders.Count == 2 && group.Folders.Contains("C:\\BMS\\DirD") && group.Folders.Contains("C:\\BMS\\DirE")));
        Assert.IsTrue(result.DuplicateGroups.SelectMany((DuplicateGroup group) => group.Files).Any((BMSFile file) => PendingChartEntry.IsBmsonChartFile(file)));
        Assert.IsFalse(result.DuplicateGroups.Any((DuplicateGroup group) => group.Folders.Contains("C:\\BMS\\DirB") || group.Folders.Contains("C:\\BMS\\DirF")));
        Assert.AreEqual(4, result.DuplicateFiles.Count);
    }

    [TestMethod]
    public void BmsonSongsSetter_InvalidatesDuplicateCache()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            BMSLibrary library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            library.DuplicateChartGroups = new List<DuplicateGroup>
            {
                new DuplicateGroup(
                    new List<BMSFile>
                    {
                        CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", @"C:\BMS\DirA\a.bms")
                    },
                    new List<string> { @"C:\BMS\DirA" })
            };

            library.BmsonSongs = new List<LR2SongDBExtended.bmson_song>
            {
                new LR2SongDBExtended.bmson_song
                {
                    path = @"C:\BMS\DirA\chart.bmson",
                    folder = @"C:\BMS\DirA",
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
            };

            Assert.IsNull(library.DuplicateChartGroups);
        });
    }

    [TestMethod]
    public void SearchDuplicateChartGroups_RebuildsAfterBmsonSongsChangeInvalidatesCache()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            BMSLibrary library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            library.BMSFiles = new List<BMSFile>
            {
                CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\BMS", "DirA", "a.bms"))
            };
            library.BmsonSongs = new List<LR2SongDBExtended.bmson_song>
            {
                new LR2SongDBExtended.bmson_song
                {
                    path = Path.Combine("C:\\BMS", "DirB", "b.bmson"),
                    folder = Path.Combine("C:\\BMS", "DirB"),
                    title = "duplicate",
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
            };

            library.SearchDuplicateChartGroups();
            Assert.AreEqual(1, library.DuplicateChartGroups.Count);

            library.BmsonSongs = new List<LR2SongDBExtended.bmson_song>();
            Assert.IsNull(library.DuplicateChartGroups);

            library.SearchDuplicateChartGroups();
            Assert.AreEqual(0, library.DuplicateChartGroups.Count);
        });
    }

    [TestMethod]
    public void RemoveChartFiles_BmsonPendingRow_UnregistersBmsonSong()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateRemoveBmson_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRootPath);
            string chartPath = Path.Combine(tempRootPath, "chart.bmson");
            File.WriteAllText(chartPath, "{}");
            try
            {
                BMSLibrary library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                LR2SongDBExtended.bmson_song song = new LR2SongDBExtended.bmson_song
                {
                    path = chartPath,
                    folder = tempRootPath,
                    title = "duplicate",
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                };
                library.BmsonSongs = new List<LR2SongDBExtended.bmson_song> { song };

                library.RemoveChartFiles(new[] { PendingChartEntry.CreateFromBmsonSong(song) }, sendToRecycleBin: false);

                Assert.IsFalse(File.Exists(chartPath));
                Assert.AreEqual(0, library.BmsonSongs.Count);
                using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
                {
                    Assert.IsNull(songDb.Find<LR2SongDBExtended.bmson_song>(chartPath));
                }
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
    public void TryGetInstalledDirectoryByHash_ResolvesBmsonOnlyLibrary()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BmsonInstalledDir_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRootPath);
            string chartPath = Path.Combine(tempRootPath, "chart.bmson");
            File.WriteAllText(chartPath, "{}");
            try
            {
                BMSLibrary library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                library.BmsonSongs = new List<LR2SongDBExtended.bmson_song>
                {
                    new LR2SongDBExtended.bmson_song
                    {
                        path = chartPath,
                        folder = tempRootPath,
                        title = "bmson",
                        md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        sha256 = new string('b', 64)
                    }
                };

                bool resolved = library.TryGetInstalledDirectoryByHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", out string installDir);

                Assert.IsTrue(resolved);
                Assert.AreEqual(tempRootPath, installDir);
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
    public void MergeBMSDirectory_BmsonOnly_ReRegistersSongAtDestination()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateMergeBmson_" + System.Guid.NewGuid().ToString("N"));
            string srcDir = Path.Combine(tempRootPath, "Src");
            string dstDir = Path.Combine(tempRootPath, "Dst");
            string srcChartPath = Path.Combine(srcDir, "chart.bmson");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);
            File.WriteAllText(srcChartPath, "{}");
            try
            {
                BMSLibrary library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                LR2SongDBExtended.bmson_song sourceSong = new LR2SongDBExtended.bmson_song
                {
                    path = srcChartPath,
                    folder = srcDir,
                    title = "merge target",
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                };
                library.BmsonSongs = new List<LR2SongDBExtended.bmson_song> { sourceSong };
                using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(sourceSong, typeof(LR2SongDBExtended.bmson_song));
                }

                library.MergeBMSDirectory(srcDir, dstDir);

                string dstChartPath = Path.Combine(dstDir, "chart.bmson");
                Assert.IsFalse(File.Exists(srcChartPath));
                Assert.IsFalse(Directory.Exists(srcDir));
                Assert.IsTrue(File.Exists(dstChartPath));
                Assert.AreEqual(1, library.BmsonSongs.Count);
                Assert.AreEqual(dstChartPath, library.BmsonSongs[0].path);
                Assert.AreEqual(dstDir, library.BmsonSongs[0].folder);
                using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
                {
                    Assert.IsNull(songDb.Find<LR2SongDBExtended.bmson_song>(srcChartPath));
                    Assert.IsNotNull(songDb.Find<LR2SongDBExtended.bmson_song>(dstChartPath));
                }
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
    public void MergeBMSDirectory_BmsonDuplicateSkip_KeepsDestinationOnly()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateMergeBmsonSkip_" + System.Guid.NewGuid().ToString("N"));
            string srcDir = Path.Combine(tempRootPath, "Src");
            string dstDir = Path.Combine(tempRootPath, "Dst");
            string srcChartPath = Path.Combine(srcDir, "chart.bmson");
            string dstChartPath = Path.Combine(dstDir, "chart.bmson");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);
            File.WriteAllText(srcChartPath, "{}");
            File.WriteAllText(dstChartPath, "{}");
            string duplicateHash = BmsonSongParser.Parse(srcChartPath).md5;
            try
            {
                BMSLibrary library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
                LR2SongDBExtended.bmson_song sourceSong = new LR2SongDBExtended.bmson_song
                {
                    path = srcChartPath,
                    folder = srcDir,
                    title = "src duplicate",
                    md5 = duplicateHash
                };
                LR2SongDBExtended.bmson_song destinationSong = new LR2SongDBExtended.bmson_song
                {
                    path = dstChartPath,
                    folder = dstDir,
                    title = "dst duplicate",
                    md5 = duplicateHash
                };
                library.BmsonSongs = new List<LR2SongDBExtended.bmson_song> { sourceSong, destinationSong };
                using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.InsertOrReplace(sourceSong, typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(destinationSong, typeof(LR2SongDBExtended.bmson_song));
                }

                library.MergeBMSDirectory(srcDir, dstDir);

                Assert.IsFalse(Directory.Exists(srcDir));
                Assert.IsTrue(File.Exists(dstChartPath));
                Assert.AreEqual(1, library.BmsonSongs.Count);
                Assert.AreEqual(dstChartPath, library.BmsonSongs[0].path);
                using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
                {
                    Assert.IsNull(songDb.Find<LR2SongDBExtended.bmson_song>(srcChartPath));
                    Assert.IsNotNull(songDb.Find<LR2SongDBExtended.bmson_song>(dstChartPath));
                }
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

    private static TestableBmsFile CreateFile(string? hash, string path, string? sha256 = null)
    {
        TestableBmsFile file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        file.SetSha256(sha256);
        return file;
    }

    private static void WithTemporarySongDb(System.Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateTests_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, System.Array.Empty<byte>());
        try
        {
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
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
        public void SetHash(string? value)
        {
            hash = value;
        }

        public void SetSha256(string? value)
        {
            sha256 = value;
        }
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
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            if (File.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException("Destination file already exists.");
                }
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationParent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }
            if (Directory.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException("Destination directory already exists.");
                }
                Directory.Delete(destinationPath, recursive: true);
            }
            Directory.Move(sourcePath, destinationPath);
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

        public void SetTimestamps(string path, bool isDirectory, System.DateTime? creationTime, System.DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }
    }
}
