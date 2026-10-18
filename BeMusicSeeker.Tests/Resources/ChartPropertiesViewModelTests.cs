using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartPropertiesViewModelTests
{
    /// <summary>開く時点の保留配置を捕捉し、背景読取りをgateで停止中の候補・設定先変更へ追従しません。配置入力の捕捉と三分類の反映は機能側で確認し、実WindowのBindingは既存画面テストへ分担します。</summary>
    [TestMethod]
    public async Task LoadAsync_KeepsPlacementCapturedBeforeDestinationChanges()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("chart-properties-placement-");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = Task.CompletedTask;
        try
        {
            string path = System.IO.Path.Combine(directory.FullName, "chart.bms");
            File.WriteAllText(path, "#WAV01 tone.wav\n#00111:01\n");
            var entry = PackageChartEntry.FromPath(path);
            entry.ApplyInstallDestinationMetadata(@"C:\Original", "", "");
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(@"C:\Original", []);
            cache.AddDir(@"C:\Later", ["tone.ogg"]);
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], null, cache);
            var target = new ChartOperationTarget(entry.Chart, null, ChartOperationSourceScope.PendingPackage, false, true, false, ChartOperationCapabilities.None, entry);
            ChartResourcePlacement placement = ChartPropertiesViewModel.CapturePlacement(target);
            using var model = new ChartPropertiesViewModel(entry.Chart, () =>
            {
                entered.TrySetResult();
                release.Wait();
                return new ChartResourceDiagnosticsService().Read(path, ChartFileKind.Bms, placement);
            }, placement);
            operation = model.LoadAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "chart-properties-placement-entered");
            entry.ApplyInstallDestinationMetadata(@"C:\Later", "", "");
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], null, cache);
            Assert.AreEqual(100, entry.Chart.WAVHealth);
            Assert.AreEqual(Resources.ChartPropertiesConfiguredDestination, model.VerificationBasis);
            Assert.AreEqual(@"C:\Original", model.VerificationPath);
            release.Set();
            await operation;
            StringAssert.Contains(model.Issues, "tone.wav");
            StringAssert.Contains(model.Issues, Resources.ChartPropertiesMissing);
            Assert.AreEqual(@"C:\Original", model.VerificationPath);
        }
        finally
        {
            release.Set();
            await operation;
            directory.Delete(true);
        }
    }

    /// <summary>構築時には読まず、背景実行をgateで観測して二度の要求でも一回だけ読みます。成功した三分類とメタデータを表示へ渡し、実Taskをfinallyで回収します。</summary>
    [TestMethod]
    public async Task LoadAsync_RunsOnceInBackgroundAndPublishesResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int calls = 0;
        using var model = new ChartPropertiesViewModel(ChartTestValues.Empty(), () =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Wait();
            return new ChartResourceDiagnostics("Read title", "Read artist", [],
                [new ChartResourceDefinition("#WAV", "02", ChartResourceReference.Parse("unused.wav", ChartResourceKind.Audio))], [new ChartResourceReferenceNumber("#WAV", "ZZ")]);
        });
        Assert.AreEqual(0, calls);
        Assert.AreEqual(string.Empty, model.Status);
        Assert.AreEqual(string.Empty, model.Issues);
        Task operation = model.LoadAsync();
        Task second = model.LoadAsync();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "chart-properties-read-entered");
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(string.Empty, model.Status);
            Assert.AreEqual(string.Empty, model.Issues);
            release.Set();
            await Task.WhenAll(operation, second);
            Assert.AreEqual(string.Empty, model.Status);
            Assert.AreEqual("Read title", model.Title);
            Assert.AreEqual("Read artist", model.Artist);
            Assert.IsTrue(model.UnusedDefinitions.Contains("02") && model.UnusedDefinitions.Contains("unused.wav"));
            Assert.AreEqual("#WAVZZ", model.UndefinedReferences);
            Assert.AreEqual(Resources.None, model.Issues);
            Assert.AreEqual(1, calls);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(operation, second);
        }
    }

    /// <summary>読取り中に閉じ、遅延成功と遅延I/O失敗のどちらも開始済みTaskが正常に回収されるまで待ちます。閉鎖後の表示変更通知と結果反映を認めません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Close_ObservesStartedReadWithoutPublishingAfterClose(bool fail)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var model = new ChartPropertiesViewModel(ChartTestValues.Empty(), () =>
        {
            entered.TrySetResult();
            release.Wait();
            if (fail) { throw new IOException("closed read failure"); }
            return new ChartResourceDiagnostics("Late title", "Late artist", [], [], []);
        });
        int notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;
        string initialStatus = model.Status;
        Task operation = model.LoadAsync();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "chart-properties-close-entered");
            model.Close();
            release.Set();
            await operation;
            Assert.AreEqual(0, notifications);
            Assert.AreEqual(initialStatus, model.Status);
            Assert.AreEqual(string.Empty, model.Title);
            Assert.AreEqual(string.Empty, model.Issues);
        }
        finally
        {
            release.Set();
            await operation;
        }
    }

    /// <summary>取得・解析失敗だけにローカライズした失敗文言を残し、例外本文を表示へ渡しません。成功の空一覧と読取中の空欄は別の時点で検証します。</summary>
    [TestMethod]
    public async Task LoadAsync_ExposesLocalizedFailureWithoutExceptionDetails()
    {
        using var failure = new ChartPropertiesViewModel(ChartTestValues.Empty(), () => throw new IOException("private read failure"));
        Assert.AreEqual(string.Empty, failure.Status);
        await failure.LoadAsync();
        Assert.AreEqual(Resources.ChartPropertiesFailed, failure.Status);
        Assert.IsFalse(failure.Status.Contains("private read failure", StringComparison.Ordinal));
    }

    /// <summary>定義元と定義番号をコマンド風またはBMSON原項目名で整形し、理由を末尾へ置きます。拡張子/分類が逆でも出所を変えず、形式に合う説明を選択します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoadAsync_FormatsSourceCommandsAndFormatSpecificDescriptions(bool bmson)
    {
        ChartResourceDefinition first = bmson
            ? new("sound_channels[0]", null, ChartResourceReference.Parse("sound.wav", ChartResourceKind.Audio))
            : new("#WAV", "01", ChartResourceReference.Parse("visual.png", ChartResourceKind.Image));
        ChartResourceDefinition second = bmson
            ? new("bga_header[0]", "7", ChartResourceReference.Parse("visual.png", ChartResourceKind.Image))
            : new("#BMP", "02", ChartResourceReference.Parse("sound.wav", ChartResourceKind.Audio));
        ChartResourceReferenceNumber number = bmson ? new("bga_header", "9") : new("#BMP", "01");
        using var model = new ChartPropertiesViewModel(ChartTestValues.Empty(bmson ? ChartFileKind.Bmson : ChartFileKind.Bms),
            () => new ChartResourceDiagnostics("Title", "Artist", [new(first, ChartResourceDiagnosticReason.Missing)], [second], [number]));
        await model.LoadAsync();
        StringAssert.StartsWith(model.Issues, bmson ? "sound_channels[0]" : "#WAV01");
        StringAssert.EndsWith(model.Issues, "(" + Resources.ChartPropertiesMissing + ")");
        StringAssert.Contains(model.UnusedDefinitions, bmson ? "bga_header[0]" : "#BMP02");
        Assert.IsFalse(model.UnusedDefinitions.Contains("#WAV", StringComparison.Ordinal));
        StringAssert.StartsWith(model.UndefinedReferences, bmson ? "bga_header" : "#BMP01");
        Assert.AreEqual(bmson ? Resources.ChartPropertiesBmsonUnusedDescription : Resources.ChartPropertiesUnusedDescription, model.UnusedDescription);
        Assert.AreEqual(bmson ? Resources.ChartPropertiesBmsonUndefinedDescription : Resources.ChartPropertiesUndefinedDescription, model.UndefinedDescription);
        Assert.AreEqual(string.Empty, model.Status);
    }

    /// <summary>実ファイルのある単一選択を所持・保留の両方で許可し、複数選択・未所持・実ファイル欠損を拒みます。ファイル内容の解析を必要としません。</summary>
    [TestMethod]
    public void ResolveTarget_AllowsSingleLocalAndPendingFilesOnly()
    {
        string path = Path.GetTempFileName();
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
