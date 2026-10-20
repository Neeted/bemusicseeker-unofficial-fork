using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsLibraryPackageInstallServiceTests
{
    /// <summary>計算済みの不足・0件・未判定を12値とも復元し、現在の文字コード・除外・LR2診断と他分類警告を保ちます。未取得資源と不存在譜面を使い、再解析へ戻る実装を検出します。</summary>
    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    public void ClearPendingInstallDestination_RestoresOnlyOriginalHealth(int condition, bool ignored)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "unreadable.bms");
        var original = new ResourceHealthMaintenanceSnapshot
        {
            Path = path,
            Hash = "original",
            Origin = MaintenanceInfoOrigin.Calculated,
            WavFilesDefined = condition == 2 ? null : condition == 1 ? 0 : 4,
            WavFilesExisting = condition == 2 ? null : condition == 1 ? 0 : 1,
            BgaFilesDefined = condition == 2 ? null : condition == 1 ? 0 : 3,
            BgaFilesExisting = condition == 2 ? null : condition == 1 ? 0 : 2,
            MovieFilesDefined = condition == 2 ? null : condition == 1 ? 0 : 2,
            MovieFilesExisting = condition == 2 ? null : 0,
            StagefileDefined = condition == 2 ? null : condition != 1,
            StagefileExisting = condition == 2 ? null : false,
            BannerDefined = condition == 2 ? null : condition != 1,
            BannerExisting = condition == 2 ? null : condition != 1,
            BackbmpDefined = condition == 2 ? null : condition != 1,
            BackbmpExisting = condition == 2 ? null : false
        };
        var entry = PackageChartEntry.FromChart(ChartFileProjection.WithMaintenance(ChartTestValues.Empty() with
        {
            Path = path,
            Md5 = "original",
            Resources = null
        }, original));
        entry.InitializeOriginalResourceHealth(original);
        ResourceHealthMaintenanceSnapshot current = original with
        {
            Encoding = "shift_jis",
            EncodingFixed = true,
            FilesWarningIgnored = ignored,
            Lr2WarningFlags = 1,
            Lr2ResourceMaxRelativeCp932Bytes = 312,
            Lr2ResourceHasParentTraversal = true,
            WavFilesDefined = 40,
            BgaFilesDefined = 30,
            MovieFilesDefined = 20,
            StagefileDefined = false,
            BannerDefined = false,
            BackbmpDefined = false,
            WavFilesExisting = 4,
            BgaFilesExisting = 3,
            MovieFilesExisting = 2,
            StagefileExisting = true,
            BannerExisting = false,
            BackbmpExisting = true
        };
        entry.ApplyCurrentChart(ChartFileProjection.WithMaintenance(entry.Chart, current));
        entry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(@"C:\Candidate", "Title", "Artist", [@"C:\Candidate"]));
        entry.SetWarning(ChartWarningKind.InstallEstimationAudioMissing, "estimate");
        entry.SetWarning(ChartWarningKind.SingleBmsFile, "layout");
        entry.SetWarning(ChartWarningKind.AlreadyInstalled, "installed");
        var bundled = new DirectoryResourceLookupCache();
        bundled.AddDir(@"C:\Bundled", ["tone.wav"]);
        DirectoryResourceLookupCache.Entry context = bundled.GetEntryOrNull(@"C:\Bundled");
        entry.ReplaceResourceHealthProjection(current, [], new ChartResourcePlacement(ChartResourcePlacementBasis.Destination,
            @"C:\Candidate", context, bundledComplete: false));
        ChartWarning[] otherWarnings = [.. entry.Chart.Warnings.Where(warning => warning.Category != ChartWarningCategory.ResourceHealth && warning.Category != ChartWarningCategory.InstallEstimation)];

        BmsLibraryPackageInstallService.ClearPendingInstallDestination(entry);

        ChartFile result = entry.Chart;
        Assert.AreEqual(current with
        {
            WavFilesDefined = original.WavFilesDefined,
            BgaFilesDefined = original.BgaFilesDefined,
            MovieFilesDefined = original.MovieFilesDefined,
            StagefileDefined = original.StagefileDefined,
            BannerDefined = original.BannerDefined,
            BackbmpDefined = original.BackbmpDefined,
            WavFilesExisting = original.WavFilesExisting,
            BgaFilesExisting = original.BgaFilesExisting,
            MovieFilesExisting = original.MovieFilesExisting,
            StagefileExisting = original.StagefileExisting,
            BannerExisting = original.BannerExisting,
            BackbmpExisting = original.BackbmpExisting
        }, result.ResourceHealthMaintenanceSnapshot);
        Assert.AreEqual(condition == 0 ? 25 : null, result.WAVHealth);
        Assert.AreEqual(condition == 0 ? 66 : null, result.BGAHealth);
        Assert.AreEqual(condition == 0 ? 0 : null, result.MovieHealth);
        Assert.AreEqual(condition == 2 ? null : condition == 1, result.StagefileHealth);
        Assert.AreEqual(condition == 2 ? null : true, result.BannerHealth);
        Assert.AreEqual(condition == 2 ? null : condition == 1, result.BackbmpHealth);
        Assert.AreEqual("shift_jis", result.EncodingName);
        Assert.AreEqual(ignored, result.ResourceHealthWarningsIgnored);
        Assert.AreEqual(string.Empty, result.InstallDestination);
        Assert.AreEqual(string.Empty, result.InstallDestinationTitle);
        Assert.AreEqual(string.Empty, result.InstallDestinationArtist);
        Assert.AreEqual(0, result.InstallDestinationSuggestions.Count);
        Assert.IsFalse(result.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
        CollectionAssert.AreEquivalent(otherWarnings.Select(warning => (warning.Kind, warning.Message)).ToArray(),
            result.Warnings.Where(warning => warning.Category != ChartWarningCategory.ResourceHealth).Select(warning => (warning.Kind, warning.Message)).ToArray());
        ChartWarningKind[] expected = condition == 0
            ? [ChartWarningKind.ResourceWavMissing, ChartWarningKind.ResourceBgaMissing, ChartWarningKind.ResourceMovieMissing,
                ChartWarningKind.ResourceStagefileMissing, ChartWarningKind.ResourceBackbmpMissing] : [];
        CollectionAssert.AreEquivalent(expected, result.Warnings.Where(warning => warning.Category == ChartWarningCategory.ResourceHealth).Select(warning => warning.Kind).ToArray());
        Assert.AreEqual(ChartResourcePlacementBasis.Current, entry.ResourcePlacement.Basis);
        Assert.AreEqual(Path.GetDirectoryName(path), entry.ResourcePlacement.Directory);
        Assert.AreSame(context, entry.ResourcePlacement.BundledResources);
        Assert.IsFalse(entry.ResourcePlacement.BundledResourcesComplete);
        Assert.IsNull(result.Resources);
    }

    /// <summary>登録時結果の欠落は当該項目の消去前に失敗し、現在値の流用・再解析で救済しません。</summary>
    [TestMethod]
    public void ClearPendingInstallDestination_MissingOriginalResultLeavesEntryUnchanged()
    {
        var entry = PackageChartEntry.FromChart(ChartTestValues.Empty() with { Path = @"C:\Unavailable\chart.bms", Resources = null });
        entry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(@"C:\Candidate", "Title", "Artist", [@"C:\Candidate"]));
        entry.SetWarning(ChartWarningKind.InstallEstimationAudioMissing, "estimate");
        Assert.ThrowsException<InvalidOperationException>(() => BmsLibraryPackageInstallService.ClearPendingInstallDestination(entry));
        Assert.AreEqual(@"C:\Candidate", entry.Chart.InstallDestination);
        Assert.AreEqual("Title", entry.Chart.InstallDestinationTitle);
        Assert.AreEqual("Artist", entry.Chart.InstallDestinationArtist);
        CollectionAssert.AreEqual(new[] { @"C:\Candidate" }, entry.Chart.InstallDestinationSuggestions.ToArray());
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
    }

    /// <summary>実元配置の初期評価後に音源を追加・削除し、候補A/Bの通常評価を挟んでも登録時へ戻します。譜面と参照を解放してから消去し、復元での再読取りを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClearPendingInstallDestination_UsesRegistrationResultAfterExternalChanges(bool originallyPresent)
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "source");
            string path = CreateBmsFile(source, "chart.bms", "#TITLE Original\n#WAV01 tone.wav\n#00111:01\n");
            string audio = Path.Combine(source, "tone.wav");
            if (originallyPresent) { File.WriteAllText(audio, "original"); }
            var entry = PackageChartEntry.FromPath(path);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
            Assert.AreEqual(originallyPresent ? 100 : 0, entry.Chart.WAVHealth);
            if (originallyPresent) { File.Delete(audio); }
            else { File.WriteAllText(audio, "later"); }
            var cache = new DirectoryResourceLookupCache();
            string candidateA = Path.Combine(root, "a");
            string candidateB = Path.Combine(root, "b");
            cache.AddDir(candidateA, ["tone.wav"]);
            cache.AddDir(candidateB, []);
            cache.AddDir(source, []);
            foreach (string destination in new[] { candidateA, candidateB })
            {
                entry.SetInstallDestinationPathOnly(destination);
                BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], null, cache, cache.GetEntryOrNull(source));
                Assert.AreEqual(destination == candidateA ? 100 : 0, entry.Chart.WAVHealth);
            }
            entry.ApplyCurrentChart(entry.Chart with { Resources = null });
            File.Delete(path);

            BmsLibraryPackageInstallService.ClearPendingInstallDestination(entry);

            Assert.AreEqual(originallyPresent ? 100 : 0, entry.Chart.WAVHealth);
            Assert.AreEqual(!originallyPresent, entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual(1, entry.Chart.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(originallyPresent ? 1 : 0, entry.Chart.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
        });
    }
}
