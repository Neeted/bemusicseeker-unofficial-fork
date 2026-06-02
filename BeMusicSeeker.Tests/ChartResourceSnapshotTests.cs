using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartResourceSnapshotTests
{
    [TestMethod]
    public void CreateAggregate_PreservesExtensionlessResourceKeysWithDotsInStem()
    {
        BMSFile file = CreateBmsFile(
            "C:\\Pending\\dot-stem.bms",
            ["4.org1_1.wav", "4.org1_2.wav", "5.bell_1.wav"],
            ["bg.final.png", "movie.opening.mpg"]);
        ChartFile chart = ChartFileProjection.FromBmsFile(
            file,
            includeWarningSnapshot: false,
            includeResourceReferences: true,
            includeScoreSnapshot: false);

        ChartResourceSnapshot single = ChartResourceSnapshot.Create(chart);
        ChartResourceSnapshot aggregate = ChartResourceSnapshot.CreateAggregate([chart]);

        Assert.AreEqual(3, single.AudioReferenceCount);
        Assert.AreEqual(1, single.VisualReferenceCount);
        Assert.AreEqual(1, single.MovieReferenceCount);
        Assert.AreEqual(single.AudioReferenceCount, aggregate.AudioReferenceCount);
        Assert.AreEqual(single.VisualReferenceCount, aggregate.VisualReferenceCount);
        Assert.AreEqual(single.MovieReferenceCount, aggregate.MovieReferenceCount);
        CollectionAssert.AreEquivalent(single.AudioRelativePaths.ToArray(), aggregate.AudioRelativePaths.ToArray());
        CollectionAssert.Contains(aggregate.AudioRelativePaths.ToArray(), "4.org1_1");
        CollectionAssert.Contains(aggregate.AudioRelativePaths.ToArray(), "4.org1_2");
        CollectionAssert.DoesNotContain(aggregate.AudioRelativePaths.ToArray(), "4");
        CollectionAssert.Contains(aggregate.VisualRelativePaths.ToArray(), "bg.final");
        CollectionAssert.DoesNotContain(aggregate.VisualRelativePaths.ToArray(), "bg");
        CollectionAssert.Contains(aggregate.MovieRelativePaths.ToArray(), "movie.opening");
        CollectionAssert.DoesNotContain(aggregate.MovieRelativePaths.ToArray(), "movie");
    }

    [TestMethod]
    public void GetLookupFileName_PreservesDotsInStem()
    {
        Assert.AreEqual("4.org1_1", ChartResourcePathNormalizer.GetLookupFileName(Path.Combine("sound", "4.org1_1.wav")));
        Assert.AreEqual("bg.final", ChartResourcePathNormalizer.GetLookupFileName(Path.Combine("visual", "bg.final.png")));
    }

    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_UsesPrecomputedDefinedResources()
    {
        PackageChartEntry targetEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(
            CreateBmsFile("C:\\Pending\\target.bms", ["target.wav"], []),
            includeWarningSnapshot: false,
            includeResourceReferences: true,
            includeScoreSnapshot: false));
        ChartResourceSnapshot precomputedResources = ChartResourceSnapshot.CreateAggregate([
            ChartFileProjection.FromBmsFile(
                CreateBmsFile("C:\\Pending\\precomputed.bms", ["precomputed.wav"], []),
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false)
        ]);

        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(
            ChartPackage.FromChartEntries([targetEntry]),
            [targetEntry],
            PackageInstallSurfaceSnapshot.Empty,
            sourceSurfaceCacheHit: false,
            definedResources: precomputedResources);

        Assert.AreSame(precomputedResources, snapshot.DefinedResources);
        CollectionAssert.Contains(snapshot.DefinedResources.AudioRelativePaths.ToArray(), "precomputed");
        CollectionAssert.DoesNotContain(snapshot.DefinedResources.AudioRelativePaths.ToArray(), "target");
    }

    private static BMSFile CreateBmsFile(string path, IEnumerable<string> wavFiles, IEnumerable<string> bgaFiles)
    {
        return new BMSFile
        {
            path = path,
            WAVfiles = new HashSet<string>(wavFiles ?? [], System.StringComparer.OrdinalIgnoreCase),
            BGAfiles = new HashSet<string>(bgaFiles ?? [], System.StringComparer.OrdinalIgnoreCase)
        };
    }
}
