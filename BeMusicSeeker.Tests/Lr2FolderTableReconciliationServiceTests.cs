using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderTableReconciliationServiceTests
{
    [TestMethod]
    public void BuildProjection_ContainsEverySourceAndRequiredParentRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string root = Path.Combine(scope.DirectoryPath, "Root");
        string normal = Path.Combine(root, "Normal");
        string info = Path.Combine(root, "Info");
        string custom = Path.Combine(root, "Custom");
        Directory.CreateDirectory(normal);
        Directory.CreateDirectory(info);
        Directory.CreateDirectory(custom);
        string chart = Path.Combine(normal, "chart.bms");
        File.WriteAllText(chart, "#TITLE Chart\r\n", Encoding.ASCII);
        string folderInfo = Path.Combine(info, "folderinfo.txt");
        File.WriteAllText(folderInfo, "#TITLE Physical title\r\n", Encoding.ASCII);
        string lr2Folder = Path.Combine(custom, "custom.lr2folder");
        File.WriteAllText(lr2Folder, "#TITLE Generated title\r\n#MAXTRACKS 12\r\n", Encoding.ASCII);

        DateTime rootTime = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
        DateTime normalTime = rootTime.AddMinutes(1);
        DateTime infoTime = rootTime.AddMinutes(2);
        DateTime customTime = rootTime.AddMinutes(3);
        DateTime lr2Time = rootTime.AddMinutes(4);
        Lr2SongDbSyncRequest request = CreateRequest(
            root,
            normal,
            info,
            custom,
            chart,
            folderInfo,
            lr2Folder,
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
            {
                [root] = rootTime,
                [normal] = normalTime,
                [info] = infoTime,
                [custom] = customTime
            },
            lr2Time,
            generatedAtUtc: rootTime.AddHours(1));

        Lr2FolderTableProjection projection = Lr2FolderTableReconciliationService.BuildProjection(request);
        Dictionary<string, LR2SongDB.folder> rows = projection.Rows.ToDictionary(row => row.path, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(rows.ContainsKey(ToFolderPath(root)));
        Assert.IsTrue(rows.ContainsKey(ToFolderPath(normal)));
        Assert.IsTrue(rows.ContainsKey(ToFolderPath(info)));
        Assert.IsTrue(rows.ContainsKey(ToFolderPath(custom)));
        Assert.IsTrue(rows.ContainsKey(lr2Folder));
        Assert.AreEqual("Info title", rows[ToFolderPath(info)].title);
        Assert.AreEqual("Generated title", rows[lr2Folder].title);
        Assert.AreEqual(12, rows[lr2Folder].max);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(rootTime), rows[ToFolderPath(root)].date);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(lr2Time), rows[lr2Folder].date);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, rows[ToFolderPath(root)].parent);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(custom),
            rows[lr2Folder].parent);
        Assert.AreEqual(Lr2FolderTableProjectionSourceKind.FolderInfoDirectory, projection.SourceKinds[ToFolderPath(info)]);
        Assert.AreEqual(Lr2FolderTableProjectionSourceKind.Lr2FolderFile, projection.SourceKinds[lr2Folder]);
    }

    [TestMethod]
    public void BuildProjection_PriorityAndEqualDeduplicationAreOrderIndependentAndConflictsFail()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string root = Path.Combine(scope.DirectoryPath, "PriorityRoot");
        string info = Path.Combine(root, "Info");
        Directory.CreateDirectory(info);
        string folderInfo = Path.Combine(info, "folderinfo.txt");
        File.WriteAllText(folderInfo, "#TITLE Info title\r\n", Encoding.ASCII);
        DateTime directoryTime = new(2026, 6, 8, 2, 0, 0, DateTimeKind.Utc);

        Lr2SongDbSyncRequest first = CreateRequest(
            root,
            root,
            info,
            info,
            Path.Combine(info, "chart.bms"),
            folderInfo,
            null,
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
            {
                [root] = directoryTime,
                [info] = directoryTime.AddMinutes(1)
            },
            null,
            generatedAtUtc: directoryTime.AddHours(1));
        Lr2SongDbSyncRequest reversed = CreateRequest(
            root,
            info,
            root,
            info,
            Path.Combine(info, "chart.bms"),
            folderInfo,
            null,
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
            {
                [info] = directoryTime.AddMinutes(1),
                [root] = directoryTime
            },
            null,
            generatedAtUtc: directoryTime.AddHours(1));

        Lr2FolderTableProjection firstProjection = Lr2FolderTableReconciliationService.BuildProjection(first);
        Lr2FolderTableProjection reversedProjection = Lr2FolderTableReconciliationService.BuildProjection(reversed);
        CollectionAssert.AreEqual(
            firstProjection.Rows.Select(row => row.path).ToArray(),
            reversedProjection.Rows.Select(row => row.path).ToArray());
        Assert.AreEqual(
            "Info title",
            firstProjection.Rows.Single(row => string.Equals(row.path, ToFolderPath(info), StringComparison.OrdinalIgnoreCase)).title);

        string conflictPath = Path.Combine(scope.DirectoryPath, "conflict.lr2folder");
        File.WriteAllText(conflictPath, "#TITLE Conflict\r\n", Encoding.ASCII);
        string conflictAlias = Path.Combine(scope.DirectoryPath, ".", "conflict.lr2folder");
        DateTime firstTime = directoryTime;
        DateTime secondTime = directoryTime.AddMinutes(1);
        Lr2SongDbSyncRequest conflict = new()
        {
            Lr2FolderFilePaths = [conflictPath, conflictAlias],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [conflictPath] = new RootFileEnumerationEntry(conflictPath, firstTime),
                [conflictAlias] = new RootFileEnumerationEntry(conflictAlias, secondTime)
            },
            StartedAtUtc = directoryTime
        };

        Assert.ThrowsException<Lr2FolderTableProjectionConflictException>(
            () => Lr2FolderTableReconciliationService.BuildProjection(conflict));
        conflict.Lr2FolderFilePaths = [conflictAlias, conflictPath];
        Assert.ThrowsException<Lr2FolderTableProjectionConflictException>(
            () => Lr2FolderTableReconciliationService.BuildProjection(conflict));

        conflict.Lr2FolderFilePaths = [conflictPath, conflictPath];
        conflict.Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [conflictPath] = new RootFileEnumerationEntry(conflictPath, firstTime)
        };
        Assert.AreEqual(1, Lr2FolderTableReconciliationService.BuildProjection(conflict).Rows.Count);
    }

    [TestMethod]
    public void Reconcile_IncompleteOrLatePreparationFailureLeavesExistingRowsUnchanged()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        string stalePath = ToFolderPath(Path.Combine(scope.DirectoryPath, "stale"));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = stalePath,
            title = "Keep on failure",
            date = 4
        }, typeof(LR2SongDB.folder));

        Assert.ThrowsException<Lr2FolderTableProjectionIncompleteException>(() =>
            Lr2FolderTableReconciliationService.Reconcile(songDb, new Lr2SongDbSyncRequest
            {
                Lr2FolderFileDiscoveryComplete = false
            }));
        Assert.AreEqual("Keep on failure", songDb.Find<LR2SongDB.folder>(stalePath).title);

        string root = Path.Combine(scope.DirectoryPath, "LateRoot");
        Directory.CreateDirectory(root);
        string folderInfo = Path.Combine(root, "folderinfo.txt");
        File.WriteAllText(folderInfo, "#TITLE Late failure\r\n", Encoding.ASCII);
        DateTime rootTime = new(2026, 6, 8, 3, 0, 0, DateTimeKind.Utc);
        Assert.ThrowsException<Lr2FolderTableProjectionIncompleteException>(() =>
            Lr2FolderTableReconciliationService.Reconcile(songDb, new Lr2SongDbSyncRequest
            {
                RootDirectories = [root],
                NormalFolderDirectoryPaths = [root],
                FolderInfoFilePaths = [folderInfo],
                DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [root] = new RootFileEnumerationEntry(root, rootTime)
                },
                FolderInfoLinesReader = _ => throw new IOException("late folderinfo fault"),
                StartedAtUtc = rootTime
            }));
        Assert.AreEqual("Keep on failure", songDb.Find<LR2SongDB.folder>(stalePath).title);
        Assert.IsNull(songDb.Find<LR2SongDB.folder>(ToFolderPath(root)));
    }

    [TestMethod]
    public void Reconcile_PreservesZeroAndNegativeDatesAndDeletesUnexpectedRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string root = Path.Combine(scope.DirectoryPath, "DateRoot");
        Directory.CreateDirectory(root);
        string folderPath = Path.Combine(root, "date.lr2folder");
        File.WriteAllText(folderPath, "#TITLE Date folder\r\n", Encoding.ASCII);
        string unexpectedPath = ToFolderPath(Path.Combine(scope.DirectoryPath, "unexpected"));
        DateTime epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime beforeEpoch = epoch.AddDays(-1);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = unexpectedPath,
            title = "Unexpected",
            date = 10
        }, typeof(LR2SongDB.folder));

        Lr2FolderTableReconciliationResult result = Lr2FolderTableReconciliationService.Reconcile(songDb, new Lr2SongDbSyncRequest
        {
            RootDirectories = [root],
            NormalFolderDirectoryPaths = [root],
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [root] = new RootFileEnumerationEntry(root, beforeEpoch)
            },
            Lr2FolderFilePaths = [folderPath],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [folderPath] = new RootFileEnumerationEntry(folderPath, epoch)
            },
            StartedAtUtc = new DateTime(2026, 6, 8, 4, 0, 0, DateTimeKind.Utc)
        });

        Assert.IsTrue(result.HasChanges);
        Assert.AreEqual(-86400, songDb.ExecuteScalar<int>("SELECT date FROM folder WHERE path = ?;", ToFolderPath(root)));
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT date FROM folder WHERE path = ?;", folderPath));
        Assert.IsNull(songDb.Find<LR2SongDB.folder>(unexpectedPath));
    }

    [TestMethod]
    public void Reconcile_PreservesExistingFolderAddDateAcrossWholeTableApply()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string root = Path.Combine(scope.DirectoryPath, "AddDateRoot");
        Directory.CreateDirectory(root);
        DateTime rootTime = new(2026, 6, 8, 5, 0, 0, DateTimeKind.Utc);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        string rootRowPath = ToFolderPath(root);
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = rootRowPath,
            title = "Old title",
            date = 1,
            adddate = 1234
        }, typeof(LR2SongDB.folder));

        Lr2FolderTableReconciliationResult result = Lr2FolderTableReconciliationService.Reconcile(songDb, new Lr2SongDbSyncRequest
        {
            RootDirectories = [root],
            NormalFolderDirectoryPaths = [root],
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [root] = new RootFileEnumerationEntry(root, rootTime)
            },
            StartedAtUtc = rootTime.AddHours(1)
        });

        Assert.AreEqual(1, result.ExistingRowCount);
        Assert.AreEqual(1234, songDb.ExecuteScalar<int>("SELECT adddate FROM folder WHERE path = ?;", rootRowPath));
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(rootTime), songDb.ExecuteScalar<int>("SELECT date FROM folder WHERE path = ?;", rootRowPath));
        Assert.AreEqual("AddDateRoot", songDb.ExecuteScalar<string>("SELECT title FROM folder WHERE path = ?;", rootRowPath));
    }

    private static Lr2SongDbSyncRequest CreateRequest(
        string root,
        string firstDirectory,
        string secondDirectory,
        string thirdDirectory,
        string chart,
        string folderInfo,
        string? lr2Folder,
        IReadOnlyDictionary<string, DateTime> directoryTimes,
        DateTime? lr2Time,
        DateTime generatedAtUtc)
    {
        var request = new Lr2SongDbSyncRequest
        {
            RootDirectories = [root],
            ChartPaths = [chart],
            NormalFolderDirectoryPaths = [firstDirectory, secondDirectory, thirdDirectory],
            FolderInfoFilePaths = folderInfo == null ? [] : [folderInfo],
            DirectoryEntries = directoryTimes.ToDictionary(
                pair => pair.Key,
                pair => new RootFileEnumerationEntry(pair.Key, pair.Value),
                StringComparer.OrdinalIgnoreCase),
            FolderInfoLinesReader = _ => ["#TITLE Info title"],
            Lr2FolderFilePaths = lr2Folder == null ? [] : [lr2Folder],
            Lr2FolderFileEntries = lr2Folder == null
                ? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2Folder] = new RootFileEnumerationEntry(lr2Folder, lr2Time)
                },
            StartedAtUtc = generatedAtUtc
        };
        return request;
    }
}
