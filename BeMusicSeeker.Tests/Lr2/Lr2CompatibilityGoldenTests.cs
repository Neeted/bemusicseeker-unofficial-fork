using System;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2CompatibilityGoldenTests
{
    [TestMethod]
    public void RootParentHashMatchesLr2RootSentinel()
    {
        Assert.AreEqual("e2977170", Lr2SongFolderParentNormalizer.RootParentHash);
        Assert.AreEqual("e2977170", Lr2SongFolderParentNormalizer.ComputeRootHash());
    }

    [TestMethod]
    public void Lr2Crc32MatchesRootTextGoldenValue()
    {
        byte[] bytes = System.Text.Encoding.GetEncoding("shift_jis").GetBytes("ROOT");

        Assert.AreEqual("206114ef", LR2CRC32.Compute(bytes).ToString("x"));
    }

    [DataTestMethod]
    [DataRow(@"D:\BMS\Pack\Song\chart.bms", "f002e300", "a777506c")]
    [DataRow(@"D:\root.bms", "876fd4dd", "57d700a7")]
    [DataRow(@"\\server\share\Pack\chart.bms", "34521cee", "fcdbcd31")]
    [DataRow(@"D:\BMS\あいう\chart.bms", "51aa0c67", "8c132896")]
    public void FolderParentHashesMatchLr2DirectoryGoldenValues(string chartPath, string expectedFolder, string expectedParent)
    {
        bool success = Lr2SongFolderParentNormalizer.TryComputeExpectedHashes(chartPath, out string folder, out string parent);

        Assert.IsTrue(success);
        Assert.AreEqual(expectedFolder, folder);
        Assert.AreEqual(expectedParent, parent);
    }

    [TestMethod]
    public void FolderParentHashRejectsCp932UnsupportedPath()
    {
        bool success = Lr2SongFolderParentNormalizer.TryComputeExpectedHashes(
            @"D:\BMS\emoji_😀\chart.bms",
            out string folder,
            out string parent);

        Assert.IsFalse(success);
        Assert.IsNull(folder);
        Assert.IsNull(parent);
    }

    [TestMethod]
    public void UnsupportedPathClearsFolderParentAndMarksWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = @"D:\BMS\emoji_😀\chart.bms",
            Folder = "f002e300"
        };

        var row = new BeMusicSeeker.Models.LR2.LR2SongDB.song { path = file.Path, folder = file.Folder, parent = "stale-parent" };
        bool changed = Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(row);

        Assert.IsTrue(changed);
        Assert.IsNull(row.folder);
        Assert.IsNull(row.parent);
        Assert.IsTrue(Lr2CompatibilityEvaluator.EvaluateChartPath(file.Path).WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.PathEncodingUnsupported));
    }

    [TestMethod]
    public void EvaluatorMatchesExistingFolderParentHashes()
    {
        const string chartPath = @"D:\BMS\Pack\Song\chart.bms";

        Lr2ChartPathEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateChartPath(chartPath);

        Assert.AreEqual(Lr2CompatibilityWarningFlags.None, evaluation.WarningFlags);
        Assert.IsTrue(evaluation.CanComputeFolderParent);
        Assert.AreEqual("f002e300", evaluation.FolderHash);
        Assert.AreEqual("a777506c", evaluation.ParentHash);
    }

    [TestMethod]
    public void EvaluatorFlagsCp932UnsupportedPath()
    {
        Lr2ChartPathEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateChartPath(@"D:\BMS\emoji_😀\chart.bms");

        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.PathEncodingUnsupported));
        Assert.IsFalse(evaluation.CanComputeFolderParent);
        Assert.IsNull(evaluation.FolderHash);
        Assert.IsNull(evaluation.ParentHash);
    }

    [TestMethod]
    public void EvaluatorFlagsChartPathByteBoundary()
    {
        Lr2ChartPathEvaluation max = Lr2CompatibilityEvaluator.EvaluateChartPath(BuildAsciiChartPath(259));
        Lr2ChartPathEvaluation tooLong = Lr2CompatibilityEvaluator.EvaluateChartPath(BuildAsciiChartPath(260));

        Assert.IsFalse(max.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.PathTooLong));
        Assert.IsTrue(tooLong.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.PathTooLong));
    }

    [TestMethod]
    public void EvaluatorAcceptsLegacyCompatibleRootPath()
    {
        Assert.IsTrue(Lr2CompatibilityEvaluator.IsLegacyRootPathCompatible(@"D:\BMS"));
    }

    [TestMethod]
    public void EvaluatorRejectsLegacyIncompatibleRootPath()
    {
        string tooLongRoot = @"D:\" + new string('a', Lr2CompatibilityEvaluator.MaxLegacyPathBytes);

        Assert.IsFalse(Lr2CompatibilityEvaluator.IsLegacyRootPathCompatible(tooLongRoot));
        Assert.IsFalse(Lr2CompatibilityEvaluator.IsLegacyRootPathCompatible(@"D:\BMS\emoji_😀"));
        Assert.IsFalse(Lr2CompatibilityEvaluator.IsLegacyRootPathCompatible(@"\\?\GLOBALROOT\Device\HarddiskVolume1\BMS"));
    }

    [TestMethod]
    public void EvaluatorUsesRawResourcePathWithExtensionForByteFacts()
    {
        ChartFile file = CreateBmsFileWithResource(
            @"D:\BMS\Pack\Song\chart.bms",
            ChartResourceReference.Parse("4.org1_1.wav", ChartResourceKind.Audio));
        var snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.Path, snapshot);

        Assert.AreEqual(Lr2CompatibilityWarningFlags.None, evaluation.WarningFlags);
        Assert.AreEqual(StrictShiftJisByteCount("4.org1_1.wav"), evaluation.MaxRelativeCp932Bytes);
        Assert.IsFalse(evaluation.HasParentTraversal);
    }

    [TestMethod]
    public void EvaluatorIncludesParentTraversalResourcePathInByteFacts()
    {
        const string chartPath = @"D:\BMS\Pack\Song\chart.bms";
        const string parentTraversalPath = @"..\Shared\hit.wav";
        ChartFile file = CreateBmsFileWithResource(chartPath, ChartResourceReference.Parse("sound.wav", ChartResourceKind.Audio));
        file = file with { Resources = file.Resources.Add(ChartResourceReference.Parse(parentTraversalPath, ChartResourceKind.Audio)) };
        var snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.Path, snapshot);

        Assert.AreEqual(Lr2CompatibilityWarningFlags.None, evaluation.WarningFlags);
        Assert.AreEqual(StrictShiftJisByteCount(parentTraversalPath), evaluation.MaxRelativeCp932Bytes);
        Assert.IsTrue(evaluation.HasParentTraversal);
    }

    [TestMethod]
    public void EvaluatorDetectsParentTraversalFromRawBmsResourceLists()
    {
        const string parentTraversalPath = @"..\Shared\hit.wav";
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = @"D:\BMS\Pack\Song\chart.bms",
            Resources = TestChartResources.Create([parentTraversalPath], [])
        };

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.Path, ChartResourceSnapshot.Create(CreateChart(file)));

        Assert.AreEqual(Lr2CompatibilityWarningFlags.None, evaluation.WarningFlags);
        Assert.AreEqual(StrictShiftJisByteCount(parentTraversalPath), evaluation.MaxRelativeCp932Bytes);
        Assert.IsTrue(evaluation.HasParentTraversal);
    }

    [TestMethod]
    public void EvaluatorFlagsParentTraversalResolvedPathLength()
    {
        string chartPath = BuildAsciiChartPathWithDirectorySegment(242);
        const string parentTraversalPath = @"..\Shared\hit.wav";
        ChartFile file = CreateBmsFileWithResource(chartPath, ChartResourceReference.Parse("sound.wav", ChartResourceKind.Audio));
        file = file with { Resources = file.Resources.Add(ChartResourceReference.Parse(parentTraversalPath, ChartResourceKind.Audio)) };
        var snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.Path, snapshot);

        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        Assert.IsTrue(evaluation.HasParentTraversal);
    }

    [TestMethod]
    public void EvaluatorFlagsCp932UnsupportedResourcePath()
    {
        ChartFile file = CreateBmsFileWithResource(
            @"D:\BMS\Pack\Song\chart.bms",
            ChartResourceReference.Parse(@"sound\😀.wav", ChartResourceKind.Audio));
        var snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.Path, snapshot);

        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported));
        Assert.IsNull(evaluation.MaxRelativeCp932Bytes);
    }

    [TestMethod]
    public void EvaluatorFlagsCp932DecodeUnsupportedResourceAndContinuesLengthEvaluation()
    {
        string longRawPath = new string('a', 260) + ".wav";
        byte[] bytes = Encoding.UTF8.GetBytes("#WAV01 sound😀.wav\r\n#WAV02 " + longRawPath + "\r\n");
        var snapshot = new ChartFileSnapshot(
            @"D:\BMS\Pack\Song\chart.bms",
            bytes,
            DateTime.UtcNow,
            "0123456789abcdef0123456789abcdef",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        BmsEncodingDetectionResult detectionResult = BmsEncodingDetector.Detect((snapshot).Bytes);
        ChartFile file = BmsChartFileParser.ParseSnapshot(snapshot, detectionResult);
        var resources = ChartResourceSnapshot.Create(CreateChart(file));
        var aggregate = ChartResourceSnapshot.CreateAggregate([CreateChart(file)]);

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.Path, resources);

        Assert.IsTrue(file.Resources.Any(reference => reference.Status == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported));
        foreach (ChartResourceSnapshot resourceSnapshot in new[] { resources, aggregate })
        {
            Assert.IsTrue(resourceSnapshot.ResourceReferences.Any(reference => reference.Status == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported));
            Assert.IsFalse(resourceSnapshot.HasUnsupportedParentTraversalReference);
        }
        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported));
        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        Assert.AreEqual(StrictShiftJisByteCount(longRawPath), evaluation.MaxRelativeCp932Bytes);
    }

    [TestMethod]
    public void EvaluatorUsesAllRawResourceReferencesEvenWhenLookupKeyIsDuplicate()
    {
        string longRawPath = string.Concat(Enumerable.Repeat(@".\", 130)) + "sound.wav";
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = @"D:\BMS\Pack\Song\chart.bms",
            Resources =
            [
                ChartResourceReference.Parse(@".\sound.wav", ChartResourceKind.Audio),
                ChartResourceReference.Parse(longRawPath, ChartResourceKind.Audio)
            ]
        };
        var snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.Path, snapshot);

        Assert.AreEqual(1, snapshot.AudioReferenceCount);
        Assert.AreEqual(2, snapshot.ResourceReferences.Count);
        Assert.IsFalse(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        Assert.AreEqual(StrictShiftJisByteCount("sound.wav"), evaluation.MaxRelativeCp932Bytes);
    }

    [TestMethod]
    public void RelocatedParentTraversalFactsArePreservedWithoutAcquiredResources()
    {
        ResourceHealthMaintenanceSnapshot info = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance
        {
            lr2_warning_flags = (int)(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported
                | Lr2CompatibilityWarningFlags.ResourcePathTooLong),
            lr2_resource_max_relative_cp932_bytes = 120,
            lr2_resource_has_parent_traversal = true
        });

        info = Lr2CompatibilityEvaluator.RefreshRelocatedMaintenanceFacts(
            info,
            @"D:\BMS\Moved\chart.bms");

        var flags = (Lr2CompatibilityWarningFlags)info.Lr2WarningFlags.GetValueOrDefault();
        Assert.IsTrue(flags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported));
        Assert.IsTrue(flags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        Assert.AreEqual(120, info.Lr2ResourceMaxRelativeCp932Bytes);
        Assert.IsTrue(info.Lr2ResourceHasParentTraversal.GetValueOrDefault());
    }

    [TestMethod]
    [DataRow(".flac", 259, false)]
    [DataRow(".flac", 260, true)]
    [DataRow(".jpeg", 259, false)]
    [DataRow(".jpeg", 260, true)]
    public void EvaluatorCountsOriginalExtensionAtCp932Boundary(string extension, int resolvedBytes, bool tooLong)
    {
        string raw = "resource" + extension;
        string path = BuildAsciiChartPathWithDirectorySegment(resolvedBytes - 4 - raw.Length);
        var reference = ChartResourceReference.Parse(raw, ChartResourceKind.Unknown);
        var index = ChartResourceSnapshot.Create(TestChartResources.Create(
            extension == ".flac" ? [raw] : [], extension == ".jpeg" ? [raw] : []));
        Lr2ResourceReferenceEvaluation result = Lr2CompatibilityEvaluator.EvaluateResourceReferences(path, index);
        Assert.AreEqual(raw.Length, result.MaxRelativeCp932Bytes);
        Assert.AreEqual(tooLong, result.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        Assert.AreEqual(raw, reference.RawPath);
        Assert.AreEqual(extension == ".flac" ? "resource.wav" : "resource.png", reference.NormalizedPath);
    }

    [TestMethod]
    public void EvaluatorIgnoresTrulyEmptyPathsAtCp932DirectoryBoundary()
    {
        string path = BuildAsciiChartPathWithDirectorySegment(256);
        string? directory = System.IO.Path.GetDirectoryName(path);
        Assert.IsNotNull(directory);
        Assert.AreEqual(259, StrictShiftJisByteCount(directory));
        ChartFileSnapshot bmsSnapshot = ChartFileContentReader.CreateSnapshot(path,
            Encoding.UTF8.GetBytes("#WAV01 \r\n#WAV02 \t \r\n#WAV03 \r\n#BMP01 \t\r\n#STAGEFILE \r\n#BACKBMP \t\r\n#BANNER \t \r\n"), DateTime.UnixEpoch);
        ChartFileSnapshot bmsonSnapshot = ChartFileContentReader.CreateSnapshot(System.IO.Path.ChangeExtension(path, ".bmson"),
            Encoding.UTF8.GetBytes("{\"info\":{\"title\":\"Empty\",\"banner_image\":\"\",\"back_image\":\" \\t \",\"eyecatch_image\":\"\",\"preview_music\":\"\"},\"sound_channels\":[{\"name\":\"\",\"notes\":[]},{\"name\":\" \\t \",\"notes\":[]}],\"bga\":{\"bga_header\":[{\"id\":1,\"name\":\"\"}]}}"), DateTime.UnixEpoch);
        ChartFile[] charts =
        [
            (BmsChartFileParser.ParseSnapshot(bmsSnapshot)),
            (BmsonChartFileParser.ParseSnapshot(bmsonSnapshot))
        ];
        foreach (ChartFile chart in charts)
        {
            Assert.AreEqual(7, chart.Resources.Count);
            Assert.IsTrue(chart.Resources.All(reference => reference.Status == ChartResourcePathNormalizationStatus.Empty));
            Assert.IsTrue(chart.Resources.All(reference => string.IsNullOrWhiteSpace(reference.RawPath)));
            var snapshot = ChartResourceSnapshot.Create(chart);
            Lr2ResourceReferenceEvaluation empty = Lr2CompatibilityEvaluator.EvaluateResourceReferences(chart.Path, snapshot);

            Assert.AreEqual(0, snapshot.TotalReferenceCount);
            Assert.AreEqual(Lr2CompatibilityWarningFlags.None, empty.WarningFlags);
            Assert.IsNull(empty.MaxRelativeCp932Bytes);
            Assert.IsFalse(empty.HasParentTraversal);

            var withNonemptyKeylessPath = ChartResourceSnapshot.Create(chart.Resources.Add(
                ChartResourceReference.Parse(".wav", ChartResourceKind.Audio)));
            Lr2ResourceReferenceEvaluation keyless = Lr2CompatibilityEvaluator.EvaluateResourceReferences(chart.Path, withNonemptyKeylessPath);
            Assert.AreEqual(0, withNonemptyKeylessPath.TotalReferenceCount);
            Assert.AreEqual(4, keyless.MaxRelativeCp932Bytes);
            Assert.IsTrue(keyless.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        }
    }

    [TestMethod]
    public void EvaluatorIncludesUnknownEmptyKeyAndContinuesAfterPathlessCp932Diagnostic()
    {
        var diagnostic = new ChartResourceReference(ChartResourceKind.Unknown, ChartResourceUsage.InputDiagnostic,
            string.Empty, string.Empty, string.Empty, ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported);
        var index = ChartResourceSnapshot.Create(new[]
        {
            diagnostic,
            ChartResourceReference.Parse(".wav", ChartResourceKind.Audio),
            ChartResourceReference.Parse(new string('a', 260) + ".xyz", ChartResourceKind.Unknown)
        });
        Lr2ResourceReferenceEvaluation result = Lr2CompatibilityEvaluator.EvaluateResourceReferences("chart.bms", index);
        Assert.AreEqual(0, index.TotalReferenceCount);
        Assert.IsFalse(index.HasUnsupportedParentTraversalReference);
        Assert.AreEqual(264, result.MaxRelativeCp932Bytes);
        Assert.IsTrue(result.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported));
        Assert.IsTrue(result.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
    }

    private static int StrictShiftJisByteCount(string value)
    {
        return Encoding.GetEncoding(
            "shift_jis",
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback)
            .GetByteCount(value);
    }

    private static string BuildAsciiChartPath(int byteCount)
    {
        const string prefix = @"D:\";
        const string suffix = ".bms";
        return prefix + new string('a', byteCount - prefix.Length - suffix.Length) + suffix;
    }

    private static string BuildAsciiChartPathWithDirectorySegment(int directoryNameByteCount)
    {
        return @"D:\" + new string('a', directoryNameByteCount) + @"\chart.bms";
    }

    private static ChartFile CreateBmsFileWithResource(string path, ChartResourceReference reference)
    {
        return ChartTestValues.Empty() with
        {
            Path = path,
            Resources = [reference]
        };
    }

    private static ChartFile CreateChart(ChartFile file)
    {
        return new ChartFile(ChartFileKind.Bms, file.Path, file.Md5, file.Sha256, file.Title, file.RawTitle, file.Artist, file.Genre, "Song", file.Tag, file.Level?.ToString(), null, file.Mode, null, resources: file.Resources);
    }
}
