using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmtTableExportServiceTests
{
    // BMT-MANIFEST-FAILURE-1 / BMT-O: OS の delete 共有拒否で実際の cleanup 失敗を作る。
    [TestMethod]
    [DataRow("cleanup")]
    [DataRow("remove")]
    [DataRow("url-only")]
    [DataRow("full")]
    [DataRow("rename")]
    public void ManagedCleanupFailure_RetainsPhysicalOwnership(string route)
    {
        WithTemporaryDirectory(directory =>
        {
            string lockedPath = Path.Combine(directory, "locked.bmt");
            File.WriteAllText(lockedPath, "owned");
            File.WriteAllText(Path.Combine(directory, "removable.bmt"), "owned");
            string manifestPath = Path.Combine(directory, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifestPath, """
                {"files":["locked.bmt","removable.bmt","missing.bmt"],"playlists":{"1":{"file":"locked.bmt","url":"https://example.com/old"}}}
                """);
            using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                BmtTableExportService.ExportResult? result = null;
                switch (route)
                {
                    case "cleanup": result = BmtTableExportService.CleanupManagedFiles(directory); break;
                    case "remove": result = BmtTableExportService.RemoveManagedPlaylist(directory, "1"); break;
                    case "url-only": result = BmtTableExportService.UpdateManagedPlaylistUrlOwnership(directory, CreateExportMetadata("1", "https://example.com/new", "New")); break;
                    case "full": result = BmtTableExportService.ExportTableDataSet(directory, Array.Empty<JObject>(), true); break;
                    case "rename": BmtTableExportService.ExportTableData(directory, CreateSimpleTableData("https://example.com/new", "New"), CreateExportMetadata("1", "https://example.com/new", "New"), out result); break;
                }
                BmtTableExportService.ExportResult completedResult = result!;
                Assert.IsTrue(File.Exists(manifestPath), "未削除ファイルの台帳を残す。");
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                CollectionAssert.Contains(manifest["files"]!.Values<string>().ToArray(), "locked.bmt");
                Assert.AreNotEqual("locked.bmt", manifest["playlists"]?["1"]?["file"]?.Value<string>());
                Assert.AreEqual(route is "cleanup" or "full" ? 2 : 0, completedResult.RemovedCount);
                Assert.AreEqual(lockedPath, completedResult.Failures.Single().Path);
                Assert.IsFalse(string.IsNullOrWhiteSpace(completedResult.Failures.Single().Cause));
                if (route is "cleanup" or "full" or "remove")
                    Assert.IsTrue(manifest["playlists"] == null || !manifest["playlists"]!.HasValues);
                else
                    Assert.AreEqual("https://example.com/new", manifest["playlists"]!["1"]!["url"]!.Value<string>());
            }
            BmtTableExportService.CleanupManagedFiles(directory);
            Assert.IsFalse(File.Exists(lockedPath), "後続の通常 cleanup で残留を回収する。");
            Assert.IsFalse(File.Exists(manifestPath));
        });
    }

    [TestMethod]
    public void ExportWithoutCleanup_PreservesPreviousPhysicalOwnership()
    {
        WithTemporaryDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "old.bmt"), "old");
            string manifestPath = Path.Combine(directory, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifestPath, "{\"files\":[\"old.bmt\"]}");
            BmtTableExportService.ExportTableDataSet(directory, Array.Empty<JObject>(), false);
            CollectionAssert.Contains(JObject.Parse(File.ReadAllText(manifestPath))["files"]!.Values<string>().ToArray(), "old.bmt");
        });
    }

    // BMT-V: 正常項目との混在も部分 salvage せず、原本と BMT を出力前に保全する。
    [TestMethod]
    [DataRow("{")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"files\":null}")]
    [DataRow("{\"files\":[],\"schemaVersion\":3}")]
    [DataRow("{\"files\":[],\"schemaVersion\":\"2\"}")]
    [DataRow("{\"files\":[],\"files\":[]}")]
    [DataRow("{\"files\":[\"old.bmt\",\"../escape.bmt\"]}")]
    [DataRow("{\"files\":[42]}")]
    [DataRow("{\"files\":[],\"playlists\":null}")]
    [DataRow("{\"files\":[],\"playlists\":{\"1\":{\"url\":\"u\",\"contentHash\":1}}}")]
    [DataRow("{\"files\":[],\"playlists\":{\"1\":{\"url\":\"u\",\"url\":\"v\"}}}")]
    [DataRow("{\"files\":[],\"schemaVersion\":null}")]
    [DataRow("{\"files\":[],\"exporterVersion\":\"1\"}")]
    [DataRow("{\"files\":[]}{}")]
    [DataRow("{\"files\":[],\"playlists\":{\"1\":{\"url\":\"https://example.com\",\"file\":\"x/y.bmt\"}}}")]
    [DataRow("{\"files\":[],\"playlists\":{\"1\":{\"url\":\"https://example.com\",\"bmtLength\":\"2\"}}}")]
    public void InvalidManifest_RejectsIndividualAndFullExportBeforeWriting(string contents)
    {
        WithTemporaryDirectory(directory =>
        {
            string manifestPath = Path.Combine(directory, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifestPath, contents);
            byte[] original = File.ReadAllBytes(manifestPath);
            File.WriteAllText(Path.Combine(directory, "old.bmt"), "original");
            JObject data = CreateSimpleTableData("https://example.com/new", "New");
            AssertBmtFailure(() => BmtTableExportService.ExportTableData(directory, data, "1"));
            AssertBmtFailure(() => BmtTableExportService.ExportTableDataSet(directory, new[] { data }, true));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(manifestPath));
            Assert.AreEqual("original", File.ReadAllText(Path.Combine(directory, "old.bmt")));
            Assert.AreEqual(1, Directory.GetFiles(directory, "*.bmt").Length);
        });
    }

    [TestMethod]
    public void UnreadableManifest_RejectsExportBeforeWriting()
    {
        WithTemporaryDirectory(directory =>
        {
            string manifestPath = Path.Combine(directory, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifestPath, "{\"files\":[]}");
            byte[] original = File.ReadAllBytes(manifestPath);
            using (var held = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
                AssertBmtFailure(() => BmtTableExportService.ExportTableData(directory, CreateSimpleTableData("https://example.com/new", "New"), "1"));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(manifestPath));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.bmt").Length);
        });
    }

    // BMT-P: read/write は許し、同一 directory 置換に必要な delete 共有だけを拒否する。
    [TestMethod]
    public void ManifestPublishFailure_PreservesOriginalBytesAndRemovesOwnedTemp()
    {
        WithTemporaryDirectory(directory =>
        {
            string manifestPath = Path.Combine(directory, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifestPath, "{\"files\":[],\"metadata\":\"original\"}");
            byte[] original = File.ReadAllBytes(manifestPath);
            Exception? failure = null;
            using (var held = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                try { BmtTableExportService.UpdateManagedPlaylistUrlOwnership(directory, CreateExportMetadata("1", "https://example.com/new", "New")); }
                catch (Exception exception) { failure = exception; }
            }
            CollectionAssert.AreEqual(original, File.ReadAllBytes(manifestPath));
            Assert.IsInstanceOfType<IOException>(failure);
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
        });
    }

    // BMT-C: 既知 schema / URL-only / 旧 cache metadata の受理範囲。
    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void KnownManifestSchemas_AcceptOptionalDefaultsAndPlaylistFileOwnership(int schema)
    {
        WithTemporaryDirectory(directory =>
        {
            var manifest = JObject.Parse("""
                {"files":["old.bmt","OLD.bmt"],"extra":{"value":true},"exporterVersion":99,
                 "playlists":{"1":{"url":"https://example.com/one","file":"referenced.bmt","name":"2026-09-06T12:00:00Z","contentHash":"old","headerSha256":null},
                              "2":{"url":"https://example.com/two","file":null},"3":{"url":"https://example.com/three","file":""}}}
                """);
            // JObject の Date 自動変換を避け、文字列型の cache / name を入力として保証する。
            manifest["playlists"]!["1"]!["name"] = "2026-09-06T12:00:00Z";
            if (schema >= 0) manifest["schemaVersion"] = schema;
            File.WriteAllText(Path.Combine(directory, BmtTableExportService.ManifestFileName), manifest.ToString());
            var plan = BmtTableExportService.CreateExportPlan(directory, new[] { CreateExportMetadata("1", "https://example.com/one", "Name") }, false);
            Assert.AreEqual(3, BmtTableExportService.ReadManagedTableUrls(directory).Count);
            Assert.IsTrue(plan.RequiresProjection(CreateExportMetadata("1", "https://example.com/one", "Name")));
            var cleanup = BmtTableExportService.CleanupManagedFiles(directory);
            Assert.AreEqual(2, cleanup.RemovedCount, "大文字小文字の重複をまとめ、playlist 参照も物理台帳へ取り込む。");
        });
    }

    [TestMethod]
    public void OrderedProjectionProgressPublisherSerializesCallbacksInCompletionOrder()
    {
        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var observed = new List<int>();
        int callbackConcurrency = 0;
        int maximumCallbackConcurrency = 0;
        var publisher = new PlaylistBmtOutputOwner.OrderedProjectionProgressPublisher(
            2,
            (completed, _, _) =>
            {
                int concurrency = Interlocked.Increment(ref callbackConcurrency);
                maximumCallbackConcurrency = Math.Max(maximumCallbackConcurrency, concurrency);
                try
                {
                    if (completed == 1)
                    {
                        firstEntered.Set();
                        releaseFirst.Wait();
                    }
                    lock (observed)
                    {
                        observed.Add(completed);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref callbackConcurrency);
                }
            });

        Task? first = null;
        Task? second = null;
        try
        {
            first = Task.Factory.StartNew(
                () => publisher.PublishNext("first"),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Factory.StartNew(
                () => publisher.PublishNext("second"),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.IsTrue(second.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            releaseFirst.Set();
            first?.Wait(TimeSpan.FromSeconds(10));
            second?.Wait(TimeSpan.FromSeconds(10));
        }
        Task completedFirst = first!;
        Task completedSecond = second!;
        Assert.IsTrue(completedFirst.IsCompletedSuccessfully);
        Assert.IsTrue(completedSecond.IsCompletedSuccessfully);

        CollectionAssert.AreEqual(new[] { 1, 2 }, observed);
        Assert.AreEqual(1, maximumCallbackConcurrency);
    }

    private static void AssertBmtFailure(Action operation)
    {
        Exception? failure = null;
        try { operation(); }
        catch (Exception exception) { failure = exception; }
        Assert.IsNotNull(failure, "失敗を隠さず呼出し元へ返す。");
        Exception completedFailure = failure!;
        Assert.IsTrue(completedFailure is IOException or InvalidDataException, completedFailure.ToString());
    }

    private const string Sha256A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sha256B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Md5A = "11111111111111111111111111111111";
    private const string Md5B = "22222222222222222222222222222222";

    [TestMethod]
    public void BuildBeatorajaManagedTableUrlsForConfigSync_OrdersByPlaylistBmtSort()
    {
        var managedTables = new[]
        {
            new BmtTableExportService.ManagedTableUrlEntry { PlaylistIdentity = "2", Name = "Beta", Url = "file:///beta.bmt" },
            new BmtTableExportService.ManagedTableUrlEntry { PlaylistIdentity = "1", Name = "Alpha", Url = "file:///alpha.bmt" },
            new BmtTableExportService.ManagedTableUrlEntry { PlaylistIdentity = "old", Name = "Old", Url = "file:///old.bmt" }
        };
        var sortKeys = new Dictionary<string, PlaylistBmtOutputOwner.BeatorajaBmtTableUrlSortKey>(StringComparer.Ordinal)
        {
            ["1"] = new PlaylistBmtOutputOwner.BeatorajaBmtTableUrlSortKey { Sort = 2, Name = "Alpha", PlaylistId = 1 },
            ["2"] = new PlaylistBmtOutputOwner.BeatorajaBmtTableUrlSortKey { Sort = 1, Name = "Beta", PlaylistId = 2 }
        };

        List<string> result = PlaylistBmtOutputOwner.BuildBeatorajaManagedTableUrlsForConfigSync(managedTables, sortKeys);

        CollectionAssert.AreEqual(new[] { "file:///beta.bmt", "file:///alpha.bmt", "file:///old.bmt" }, result);
    }

    [TestMethod]
    public void ExportTableDataSet_KeepsManagedUrlOwnershipWhenPlaylistCannotProduceBmt()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string url = "https://example.com/empty-table";
            BmtTableExportService.PlaylistExportMetadata metadata = CreateExportMetadata("1", url, "Empty Table");
            BmtTableExportService.ExportPlan exportPlan = BmtTableExportService.CreateExportPlan(
                tempDirectory,
                [metadata],
                cleanupStaleManagedFiles: true);

            BmtTableExportService.ExportResult result = BmtTableExportService.ExportTableDataSet(
                tempDirectory,
                Array.Empty<Tuple<string, JObject>>(),
                exportPlan,
                progressReporter: null);

            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), Encoding.UTF8));
            JObject playlist = (JObject)manifest["playlists"]!["1"]!;
            Assert.AreEqual(url, playlist.Value<string>("url"));
            Assert.AreEqual("Empty Table", playlist.Value<string>("name"));
            Assert.IsNull(playlist["file"]);
            Assert.AreEqual(0, manifest["files"]!.Count());
            Assert.AreEqual(1, result.CurrentManagedTables.Count);
            Assert.AreEqual(url, result.CurrentManagedTables[0].Url);
        });
    }

    [TestMethod]
    public void ExportTableDataSet_RemovesStaleBmtButKeepsManagedUrlOwnershipWhenPlaylistBecomesEmpty()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string url = "https://example.com/empty-table";
            string staleFileName = BMSTable.ComputeSha256Hex(url) + ".bmt";
            File.WriteAllText(Path.Combine(tempDirectory, staleFileName), "stale");
            File.WriteAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), new JObject
            {
                ["schemaVersion"] = 2,
                ["exporterVersion"] = 1,
                ["files"] = new JArray(staleFileName),
                ["playlists"] = new JObject
                {
                    ["1"] = new JObject
                    {
                        ["file"] = staleFileName,
                        ["url"] = url,
                        ["name"] = "Empty Table",
                        ["headerSha256"] = "old-header",
                        ["dataSha256"] = "old-data",
                        ["lastUpdateTicks"] = 1,
                        ["projectionInputSha256"] = "old-projection",
                        ["bmtLastWriteTimeUtcTicks"] = 1,
                        ["bmtLength"] = 5
                    }
                }
            }.ToString(), Encoding.UTF8);
            BmtTableExportService.PlaylistExportMetadata metadata = CreateExportMetadata("1", url, "Empty Table");
            BmtTableExportService.ExportPlan exportPlan = BmtTableExportService.CreateExportPlan(
                tempDirectory,
                [metadata],
                cleanupStaleManagedFiles: true);

            BmtTableExportService.ExportResult result = BmtTableExportService.ExportTableDataSet(
                tempDirectory,
                Array.Empty<Tuple<string, JObject>>(),
                exportPlan,
                progressReporter: null);

            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), Encoding.UTF8));
            JObject playlist = (JObject)manifest["playlists"]!["1"]!;
            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, staleFileName)));
            Assert.AreEqual(1, result.RemovedCount);
            Assert.AreEqual(url, playlist.Value<string>("url"));
            Assert.IsNull(playlist["file"]);
            Assert.AreEqual(0, manifest["files"]!.Count());
        });
    }

    [TestMethod]
    public void UpdateManagedPlaylistUrlOwnership_RemovesStaleBmtButKeepsManagedUrl()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string url = "https://example.com/empty-table";
            string staleFileName = BMSTable.ComputeSha256Hex(url) + ".bmt";
            File.WriteAllText(Path.Combine(tempDirectory, staleFileName), "stale");
            File.WriteAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), new JObject
            {
                ["schemaVersion"] = 2,
                ["exporterVersion"] = 1,
                ["files"] = new JArray(staleFileName),
                ["playlists"] = new JObject
                {
                    ["1"] = new JObject
                    {
                        ["file"] = staleFileName,
                        ["url"] = url,
                        ["name"] = "Old Table"
                    }
                }
            }.ToString(), Encoding.UTF8);
            BmtTableExportService.PlaylistExportMetadata metadata = CreateExportMetadata("1", url, "Empty Table");

            BmtTableExportService.ExportResult result = BmtTableExportService.UpdateManagedPlaylistUrlOwnership(tempDirectory, metadata);

            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), Encoding.UTF8));
            JObject playlist = (JObject)manifest["playlists"]!["1"]!;
            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, staleFileName)));
            Assert.AreEqual(1, result.RemovedCount);
            Assert.AreEqual(url, playlist.Value<string>("url"));
            Assert.AreEqual("Empty Table", playlist.Value<string>("name"));
            Assert.IsNull(playlist["file"]);
            Assert.AreEqual(url, result.CurrentManagedTables.Single().Url);
        });
    }

    [TestMethod]
    public void UpdateManagedPlaylistUrlOwnership_NoopsWhenUrlOnlyEntryIsCurrent()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string url = "https://example.com/empty-table";
            BmtTableExportService.PlaylistExportMetadata metadata = CreateExportMetadata("1", url, "Empty Table");
            BmtTableExportService.UpdateManagedPlaylistUrlOwnership(tempDirectory, metadata);
            string manifestPath = Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName);
            DateTime firstWriteTime = File.GetLastWriteTimeUtc(manifestPath);

            BmtTableExportService.ExportResult result = BmtTableExportService.UpdateManagedPlaylistUrlOwnership(tempDirectory, metadata);

            Assert.IsFalse(result.Changed);
            Assert.AreEqual(0, result.RemovedCount);
            Assert.AreEqual(firstWriteTime, File.GetLastWriteTimeUtc(manifestPath));
            Assert.AreEqual(url, result.CurrentManagedTables.Single().Url);
        });
    }

    [TestMethod]
    public void ExportTableDataSet_SkipsCurrentManagedUrlOnlyPlaylist()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string url = "https://example.com/empty-table";
            BmtTableExportService.PlaylistExportMetadata metadata = CreateExportMetadata("1", url, "Empty Table");
            BmtTableExportService.ExportPlan firstPlan = BmtTableExportService.CreateExportPlan(
                tempDirectory,
                [metadata],
                cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportTableDataSet(
                tempDirectory,
                Array.Empty<Tuple<string, JObject>>(),
                firstPlan,
                progressReporter: null);

            BmtTableExportService.ExportPlan secondPlan = BmtTableExportService.CreateExportPlan(
                tempDirectory,
                [metadata],
                cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportResult secondResult = BmtTableExportService.ExportTableDataSet(
                tempDirectory,
                Array.Empty<Tuple<string, JObject>>(),
                secondPlan,
                progressReporter: null);

            Assert.IsFalse(secondPlan.RequiresProjection(metadata));
            Assert.AreEqual(1, secondResult.SkippedWriteCount);
            Assert.AreEqual(0, secondResult.WrittenCount);
            Assert.AreEqual(0, secondResult.RemovedCount);
            Assert.AreEqual(url, secondResult.CurrentManagedTables.Single().Url);
            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), Encoding.UTF8));
            Assert.AreEqual(0, manifest["files"]!.Count());
            Assert.IsNull(manifest["playlists"]!["1"]!["file"]);
        });
    }

    [TestMethod]
    public void ExportTableDataSet_WritesBmtWhenManagedUrlOnlyPlaylistLaterHasData()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string url = "https://example.com/recovered-table";
            File.WriteAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), new JObject
            {
                ["schemaVersion"] = 2,
                ["exporterVersion"] = 1,
                ["files"] = new JArray(),
                ["playlists"] = new JObject
                {
                    ["1"] = new JObject
                    {
                        ["url"] = url,
                        ["name"] = "Recovered Table"
                    }
                }
            }.ToString(), Encoding.UTF8);
            JObject tableData = CreateSimpleTableData(url, "Recovered Table");

            BmtTableExportService.ExportResult result = BmtTableExportService.ExportTableDataSet(
                tempDirectory,
                [Tuple.Create("1", tableData)],
                cleanupStaleManagedFiles: true);

            string fileName = BMSTable.ComputeSha256Hex(url) + ".bmt";
            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), Encoding.UTF8));
            JObject playlist = (JObject)manifest["playlists"]!["1"]!;
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, fileName)));
            Assert.AreEqual(1, result.WrittenCount);
            Assert.AreEqual(fileName, playlist.Value<string>("file"));
            Assert.AreEqual(url, playlist.Value<string>("url"));
            CollectionAssert.AreEqual(new[] { fileName }, manifest["files"]!.Select(token => token.ToString()).ToArray());
        });
    }

    [TestMethod]
    public void BeatorajaConfigService_SyncTableUrlsMovesManagedEmptyPlaylistUrlOutOfUnmanagedPrefix()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string root = CreateBeatorajaRoot(tempDirectory);
            string configPath = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(configPath, "{\"tablepath\":\"table\",\"playerpath\":\"player\",\"playername\":\"player2\",\"tableURL\":[\"https://example.com/managed-empty\",\"https://external.example/table\"]}", Encoding.UTF8);

            BeatorajaConfigService.SyncTableUrls(
                root,
                ["https://example.com/managed-empty"],
                []);

            var config = JObject.Parse(File.ReadAllText(configPath, Encoding.UTF8));
            CollectionAssert.AreEqual(new[]
            {
                "https://external.example/table",
                "https://example.com/managed-empty"
            }, config["tableURL"]!.Select(token => token.ToString()).ToArray());
        });
    }

    [TestMethod]
    public void NormalizeBeatorajaBmtSortOrder_AssignsStableSequentialOrder()
    {
        var alpha = new BMSTable { playlist_id = 1, name = "Alpha", bmt_sort = 1 };
        var bravo = new BMSTable { playlist_id = 2, name = "Bravo", bmt_sort = null };
        var charlie = new BMSTable { playlist_id = 3, name = "Charlie", bmt_sort = 1 };
        var tables = new[] { bravo, charlie, alpha };

        int changedCount = PlaylistBmtOutputOwner.NormalizeBeatorajaBmtSortOrder(tables);

        Assert.AreEqual(2, changedCount);
        Assert.AreEqual(1, alpha.bmt_sort);
        Assert.AreEqual(2, charlie.bmt_sort);
        Assert.AreEqual(3, bravo.bmt_sort);
    }

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
                new BMSTableEntry(JObject.Parse("{\"title\":\"Local Song\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}"))
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
                new BMSTableEntry(JObject.Parse("{\"title\":\"Md5 Only\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Sha Only\",\"sha256\":\"" + Sha256B + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Both\",\"md5\":\"33333333333333333333333333333333\",\"sha256\":\"" + new string('c', 64) + "\",\"level\":\"Alpha\"}"))
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
                new BMSTableEntry(JObject.Parse("{\"title\":\"Md5 Only\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Sha Only\",\"sha256\":\"" + Sha256B + "\",\"level\":\"Alpha\"}"))
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
                new BMSTableEntry(JObject.Parse("{\"title\":\"Md5 Resolved\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Md5 Unresolved\",\"md5\":\"" + Md5B + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Sha Only\",\"sha256\":\"" + Sha256B + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Both\",\"md5\":\"33333333333333333333333333333333\",\"sha256\":\"" + new string('c', 64) + "\",\"level\":\"Alpha\"}"))
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
                new BMSTableEntry(JObject.Parse("{\"title\":\"Alpha 1\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Beta 1\",\"md5\":\"" + Md5B + "\",\"level\":\"Beta\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Alpha 2\",\"sha256\":\"" + Sha256A + "\",\"level\":\"Alpha\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Removed\",\"md5\":\"33333333333333333333333333333333\",\"level\":\"Beta\"}"))
                {
                    is_removed = true
                },
                new BMSTableEntry(JObject.Parse("{\"title\":\"No Hash\",\"level\":\"Beta\"}")),
                new BMSTableEntry(JObject.Parse("{\"title\":\"Empty Folder\",\"level\":\"Gamma\"}"))
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
                    new BMSTableEntry(JObject.Parse("{\"title\":\"Local Song\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}"))
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
            BmtTableExportService.PlaylistExportMetadata metadata = CreateExportMetadata("1", tableData, "header-a", "data-a", 100L);
            BmtTableExportService.ExportPlan firstPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [metadata], cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportResult firstResult = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], firstPlan, null);
            string fileName = BMSTable.ComputeSha256Hex(tableData.Value<string>("url")) + ".bmt";
            string filePath = Path.Combine(tempDirectory, fileName);
            DateTime firstWriteTime = File.GetLastWriteTimeUtc(filePath);

            BmtTableExportService.ExportPlan secondPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [metadata], cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportResult secondResult = BmtTableExportService.ExportTableDataSet(tempDirectory, Array.Empty<Tuple<string, JObject>>(), secondPlan, null);

            Assert.AreEqual(1, firstResult.WrittenCount);
            Assert.AreEqual(0, firstResult.SkippedWriteCount);
            Assert.AreEqual(0, firstResult.RemovedCount);
            Assert.AreEqual(0, secondResult.WrittenCount);
            Assert.AreEqual(1, secondResult.SkippedWriteCount);
            Assert.AreEqual(0, secondResult.RemovedCount);
            Assert.AreEqual(firstWriteTime, File.GetLastWriteTimeUtc(filePath));
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_SkipsUnchangedPlaylistFromManifestMetadataBeforeProjection()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject tableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            BmtTableExportService.PlaylistExportMetadata metadata = CreateExportMetadata("1", tableData, "header-a", "data-a", 100L);
            BmtTableExportService.ExportPlan firstPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [metadata], cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportResult firstResult = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], firstPlan, null);

            BmtTableExportService.ExportPlan secondPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [metadata], cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportResult secondResult = BmtTableExportService.ExportTableDataSet(tempDirectory, Array.Empty<Tuple<string, JObject>>(), secondPlan, null);

            Assert.AreEqual(1, firstResult.WrittenCount);
            Assert.IsTrue(firstPlan.RequiresProjection(metadata));
            Assert.IsFalse(secondPlan.RequiresProjection(metadata));
            Assert.AreEqual(0, secondResult.WrittenCount);
            Assert.AreEqual(1, secondResult.SkippedWriteCount);
            Assert.AreEqual("bemusicseeker://playlist/1", secondResult.CurrentManagedTables.Single().Url);
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_WritesManifestV2WithoutContentHash()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject tableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            BmtTableExportService.PlaylistExportMetadata metadata = CreateExportMetadata("1", tableData, "header-a", "data-a", 100L);
            BmtTableExportService.ExportPlan plan = BmtTableExportService.CreateExportPlan(tempDirectory, [metadata], cleanupStaleManagedFiles: true);

            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], plan, null);

            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), Encoding.UTF8));
            JObject playlist = (JObject)manifest["playlists"]!["1"]!;
            Assert.AreEqual(2, manifest.Value<int>("schemaVersion"));
            Assert.AreEqual("header-a", playlist.Value<string>("headerSha256"));
            Assert.AreEqual("data-a", playlist.Value<string>("dataSha256"));
            Assert.AreEqual(100L, playlist.Value<long>("lastUpdateTicks"));
            Assert.AreEqual("projection-1", playlist.Value<string>("projectionInputSha256"));
            Assert.IsTrue(playlist.Value<long>("bmtLastWriteTimeUtcTicks") > 0);
            Assert.IsTrue(playlist.Value<long>("bmtLength") > 0);
            Assert.IsNull(playlist["contentHash"]);
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_LegacyManifestFilesRemainCleanupCandidates()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string oldFileName = "old-managed.bmt";
            File.WriteAllText(Path.Combine(tempDirectory, oldFileName), "legacy");
            File.WriteAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), new JObject
            {
                ["files"] = new JArray(oldFileName),
                ["playlists"] = new JObject
                {
                    ["old"] = new JObject
                    {
                        ["file"] = oldFileName,
                        ["url"] = "bemusicseeker://playlist/old",
                        ["name"] = "Old",
                        ["contentHash"] = "legacy"
                    }
                }
            }.ToString(), Encoding.UTF8);

            BmtTableExportService.ExportTableDataSet(tempDirectory, Array.Empty<Tuple<string, JObject>>(), cleanupStaleManagedFiles: true);

            JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(tempDirectory, BmtTableExportService.ManifestFileName), Encoding.UTF8));
            Assert.IsFalse(File.Exists(Path.Combine(tempDirectory, oldFileName)));
            Assert.AreEqual(2, manifest.Value<int>("schemaVersion"));
            Assert.IsNull(manifest["playlists"]);
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_RewritesWhenManagedPlaylistFileTimestampChanges()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject tableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            BmtTableExportService.PlaylistExportMetadata metadata = CreateExportMetadata("1", tableData, "header-a", "data-a", 100L);
            BmtTableExportService.ExportPlan firstPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [metadata], cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], firstPlan, null);
            string fileName = BMSTable.ComputeSha256Hex(tableData.Value<string>("url")) + ".bmt";
            string filePath = Path.Combine(tempDirectory, fileName);
            var markerTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, markerTime);
            DateTime stampedTime = File.GetLastWriteTimeUtc(filePath);

            BmtTableExportService.ExportPlan secondPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [metadata], cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportResult result = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], secondPlan, null);

            Assert.AreEqual(1, result.WrittenCount);
            Assert.AreEqual(0, result.SkippedWriteCount);
            Assert.AreNotEqual(stampedTime, File.GetLastWriteTimeUtc(filePath));
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
    public void ExportTableDataSet_RewritesWhenProjectionInputFingerprintChanges()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject tableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            BmtTableExportService.PlaylistExportMetadata firstMetadata = CreateExportMetadata("1", tableData, "header-a", "data-a", 100L);
            BmtTableExportService.ExportPlan firstPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [firstMetadata], cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], firstPlan, null);
            BmtTableExportService.PlaylistExportMetadata changedMetadata = CreateExportMetadata("1", tableData, "header-a", "data-a", 100L);
            changedMetadata.ProjectionInputSha256 = "projection-changed";
            BmtTableExportService.ExportPlan secondPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [changedMetadata], cleanupStaleManagedFiles: true);

            BmtTableExportService.ExportResult result = BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData)
            ], secondPlan, null);

            Assert.IsTrue(secondPlan.RequiresProjection(changedMetadata));
            Assert.AreEqual(1, result.WrittenCount);
            Assert.AreEqual(0, result.SkippedWriteCount);
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ExportTableDataSet_DoesNotReportProgressForFastSkippedManagedPlaylists()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject firstTableData = CreateLocalTableData("bemusicseeker://playlist/1", "First");
            JObject secondTableData = CreateLocalTableData("bemusicseeker://playlist/2", "Second");
            BmtTableExportService.PlaylistExportMetadata firstMetadata = CreateExportMetadata("1", firstTableData, "header-a", "data-a", 100L);
            BmtTableExportService.PlaylistExportMetadata secondMetadata = CreateExportMetadata("2", secondTableData, "header-b", "data-b", 200L);
            BmtTableExportService.ExportPlan firstPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [firstMetadata, secondMetadata], cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", firstTableData),
                Tuple.Create("2", secondTableData)
            ], firstPlan, null);
            var progress = new List<Tuple<int, int, string>>();

            BmtTableExportService.ExportPlan secondPlan = BmtTableExportService.CreateExportPlan(tempDirectory, [firstMetadata, secondMetadata], cleanupStaleManagedFiles: true);
            BmtTableExportService.ExportResult result = BmtTableExportService.ExportTableDataSet(tempDirectory, Array.Empty<Tuple<string, JObject>>(), secondPlan, delegate (int completed, int total, string tableName)
            {
                progress.Add(Tuple.Create(completed, total, tableName));
            });

            Assert.AreEqual(0, result.WrittenCount);
            Assert.AreEqual(2, result.SkippedWriteCount);
            Assert.AreEqual(0, progress.Count);
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
    public void ExportTableData_WritesManagedFilesUnderLongPath()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string tablePath = BuildLongDirectoryPath(tempDirectory, "bmt");
            JObject tableData = CreateLocalTableData("bemusicseeker://playlist/long-path", "Long Path");

            string fileName = BmtTableExportService.ExportTableData(tablePath, tableData, "long-path");

            string bmtPath = Path.Combine(tablePath, fileName);
            Assert.IsTrue(LongPathFileSystem.FileExists(bmtPath));
            Assert.IsTrue(LongPathFileSystem.FileExists(Path.Combine(tablePath, BmtTableExportService.ManifestFileName)));
            JObject json = ReadBmtJson(bmtPath);
            Assert.AreEqual("bemusicseeker://playlist/long-path", json.Value<string>("url"));
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
    public void RemoveManagedPlaylist_KeepsSharedManagedFileWhenAnotherPlaylistStillReferencesIt()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            JObject tableData = CreateLocalTableData("https://example.com/shared-table", "Shared");
            BmtTableExportService.ExportTableDataSet(tempDirectory,
            [
                Tuple.Create("1", tableData),
                Tuple.Create("2", tableData)
            ], cleanupStaleManagedFiles: true);
            string fileName = BMSTable.ComputeSha256Hex(tableData.Value<string>("url")) + ".bmt";

            BmtTableExportService.ExportResult result = BmtTableExportService.RemoveManagedPlaylist(tempDirectory, "1");

            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, fileName)));
            Assert.AreEqual(0, result.RemovedCount);
            Assert.AreEqual("2", result.CurrentManagedTables.Single().PlaylistIdentity);
            Assert.AreEqual("https://example.com/shared-table", result.CurrentManagedTables.Single().Url);
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

    [TestMethod]
    [TestCategory("Playlist")]
    public void BeatorajaConfigService_ReadTableUrls_PreservesConfiguredOrder()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string root = CreateBeatorajaRoot(tempDirectory);
            string configPath = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(
                configPath,
                "{\"tablepath\":\"table\",\"playerpath\":\"player\",\"tableURL\":[\"https://example.com/a\",\"\",123,\"https://example.com/b\",\"https://example.com/a\"]}",
                Encoding.UTF8);

            IReadOnlyList<string> urls = BeatorajaConfigService.ReadTableUrls(root);

            CollectionAssert.AreEqual(new[] { "https://example.com/a", "https://example.com/b", "https://example.com/a" }, urls.ToArray());
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BeatorajaBmtTableImportService_LoadCachedTable_RestoresTableWithConfigRawUrl()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string root = CreateBeatorajaRoot(tempDirectory);
            string tablePath = BeatorajaConfigService.GetTablePath(root);
            string configTableUrl = "https://EXAMPLE.com/table/%7Efull.html";
            var configTableUri = new Uri(configTableUrl, UriKind.Absolute);
            var tableData = new JObject
            {
                ["url"] = "https://example.com/old.html",
                ["name"] = "Cached Table",
                ["tag"] = "st",
                ["folder"] = new JArray(new JObject
                {
                    ["name"] = "st0",
                    ["songs"] = new JArray(new JObject
                    {
                        ["title"] = "Song",
                        ["artist"] = "Artist",
                        ["md5"] = Md5A,
                        ["sha256"] = Sha256A,
                        ["url"] = "https://example.com/song.zip",
                        ["appendurl"] = "https://example.com/song-diff.zip",
                        ["org_md5"] = new JArray(Md5B)
                    })
                }),
                ["course"] = new JArray(new JObject
                {
                    ["name"] = "Course",
                    ["hash"] = new JArray(new JObject
                    {
                        ["title"] = "Course Song",
                        ["md5"] = Md5A
                    })
                })
            };
            string cachePath = BeatorajaBmtTableImportService.GetCachedTablePath(tablePath, configTableUrl);
            WriteBmtJson(cachePath, tableData);

            BMSTable table = BeatorajaBmtTableImportService.LoadCachedTable(tablePath, configTableUrl);

            Assert.AreEqual(configTableUri, table.Page_url);
            Assert.AreEqual(configTableUrl, table.Page_url.OriginalString);
            Assert.AreEqual(configTableUrl, table.page_url);
            Assert.AreEqual(configTableUri, table.Header_url);
            Assert.AreEqual(configTableUrl, table.header_url);
            Assert.IsTrue(table.EnableExternalSync());
            Assert.AreEqual("Cached Table", table.name);
            Assert.AreEqual("st", table.symbol);
            Assert.AreEqual("st", table.tag);
            Assert.AreEqual("st", table.compat_prefix);
            CollectionAssert.AreEqual(new[] { "st0" }, table.Folder_order.ToArray());
            Assert.AreEqual(1, table.entries.Count);
            BMSTableEntry entry = table.entries[0];
            Assert.AreEqual("st0", entry.folder);
            Assert.AreEqual("Song", entry.title);
            Assert.AreEqual("Artist", entry.artist);
            Assert.AreEqual(Md5A, entry.md5);
            Assert.AreEqual(Sha256A, entry.sha256);
            Assert.AreEqual("https://example.com/song.zip", entry.url);
            Assert.AreEqual("https://example.com/song-diff.zip", entry.url_diff);
            CollectionAssert.AreEqual(new[] { Md5B }, entry.Org_md5.ToArray());
            Assert.AreEqual(1, table.Courses.Count);
            StringAssert.Contains(table.Courses[0].course_json, "\"Course\"");

            table.playlist_id = 99;
            JObject exported = BmtTableExportService.BuildTableData(table);
            Assert.AreEqual(configTableUrl, exported.Value<string>("url"));
            Assert.AreEqual(BMSTable.ComputeSha256Hex(configTableUrl) + ".bmt", BmtTableExportService.CreatePlaylistExportMetadata(table).FileName);
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildTableData_UsesPersistedRawPageUrl()
    {
        string rawPageUrl = "https://EXAMPLE.com/table/%7Efull.html";
        var table = new BMSTable
        {
            name = "Changed",
            playlist_id = 100,
            Page_url = new Uri(rawPageUrl, UriKind.Absolute),
            Folder_order = ["Alpha"],
            entries =
            [
                new BMSTableEntry(JObject.Parse("{\"title\":\"Song\",\"md5\":\"" + Md5A + "\",\"level\":\"Alpha\"}"))
            ]
        };

        JObject exported = BmtTableExportService.BuildTableData(table);

        Assert.AreEqual(rawPageUrl, exported.Value<string>("url"));
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BuildBeatorajaTableUrlImportTargets_DeduplicatesByAbsoluteUriKeepingFirstRawUrl()
    {
        object targets = typeof(PlaylistWorkspaceViewModel)
            .GetMethod("BuildBeatorajaTableUrlImportTargets", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new[] { "https://EXAMPLE.com/table/%7Efull.html", "https://example.com/table/~full.html", "https://example.com/other.html" }])!;
        var targetList = ((System.Collections.IEnumerable)targets).Cast<object>().ToArray();

        Assert.AreEqual(2, targetList.Length);
        Assert.AreEqual("https://EXAMPLE.com/table/%7Efull.html", GetPrivateProperty<string>(targetList[0], "RawUrl"));
        Assert.AreEqual("https://example.com/other.html", GetPrivateProperty<string>(targetList[1], "RawUrl"));
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void HasSamePersistedTableUrl_RequiresExactConfigRawUrl()
    {
        string rawPageUrl = "https://EXAMPLE.com/table/%7Efull.html";
        var table = new BMSTable
        {
            Page_url = new Uri(rawPageUrl, UriKind.Absolute)
        };
        MethodInfo method = typeof(PlaylistWorkspaceViewModel)
            .GetMethod("HasSamePersistedTableUrl", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.IsTrue((bool)method.Invoke(null, [table, rawPageUrl])!);
        Assert.IsFalse((bool)method.Invoke(null, [table, "https://example.com/table/~full.html"])!);
    }

    private static JObject ReadBmtJson(string path)
    {
        using FileStream fileStream = LongPathFileSystem.OpenRead(path);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream, Encoding.UTF8);
        return JObject.Parse(reader.ReadToEnd());
    }

    private static void WriteBmtJson(string path, JObject json)
    {
        using FileStream fileStream = LongPathFileSystem.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Compress);
        using var writer = new StreamWriter(gzipStream, new UTF8Encoding(false));
        writer.Write(json.ToString());
    }

    private static T GetPrivateProperty<T>(object target, string propertyName)
    {
        return (T)target.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(target)!;
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

    private static BmtTableExportService.PlaylistExportMetadata CreateExportMetadata(string playlistIdentity, JObject tableData, string headerSha256, string dataSha256, long lastUpdateTicks)
    {
        string url = tableData.Value<string>("url") ?? string.Empty;
        return new BmtTableExportService.PlaylistExportMetadata
        {
            PlaylistIdentity = playlistIdentity,
            Url = url,
            FileName = BMSTable.ComputeSha256Hex(url) + ".bmt",
            Name = tableData.Value<string>("name") ?? string.Empty,
            HeaderSha256 = headerSha256,
            DataSha256 = dataSha256,
            LastUpdateTicks = lastUpdateTicks,
            ProjectionInputSha256 = "projection-" + playlistIdentity
        };
    }

    private static BmtTableExportService.PlaylistExportMetadata CreateExportMetadata(string playlistIdentity, string url, string name)
    {
        return new BmtTableExportService.PlaylistExportMetadata
        {
            PlaylistIdentity = playlistIdentity,
            Url = url,
            FileName = BMSTable.ComputeSha256Hex(url) + ".bmt",
            Name = name,
            HeaderSha256 = "header-" + playlistIdentity,
            DataSha256 = "data-" + playlistIdentity,
            LastUpdateTicks = 1,
            ProjectionInputSha256 = "projection-" + playlistIdentity
        };
    }

    private static JObject CreateSimpleTableData(string url, string name)
    {
        return new JObject
        {
            ["url"] = url,
            ["name"] = name,
            ["tag"] = "t",
            ["folder"] = new JArray
            {
                new JObject
                {
                    ["name"] = "t1",
                    ["songs"] = new JArray
                    {
                        new JObject
                        {
                            ["title"] = "Song",
                            ["md5"] = Md5A
                        }
                    }
                }
            },
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
        LongPathFileSystem.CreateDirectory(tempDirectory);
        try
        {
            testAction(tempDirectory);
        }
        finally
        {
            if (LongPathFileSystem.DirectoryExists(tempDirectory))
            {
                LongPathFileSystem.DeleteDirectory(tempDirectory, recursive: true);
            }
        }
    }

    private static string BuildLongDirectoryPath(string root, string leaf)
    {
        string path = root;
        while (Path.Combine(path, leaf).Length <= 270)
        {
            path = Path.Combine(path, "segment-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        }
        return Path.Combine(path, leaf);
    }

    private sealed class TestSongHashResolver(Dictionary<string, Tuple<string, string>> hashesByTitle)
        : BmtTableExportService.ISongHashResolver
    {
        public BmtTableExportService.SongHashResolution Resolve(BmtSongHashResolveRequest request)
        {
            if (request == null || !hashesByTitle.TryGetValue(request.Title, out Tuple<string, string>? hashes))
            {
                return new BmtTableExportService.SongHashResolution(null, null);
            }

            Tuple<string, string> resolvedHashes = hashes!;
            return new BmtTableExportService.SongHashResolution(resolvedHashes.Item1, resolvedHashes.Item2);
        }
    }
}
