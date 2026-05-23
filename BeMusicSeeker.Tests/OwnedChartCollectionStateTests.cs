using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionStateTests
{
    [TestMethod]
    public void FromStorageRows_BuildsBmsAndBmsonOwnedChartSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('d', 64),
            title = "bmson title"
        };

        List<ChartFile> snapshot = OwnedChartCollectionState
            .FromStorageRows([bmsFile], [bmsonSong])
            .CreateSnapshot(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false);

        Assert.AreEqual(2, snapshot.Count);
        ChartFile bmsChart = snapshot.Single(chart => chart.Kind == ChartFileKind.Bms);
        ChartFile bmsonChart = snapshot.Single(chart => chart.Kind == ChartFileKind.Bmson);
        Assert.AreEqual(bmsFile.path, bmsChart.Path);
        Assert.AreEqual(bmsFile.hash, bmsChart.Md5);
        Assert.AreEqual(bmsFile.sha256, bmsChart.Sha256);
        Assert.AreSame(bmsFile, bmsChart.GetBmsStorageOwner());
        Assert.AreEqual(bmsonSong.path, bmsonChart.Path);
        Assert.AreEqual(bmsonSong.md5, bmsonChart.Md5);
        Assert.AreEqual(bmsonSong.sha256, bmsonChart.Sha256);
        Assert.AreSame(bmsonSong, bmsonChart.GetBmsonStorageOwner());
    }

    [TestMethod]
    public void CreateSnapshot_MatchesStorageRowsProjection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('d', 64),
            title = "bmson title"
        };

        List<ChartFile> ownedSnapshot = OwnedChartCollectionState
            .FromStorageRows([bmsFile], [bmsonSong])
            .CreateSnapshot(includeWarningSnapshot: false, includeResourceReferences: true, includeScoreSnapshot: false);
        List<ChartFile> projectionSnapshot = ChartFileProjection.FromStorageRows(
            [bmsFile],
            [bmsonSong],
            includeWarningSnapshot: false,
            includeResourceReferences: true,
            includeScoreSnapshot: false);

        AssertChartSnapshotParity(projectionSnapshot, ownedSnapshot);
    }

    [TestMethod]
    public void MatchesStorageRows_DetectsSameCountReplacementAndReorder()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
        var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
        var replacement = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Replacement", "chart.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, second], []);

        Assert.IsTrue(state.MatchesStorageRows([first, second], []));
        Assert.IsFalse(state.MatchesStorageRows([first, replacement], []));
        Assert.IsFalse(state.MatchesStorageRows([second, first], []));
    }

    [TestMethod]
    public void CreateSnapshot_ReprojectsCurrentStorageOwnerValues()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Old", "chart.bms"), new string('b', 64));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], []);
        string newPath = Path.Combine("C:\\Installed", "New", "chart.bms");
        bmsFile.path = newPath;
        bmsFile.SetHash("cccccccccccccccccccccccccccccccc");
        bmsFile.SetSha256(new string('d', 64));

        ChartFile snapshot = state.CreateSnapshot(
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: false).Single();

        Assert.AreEqual(newPath, snapshot.Path);
        Assert.AreEqual(bmsFile.hash, snapshot.Md5);
        Assert.AreEqual(bmsFile.sha256, snapshot.Sha256);
        Assert.AreSame(bmsFile, snapshot.GetBmsStorageOwner());
    }

    [TestMethod]
    public void RemoveCharts_UpdateOwnedCollectionMembership()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
        var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
            md5 = "cccccccccccccccccccccccccccccccc"
        };
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, second], [bmsonSong]);

        Assert.AreEqual(2, state.RemoveCharts([
            ChartFileProjection.FromBmsStorageOwnerIdentity(first),
            ChartFileProjection.FromBmsonStorageOwnerIdentity(bmsonSong)
        ]));
        List<ChartFile> afterRemove = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(1, afterRemove.Count);
        Assert.AreSame(second, afterRemove[0].GetBmsStorageOwner());
    }

    [TestMethod]
    public void RemoveCharts_PathFallbackDoesNotCrossChartKind()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = sharedPath,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([bmsFile], [bmsonSong]);
        var bmsPathOnlyChart = new ChartFile(
            ChartFileKind.Bms,
            sharedPath,
            bmsFile.hash,
            null,
            "title",
            "raw",
            "artist",
            string.Empty,
            Path.GetDirectoryName(sharedPath),
            string.Empty,
            string.Empty,
            null,
            0,
            null,
            null,
            null);

        Assert.AreEqual(1, state.RemoveCharts([bmsPathOnlyChart]));
        List<ChartFile> snapshot = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(bmsonSong, snapshot[0].GetBmsonStorageOwner());
    }

    [TestMethod]
    public void RemoveCharts_PathFallbackMatchesSameKindDuplicateRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string sharedPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
        var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath);
        var samePath = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath);
        var other = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"));
        OwnedChartCollectionState state = OwnedChartCollectionState.FromStorageRows([first, samePath, other], []);

        Assert.AreEqual(2, state.RemoveCharts([ChartFileProjection.FromBmsStorageOwnerIdentity(first)]));
        List<ChartFile> snapshot = state.CreateSnapshot(includeResourceReferences: false);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreSame(other, snapshot[0].GetBmsStorageOwner());
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterKeepsOwnedCollectionInitializedAndSynced()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [first, second],
                BmsonSongs = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateInstalledChartSnapshot(library, includeResourceReferences: false);
            Assert.AreEqual(2, initialSnapshot.Count);
            Assert.IsTrue(IsOwnedChartCollectionInitialized(library));
            var delta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true
            };
            delta.ChartsToUnregister.Add(initialSnapshot[0]);

            InvokeApplyLibraryMutationDelta(library, delta);

            Assert.IsTrue(IsOwnedChartCollectionInitialized(library));
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreSame(second, library.BMSFiles[0]);
            List<ChartFile> afterSnapshot = InvokeCreateInstalledChartSnapshot(library, includeResourceReferences: false);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second, afterSnapshot[0].GetBmsStorageOwner());
        });
    }

    private static void AssertChartSnapshotParity(IReadOnlyList<ChartFile> expected, IReadOnlyList<ChartFile> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].Kind, actual[i].Kind);
            Assert.AreEqual(expected[i].Path, actual[i].Path);
            Assert.AreEqual(expected[i].Md5, actual[i].Md5);
            Assert.AreEqual(expected[i].Sha256, actual[i].Sha256);
            Assert.AreSame(expected[i].GetBmsStorageOwner(), actual[i].GetBmsStorageOwner());
            Assert.AreSame(expected[i].GetBmsonStorageOwner(), actual[i].GetBmsonStorageOwner());
        }
    }

    private static List<ChartFile> InvokeCreateInstalledChartSnapshot(BMSLibrary library, bool includeResourceReferences)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CreateInstalledChartSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (List<ChartFile>)methodInfo.Invoke(library, [library.BMSFiles, library.BmsonSongs, includeResourceReferences]);
    }

    private static void InvokeApplyLibraryMutationDelta(BMSLibrary library, LibraryMutationDelta delta)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("ApplyLibraryMutationDelta", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [delta]);
    }

    private static bool IsOwnedChartCollectionInitialized(BMSLibrary library)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField("ownedChartCollectionInitialized", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        return (bool)fieldInfo.GetValue(library);
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_OwnedChartCollection_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
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

    private static TestableBmsFile CreateFile(string hash, string path, string? sha256 = null)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        file.SetSha256(sha256);
        return file;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string? value)
        {
            sha256 = value;
        }
    }
}
