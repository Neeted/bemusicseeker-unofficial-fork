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
        ChartFile file = CreateBmsFile(
            "C:\\Pending\\dot-stem.bms",
            ["4.org1_1.wav", "4.org1_2.wav", "5.bell_1.wav"],
            ["bg.final.png", "movie.opening.mpg"]);
        ChartFile chart = (file);

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
        ChartFile file = CreateBmsFile(@"C:\Pending\unknown.bms", [], [path]);
        ChartFile chart = (file);
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
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            Assert.IsTrue(file.Resources.Any(reference => reference.NormalizedPath == "mystery.xyz"));
            Assert.IsTrue(file.Resources.Any(reference => reference.Kind == ChartResourceKind.Unknown && reference.RawPath == "mystery.xyz"));

            ChartFile chart = (file);
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
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile chart = (file);

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
    public void Create_PreservesAcquiredBmsRawResourcePaths()
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
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile chart = (file);

            var snapshot = ChartResourceSnapshot.Create(chart);
            var aggregate = ChartResourceSnapshot.CreateAggregate([chart]);

            Assert.AreEqual(1, snapshot.AudioReferenceCount);
            Assert.AreEqual(1, snapshot.VisualReferenceCount);
            Assert.AreEqual(1, snapshot.MovieReferenceCount);
            Assert.AreEqual(@".\sound\kick.wav", snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Audio).RawPath);
            Assert.AreEqual(Path.Combine("sound", "kick"), snapshot.AudioReferences.Single().LookupKey);
            Assert.AreEqual(ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick")), snapshot.AudioReferences.Single().RelativePathHash);
            CollectionAssert.Contains(snapshot.AudioRelativePathHashes.ToArray(), ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick")));
            CollectionAssert.DoesNotContain(snapshot.AudioRelativePathHashes.ToArray(), ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick.wav")));
            Assert.AreEqual(@"visual\bg.final.png", snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Image).RawPath);
            Assert.AreEqual(Path.Combine("visual", "bg.final"), snapshot.VisualReferences.Single().LookupKey);
            Assert.AreEqual(@"movie\op.mpg", snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Movie).RawPath);
            Assert.AreEqual(Path.Combine("movie", "op"), snapshot.MovieReferences.Single().LookupKey);
            Assert.AreEqual(snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Audio).RawPath, aggregate.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Audio).RawPath);
            Assert.AreEqual(snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Image).RawPath, aggregate.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Image).RawPath);
            Assert.AreEqual(snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Movie).RawPath, aggregate.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Movie).RawPath);
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
    public void Create_ResourcesRemainAuthoritativeWhenStorageOwnerHasDifferentResources()
    {
        ChartFile file = CreateBmsFile("C:\\Pending\\owner.bms", ["owner.wav"], []);
        file = file with
        {
            Resources = [
            ChartResourceReference.Parse("owner.wav", ChartResourceKind.Audio)
        ]
        };
        var chart = new ChartFile(ChartFileKind.Bms, file.Path, file.Md5, file.Sha256, file.Title, file.RawTitle, file.Artist, file.Genre, "Pending", file.Tag, file.Level?.ToString(), null, file.Mode, null, resources: TestChartResources.Create(["projected.wav"]));

        var snapshot = ChartResourceSnapshot.Create(chart);

        CollectionAssert.Contains(snapshot.AudioRelativePaths.ToArray(), "projected");
        CollectionAssert.DoesNotContain(snapshot.AudioRelativePaths.ToArray(), "owner");
        Assert.AreEqual("projected.wav", snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Audio).RawPath);
    }

    [TestMethod]
    public void Create_DuplicateNormalizedResourceKeepsUniqueLookupAndAllRawReferences()
    {
        ChartFile file = CreateBmsFile("C:\\Pending\\duplicate.bms", ["sound.wav", "sound.ogg"], []);
        file = file with
        {
            Resources = [
            ChartResourceReference.Parse(@".\sound.wav", ChartResourceKind.Audio),
            ChartResourceReference.Parse("sound.ogg", ChartResourceKind.Audio)
        ]
        };
        var chart = new ChartFile(ChartFileKind.Bms, file.Path, file.Md5, file.Sha256, file.Title, file.RawTitle, file.Artist, file.Genre, "Pending", file.Tag, file.Level?.ToString(), null, file.Mode, null, resources: file.Resources);

        var snapshot = ChartResourceSnapshot.Create(chart);

        Assert.AreEqual(1, snapshot.AudioReferenceCount);
        Assert.AreEqual(@".\sound.wav", snapshot.ResourceReferences.First(reference => reference.Kind == ChartResourceKind.Audio).RawPath);
        CollectionAssert.Contains(snapshot.AudioRelativePaths.ToArray(), "sound");
        Assert.AreEqual(2, snapshot.ResourceReferences.Count);
        CollectionAssert.AreEqual(
            new[] { @".\sound.wav", "sound.ogg" },
            snapshot.ResourceReferences.Select(reference => reference.RawPath).ToArray());
    }

    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_UsesPrecomputedDefinedResources()
    {
        var targetEntry = PackageChartEntry.FromChart((CreateBmsFile("C:\\Pending\\target.bms", ["target.wav"], [])));
        var precomputedResources = ChartResourceSnapshot.CreateAggregate([
            (CreateBmsFile("C:\\Pending\\precomputed.bms", ["precomputed.wav"], []))
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

    [TestMethod]
    public void Create_UnacquiredResourcesFailAndSuccessfulEmptyResourcesRemainEmpty()
    {
        ChartFile owner = (ChartTestValues.Empty() with { Path = @"C:\Missing\chart.bms" });
        ChartFile unacquired = (owner);
        Assert.IsNull(unacquired.Resources);
        Assert.ThrowsException<InvalidOperationException>(() => ChartResourceSnapshot.Create(unacquired));
        Assert.ThrowsException<InvalidOperationException>(() => ChartResourceSnapshot.CreateAggregate([unacquired]));
        owner = owner with { Resources = [] };
        ChartFile acquired = (owner);
        Assert.IsNotNull(acquired.Resources);
        Assert.AreEqual(0, ChartResourceSnapshot.Create(acquired).TotalReferenceCount);
        ChartFile omitted = owner with { Resources = null };
        Assert.ThrowsException<InvalidOperationException>(() => ChartResourceSnapshot.Create(omitted));
    }

    [TestMethod]
    public void AcquiredResourcesRemainSharedAcrossOwnerlessCopiesAfterChartDeletion()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(ChartResourceSnapshotTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "chart.bms");
            File.WriteAllText(path, "#WAV01 .\\audio\\foo.v2.flac\n#WAV02 audio/foo.v2.ogg\n#BMP01 mystery.xyz\n#BMP02 .png\n#WAV03 ..\\base\\hit.wav\n#BANNER .\\banner.jpeg\n#BACKBMP ..\\back.png\n#STAGEFILE stage.png\n");
            ChartFile owner = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
            ChartFile chart = (owner);
            ChartFile detached = ChartFileProjection.ToImmutableSnapshot(chart);
            File.Delete(path);
            owner = owner with { Resources = [] };
            owner = owner with { Banner = "different.png" };
            var entry = PackageChartEntry.FromChart(detached);
            ChartFile copy = ChartFileProjection.WithWarnings(entry.Chart, []);
            foreach (ChartFile result in new[] { detached, entry.Chart, copy })
            {
                Assert.IsNull(result.Token);
                Assert.AreSame(chart.Resources, result.Resources);
                Assert.AreEqual(8, result.Resources.Count);
                ChartResourceReference audio = result.Resources[0];
                Assert.AreEqual(@".\audio\foo.v2.flac", audio.RawPath);
                Assert.AreEqual(@"audio\foo.v2.wav", audio.NormalizedPath);
                Assert.AreEqual(@"audio\foo.v2", audio.LookupKey);
                Assert.AreEqual(ChartResourceUsage.Normal, audio.Usage);
                Assert.AreEqual(ChartResourcePathNormalizationStatus.Valid, audio.Status);
                Assert.AreEqual("banner.png", result.Resources.Single(reference => reference.Usage == ChartResourceUsage.Banner).NormalizedPath);
                Assert.AreEqual(".\\banner.jpeg", result.Resources.Single(reference => reference.Usage == ChartResourceUsage.Banner).RawPath);
                var index = ChartResourceSnapshot.Create(result);
                Assert.AreEqual(1, index.AudioReferenceCount);
                Assert.AreEqual(2, index.UnsupportedResourceReferenceCount);
                Assert.AreEqual(8, index.ResourceReferences.Count);
                CollectionAssert.AreEqual(chart.Resources.ToArray(), index.ResourceReferences.ToArray());
                CollectionAssert.AreEqual(chart.Resources.ToArray(), ChartResourceSnapshot.CreateAggregate([result]).ResourceReferences.ToArray());
                ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(result);
                Assert.AreEqual(1, health.WavFilesDefined);
                Assert.AreEqual(true, health.StagefileDefined);
                Assert.AreEqual(true, health.BannerDefined);
                Assert.AreEqual(true, health.BackbmpDefined);
                Assert.AreEqual(false, health.StagefileExisting);
                Assert.AreEqual(false, health.BannerExisting);
                Assert.AreEqual(false, health.BackbmpExisting);
                Lr2ResourceReferenceEvaluation compatibility = Lr2CompatibilityEvaluator.EvaluateResourceReferences(result.Path, index);
                Assert.IsTrue(compatibility.HasParentTraversal);
                Assert.AreEqual(@"audio\foo.v2.flac".Length, compatibility.MaxRelativeCp932Bytes);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void StorageOwnerCopiesKeepSourceResourcesWhenOwnersReleaseOrReplaceThem()
    {
        ImmutableList<ChartResourceReference> resources =
        [
            ChartResourceReference.Parse(@".\audio\foo.v2.flac", ChartResourceKind.Audio),
            ChartResourceReference.Parse(@"..\base\sound.wav", ChartResourceKind.Audio),
            ChartResourceReference.Parse("mystery.xyz", ChartResourceKind.Unknown),
            ChartResourceReference.Parse(".wav", ChartResourceKind.Audio),
            ChartResourceReference.Parse(@".\banner.jpeg", ChartResourceKind.Image, ChartResourceUsage.Banner),
            ChartResourceReference.Parse("stage.png", ChartResourceKind.Image, ChartResourceUsage.Stagefile),
            ChartResourceReference.Parse(@"..\back.png", ChartResourceKind.Image, ChartResourceUsage.Backbmp),
            new(ChartResourceKind.Unknown, ChartResourceUsage.InputDiagnostic, string.Empty, string.Empty,
                string.Empty, ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported)
        ];
        ChartFile bmsOwner = (ChartTestValues.Empty() with { Path = @"C:\Missing\source.bms", Md5 = new string('a', 32), RawTitle = "before", Resources = resources });
        ChartFile bmsonOwner = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = @"C:\Missing\source.bmson", Md5 = new string('b', 32), RawTitle = "before", Resources = resources };
        ChartFile[] sources = [(bmsOwner), (bmsonOwner)];
        ChartFile[] unacquiredSources =
        [
            bmsOwner with { Resources = null },
            bmsonOwner with { Resources = null }
        ];
        foreach (ImmutableList<ChartResourceReference>? ownerResources in new ImmutableList<ChartResourceReference>?[]
            { null, TestChartResources.Create(["different.wav"]) })
        {
            bmsOwner = bmsOwner with { Resources = ownerResources };
            bmsonOwner = bmsonOwner with { Resources = ownerResources };
            bmsOwner = bmsOwner with { RawTitle = "after" };
            bmsonOwner = bmsonOwner with { RawTitle = "after" };
            foreach (ChartFile source in sources)
            {
                ChartFile[] copies =
                [
                    ChartFileProjection.CaptureBasicSnapshot(source),
                    ChartFileProjection.CaptureBasicSnapshotWithTransientState(source, ChartFileTransientState.Empty),
                    ChartFileProjection.CaptureListSnapshot(source),
                    PackageChartEntry.FromChart(source).Chart,
                    ChartStorageTargetSet.FromCharts([source]).Charts.Single()
                ];
                foreach (ChartFile copy in copies)
                {
                    Assert.AreEqual("before", copy.RawTitle);
                    Assert.AreSame(resources, copy.Resources);
                    CollectionAssert.AreEqual(resources.ToArray(), ChartResourceSnapshot.Create(copy).ResourceReferences.ToArray());
                    Assert.AreEqual(@"audio\foo.v2", copy.Resources[0].LookupKey);
                    Assert.AreEqual(3, ChartResourceSnapshot.Create(copy).UnsupportedResourceReferenceCount);
                }
            }
            foreach (ChartFile source in unacquiredSources)
            {
                foreach (ChartFile copy in new[]
                {
                    ChartFileProjection.CaptureBasicSnapshot(source),
                    ChartFileProjection.CaptureBasicSnapshotWithTransientState(source, ChartFileTransientState.Empty),
                    ChartFileProjection.CaptureListSnapshot(source),
                    PackageChartEntry.FromChart(source).Chart,
                    ChartStorageTargetSet.FromCharts([source]).Charts.Single()
                })
                {
                    Assert.IsNull(copy.Resources);
                    Assert.ThrowsException<InvalidOperationException>(() => ChartResourceSnapshot.Create(copy));
                }
            }
        }
    }

    private static ChartFile CreateBmsFile(string path, IEnumerable<string> wavFiles, IEnumerable<string> bgaFiles)
    {
        return ChartTestValues.Empty() with
        {
            Path = path,
            Resources = TestChartResources.Create(wavFiles, bgaFiles)
        };
    }
}
