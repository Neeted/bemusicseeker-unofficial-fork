using System;
using System.IO;
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
        var file = new BMSFile
        {
            path = @"D:\BMS\emoji_😀\chart.bms",
            folder = "f002e300",
            parent = "a777506c"
        };

        bool changed = Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(file);

        Assert.IsTrue(changed);
        Assert.IsNull(file.folder);
        Assert.IsNull(file.parent);
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
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
        BMSFile file = CreateBmsFileWithResource(
            @"D:\BMS\Pack\Song\chart.bms",
            new ChartResourceReference(ChartResourceKind.Audio, "4.org1_1.wav", "4.org1_1.wav"));
        ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.path, snapshot);

        Assert.AreEqual(Lr2CompatibilityWarningFlags.None, evaluation.WarningFlags);
        Assert.AreEqual(StrictShiftJisByteCount("4.org1_1.wav"), evaluation.MaxRelativeCp932Bytes);
        Assert.IsFalse(evaluation.HasParentTraversal);
    }

    [TestMethod]
    public void EvaluatorIncludesParentTraversalResourcePathInByteFacts()
    {
        const string chartPath = @"D:\BMS\Pack\Song\chart.bms";
        const string parentTraversalPath = @"..\Shared\hit.wav";
        BMSFile file = CreateBmsFileWithResource(chartPath, new ChartResourceReference(ChartResourceKind.Audio, "sound.wav", "sound.wav"));
        file.UnsupportedResourceReferences =
        [
            new UnsupportedChartResourceReference(
                ChartResourceKind.Audio,
                parentTraversalPath,
                ChartResourcePathNormalizationStatus.ParentTraversalUnsupported)
        ];
        ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.path, snapshot);

        Assert.AreEqual(Lr2CompatibilityWarningFlags.None, evaluation.WarningFlags);
        Assert.AreEqual(StrictShiftJisByteCount(parentTraversalPath), evaluation.MaxRelativeCp932Bytes);
        Assert.IsTrue(evaluation.HasParentTraversal);
    }

    [TestMethod]
    public void EvaluatorDetectsParentTraversalFromRawBmsResourceLists()
    {
        const string parentTraversalPath = @"..\Shared\hit.wav";
        var file = new BMSFile
        {
            path = @"D:\BMS\Pack\Song\chart.bms",
            WAVfiles = [parentTraversalPath],
            BGAfiles = []
        };

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateBmsResourceReferences(file.path, file);

        Assert.AreEqual(Lr2CompatibilityWarningFlags.None, evaluation.WarningFlags);
        Assert.AreEqual(StrictShiftJisByteCount(parentTraversalPath), evaluation.MaxRelativeCp932Bytes);
        Assert.IsTrue(evaluation.HasParentTraversal);
    }

    [TestMethod]
    public void EvaluatorFlagsParentTraversalResolvedPathLength()
    {
        string chartPath = BuildAsciiChartPathWithDirectorySegment(242);
        const string parentTraversalPath = @"..\Shared\hit.wav";
        BMSFile file = CreateBmsFileWithResource(chartPath, new ChartResourceReference(ChartResourceKind.Audio, "sound.wav", "sound.wav"));
        file.UnsupportedResourceReferences =
        [
            new UnsupportedChartResourceReference(
                ChartResourceKind.Audio,
                parentTraversalPath,
                ChartResourcePathNormalizationStatus.ParentTraversalUnsupported)
        ];
        ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.path, snapshot);

        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        Assert.IsTrue(evaluation.HasParentTraversal);
    }

    [TestMethod]
    public void EvaluatorFlagsCp932UnsupportedResourcePath()
    {
        BMSFile file = CreateBmsFileWithResource(
            @"D:\BMS\Pack\Song\chart.bms",
            new ChartResourceReference(ChartResourceKind.Audio, @"sound\😀.wav", @"sound\😀.wav"));
        ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.path, snapshot);

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
        BMSFile.BmsEncodingDetectionResult detectionResult = BMSFile.DetectEncodingOfBMSFileDetailed(snapshot);
        BMSFile file = BMSFile.CreateBMSFileFromSnapshot(snapshot, detectionResult);
        ChartResourceSnapshot resources = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.path, resources);

        Assert.IsTrue(file.UnsupportedResourceReferences.Any(reference => reference.Reason == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported));
        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported));
        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        Assert.AreEqual(StrictShiftJisByteCount(longRawPath), evaluation.MaxRelativeCp932Bytes);
    }

    [TestMethod]
    public void EvaluatorUsesAllRawResourceReferencesEvenWhenLookupKeyIsDuplicate()
    {
        string longRawPath = string.Concat(Enumerable.Repeat(@".\", 130)) + "sound.wav";
        BMSFile file = new BMSFile
        {
            path = @"D:\BMS\Pack\Song\chart.bms",
            WAVfiles = ["sound.wav"],
            BGAfiles = [],
            ResourceReferences =
            [
                new ChartResourceReference(ChartResourceKind.Audio, @".\sound.wav", "sound.wav"),
                new ChartResourceReference(ChartResourceKind.Audio, longRawPath, "sound.wav")
            ]
        };
        ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.path, snapshot);

        Assert.AreEqual(1, snapshot.AudioReferenceCount);
        Assert.AreEqual(2, snapshot.ResourceReferences.Count);
        Assert.IsFalse(evaluation.WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        Assert.AreEqual(StrictShiftJisByteCount("sound.wav"), evaluation.MaxRelativeCp932Bytes);
    }

    [TestMethod]
    public void RelocatedParentTraversalFactsArePreservedWhenSnapshotReadFails()
    {
        var info = new BMSFileMaintenanceInfo
        {
            lr2_warning_flags = (int)(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported
                | Lr2CompatibilityWarningFlags.ResourcePathTooLong),
            lr2_resource_max_relative_cp932_bytes = 120,
            lr2_resource_has_parent_traversal = true
        };

        Lr2CompatibilityEvaluator.RefreshRelocatedMaintenanceFacts(
            info,
            @"D:\BMS\Moved\chart.bms",
            () => throw new IOException("locked"));

        var flags = (Lr2CompatibilityWarningFlags)info.lr2_warning_flags.GetValueOrDefault();
        Assert.IsTrue(flags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported));
        Assert.IsTrue(flags.HasFlag(Lr2CompatibilityWarningFlags.ResourcePathTooLong));
        Assert.AreEqual(120, info.lr2_resource_max_relative_cp932_bytes);
        Assert.IsTrue(info.lr2_resource_has_parent_traversal.GetValueOrDefault());
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

    private static BMSFile CreateBmsFileWithResource(string path, ChartResourceReference reference)
    {
        return new BMSFile
        {
            path = path,
            WAVfiles = [reference.NormalizedPath],
            BGAfiles = [],
            ResourceReferences = [reference]
        };
    }

    private static ChartFile CreateChart(BMSFile file)
    {
        return new ChartFile(
            ChartFileKind.Bms,
            file.path,
            file.hash,
            file.sha256,
            file.Title,
            file.GetRawTitleForDisplay(),
            file.Artist,
            file.genre,
            "Song",
            file.tag,
            file.level?.ToString(),
            null,
            file.mode,
            null,
            file,
            null);
    }
}
