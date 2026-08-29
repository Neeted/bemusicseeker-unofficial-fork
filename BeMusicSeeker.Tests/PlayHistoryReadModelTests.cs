using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
// This fixture uses the shared WPF dispatcher host and process-wide settings-backed test ports.
[DoNotParallelize]
public sealed class PlayHistoryReadModelTests
{
    [TestMethod]
    public void Lr2PlayHistorySchemaStatusSnapshot_DetachesMutableServiceResult()
    {
        var result = new Lr2PlayHistorySchemaCheckResult
        {
            Status = Lr2PlayHistorySchemaStatus.Installed,
            ScoreDbPath = "score.db",
            Message = "installed"
        };

        Lr2PlayHistorySchemaStatusSnapshot snapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(result);
        result.Status = Lr2PlayHistorySchemaStatus.Repairable;
        result.ScoreDbPath = "other.db";
        result.Message = "changed";

        Assert.IsFalse(snapshot.IsReset);
        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, snapshot.Status);
        Assert.AreEqual("score.db", snapshot.ScoreDbPath);
        Assert.AreEqual("installed", snapshot.Message);
    }

    [TestMethod]
    public void PlayHistoryPeriodRequest_AllHasNoEpochBounds()
    {
        PlayHistoryPeriodRequest request = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.All,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.IsNull(request.PlayedAtFromInclusive);
        Assert.IsNull(request.PlayedAtToExclusive);
        Assert.IsFalse(request.IncludeUnfinalized);
        Assert.AreEqual(Lr2PlayHistoryFinalizationFilter.FinalizedOnly, request.FinalizationFilter);
    }

    [TestMethod]
    public void PlayHistoryPeriodRequest_DayRangesUseLocalMidnightInclusiveAndNextMidnightExclusive()
    {
        AssertPeriodRange(PlayHistoryPeriodKind.Today, UtcEpoch(2026, 6, 19), UtcEpoch(2026, 6, 20));
        AssertPeriodRange(PlayHistoryPeriodKind.Yesterday, UtcEpoch(2026, 6, 18), UtcEpoch(2026, 6, 19));
        AssertPeriodRange(PlayHistoryPeriodKind.Recent7Days, UtcEpoch(2026, 6, 13), UtcEpoch(2026, 6, 20));
        AssertPeriodRange(PlayHistoryPeriodKind.Recent30Days, UtcEpoch(2026, 5, 21), UtcEpoch(2026, 6, 20));
    }

    [TestMethod]
    public void PlayHistoryPeriodRequest_UsesProvidedLocalTimeZoneForDayBoundary()
    {
        TimeZoneInfo utcPlusNine = TimeZoneInfo.CreateCustomTimeZone("UTC+09", TimeSpan.FromHours(9), "UTC+09", "UTC+09");
        PlayHistoryPeriodRequest request = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.Today,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            utcPlusNine);

        Assert.AreEqual(UtcEpoch(2026, 6, 19, 15), request.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 6, 20, 15), request.PlayedAtToExclusive);
    }

    [TestMethod]
    public void PlayHistoryPeriodRequest_DiagnosticsIncludesUnfinalizedWithoutDateBounds()
    {
        PlayHistoryPeriodRequest request = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.Diagnostics,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.IsNull(request.PlayedAtFromInclusive);
        Assert.IsNull(request.PlayedAtToExclusive);
        Assert.IsTrue(request.IncludeUnfinalized);
        Assert.AreEqual(Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly, request.FinalizationFilter);
        Assert.AreEqual(
            Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly,
            request.ToLr2ReadRequest("score.db", isLr2LinkedProfile: true).FinalizationFilter);
        Assert.AreEqual(
            Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly,
            request.ToBeatorajaReadRequest("score.db").FinalizationFilter);
    }

    [TestMethod]
    public void PlayHistoryPeriodRequest_ArchiveRangesUseLocalBoundaries()
    {
        TimeZoneInfo utcPlusNine = TimeZoneInfo.CreateCustomTimeZone("UTC+09", TimeSpan.FromHours(9), "UTC+09", "UTC+09");

        PlayHistoryPeriodRequest year = PlayHistoryPeriodRequest.CreateYear(2026, utcPlusNine);
        PlayHistoryPeriodRequest month = PlayHistoryPeriodRequest.CreateMonth(2026, 12, utcPlusNine);
        PlayHistoryPeriodRequest day = PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, utcPlusNine);

        Assert.AreEqual(PlayHistoryPeriodKind.Year, year.Kind);
        Assert.AreEqual("2026", year.Label);
        Assert.AreEqual(UtcEpoch(2025, 12, 31, 15), year.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 12, 31, 15), year.PlayedAtToExclusive);
        Assert.AreEqual(PlayHistoryPeriodKind.Month, month.Kind);
        Assert.AreEqual("2026/12", month.Label);
        Assert.AreEqual(UtcEpoch(2026, 11, 30, 15), month.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 12, 31, 15), month.PlayedAtToExclusive);
        Assert.AreEqual(PlayHistoryPeriodKind.Day, day.Kind);
        Assert.AreEqual("2026/01/02", day.Label);
        Assert.AreEqual(UtcEpoch(2026, 1, 1, 15), day.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 1, 2, 15), day.PlayedAtToExclusive);
    }

    [TestMethod]
    public void PlayHistoryPeriodTreeItem_BuildArchiveTreeGroupsDescending()
    {
        IReadOnlyList<PlayHistoryPeriodTreeItem> tree = PlayHistoryPeriodTreeItem.BuildArchiveTree(
            [
                UtcEpoch(2025, 12, 31, 23),
                UtcEpoch(2026, 1, 1, 1),
                UtcEpoch(2026, 1, 2, 1),
                UtcEpoch(2026, 1, 2, 23)
            ],
            TimeZoneInfo.Utc);

        CollectionAssert.AreEqual(new[] { "2026", "2025" }, tree.Select(node => node.Label).ToArray());
        PlayHistoryPeriodTreeItem year2026 = tree[0];
        Assert.AreEqual("2026", year2026.ToString());
        Assert.AreEqual(PlayHistoryPeriodKind.Year, year2026.Request.Kind);
        Assert.AreEqual(UtcEpoch(2026, 1, 1), year2026.Request.PlayedAtFromInclusive);
        CollectionAssert.AreEqual(new[] { "2026/01" }, year2026.Children.Select(node => node.Label).ToArray());
        CollectionAssert.AreEqual(new[] { "2026/01/02", "2026/01/01" }, year2026.Children[0].Children.Select(node => node.Label).ToArray());
        Assert.AreEqual(PlayHistoryPeriodKind.Day, year2026.Children[0].Children[0].Request.Kind);
        Assert.AreEqual(UtcEpoch(2026, 1, 2), year2026.Children[0].Children[0].Request.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 1, 3), year2026.Children[0].Children[0].Request.PlayedAtToExclusive);
    }

    [TestMethod]
    public void PlayHistoryPeriodTreeItem_BuildArchiveTreeUsesProvidedTimeZone()
    {
        TimeZoneInfo utcPlusNine = TimeZoneInfo.CreateCustomTimeZone("UTC+09", TimeSpan.FromHours(9), "UTC+09", "UTC+09");
        IReadOnlyList<PlayHistoryPeriodTreeItem> tree = PlayHistoryPeriodTreeItem.BuildArchiveTree(
            [
                UtcEpoch(2026, 1, 1, 14),
                UtcEpoch(2026, 1, 1, 15),
                UtcEpoch(2026, 1, 2, 14)
            ],
            utcPlusNine);

        PlayHistoryPeriodTreeItem january = tree.Single().Children.Single();

        CollectionAssert.AreEqual(new[] { "2026/01/02", "2026/01/01" }, january.Children.Select(node => node.Label).ToArray());
        Assert.AreEqual(UtcEpoch(2026, 1, 1, 15), january.Children[0].Request.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 1, 2, 15), january.Children[0].Request.PlayedAtToExclusive);
    }

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
    public void Lr2Reader_UnfinalizedOnlyFilterReadsOnlyDiagnosticRows()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                InsertHistory(db, historyId: 2, hash: HashB, playedAt: 2000, finalized: false, newExscore: 250);
                InsertHistory(db, historyId: 3, hash: HashC, playedAt: 3000, finalized: false, newExscore: 300);
            }

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                FinalizationFilter = Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly,
                Limit = 10
            });

            CollectionAssert.AreEqual(new long[] { 3L, 2L }, result.Rows.Select(row => row.history_id).ToArray());
            Assert.IsTrue(result.Rows.All(row => row.finalized == 0));
        });
    }

    [TestMethod]
    public void Lr2Reader_RangeBoundariesAreInclusiveFromExclusiveTo()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 999, finalized: true, newExscore: 100);
                InsertHistory(db, historyId: 2, hash: HashA, playedAt: 1000, finalized: true, newExscore: 110);
                InsertHistory(db, historyId: 3, hash: HashA, playedAt: 1999, finalized: true, newExscore: 120);
                InsertHistory(db, historyId: 4, hash: HashA, playedAt: 2000, finalized: true, newExscore: 130);
            }

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                PlayedAtFromInclusive = 1000,
                PlayedAtToExclusive = 2000,
                Limit = 10
            });

            CollectionAssert.AreEqual(new long[] { 3L, 2L }, result.Rows.Select(row => row.history_id).ToArray());
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

    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("mismatched")]
    public void Lr2Reader_StrictHistoryTriggerDefectReturnsErrorWithoutReadingRows(string defect)
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
                if (string.Equals(defect, "mismatched", StringComparison.Ordinal))
                {
                    db.Execute(
                        "CREATE TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName
                        + " AFTER INSERT ON score BEGIN SELECT 1; END;");
                }
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                AllowRepairableIndexRead = true,
                RequireCompleteHistoryTriggers = true,
                DisableLimit = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsTrue(result.HasErrors);
            Assert.AreEqual(0, result.Rows.Count);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual(PlayHistoryDiagnosticSeverity.Error, result.Diagnostics[0].Severity);
            Assert.AreEqual(scoreDbPath, result.Diagnostics[0].SourcePath);
            Assert.IsNotNull(result.SchemaCheckResult);
            Assert.IsTrue(
                result.SchemaCheckResult.MissingTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName)
                || result.SchemaCheckResult.MismatchedTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        });
    }

    [DataTestMethod]
    [DataRow("missing", true, false)]
    [DataRow("mismatched", true, false)]
    [DataRow("missing", false, true)]
    [DataRow("mismatched", false, true)]
    public void Lr2Reader_TriggerRequirementAndIndexRepairPoliciesRemainIndependent(
        string defect,
        bool requireCompleteHistoryTriggers,
        bool allowRepairableIndexRead)
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
                if (string.Equals(defect, "mismatched", StringComparison.Ordinal))
                {
                    db.Execute(
                        "CREATE TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName
                        + " AFTER INSERT ON score BEGIN SELECT 1; END;");
                }
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                AllowRepairableIndexRead = allowRepairableIndexRead,
                RequireCompleteHistoryTriggers = requireCompleteHistoryTriggers,
                DisableLimit = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsNotNull(result.SchemaCheckResult);
            if (string.Equals(defect, "missing", StringComparison.Ordinal))
            {
                Assert.IsTrue(result.SchemaCheckResult.MissingTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
                Assert.AreEqual(0, result.SchemaCheckResult.MismatchedTriggers.Count);
            }
            else
            {
                Assert.AreEqual(0, result.SchemaCheckResult.MissingTriggers.Count);
                Assert.IsTrue(result.SchemaCheckResult.MismatchedTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
            }

            if (requireCompleteHistoryTriggers)
            {
                Assert.IsTrue(result.HasErrors);
                Assert.AreEqual(0, result.Rows.Count);
                AssertSingleDiagnostic(
                    result,
                    PlayHistoryDiagnosticSeverity.Error,
                    "play_history_lr2_schema_trigger_repair_required",
                    scoreDbPath);
            }
            else
            {
                Assert.IsFalse(result.HasErrors);
                Assert.AreEqual(1, result.Rows.Count);
                AssertSingleDiagnostic(
                    result,
                    PlayHistoryDiagnosticSeverity.Warning,
                    "play_history_lr2_schema_repairable",
                    scoreDbPath);
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        });
    }

    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("mismatched")]
    public void Lr2Reader_StrictHistoryTriggerPolicyStopsBeforeMalformedRowQuery(string defect)
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
                if (string.Equals(defect, "mismatched", StringComparison.Ordinal))
                {
                    db.Execute(
                        "CREATE TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName
                        + " AFTER INSERT ON score BEGIN SELECT 1; END;");
                }
                db.Execute(
                    "INSERT INTO bms_lr2_play_history (history_id, hash, played_at, finalized, score_write_type, new_playcount, playcount_delta, new_exscore, new_totalnotes) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);",
                    1,
                    HashA,
                    "not-a-unix-timestamp",
                    1,
                    "update",
                    1,
                    1,
                    200,
                    100);
                // Keep PRAGMA table_info compatible while making SELECT ... ORDER BY fail
                // on the reader's fresh connection if the strict policy is bypassed.
                db.Execute("PRAGMA writable_schema = ON;");
                db.Execute(
                    "UPDATE sqlite_master SET sql = replace(sql, 'played_at INTEGER NOT NULL', 'played_at INTEGER COLLATE missing_history_collation NOT NULL') WHERE type = 'table' AND name = 'bms_lr2_play_history';");
                db.Execute("PRAGMA writable_schema = OFF;");
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                AllowRepairableIndexRead = true,
                RequireCompleteHistoryTriggers = true,
                DisableLimit = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsTrue(result.HasErrors);
            Assert.AreEqual(0, result.Rows.Count);
            AssertSingleDiagnostic(
                result,
                PlayHistoryDiagnosticSeverity.Error,
                "play_history_lr2_schema_trigger_repair_required",
                scoreDbPath);
            Assert.IsNotNull(result.SchemaCheckResult);
            if (string.Equals(defect, "missing", StringComparison.Ordinal))
            {
                Assert.IsTrue(result.SchemaCheckResult.MissingTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
            }
            else
            {
                Assert.IsTrue(result.SchemaCheckResult.MismatchedTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        });
    }

    [TestMethod]
    public void Lr2Reader_StrictHistoryTriggerRequirementAllowsIndexOnlyRepairableReadWhenOptedIn()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP INDEX " + Lr2PlayHistorySchemaService.TimeIndexName + ";");
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                AllowRepairableIndexRead = true,
                RequireCompleteHistoryTriggers = true,
                DisableLimit = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(1L, result.Rows[0].history_id);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual(PlayHistoryDiagnosticSeverity.Warning, result.Diagnostics[0].Severity);
            Assert.IsNotNull(result.SchemaCheckResult);
            Assert.IsTrue(result.SchemaCheckResult.MissingIndexes.Contains(Lr2PlayHistorySchemaService.TimeIndexName));
            Assert.AreEqual(0, result.SchemaCheckResult.MissingTriggers.Count);
            Assert.AreEqual(0, result.SchemaCheckResult.MismatchedTriggers.Count);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
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
    public void Lr2Reader_ReadPeriodIndexReadsFinalizedRowsWithoutDefaultLimit()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.BeginTransaction();
                try
                {
                    long baseEpoch = UtcEpoch(2020, 1, 1);
                    for (int index = 1; index <= Lr2PlayHistoryReader.DefaultReadLimit + 1; index++)
                    {
                        InsertHistory(db, historyId: index, hash: HashA, playedAt: baseEpoch + (index * 86400L), finalized: true, newExscore: 200);
                    }
                    InsertHistory(db, historyId: 99999, hash: HashB, playedAt: 99999, finalized: false, newExscore: 999);
                    db.Commit();
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            }

            Lr2PlayHistoryPeriodIndexResult result = new Lr2PlayHistoryReader().ReadPeriodIndex(
                new Lr2PlayHistoryPeriodIndexRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true
                },
                CancellationToken.None);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.SchemaStatus);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1, result.PlayedAtUnixSeconds.Count);
            Assert.IsTrue(result.PlayedAtUnixSeconds[0] > result.PlayedAtUnixSeconds[result.PlayedAtUnixSeconds.Count - 1]);
        });
    }

    [TestMethod]
    public void PlayHistoryReadCache_Lr2LoadsAllRowsOnceAndFiltersFromMemory()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            long day1 = UtcEpoch(2026, 1, 1);
            long day2 = UtcEpoch(2026, 1, 2);
            long day3 = UtcEpoch(2026, 1, 3);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: day1, finalized: true, newExscore: 100);
                InsertHistory(db, historyId: 2, hash: HashA, playedAt: day2, finalized: true, newExscore: 200);
                InsertHistory(db, historyId: 3, hash: HashB, playedAt: day3, finalized: false, newExscore: 300);
            }

            var cache = new PlayHistoryReadCache();
            Lr2PlayHistoryReadResult first = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    PlayedAtFromInclusive = day1 - 100,
                    PlayedAtToExclusive = day1 + 100,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly
                },
                CancellationToken.None,
                out bool firstCacheHit);

            Assert.IsFalse(firstCacheHit);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, first.SchemaStatus);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, first.SchemaCheckResult.Status);
            CollectionAssert.AreEqual(new long[] { 1L }, first.Rows.Select(row => row.history_id).ToArray());

            File.Delete(scoreDbPath);

            Lr2PlayHistoryReadResult second = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    PlayedAtFromInclusive = day2 - 100,
                    PlayedAtToExclusive = day2 + 100,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly
                },
                CancellationToken.None,
                out bool secondCacheHit);

            Assert.IsTrue(secondCacheHit);
            CollectionAssert.AreEqual(new long[] { 2L }, second.Rows.Select(row => row.history_id).ToArray());

            Lr2PlayHistoryReadResult unfinalized = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly
                },
                CancellationToken.None,
                out bool unfinalizedCacheHit);

            Assert.IsTrue(unfinalizedCacheHit);
            CollectionAssert.AreEqual(new long[] { 3L }, unfinalized.Rows.Select(row => row.history_id).ToArray());

            Lr2PlayHistoryPeriodIndexResult periodIndex = cache.ReadLr2PeriodIndex(
                new Lr2PlayHistoryPeriodIndexRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true
                },
                CancellationToken.None,
                out bool periodIndexCacheHit);

            Assert.IsTrue(periodIndexCacheHit);
            CollectionAssert.AreEqual(new[] { day2, day1 }, periodIndex.PlayedAtUnixSeconds.ToArray());

            cache.Invalidate();
            Lr2PlayHistoryReadResult afterInvalidate = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly
                },
                CancellationToken.None,
                out bool afterInvalidateCacheHit);

            Assert.IsFalse(afterInvalidateCacheHit);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, afterInvalidate.SchemaStatus);
        });
    }

    [TestMethod]
    public void PlayHistoryReadCache_Lr2KeepsDefaultLimitUnlessDisabled()
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

            var cache = new PlayHistoryReadCache();
            Lr2PlayHistoryReadResult defaultLimited = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly
                },
                CancellationToken.None,
                out bool firstCacheHit);

            Lr2PlayHistoryReadResult unlimited = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly,
                    DisableLimit = true
                },
                CancellationToken.None,
                out bool secondCacheHit);

            Assert.IsFalse(firstCacheHit);
            Assert.IsTrue(secondCacheHit);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit, defaultLimited.Rows.Count);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1, unlimited.Rows.Count);
        });
    }

    [TestMethod]
    public void PlayHistoryReadCache_BeatorajaLoadsAllRowsOnceAndFiltersFromMemory()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            long day1 = UtcEpoch(2026, 1, 1);
            long day2 = UtcEpoch(2026, 1, 2);
            long day3 = UtcEpoch(2026, 1, 3);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: day1, oldClear: 4, clear: 5, oldScore: 80, score: 120, oldCombo: 10, combo: 12, oldMinBp: 4, minBp: 3);
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: day2, oldClear: 5, clear: 6, oldScore: 100, score: 180, oldCombo: 12, combo: 24, oldMinBp: 3, minBp: 2);
                InsertBeatorajaScoreLog(db, ShaA, mode: 10000, date: day3, oldClear: 5, clear: 9, oldScore: 10, score: 99, oldCombo: 1, combo: 2, oldMinBp: 1, minBp: 0);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertBeatorajaPlayerAggregate(db, day1 - 1, playCount: 1, judgeCount: 100, playtime: 100);
                InsertBeatorajaPlayerAggregate(db, day2 - 1, playCount: 2, judgeCount: 160, playtime: 160);
                InsertBeatorajaPlayerAggregate(db, day3 - 1, playCount: 3, judgeCount: 210, playtime: 210);
            }

            var cache = new PlayHistoryReadCache();
            BeatorajaPlayHistoryReadResult first = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1,
                    PlayedAtFromInclusive = day1 - 100,
                    PlayedAtToExclusive = day1 + 100
                },
                CancellationToken.None,
                out bool firstCacheHit);

            Assert.IsFalse(firstCacheHit);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, first.SchemaStatus);
            CollectionAssert.AreEqual(new[] { day1 }, first.Rows.Select(row => row.played_at).ToArray());
            Assert.AreEqual(100, first.Rows[0].notes);
            Assert.IsTrue(first.PlayerSnapshotsAvailable);
            Assert.AreEqual(3, first.PlayerSnapshots.Count);

            File.Delete(scoreLogDbPath);

            BeatorajaPlayHistoryReadResult second = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoreLogDbPath = scoreLogDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1,
                    PlayedAtFromInclusive = day2 - 100,
                    PlayedAtToExclusive = day2 + 100
                },
                CancellationToken.None,
                out bool secondCacheHit);

            Assert.IsTrue(secondCacheHit);
            Assert.AreEqual(1, second.Rows.Count);
            Assert.AreEqual(day2, second.Rows[0].played_at);
            Assert.AreEqual(100, second.Rows[0].old_exscore);
            Assert.AreEqual(180, second.Rows[0].new_exscore);
            Assert.AreEqual(100, second.Rows[0].notes);
            Assert.IsTrue(second.PlayerSnapshotsAvailable);
            Assert.AreEqual(3, second.PlayerSnapshots.Count);

            BeatorajaPlayHistoryReadResult unfinalized = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly
                },
                CancellationToken.None,
                out bool unfinalizedCacheHit);

            Assert.IsTrue(unfinalizedCacheHit);
            Assert.AreEqual(0, unfinalized.Rows.Count);

            BeatorajaPlayHistoryPeriodIndexResult periodIndex = cache.ReadBeatorajaPeriodIndex(
                new BeatorajaPlayHistoryPeriodIndexRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1
                },
                CancellationToken.None,
                out bool periodIndexCacheHit);

            Assert.IsTrue(periodIndexCacheHit);
            CollectionAssert.AreEqual(new[] { day2, day1 }, periodIndex.PlayedAtUnixSeconds.ToArray());

            cache.Invalidate();
            BeatorajaPlayHistoryReadResult afterInvalidate = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath
                },
                CancellationToken.None,
                out bool afterInvalidateCacheHit);

            Assert.IsFalse(afterInvalidateCacheHit);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, afterInvalidate.SchemaStatus);
            Assert.AreEqual(0, afterInvalidate.Rows.Count);
            Assert.AreEqual("play_history_beatoraja_scorelog_missing", afterInvalidate.Diagnostics.Single().Code);
        });
    }

    [TestMethod]
    public void PlayHistoryReadCache_BeatorajaScoreSnapshotVersionInvalidatesCachedNotes()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 1000, oldClear: 4, clear: 5, oldScore: 100, score: 133, oldCombo: 70, combo: 80, oldMinBp: 20, minBp: 10);
            }

            var cache = new PlayHistoryReadCache();
            BeatorajaPlayHistoryReadResult first = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1
                },
                CancellationToken.None,
                out bool firstCacheHit);
            BeatorajaPlayHistoryReadResult second = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 200)),
                    ScoreSnapshotVersion = 2
                },
                CancellationToken.None,
                out bool secondCacheHit);

            Assert.IsFalse(firstCacheHit);
            Assert.IsFalse(secondCacheHit);
            Assert.AreEqual(100, first.Rows.Single().notes);
            Assert.AreEqual(200, second.Rows.Single().notes);
        });
    }

    [TestMethod]
    public void BuildReadView_Lr2OwnsReadPeriodProjectionPresentationAndCacheLifecycle()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            long playedAt = UtcEpoch(2026, 1, 2);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: playedAt, finalized: true, newExscore: 200);
            }

            string songDbPath = Path.Combine(Path.GetDirectoryName(scoreDbPath)!, "song.db");
            File.WriteAllBytes(songDbPath, []);
            var library = new TestBmsLibrary(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath);
            var owner = new PlayHistoryWorkflowOwner();
            var progressStages = new List<PlayHistoryReadWorkflowProgressStage>();
            PlayHistoryViewRequest firstRequest = owner.BeginRequest(
                PlayHistoryPeriodRequest.All(),
                string.Empty,
                PlayHistoryDisplayTargetItem.All.Identity,
                owner.DisplayTargetRevision);

            PlayHistoryReadWorkflowResult first = owner.BuildReadView(
                new PlayHistoryReadWorkflowRequest(
                    firstRequest,
                    PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLr2LinkedProfile: true),
                    string.Empty,
                    PlayHistoryDisplayTargetItem.All,
                    summaryFilterTexts: null,
                    reportProgress: progress => progressStages.Add(progress.Stage)),
                library,
                playlist);

            Assert.IsTrue(first.Built);
            Assert.IsTrue(first.Read.Completed);
            Assert.IsFalse(first.Read.CacheHit);
            Assert.AreEqual(PlayHistoryProvider.Lr2, first.Read.Provider);
            Assert.AreEqual(1, first.Read.RowCount);
            Assert.AreEqual(PlayHistoryPeriodIndexStageStatus.Completed, first.PeriodIndex.Status);
            Assert.AreEqual(1, first.PeriodIndex.DayCount);
            Assert.AreEqual(PlayHistoryProjectionStageStatus.Completed, first.Projection.Status);
            Assert.AreEqual(1, first.Projection.RawCount);
            Assert.AreEqual(1, first.Projection.ProjectedCount);
            Assert.AreEqual(1, first.Presentation.SortedRows.Count);
            Assert.AreEqual(1, first.Presentation.State.SourceCount);
            CollectionAssert.AreEqual(
                new[]
                {
                    PlayHistoryReadWorkflowProgressStage.ReadCompleted,
                    PlayHistoryReadWorkflowProgressStage.PeriodIndexCompleted,
                    PlayHistoryReadWorkflowProgressStage.ProjectionCompleted
                },
                progressStages);

            File.Delete(scoreDbPath);
            PlayHistoryViewRequest secondRequest = owner.BeginRequest(
                PlayHistoryPeriodRequest.All(),
                string.Empty,
                PlayHistoryDisplayTargetItem.All.Identity,
                owner.DisplayTargetRevision);
            PlayHistoryReadWorkflowResult second = owner.BuildReadView(
                new PlayHistoryReadWorkflowRequest(
                    secondRequest,
                    PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLr2LinkedProfile: true),
                    string.Empty,
                    PlayHistoryDisplayTargetItem.All,
                    summaryFilterTexts: null),
                library,
                playlist);

            Assert.IsTrue(second.Built);
            Assert.IsTrue(second.Read.CacheHit);
            Assert.IsTrue(second.PeriodIndex.CacheHit);
            Assert.AreEqual(1, second.Presentation.SortedRows.Count);

            PlayHistoryViewRequest thirdRequest = owner.BeginRequest(
                PlayHistoryPeriodRequest.All(),
                string.Empty,
                PlayHistoryDisplayTargetItem.All.Identity,
                owner.DisplayTargetRevision);
            Assert.ThrowsException<OperationCanceledException>(() => owner.BuildReadView(
                new PlayHistoryReadWorkflowRequest(
                    thirdRequest,
                    PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLr2LinkedProfile: true),
                    string.Empty,
                    PlayHistoryDisplayTargetItem.All,
                    summaryFilterTexts: null,
                    reportProgress: progress =>
                    {
                        if (progress.Stage == PlayHistoryReadWorkflowProgressStage.PeriodIndexCompleted)
                        {
                            throw new OperationCanceledException("observer failure");
                        }
                    }),
                library,
                playlist));
        });
    }

    [TestMethod]
    public void BuildReadView_StaleRequestStopsAtReadBoundary()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest staleRequest = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            owner.DisplayTargetRevision);
        owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            owner.DisplayTargetRevision);

        PlayHistoryReadWorkflowResult result = owner.BuildReadView(
            new PlayHistoryReadWorkflowRequest(
                staleRequest,
                PlayHistoryReadSourceContext.Lr2("unused-score.db", isLr2LinkedProfile: true),
                string.Empty,
                PlayHistoryDisplayTargetItem.All,
                summaryFilterTexts: null),
            library: null,
            playlist: null);

        Assert.IsFalse(result.Built);
        Assert.AreEqual("read", result.CanceledStage);
        Assert.IsFalse(result.Read.Completed);
        Assert.AreEqual(PlayHistoryPeriodIndexStageStatus.NotStarted, result.PeriodIndex.Status);
        Assert.AreEqual(PlayHistoryProjectionStageStatus.NotStarted, result.Projection.Status);
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
        Assert.AreEqual("NP -> NORMAL", row.BestClear);
        Assert.AreEqual("200 -> 250", row.BestExscore);
        Assert.AreEqual("20", row.BestBp);
        Assert.AreEqual("80 -> 120", row.BestCombo);
        Assert.AreEqual(RankType.AA, row.BestDjLevel);
        Assert.AreEqual("A -> AA", row.BestDjLevelText);
        Assert.IsTrue(row.BestRate.HasValue);
        Assert.AreEqual(250 / 300.0, row.BestRate.Value, 0.0001);
        Assert.AreEqual("66.67 -> 83.33", row.BestRateText);
        Assert.AreEqual(240, row.PlayExscore);
        Assert.AreEqual(250, row.NewBestExscore);
        Assert.AreEqual(90, row.PlaytimeSeconds);
        Assert.AreEqual(157, row.JudgeTotal);
        Assert.AreEqual("PG 100 / GR 40 / GD 10 / BD 5 / PR 2", row.Judges);
        Assert.AreEqual("EASY RANDOM", row.Option);
        Assert.AreEqual(16, row.OpHistoryNewBits);
        Assert.AreEqual("P.A", row.OpHistory);
        Assert.AreEqual("score bp clear combo", row.Kind);
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_PlaylistTargetUsesPlaylistFolder()
    {
        PlayHistoryRow row = CreateProjectedRow(HashA);
        BMSTable table = CreateTargetTable(HashA, "Alpha");
        PlayHistoryDisplayTargetIndex index = PlayHistoryDisplayTargetIndex.Create(
            PlayHistoryDisplayTargetItem.FromPlaylist(table),
            [table],
            _ => { });

        bool matched = index.TryApply(row, out PlayHistoryRow displayRow);

        Assert.IsTrue(matched);
        Assert.AreEqual("Alpha", displayRow.FolderLabels);
        Assert.AreEqual(string.Empty, row.FolderLabels);
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_TargetSetFiltersRowsAndUsesOrgSymbolLevel()
    {
        PlayHistoryRow matchingRow = CreateProjectedRow(HashA);
        PlayHistoryRow otherRow = CreateProjectedRow(HashB);
        BMSTable table = CreateTargetTable(HashA, "Alpha");
        PlayHistoryDisplayTargetItem target = PlayHistoryDisplayTargetItem.FromTargetSet(new PlayHistoryDisplayTargetSet
        {
            Name = "SAT Alpha",
            Targets =
            [
                new PlayHistoryDisplayTargetReference
                {
                    PlaylistId = table.playlist_id
                }
            ]
        });
        PlayHistoryDisplayTargetIndex index = PlayHistoryDisplayTargetIndex.Create(target, [table], _ => { });

        bool matched = index.TryApply(matchingRow, out PlayHistoryRow displayRow);
        bool otherMatched = index.TryApply(otherRow, out _);

        Assert.IsTrue(matched);
        Assert.IsFalse(otherMatched);
        Assert.AreEqual("SATAlpha", displayRow.FolderLabels);
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_TargetSetProjectionOnlyKeepsRowsAndClearsUnmatchedFolder()
    {
        PlayHistoryRow matchingRow = CreateProjectedRow(HashA, initialFolderLabels: "Original");
        PlayHistoryRow otherRow = CreateProjectedRow(HashB, initialFolderLabels: "Original");
        BMSTable table = CreateTargetTable(HashA, "Alpha");
        PlayHistoryDisplayTargetItem target = PlayHistoryDisplayTargetItem.FromTargetSetProjectionOnly(new PlayHistoryDisplayTargetSet
        {
            Name = "SAT Alpha",
            Targets =
            [
                new PlayHistoryDisplayTargetReference
                {
                    PlaylistId = table.playlist_id
                }
            ]
        });
        PlayHistoryDisplayTargetIndex index = PlayHistoryDisplayTargetIndex.Create(target, [table], _ => { });

        bool matched = index.TryApply(matchingRow, out PlayHistoryRow displayRow);
        bool otherMatched = index.TryApply(otherRow, out PlayHistoryRow otherDisplayRow);

        Assert.IsTrue(matched);
        Assert.IsTrue(otherMatched);
        Assert.IsFalse(target.IsFiltering);
        Assert.IsTrue(target.UsesProjection);
        Assert.AreEqual("SATAlpha", displayRow.FolderLabels);
        Assert.AreEqual(string.Empty, otherDisplayRow.FolderLabels);
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_TargetSetUsesPlaylistIdOnly()
    {
        PlayHistoryRow idMatchRow = CreateProjectedRow(HashA);
        PlayHistoryRow nameOnlyRow = CreateProjectedRow(HashB);
        BMSTable idMatchedTable = CreateTargetTable(HashA, "Alpha");
        idMatchedTable.playlist_id = 101;
        BMSTable sameNameDifferentIdTable = CreateTargetTable(HashB, "Beta");
        sameNameDifferentIdTable.playlist_id = 202;
        sameNameDifferentIdTable.name = idMatchedTable.name;
        sameNameDifferentIdTable.symbol = idMatchedTable.symbol;
        sameNameDifferentIdTable.org_symbol = idMatchedTable.org_symbol;
        PlayHistoryDisplayTargetItem target = PlayHistoryDisplayTargetItem.FromTargetSet(new PlayHistoryDisplayTargetSet
        {
            Name = "SAT Alpha",
            Targets =
            [
                new PlayHistoryDisplayTargetReference
                {
                    PlaylistId = idMatchedTable.playlist_id
                }
            ]
        });
        PlayHistoryDisplayTargetIndex index = PlayHistoryDisplayTargetIndex.Create(target, [idMatchedTable, sameNameDifferentIdTable], _ => { });

        bool idMatched = index.TryApply(idMatchRow, out PlayHistoryRow displayRow);
        bool sameNameMatched = index.TryApply(nameOnlyRow, out _);

        Assert.IsTrue(idMatched);
        Assert.IsFalse(sameNameMatched);
        Assert.AreEqual("SATAlpha", displayRow.FolderLabels);
    }

    [TestMethod]
    public void ApplyPlayHistoryDisplayTargetRows_AllKeepsProjectedRowsFastPath()
    {
        var owner = new PlayHistoryWorkflowOwner();
        IReadOnlyList<PlayHistoryRow> rows = [CreateProjectedRow(HashA, initialFolderLabels: "SAT")];
        IReadOnlyList<PlayHistoryRow> result = owner.ApplyDisplayTarget(
            rows,
            PlayHistoryDisplayTargetItem.All,
            requestId: 1L,
            displayTargetRevision: 0L,
            CancellationToken.None,
            tableSnapshotFactory: () => throw new AssertFailedException("All must not snapshot display-target tables."),
            ensureEntriesLoaded: null);

        Assert.AreSame(rows, result);
        Assert.AreEqual("SAT", rows[0].FolderLabels);
    }

    [TestMethod]
    public void ApplyPlayHistoryDisplayTargetRows_StaleRequestStopsBeforeTableSnapshot()
    {
        var owner = new PlayHistoryWorkflowOwner();
        IReadOnlyList<PlayHistoryRow> rows = [CreateProjectedRow(HashA, initialFolderLabels: "SAT")];

        Assert.ThrowsException<OperationCanceledException>(() => owner.ApplyDisplayTarget(
            rows,
            PlayHistoryDisplayTargetItem.FromPlaylist(new BMSTable()),
            requestId: 1L,
            displayTargetRevision: owner.DisplayTargetRevision,
            CancellationToken.None,
            tableSnapshotFactory: () => throw new AssertFailedException("Stale requests must not snapshot display-target tables."),
            ensureEntriesLoaded: null));
    }

    [TestMethod]
    public void SnapshotActiveRequest_ReturnsCurrentRequest()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest active = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0L);

        Assert.AreSame(active, owner.SnapshotActiveRequest());
    }

    [TestMethod]
    public void PlayHistoryWorkflowOwner_OwnsSortParametersAndFreshnessSnapshot()
    {
        var owner = new PlayHistoryWorkflowOwner();
        Assert.IsNull(owner.CaptureSortParameters(out SortSnapshot initialSnapshot));
        Assert.IsTrue(owner.IsCurrentSortSnapshot(initialSnapshot));
        Assert.IsTrue(owner.UpdateSortParameters(new ChartListSortParameters()));
        Assert.IsNotNull(owner.CaptureSortParameters(out _));
        Assert.IsTrue(owner.UpdateSortParameters(null));
        var requested = new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.PlayedAt),
            Direction = ListSortDirection.Descending
        };

        Assert.IsTrue(owner.UpdateSortParameters(requested));
        ChartListSortParameters captured = owner.CaptureSortParameters(out SortSnapshot snapshot);
        requested.ColumnsName = nameof(PlayHistoryRow.Title);

        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), captured.ColumnsName);
        Assert.IsTrue(owner.IsCurrentSortSnapshot(snapshot));
        Assert.IsFalse(owner.UpdateSortParameters(captured));
        Assert.IsTrue(owner.UpdateSortParameters(new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.Title),
            Direction = ListSortDirection.Ascending
        }));
        Assert.IsFalse(owner.IsCurrentSortSnapshot(snapshot));
    }

    [TestMethod]
    public void PlayHistoryWorkflowOwner_OwnsCurrentViewFreshnessAndRefreshCandidate()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest request = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: owner.DisplayTargetRevision);
        owner.UpdateSortParameters(new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.PlayedAt),
            Direction = ListSortDirection.Descending
        });
        owner.CaptureSortParameters(out SortSnapshot sortSnapshot);
        PlayHistoryViewState state = CreatePlayHistoryViewState(
            request,
            sortSnapshot,
            keywordFilter: string.Empty,
            keywordRevision: owner.KeywordRevision,
            displayTarget: PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: owner.DisplayTargetRevision);
        owner.PresentationState.CurrentView = state;

        Assert.IsTrue(owner.TrySnapshotCurrentView(request, out PlayHistoryViewState currentView));
        Assert.AreSame(state, currentView);
        Assert.AreEqual(
            PlayHistoryPresentationFreshnessStatus.Fresh,
            owner.EvaluateTerminalPresentationFreshness(state, string.Empty, PlayHistoryDisplayTargetItem.All).Status);

        owner.UpdateSortParameters(new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.Title),
            Direction = ListSortDirection.Ascending
        });
        Assert.AreEqual(
            PlayHistoryPresentationFreshnessStatus.SortStale,
            owner.EvaluateTerminalPresentationFreshness(state, string.Empty, PlayHistoryDisplayTargetItem.All).Status);

        owner.CaptureSortParameters(out SortSnapshot latestSortSnapshot);
        state = CreatePlayHistoryViewState(
            request,
            latestSortSnapshot,
            keywordFilter: string.Empty,
            keywordRevision: owner.KeywordRevision,
            displayTarget: PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: owner.DisplayTargetRevision);
        owner.PresentationState.CurrentView = state;

        PlayHistoryDisplayTargetItem latestTarget = PlayHistoryDisplayTargetItem.FromPlaylist(CreateTargetTable(HashA, "Latest"));
        long latestDisplayRevision = owner.AdvanceDisplayTargetRevision(latestTarget.Identity);
        PlayHistoryPresentationFreshnessResult displayStale = owner.EvaluateTerminalPresentationFreshness(
            state,
            string.Empty,
            latestTarget);
        Assert.AreEqual(PlayHistoryPresentationFreshnessStatus.DisplayTargetStale, displayStale.Status);
        Assert.IsTrue(displayStale.QueueRefresh);
        Assert.AreSame(state, owner.PresentationState.CurrentView);

        PlayHistoryViewState latestDisplayState = CreatePlayHistoryViewState(
            request,
            latestSortSnapshot,
            keywordFilter: string.Empty,
            keywordRevision: owner.KeywordRevision,
            displayTarget: latestTarget,
            displayTargetRevision: latestDisplayRevision);
        owner.PresentationState.CurrentView = latestDisplayState;
        displayStale = owner.EvaluateTerminalPresentationFreshness(state, string.Empty, latestTarget);
        Assert.AreEqual(PlayHistoryPresentationFreshnessStatus.DisplayTargetStale, displayStale.Status);
        Assert.IsFalse(displayStale.QueueRefresh);
        Assert.AreSame(latestDisplayState, owner.PresentationState.CurrentView);

        long latestKeywordRevision = owner.UpdateKeywordIdentity("latest", advanceRevision: true);
        PlayHistoryPresentationFreshnessResult keywordStale = owner.EvaluateTerminalPresentationFreshness(
            latestDisplayState,
            "latest",
            latestTarget);
        Assert.AreEqual(PlayHistoryPresentationFreshnessStatus.KeywordStale, keywordStale.Status);
        Assert.IsTrue(keywordStale.QueueRefresh);

        PlayHistoryViewState latestKeywordState = CreatePlayHistoryViewState(
            request,
            latestSortSnapshot,
            keywordFilter: "latest",
            keywordRevision: latestKeywordRevision,
            displayTarget: latestTarget,
            displayTargetRevision: latestDisplayRevision);
        owner.PresentationState.CurrentView = latestKeywordState;
        keywordStale = owner.EvaluateTerminalPresentationFreshness(latestDisplayState, "latest", latestTarget);
        Assert.AreEqual(PlayHistoryPresentationFreshnessStatus.KeywordStale, keywordStale.Status);
        Assert.IsFalse(keywordStale.QueueRefresh);
        Assert.AreSame(latestKeywordState, owner.PresentationState.CurrentView);
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_UsesPlaylistEntryMd5BeforeSha256()
    {
        PlayHistoryRow row = CreateProjectedRow(HashA, ShaA, initialFolderLabels: "SAT");
        BMSTable table = CreateTargetTable(HashB, "Alpha", ShaA);
        PlayHistoryDisplayTargetIndex index = PlayHistoryDisplayTargetIndex.Create(
            PlayHistoryDisplayTargetItem.FromPlaylist(table),
            [table],
            _ => { });

        bool matched = index.TryApply(row, out _);

        Assert.IsFalse(matched);
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_PlaylistTargetKeepsEmptyFolderEmpty()
    {
        PlayHistoryRow row = CreateProjectedRow(HashA, initialFolderLabels: "SAT");
        BMSTable table = CreateTargetTable(HashA, string.Empty);
        PlayHistoryDisplayTargetIndex index = PlayHistoryDisplayTargetIndex.Create(
            PlayHistoryDisplayTargetItem.FromPlaylist(table),
            [table],
            _ => { });

        bool matched = index.TryApply(row, out PlayHistoryRow displayRow);

        Assert.IsTrue(matched);
        Assert.AreEqual(string.Empty, displayRow.FolderLabels);
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
        Assert.AreEqual("FC -> PA", row.BestClear);

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
        Assert.AreEqual(1L, summary.FinalizedCount);
        Assert.AreEqual(1L, summary.UnfinalizedCount);
        Assert.AreEqual(1, summary.SummaryEligibleCount);
        Assert.AreEqual(60L, summary.PlaytimeSeconds);
        Assert.AreEqual(80L, summary.JudgeCount);
        Assert.AreEqual(1, summary.ScoreUpdateCount);
        Assert.AreEqual(1, summary.NewClearCount);
    }

    [TestMethod]
    public void SummaryTextIncludesDiagnosticDetailWhenPresent()
    {
        PlayHistoryPeriodRequest request = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.All,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("all", []);

        string text = PlayHistoryPresentationState.FormatGridSummaryText(
            request,
            summary,
            [
                new PlayHistoryDiagnostic
                {
                    Provider = PlayHistoryProvider.Lr2,
                    Stage = "read",
                    Severity = PlayHistoryDiagnosticSeverity.Error,
                    Code = "play_history_lr2_schema_unreadable",
                    Message = "Score DB file does not exist.",
                    SourcePath = "C:\\LR2\\Score\\player.db"
                }
            ]);

        StringAssert.Contains(text, "play_history_lr2_schema_unreadable");
        StringAssert.Contains(text, "Score DB file does not exist.");
        StringAssert.Contains(text, "C:\\LR2\\Score\\player.db");
    }

    [TestMethod]
    public void SummaryCardsExposeDedicatedPlayHistoryMetrics()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(1, HashA, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100, oldClear: 0, newClear: 2, newOpHistory: ClearTypeStorageConverter.OptionHistoryEasy, oldMinBp: null, newMinBp: 10, oldMaxCombo: 50, newMaxCombo: 80, playtimeDelta: 70, judgeDelta: 100),
                    CreateRawRecord(2, HashA, 2000, finalized: true, oldExscore: 100, newExscore: 100, newTotalNotes: 100, oldClear: 3, newClear: 5, playtimeDelta: 50, judgeDelta: 60)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("today", projected.Rows);

        IReadOnlyList<PlayHistorySummaryCard> cards = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Lr2);

        Assert.AreEqual(Resources.Play_history_summary_judge_count, cards[0].Label);
        Assert.AreEqual("160", cards[0].Value);
        Assert.AreEqual(Resources.Play_history_summary_play_count, cards[1].Label);
        Assert.AreEqual("2", cards[1].Value);
        Assert.AreEqual(Resources.Play_history_summary_playtime, cards[2].Label);
        Assert.AreEqual("2:00", cards[2].Value);
        Assert.AreEqual(Resources.Play_history_summary_score_update, cards[3].Label);
        Assert.AreEqual("1", cards[3].Value);
        Assert.AreEqual("score", cards[3].FilterKey);
        Assert.AreEqual("type:score", cards[3].FilterText);
        Assert.AreEqual(Resources.Play_history_summary_bp_update, cards[4].Label);
        Assert.AreEqual("1", cards[4].Value);
        Assert.AreEqual("type:bp", cards[4].FilterText);
        Assert.AreEqual(Resources.Play_history_summary_combo_update, cards[5].Label);
        Assert.AreEqual("1", cards[5].Value);
        Assert.AreEqual("type:combo", cards[5].FilterText);
        Assert.AreEqual(Resources.Play_history_summary_clear_update, cards[6].Label);
        Assert.AreEqual("2", cards[6].Value);
        Assert.AreEqual("type:clear", cards[6].FilterText);
        Assert.AreEqual("EASY", cards[8].Label);
        Assert.AreEqual("1", cards[8].Value);
        Assert.AreEqual("type:clear newclear:EC", cards[8].FilterText);
        Assert.IsFalse(cards.Any(card => card.Label == "EXH"));
        Assert.AreEqual("FC", cards[11].Label);
        Assert.AreEqual("1", cards[11].Value);
        Assert.AreEqual("type:clear newclear:FC|PF", cards[11].FilterText);
        Assert.IsTrue(cards[11].Compact);
        Assert.IsFalse(cards[0].IsFilterable);
        Assert.IsTrue(cards[3].IsFilterable);
    }

    [TestMethod]
    public void PresentationOwnerMapsSelectedSummaryFiltersAndDiagnosticPriority()
    {
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal) { "score", "fc" };
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("all", []);

        IReadOnlyList<PlayHistorySummaryCard> cards = PlayHistoryPresentationState.CreateSummaryCards(
            summary,
            PlayHistoryProvider.Lr2,
            selectedKeys);
        IReadOnlyList<string> filterTexts = PlayHistoryPresentationState.GetSummaryFilterTexts(selectedKeys);
        string diagnostic = PlayHistoryPresentationState.FormatDiagnosticSummary(
        [
            null,
            new PlayHistoryDiagnostic { Severity = PlayHistoryDiagnosticSeverity.Warning, Code = "warning" },
            new PlayHistoryDiagnostic { Severity = PlayHistoryDiagnosticSeverity.Error, Code = "error", Message = "detail" }
        ]);

        Assert.IsTrue(cards.Single(card => card.FilterKey == "score").IsSelected);
        Assert.IsTrue(cards.Single(card => card.FilterKey == "fc").IsSelected);
        Assert.IsFalse(cards.Single(card => card.FilterKey == "bp").IsSelected);
        CollectionAssert.AreEqual(new[] { "type:score", "type:clear newclear:FC|PF" }, filterTexts.ToArray());
        StringAssert.StartsWith(diagnostic, "Error error: detail");
        Assert.AreEqual(string.Empty, PlayHistoryPresentationState.FormatDiagnosticSummary([null]));
    }

    [TestMethod]
    public async Task SelectPlaylistSummaryClearsPlayHistorySummaryPresentation()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var archive = new[] { new PlayHistoryPeriodTreeItem("archive", PlayHistoryPeriodRequest.All()) };
        viewModel.PlayHistory.PresentationState.SetArchivePeriodTree(archive);
        viewModel.PlayHistory.PresentationState.SetSummaryCards(new[] { new PlayHistorySummaryCard(Resources.Play_history_summary_judge_count, "1") });
        viewModel.PlayHistory.PresentationState.SetDiagnosticText("diagnostic");

        viewModel.PlaylistWorkspace.RequestSummarySelection();
        TestUiDispatcherHost.Drain();
        await viewModel.PlaylistWorkspace.WaitForPlaylistSummaryDataBuildIdleAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        TestUiDispatcherHost.Drain();

        Assert.AreEqual(0, viewModel.PlayHistory.SummaryCards.Count);
        Assert.AreEqual(string.Empty, viewModel.PlayHistory.SummaryDiagnosticText);
        Assert.AreSame(archive, viewModel.PlayHistory.ArchivePeriodTree);
    }

    [TestMethod]
    public void SummaryFilterCommandUpdatesChildPresentationBeforeRequestingRefresh()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistorySummaryCard scoreCard = PlayHistoryPresentationState.CreateSummaryCards(
            PlayHistoryPeriodSummary.FromRows("All", []),
            PlayHistoryProvider.Lr2).Single(card => card.FilterKey == "score");
        owner.PresentationState.SetSummaryCards([scoreCard]);
        var propertyNames = new List<string>();
        int refreshRequests = 0;
        bool selectedWhenRefreshRequested = false;
        owner.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);
        owner.SummaryFilterRefreshRequested += (_, _) =>
        {
            refreshRequests++;
            selectedWhenRefreshRequested = owner.SummaryCards.Single().IsSelected;
        };

        owner.ToggleSummaryFilterCommand.Execute(scoreCard);

        Assert.IsTrue(owner.SummaryCards.Single().IsSelected);
        Assert.IsTrue(selectedWhenRefreshRequested);
        Assert.AreEqual(1, refreshRequests);
        CollectionAssert.AreEqual(new[] { nameof(PlayHistoryWorkflowOwner.SummaryCards) }, propertyNames);

        owner.ToggleSummaryFilterCommand.Execute(new PlayHistorySummaryCard("label", "value"));
        Assert.AreEqual(1, refreshRequests);
        Assert.AreEqual(1, propertyNames.Count);
    }

    [TestMethod]
    public void SummaryCardsIncludeExHardOnlyForBeatorajaProvider()
    {
        var readResult = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("score.db"),
            [
                new BeatorajaPlayHistoryRecord
                {
                    history_id = 1,
                    sha256 = ShaA,
                    played_at = 1000,
                    playcount = 1,
                    old_clear = (int)ClearType.HARD,
                    new_clear = (int)ClearType.EX_HARD,
                    old_exscore = 100,
                    new_exscore = 120,
                    notes = 100
                }
            ],
            [],
            Lr2PlayHistorySchemaStatus.Installed);
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows(
            "today",
            PlayHistoryRow.ProjectBeatorajaRows(readResult, CreateProjectionIndex()).Rows);

        IReadOnlyList<PlayHistorySummaryCard> beatorajaCards = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Beatoraja);
        IReadOnlyList<PlayHistorySummaryCard> lr2Cards = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Lr2);

        Assert.IsTrue(beatorajaCards.Any(card => card.Label == "EXH" && card.Value == "1"));
        Assert.AreEqual("type:clear newclear:EXH", beatorajaCards.Single(card => card.Label == "EXH").FilterText);
        Assert.IsFalse(lr2Cards.Any(card => card.Label == "EXH"));
    }

    [TestMethod]
    public void SummaryCardsShowDashWhenSnapshotValuesAreUnavailable()
    {
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows(
            "summary",
            [],
            new PlayHistoryPeriodSummaryOverride(null, null, null));

        IReadOnlyList<PlayHistorySummaryCard> cards = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Beatoraja);

        Assert.IsFalse(summary.PlayCountAvailable);
        Assert.IsFalse(summary.JudgeCountAvailable);
        Assert.IsFalse(summary.PlaytimeAvailable);
        Assert.AreEqual(Resources.Play_history_summary_judge_count, cards[0].Label);
        Assert.AreEqual("-", cards[0].Value);
        Assert.AreEqual(Resources.Play_history_summary_play_count, cards[1].Label);
        Assert.AreEqual("-", cards[1].Value);
        Assert.AreEqual(Resources.Play_history_summary_playtime, cards[2].Label);
        Assert.AreEqual("-", cards[2].Value);
    }

    [TestMethod]
    public void RowProjectionFormatsTransitionsAndOptionHistory()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        1,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 100,
                        newExscore: 120,
                        newTotalNotes: 100,
                        oldClear: 0,
                        newClear: 3,
                        oldMinBp: null,
                        newMinBp: 10,
                        oldMaxCombo: 50,
                        newMaxCombo: 80,
                        oldOpHistory: 0x01000000,
                        newOpHistory: 0x00000010)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();

        Assert.AreEqual("score bp clear combo", row.Kind);
        Assert.AreEqual("10", row.BestBp);
        Assert.AreEqual("50.00 -> 60.00", row.BestRateText);
        Assert.AreEqual("P.A / ASSIST off", row.OpHistory);
    }

    [TestMethod]
    public void RowProjectionTreatsLr2AssistToEasyBitAsClearUpdate()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        40,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 100,
                        newExscore: 100,
                        newTotalNotes: 100,
                        oldClear: 2,
                        newClear: 2,
                        oldOpHistory: ClearTypeStorageConverter.OptionHistoryAssist,
                        newOpHistory: ClearTypeStorageConverter.OptionHistoryAssist | ClearTypeStorageConverter.OptionHistoryEasy)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("assist-to-easy", projected.Rows);

        Assert.AreEqual(ClearType.INVALID, row.OldBestClear);
        Assert.AreEqual(ClearType.EASY, row.NewBestClear);
        Assert.AreEqual("ASSIST -> EASY", row.BestClear);
        Assert.AreEqual("clear", row.Kind);
        Assert.AreEqual(1, summary.ClearUpdateCount);
        Assert.AreEqual(1, summary.EasyClearUpdateCount);
        Assert.AreEqual(0, summary.AssistClearUpdateCount);
    }

    [TestMethod]
    public void RowProjectionCountsLr2Clear2WithoutEasyBitAsAssistUpdate()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        42,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 100,
                        newExscore: 100,
                        newTotalNotes: 100,
                        oldClear: 0,
                        newClear: 2,
                        newOpHistory: 0)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("assist-update", projected.Rows);
        PlayHistorySummaryCard assistCard = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Lr2).Single(card => card.Label == "ASSIST");

        Assert.AreEqual(ClearType.NO_PLAY, row.OldBestClear);
        Assert.AreEqual(ClearType.INVALID, row.NewBestClear);
        Assert.AreEqual("NP -> ASSIST", row.BestClear);
        Assert.AreEqual("clear", row.Kind);
        Assert.AreEqual(1, summary.ClearUpdateCount);
        Assert.AreEqual(1, summary.AssistClearUpdateCount);
        Assert.AreEqual(0, summary.EasyClearUpdateCount);
        Assert.AreEqual("1", assistCard.Value);
        Assert.AreEqual("type:clear newclear:AE|LAE", assistCard.FilterText);
    }

    [TestMethod]
    public void RowProjectionTreatsInitialLr2ForceEasySentinelsAsClearOnly()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        43,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: null,
                        newExscore: 1774,
                        newTotalNotes: 1000,
                        oldClear: 0,
                        newClear: 2,
                        oldMinBp: null,
                        newMinBp: -1,
                        oldMaxCombo: null,
                        newMaxCombo: 935,
                        newOpBest: 12,
                        newOpHistory: 0)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("force-easy", projected.Rows);

        Assert.AreEqual(ClearType.NO_PLAY, row.OldBestClear);
        Assert.AreEqual(ClearType.INVALID, row.NewBestClear);
        Assert.AreEqual("NP -> ASSIST", row.BestClear);
        Assert.AreEqual("clear", row.Kind);
        Assert.IsFalse(row.BestScoreUpdated);
        Assert.IsFalse(row.BestBpUpdated);
        Assert.IsFalse(row.BestComboUpdated);
        Assert.IsNull(row.NewBestExscore);
        Assert.IsNull(row.NewBestBp);
        Assert.IsNull(row.NewBestCombo);
        Assert.AreEqual(string.Empty, row.BestExscore);
        Assert.AreEqual(string.Empty, row.BestDjLevelText);
        Assert.AreEqual(string.Empty, row.BestRateText);
        Assert.AreEqual(string.Empty, row.BestBp);
        Assert.AreEqual(string.Empty, row.BestCombo);
        Assert.AreEqual(string.Empty, row.Option);
        Assert.AreEqual(1, summary.ClearUpdateCount);
        Assert.AreEqual(0, summary.ScoreUpdateCount);
        Assert.AreEqual(0, summary.BpUpdateCount);
        Assert.AreEqual(0, summary.ComboUpdateCount);
        Assert.AreEqual(1, summary.AssistClearUpdateCount);
    }

    [TestMethod]
    public void RowProjectionDoesNotCountSameResolvedEasyClearAsClearUpdate()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        41,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 100,
                        newExscore: 100,
                        newTotalNotes: 100,
                        oldClear: 2,
                        newClear: 2,
                        oldOpHistory: ClearTypeStorageConverter.OptionHistoryEasy,
                        newOpHistory: ClearTypeStorageConverter.OptionHistoryAssist | ClearTypeStorageConverter.OptionHistoryEasy)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("same-easy", projected.Rows);

        Assert.AreEqual(ClearType.EASY, row.OldBestClear);
        Assert.AreEqual(ClearType.EASY, row.NewBestClear);
        Assert.IsFalse(row.BestClearUpdated);
        Assert.AreEqual(string.Empty, row.BestClear);
        Assert.AreEqual("play", row.Kind);
        Assert.AreEqual(0, summary.ClearUpdateCount);
        Assert.AreEqual(0, summary.EasyClearUpdateCount);
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
        Assert.AreEqual(1L, summary.FinalizedCount);
        Assert.AreEqual(0, summary.SummaryEligibleCount);
        Assert.AreEqual(0L, summary.PlaytimeSeconds);
        Assert.AreEqual(0L, summary.JudgeCount);
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
            new ChartListSortParameters { ColumnsName = "LibraryChartRowOnlyColumn", Direction = ListSortDirection.Ascending },
            out _,
            out string unknownProfile);

        Assert.IsFalse(unknown);
        Assert.AreEqual("play_history_unknown_column", unknownProfile);
    }

    [TestMethod]
    public async Task MainChartListSortRequest_KeepsPlayHistorySortSeparateFromMainSort()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var regularSortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        regularOwner.SortChanged += (_, request) => regularSortChanged.TrySetResult(request);
        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.FolderFilterSelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
        viewModel.MainChartList.RequestSort(nameof(BMSFile.Title), ListSortDirection.Ascending);
        MainChartListSortRequestedEventArgs regularReceipt = await regularSortChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(nameof(BMSFile.Title), regularReceipt.ColumnName);

        ChartListSortParameters regularSort = regularOwner.CaptureSortParameters();
        Assert.AreEqual(nameof(BMSFile.Title), regularSort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Ascending, regularSort.Direction);
        Assert.IsNull(viewModel.PlayHistory.CaptureSortParameters(out _));
        Assert.AreEqual(regularSort.ColumnsName, viewModel.MainChartList.SortParameters.ColumnsName);
        Assert.AreEqual(regularSort.Direction, viewModel.MainChartList.SortParameters.Direction);

        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.PlayHistorySelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlayHistorySelected);

        var playHistorySortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PlayHistory.SortChanged += (_, request) => playHistorySortChanged.TrySetResult(request);
        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.PlayHistory);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.PlayedAt), ListSortDirection.Descending);
        MainChartListSortRequestedEventArgs playHistoryReceipt = await playHistorySortChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistoryReceipt.ColumnName);

        ChartListSortParameters playHistorySort = viewModel.PlayHistory.CaptureSortParameters(out _);
        Assert.AreEqual(nameof(BMSFile.Title), regularOwner.CaptureSortParameters().ColumnsName);
        Assert.AreEqual(ListSortDirection.Ascending, regularOwner.CaptureSortParameters().Direction);
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistorySort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Descending, playHistorySort.Direction);
        Assert.AreEqual(playHistorySort.ColumnsName, viewModel.MainChartList.SortParameters.ColumnsName);
        Assert.AreEqual(playHistorySort.Direction, viewModel.MainChartList.SortParameters.Direction);
    }

    [TestMethod]
    public async Task MainChartListSortRequest_UsesCapturedSortScopeWhenViewChangesBeforeExecution()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var playHistorySortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PlayHistory.SortChanged += (_, request) => playHistorySortChanged.TrySetResult(request);
        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.FolderFilterSelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.PlayHistory);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.PlayedAt), ListSortDirection.Descending);
        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
        MainChartListSortRequestedEventArgs playHistoryReceipt = await playHistorySortChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistoryReceipt.ColumnName);

        ChartListSortParameters playHistorySort = viewModel.PlayHistory.CaptureSortParameters(out _);
        Assert.IsNull(regularOwner.CaptureSortParameters());
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistorySort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Descending, playHistorySort.Direction);
        Assert.IsNull(viewModel.MainChartList.SortParameters);

        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.PlayHistorySelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlayHistorySelected);

        var regularSortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        regularOwner.SortChanged += (_, request) => regularSortChanged.TrySetResult(request);
        viewModel.MainChartList.SetSortPresentation(
            new MainChartListSortPresentation(
                playHistorySort.ColumnsName,
                playHistorySort.Direction),
            MainChartListSortTarget.Regular);
        viewModel.MainChartList.RequestSort(nameof(BMSFile.Title), ListSortDirection.Ascending);
        viewModel.MainChartList.SetSortPresentation(
            new MainChartListSortPresentation(
                playHistorySort.ColumnsName,
                playHistorySort.Direction),
            MainChartListSortTarget.PlayHistory);
        MainChartListSortRequestedEventArgs regularReceipt = await regularSortChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(nameof(BMSFile.Title), regularReceipt.ColumnName);

        ChartListSortParameters regularSort = regularOwner.CaptureSortParameters();
        Assert.AreEqual(nameof(BMSFile.Title), regularSort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Ascending, regularSort.Direction);
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistorySort.ColumnsName);
        Assert.AreEqual(playHistorySort.ColumnsName, viewModel.MainChartList.SortParameters.ColumnsName);
        Assert.AreEqual(playHistorySort.Direction, viewModel.MainChartList.SortParameters.Direction);
    }

    [TestMethod]
    public void MainChartListSortRequest_RejectsStaleAndAbaOwnerRevisions()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var regularRequests = new List<MainChartListSortRequestedEventArgs>();
        var playHistoryRequests = new List<MainChartListSortRequestedEventArgs>();
        regularOwner.SortChanged += (_, request) => regularRequests.Add(request);
        viewModel.PlayHistory.SortChanged += (_, request) => playHistoryRequests.Add(request);

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
        viewModel.MainChartList.RequestSort(nameof(BMSFile.Title), ListSortDirection.Ascending);
        viewModel.MainChartList.RequestSort(nameof(BMSFile.Artist), ListSortDirection.Descending);
        viewModel.MainChartList.RequestSort(nameof(BMSFile.Title), ListSortDirection.Ascending);

        Assert.AreEqual(3, regularRequests.Count);
        Assert.IsTrue(regularRequests[0].OwnerRevision < regularRequests[1].OwnerRevision);
        Assert.IsTrue(regularRequests[1].OwnerRevision < regularRequests[2].OwnerRevision);
        Assert.IsFalse(regularOwner.IsCurrentSortRequest(regularRequests[0]));
        Assert.IsFalse(regularOwner.IsCurrentSortRequest(regularRequests[1]));
        Assert.IsTrue(regularOwner.IsCurrentSortRequest(regularRequests[2]));

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.PlayHistory);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.Title), ListSortDirection.Ascending);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.PlayedAt), ListSortDirection.Descending);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.Title), ListSortDirection.Ascending);

        Assert.AreEqual(3, playHistoryRequests.Count);
        Assert.IsTrue(playHistoryRequests[0].OwnerRevision < playHistoryRequests[1].OwnerRevision);
        Assert.IsTrue(playHistoryRequests[1].OwnerRevision < playHistoryRequests[2].OwnerRevision);
        Assert.IsFalse(viewModel.PlayHistory.IsCurrentSortRequest(playHistoryRequests[0]));
        Assert.IsFalse(viewModel.PlayHistory.IsCurrentSortRequest(playHistoryRequests[1]));
        Assert.IsTrue(viewModel.PlayHistory.IsCurrentSortRequest(playHistoryRequests[2]));
    }

    [TestMethod]
    public void RegularSortMutation_CancelsTheInFlightRowRequestBeforeRefreshRuns()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var owner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        Assert.IsTrue(owner.TryBeginRequest(out RegularChartListRequestLease lease));

        owner.QueueSort(new MainChartListSortRequestedEventArgs(
            nameof(BMSFile.Title),
            ListSortDirection.Ascending,
            MainChartListSortTarget.Regular));

        Assert.IsTrue(lease.Token.IsCancellationRequested);
    }

    [TestMethod]
    public async Task PlayHistorySortRefreshQueue_CoalescesAndNeverPublishesOutOfRevisionOrder()
    {
        var owner = new PlayHistoryWorkflowOwner();
        var changed = new List<MainChartListSortRequestedEventArgs>();
        var refreshed = new List<MainChartListSortRequestedEventArgs>();
        var firstRefreshEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRefreshRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalRefreshPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long finalRevision = -1L;
        owner.SortChanged += (_, request) => changed.Add(request);
        owner.SortRefreshRequested += (_, request) =>
        {
            lock (refreshed)
            {
                refreshed.Add(request);
            }
            if (firstRefreshEntered.TrySetResult(true))
            {
                firstRefreshRelease.Task.GetAwaiter().GetResult();
            }
            if (request.OwnerRevision == Volatile.Read(ref finalRevision))
            {
                finalRefreshPublished.TrySetResult(true);
            }
        };

        owner.QueueSort(new MainChartListSortRequestedEventArgs(
            nameof(PlayHistoryRow.Title),
            ListSortDirection.Ascending,
            MainChartListSortTarget.PlayHistory));
        await firstRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (int index = 1; index < 40; index++)
            {
                owner.QueueSort(new MainChartListSortRequestedEventArgs(
                    index % 2 == 0 ? nameof(PlayHistoryRow.Title) : nameof(PlayHistoryRow.PlayedAt),
                    index % 2 == 0 ? ListSortDirection.Ascending : ListSortDirection.Descending,
                    MainChartListSortTarget.PlayHistory));
            }
            Volatile.Write(ref finalRevision, changed[changed.Count - 1].OwnerRevision);
        }
        finally
        {
            firstRefreshRelease.TrySetResult(true);
        }
        await finalRefreshPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (refreshed)
        {
            Assert.IsTrue(refreshed.Count < changed.Count);
            for (int index = 1; index < refreshed.Count; index++)
            {
                Assert.IsTrue(refreshed[index - 1].OwnerRevision < refreshed[index].OwnerRevision);
            }
            Assert.IsTrue(owner.IsCurrentSortRequest(refreshed[refreshed.Count - 1]));
        }
    }

    [TestMethod]
    public void PlayHistorySortRefreshQueue_RecoversAfterSubscriberFailure()
    {
        var owner = new PlayHistoryWorkflowOwner();
        using var firstAttempt = new ManualResetEventSlim();
        using var recovered = new ManualResetEventSlim();
        int attempt = 0;
        owner.SortRefreshRequested += (_, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                firstAttempt.Set();
                throw new InvalidOperationException("expected test failure");
            }
            recovered.Set();
        };

        owner.QueueSort(new MainChartListSortRequestedEventArgs(
            nameof(PlayHistoryRow.Title),
            ListSortDirection.Ascending,
            MainChartListSortTarget.PlayHistory));
        Assert.IsTrue(firstAttempt.Wait(TimeSpan.FromSeconds(5)));

        owner.QueueSort(new MainChartListSortRequestedEventArgs(
            nameof(PlayHistoryRow.PlayedAt),
            ListSortDirection.Descending,
            MainChartListSortTarget.PlayHistory));

        Assert.IsTrue(recovered.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, Volatile.Read(ref attempt));
    }

    [TestMethod]
    public void SortRefreshQueues_StartEvenWhenSortChangedSubscriberFails()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        using var regularRefreshed = new ManualResetEventSlim();
        regularOwner.SortChanged += (_, _) => throw new InvalidOperationException("expected regular notification failure");
        regularOwner.SortRefreshRequested += (_, _) => regularRefreshed.Set();

        Assert.ThrowsException<InvalidOperationException>(() => regularOwner.QueueSort(
            new MainChartListSortRequestedEventArgs(
                nameof(BMSFile.Title),
                ListSortDirection.Ascending,
                MainChartListSortTarget.Regular)));
        Assert.IsTrue(regularRefreshed.Wait(TimeSpan.FromSeconds(5)));

        var playHistoryOwner = new PlayHistoryWorkflowOwner();
        using var playHistoryRefreshed = new ManualResetEventSlim();
        playHistoryOwner.SortChanged += (_, _) => throw new InvalidOperationException("expected play-history notification failure");
        playHistoryOwner.SortRefreshRequested += (_, _) => playHistoryRefreshed.Set();

        Assert.ThrowsException<InvalidOperationException>(() => playHistoryOwner.QueueSort(
            new MainChartListSortRequestedEventArgs(
                nameof(PlayHistoryRow.Title),
                ListSortDirection.Ascending,
                MainChartListSortTarget.PlayHistory)));
        Assert.IsTrue(playHistoryRefreshed.Wait(TimeSpan.FromSeconds(5)));
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

    [TestMethod]
    public void BeatorajaReader_ReadsScoreLogUpdateHistory()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 1000, oldClear: 4, clear: 5, oldScore: 100, score: 133, oldCombo: 70, combo: 80, oldMinBp: 20, minBp: 10);
                InsertBeatorajaScoreLog(db, new string('b', 64), mode: 10000, date: 1050, oldClear: 4, clear: 9, oldScore: 1, score: 99, oldCombo: 1, combo: 10, oldMinBp: 2, minBp: 0);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertBeatorajaPlayerAggregate(db, 900, playCount: 6, judgeCount: 800, playtime: 300);
                InsertBeatorajaPlayerAggregate(db, 1099, playCount: 7, judgeCount: 893, playtime: 390);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                ScoreSnapshotVersion = 1,
                PlayedAtFromInclusive = 900,
                PlayedAtToExclusive = 1100
            });
            PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectBeatorajaRows(read, CreateProjectionIndex());
            PlayHistoryRow row = projected.Rows.Single();
            PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows("beatoraja", projected.Rows);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, read.SchemaStatus);
            Assert.IsFalse(read.HasErrors);
            Assert.IsTrue(read.PlayerSnapshotsAvailable);
            Assert.AreEqual(2, read.PlayerSnapshots.Count);
            Assert.AreEqual(PlayHistoryProvider.Beatoraja, row.Provider);
            Assert.AreEqual("Resolved Title", row.Title);
            Assert.AreEqual("Resolved Artist", row.Artist);
            Assert.AreEqual("SAT", row.FolderLabels);
            Assert.AreEqual("Satellite", row.PlaylistNames);
            Assert.AreEqual(HashA, row.Md5);
            Assert.AreEqual(ShaA, row.RawHash);
            Assert.AreEqual(ShaA, row.Sha256);
            Assert.AreEqual(1000L, row.PlayedAtUnix);
            Assert.IsNull(row.PlayExscore);
            Assert.AreEqual(string.Empty, row.Judges);
            Assert.AreEqual("100 -> 133", row.BestExscore);
            Assert.AreEqual("20 -> 10", row.BestBp);
            Assert.AreEqual("70 -> 80", row.BestCombo);
            Assert.AreEqual("EASY -> NORMAL", row.BestClear);
            Assert.AreEqual(string.Empty, row.Option);
            Assert.AreEqual("score bp clear combo", row.Kind);
            Assert.AreEqual("C -> B", row.BestDjLevelText);
            Assert.AreEqual("50.00 -> 66.50", row.BestRateText);
            Assert.IsNull(row.PlaytimeSeconds);
            Assert.AreEqual(1, summary.RowCount);
            Assert.AreEqual(1, summary.SummaryEligibleCount);
            Assert.AreEqual(1, summary.ScoreUpdateCount);
            Assert.AreEqual(1, summary.ClearUpdateCount);
            Assert.AreEqual(0L, summary.PlaytimeSeconds);
            Assert.AreEqual(0L, summary.JudgeCount);
            Assert.IsTrue(GridKeywordSearchQuery.Parse("title:resolved artist:artist folder:SAT playlist:Satellite md5:" + HashA.Substring(0, 8) + " hash:" + ShaA.Substring(0, 12) + " sha256:" + ShaA.Substring(0, 12) + " source:beatoraja kind:score clear:CLEAR").MatchesPlayHistoryRow(row));
            BMSTable targetTable = CreateTargetTable(HashA, "FolderA");
            PlayHistoryDisplayTargetIndex targetIndex = PlayHistoryDisplayTargetIndex.Create(
                PlayHistoryDisplayTargetItem.FromPlaylist(targetTable),
                [targetTable],
                null);
            Assert.IsTrue(targetIndex.TryApply(row, out PlayHistoryRow displayRow));
            Assert.AreEqual("FolderA", displayRow.FolderLabels);
        });
    }

    [TestMethod]
    public void BeatorajaPeriodSummaryUsesPlayerAggregateRangeForScreenPeriod()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            long day1 = UtcEpoch(2026, 1, 1);
            long day2 = UtcEpoch(2026, 1, 2);
            long day3 = UtcEpoch(2026, 1, 3);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: day2 + 3600, oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertBeatorajaPlayerAggregate(db, day1, playCount: 10, judgeCount: 1000, playtime: 100);
                InsertBeatorajaPlayerAggregate(db, day2, playCount: 12, judgeCount: 1160, playtime: 160);
                InsertBeatorajaPlayerAggregate(db, day3, playCount: 13, judgeCount: 1220, playtime: 220);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });
            PlayHistoryPeriodRequest day2Request = PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc);
            PlayHistoryPeriodSummaryOverride day2SummaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(day2Request, read);
            PlayHistoryPeriodSummaryOverride allSummaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
                PlayHistoryPeriodRequest.Create(PlayHistoryPeriodKind.All, new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc),
                read);
            PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectBeatorajaRows(read, PlayHistoryProjectionIndex.Empty);
            PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows(
                "beatoraja",
                [],
                day2SummaryOverride);

            Assert.IsTrue(read.PlayerSnapshotsAvailable);
            Assert.AreEqual(3, read.PlayerSnapshots.Count);
            Assert.AreEqual(2L, day2SummaryOverride.PlayCount);
            Assert.AreEqual(160L, day2SummaryOverride.JudgeCount);
            Assert.AreEqual(60L, day2SummaryOverride.PlaytimeSeconds);
            Assert.AreEqual(13L, allSummaryOverride.PlayCount);
            Assert.AreEqual(1220L, allSummaryOverride.JudgeCount);
            Assert.AreEqual(220L, allSummaryOverride.PlaytimeSeconds);
            Assert.IsNull(projected.Rows.Single().PlaytimeSeconds);
            Assert.AreEqual(0, summary.RowCount);
            Assert.AreEqual(2L, summary.FinalizedCount);
            Assert.AreEqual(160L, summary.JudgeCount);
            Assert.AreEqual(60L, summary.PlaytimeSeconds);
            Assert.IsTrue(summary.PlayCountAvailable);
            Assert.IsTrue(summary.JudgeCountAvailable);
            Assert.IsTrue(summary.PlaytimeAvailable);
        });
    }

    [TestMethod]
    public void BeatorajaPeriodSummaryIsUnavailableForDiagnosticsOrUnreadablePlayerTable()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: UtcEpoch(2026, 1, 2), oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("CREATE TABLE unrelated_to_player (id INTEGER);");
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });
            PlayHistoryPeriodSummaryOverride daySummaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
                PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
                read);
            PlayHistoryPeriodSummaryOverride diagnosticSummaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
                PlayHistoryPeriodRequest.Create(PlayHistoryPeriodKind.Diagnostics, new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc),
                new BeatorajaPlayHistoryReadResult(
                    PlayHistorySourceProfile.Beatoraja(scoreDbPath),
                    [],
                    [],
                    Lr2PlayHistorySchemaStatus.Installed,
                    [new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = UtcEpoch(2026, 1, 2), PlayCount = 1, JudgeCount = 100, PlaytimeSeconds = 100 }],
                    playerSnapshotsAvailable: true));

            Assert.IsFalse(read.HasErrors);
            Assert.IsFalse(read.PlayerSnapshotsAvailable);
            Assert.AreEqual("play_history_beatoraja_player_aggregate_unreadable", read.Diagnostics.Single().Code);
            Assert.IsNull(daySummaryOverride.PlayCount);
            Assert.IsNull(daySummaryOverride.JudgeCount);
            Assert.IsNull(daySummaryOverride.PlaytimeSeconds);
            Assert.IsNull(diagnosticSummaryOverride.PlayCount);
            Assert.IsNull(diagnosticSummaryOverride.JudgeCount);
            Assert.IsNull(diagnosticSummaryOverride.PlaytimeSeconds);
        });
    }

    [TestMethod]
    public void BeatorajaPeriodSummaryIsUnavailableWhenPlayerAggregateDecreases()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        AssertBeatorajaAggregateDecreaseIsUnavailable(day1, day2, playCount2: 9, judgeCount2: 1160, playtime2: 160);
        AssertBeatorajaAggregateDecreaseIsUnavailable(day1, day2, playCount2: 11, judgeCount2: 900, playtime2: 160);
        AssertBeatorajaAggregateDecreaseIsUnavailable(day1, day2, playCount2: 11, judgeCount2: 1160, playtime2: 90);
        AssertBeatorajaAggregateDecreaseIsUnavailable(
            day1,
            day2,
            playCount1: int.MaxValue + 1000L,
            judgeCount1: int.MaxValue + 1000L,
            playtime1: int.MaxValue + 1000L,
            playCount2: int.MaxValue + 500L,
            judgeCount2: int.MaxValue + 1500L,
            playtime2: int.MaxValue + 1500L);
        AssertBeatorajaAggregateDecreaseIsUnavailable(
            day1,
            day2,
            playCount1: -1,
            judgeCount1: 1000,
            playtime1: 100,
            playCount2: 11,
            judgeCount2: 1160,
            playtime2: 160);
    }

    [TestMethod]
    public void BeatorajaPeriodSummaryPreservesLargePlayerAggregateValues()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        long baseValue = int.MaxValue + 1000L;
        long deltaValue = 500L;
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = baseValue, JudgeCount = baseValue + 100, PlaytimeSeconds = baseValue + 200 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = baseValue + deltaValue, JudgeCount = baseValue + 100 + deltaValue, PlaytimeSeconds = baseValue + 200 + deltaValue }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride daySummary = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
            read);
        PlayHistoryPeriodSummaryOverride allSummary = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.All(),
            read);

        Assert.AreEqual(deltaValue, daySummary.PlayCount);
        Assert.AreEqual(deltaValue, daySummary.JudgeCount);
        Assert.AreEqual(deltaValue, daySummary.PlaytimeSeconds);
        Assert.AreEqual(baseValue + deltaValue, allSummary.PlayCount);
        Assert.AreEqual(baseValue + 100 + deltaValue, allSummary.JudgeCount);
        Assert.AreEqual(baseValue + 200 + deltaValue, allSummary.PlaytimeSeconds);
    }

    [TestMethod]
    public void BeatorajaPeriodSummaryIsUnavailableWhenAnyRawJudgeAggregateIsNegative()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = 10, JudgeCount = 1000, PlaytimeSeconds = 100 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = 11, JudgeCount = 1160, PlaytimeSeconds = 160, HasInvalidRawValue = true }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
            read);

        Assert.IsNull(summaryOverride.PlayCount);
        Assert.IsNull(summaryOverride.JudgeCount);
        Assert.IsNull(summaryOverride.PlaytimeSeconds);
    }

    [TestMethod]
    public void BeatorajaPeriodSummaryIsUnavailableWhenIntermediatePlayerAggregateIsInvalid()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        long day3 = UtcEpoch(2026, 1, 3);
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = 10, JudgeCount = 1000, PlaytimeSeconds = 100 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = 11, JudgeCount = 1160, PlaytimeSeconds = 160, HasInvalidRawValue = true },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day3, PlayCount = 12, JudgeCount = 1220, PlaytimeSeconds = 220 }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.All(),
            read);

        Assert.IsNull(summaryOverride.PlayCount);
        Assert.IsNull(summaryOverride.JudgeCount);
        Assert.IsNull(summaryOverride.PlaytimeSeconds);
    }

    [TestMethod]
    public void BeatorajaPeriodSummaryIsUnavailableWhenIntermediatePlayerAggregateDecreases()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        long day3 = UtcEpoch(2026, 1, 3);
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = 10, JudgeCount = 1000, PlaytimeSeconds = 100 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = 9, JudgeCount = 900, PlaytimeSeconds = 90 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day3, PlayCount = 12, JudgeCount = 1220, PlaytimeSeconds = 220 }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.All(),
            read);

        Assert.IsNull(summaryOverride.PlayCount);
        Assert.IsNull(summaryOverride.JudgeCount);
        Assert.IsNull(summaryOverride.PlaytimeSeconds);
    }

    [TestMethod]
    public void BeatorajaReaderMarksNegativeRawJudgeAggregateAsInvalid()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            long day1 = UtcEpoch(2026, 1, 1);
            long day2 = UtcEpoch(2026, 1, 2);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: day2 + 3600, oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertBeatorajaPlayerAggregate(db, day1, playCount: 10, judgeCount: 1000, playtime: 100);
                db.Execute(
                    "INSERT OR REPLACE INTO player (date, playcount, epg, lpg, egr, lgr, egd, lgd, ebd, lbd, epr, lpr, ems, lms, playtime) VALUES (?, ?, ?, ?, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ?);",
                    day2,
                    11,
                    -1,
                    200,
                    160);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });
            PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
                PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
                read);

            Assert.IsTrue(read.PlayerSnapshots.Last().HasInvalidRawValue);
            Assert.IsNull(summaryOverride.PlayCount);
            Assert.IsNull(summaryOverride.JudgeCount);
            Assert.IsNull(summaryOverride.PlaytimeSeconds);
        });
    }

    [TestMethod]
    public void BeatorajaReader_MissingScoreLogReturnsNoUpdateRows()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaPlayerDb(scoreDbPath);

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });

            Assert.IsFalse(File.Exists(scoreLogDbPath));
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, read.SchemaStatus);
            Assert.AreEqual(0, read.Rows.Count);
            Assert.AreEqual("play_history_beatoraja_scorelog_missing", read.Diagnostics.Single().Code);
        });
    }

    [TestMethod]
    public void BeatorajaReader_DoesNotFallbackToScoreDataLogWhenScoreLogIsMissing()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaPlayerDb(scoreDbPath);
            string scoreDataLogDbPath = Path.Combine(Path.GetDirectoryName(scoreDbPath), "scoredatalog.db");
            CreateBeatorajaScoreDataLogLatestDb(scoreDataLogDbPath);
            using (var db = new SQLiteConnection(scoreDataLogDbPath))
            {
                InsertBeatorajaScoreDataLogLatest(db, ShaA, mode: 0, date: 1000);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });

            Assert.IsTrue(File.Exists(scoreDataLogDbPath));
            Assert.IsFalse(File.Exists(scoreLogDbPath));
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, read.SchemaStatus);
            Assert.AreEqual(0, read.Rows.Count);
            Assert.AreEqual("play_history_beatoraja_scorelog_missing", read.Diagnostics.Single().Code);
        });
    }

    [TestMethod]
    public void BeatorajaReader_UnfinalizedOnlyReturnsEmptyRows()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 1000, oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                FinalizationFilter = Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, read.SchemaStatus);
            Assert.IsFalse(read.HasErrors);
            Assert.AreEqual(0, read.Rows.Count);
        });
    }

    [TestMethod]
    public void BeatorajaReader_ReadReportsUnreadableScoreLog()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaPlayerDb(scoreDbPath);
            File.WriteAllText(scoreLogDbPath, "not a sqlite database");

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, read.SchemaStatus);
            Assert.IsTrue(read.HasErrors);
            Assert.AreEqual(0, read.Rows.Count);
            Assert.IsTrue(read.Diagnostics.Any(diagnostic => diagnostic.Code == "play_history_beatoraja_read_failed"));
        });
    }

    [TestMethod]
    public void BeatorajaReader_TreatsMaxValueOldMinBpAsUnplayed()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 1000, oldClear: 5, clear: 5, oldScore: 50, score: 50, oldCombo: 12, combo: 12, oldMinBp: int.MaxValue, minBp: 10);
            }

            PlayHistoryRow row = PlayHistoryRow.ProjectBeatorajaRows(
                new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest { ScoreDbPath = scoreDbPath }),
                CreateProjectionIndex()).Rows.Single();

            Assert.IsNull(row.OldBestBp);
            Assert.AreEqual(10, row.NewBestBp);
            Assert.AreEqual("10", row.BestBp);
            Assert.AreEqual("bp", row.Kind);
            Assert.IsFalse(row.BestBp.Contains(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        });
    }

    [TestMethod]
    public void BeatorajaReader_ReadsScoreLogRowsDirectly()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 999, oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
                InsertBeatorajaScoreLog(db, ShaA, mode: 10000, date: 1000, oldClear: 4, clear: 9, oldScore: 10, score: 99, oldCombo: 8, combo: 20, oldMinBp: 5, minBp: 0);
                InsertBeatorajaScoreLog(db, new string('b', 64), mode: 0, date: 1000, oldClear: 4, clear: 9, oldScore: 10, score: 99, oldCombo: 8, combo: 20, oldMinBp: 5, minBp: 0);
            }

            PlayHistoryRow row = PlayHistoryRow.ProjectBeatorajaRows(
                new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    PlayedAtToExclusive = 1000
                }),
                PlayHistoryProjectionIndex.Empty).Rows.Single();

            Assert.AreEqual(999L, row.PlayedAtUnix);
            Assert.AreEqual("10 -> 25", row.BestExscore);
            Assert.AreEqual("5 -> 3", row.BestBp);
            Assert.AreEqual("EASY -> NORMAL", row.BestClear);
            Assert.AreEqual("score bp clear combo", row.Kind);
            Assert.AreEqual(string.Empty, row.BestDjLevelText);
            Assert.AreEqual(string.Empty, row.BestRateText);
        });
    }

    [TestMethod]
    public void BeatorajaReader_PeriodIndexUsesScoreLogMaxDatePerLocalDay()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: UtcEpoch(2026, 1, 1, 1), oldClear: 4, clear: 5, oldScore: 10, score: 20, oldCombo: 5, combo: 10, oldMinBp: 8, minBp: 7);
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: UtcEpoch(2026, 1, 1, 12), oldClear: 5, clear: 5, oldScore: 20, score: 30, oldCombo: 10, combo: 11, oldMinBp: 7, minBp: 6);
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: UtcEpoch(2026, 1, 2, 1), oldClear: 5, clear: 5, oldScore: 30, score: 40, oldCombo: 11, combo: 12, oldMinBp: 6, minBp: 5);
                InsertBeatorajaScoreLog(db, new string('b', 64), mode: 10000, date: UtcEpoch(2026, 1, 3, 1), oldClear: 4, clear: 9, oldScore: 1, score: 99, oldCombo: 1, combo: 20, oldMinBp: 5, minBp: 0);
            }

            BeatorajaPlayHistoryPeriodIndexResult result = new BeatorajaPlayHistoryReader().ReadPeriodIndex(
                new BeatorajaPlayHistoryPeriodIndexRequest { ScoreDbPath = scoreDbPath },
                CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { UtcEpoch(2026, 1, 2, 1), UtcEpoch(2026, 1, 1, 12) },
                result.PlayedAtUnixSeconds.ToArray());
        });
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
            (md5, sha256) => new PlaylistReferenceDisplay(
                [new PlaylistReferenceTableDisplaySnapshot(table.symbol, table.name)]),
            (sha256, md5) => null);
    }

    private static void AssertPeriodRange(PlayHistoryPeriodKind kind, long expectedFromInclusive, long expectedToExclusive)
    {
        PlayHistoryPeriodRequest request = PlayHistoryPeriodRequest.Create(
            kind,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.AreEqual(expectedFromInclusive, request.PlayedAtFromInclusive, kind.ToString());
        Assert.AreEqual(expectedToExclusive, request.PlayedAtToExclusive, kind.ToString());
        Assert.IsFalse(request.IncludeUnfinalized, kind.ToString());
        Assert.AreEqual(Lr2PlayHistoryFinalizationFilter.FinalizedOnly, request.FinalizationFilter, kind.ToString());
    }

    private static long UtcEpoch(int year, int month, int day)
    {
        return UtcEpoch(year, month, day, 0);
    }

    private static long UtcEpoch(int year, int month, int day, int hour)
    {
        return new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
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

    private static void WithBeatorajaPlayerDb(Action<string, string> action)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BeatorajaPlayHistory_" + Guid.NewGuid().ToString("N"));
        string playerDirectoryPath = Path.Combine(directoryPath, "player", "player1");
        Directory.CreateDirectory(playerDirectoryPath);
        try
        {
            string scoreDbPath = Path.Combine(playerDirectoryPath, "score.db");
            using (new SQLiteConnection(scoreDbPath))
            {
            }
            action(
                scoreDbPath,
                Path.Combine(playerDirectoryPath, "scorelog.db"));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private static void CreateBeatorajaScoreLogDb(string scoreLogDbPath)
    {
        using var db = new SQLiteConnection(scoreLogDbPath);
        db.Execute(
            "CREATE TABLE scorelog (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, oldclear INTEGER, score INTEGER, oldscore INTEGER, combo INTEGER, oldcombo INTEGER, minbp INTEGER, oldminbp INTEGER, date INTEGER);");
    }

    private static void CreateBeatorajaScoreDataLogLatestDb(string scoreDataLogDbPath)
    {
        using var db = new SQLiteConnection(scoreDataLogDbPath);
        db.Execute(
            "CREATE TABLE scoredatalog (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, date INTEGER, scorehash TEXT, PRIMARY KEY(sha256, mode));");
    }

    private static void CreateBeatorajaPlayerDb(string scoreDbPath)
    {
        using var db = new SQLiteConnection(scoreDbPath);
        db.Execute("CREATE TABLE player (date INTEGER PRIMARY KEY, playcount INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, egd INTEGER, lgd INTEGER, ebd INTEGER, lbd INTEGER, epr INTEGER, lpr INTEGER, ems INTEGER, lms INTEGER, playtime INTEGER);");
    }

    private static void InsertBeatorajaPlayerAggregate(SQLiteConnection db, long date, int playCount, int judgeCount, int playtime)
    {
        db.Execute(
            "INSERT OR REPLACE INTO player (date, playcount, epg, lpg, egr, lgr, egd, lgd, ebd, lbd, epr, lpr, ems, lms, playtime) VALUES (?, ?, ?, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ?);",
            date,
            playCount,
            judgeCount,
            playtime);
    }

    private static IReadOnlyDictionary<string, BMSScore> CreateBeatorajaScoreSnapshot(params (string Sha256, int Notes)[] entries)
    {
        var scoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sha256, int notes) in entries)
        {
            if (string.IsNullOrWhiteSpace(sha256) || notes <= 0)
            {
                continue;
            }
            scoresBySha256[sha256.Trim()] = new BMSScore
            {
                hash = sha256.Trim(),
                totalnotes = notes
            };
        }
        return scoresBySha256;
    }

    private static void AssertBeatorajaAggregateDecreaseIsUnavailable(long day1, long day2, long playCount2, long judgeCount2, long playtime2)
    {
        AssertBeatorajaAggregateDecreaseIsUnavailable(
            day1,
            day2,
            playCount1: 10,
            judgeCount1: 1000,
            playtime1: 100,
            playCount2,
            judgeCount2,
            playtime2);
    }

    private static void AssertBeatorajaAggregateDecreaseIsUnavailable(
        long day1,
        long day2,
        long playCount1,
        long judgeCount1,
        long playtime1,
        long playCount2,
        long judgeCount2,
        long playtime2)
    {
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = playCount1, JudgeCount = judgeCount1, PlaytimeSeconds = playtime1 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = playCount2, JudgeCount = judgeCount2, PlaytimeSeconds = playtime2 }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
            read);

        Assert.IsNull(summaryOverride.PlayCount);
        Assert.IsNull(summaryOverride.JudgeCount);
        Assert.IsNull(summaryOverride.PlaytimeSeconds);
    }

    private static void InsertBeatorajaScoreDataLogLatest(SQLiteConnection db, string sha256, int mode, long date)
    {
        db.Execute(
            "INSERT OR REPLACE INTO scoredatalog (sha256, mode, clear, date, scorehash) VALUES (?, ?, ?, ?, ?);",
            sha256,
            mode,
            5,
            date,
            "latest-detail-only");
    }

    private static void InsertBeatorajaScoreLog(
        SQLiteConnection db,
        string sha256,
        int mode,
        long date,
        int oldClear,
        int clear,
        int oldScore,
        int score,
        int oldCombo,
        int combo,
        int oldMinBp,
        int minBp)
    {
        db.Execute(
            "INSERT INTO scorelog (sha256, mode, clear, oldclear, score, oldscore, combo, oldcombo, minbp, oldminbp, date) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
            sha256,
            mode,
            clear,
            oldClear,
            score,
            oldScore,
            combo,
            oldCombo,
            minBp,
            oldMinBp,
            date);
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

    private static PlayHistoryRow CreateProjectedRow(string hash, string sha256 = "", string initialFolderLabels = "")
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [CreateRawRecord(100, hash, 1000, finalized: true, oldExscore: null, newExscore: 100, newTotalNotes: 100)],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            string.IsNullOrWhiteSpace(sha256)
                ? PlayHistoryProjectionIndex.Empty
                : PlayHistoryProjectionIndex.Create(
                    PlaylistLibraryResolveIndexSnapshot.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [hash] = sha256 }));
        PlayHistoryRow row = projected.Rows.Single();
        return string.IsNullOrEmpty(initialFolderLabels) ? row : row.WithPlaylistDisplay(initialFolderLabels);
    }

    private static PlayHistoryViewState CreatePlayHistoryViewState(
        PlayHistoryViewRequest request,
        SortSnapshot sortSnapshot,
        string keywordFilter,
        long keywordRevision,
        PlayHistoryDisplayTargetItem displayTarget,
        long displayTargetRevision)
    {
        IReadOnlyList<PlayHistoryRow> rows = [CreateProjectedRow(HashA)];
        return new PlayHistoryViewState(
            request.RequestId,
            request.PeriodRequest,
            rows,
            rows,
            rows,
            [],
            PlayHistoryProvider.Lr2,
            Lr2PlayHistorySchemaStatus.Installed,
            rows.Count,
            sortSnapshot,
            keywordFilter,
            keywordRevision,
            displayTarget,
            displayTargetRevision);
    }

    private static BMSTable CreateTargetTable(string md5, string folder, string sha256 = "")
    {
        string sha256Json = string.IsNullOrWhiteSpace(sha256) ? string.Empty : ",\"sha256\":\"" + sha256 + "\"";
        var table = new BMSTable
        {
            playlist_id = 7,
            name = "Satellite",
            symbol = "SAT",
            org_symbol = "SAT",
            entries =
            [
                new BMSTableEntry(JObject.Parse("{\"md5\":\"" + md5 + "\"" + sha256Json + ",\"title\":\"Target\",\"level\":\"" + folder + "\"}"))
            ]
        };
        return table;
    }

    private static void SetPrivateField<T>(MainWindowViewModel viewModel, string fieldName, T value)
    {
        typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }

    private static T GetPrivateField<T>(MainWindowViewModel viewModel, string fieldName)
    {
        return (T)typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
    }

    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string HashC = "cccccccccccccccccccccccccccccccc";

    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public void WorkflowOwner_TransfersRequestIdentityAndCancelsPreviousLifecycle()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest first = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            "keyword-a",
            "target-a",
            displayTargetRevision: 3);
        System.Threading.CancellationToken firstToken = owner.GetCancellationToken(first.RequestId);

        PlayHistoryViewRequest second = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            "keyword-b",
            "target-b",
            displayTargetRevision: 4);

        Assert.IsTrue(firstToken.IsCancellationRequested);
        Assert.IsFalse(owner.IsCurrentRequest(first.RequestId));
        Assert.IsTrue(owner.IsCurrentRequest(second.RequestId));
        Assert.AreSame(second, owner.SnapshotActiveRequest());
        Assert.AreEqual("keyword-b", owner.PresentationState.CurrentKeywordIdentity);
        Assert.AreEqual("target-b", owner.PresentationState.CurrentDisplayTargetIdentity);
        Assert.AreEqual(4L, owner.PresentationState.DisplayTargetRevision);

        bool selectionDeactivated = false;
        System.Threading.CancellationToken secondToken = owner.GetCancellationToken(second.RequestId);
        owner.Deactivate(() => selectionDeactivated = true);

        Assert.IsTrue(selectionDeactivated);
        Assert.IsTrue(secondToken.IsCancellationRequested);
        Assert.IsNull(owner.SnapshotActiveRequest());
        Assert.IsFalse(owner.IsCurrentRequest(second.RequestId));
    }

    [TestMethod]
    public void WorkflowOwner_InvalidateReadCache_CancelsRequestPreservesActivityAndLogsReason()
    {
        var logMessages = new List<string>();
        var owner = new PlayHistoryWorkflowOwner(logMessages.Add);
        PlayHistoryViewRequest request = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);
        System.Threading.CancellationToken token = owner.GetCancellationToken(request.RequestId);

        owner.InvalidateReadCache("score_reload");

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsNull(owner.SnapshotActiveRequest());
        Assert.IsTrue(owner.IsViewActive);
        CollectionAssert.AreEqual(
            new[] { "play_history_read_cache_invalidated reason=score_reload" },
            logMessages);
    }

    [TestMethod]
    public void WorkflowOwner_PublishesViewActiveFromRequestLifecycle()
    {
        var owner = new PlayHistoryWorkflowOwner();
        List<bool> activeNotifications = [];
        owner.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(PlayHistoryWorkflowOwner.IsViewActive), StringComparison.Ordinal))
            {
                activeNotifications.Add(owner.IsViewActive);
            }
        };

        Assert.IsFalse(owner.IsViewActive);
        PlayHistoryViewRequest first = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);

        Assert.IsTrue(owner.IsViewActive);
        Assert.IsTrue(owner.IsCurrentRequest(first.RequestId));
        Assert.AreEqual(1, activeNotifications.Count);
        Assert.IsTrue(activeNotifications[0]);

        owner.Deactivate(clearViewActivity: false);
        Assert.IsTrue(owner.IsViewActive, "A data refresh may cancel the request while keeping the play-history shell selected.");
        Assert.AreEqual(1, activeNotifications.Count);

        owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            "reloaded",
            string.Empty,
            displayTargetRevision: 0);
        Assert.IsTrue(owner.IsViewActive);
        Assert.AreEqual(1, activeNotifications.Count, "Restoring a request in the selected view does not change activity.");

        owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            "next",
            string.Empty,
            displayTargetRevision: 0);
        Assert.AreEqual(1, activeNotifications.Count, "Replacing an active request keeps the owner active.");

        owner.Deactivate();

        Assert.IsFalse(owner.IsViewActive);
        Assert.AreEqual(2, activeNotifications.Count);
        Assert.IsFalse(activeNotifications[1]);
    }

    [TestMethod]
    public void WorkflowOwner_PeriodActivationFailureDoesNotLeaveRequestLifecycleActive()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.ConfigureDetailSourceRetirement(
            () => new PlaylistSourceRetirementRequest(1, null));
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () => [],
            _ => { });
        PlayHistoryViewRequest first = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);
        System.Threading.CancellationToken firstToken = owner.GetCancellationToken(first.RequestId);

        owner.PeriodRequestActivated += (_, _) => throw new InvalidOperationException("selection failed");
        Assert.ThrowsException<InvalidOperationException>(() => owner.ActivatePeriod(
            PlayHistoryPeriodRequest.All(),
            string.Empty));

        Assert.IsTrue(firstToken.IsCancellationRequested);
        Assert.IsNull(owner.SnapshotActiveRequest());
        Assert.IsFalse(owner.IsCurrentRequest(owner.CurrentRequestId));

        PlayHistoryViewRequest next = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);
        System.Threading.CancellationToken nextToken = owner.GetCancellationToken(next.RequestId);
        bool lockHeldDuringDeactivateSelection = false;
        Assert.ThrowsException<InvalidOperationException>(() => owner.Deactivate(
            () =>
            {
                lockHeldDuringDeactivateSelection =
                    Monitor.IsEntered(owner.PresentationState.SyncRoot);
                throw new InvalidOperationException("deactivation failed");
            }));
        Assert.IsFalse(lockHeldDuringDeactivateSelection);
        Assert.IsTrue(nextToken.IsCancellationRequested);
        Assert.IsNull(owner.SnapshotActiveRequest());
    }

    [TestMethod]
    public void MainViewSelectionKeepsExistingChartOperationContextWhenDeactivationRaises()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        PlayHistoryWorkflowOwner playHistory = viewModel.PlayHistory;
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
        playHistory.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);
        playHistory.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(PlayHistoryWorkflowOwner.IsViewActive), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("deactivation notification failed");
            }
        };

        MethodInfo setSelection = typeof(MainWindowViewModel).GetMethod(
            "SetTreeViewFilterSelection",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.ThrowsException<TargetInvocationException>(() => setSelection.Invoke(
            viewModel,
            new object[] { MainViewUpdateMode.FolderFilterSelected, null! }));

        Assert.AreEqual(MainViewOperationSection.InstallPending, viewModel.MainChartList.CurrentOperationContext.OperationSection);
        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, viewModel.MainChartList.CurrentOperationContext.SourceScope);
        Assert.IsNull(playHistory.SnapshotActiveRequest());
    }

    [TestMethod]
    public void WorkflowOwner_ActivatePeriodRaisesAfterRequestLockIsReleased()
    {
        var owner = new PlayHistoryWorkflowOwner();
        var detailSourceRetirement = new PlaylistSourceRetirementRequest(1, null);
        bool lockHeldDuringRetirement = false;
        owner.ConfigureDetailSourceRetirement(() =>
        {
            lockHeldDuringRetirement = Monitor.IsEntered(owner.PresentationState.SyncRoot);
            return detailSourceRetirement;
        });
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () => [],
            _ => { });
        PlayHistoryViewRequest? activated = null;
        bool lockWasHeld = false;
        owner.PeriodRequestActivated += (_, args) =>
        {
            activated = args.Request;
            lockWasHeld = Monitor.IsEntered(owner.PresentationState.SyncRoot);
        };

        PlayHistoryViewRequest request = owner.ActivatePeriod(
            PlayHistoryPeriodRequest.All(),
            "  keyword  ");

        Assert.AreSame(request, activated);
        Assert.AreSame(detailSourceRetirement, request.DetailSourceRetirement);
        Assert.IsFalse(lockHeldDuringRetirement);
        Assert.IsFalse(lockWasHeld);
        Assert.AreEqual(PlaylistRequestFactory.NormalizeKeywordFilter("  keyword  "), owner.CurrentKeywordIdentity);
        Assert.IsTrue(owner.IsCurrentRequest(request.RequestId));
    }

    [TestMethod]
    public async Task WorkflowOwner_OwnsRefreshRevisionCoalescingAndActivity()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest =
            owner.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty, string.Empty, 0);
        var detailSourceRetirement = new PlaylistSourceRetirementRequest(
            requestVersion: 7,
            buildCancellation: null);
        activeRequest.DetailSourceRetirement = detailSourceRetirement;

        Assert.IsTrue(owner.TryBeginKeywordRefresh(string.Empty, advanceRevision: false, out PlayHistoryViewRequest initialRequest));
        Assert.AreEqual(0L, initialRequest.KeywordFilterRevision);
        Assert.AreSame(detailSourceRetirement, initialRequest.DetailSourceRetirement);
        owner.CompleteKeywordRefresh(initialRequest.KeywordFilterRevision);

        Task alreadyIdle = owner.WaitForRefreshQueuesIdleAsync();
        Assert.IsTrue(alreadyIdle.IsCompletedSuccessfully);

        Assert.IsTrue(owner.TryBeginKeywordRefresh("keyword", advanceRevision: true, out PlayHistoryViewRequest keywordRequest));
        Task keywordIdle = owner.WaitForRefreshQueuesIdleAsync();
        Assert.IsFalse(keywordIdle.IsCompleted);
        Assert.IsFalse(owner.TryBeginKeywordRefresh("keyword", advanceRevision: false, out _));
        Assert.IsTrue(owner.TryBeginDisplayTargetRefresh("target", advanceRevision: true, out PlayHistoryViewRequest displayRequest));
        Task displayIdle = owner.WaitForRefreshQueuesIdleAsync();
        Assert.AreSame(keywordIdle, displayIdle);
        Assert.AreSame(detailSourceRetirement, keywordRequest.DetailSourceRetirement);
        Assert.AreSame(detailSourceRetirement, displayRequest.DetailSourceRetirement);
        Assert.IsFalse(owner.TryBeginDisplayTargetRefresh("target", advanceRevision: false, out _));
        Assert.IsFalse(owner.AreRefreshQueuesIdle);

        owner.ClearQueuedRefreshes();
        Assert.IsFalse(owner.AreRefreshQueuesIdle, "Active workers keep the owner non-idle after queued revisions are cleared.");
        Assert.IsFalse(keywordIdle.IsCompleted, "Clearing revisions must not report idle while their workers remain active.");
        owner.CompleteKeywordRefresh(keywordRequest.KeywordFilterRevision);
        Assert.IsFalse(keywordIdle.IsCompleted, "The display-target worker still owns active work.");
        owner.CompleteDisplayTargetRefresh(displayRequest.DisplayTargetRevision);

        await keywordIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(owner.AreRefreshQueuesIdle);
        Assert.AreEqual(keywordRequest.KeywordFilterRevision, owner.KeywordRevision);
        Assert.AreEqual(displayRequest.DisplayTargetRevision, owner.DisplayTargetRevision);
        Assert.AreEqual("target", owner.PresentationState.CurrentDisplayTargetIdentity);

        owner.UpdateKeywordIdentity("latest", advanceRevision: true);
        long latestKeywordRevision = owner.KeywordRevision;
        Assert.IsFalse(owner.TryBeginKeywordRefresh("stale", advanceRevision: false, out _));
        Assert.AreEqual(latestKeywordRevision, owner.KeywordRevision);
        Assert.AreEqual("latest", owner.PresentationState.CurrentKeywordIdentity);
        owner.AdvanceDisplayTargetRevision("latest-target");
        long latestDisplayRevision = owner.DisplayTargetRevision;
        Assert.IsFalse(owner.TryBeginDisplayTargetRefresh("stale-target", advanceRevision: false, out _));
        Assert.AreEqual(latestDisplayRevision, owner.DisplayTargetRevision);
        Assert.AreEqual("latest-target", owner.PresentationState.CurrentDisplayTargetIdentity);

        int keywordReservations = 0;
        owner.UpdateKeywordIdentity("concurrent", advanceRevision: true);
        PlayHistoryViewRequest? concurrentRequest = null;
        System.Threading.Tasks.Parallel.For(0, 32, _ =>
        {
            if (owner.TryBeginKeywordRefresh("concurrent", advanceRevision: false, out PlayHistoryViewRequest request))
            {
                concurrentRequest = request;
                Interlocked.Increment(ref keywordReservations);
            }
        });
        Assert.AreEqual(1, keywordReservations);
        owner.CompleteKeywordRefresh(concurrentRequest!.KeywordFilterRevision);
        Assert.ThrowsException<InvalidOperationException>(() => owner.CompleteKeywordRefresh(concurrentRequest.KeywordFilterRevision));
        await owner.WaitForRefreshQueuesIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(owner.TryBeginKeywordRefresh("stale-current", advanceRevision: true, out PlayHistoryViewRequest staleRequest));
        Assert.IsTrue(owner.TryBeginKeywordRefresh("stale-current", advanceRevision: true, out PlayHistoryViewRequest currentRequest));
        Task requeuedIdle = owner.WaitForRefreshQueuesIdleAsync();
        owner.CompleteKeywordRefresh(staleRequest.KeywordFilterRevision);
        Assert.IsFalse(requeuedIdle.IsCompleted, "Completing a stale worker must retain the current queued revision.");
        owner.CompleteKeywordRefresh(currentRequest.KeywordFilterRevision);
        await requeuedIdle.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task WorkflowOwner_QueuesViewRefreshAndCompletesRevisionAfterRefreshCallback()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty, string.Empty, 0);
        using var refreshed = new ManualResetEventSlim();
        var requests = new List<PlayHistoryViewRequest>();
        owner.ConfigureViewRefreshScheduler(
            () => false,
            request =>
            {
                lock (requests)
                {
                    requests.Add(request);
                }
                refreshed.Set();
            });

        owner.QueueKeywordFilterRefresh(string.Empty, advanceRevision: false);

        Assert.IsTrue(refreshed.Wait(TimeSpan.FromSeconds(5)));
        await owner.WaitForRefreshQueuesIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        lock (requests)
        {
            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual(0L, requests[0].KeywordFilterRevision);
        }
    }

    [TestMethod]
    public void WorkflowOwner_ExecuteViewRejectsStaleRequestBeforeCallingCompositionDependencies()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest active = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        bool dependencyCalled = false;
        owner.ConfigureViewExecution(CreateViewExecutionDependencies(
            () =>
            {
                dependencyCalled = true;
                throw new AssertFailedException("A stale request must not resolve its source.");
            }));

        PlayHistoryViewExecutionResult result = owner.ExecuteView(
            new PlayHistoryViewExecutionRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.PlayHistorySelected,
                parameter: null,
                Stopwatch.StartNew(),
                new PlayHistoryViewRequest(PlayHistoryPeriodRequest.All(), active.RequestId + 1),
                keywordFilter: string.Empty,
                PlayHistoryDisplayTargetItem.All));

        Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, result.Status);
        Assert.IsFalse(dependencyCalled);
    }

    [TestMethod]
    public void WorkflowOwner_ExecuteViewUsesExplicitActiveRequest()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.ConfigureViewExecution(CreateViewExecutionDependencies());
        PlayHistoryPeriodRequest period = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.Today,
            new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        PlayHistoryViewRequest active = owner.BeginRequest(
            period,
            PlaylistRequestFactory.NormalizeKeywordFilter("  title  "),
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);

        PlayHistoryViewExecutionResult result = owner.ExecuteView(
            new PlayHistoryViewExecutionRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.SortUpdated,
                parameter: null,
                Stopwatch.StartNew(),
                active,
                keywordFilter: "  title  ",
                PlayHistoryDisplayTargetItem.All));

        Assert.AreEqual(PlayHistoryViewExecutionStatus.NoCurrentMatchingState, result.Status);
        Assert.AreEqual(period.Kind, active.PeriodRequest.Kind);
        Assert.AreEqual(owner.CurrentRequestId, active.RequestId);
        Assert.AreEqual(
            PlaylistRequestFactory.NormalizeKeywordFilter("  title  "),
            owner.CurrentKeywordIdentity);

        PlayHistoryViewExecutionResult existingRequestResult = owner.ExecuteView(
            new PlayHistoryViewExecutionRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.SortUpdated,
                parameter: active,
                Stopwatch.StartNew(),
                active,
                keywordFilter: "ignored",
                PlayHistoryDisplayTargetItem.All));

        Assert.AreEqual(PlayHistoryViewExecutionStatus.NoCurrentMatchingState, existingRequestResult.Status);
        Assert.AreEqual(active.RequestId, owner.CurrentRequestId);
    }

    [TestMethod]
    public void WorkflowOwner_ExecuteViewKeywordFilterUpdatedReusesCurrentProjectionWithoutReadingSource()
    {
        var owner = new PlayHistoryWorkflowOwner();
        var detailSourceRetirement = new PlaylistSourceRetirementRequest(
            requestVersion: 1,
            buildCancellation: null);
        PlayHistoryViewRequest active = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0,
            detailSourceRetirement);
        PlayHistoryRow row = CreateProjectedRow(HashA);
        IReadOnlyList<PlayHistoryRow> allProjectedRows = [row];
        IReadOnlyList<PlayHistoryRow> filterSourceRows = [row];
        owner.PresentationState.CurrentView = new PlayHistoryViewState(
            active.RequestId,
            active.PeriodRequest,
            allProjectedRows,
            filterSourceRows,
            [row],
            diagnostics: [],
            PlayHistoryProvider.Lr2,
            schemaStatus: default,
            sourceCount: 1,
            new SortSnapshot(null, null, revision: 0),
            keywordFilter: string.Empty,
            keywordFilterRevision: owner.KeywordRevision,
            PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: owner.DisplayTargetRevision);
        Assert.IsTrue(owner.TryBeginKeywordRefresh(
            "title:missing",
            advanceRevision: true,
            out PlayHistoryViewRequest keywordRequest));
        Assert.AreSame(detailSourceRetirement, keywordRequest.DetailSourceRetirement);

        int presentationInvocationCount = 0;
        owner.ConfigureViewExecution(CreateViewExecutionDependencies(
            () => throw new AssertFailedException("A keyword-only refresh must not resolve its source."),
            _ =>
            {
                presentationInvocationCount++;
                return false;
            }));

        PlayHistoryViewExecutionResult result;
        try
        {
            result = owner.ExecuteView(
                new PlayHistoryViewExecutionRequest(
                    MainViewUpdateMode.PlayHistorySelected,
                    MainViewUpdateMode.KeywordFilterUpdated,
                    parameter: null,
                    Stopwatch.StartNew(),
                    keywordRequest,
                    keywordFilter: "title:missing",
                    PlayHistoryDisplayTargetItem.All));
        }
        finally
        {
            owner.CompleteKeywordRefresh(keywordRequest.KeywordFilterRevision);
        }

        Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, result.Status);
        Assert.IsTrue(result.FromSortOnly);
        Assert.AreEqual(1, presentationInvocationCount);
        Assert.IsNull(result.ReadResult);
        Assert.AreEqual(0L, result.ReadMs);
        Assert.AreEqual(0L, result.ProjectionIndexMs);
        Assert.IsFalse(result.ProjectionIndexCacheHit);
        Assert.AreEqual(0, result.ProjectionIndexStaleRetries);
        Assert.AreEqual(0L, result.ProjectionMs);
        Assert.AreEqual(0L, result.PeriodIndexMs);
        Assert.IsNotNull(result.PresentationOnlyResult);
        Assert.AreEqual(PlayHistoryPresentationOnlyBuildStatus.Built, result.PresentationOnlyResult.Status);
        Assert.IsTrue(result.PresentationOnlyResult.KeywordFilterApplied);
        Assert.AreEqual(keywordRequest.KeywordFilterRevision, result.PresentationOnlyResult.State.KeywordFilterRevision);
        Assert.AreEqual(owner.DisplayTargetRevision, result.PresentationOnlyResult.State.DisplayTargetRevision);
        Assert.AreSame(row, result.PresentationOnlyResult.State.AllProjectedRows.Single());
        Assert.AreSame(row, result.PresentationOnlyResult.State.FilterSourceRows.Single());
        Assert.AreEqual(0, result.PresentationOnlyResult.State.ProjectedRows.Count);
    }

    [TestMethod]
    public async Task WorkflowOwner_ExecuteViewRequeuesLatestDisplayTargetAfterPresentationStale()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest active = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        PlayHistoryViewState state = new(
            active.RequestId,
            active.PeriodRequest,
            [],
            [],
            [],
            [],
            PlayHistoryProvider.Lr2,
            default,
            sourceCount: 0,
            new SortSnapshot(null, null, revision: 0),
            string.Empty,
            owner.KeywordRevision,
            PlayHistoryDisplayTargetItem.All,
            owner.DisplayTargetRevision);
        owner.PresentationState.CurrentView = state;

        PlayHistoryDisplayTargetItem latestTarget = PlayHistoryDisplayTargetItem.FromTargetSet(
            new PlayHistoryDisplayTargetSet { Name = "latest-target" });
        owner.AdvanceDisplayTargetRevision(latestTarget.Identity);
        using var refreshed = new ManualResetEventSlim();
        PlayHistoryViewRequest? refreshRequest = null;
        owner.ConfigureViewRefreshScheduler(
            () => false,
            request =>
            {
                refreshRequest = request;
                refreshed.Set();
            });
        owner.ConfigureViewExecution(CreateViewExecutionDependencies());

        PlayHistoryViewExecutionResult result = owner.ExecuteView(
            new PlayHistoryViewExecutionRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.SortUpdated,
                parameter: null,
                Stopwatch.StartNew(),
                active,
                keywordFilter: string.Empty,
                PlayHistoryDisplayTargetItem.All));

        Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, result.Status);
        Assert.IsTrue(refreshed.Wait(TimeSpan.FromSeconds(5)));
        await owner.WaitForRefreshQueuesIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(refreshRequest);
        Assert.AreEqual(owner.DisplayTargetRevision, refreshRequest.DisplayTargetRevision);
        Assert.AreEqual(latestTarget.Identity, owner.CurrentDisplayTargetIdentity);
    }

    [TestMethod]
    public void WorkflowOwner_ExecuteViewReturnsStaleWhenSynchronousPresentationDispatchIsUnavailable()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest active = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        owner.PresentationState.CurrentView = new PlayHistoryViewState(
            active.RequestId,
            active.PeriodRequest,
            [],
            [],
            [],
            [],
            PlayHistoryProvider.Lr2,
            default,
            sourceCount: 0,
            new SortSnapshot(null, null, revision: 0),
            string.Empty,
            owner.KeywordRevision,
            PlayHistoryDisplayTargetItem.All,
            owner.DisplayTargetRevision);
        owner.ConfigureViewExecution(CreateViewExecutionDependencies(invokePresentation: _ => false));

        PlayHistoryViewExecutionResult result = owner.ExecuteView(
            new PlayHistoryViewExecutionRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.SortUpdated,
                parameter: null,
                Stopwatch.StartNew(),
                active,
                keywordFilter: string.Empty,
                PlayHistoryDisplayTargetItem.All));

        Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, result.Status);
    }

    private static PlayHistoryViewExecutionDependencies CreateViewExecutionDependencies(
        Func<PlayHistoryReadSourceContext>? sourceResolver = null,
        Func<Action, bool>? invokePresentation = null)
    {
        return new PlayHistoryViewExecutionDependencies(
            () => null,
            () => null,
            sourceResolver ?? (() => PlayHistoryReadSourceContext.Lr2(string.Empty, isLr2LinkedProfile: false)),
            invokePresentation ?? (action =>
            {
                action();
                return true;
            }),
            (_, _) => { },
            () => MainViewUpdateMode.PlayHistorySelected,
            new MainChartListViewModel(),
            new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
    }

    [TestMethod]
    public void WorkflowOwner_OwnsDisplayTargetCatalogRefreshScheduling()
    {
        var owner = new PlayHistoryWorkflowOwner();
        var scheduled = new Queue<Action>();
        bool shutdown = false;
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => shutdown,
            () => [],
            action => scheduled.Enqueue(action));

        owner.QueueDisplayTargetCatalogRefresh();
        owner.QueueDisplayTargetCatalogRefresh(queueRefreshWhenSelectionChanges: false);

        Assert.AreEqual(1, scheduled.Count);
        Assert.IsFalse(owner.IsDisplayTargetCatalogRefreshIdle);

        scheduled.Dequeue()();

        Assert.IsTrue(owner.IsDisplayTargetCatalogRefreshIdle);
        Assert.AreEqual(1, owner.DisplayTargets.Count);
        Assert.AreEqual(PlayHistoryDisplayTargetItem.All.Identity, owner.SelectedDisplayTarget.Identity);

        owner.QueueDisplayTargetCatalogRefresh();
        shutdown = true;
        scheduled.Dequeue()();

        Assert.IsTrue(owner.IsDisplayTargetCatalogRefreshIdle);
        StringAssert.Contains(owner.DescribeDisplayTargetCatalogRefresh(), "displayTargetsRequestedRevision=");
    }

    [TestMethod]
    public void WorkflowOwner_RefreshDisplayTargetCatalogUsesConfiguredSnapshotProvider()
    {
        var owner = new PlayHistoryWorkflowOwner();
        int snapshotCount = 0;
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () =>
            {
                snapshotCount++;
                return [new BMSTable { name = "Configured" }];
            },
            _ => { });

        owner.RefreshDisplayTargetCatalog(queueRefreshWhenSelectionChanges: false);

        Assert.AreEqual(1, snapshotCount);
        Assert.IsTrue(owner.DisplayTargets.Any(item => item.Kind == PlayHistoryDisplayTargetKind.Playlist && item.Table?.name == "Configured"));
    }

    [TestMethod]
    public void WorkflowOwner_RefreshDisplayTargetSetsFromSettingsSnapshotsBeforeMutation()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.ReplaceDisplayTargetCatalog([], queueRefreshWhenSelectionChanges: false);
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () => throw new InvalidOperationException("snapshot failed"),
            _ => { });

        Assert.ThrowsException<InvalidOperationException>(() => owner.RefreshDisplayTargetSetsFromSettings(
            PlayHistoryDisplayTargetSetStore.Serialize(
            [
                new PlayHistoryDisplayTargetSet { Name = "Configured" }
            ]),
            queueRefreshWhenSelectionChanges: false));
        Assert.AreEqual(1, owner.DisplayTargets.Count);
        Assert.AreEqual(PlayHistoryDisplayTargetItem.All.Identity, owner.SelectedDisplayTarget.Identity);
    }

    [TestMethod]
    public void WorkflowOwner_RefreshDisplayTargetSetsFromSettingsInvalidJsonPreservesState()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.ReplaceDisplayTargetSets(
            [new PlayHistoryDisplayTargetSet
            {
                Name = "Existing",
                Targets = [new PlayHistoryDisplayTargetReference { PlaylistId = 1 }]
            }],
            [],
            queueRefreshWhenSelectionChanges: false);
        owner.ConfigureDisplayTargetPersistence(_ => { });
        PlayHistoryDisplayTargetItem selected = owner.DisplayTargets.First(item => item.Kind == PlayHistoryDisplayTargetKind.TargetSet);
        owner.SelectedDisplayTarget = selected;
        string[] displayTargetIdentities = owner.DisplayTargets.Select(item => item.Identity).ToArray();
        string selectedDisplayTargetIdentity = owner.SelectedDisplayTarget.Identity;
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () => [],
            _ => { });

        Assert.ThrowsException<InvalidOperationException>(() => owner.RefreshDisplayTargetSetsFromSettings(
            "not-json",
            queueRefreshWhenSelectionChanges: false));

        CollectionAssert.AreEqual(displayTargetIdentities, owner.DisplayTargets.Select(item => item.Identity).ToArray());
        Assert.AreEqual(selectedDisplayTargetIdentity, owner.SelectedDisplayTarget.Identity);
    }
}
