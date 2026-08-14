using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class InstallDestinationStateOwnerTests
{
    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_PrefersHashAndPreservesProjection()
    {
        string path = "C:\\Library\\Chart.bms";
        var pathCandidate = CreateBms(path, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var hashCandidate = CreateBms(path.ToLowerInvariant(), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        storageRowsOwner.ReplaceBmsRows([pathCandidate, hashCandidate]);
        var owner = new InstallDestinationStateOwner(storageRowsOwner, () => []);
        ChartFile detached = CreateDetachedBmsChart(
            path,
            hashCandidate.hash,
            "C:\\Install\\Chart",
            "Overlay title");

        ChartFile reattached = owner.ReattachFileScanResidualInstallDestinationCharts([detached]).Single();

        Assert.AreSame(hashCandidate, reattached.GetBmsStorageOwner());
        Assert.AreEqual("C:\\Install\\Chart", reattached.InstallDestination);
        Assert.AreEqual("Overlay title", reattached.InstallDestinationTitle);
    }

    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_RejectsAmbiguousPathAndCrossKindLeakage()
    {
        string path = "C:\\Library\\Chart.bms";
        var first = CreateBms(path, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var second = CreateBms(path, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = path,
            md5 = "cccccccccccccccccccccccccccccccc"
        };
        var storageRowsOwner = new CatalogStorageRowsOwner();
        storageRowsOwner.ReplaceBmsRows([first, second]);
        storageRowsOwner.ReplaceBmsonRows([bmson]);
        var owner = new InstallDestinationStateOwner(storageRowsOwner, () => []);

        ChartFile ambiguousBms = CreateDetachedBmsChart(path, "dddddddddddddddddddddddddddddddd", "C:\\Install\\Bms", "");
        ChartFile sameHashBms = CreateDetachedBmsChart(path, bmson.md5, "C:\\Install\\BmsHash", "");

        IReadOnlyList<ChartFile> reattached = owner.ReattachFileScanResidualInstallDestinationCharts(
            [ambiguousBms, sameHashBms]);

        Assert.AreEqual(0, reattached.Count);
    }

    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_ReattachesBmsonOwner()
    {
        string path = "C:\\Library\\Chart.bmson";
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = path,
            md5 = "cccccccccccccccccccccccccccccccc"
        };
        var storageRowsOwner = new CatalogStorageRowsOwner();
        storageRowsOwner.ReplaceBmsonRows([bmson]);
        var owner = new InstallDestinationStateOwner(storageRowsOwner, () => []);
        ChartFile detached = CreateDetachedBmsonChart(path, bmson.md5, "C:\\Install\\Bmson");

        ChartFile reattached = owner.ReattachFileScanResidualInstallDestinationCharts([detached]).Single();

        Assert.AreSame(bmson, reattached.GetBmsonStorageOwner());
        Assert.AreEqual("C:\\Install\\Bmson", reattached.InstallDestination);
    }

    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_DropsOwnerlessSnapshot()
    {
        var storageRowsOwner = new CatalogStorageRowsOwner();
        var owner = new InstallDestinationStateOwner(storageRowsOwner, () => []);
        ChartFile detached = CreateDetachedBmsChart(
            "C:\\Library\\Missing.bms",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "C:\\Install\\Missing",
            "");

        IReadOnlyList<ChartFile> reattached = owner.ReattachFileScanResidualInstallDestinationCharts([detached]);

        Assert.AreEqual(0, reattached.Count);
    }

    private static ChartFile CreateDetachedBmsChart(
        string path,
        string md5,
        string installDestination,
        string installDestinationTitle)
    {
        var source = CreateBms(path, md5);
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(source, includeWarningSnapshot: false, includeResourceReferences: false),
            installDestination,
            installDestinationTitle,
            "Overlay artist",
            [],
            []);
        return ChartFileProjection.ToImmutableSnapshot(chart);
    }

    private static ChartFile CreateDetachedBmsonChart(string path, string md5, string installDestination)
    {
        var source = new LR2SongDBExtended.bmson_song
        {
            path = path,
            md5 = md5
        };
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(source, includeWarningSnapshot: false, includeResourceReferences: false),
            installDestination,
            string.Empty,
            string.Empty,
            [],
            []);
        return ChartFileProjection.ToImmutableSnapshot(chart);
    }

    private static TestableBmsFile CreateBms(string path, string hash)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        return file;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void SetHash(string value)
        {
            hash = value;
        }
    }
}
