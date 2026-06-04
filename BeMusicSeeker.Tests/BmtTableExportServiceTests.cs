using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using Codeplex.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmtTableExportServiceTests
{
    private const string Sha256A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sha256B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Md5A = "11111111111111111111111111111111";
    private const string Md5B = "22222222222222222222222222222222";

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTable_ExternalTableWritesBeatorajaBmtShape()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            var table = new BMSTable();
            table.LoadHeaderJSON("{\"name\":\"Stella\",\"symbol\":\"sl\",\"tag\":\"st\",\"data_url\":\"data.json\",\"level_order\":[0],\"course\":[[{\"name\":\"段位\",\"constraint\":[\"grade_mirror\",\"gauge_lr2\",\"ln\"],\"trophy\":[{\"name\":\"drop\",\"missrate\":0,\"scorerate\":100},{\"name\":\"gold\",\"missrate\":1.5,\"scorerate\":99.9}],\"md5\":[\"" + Md5A + "\"],\"sha256\":[\"" + Sha256B + "\"]}]]}", new Uri("https://example.com/sl/"), new Uri("https://example.com/sl/header.json"));
            table.LoadDataJSON("[{\"title\":\"Song\",\"artist\":\"Artist\",\"md5\":\"" + Md5A + "\",\"level\":\"0\",\"url\":\"https://example.com/song.zip\"},{\"title\":\"Sha Only\",\"sha256\":\"" + Sha256A + "\",\"level\":\"0\"},{\"title\":\"No Hash\",\"level\":\"0\"}]");

            string fileName = BmtTableExportService.ExportTable(tempDirectory, table);

            Assert.AreEqual(BMSTable.ComputeSha256Hex("https://example.com/sl/") + ".bmt", fileName);
            JObject json = ReadBmtJson(Path.Combine(tempDirectory, fileName));
            Assert.AreEqual("https://example.com/sl/", json.Value<string>("url"));
            Assert.AreEqual("Stella", json.Value<string>("name"));
            Assert.AreEqual("st", json.Value<string>("tag"));
            Assert.AreEqual("st0", json["folder"]![0]!.Value<string>("name"));
            Assert.AreEqual(2, json["folder"]![0]!["songs"]!.Count());
            Assert.AreEqual(Sha256A, json["folder"]![0]!["songs"]![1]!.Value<string>("sha256"));
            CollectionAssert.AreEqual(new[] { "MIRROR", "GAUGE_LR2", "LN" }, json["course"]![0]!["constraint"]!.Select(token => token.ToString()).ToArray());
            Assert.AreEqual(Md5A, json["course"]![0]!["hash"]![0]!.Value<string>("md5"));
            Assert.AreEqual(Sha256B, json["course"]![0]!["hash"]![1]!.Value<string>("sha256"));
            Assert.AreEqual(1, json["course"]![0]!["trophy"]!.Count());
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildTableData_LocalPlaylistUsesPseudoUrlAndFolderNameAsIs()
    {
        var table = new BMSTable
        {
            name = "Local Table",
            playlist_id = 42,
            Folder_order = ["Alpha"],
            entries =
            [
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Local Song\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}"))
            ]
        };

        JObject json = BmtTableExportService.BuildTableData(table);

        Assert.AreEqual("bemusicseeker://playlist/42", json.Value<string>("url"));
        Assert.AreEqual("Alpha", json["folder"]![0]!.Value<string>("name"));
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildTableData_ResolverFillsMissingFolderSongHashesButNotCourses()
    {
        var table = new BMSTable
        {
            name = "Local Table",
            playlist_id = 43,
            Folder_order = ["Alpha"],
            entries =
            [
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Md5 Only\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Sha Only\",\"sha256\":\"" + Sha256B + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Both\",\"md5\":\"33333333333333333333333333333333\",\"sha256\":\"" + new string('c', 64) + "\",\"level\":\"Alpha\"}"))
            ]
        };
        table.SetPersistedCourses(
        [
            new LR2SongDBExtended.playlist_course
            {
                course_json = "{\"name\":\"Course\",\"md5\":[\"" + Md5A + "\"]}"
            }
        ]);
        var resolver = new TestSongHashResolver(new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Md5 Only"] = Tuple.Create(Md5A, Sha256A),
            ["Sha Only"] = Tuple.Create(Md5B, Sha256B),
            ["Both"] = Tuple.Create(Md5B, Sha256B)
        });

        JObject json = BmtTableExportService.BuildTableData(table, resolver);

        JToken md5Only = json["folder"]![0]!["songs"]![0]!;
        JToken shaOnly = json["folder"]![0]!["songs"]![1]!;
        JToken both = json["folder"]![0]!["songs"]![2]!;
        Assert.AreEqual(Md5A, md5Only.Value<string>("md5"));
        Assert.AreEqual(Sha256A, md5Only.Value<string>("sha256"));
        Assert.AreEqual(Md5B, shaOnly.Value<string>("md5"));
        Assert.AreEqual(Sha256B, shaOnly.Value<string>("sha256"));
        Assert.AreEqual("33333333333333333333333333333333", both.Value<string>("md5"));
        Assert.AreEqual(new string('c', 64), both.Value<string>("sha256"));
        Assert.IsNull(json["course"]![0]!["hash"]![0]!["sha256"]);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildTableData_OriginalHashModeDoesNotUseResolver()
    {
        var table = new BMSTable
        {
            name = "Local Table",
            playlist_id = 45,
            Folder_order = ["Alpha"],
            entries =
            [
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Md5 Only\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Sha Only\",\"sha256\":\"" + Sha256B + "\",\"level\":\"Alpha\"}"))
            ]
        };
        var resolver = new TestSongHashResolver(new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Md5 Only"] = Tuple.Create(Md5A, Sha256A),
            ["Sha Only"] = Tuple.Create(Md5B, Sha256B)
        });

        JObject json = BmtTableExportService.BuildTableData(table, resolver, BeatorajaBmtHashOutputMode.Original);

        JToken md5Only = json["folder"]![0]!["songs"]![0]!;
        JToken shaOnly = json["folder"]![0]!["songs"]![1]!;
        Assert.AreEqual(Md5A, md5Only.Value<string>("md5"));
        Assert.IsNull(md5Only["sha256"]);
        Assert.IsNull(shaOnly["md5"]);
        Assert.AreEqual(Sha256B, shaOnly.Value<string>("sha256"));
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildTableData_PreferSha256OnlyModeKeepsMd5WhenSha256Unavailable()
    {
        var table = new BMSTable
        {
            name = "Local Table",
            playlist_id = 46,
            Folder_order = ["Alpha"],
            entries =
            [
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Md5 Resolved\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Md5 Unresolved\",\"md5\":\"" + Md5B + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Sha Only\",\"sha256\":\"" + Sha256B + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Both\",\"md5\":\"33333333333333333333333333333333\",\"sha256\":\"" + new string('c', 64) + "\",\"level\":\"Alpha\"}"))
            ]
        };
        var resolver = new TestSongHashResolver(new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Md5 Resolved"] = Tuple.Create(Md5A, Sha256A),
            ["Md5 Unresolved"] = Tuple.Create(Md5B, string.Empty)
        });

        JObject json = BmtTableExportService.BuildTableData(table, resolver, BeatorajaBmtHashOutputMode.PreferSha256Only);

        JToken md5Resolved = json["folder"]![0]!["songs"]![0]!;
        JToken md5Unresolved = json["folder"]![0]!["songs"]![1]!;
        JToken shaOnly = json["folder"]![0]!["songs"]![2]!;
        JToken both = json["folder"]![0]!["songs"]![3]!;
        Assert.IsNull(md5Resolved["md5"]);
        Assert.AreEqual(Sha256A, md5Resolved.Value<string>("sha256"));
        Assert.AreEqual(Md5B, md5Unresolved.Value<string>("md5"));
        Assert.IsNull(md5Unresolved["sha256"]);
        Assert.IsNull(shaOnly["md5"]);
        Assert.AreEqual(Sha256B, shaOnly.Value<string>("sha256"));
        Assert.IsNull(both["md5"]);
        Assert.AreEqual(new string('c', 64), both.Value<string>("sha256"));
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void NormalizeHashOutputMode_InvalidValueFallsBackToOriginal()
    {
        Assert.AreEqual(BeatorajaBmtHashOutputMode.Original, BmtTableExportService.NormalizeHashOutputMode(null));
        Assert.AreEqual(BeatorajaBmtHashOutputMode.Original, BmtTableExportService.NormalizeHashOutputMode("unknown"));
        Assert.AreEqual(BeatorajaBmtHashOutputMode.FillMissingMd5Sha256, BmtTableExportService.NormalizeHashOutputMode("FillMissingMd5Sha256"));
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildTableData_GroupsFolderSongsWithoutChangingFolderOrEntryOrder()
    {
        var table = new BMSTable
        {
            name = "Local Table",
            playlist_id = 44,
            Folder_order = ["Beta", "Alpha"],
            entries =
            [
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Alpha 1\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Beta 1\",\"md5\":\"" + Md5B + "\",\"level\":\"Beta\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Alpha 2\",\"sha256\":\"" + Sha256A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Removed\",\"md5\":\"33333333333333333333333333333333\",\"level\":\"Beta\"}"))
                {
                    is_removed = true
                },
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"No Hash\",\"level\":\"Beta\"}")),
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Empty Folder\",\"level\":\"Gamma\"}"))
            ]
        };

        JObject json = BmtTableExportService.BuildTableData(table);

        JArray folders = (JArray)json["folder"]!;
        Assert.AreEqual(2, folders.Count);
        Assert.AreEqual("Beta", folders[0]!.Value<string>("name"));
        Assert.AreEqual("Alpha", folders[1]!.Value<string>("name"));
        JArray betaSongs = (JArray)folders[0]!["songs"]!;
        JArray alphaSongs = (JArray)folders[1]!["songs"]!;
        Assert.AreEqual(1, betaSongs.Count);
        Assert.AreEqual("Beta 1", betaSongs[0]!.Value<string>("title"));
        Assert.AreEqual(2, alphaSongs.Count);
        Assert.AreEqual("Alpha 1", alphaSongs[0]!.Value<string>("title"));
        Assert.AreEqual("Alpha 2", alphaSongs[1]!.Value<string>("title"));
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CleanupManagedFiles_RemovesOnlyManifestEntries()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            var table = new BMSTable
            {
                name = "Local Table",
                playlist_id = 7,
                Folder_order = ["Alpha"],
                entries =
                [
                    new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Local Song\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}"))
                ]
            };
            string managedFileName = BmtTableExportService.ExportTable(tempDirectory, table);
            string unmanagedPath = Path.Combine(tempDirectory, "unmanaged.bmt");
            File.WriteAllText(unmanagedPath, "keep");

            BmtTableExportService.CleanupManagedFiles(tempDirectory);

            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, managedFileName)));
            Assert.IsTrue(File.Exists(unmanagedPath));
            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName)));
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableData_WithSamePlaylistIdentityRemovesOldUrlFile()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            var oldTableData = new JObject
            {
                ["url"] = "bemusicseeker://playlist/7",
                ["name"] = "Old",
                ["folder"] = new JArray(new JObject
                {
                    ["name"] = "Alpha",
                    ["songs"] = new JArray(new JObject
                    {
                        ["title"] = "Song",
                        ["md5"] = Md5A
                    })
                }),
                ["course"] = new JArray()
            };
            var newTableData = (JObject)oldTableData.DeepClone();
            newTableData["url"] = "https://example.com/new-table";

            string oldFileName = BmtTableExportService.ExportTableData(tempDirectory, oldTableData, "7");
            string newFileName = BmtTableExportService.ExportTableData(tempDirectory, newTableData, "7");

            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, oldFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, newFileName)));
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_WithCleanupRemovesManagedFilesOutsideCurrentActiveSet()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject firstTableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            JObject secondTableData = CreateLocalTableData("bemusicseeker://playlist/2", "Second");

            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", firstTableData),
                Tuple.Create("2", secondTableData)
            ], cleanupStaleManagedFiles: true);
            string firstFileName = BMSTable.ComputeSha256Hex(firstTableData.Value<string>("url")) + ".bmt";
            string secondFileName = BMSTable.ComputeSha256Hex(secondTableData.Value<string>("url")) + ".bmt";
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, firstFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, secondFileName)));

            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("2", secondTableData)
            ], cleanupStaleManagedFiles: true);

            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, firstFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, secondFileName)));
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_SkipsUnchangedManagedPlaylistWrites()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject tableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            BmtTableExportService.ExportResult firstResult = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], cleanupStaleManagedFiles: true);
            string fileName = BMSTable.ComputeSha256Hex(tableData.Value<string>("url")) + ".bmt";
            string filePath = Path.Combine(tempDirectory, fileName);
            var markerTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, markerTime);
            DateTime stampedTime = File.GetLastWriteTimeUtc(filePath);

            BmtTableExportService.ExportResult secondResult = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], cleanupStaleManagedFiles: true);

            Assert.AreEqual(1, firstResult.WrittenCount);
            Assert.AreEqual(0, firstResult.SkippedWriteCount);
            Assert.AreEqual(0, firstResult.RemovedCount);
            Assert.AreEqual(0, secondResult.WrittenCount);
            Assert.AreEqual(1, secondResult.SkippedWriteCount);
            Assert.AreEqual(0, secondResult.RemovedCount);
            Assert.AreEqual(stampedTime, File.GetLastWriteTimeUtc(filePath));
            Assert.IsFalse(string.IsNullOrWhiteSpace(secondResult.CurrentManagedTables.Single().ContentHash));
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_RewritesWhenManagedPlaylistContentChanges()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject tableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], cleanupStaleManagedFiles: true);
            JObject changedTableData = CreateLocalTableData("bemusicseeker://playlist/1", "First changed");

            BmtTableExportService.ExportResult result = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", changedTableData)
            ], cleanupStaleManagedFiles: true);

            Assert.AreEqual(1, result.WrittenCount);
            Assert.AreEqual(0, result.SkippedWriteCount);
            Assert.AreEqual("First changed", result.CurrentManagedTables.Single().Name);
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_ReportsProgressForEachManagedPlaylistEvenWhenSkipped()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject firstTableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            JObject secondTableData = CreateLocalTableData("bemusicseeker://playlist/2", "Second");
            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", firstTableData),
                Tuple.Create("2", secondTableData)
            ], cleanupStaleManagedFiles: true);
            var progress = new List<Tuple<int, int, string>>();

            BmtTableExportService.ExportResult result = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", firstTableData),
                Tuple.Create("2", secondTableData)
            ], cleanupStaleManagedFiles: true, delegate (int completed, int total, string tableName)
            {
                progress.Add(Tuple.Create(completed, total, tableName));
            });

            Assert.AreEqual(0, result.WrittenCount);
            Assert.AreEqual(2, result.SkippedWriteCount);
            Assert.AreEqual(2, progress.Count);
            CollectionAssert.AreEqual(new[] { 1, 2 }, progress.Select(item => item.Item1).ToArray());
            Assert.IsTrue(progress.All(item => item.Item2 == 2));
            CollectionAssert.AreEquivalent(new[] { "First", "Second" }, progress.Select(item => item.Item3).ToArray());
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_DoesNotReportProgressForEmptyDataSet()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            var progress = new List<Tuple<int, int, string>>();

            BmtTableExportService.ExportTableDataSet(tempDirectory, Array.Empty<Tuple<string, JObject>>(), cleanupStaleManagedFiles: true, delegate (int completed, int total, string tableName)
            {
                progress.Add(Tuple.Create(completed, total, tableName));
            });

            Assert.AreEqual(0, progress.Count);
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_WithCleanupAndEmptyCurrentSetRemovesAllManagedFiles()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject tableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            string fileName = BmtTableExportService.ExportTableData(tempDirectory, tableData, "1");
            string unmanagedPath = Path.Combine(tempDirectory, "unmanaged.bmt");
            File.WriteAllText(unmanagedPath, "keep");

            BmtTableExportService.ExportTableDataSet(tempDirectory, Array.Empty<Tuple<string, JObject>>(), cleanupStaleManagedFiles: true);

            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, fileName)));
            Assert.IsTrue(File.Exists(unmanagedPath));
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_ReturnsManagedUrlsForConfigSync()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject firstTableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            BmtTableExportService.ExportResult firstResult = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", firstTableData)
            ], cleanupStaleManagedFiles: true);
            Assert.AreEqual(0, firstResult.PreviousManagedTables.Count);
            Assert.AreEqual("bemusicseeker://playlist/1", firstResult.CurrentManagedTables.Single().Url);
            Assert.AreEqual("First", firstResult.CurrentManagedTables.Single().Name);

            JObject secondTableData = CreateLocalTableData("bemusicseeker://playlist/2", "Second");
            BmtTableExportService.ExportResult secondResult = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("2", secondTableData)
            ], cleanupStaleManagedFiles: true);

            Assert.AreEqual("bemusicseeker://playlist/1", secondResult.PreviousManagedTables.Single().Url);
            Assert.AreEqual("bemusicseeker://playlist/2", secondResult.CurrentManagedTables.Single().Url);
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveManagedPlaylist_WhenPlaylistIsUnknownKeepsCurrentManagedUrls()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject tableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], cleanupStaleManagedFiles: true);

            BmtTableExportService.ExportResult result = BmtTableExportService.RemoveManagedPlaylist(tempDirectory, "missing");

            Assert.AreEqual("bemusicseeker://playlist/1", result.PreviousManagedTables.Single().Url);
            Assert.AreEqual("bemusicseeker://playlist/1", result.CurrentManagedTables.Single().Url);
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BeatorajaConfigService_SyncTableUrlsPreservesUnmanagedAndReplacesManaged()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string root = CreateBeatorajaRoot(tempDirectory);
            string configPath = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(configPath, "{\"tablepath\":\"table\",\"playerpath\":\"player\",\"playername\":\"player2\",\"tableURL\":[\"https://external.example/table\",\"bemusicseeker://playlist/old\",\"https://keep.example/table\"]}", Encoding.UTF8);

            BeatorajaConfigService.SyncTableUrls(
                root,
                ["bemusicseeker://playlist/2", "bemusicseeker://playlist/1"],
                ["bemusicseeker://playlist/old", "bemusicseeker://playlist/1"]);

            var config = JObject.Parse(File.ReadAllText(configPath, Encoding.UTF8));
            CollectionAssert.AreEqual(new[]
            {
                "https://external.example/table",
                "https://keep.example/table",
                "bemusicseeker://playlist/2",
                "bemusicseeker://playlist/1"
            }, config["tableURL"]!.Select(token => token.ToString()).ToArray());
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BeatorajaConfigService_ResolvesConfiguredTableAndPlayerPaths()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string root = CreateBeatorajaRoot(tempDirectory);
            string configPath = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
            Directory.CreateDirectory(Path.Combine(root, "players", "player1"));
            Directory.CreateDirectory(Path.Combine(root, "players", "player2"));
            File.WriteAllText(Path.Combine(root, "players", "player2", "score.db"), string.Empty);
            File.WriteAllText(configPath, "{\"tablepath\":\"tables\",\"playerpath\":\"players\",\"playername\":\"player2\",\"tableURL\":[]}", Encoding.UTF8);

            Assert.IsTrue(BeatorajaConfigService.IsBeatorajaRootPathValid(root));
            Assert.AreEqual(Path.Combine(root, "tables"), BeatorajaConfigService.GetTablePath(root));
            Assert.AreEqual(Path.Combine(root, "players", "player2", "score.db"), BeatorajaConfigService.GetScoreDbPath(root, "player2"));
            CollectionAssert.AreEqual(new[] { "player1", "player2" }, BeatorajaConfigService.GetPlayerIds(root).ToArray());
            Assert.IsTrue(BeatorajaConfigService.IsPlayerScoreDbPathValid(root, "player2"));
        });
    }

    private static JObject ReadBmtJson(string path)
    {
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream, Encoding.UTF8);
        return JObject.Parse(reader.ReadToEnd());
    }

    private static JObject CreateLocalTableData(string url, string name)
    {
        return new JObject
        {
            ["url"] = url,
            ["name"] = name,
            ["folder"] = new JArray(new JObject
            {
                ["name"] = "Alpha",
                ["songs"] = new JArray(new JObject
                {
                    ["title"] = "Song",
                    ["md5"] = Md5A
                })
            }),
            ["course"] = new JArray()
        };
    }

    private static string CreateBeatorajaRoot(string tempDirectory)
    {
        string root = Path.Combine(tempDirectory, "beatoraja");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "table"));
        Directory.CreateDirectory(Path.Combine(root, "player"));
        File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(root, BeatorajaConfigService.ConfigFileName), "{\"tablepath\":\"table\",\"playerpath\":\"player\",\"tableURL\":[]}", Encoding.UTF8);
        return root;
    }

    private static void WithTemporaryDirectory(Action<string> testAction)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmtTableExportServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testAction(tempDirectory);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private sealed class TestSongHashResolver(Dictionary<string, Tuple<string, string>> hashesByTitle)
        : BmtTableExportService.ISongHashResolver
    {
        public BmtTableExportService.SongHashResolution Resolve(BmtSongHashResolveRequest request)
        {
            return request != null && hashesByTitle.TryGetValue(request.Title, out Tuple<string, string> hashes)
                ? new BmtTableExportService.SongHashResolution(hashes.Item1, hashes.Item2)
                : new BmtTableExportService.SongHashResolution(null, null);
        }
    }
}
