using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class BmsLibraryInitializationFileScanTestSupport
{
    /// <summary>日常と大規模条件で同じ走査chunkの確定件数と保存結果を表明します。</summary>
    internal static void AssertFileScanChunkCommits(ApplicationDatabaseScope databaseScope, int count, int chunkSize, BmsLibraryInitializationService service)
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "ManyAdded");
            Directory.CreateDirectory(chartDirectoryPath);
            List<string> paths = [];
            for (int i = 0; i < count; i++)
            {
                string path = Path.Combine(chartDirectoryPath, "added-" + i.ToString("D4") + ".bms");
                File.WriteAllText(path, CreateValidBmsText("Added " + i.ToString("D4")), Encoding.ASCII);
                paths.Add(path);
            }

            using (LR2SongDBExtended songDbConnection = databaseScope.OpenSongWrite(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            List<string> events = [];
            object eventLock = new();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath),
                new EverythingNative(ApplicationPathPolicy.Current),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        paths,
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: delegate (string message)
                {
                    lock (eventLock)
                    {
                        if (message.Contains("song_tbl_file_check db_commit_chunk_done"))
                        {
                            events.Add(message);
                        }
                    }
                },
                reportParseProgress: delegate (int total, int processed, string path)
                {
                    lock (eventLock)
                    {
                        events.Add("progress " + processed + "/" + total);
                    }
                });

            Assert.AreEqual(count, result.AddedFiles.Count);
            Assert.AreEqual(count, result.DbCommitBmsChangedCount);
            Assert.AreEqual((int)Math.Ceiling(paths.Count / (double)chunkSize), result.DbCommitChunks);
            Assert.AreEqual(count, result.PostParseWorkItemCount);
            Assert.IsTrue(result.PostParseWallMs >= 0);
            Assert.IsTrue(result.CommitQueueWaitMs >= 0);
            int firstCommitIndex = events.FindIndex(item => item.Contains("db_commit_chunk_done chunk=1"));
            int finalProgressIndex = events.FindIndex(item => item == "progress " + count + "/" + count);
            int firstCommitChunkProgressIndex = events.FindIndex(item => item == "progress " + chunkSize + "/" + count);
            List<string> progressEvents = [.. events.Where(item => item.StartsWith("progress ", StringComparison.Ordinal))];
            Assert.IsTrue(firstCommitIndex >= 0, "first commit chunk log was not recorded.");
            Assert.AreEqual(count + 1, progressEvents.Count, "file diff progress should be reported for the initial state and each prepared chart.");
            Assert.IsTrue(progressEvents.Contains("progress 1/" + count), "first prepared chart progress was not recorded.");
            Assert.IsTrue(progressEvents.Contains("progress " + (count - 1) + "/" + count), "near-final per-chart progress was not recorded.");
            Assert.IsTrue(finalProgressIndex >= 0, "final parse progress was not recorded.");
            Assert.IsTrue(firstCommitChunkProgressIndex >= 0, "first commit chunk prepared progress was not recorded.");
            Assert.IsTrue(firstCommitChunkProgressIndex < firstCommitIndex, "file diff progress should report DB-ready rows before their commit completion.");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual((long)count, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual((long)count, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual((long)count, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance;"));
            LR2SongDB.song[] rows = [.. verify.Query<LR2SongDB.song>("SELECT * FROM song;")];
            CollectionAssert.AreEquivalent(paths.ToArray(), rows.Select(row => row.path).ToArray());
            foreach (LR2SongDB.song row in rows)
            {
                Assert.AreEqual("Added " + Path.GetFileNameWithoutExtension(row.path).Substring("added-".Length), row.title);
            }
        });
    }

}
