using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using Codeplex.Data;
using Newtonsoft.Json.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
            BMSTable table = new BMSTable();
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
            CollectionAssert.AreEqual(new[] { "MIRROR", "GAUGE_LR2", "LN" }, json["course"]![0]!["constraint"]!.Select((JToken token) => token.ToString()).ToArray());
            Assert.AreEqual(Md5A, json["course"]![0]!["hash"]![0]!.Value<string>("md5"));
            Assert.AreEqual(Sha256B, json["course"]![0]!["hash"]![1]!.Value<string>("sha256"));
            Assert.AreEqual(1, json["course"]![0]!["trophy"]!.Count());
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildTableData_LocalPlaylistUsesPseudoUrlAndFolderNameAsIs()
    {
        BMSTable table = new BMSTable
        {
            name = "Local Table",
            playlist_id = 42,
            Folder_order = new List<string> { "Alpha" },
            entries = new List<BMSTableEntry>
            {
                new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Local Song\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}"))
            }
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
            BMSTable table = new BMSTable
            {
                name = "Local Table",
                playlist_id = 7,
                Folder_order = new List<string> { "Alpha" },
                entries = new List<BMSTableEntry>
                {
                    new BMSTableEntry(DynamicJson.Parse("{\"title\":\"Local Song\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}"))
                }
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
            JObject oldTableData = new JObject
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
            JObject newTableData = (JObject)oldTableData.DeepClone();
            newTableData["url"] = "https://example.com/new-table";

            string oldFileName = BmtTableExportService.ExportTableData(tempDirectory, oldTableData, "7");
            string newFileName = BmtTableExportService.ExportTableData(tempDirectory, newTableData, "7");

            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, oldFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, newFileName)));
        });
    }

    private static JObject ReadBmtJson(string path)
    {
        using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using GZipStream gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using StreamReader reader = new StreamReader(gzipStream, Encoding.UTF8);
        return JObject.Parse(reader.ReadToEnd());
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
