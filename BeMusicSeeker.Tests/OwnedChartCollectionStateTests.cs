using System.Collections.Generic;
using System.IO;
using System.Linq;
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
