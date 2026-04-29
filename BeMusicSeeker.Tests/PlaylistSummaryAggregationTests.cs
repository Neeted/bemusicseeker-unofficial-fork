using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualBasic.FileIO;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSummaryAggregationTests
{
    [TestMethod]
    public void CalculatePlaylistSummaryCounts_CountsActiveHashedRowsWithoutDedup()
    {
        BMSTableEntry[] entries = new[]
        {
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null),
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null),
            CreateEntry(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CreateEntry(null, null),
            CreateEntry("cccccccccccccccccccccccccccccccc", null, isRemoved: true)
        };

        MainWindowViewModel.PlaylistSummaryCountResult result = MainWindowViewModel.CalculatePlaylistSummaryCounts(
            entries,
            CreateHashSet("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CreateHashSet("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.AreEqual(3, result.TotalCharts);
        Assert.AreEqual(3, result.OwnedCharts);
    }

    [TestMethod]
    public void CalculatePlaylistSummaryCounts_DoesNotFallbackToSha256WhenMd5Exists()
    {
        BMSTableEntry[] entries = new[]
        {
            CreateEntry(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        };

        MainWindowViewModel.PlaylistSummaryCountResult result = MainWindowViewModel.CalculatePlaylistSummaryCounts(
            entries,
            CreateHashSet("cccccccccccccccccccccccccccccccc"),
            CreateHashSet("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.AreEqual(1, result.TotalCharts);
        Assert.AreEqual(0, result.OwnedCharts);
    }

    [TestMethod]
    public void CalculatePlaylistSummaryCounts_UsesSha256WhenMd5IsMissing()
    {
        BMSTableEntry[] entries = new[]
        {
            CreateEntry(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        };

        MainWindowViewModel.PlaylistSummaryCountResult result = MainWindowViewModel.CalculatePlaylistSummaryCounts(
            entries,
            CreateHashSet(),
            CreateHashSet("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.AreEqual(1, result.TotalCharts);
        Assert.AreEqual(1, result.OwnedCharts);
    }

    [TestMethod]
    public void GetPlaylistSummaryOwnedHashSnapshot_IncludesBmsonHashes()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            BMSLibrary library = new BMSLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            SetLibraryFilesWithoutNotification(library, new[]
            {
                CreateLibraryFile(@"C:\Songs\bms.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            });
            SetLibraryBmsonSongsWithoutNotification(library, new[]
            {
                new LR2SongDBExtended.bmson_song
                {
                    path = @"C:\Songs\bmson\chart.bmson",
                    folder = @"C:\Songs\bmson",
                    md5 = "cccccccccccccccccccccccccccccccc",
                    sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
                }
            });

            BMSLibrary.PlaylistSummaryOwnedHashSnapshot snapshot = library.GetPlaylistSummaryOwnedHashSnapshot();

            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "cccccccccccccccccccccccccccccccc");
            CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
        });
    }

    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_AppliesFilterAndSortWithoutRebuildLogic()
    {
        List<PlaylistSummaryRow> rows = new List<PlaylistSummaryRow>
        {
            new PlaylistSummaryRow
            {
                Name = "beta",
                Symbol = "B",
                TotalCharts = 4,
                OwnedCharts = 2
            },
            new PlaylistSummaryRow
            {
                Name = "alpha",
                Symbol = "A",
                TotalCharts = 3,
                OwnedCharts = 3
            },
            new PlaylistSummaryRow
            {
                Name = "gamma",
                Symbol = "G",
                TotalCharts = 1,
                OwnedCharts = 0
            }
        };

        MainWindowViewModel.PlaylistSummaryPresentationResult result = MainWindowViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "a",
            MainWindowViewModel.PlaylistSummaryOwnedFilterType.OwnedIncomplete,
            new MainWindowViewModel.cSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.Name),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            },
            useLegacySort: false);

        Assert.AreEqual(2, result.FilteredCount);
        CollectionAssert.AreEqual(new[] { "beta", "gamma" }, result.Rows.Select((PlaylistSummaryRow row) => row.Name).ToArray());
    }

    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_KeywordFilterSupportsAndAndFieldQueries()
    {
        List<PlaylistSummaryRow> rows = new List<PlaylistSummaryRow>
        {
            new PlaylistSummaryRow
            {
                PlaylistId = 10,
                Name = "alpha pack",
                Symbol = "A"
            },
            new PlaylistSummaryRow
            {
                PlaylistId = 20,
                Name = "alpha other",
                Symbol = "B"
            }
        };

        MainWindowViewModel.PlaylistSummaryPresentationResult result = MainWindowViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "name:alpha symbol:A",
            MainWindowViewModel.PlaylistSummaryOwnedFilterType.All,
            new MainWindowViewModel.cSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.PlaylistId),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            },
            useLegacySort: false);

        Assert.AreEqual(1, result.FilteredCount);
        Assert.AreEqual(10, result.Rows[0].PlaylistId);
    }

    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_UnknownFieldDoesNotMatch()
    {
        List<PlaylistSummaryRow> rows = new List<PlaylistSummaryRow>
        {
            new PlaylistSummaryRow
            {
                PlaylistId = 10,
                Name = "alpha pack",
                Symbol = "A"
            }
        };

        MainWindowViewModel.PlaylistSummaryPresentationResult result = MainWindowViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "md5:aaaaaaaa",
            MainWindowViewModel.PlaylistSummaryOwnedFilterType.All,
            new MainWindowViewModel.cSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.PlaylistId),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            },
            useLegacySort: false);

        Assert.AreEqual(0, result.FilteredCount);
    }

    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_KeywordFilterSupportsQuoteNegationOrAndRegex()
    {
        List<PlaylistSummaryRow> rows = new List<PlaylistSummaryRow>
        {
            new PlaylistSummaryRow
            {
                PlaylistId = 10,
                Name = "alpha pack",
                Symbol = "A"
            },
            new PlaylistSummaryRow
            {
                PlaylistId = 20,
                Name = "alpha pack",
                Symbol = "B"
            },
            new PlaylistSummaryRow
            {
                PlaylistId = 30,
                Name = "beta pack",
                Symbol = "C"
            }
        };

        MainWindowViewModel.PlaylistSummaryPresentationResult result = MainWindowViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "name:\"alpha pack\" symbol:A|C -id:20 name:re:^alpha",
            MainWindowViewModel.PlaylistSummaryOwnedFilterType.All,
            new MainWindowViewModel.cSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.PlaylistId),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            },
            useLegacySort: false);

        Assert.AreEqual(1, result.FilteredCount);
        Assert.AreEqual(10, result.Rows[0].PlaylistId);
    }

    private static HashSet<string> CreateHashSet(params string[] values)
    {
        return new HashSet<string>(values ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    private static BMSTableEntry CreateEntry(string? md5, string? sha256, bool isRemoved = false)
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry
        {
            is_removed = isRemoved
        };
        if (md5 != null)
        {
            entry.SetHash(md5);
        }
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        return entry;
    }

    private static BMSFile CreateLibraryFile(string path, string md5, string sha256)
    {
        TestableBmsFile file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(md5);
        file.SetSha256(sha256);
        return file;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public void SetHash(string value)
        {
            md5 = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }
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

    private static void WithTemporarySongDb(System.Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlaylistSummaryAggregation_" + System.Guid.NewGuid().ToString("N"));
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
