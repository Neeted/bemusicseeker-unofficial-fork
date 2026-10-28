using System;
using System.Globalization;
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
    /// <summary>読取り中の言語変更は設定先という捕捉基準だけを再翻訳し、後から変わる候補・設定先へ追従しません。実読取りの不足理由と元の確認先を完成後も保持します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task LoadAsync_KeepsPlacementCapturedBeforeDestinationChanges()
    {
        CultureInfo? previousCulture = Resources.Culture;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("chart-properties-placement-");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = Task.CompletedTask;
        ChartPropertiesViewModel? model = null;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
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
            model = new ChartPropertiesViewModel(entry.Chart, () =>
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
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(Resources.ChartPropertiesConfiguredDestination, model.VerificationBasis);
            Assert.AreEqual(@"C:\Original", model.VerificationPath);
            release.Set();
            await operation;
            foreach (string culture in new[] { "en-US", "ja-JP" })
            {
                ResourceService.Current.ChangeCulture(culture);
                Assert.AreEqual("#WAV01 tone.wav (" + Resources.ChartPropertiesMissing + ")", model.Issues);
                Assert.AreEqual(Resources.ChartPropertiesConfiguredDestination, model.VerificationBasis);
                Assert.AreEqual(@"C:\Original", model.VerificationPath);
                Assert.AreEqual(path, model.Path);
            }
        }
        finally
        {
            model?.Close();
            release.Set();
            await operation;
            model?.Dispose();
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
            directory.Delete(true);
        }
    }

    /// <summary>初期と読取り中の三一覧・Statusは言語変更後も空に保ち、一回の背景読取りの成功後だけ三一覧を「なし」にします。完成後の言語往復でもメタデータと読取り回数を保持します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task LoadAsync_RunsOnceInBackgroundAndPublishesResult()
    {
        CultureInfo? previousCulture = Resources.Culture;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int calls = 0;
        using var model = new ChartPropertiesViewModel(ChartTestValues.Empty() with { Path = "chart.bms" }, () =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Wait();
            return new ChartResourceDiagnostics("Read title", "Read artist", [], [], []);
        });
        Task operation = Task.CompletedTask;
        Task second = Task.CompletedTask;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreEqual(0, calls);
            Assert.AreEqual(string.Empty, model.Status);
            Assert.AreEqual(string.Empty, model.Issues);
            Assert.AreEqual(string.Empty, model.UnusedDefinitions);
            Assert.AreEqual(string.Empty, model.UndefinedReferences);
            operation = model.LoadAsync();
            second = model.LoadAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "chart-properties-read-entered");
            ResourceService.Current.ChangeCulture("en-US");
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(string.Empty, model.Status);
            Assert.AreEqual(string.Empty, model.Issues);
            Assert.AreEqual(string.Empty, model.UnusedDefinitions);
            Assert.AreEqual(string.Empty, model.UndefinedReferences);
            release.Set();
            await Task.WhenAll(operation, second);
            foreach (string culture in new[] { "en-US", "ja-JP", "en-US" })
            {
                ResourceService.Current.ChangeCulture(culture);
                Assert.AreEqual(string.Empty, model.Status);
                Assert.AreEqual("Read title", model.Title);
                Assert.AreEqual("Read artist", model.Artist);
                Assert.AreEqual("chart.bms", model.Path);
                Assert.AreEqual(Resources.ChartPropertiesCurrentPlacement, model.VerificationBasis);
                Assert.AreEqual(Resources.None, model.Issues);
                Assert.AreEqual(Resources.None, model.UnusedDefinitions);
                Assert.AreEqual(Resources.None, model.UndefinedReferences);
                await model.LoadAsync();
                Assert.AreEqual(1, calls);
            }
        }
        finally
        {
            model.Close();
            release.Set();
            await Task.WhenAll(operation, second);
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
    }

    /// <summary>読取り中に閉じた後の言語変更と遅延成功・遅延I/O失敗は、どちらも表示変更を通知しません。開始済みTaskの失敗を回収し、初期の表示値を保持します。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Close_ObservesStartedReadWithoutPublishingAfterClose(bool fail)
    {
        CultureInfo? previousCulture = Resources.Culture;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var model = new ChartPropertiesViewModel(ChartTestValues.Empty(), () =>
        {
            entered.TrySetResult();
            release.Wait();
            if (fail) { throw new IOException("closed read failure"); }
            return new ChartResourceDiagnostics("Late title", "Late artist", [], [], []);
        });
        Task operation = Task.CompletedTask;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            operation = model.LoadAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "chart-properties-close-entered");
            model.Close();
            int notifications = 0;
            model.PropertyChanged += (_, _) => notifications++;
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(0, notifications);
            release.Set();
            await operation;
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreEqual(0, notifications);
            Assert.AreEqual(string.Empty, model.Status);
            Assert.AreEqual(string.Empty, model.Title);
            Assert.AreEqual(string.Empty, model.Artist);
            Assert.AreEqual(string.Empty, model.Issues);
            Assert.AreEqual(string.Empty, model.UnusedDefinitions);
            Assert.AreEqual(string.Empty, model.UndefinedReferences);
        }
        finally
        {
            model.Close();
            release.Set();
            await operation;
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
    }

    /// <summary>読取り中の言語変更後に失敗しても現言語の失敗文を表示し、その後も再翻訳します。例外本文を渡さず、三一覧を成功の「なし」へ置き換えません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task LoadAsync_ExposesLocalizedFailureWithoutExceptionDetails()
    {
        CultureInfo? previousCulture = Resources.Culture;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var failure = new ChartPropertiesViewModel(ChartTestValues.Empty(), () =>
        {
            entered.TrySetResult();
            release.Wait();
            throw new IOException("private read failure");
        });
        Task operation = Task.CompletedTask;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreEqual(string.Empty, failure.Status);
            operation = failure.LoadAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "chart-properties-failure-entered");
            ResourceService.Current.ChangeCulture("en-US");
            Assert.AreEqual(string.Empty, failure.Status);
            release.Set();
            await operation;
            foreach (string culture in new[] { "en-US", "ja-JP" })
            {
                ResourceService.Current.ChangeCulture(culture);
                Assert.AreEqual(Resources.ChartPropertiesFailed, failure.Status);
                Assert.IsFalse(failure.Status.Contains("private read failure", StringComparison.Ordinal));
                Assert.AreEqual(string.Empty, failure.Issues);
                Assert.AreEqual(string.Empty, failure.UnusedDefinitions);
                Assert.AreEqual(string.Empty, failure.UndefinedReferences);
            }
        }
        finally
        {
            failure.Close();
            release.Set();
            await operation;
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
    }

    /// <summary>BMS/BMSONの元コマンド・番号・パスと診断理由を保ち、全理由と形式説明だけを現言語へ整形します。分類と拡張子が逆の定義も再推定せず、言語往復で再読取りしません。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoadAsync_FormatsSourceCommandsAndFormatSpecificDescriptions(bool bmson)
    {
        CultureInfo? previousCulture = Resources.Culture;
        ChartResourceDefinition first = bmson
            ? new("sound_channels[0]", null, ChartResourceReference.Parse("sound.wav", ChartResourceKind.Audio))
            : new("#WAV", "01", ChartResourceReference.Parse("visual.png", ChartResourceKind.Image));
        ChartResourceDefinition second = bmson
            ? new("bga_header[0]", "7", ChartResourceReference.Parse("visual.png", ChartResourceKind.Image))
            : new("#BMP", "02", ChartResourceReference.Parse("sound.wav", ChartResourceKind.Audio));
        ChartResourceReferenceNumber number = bmson ? new("bga_header", "9") : new("#BMP", "01");
        int calls = 0;
        using var model = new ChartPropertiesViewModel(ChartTestValues.Empty(bmson ? ChartFileKind.Bmson : ChartFileKind.Bms), () =>
        {
            Interlocked.Increment(ref calls);
            return new ChartResourceDiagnostics("Title", "Artist",
                [new(first, ChartResourceDiagnosticReason.Missing), new(first, ChartResourceDiagnosticReason.InvalidPath),
                    new(first, ChartResourceDiagnosticReason.Unsupported), new(first, ChartResourceDiagnosticReason.Cp932DecodeUnsupported),
                    new(first, ChartResourceDiagnosticReason.Unverifiable)], [second], [number]);
        });
        Task operation = Task.CompletedTask;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            operation = model.LoadAsync();
            await operation;
            foreach (string culture in new[] { "ja-JP", "en-US", "ja-JP" })
            {
                ResourceService.Current.ChangeCulture(culture);
                string[] reasons = [Resources.ChartPropertiesMissing, Resources.ChartPropertiesInvalidPath, Resources.ChartPropertiesUnsupported,
                    Resources.ChartPropertiesCp932Unsupported, Resources.ChartPropertiesUnverifiable];
                string prefix = bmson ? "sound_channels[0] sound.wav" : "#WAV01 visual.png";
                string[] lines = model.Issues.Split(Environment.NewLine);
                Assert.AreEqual(reasons.Length, lines.Length);
                for (int i = 0; i < reasons.Length; i++)
                {
                    Assert.AreEqual(prefix + " (" + reasons[i] + ")", lines[i]);
                }
                Assert.AreEqual(bmson ? "bga_header[0] (7) visual.png" : "#BMP02 sound.wav", model.UnusedDefinitions);
                Assert.AreEqual(bmson ? "bga_header (9)" : "#BMP01", model.UndefinedReferences);
                Assert.AreEqual(bmson ? Resources.ChartPropertiesBmsonUnusedDescription : Resources.ChartPropertiesUnusedDescription, model.UnusedDescription);
                Assert.AreEqual(bmson ? Resources.ChartPropertiesBmsonUndefinedDescription : Resources.ChartPropertiesUndefinedDescription, model.UndefinedDescription);
                Assert.AreEqual("Title", model.Title);
                Assert.AreEqual("Artist", model.Artist);
                Assert.AreEqual(string.Empty, model.Status);
                await model.LoadAsync();
                Assert.AreEqual(1, calls);
            }
        }
        finally
        {
            model.Close();
            await operation;
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
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
