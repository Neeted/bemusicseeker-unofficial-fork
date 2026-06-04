using System;
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
    public void NormalizeReferencePathForLookup_SkipsCurrentDirectorySegments()
    {
        Assert.AreEqual("foo", ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(@".\foo.wav"));
        Assert.AreEqual(Path.Combine("sound", "foo"), ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(@".\sound\foo.wav"));
        Assert.AreEqual(Path.Combine("sound", "foo"), ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(@"sound\.\foo.wav"));
    }

    [TestMethod]
    public void AnalyzeReferencePathForLookup_RejectsParentTraversalAsUnsupported()
    {
        ChartResourcePathNormalizationResult result = ChartResourcePathNormalizer.AnalyzeReferencePathForLookup(@"..\pkg\foo.wav");

        Assert.AreEqual(ChartResourcePathNormalizationStatus.ParentTraversalUnsupported, result.Status);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(string.Empty, result.NormalizedPath);
    }

    [TestMethod]
    public void CreateAggregate_PreservesUnsupportedParentTraversalReferencesWithoutLookupKeys()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "ChartResourceSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string chartPath = Path.Combine(tempDirectory, "parent-resource.bms");
        try
        {
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                + "#TITLE Parent Resource\r\n"
                + "#WAVAA ..\\Base\\sound.wav\r\n"
                + "#BMPAA ..\\Base\\movie.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:AA\r\n");
            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            ChartFile chart = ChartFileProjection.FromBmsFile(
                file,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false);

            ChartResourceSnapshot snapshot = ChartResourceSnapshot.CreateAggregate([chart]);

            Assert.AreEqual(0, snapshot.AudioReferenceCount);
            Assert.AreEqual(0, snapshot.MovieReferenceCount);
            Assert.AreEqual(2, snapshot.UnsupportedResourceReferenceCount);
            Assert.IsTrue(snapshot.HasUnsupportedParentTraversalReference);
            Assert.AreEqual(1, snapshot.UnsupportedResourceReferences.Count(reference => reference.Kind == ChartResourceKind.Audio));
            Assert.AreEqual(1, snapshot.UnsupportedResourceReferences.Count(reference => reference.Kind == ChartResourceKind.Movie));
            CollectionAssert.DoesNotContain(snapshot.AudioRelativePaths.ToArray(), Path.Combine("Base", "sound"));
            CollectionAssert.DoesNotContain(snapshot.MovieRelativePaths.ToArray(), Path.Combine("Base", "movie"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Create_PreservesBmsRawResourcePathsFromStorageOwner()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "ChartResourceSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string chartPath = Path.Combine(tempDirectory, "raw-resource.bms");
        try
        {
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                + "#TITLE Raw Resource\r\n"
                + "#WAVAA .\\sound\\kick.wav\r\n"
                + "#BMPAA visual\\bg.final.png\r\n"
                + "#BMPAB movie\\op.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:AB\r\n");
            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            ChartFile chart = ChartFileProjection.FromBmsFile(
                file,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false);

            ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(chart);
            ChartResourceSnapshot aggregate = ChartResourceSnapshot.CreateAggregate([chart]);

            Assert.AreEqual(1, snapshot.AudioReferenceCount);
            Assert.AreEqual(1, snapshot.VisualReferenceCount);
            Assert.AreEqual(1, snapshot.MovieReferenceCount);
            Assert.AreEqual(@".\sound\kick.wav", snapshot.AudioReferences.Single().RawPath);
            Assert.AreEqual(Path.Combine("sound", "kick"), snapshot.AudioReferences.Single().NormalizedPath);
            Assert.AreEqual(ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick")), snapshot.AudioReferences.Single().RelativePathHash);
            CollectionAssert.Contains(snapshot.AudioRelativePathHashes.ToArray(), ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick")));
            CollectionAssert.DoesNotContain(snapshot.AudioRelativePathHashes.ToArray(), ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick.wav")));
            Assert.AreEqual(@"visual\bg.final.png", snapshot.VisualReferences.Single().RawPath);
            Assert.AreEqual(Path.Combine("visual", "bg.final"), snapshot.VisualReferences.Single().NormalizedPath);
            Assert.AreEqual(@"movie\op.mpg", snapshot.MovieReferences.Single().RawPath);
            Assert.AreEqual(Path.Combine("movie", "op"), snapshot.MovieReferences.Single().NormalizedPath);
            Assert.AreEqual(snapshot.AudioReferences.Single().RawPath, aggregate.AudioReferences.Single().RawPath);
            Assert.AreEqual(snapshot.VisualReferences.Single().RawPath, aggregate.VisualReferences.Single().RawPath);
            Assert.AreEqual(snapshot.MovieReferences.Single().RawPath, aggregate.MovieReferences.Single().RawPath);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Create_ProjectedResourceListsRemainAuthoritativeWhenStorageOwnerHasRawReferences()
    {
        BMSFile file = CreateBmsFile("C:\\Pending\\owner.bms", ["owner.wav"], []);
        file.ResourceReferences =
        [
            new ChartResourceReference(ChartResourceKind.Audio, "owner.wav", "owner.wav")
        ];
        var chart = new ChartFile(
            ChartFileKind.Bms,
            file.path,
            file.hash,
            file.sha256,
            file.Title,
            file.GetRawTitleForDisplay(),
            file.Artist,
            file.genre,
            "Pending",
            file.tag,
            file.level?.ToString(),
            null,
            file.mode,
            null,
            file,
            null,
            audioResourcePaths: ["projected.wav"],
            visualResourcePaths: []);

        ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(chart);

        CollectionAssert.Contains(snapshot.AudioRelativePaths.ToArray(), "projected");
        CollectionAssert.DoesNotContain(snapshot.AudioRelativePaths.ToArray(), "owner");
        Assert.AreEqual("projected.wav", snapshot.AudioReferences.Single().RawPath);
    }

    [TestMethod]
    public void Create_DuplicateNormalizedResourceKeepsFirstRawPath()
    {
        BMSFile file = CreateBmsFile("C:\\Pending\\duplicate.bms", ["sound.wav", "sound.ogg"], []);
        file.ResourceReferences =
        [
            new ChartResourceReference(ChartResourceKind.Audio, @".\sound.wav", "sound.wav"),
            new ChartResourceReference(ChartResourceKind.Audio, "sound.ogg", "sound.ogg")
        ];
        var chart = new ChartFile(
            ChartFileKind.Bms,
            file.path,
            file.hash,
            file.sha256,
            file.Title,
            file.GetRawTitleForDisplay(),
            file.Artist,
            file.genre,
            "Pending",
            file.tag,
            file.level?.ToString(),
            null,
            file.mode,
            null,
            file,
            null);

        ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(chart);

        Assert.AreEqual(1, snapshot.AudioReferenceCount);
        Assert.AreEqual(@".\sound.wav", snapshot.AudioReferences.Single().RawPath);
        CollectionAssert.Contains(snapshot.AudioRelativePaths.ToArray(), "sound");
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
