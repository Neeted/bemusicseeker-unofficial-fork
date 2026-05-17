using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
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

            BmtTableExportService.ExportTableDataSet(tempDirectory, new[]
            {
                Tuple.Create("1", firstTableData),
                Tuple.Create("2", secondTableData)
            }, cleanupStaleManagedFiles: true);
            string firstFileName = BMSTable.ComputeSha256Hex(firstTableData.Value<string>("url")) + ".bmt";
            string secondFileName = BMSTable.ComputeSha256Hex(secondTableData.Value<string>("url")) + ".bmt";
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, firstFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, secondFileName)));

            BmtTableExportService.ExportTableDataSet(tempDirectory, new[]
            {
                Tuple.Create("2", secondTableData)
            }, cleanupStaleManagedFiles: true);

            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, firstFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, secondFileName)));
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
            BmtTableExportService.ExportResult firstResult = BmtTableExportService.ExportTableDataSet(tempDirectory, new[]
            {
                Tuple.Create("1", firstTableData)
            }, cleanupStaleManagedFiles: true);
            Assert.AreEqual(0, firstResult.PreviousManagedTables.Count);
            Assert.AreEqual("bemusicseeker://playlist/1", firstResult.CurrentManagedTables.Single().Url);
            Assert.AreEqual("First", firstResult.CurrentManagedTables.Single().Name);

            JObject secondTableData = CreateLocalTableData("bemusicseeker://playlist/2", "Second");
            BmtTableExportService.ExportResult secondResult = BmtTableExportService.ExportTableDataSet(tempDirectory, new[]
            {
                Tuple.Create("2", secondTableData)
            }, cleanupStaleManagedFiles: true);

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
            BmtTableExportService.ExportTableDataSet(tempDirectory, new[]
            {
                Tuple.Create("1", tableData)
            }, cleanupStaleManagedFiles: true);

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
                new[] { "bemusicseeker://playlist/2", "bemusicseeker://playlist/1" },
                new[] { "bemusicseeker://playlist/old", "bemusicseeker://playlist/1" });

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
}
