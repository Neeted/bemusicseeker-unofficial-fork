using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Linq;
using System.Text;

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

        Assert.AreEqual(Lr2PathWarningFlags.None, evaluation.WarningFlags);
        Assert.IsTrue(evaluation.CanComputeFolderParent);
        Assert.AreEqual("f002e300", evaluation.FolderHash);
        Assert.AreEqual("a777506c", evaluation.ParentHash);
        Assert.AreEqual(StrictShiftJisByteCount(chartPath), evaluation.ChartPathCp932Bytes);
        Assert.AreEqual(StrictShiftJisByteCount(@"D:\BMS\Pack\Song\*.*"), evaluation.FolderScanPathCp932Bytes);
    }

    [TestMethod]
    public void EvaluatorFlagsCp932UnsupportedPath()
    {
        Lr2ChartPathEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateChartPath(@"D:\BMS\emoji_😀\chart.bms");

        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2PathWarningFlags.PathEncodingUnsupported));
        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2PathWarningFlags.FolderScanPathEncodingUnsupported));
        Assert.IsFalse(evaluation.CanComputeFolderParent);
        Assert.IsNull(evaluation.ChartPathCp932Bytes);
        Assert.IsNull(evaluation.FolderHash);
        Assert.IsNull(evaluation.ParentHash);
    }

    [TestMethod]
    public void EvaluatorFlagsChartPathByteBoundary()
    {
        Lr2ChartPathEvaluation max = Lr2CompatibilityEvaluator.EvaluateChartPath(BuildAsciiChartPath(259));
        Lr2ChartPathEvaluation tooLong = Lr2CompatibilityEvaluator.EvaluateChartPath(BuildAsciiChartPath(260));

        Assert.AreEqual(259, max.ChartPathCp932Bytes);
        Assert.IsFalse(max.WarningFlags.HasFlag(Lr2PathWarningFlags.PathTooLong));
        Assert.AreEqual(260, tooLong.ChartPathCp932Bytes);
        Assert.IsTrue(tooLong.WarningFlags.HasFlag(Lr2PathWarningFlags.PathTooLong));
    }

    [TestMethod]
    public void EvaluatorUsesRawResourcePathWithExtensionForByteFacts()
    {
        BMSFile file = CreateBmsFileWithResource(
            @"D:\BMS\Pack\Song\chart.bms",
            new ChartResourceReference(ChartResourceKind.Audio, "4.org1_1.wav", "4.org1_1.wav"));
        ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.path, snapshot);

        Assert.AreEqual(Lr2ResourceWarningFlags.None, evaluation.WarningFlags);
        Assert.AreEqual(StrictShiftJisByteCount("4.org1_1.wav"), evaluation.MaxRawCp932Bytes);
        Assert.AreEqual(
            StrictShiftJisByteCount(Path.GetFullPath(@"D:\BMS\Pack\Song\4.org1_1.wav")),
            evaluation.MaxResolvedCp932Bytes);
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

        Assert.AreEqual(0, evaluation.UnsupportedCount);
        Assert.AreEqual(Lr2ResourceWarningFlags.None, evaluation.WarningFlags);
        Assert.AreEqual(StrictShiftJisByteCount(parentTraversalPath), evaluation.MaxRawCp932Bytes);
        Assert.AreEqual(ExpectedLr2ResourcePathByteCount(chartPath, parentTraversalPath), evaluation.MaxResolvedCp932Bytes);
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

        Assert.AreEqual(0, evaluation.UnsupportedCount);
        Assert.IsFalse(evaluation.WarningFlags.HasFlag(Lr2ResourceWarningFlags.RawPathTooLong));
        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2ResourceWarningFlags.ResolvedPathTooLong));
        Assert.AreEqual(ExpectedLr2ResourcePathByteCount(chartPath, parentTraversalPath), evaluation.MaxResolvedCp932Bytes);
    }

    [TestMethod]
    public void EvaluatorFlagsCp932UnsupportedResourcePath()
    {
        BMSFile file = CreateBmsFileWithResource(
            @"D:\BMS\Pack\Song\chart.bms",
            new ChartResourceReference(ChartResourceKind.Audio, @"sound\😀.wav", @"sound\😀.wav"));
        ChartResourceSnapshot snapshot = ChartResourceSnapshot.Create(CreateChart(file));

        Lr2ResourceReferenceEvaluation evaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(file.path, snapshot);

        Assert.AreEqual(0, evaluation.UnsupportedCount);
        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2ResourceWarningFlags.RawPathEncodingUnsupported));
        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2ResourceWarningFlags.ResolvedPathEncodingUnsupported));
        Assert.IsNull(evaluation.MaxRawCp932Bytes);
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
        Assert.IsTrue(evaluation.WarningFlags.HasFlag(Lr2ResourceWarningFlags.RawPathTooLong));
        Assert.AreEqual(StrictShiftJisByteCount(longRawPath), evaluation.MaxRawCp932Bytes);
    }

    private static int StrictShiftJisByteCount(string value)
    {
        return Encoding.GetEncoding(
            "shift_jis",
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback)
            .GetByteCount(value);
    }

    private static int ExpectedLr2ResourcePathByteCount(string chartPath, string relativePath)
    {
        string directory = Path.GetDirectoryName(chartPath);
        return StrictShiftJisByteCount(directory) + 1 + StrictShiftJisByteCount(relativePath);
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
