using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsLibraryPackageInstallServiceTests
{
    /// <summary>実発見の全定義と最寄りchartdirで限定禁止を判定します。補助画像で元一式を捕捉し、分割の偶然で陰性にならない条件表です。通常Resourcesは既存の使用分投影と一致させます。</summary>
    [DataTestMethod]
    [DataRow("audio-bms", true)]
    [DataRow("audio-pms", true)]
    [DataRow("audio-alias", true)]
    [DataRow("audio-bmson", true)]
    [DataRow("image", true)]
    [DataRow("movie", true)]
    [DataRow("unused", true)]
    [DataRow("parent-subdirectory", true)]
    [DataRow("empty-middle", true)]
    [DataRow("nearest-child", false)]
    [DataRow("parent-reference", false)]
    [DataRow("resource-only-child", false)]
    [DataRow("disabled-child", false)]
    [DataRow("banner-only", false)]
    [DataRow("backbmp-only", false)]
    [DataRow("stagefile-only", false)]
    [DataRow("undefined-media", false)]
    [DataRow("missing", false)]
    [DataRow("invalid-resource-path", false)]
    [DataRow("bms-preview", false)]
    [DataRow("bmson-preview", false)]
    [DataRow("bmson-preview-normal", true)]
    [DataRow("siblings", false)]
    public void PrepareAutoInstallWorkflow_ResourceBearingNestedChartsUsesDefinitionsAndNearestOwner(string condition, bool blocked)
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "Input");
            string child = Path.Combine(source, condition == "empty-middle" ? Path.Combine("middle", "child") : "child");
            Directory.CreateDirectory(child);
            string parentBody = "#TITLE Parent\n#BACKBMP cover.png\n";
            if (condition == "nearest-child") { parentBody += "#WAV01 child/tone.wav\n#00111:01\n"; }
            else if (condition != "siblings")
            {
                string parentAudio = condition == "parent-subdirectory" ? "sound/tone.wav" : condition == "image" ? "parent.png" : condition == "movie" ? "parent.mp4" : "tone.wav";
                parentBody += (condition is "image" or "movie" ? "#BMP01 " : "#WAV01 ") + parentAudio + (condition is "image" or "movie" ? "\n#00104:01\n" : "\n#00111:01\n");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, parentAudio)) ?? throw new AssertFailedException());
                File.WriteAllBytes(Path.Combine(source, parentAudio), [1, 3]);
            }
            CreateBmsFile(source, "parent.bms", parentBody);
            File.WriteAllBytes(Path.Combine(source, "cover.png"), [4]);
            string childBody = "#TITLE Child\n#WAV01 tone.wav\n#00111:01\n";
            string media = "tone.wav";
            if (condition is "image" or "movie")
            {
                media = condition == "image" ? "visual.png" : "visual.mp4";
                childBody = "#TITLE Child\n#BMP01 " + media + "\n#00104:01\n";
            }
            else if (condition == "invalid-resource-path") { childBody = "#TITLE Child\n#WAV01 bad:name.wav\n#00111:01\n"; }
            else if (condition == "unused") { childBody = "#TITLE Child\n#WAV01 tone.wav\n"; }
            else if (condition == "parent-reference") { childBody = "#TITLE Child\n#WAV01 ../tone.wav\n#00111:01\n"; }
            else if (condition is "banner-only" or "backbmp-only" or "stagefile-only")
            {
                media = "optional.png";
                childBody = "#TITLE Child\n#" + condition.Split('-')[0].ToUpperInvariant() + " " + media + "\n";
            }
            else if (condition == "undefined-media") { childBody = "#TITLE Child\n"; }
            else if (condition == "bms-preview") { childBody = "#TITLE Child\n#PREVIEW tone.wav\n"; }
            else if (condition == "audio-alias") { media = "tone.ogg"; }
            if (condition == "empty-middle") { CreateBmsFile(Path.Combine(source, "middle"), "middle.bms", "#TITLE Middle\n"); }
            if (condition == "siblings")
            {
                string sibling = Path.Combine(source, "sibling");
                CreateBmsFile(sibling, "sibling.bms", "#TITLE Sibling\n#WAV01 tone.wav\n#00111:01\n");
                File.WriteAllBytes(Path.Combine(sibling, "tone.wav"), [2]);
            }
            if (condition is "audio-bmson" or "bmson-preview" or "bmson-preview-normal")
            {
                bool preview = condition.StartsWith("bmson-preview", StringComparison.Ordinal);
                string channels = condition == "bmson-preview" ? "[]" : "[{\"name\":\"tone.wav\",\"notes\":[]}]";
                File.WriteAllText(Path.Combine(child, "child.bmson"), "{\"info\":{\"title\":\"Child\"" + (preview ? ",\"preview_music\":\"tone.wav\"" : "") + "},\"sound_channels\":" + channels + "}");
            }
            else if (condition != "resource-only-child")
            {
                CreateBmsFile(child, "child." + (condition == "audio-pms" ? "pms" : condition == "disabled-child" ? "bms_" : "bms"), childBody);
            }
            if (condition is not "missing" and not "parent-reference") { File.WriteAllBytes(Path.Combine(child, media), [5, 7]); }

            AutoInstallWorkflowResult result = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow([source], [], [], _ => false);
            ChartPackage package = result.DiscoveredPackages.Single();
            Assert.AreEqual(source, package.path, "明示した元一式で判定します。");
            Assert.AreEqual(blocked, package.HasResourceBearingNestedCharts);
            Assert.AreEqual(blocked, package.ChartEntries.All(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBearingNestedCharts)));
            if (blocked)
            {
                Assert.IsTrue(result.PendingPackagesToAdd.Contains(package));
                Assert.IsFalse(result.AutoInstallCandidates.Contains(package));
                Assert.IsTrue(package.ChartEntries.All(entry => LibraryChartRow.FromPackageChartEntry(entry).HasHighlightedWarning));
                Assert.IsTrue(LibraryChartRow.FromPackageChartEntry(package.ChartEntries.Single(entry => entry.Chart.Path == Path.Combine(source, "parent.bms"))).HasHighlightedWarning);
            }
            foreach (PackageChartEntry entry in package.ChartEntries)
            {
                ChartFile parsed = PackageChartEntry.FromPath(entry.Chart.Path).Chart;
                string[] expected = parsed.Resources.Where(reference => reference.IsUsed || reference.Usage == ChartResourceUsage.InputDiagnostic)
                    .Select(reference => reference.Kind + "|" + reference.Usage + "|" + reference.RawPath + "|" + reference.IsUsed).ToArray();
                CollectionAssert.AreEqual(expected, entry.Chart.Resources.Select(reference => reference.Kind + "|" + reference.Usage + "|" + reference.RawPath + "|" + reference.IsUsed).ToArray());
            }
        });
    }
    /// <summary>通常Resourcesが取得済みでも未使用定義が解放済みの元packageは明示読取りで判定します。FromChart自体の契約と通常資源の投影を変えないことを確認します。</summary>
    [TestMethod]
    public void CaptureResourceBearingNestedCharts_NonNullUsedResourcesDoNotProveAllDefinitionsWereCaptured()
    {
        WithTemporaryDirectory(root =>
        {
            string child = Path.Combine(root, "child");
            string parentPath = CreateBmsFile(root, "parent.bms", "#TITLE Parent\n#WAV01 parent.wav\n");
            string childPath = CreateBmsFile(child, "child.bms", "#TITLE Child\n#WAV01 child.wav\n");
            File.WriteAllBytes(Path.Combine(root, "parent.wav"), [1]);
            File.WriteAllBytes(Path.Combine(child, "child.wav"), [2]);
            PackageChartEntry[] entries = new[] { parentPath, childPath }.Select(path =>
                PackageChartEntry.FromChart(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)) with { Resources = [] })).ToArray();
            var package = ChartPackage.FromChartEntries(entries);
            package.path = root;

            BmsLibraryPackageInstallService.CaptureResourceBearingNestedCharts(package);

            Assert.IsTrue(package.HasResourceBearingNestedCharts);
            Assert.IsTrue(entries.All(entry => entry.Chart.Resources.Count == 0));
        });
    }

}
