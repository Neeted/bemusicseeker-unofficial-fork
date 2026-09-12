using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2BuiltinCustomFolderRealFixtureTests
{
    private static readonly DateTime GeneratedAtUtc = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void RealBuiltinCustomFolderFixture_ProjectsKnownLr2FolderRows()
    {
        using FixtureScope scope = FixtureScope.Create();
        IReadOnlyList<string> lr2FolderFiles = scope.GetLr2FolderFiles();
        Assert.AreEqual(91, lr2FolderFiles.Count);

        Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult result =
            Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                lr2FolderFiles,
                scope.CreateRequest(),
                scope.CreateEntries(lr2FolderFiles));

        Assert.IsFalse(result.HasReadFailures);
        Assert.AreEqual(91, result.Items.Count);

        Dictionary<string, LR2SongDB.folder> rowsByPath = CreateFolderRows(result.Items);
        Assert.AreEqual(91, rowsByPath.Count);

        LR2SongDB.folder favorite = rowsByPath[@"LR2files\CustomFolder\favorite.lr2folder"];
        Assert.AreEqual(2, favorite.type);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, favorite.parent);
        Assert.AreEqual("MY FAVORITE", favorite.title);
        Assert.AreEqual("favorite = 1", favorite.command);
        Assert.AreEqual(0, favorite.max);
        Assert.AreEqual(scope.GetTimestamp(@"LR2files/CustomFolder/favorite.lr2folder").ToUnixtime(), favorite.date);

        LR2SongDB.folder newsong = rowsByPath[@"LR2files\CustomFolder\newsong.lr2folder"];
        Assert.AreEqual(3, newsong.type);
        Assert.AreEqual("__NEWSONG__", newsong.command);
        Assert.AreEqual(1000, newsong.max);

        LR2SongDB.folder course = rowsByPath[@"LR2files\CustomFolder\course1.lr2folder"];
        Assert.AreEqual(6, course.type);
        Assert.AreEqual("__EXPERT__", course.command);

        LR2SongDB.folder random = rowsByPath[@"LR2files\CustomFolder\RANDOM\07.lr2folder"];
        Assert.AreEqual(2, random.type);
        Assert.AreEqual("7KEYS", random.title);
        Assert.AreEqual("level>=0 AND mode=7", random.command);
        Assert.AreEqual(1, random.max);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\RANDOM"),
            random.parent);

        LR2SongDB.folder insane = rowsByPath[@"LR2files\CustomFolder\INSANE01\sp99.lr2folder"];
        Assert.AreEqual("exlevel>24 AND mode=7", insane.command);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\INSANE01"),
            insane.parent);
    }

    [TestMethod]
    public void RealBuiltinCustomFolderFixture_SyncsCategoryRowsFromFolderInfoAndDirectoryMtime()
    {
        using FixtureScope scope = FixtureScope.Create();
        IReadOnlyList<string> lr2FolderFiles = scope.GetLr2FolderFiles();
        IReadOnlyList<string> folderInfoFiles = scope.GetFolderInfoFiles();
        IReadOnlyList<string> directories = scope.GetDirectories();

        Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult syncItems =
            Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                lr2FolderFiles,
                scope.CreateRequest(),
                scope.CreateEntries(lr2FolderFiles));

        Lr2FolderDirectoryMetadataSnapshot metadataSnapshot =
            Lr2SongDbSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(
                syncItems.Items,
                scope.CreateRequest(folderInfoFiles, directories));

        string songDbPath = Path.Combine(scope.TempDirectory, "song.db");
        using var songDb = new LR2SongDBExtended(songDbPath);
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2FolderFileDbSyncResult syncResult = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
        {
            Items = syncItems.Items,
            DirectoryRowScopeDirectories = [@"LR2files\CustomFolder"],
            DirectoryRowGenerationScopeDirectories = [@"LR2files\CustomFolder"],
            DirectoryMetadataResolver = metadataSnapshot.Resolve,
            GeneratedAtUtc = GeneratedAtUtc
        });

        Assert.AreEqual(98, syncResult.UpsertedCount);
        List<LR2SongDB.folder> rows = songDb.Table<LR2SongDB.folder>().ToList();
        Assert.AreEqual(98, rows.Count);

        LR2SongDB.folder randomCategory = rows.Single(row => row.path == @"LR2files\CustomFolder\RANDOM\");
        Assert.AreEqual(2, randomCategory.type);
        Assert.AreEqual("RANDOM SELECT", randomCategory.title);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, randomCategory.parent);
        Assert.AreEqual(scope.GetTimestamp(@"LR2files/CustomFolder/RANDOM").ToUnixtime(), randomCategory.date);
        Assert.AreNotEqual(scope.GetTimestamp(@"LR2files/CustomFolder/RANDOM/folderinfo.txt").ToUnixtime(), randomCategory.date);

        LR2SongDB.folder clearCategory = rows.Single(row => row.path == @"LR2files\CustomFolder\CLEAR\");
        Assert.AreEqual("CLEAR", clearCategory.title);
        Assert.AreEqual(scope.GetTimestamp(@"LR2files/CustomFolder/CLEAR").ToUnixtime(), clearCategory.date);

        LR2SongDB.folder randomChild = rows.Single(row => row.path == @"LR2files\CustomFolder\RANDOM\07.lr2folder");
        Assert.AreEqual(scope.GetTimestamp(@"LR2files/CustomFolder/RANDOM/07.lr2folder").ToUnixtime(), randomChild.date);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\RANDOM"),
            randomChild.parent);
    }

    private static Dictionary<string, LR2SongDB.folder> CreateFolderRows(IEnumerable<Lr2FolderFileSyncItem> items)
    {
        var rowsByPath = new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);
        foreach (Lr2FolderFileSyncItem item in items)
        {
            bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
            {
                FilePath = item.FilePath,
                DatabasePath = item.DatabasePath,
                Definition = item.Definition,
                LastWriteTimeUtc = item.LastWriteTimeUtc,
                FolderType = item.FolderType,
                ParentHash = item.ParentHash,
                GeneratedAtUtc = GeneratedAtUtc
            }, out LR2SongDB.folder row);

            Assert.IsTrue(created, item.DatabasePath);
            rowsByPath[row.path] = row;
        }
        return rowsByPath;
    }

    private sealed class FixtureScope : IDisposable
    {
        private const string FixtureRelativeDirectory = @"TestData\lr2_builtin_custom_folder_real";

        private readonly IReadOnlyDictionary<string, ManifestEntry> manifestByRelativePath;

        private FixtureScope(string tempDirectory, IReadOnlyDictionary<string, ManifestEntry> manifest)
        {
            TempDirectory = tempDirectory;
            manifestByRelativePath = manifest;
        }

        public string TempDirectory { get; }

        public string Lr2RootPath => TempDirectory;

        public string BuiltinRootPath => Path.Combine(Lr2RootPath, "LR2files", "CustomFolder");

        public static FixtureScope Create()
        {
            string sourceDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FixtureRelativeDirectory);
            Assert.IsTrue(Directory.Exists(sourceDirectory), "Fixture directory was not copied to the test output.");

            string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2BuiltinCustomFolderRealFixtureTests), Guid.NewGuid().ToString("N"));
            CopyDirectory(sourceDirectory, tempDirectory);
            IReadOnlyDictionary<string, ManifestEntry> manifest = LoadManifest(tempDirectory);
            ApplyManifestTimestamps(tempDirectory, manifest);
            return new FixtureScope(tempDirectory, manifest);
        }

        public IReadOnlyList<string> GetLr2FolderFiles()
        {
            return Directory.GetFiles(BuiltinRootPath, "*.lr2folder", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<string> GetFolderInfoFiles()
        {
            return Directory.GetFiles(BuiltinRootPath, "folderinfo.txt", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<string> GetDirectories()
        {
            return Directory.GetDirectories(Path.Combine(Lr2RootPath, "LR2files"), "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Lr2SongDbSyncRequest CreateRequest(
            IReadOnlyList<string>? folderInfoFiles = null,
            IReadOnlyList<string>? directories = null)
        {
            return new Lr2SongDbSyncRequest
            {
                Lr2RootPath = Lr2RootPath,
                Lr2BuiltinFolderSourceDirectories = [BuiltinRootPath],
                Lr2FolderPruneDirectories = [@"LR2files\CustomFolder"],
                FolderInfoFilePaths = folderInfoFiles ?? [],
                FolderInfoFileEntries = CreateEntries(folderInfoFiles ?? []),
                DirectoryEntries = CreateEntries(directories ?? [])
            };
        }

        public IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateEntries(IEnumerable<string> paths)
        {
            var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths ?? [])
            {
                string relative = ToManifestRelativePath(path);
                entries[path] = new RootFileEnumerationEntry(path, GetTimestamp(relative));
            }
            return entries;
        }

        public DateTime GetTimestamp(string relativePath)
        {
            string key = NormalizeManifestRelativePath(relativePath);
            Assert.IsTrue(manifestByRelativePath.TryGetValue(key, out ManifestEntry? entry), "Missing manifest entry: " + key);
            return entry!.LastWriteTimeUtc;
        }

        public void Dispose()
        {
            if (Directory.Exists(TempDirectory))
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
        }

        private string ToManifestRelativePath(string path)
        {
            string relative = path.Substring(Lr2RootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return NormalizeManifestRelativePath(relative);
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = directory.Substring(sourceDirectory.Length).TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
            }
            foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(sourceDirectory.Length).TrimStart(Path.DirectorySeparatorChar);
                string destination = Path.Combine(destinationDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }

        private static IReadOnlyDictionary<string, ManifestEntry> LoadManifest(string fixtureRoot)
        {
            string manifestPath = Path.Combine(fixtureRoot, "mtime-manifest.tsv");
            Assert.IsTrue(File.Exists(manifestPath), "Missing fixture mtime manifest.");
            var result = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(manifestPath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string[] parts = line.Split('\t');
                Assert.AreEqual(3, parts.Length, "Invalid manifest line: " + line);
                string relativePath = NormalizeManifestRelativePath(parts[1]);
                DateTime timestamp = DateTime.ParseExact(
                    parts[2],
                    "yyyy-MM-ddTHH:mm:ssZ",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                result[relativePath] = new ManifestEntry(parts[0], relativePath, timestamp);
            }
            return result;
        }

        private static void ApplyManifestTimestamps(string fixtureRoot, IReadOnlyDictionary<string, ManifestEntry> manifest)
        {
            foreach (ManifestEntry entry in manifest.Values.Where(entry => string.Equals(entry.Kind, "file", StringComparison.OrdinalIgnoreCase)))
            {
                string path = Path.Combine(fixtureRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(File.Exists(path), "Missing fixture file: " + entry.RelativePath);
                File.SetLastWriteTimeUtc(path, entry.LastWriteTimeUtc);
            }

            foreach (ManifestEntry entry in manifest.Values.Where(entry => string.Equals(entry.Kind, "dir", StringComparison.OrdinalIgnoreCase)))
            {
                string path = Path.Combine(fixtureRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(Directory.Exists(path), "Missing fixture directory: " + entry.RelativePath);
                Directory.SetLastWriteTimeUtc(path, entry.LastWriteTimeUtc);
            }
        }

        private static string NormalizeManifestRelativePath(string relativePath)
        {
            return (relativePath ?? string.Empty)
                .Trim()
                .Replace('\\', '/');
        }
    }

    private sealed class ManifestEntry(string kind, string relativePath, DateTime lastWriteTimeUtc)
    {
        public string Kind { get; } = kind;

        public string RelativePath { get; } = relativePath;

        public DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;
    }
}
