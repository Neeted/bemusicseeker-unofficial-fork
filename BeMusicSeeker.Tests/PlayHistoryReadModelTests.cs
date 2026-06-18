using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlayHistoryReadModelTests
{
    [TestMethod]
    public void Lr2Reader_ReadsFinalizedRowsByRangeDescendingAndExcludesUnfinalized()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 4, hash: HashA, playedAt: 999, finalized: true, newExscore: 180);
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                InsertHistory(db, historyId: 2, hash: HashB, playedAt: 2000, finalized: true, newExscore: 250);
                InsertHistory(db, historyId: 3, hash: HashC, playedAt: 2500, finalized: false, newExscore: 300);
                InsertHistory(db, historyId: 5, hash: HashB, playedAt: 3000, finalized: true, newExscore: 400);
            }

            var reader = new Lr2PlayHistoryReader();
            Lr2PlayHistoryReadResult result = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                PlayedAtFromInclusive = 1000,
                PlayedAtToExclusive = 3000
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.SchemaStatus);
            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(2, result.Rows.Count);
            Assert.AreEqual(2L, result.Rows[0].history_id);
            Assert.AreEqual(1L, result.Rows[1].history_id);

            Lr2PlayHistoryReadResult withUnfinalized = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                IncludeUnfinalized = true,
                PlayedAtFromInclusive = 1000,
                PlayedAtToExclusive = 3000,
                Limit = 2
            });

            Assert.AreEqual(2, withUnfinalized.Rows.Count);
            Assert.AreEqual(3L, withUnfinalized.Rows[0].history_id);
            Assert.AreEqual(2L, withUnfinalized.Rows[1].history_id);
        });
    }

    [TestMethod]
    public void Lr2Reader_NotInstalledReturnsDiagnosticWithoutCreatingObjects()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, result.SchemaStatus);
            Assert.IsTrue(result.HasErrors);
            AssertSingleDiagnostic(result, PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_schema_not_installed", scoreDbPath);
            using var verify = new SQLiteConnection(scoreDbPath);
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM sqlite_master WHERE name LIKE 'bms_lr2_%';"));
        });
    }

    [TestMethod]
    public void Lr2Reader_IndexRepairRequiredReturnsDiagnosticWithoutReading()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP INDEX " + Lr2PlayHistorySchemaService.TimeIndexName + ";");
            }

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsTrue(result.HasErrors);
            Assert.AreEqual(0, result.Rows.Count);
            AssertSingleDiagnostic(result, PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_schema_index_repair_required", scoreDbPath);
        });
    }

    [TestMethod]
    public void Lr2Reader_MapsSchemaStatusesToDiagnostics()
    {
        var reader = new Lr2PlayHistoryReader();

        Lr2PlayHistoryReadResult skipped = reader.Read(new Lr2PlayHistoryReadRequest
        {
            ScoreDbPath = "missing.db",
            IsLr2LinkedProfile = false
        });

        Assert.AreEqual(Lr2PlayHistorySchemaStatus.SkippedProfile, skipped.SchemaStatus);
        Assert.IsFalse(skipped.HasErrors);
        Assert.AreEqual(0, skipped.Rows.Count);
        AssertSingleDiagnostic(skipped, PlayHistoryDiagnosticSeverity.Info, "play_history_lr2_skipped_profile", "missing.db");

        Lr2PlayHistoryReadResult unreadable = reader.Read(new Lr2PlayHistoryReadRequest
        {
            ScoreDbPath = null,
            IsLr2LinkedProfile = true
        });

        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, unreadable.SchemaStatus);
        Assert.IsTrue(unreadable.HasErrors);
        Assert.AreEqual(0, unreadable.Rows.Count);
        AssertSingleDiagnostic(unreadable, PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_schema_unreadable", string.Empty);

        WithScoreDb(delegate (string scoreDbPath)
        {
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("CREATE TABLE unrelated (id INTEGER);");
            }

            Lr2PlayHistoryReadResult manualRepair = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.ManualRepairRequired, manualRepair.SchemaStatus);
            Assert.IsTrue(manualRepair.HasErrors);
            Assert.AreEqual(0, manualRepair.Rows.Count);
            AssertSingleDiagnostic(manualRepair, PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_schema_manual_repair_required", scoreDbPath);
        });

        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
            }

            Lr2PlayHistoryReadResult repairableTrigger = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, repairableTrigger.SchemaStatus);
            Assert.IsFalse(repairableTrigger.HasErrors);
            Assert.AreEqual(1, repairableTrigger.Rows.Count);
            AssertSingleDiagnostic(repairableTrigger, PlayHistoryDiagnosticSeverity.Warning, "play_history_lr2_schema_repairable", scoreDbPath);
        });
    }

    [TestMethod]
    public void Lr2Reader_DefaultAndNonPositiveLimitUseDefaultLimit()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.BeginTransaction();
                try
                {
                    for (int index = 1; index <= Lr2PlayHistoryReader.DefaultReadLimit + 1; index++)
                    {
                        InsertHistory(db, historyId: index, hash: HashA, playedAt: index, finalized: true, newExscore: 200);
                    }
                    db.Commit();
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            }

            var reader = new Lr2PlayHistoryReader();
            Lr2PlayHistoryReadResult defaultResult = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });
            Lr2PlayHistoryReadResult zeroResult = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                Limit = 0
            });
            Lr2PlayHistoryReadResult negativeResult = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                Limit = -1
            });

            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit, defaultResult.Rows.Count);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1L, defaultResult.Rows[0].history_id);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit, zeroResult.Rows.Count);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1L, zeroResult.Rows[0].history_id);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit, negativeResult.Rows.Count);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1L, negativeResult.Rows[0].history_id);
        });
    }

    [TestMethod]
    public void ProjectLr2Rows_ResolvesChartAndSeparatesBestDeltaFromActualResult()
    {
        var readResult = new Lr2PlayHistoryReadResult(
            PlayHistorySourceProfile.Lr2("score.db"),
            [
                CreateRawRecord(
                    historyId: 10,
                    hash: HashA,
                    playedAt: 1000,
                    finalized: true,
                    oldExscore: 200,
                    newExscore: 250,
                    newTotalNotes: 150,
                    oldClear: 0,
                    newClear: 3,
                    oldMinBp: null,
                    newMinBp: 20,
                    oldMaxCombo: 80,
                    newMaxCombo: 120,
                    perfectDelta: 100,
                    greatDelta: 40,
                    goodDelta: 10,
                    badDelta: 5,
                    poorDelta: 2,
                    playtimeDelta: 90,
                    judgeDelta: 157,
                    newOpBest: 23,
                    oldOpHistory: 1,
                    newOpHistory: 17)
            ],
            [],
            Lr2PlayHistorySchemaStatus.Installed);
        PlayHistoryProjectionIndex projectionIndex = CreateProjectionIndex();

        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(readResult, projectionIndex);

        Assert.AreEqual(1, projected.Rows.Count);
        PlayHistoryRow row = projected.Rows[0];
        Assert.AreEqual(PlayHistoryProvider.Lr2, row.Provider);
        Assert.AreEqual(PlayHistoryHashKind.Chart, row.HashKind);
        Assert.AreEqual("Resolved Title", row.Title);
        Assert.AreEqual("Resolved Artist", row.Artist);
        Assert.AreEqual(ShaA, row.Sha256);
        Assert.AreEqual("SAT", row.FolderLabels);
        Assert.AreEqual("NO PLAY -> CLEAR", row.BestClear);
        Assert.AreEqual("200 -> 250", row.BestExscore);
        Assert.AreEqual("BP 20", row.BestBp);
        Assert.AreEqual("80 -> 120", row.BestCombo);
        Assert.AreEqual(RankType.AA, row.BestDjLevel);
        Assert.IsTrue(row.BestRate.HasValue);
        Assert.AreEqual(250 / 300.0, row.BestRate.Value, 0.0001);
        Assert.AreEqual(240, row.PlayExscore);
        Assert.AreEqual(250, row.NewBestExscore);
        Assert.AreEqual(90, row.PlaytimeSeconds);
        Assert.AreEqual(157, row.JudgeTotal);
        Assert.AreEqual("PG 100 / GR 40 / GD 10 / BD 5 / PR 2", row.Judges);
        Assert.AreEqual("EASY RANDOM", row.Option);
        Assert.AreEqual(16, row.OpHistoryNewBits);
        Assert.AreEqual("0x00000010", row.OpHistory);
        Assert.AreEqual("score", row.Kind);
    }

    [TestMethod]
    public void ProjectLr2Rows_UnresolvedUnfinalizedRowRemainsVisible()
    {
        var readResult = new Lr2PlayHistoryReadResult(
            PlayHistorySourceProfile.Lr2("score.db"),
            [CreateRawRecord(11, HashC, 1000, finalized: false, oldExscore: null, newExscore: 100, newTotalNotes: 100)],
            [],
            Lr2PlayHistorySchemaStatus.Installed);

        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(readResult, PlayHistoryProjectionIndex.Empty);

        Assert.AreEqual(1, projected.Rows.Count);
        PlayHistoryRow row = projected.Rows[0];
        Assert.AreEqual(HashC, row.RawHash);
        Assert.AreEqual(PlayHistoryHashKind.Unknown, row.HashKind);
        Assert.AreEqual(string.Empty, row.Title);
        Assert.IsFalse(row.Finalized);
        Assert.IsNull(row.PlayExscore);
    }

    [TestMethod]
    public void ProjectLr2Rows_BestScoreColumnsAreBlankWhenBestScoreDidNotChange()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        12,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 250,
                        newExscore: 250,
                        newTotalNotes: 150,
                        oldClear: 3,
                        newClear: 3,
                        oldMinBp: 20,
                        newMinBp: 20,
                        oldMaxCombo: 120,
                        newMaxCombo: 120,
                        perfectDelta: 90,
                        greatDelta: 30,
                        judgeDelta: 150)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        Assert.IsFalse(row.BestScoreUpdated);
        Assert.AreEqual(string.Empty, row.BestExscore);
        Assert.AreEqual(RankType.INVALID, row.BestDjLevel);
        Assert.IsNull(row.BestRate);
        Assert.AreEqual(string.Empty, row.BestClear);
        Assert.AreEqual(string.Empty, row.BestBp);
        Assert.AreEqual(string.Empty, row.BestCombo);
        Assert.AreEqual("play", row.Kind);
        Assert.AreEqual(210, row.PlayExscore);
    }

    [TestMethod]
    public void ProjectLr2Rows_OpHistoryPerfectBitPromotesFullComboToPerfect()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        13,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 250,
                        newExscore: 300,
                        newTotalNotes: 150,
                        oldClear: 5,
                        newClear: 5,
                        oldOpHistory: 0,
                        newOpHistory: 0x10)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        Assert.AreEqual(ClearType.FC, row.OldBestClear);
        Assert.AreEqual(ClearType.PA, row.NewBestClear);
        Assert.IsTrue(row.BestClearUpdated);
        Assert.AreEqual("FULL COMBO -> PERFECT", row.BestClear);

        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("all", projected.Rows);
        Assert.AreEqual(1, summary.NewPerfectCount);
    }

    [TestMethod]
    public void SummaryCountsOnlyFinalizedRowsForPlaytimeAndJudges()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(1, HashA, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100, oldClear: 0, newClear: 5, perfectDelta: 40, greatDelta: 20, playtimeDelta: 60, judgeDelta: 80),
                    CreateRawRecord(2, HashB, 2000, finalized: false, oldExscore: 100, newExscore: 140, newTotalNotes: 100, oldClear: 5, newClear: 5, playtimeDelta: 500, judgeDelta: 500)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("today", projected.Rows);

        Assert.AreEqual("today", summary.Label);
        Assert.AreEqual(2, summary.RowCount);
        Assert.AreEqual(1, summary.FinalizedCount);
        Assert.AreEqual(1, summary.UnfinalizedCount);
        Assert.AreEqual(1, summary.SummaryEligibleCount);
        Assert.AreEqual(60, summary.PlaytimeSeconds);
        Assert.AreEqual(80, summary.JudgeCount);
        Assert.AreEqual(1, summary.ScoreUpdateCount);
        Assert.AreEqual(1, summary.NewClearCount);
    }

    [TestMethod]
    public void SummaryDoesNotCountFailedAsNewClear()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [CreateRawRecord(30, HashA, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100, oldClear: 0, newClear: 1)],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("failed", projected.Rows);

        Assert.AreEqual(1, summary.ClearUpdateCount);
        Assert.AreEqual(0, summary.NewClearCount);
        Assert.AreEqual(0, summary.NewFullComboCount);
        Assert.AreEqual(0, summary.NewPerfectCount);
    }

    [TestMethod]
    public void SummaryExcludesFinalizedUnknownHashFromAggregates()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [CreateRawRecord(20, HashC, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100, oldClear: 0, newClear: 3, perfectDelta: 40, greatDelta: 20, playtimeDelta: 60, judgeDelta: 80)],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);

        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("diagnostic", projected.Rows);

        Assert.AreEqual(1, summary.RowCount);
        Assert.AreEqual(1, summary.FinalizedCount);
        Assert.AreEqual(0, summary.SummaryEligibleCount);
        Assert.AreEqual(0, summary.PlaytimeSeconds);
        Assert.AreEqual(0, summary.JudgeCount);
        Assert.AreEqual(0, summary.ScoreUpdateCount);
    }

    [TestMethod]
    public void SortEngine_DefaultsDateDescendingAndRejectsUnknownColumn()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(1, HashA, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100),
                    CreateRawRecord(2, HashB, 2000, finalized: true, oldExscore: 100, newExscore: 140, newTotalNotes: 100),
                    CreateRawRecord(3, HashC, 2000, finalized: true, oldExscore: 100, newExscore: 160, newTotalNotes: 100)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);

        bool sorted = PlayHistorySortEngine.TrySort(projected.Rows, null, out List<PlayHistoryRow> rows, out string sortProfile);

        Assert.IsTrue(sorted);
        Assert.AreEqual(3L, rows[0].HistoryId);
        Assert.AreEqual(2L, rows[1].HistoryId);
        Assert.AreEqual(1L, rows[2].HistoryId);
        StringAssert.Contains(sortProfile, nameof(PlayHistoryRow.PlayedAt));

        bool unknown = PlayHistorySortEngine.TrySort(
            projected.Rows,
            new MainWindowViewModel.cSortParameters { ColumnsName = "LibraryChartRowOnlyColumn", Direction = ListSortDirection.Ascending },
            out _,
            out string unknownProfile);

        Assert.IsFalse(unknown);
        Assert.AreEqual("play_history_unknown_column", unknownProfile);
    }

    [TestMethod]
    public void VirtualViewExposesReadOnlyRows()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [CreateRawRecord(1, HashA, 1000, finalized: true, oldExscore: null, newExscore: 120, newTotalNotes: 100)],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);
        var view = new PlayHistoryVirtualView(projected.Rows, distinctFolderCount: 3);

        Assert.AreEqual(1, view.Count);
        Assert.AreSame(projected.Rows[0], view[0]);
        Assert.IsTrue(view.IsReadOnly);
        Assert.IsTrue(view.IsFixedSize);
        Assert.IsTrue(view.Contains(projected.Rows[0]));
        Assert.AreEqual(0, view.IndexOf(projected.Rows[0]));
        Assert.AreEqual(3, view.DistinctFolderCount);
        Assert.ThrowsException<NotSupportedException>(() => view.Add(projected.Rows[0]));
        Assert.ThrowsException<NotSupportedException>(() => view[0] = projected.Rows[0]);
        Assert.ThrowsException<NotSupportedException>(() => view.Clear());
        Assert.ThrowsException<NotSupportedException>(() => view.Insert(0, projected.Rows[0]));
        Assert.ThrowsException<NotSupportedException>(() => view.Remove(projected.Rows[0]));
        Assert.ThrowsException<NotSupportedException>(() => view.RemoveAt(0));
    }

    private static PlayHistoryProjectionIndex CreateProjectionIndex()
    {
        BMSFile file = BMSFile.FromSongTableRawValues(
        [
            HashA,
            "Resolved Title",
            "",
            "Resolved Artist",
            "",
            "",
            "",
            "C:\\BMS\\resolved.bms",
            "",
            "Folder",
            "",
            "",
            "",
            "",
            "12",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            ""
        ]);
        file.ApplySnapshotDigest(HashA, ShaA);
        PlaylistLibraryResolveIndexSnapshot resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs([LibraryChartRef.FromBmsFile(file)]);
        var table = new BMSTable
        {
            name = "Satellite",
            symbol = "SAT"
        };
        return PlayHistoryProjectionIndex.Create(
            resolveIndex,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [HashA] = ShaA },
            (md5, sha256) => new PlaylistReferenceDisplay([table]),
            (sha256, md5) => null);
    }

    private static void WithScoreDb(Action<string> action)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayHistoryReadModel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            action(Path.Combine(directoryPath, "score.db"));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private static void CreateInstalledScoreDb(string scoreDbPath)
    {
        CreateBaseScoreDb(scoreDbPath);
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.Status);
    }

    private static void CreateBaseScoreDb(string scoreDbPath)
    {
        using var db = new SQLiteConnection(scoreDbPath);
        db.CreateTable<LR2ScoreDB.score>();
        db.CreateTable<LR2ScoreDB.player>();
    }

    private static void AssertSingleDiagnostic(
        Lr2PlayHistoryReadResult result,
        PlayHistoryDiagnosticSeverity severity,
        string code,
        string sourcePath)
    {
        PlayHistoryDiagnostic diagnostic = result.Diagnostics.Single();
        Assert.AreEqual(PlayHistoryProvider.Lr2, diagnostic.Provider);
        Assert.AreEqual("read", diagnostic.Stage);
        Assert.AreEqual(severity, diagnostic.Severity);
        Assert.AreEqual(code, diagnostic.Code);
        Assert.AreEqual(sourcePath ?? string.Empty, diagnostic.SourcePath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostic.Message));
    }

    private static void InsertHistory(SQLiteConnection db, long historyId, string hash, long playedAt, bool finalized, int? newExscore)
    {
        db.Execute(
            "INSERT INTO bms_lr2_play_history (history_id, hash, played_at, finalized, score_write_type, new_playcount, playcount_delta, new_exscore, new_totalnotes) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);",
            historyId, hash, playedAt, finalized ? 1 : 0, "update", 1, 1, newExscore, 100);
    }

    private static Lr2PlayHistoryRecord CreateRawRecord(
        long historyId,
        string hash,
        long playedAt,
        bool finalized,
        int? oldExscore,
        int? newExscore,
        int? newTotalNotes,
        int? oldClear = null,
        int? newClear = null,
        int? oldMinBp = null,
        int? newMinBp = null,
        int? oldMaxCombo = null,
        int? newMaxCombo = null,
        int? perfectDelta = null,
        int? greatDelta = null,
        int? goodDelta = null,
        int? badDelta = null,
        int? poorDelta = null,
        int? playtimeDelta = null,
        int? judgeDelta = null,
        int? newOpBest = null,
        int? oldOpHistory = null,
        int? newOpHistory = null)
    {
        return new Lr2PlayHistoryRecord
        {
            history_id = historyId,
            hash = hash,
            played_at = playedAt,
            finalized = finalized ? 1 : 0,
            score_write_type = "update",
            old_playcount = historyId == 1 ? null : 1,
            new_playcount = historyId == 1 ? 1 : 2,
            playcount_delta = 1,
            old_clear = oldClear,
            new_clear = newClear,
            old_minbp = oldMinBp,
            new_minbp = newMinBp,
            old_exscore = oldExscore,
            new_exscore = newExscore,
            old_maxcombo = oldMaxCombo,
            new_maxcombo = newMaxCombo,
            new_totalnotes = newTotalNotes,
            perfect_delta = perfectDelta,
            great_delta = greatDelta,
            good_delta = goodDelta,
            bad_delta = badDelta,
            poor_delta = poorDelta,
            playtime_delta = playtimeDelta,
            judge_delta = judgeDelta,
            new_op_best = newOpBest,
            old_op_history = oldOpHistory,
            new_op_history = newOpHistory
        };
    }

    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string HashC = "cccccccccccccccccccccccccccccccc";

    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
}
