using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsLibraryPackageInstallServiceTests
{
    /// <summary>直接file指定から新しい三根拠の実在・相対範囲・音声互換を確認し、通常音声の参照数・不足とPREVIEW非保存を維持します。全選択や通常音源による偶然のfolder化を使いません。</summary>
    [DataTestMethod]
    [DataRow("stagefile", true)]
    [DataRow("backbmp", true)]
    [DataRow("banner", true)]
    [DataRow("preview", true)]
    [DataRow("preview-sub", true)]
    [DataRow("preview-case", true)]
    [DataRow("preview-first", true)]
    [DataRow("preview-cp932", true)]
    [DataRow("preview-missing", false)]
    [DataRow("preview-parent", false)]
    [DataRow("preview-rooted", false)]
    [DataRow("preview-invalid", false)]
    [DataRow("prefix", true)]
    [DataRow("prefix-image", false)]
    [DataRow("prefix-child", false)]
    public void PrepareAutoInstallWorkflow_SupplementEvidencePromotesOnlyActualAdjacentScope(string kind, bool promoted)
    {
        WithTemporaryDirectory(root =>
        {
            string directory = Path.Combine(root, "Input");
            Directory.CreateDirectory(directory);
            string directive = string.Empty;
            string resource = "trailer.ogg";
            if (kind is "stagefile" or "backbmp" or "banner")
            {
                directive = "#" + kind.ToUpperInvariant() + " artwork.png\n";
                resource = "artwork.png";
            }
            else if (kind.StartsWith("preview", StringComparison.Ordinal))
            {
                string reference = kind switch
                {
                    "preview-sub" => "media/trailer.wav",
                    "preview-case" => "TRAILER.WAV",
                    "preview-parent" => "../trailer.wav",
                    "preview-rooted" => Path.Combine(root, "trailer.wav"),
                    "preview-invalid" => "bad|name.wav",
                    "preview-cp932" => "試聴.wav",
                    _ => "trailer.wav"
                };
                directive = "  #pReViEw\t" + reference + "\n";
                if (kind == "preview-first") { directive = "#PREVIEW   \n" + directive + "#PREVIEW missing.wav\n"; }
                if (kind == "preview-sub") { resource = Path.Combine("media", "trailer.mp3"); }
                if (kind == "preview-cp932") { resource = "試聴.flac"; }
                if (kind == "preview-missing") { resource = "unrelated.ogg"; }
            }
            else { resource = kind switch { "prefix" => "PrEvIeW_demo.FLAC", "prefix-image" => "preview.png", _ => Path.Combine("media", "preview.wav") }; }
            string physical = kind is "preview-parent" or "preview-rooted" ? Path.Combine(root, "trailer.wav") : Path.Combine(directory, resource);
            Directory.CreateDirectory(Path.GetDirectoryName(physical) ?? throw new AssertFailedException());
            File.WriteAllBytes(physical, [9, 7]);
            string first = Path.Combine(directory, "a.pms");
            string second = Path.Combine(directory, "b.bms");
            File.WriteAllText(first, "#TITLE Song\n#ARTIST Artist\n#WAV01 missing-a.wav\n#00111:01\n" + directive, Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(second, "#TITLE Other\n#WAV01 missing-b.wav\n#00111:01\n", Encoding.ASCII);
            File.WriteAllText(Path.Combine(directory, "readme.txt"), "unselected");
            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow([first], [], [], _ => false);
            ChartPackage package = result.DiscoveredPackages.Single();
            Assert.AreEqual(promoted ? directory : first, package.path);
            CollectionAssert.AreEquivalent(promoted ? new[] { first, second } : new[] { first }, package.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
            ChartFile chart = package.ChartEntries.Single(entry => entry.Chart.Path == first).Chart;
            var resources = ChartResourceSnapshot.Create(chart.Resources, includeUnused: true);
            Assert.AreEqual(1, resources.AudioReferenceCount);
            Assert.AreEqual(0, chart.WAVHealth);
            Assert.AreEqual(string.Empty, chart.PreviewMusic);
            Assert.IsFalse(resources.HasUnsupportedParentTraversalReference, "発見専用PREVIEWの親参照を通常資源へ混入させません。");
        });
    }

    /// <summary>資源なしの明示folderと中身全選択は非譜面の子を含む一式、A/B部分選択は二単体です。譜面childを含む差分集では親直下を単体化し、親資源があれば全一式を優先します。</summary>
    [DataTestMethod]
    [DataRow("folder", false, false)]
    [DataRow("all", false, false)]
    [DataRow("partial", false, false)]
    [DataRow("folder", true, false)]
    [DataRow("all", true, false)]
    [DataRow("folder", true, true)]
    [DataRow("all", true, true)]
    public void PrepareAutoInstallWorkflow_PreservesInputUnitBeforeDestinationEstimation(string selection, bool chartChildren, bool parentResource)
    {
        WithTemporaryDirectory(root =>
        {
            string directory = Path.Combine(root, "Input");
            Directory.CreateDirectory(directory);
            string a = CreateBmsFile(directory, "a.bms", "#TITLE A\n#WAV01 a.wav\n#00111:01\n" + (parentResource ? "#BACKBMP cover.png\n" : ""));
            string b = CreateBmsFile(directory, "b.bms", "#TITLE B\n#WAV01 b.wav\n#00111:01\n");
            File.WriteAllText(Path.Combine(directory, "readme.txt"), "readme");
            string sub = Path.Combine(directory, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, "omakefile.txt"), [2]);
            var expected = new List<string> { directory };
            if (chartChildren)
            {
                string song1 = Path.Combine(directory, "song1");
                string song2 = Path.Combine(directory, "song2");
                CreateBmsFile(song1, "c.bms", "#TITLE C\n#WAV01 c.wav\n#00111:01\n");
                CreateBmsFile(song2, "d.bms", "#TITLE D\n#WAV01 add.ogg\n#00111:01\n");
                File.WriteAllBytes(Path.Combine(song2, "add.ogg"), [3]);
                if (!parentResource) { expected = [a, b, song1, song2]; }
            }
            if (parentResource) { File.WriteAllBytes(Path.Combine(directory, "cover.png"), [4]); }
            if (selection == "partial") { expected = [a, b]; }
            string[] paths = selection switch { "folder" => [directory], "all" => Directory.GetFileSystemEntries(directory), _ => [a, b] };
            AutoInstallWorkflowResult result = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow(paths, [], [], _ => false);
            CollectionAssert.AreEquivalent(expected, result.DiscoveredPackages.Select(package => package.path).ToArray());
            Assert.AreEqual(chartChildren ? 4 : 2, result.DiscoveredPackages.SelectMany(package => package.ChartEntries).Count());
        });
    }
}
