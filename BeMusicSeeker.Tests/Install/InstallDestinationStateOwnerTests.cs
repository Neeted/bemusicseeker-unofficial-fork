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
    public void ReattachFileScanResidualInstallDestinationCharts_UsesExactPathWhenAliasHashMatches()
    {
        string path = "C:\\Library\\Chart.bms";
        TestableBmsFile pathCandidate = CreateBms(path, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        TestableBmsFile hashCandidate = CreateBms(path.ToLowerInvariant(), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        storageRowsOwner.ReplaceBmsRows([pathCandidate, hashCandidate]);
        var owner = new InstallDestinationStateOwner(storageRowsOwner, () => []);
        ChartFile detached = CreateDetachedBmsChart(
            path,
            hashCandidate.hash,
            "C:\\Install\\Chart",
            "Overlay title");

        ChartFile reattached = owner.ReattachFileScanResidualInstallDestinationCharts([detached]).Single();

        Assert.AreSame(pathCandidate, reattached.GetBmsStorageOwner());
        Assert.AreEqual("C:\\Install\\Chart", reattached.InstallDestination);
        Assert.AreEqual("Overlay title", reattached.InstallDestinationTitle);
    }

    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_RejectsAmbiguousPathAndCrossKindLeakage()
    {
        string path = "C:\\Library\\Chart.bms";
        TestableBmsFile first = CreateBms(path, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        TestableBmsFile second = CreateBms(path, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
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
    public void OverlayRuntimeStates_UsesExactPathAndCaseInsensitiveHash()
    {
        string path = "C:\\Library\\Chart.bms";
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        TestableBmsFile storageOwner = CreateBms(path, hash);
        var storageRowsOwner = new CatalogStorageRowsOwner();
        storageRowsOwner.ReplaceBmsRows([storageOwner]);
        var owner = new InstallDestinationStateOwner(storageRowsOwner, () => []);
        var mutation = new InstallDestinationRuntimeStateMutation();
        mutation.AppliedCharts.Add(CreateOwnedBmsChart(storageOwner, "C:\\Install\\Exact"));
        owner.Apply(mutation);

        ChartFile exactChart = ChartFileProjection.FromBmsFile(
            CreateBms(path, hash.ToUpperInvariant()),
            includeWarningSnapshot: false,
            includeResourceReferences: false);
        ChartFile aliasChart = ChartFileProjection.FromBmsFile(
            CreateBms(path.ToLowerInvariant(), hash),
            includeWarningSnapshot: false,
            includeResourceReferences: false);

        ChartFile exactOverlay = owner.OverlayRuntimeStates([exactChart]).Single();
        ChartFile aliasOverlay = owner.OverlayRuntimeStates([aliasChart]).Single();

        Assert.AreEqual("C:\\Install\\Exact", exactOverlay.InstallDestination);
        Assert.IsTrue(string.IsNullOrWhiteSpace(aliasOverlay.InstallDestination));
    }

    [TestMethod]
    public void CreateOverlaySnapshot_PreservesCaseOnlyRowsWithSameHash()
    {
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        TestableBmsFile first = CreateBms("C:\\Library\\Chart.bms", hash);
        TestableBmsFile second = CreateBms("C:\\Library\\chart.bms", hash);
        var storageRowsOwner = new CatalogStorageRowsOwner();
        storageRowsOwner.ReplaceBmsRows([first, second]);
        var owner = new InstallDestinationStateOwner(storageRowsOwner, () => []);
        var mutation = new InstallDestinationRuntimeStateMutation();
        mutation.AppliedCharts.Add(CreateOwnedBmsChart(first, "C:\\Install\\First"));
        mutation.AppliedCharts.Add(CreateOwnedBmsChart(second, "C:\\Install\\Second"));
        owner.Apply(mutation);

        InstallDestinationOverlayChartRefSnapshot snapshot = owner.CreateOverlaySnapshot(out bool wasCached);

        Assert.IsFalse(wasCached);
        Assert.AreEqual(2, snapshot.ChartCount);
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

    private static ChartFile CreateOwnedBmsChart(BMSFile owner, string installDestination)
    {
        return ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(owner, includeWarningSnapshot: false, includeResourceReferences: false),
            installDestination,
            string.Empty,
            string.Empty,
            [],
            []);
    }

    private static ChartFile CreateDetachedBmsChart(
        string path,
        string md5,
        string installDestination,
        string installDestinationTitle)
    {
        TestableBmsFile source = CreateBms(path, md5);
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
