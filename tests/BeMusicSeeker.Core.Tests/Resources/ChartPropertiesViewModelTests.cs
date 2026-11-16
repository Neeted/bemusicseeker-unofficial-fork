using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartPropertiesViewModelTests
{
    /// <summary>実読取りの既定経路が、開く時点の固定Big5指定を使います。後の項目変更へ追従せず、ASCIIメタデータだけでは分からない非ASCII資源の不足を誤表示しません。</summary>
    [TestMethod]
    public async Task LoadAsync_DefaultReaderUsesCapturedFixedEncodingForResourceNames()
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        DirectoryInfo directory = Directory.CreateDirectory(Path.Combine(caseWorkspace.RootDirectory, "chart-properties-encoding-" + Guid.NewGuid().ToString("N")));
        ChartPropertiesViewModel? model = null;
        try
        {
            string path = System.IO.Path.Combine(directory.FullName, "chart.bms");
            File.WriteAllText(path, "#TITLE ASCII\n#WAV01 中文.wav\n#00111:01\n", System.Text.Encoding.GetEncoding("big5"));
            File.WriteAllBytes(System.IO.Path.Combine(directory.FullName, "中文.ogg"), []);
            ChartFile parsed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path), "big5");
            ChartFile fixedChart = ChartFileProjection.WithMaintenance(parsed,
                BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(parsed) with { Encoding = "big5", EncodingFixed = true });
            var entry = PackageChartEntry.FromChart(fixedChart);
            model = new ChartPropertiesViewModel(entry.Chart);
            entry.ApplyCurrentChart(ChartFileProjection.WithMaintenance(entry.Chart,
                entry.Chart.ResourceHealthMaintenanceSnapshot with { Encoding = "utf-8" }));
            await model.LoadAsync();
            Assert.AreEqual("ASCII", model.Title);
            Assert.IsFalse(model.Issues.Contains(Resources.ChartPropertiesMissing, StringComparison.Ordinal));
        }
        finally
        {
            model?.Close();
            model?.Dispose();
            directory.Delete(recursive: true);
        }
    }

    /// <summary>実ファイルのある単一選択を所持・保留の両方で許可し、複数選択・未所持・実ファイル欠損を拒みます。ファイル内容の解析を必要としません。</summary>
    [TestMethod]
    public void ResolveTarget_AllowsSingleLocalAndPendingFilesOnly()
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = caseWorkspace.GetPath("chart.tmp");
        File.WriteAllBytes(path, []);
        try
        {
            ChartFile chart = ChartTestValues.Empty() with { Path = path };
            ChartOperationTarget owned = Target(chart, false, false);
            ChartOperationTarget pending = Target(chart, true, false);
            Assert.IsNotNull(ChartPropertiesViewModel.ResolveTarget([owned]));
            Assert.IsNotNull(ChartPropertiesViewModel.ResolveTarget([pending]));
            Assert.IsNull(ChartPropertiesViewModel.ResolveTarget([]));
            Assert.IsNull(ChartPropertiesViewModel.ResolveTarget([owned, pending]));
            Assert.IsNull(ChartPropertiesViewModel.ResolveTarget([Target(chart, false, true)]));
            File.Delete(path);
            Assert.IsNull(ChartPropertiesViewModel.ResolveTarget([owned]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ChartOperationTarget Target(ChartFile chart, bool pending, bool missing)
        => new(chart, null, pending ? ChartOperationSourceScope.PendingPackage : ChartOperationSourceScope.Library,
            !pending && !missing, pending, missing, ChartOperationCapabilities.None);

}
