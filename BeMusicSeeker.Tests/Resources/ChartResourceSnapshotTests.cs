using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    public void ResourcesSnapshotsRemainImmutableAfterOwnerMutation()
    {
        var resources = new Ribbit.BMS.Resources();
        resources.AddFilePath("sound.wav");
        ImmutableHashSet<string> paths = resources.FilePaths;
        ImmutableHashSet<uint> hashes = resources.FilePathsHashSet;

        resources.AddFilePath("image.png");

        Assert.AreEqual(1, paths.Count);
        Assert.AreEqual(1, hashes.Count);
        CollectionAssert.Contains(paths.ToArray(), "sound.wav");
        CollectionAssert.DoesNotContain(paths.ToArray(), "image.png");
    }

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

        var single = ChartResourceSnapshot.Create(chart);
        var aggregate = ChartResourceSnapshot.CreateAggregate([chart]);

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
    [DataRow(@"sound\..\foo.wav", "ParentTraversalUnsupported", "")]
    [DataRow("foo..bar.wav", "Valid", "foo..bar.wav")]
    [DataRow("", "Empty", "")]
    [DataRow("bad\0.wav", "InvalidPath", "")]
    [DataRow(@"C:\sound\foo.wav", "RootedOrAbsolute", "")]
    [DataRow(@"\sound\foo.wav", "Valid", @"sound\foo.wav")]
    public void AnalyzeReferencePathForLookup_PreservesExistingPathClassification(string path, string status, string normalizedPath)
    {
        ChartResourcePathNormalizationResult result = ChartResourcePathNormalizer.AnalyzeReferencePathForLookup(path);

        Assert.AreEqual(status, result.Status.ToString());
        Assert.AreEqual(normalizedPath, result.NormalizedPath);
    }

    [TestMethod]
    [DataRow("mystery.xyz", false)]
    [DataRow("mystery..xyz", false)]
    [DataRow(@".\mystery.xyz", false)]
    [DataRow(@"sound\..\mystery.xyz", true)]
    [DataRow("", false)]
    [DataRow("bad\0.xyz", false)]
    [DataRow(@"C:\sound\mystery.xyz", false)]
    public void Create_UnknownResourcePreservesOnlyActualParentTraversal(string path, bool hasParentTraversal)
    {
        BMSFile file = CreateBmsFile(@"C:\Pending\unknown.bms", [], [path]);
        ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false);
        foreach (ChartResourceSnapshot snapshot in new[] { ChartResourceSnapshot.Create(chart), ChartResourceSnapshot.CreateAggregate([chart]) })
        {
            Assert.AreEqual(0, snapshot.TotalReferenceCount);
            Assert.AreEqual(hasParentTraversal, snapshot.HasUnsupportedParentTraversalReference);
            Assert.AreEqual(hasParentTraversal ? 1 : 0, snapshot.UnsupportedResourceReferenceCount);
        }
    }

    [TestMethod]
    public void Create_ExtensionOnlyBmsResourcesKeepNormalKeysWithoutParentTraversal()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "ChartResourceSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string chartPath = Path.Combine(tempDirectory, "resources.bms");
            File.WriteAllText(chartPath,
                "#PLAYER 1\r\n#TITLE Resources\r\n"
                + "#WAVAA .\\sound\\kick.wav\r\n#WAVAB .wav\r\n#WAVAC .ogg\r\n"
                + "#BMPAA bg.final.png\r\n#BMPAB movie.mpg\r\n#BMPAC .png\r\n#BMPAD mystery.xyz\r\n"
                + "#BANNER .png\r\n#BACKBMP \r\n#STAGEFILE .png\r\n#00111:AA\r\n");
            var file = BMSFile.CreateBMSFileFromFile(chartPath);
            Assert.IsTrue(file.BGAfiles.Contains("mystery.xyz"));
            Assert.IsTrue(file.ResourceReferences.Any(reference => reference.Kind == ChartResourceKind.Unknown && reference.RawPath == "mystery.xyz"));

            foreach (bool includeResourceReferences in new[] { false, true })
            {
                ChartFile chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false, includeResourceReferences: includeResourceReferences, includeScoreSnapshot: false);
                foreach (ChartResourceSnapshot snapshot in new[] { ChartResourceSnapshot.Create(chart), ChartResourceSnapshot.CreateAggregate([chart]) })
                {
                    Assert.AreEqual(1, snapshot.AudioReferenceCount);
                    Assert.AreEqual(1, snapshot.VisualReferenceCount);
                    Assert.AreEqual(1, snapshot.MovieReferenceCount);
                    Assert.AreEqual(0, snapshot.OptionalImageReferenceCount);
                    CollectionAssert.AreEquivalent(new[] { Path.Combine("sound", "kick"), "bg.final", "movie" }, snapshot.EnumerateAllRelativePaths().ToArray());
                    CollectionAssert.AreEquivalent(new[] { ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick")), ChartResourceKeyHash.GetLookupHash("bg.final"), ChartResourceKeyHash.GetLookupHash("movie") }, snapshot.EnumerateAllRelativePathHashes().ToArray());
                    Assert.AreEqual(0, snapshot.UnsupportedResourceReferenceCount);
                    Assert.IsFalse(snapshot.HasUnsupportedParentTraversalReference);
                }
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
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
            var file = BMSFile.CreateBMSFileFromFile(chartPath);
            ChartFile chart = ChartFileProjection.FromBmsFile(
                file,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false);

            var snapshot = ChartResourceSnapshot.CreateAggregate([chart]);

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
            var file = BMSFile.CreateBMSFileFromFile(chartPath);
            ChartFile chart = ChartFileProjection.FromBmsFile(
                file,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false);

            var snapshot = ChartResourceSnapshot.Create(chart);
            var aggregate = ChartResourceSnapshot.CreateAggregate([chart]);

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
            Assert.AreEqual(3, snapshot.ResourceReferences.Count);
            CollectionAssert.AreEquivalent(
                snapshot.ResourceReferences.Select(reference => reference.RawPath).ToArray(),
                aggregate.ResourceReferences.Select(reference => reference.RawPath).ToArray());
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

        var snapshot = ChartResourceSnapshot.Create(chart);

        CollectionAssert.Contains(snapshot.AudioRelativePaths.ToArray(), "projected");
        CollectionAssert.DoesNotContain(snapshot.AudioRelativePaths.ToArray(), "owner");
        Assert.AreEqual("projected.wav", snapshot.AudioReferences.Single().RawPath);
    }

    [TestMethod]
    public void Create_DuplicateNormalizedResourceKeepsUniqueLookupAndAllRawReferences()
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

        var snapshot = ChartResourceSnapshot.Create(chart);

        Assert.AreEqual(1, snapshot.AudioReferenceCount);
        Assert.AreEqual(@".\sound.wav", snapshot.AudioReferences.Single().RawPath);
        CollectionAssert.Contains(snapshot.AudioRelativePaths.ToArray(), "sound");
        Assert.AreEqual(2, snapshot.ResourceReferences.Count);
        CollectionAssert.AreEqual(
            new[] { @".\sound.wav", "sound.ogg" },
            snapshot.ResourceReferences.Select(reference => reference.RawPath).ToArray());
    }

    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_UsesPrecomputedDefinedResources()
    {
        var targetEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(
            CreateBmsFile("C:\\Pending\\target.bms", ["target.wav"], []),
            includeWarningSnapshot: false,
            includeResourceReferences: true,
            includeScoreSnapshot: false));
        var precomputedResources = ChartResourceSnapshot.CreateAggregate([
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
