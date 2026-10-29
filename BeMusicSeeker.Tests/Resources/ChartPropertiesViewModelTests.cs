using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// <summary>実読取りの既定経路が、開く時点の固定Big5指定を使います。後の項目変更へ追従せず、ASCIIメタデータだけでは分からない非ASCII資源の不足を誤表示しません。</summary>
    [TestMethod]
    public async Task LoadAsync_DefaultReaderUsesCapturedFixedEncodingForResourceNames()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("chart-properties-encoding-");
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
                Assert.AreEqual("#WAV01 [" + string.Format(Resources.ChartPropertiesDefinitionLine, 1) + "] #WAV01 tone.wav (" + Resources.ChartPropertiesMissing + ")", model.Issues);
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
                    new(first, ChartResourceDiagnosticReason.Unsupported),
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
                    Resources.ChartPropertiesUnverifiable];
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

    /// <summary>実サービスが返す三用途の正常採用画像と全後続定義を、採用状態・存在結果付きで各一回整形します。元ファイルを削除した後も六言語の往復は捕捉済みの事実だけを使い、読取り回数を増やしません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task LoadAsync_OptionalImageDefinitionsKeepAdoptionAndExistenceAcrossLanguages()
    {
        CultureInfo? previousCulture = Resources.Culture;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("chart-properties-optional-");
        ChartPropertiesViewModel? model = null;
        try
        {
            string path = System.IO.Path.Combine(directory.FullName, "chart.bms");
            File.WriteAllBytes(System.IO.Path.Combine(directory.FullName, "present.jpg"), []);
            string[] sources = ["#STAGEFILE", "#BACKBMP", "#BANNER"];
            string[] paths = ["present.png", "present.bmp", "missing.png", "../outside.png", ".png"];
            string[] rawLines = sources.SelectMany(source => paths.Select(value => source + " " + value)).ToArray();
            File.WriteAllText(path, string.Join("\n", rawLines), System.Text.Encoding.ASCII);
            int calls = 0;
            model = new ChartPropertiesViewModel(ChartTestValues.Empty() with { Path = path }, () =>
            {
                Interlocked.Increment(ref calls);
                return new ChartResourceDiagnosticsService().Read(path, ChartFileKind.Bms);
            });
            await model.LoadAsync();
            File.Delete(path);
            foreach (string culture in new[] { "ja-JP", "en-US", "fr-FR", "ko-KR", "zh-CN", "zh-TW", "ja-JP" })
            {
                ResourceService.Current.ChangeCulture(culture);
                string[] reasons = [Resources.ChartPropertiesSatisfied, Resources.ChartPropertiesSatisfied, Resources.ChartPropertiesMissing,
                    Resources.ChartPropertiesUnsupported, Resources.ChartPropertiesExcludedFileName];
                string[] lines = model.Issues.Split(Environment.NewLine);
                Assert.AreEqual(rawLines.Length, lines.Length);
                for (int source = 0; source < sources.Length; source++)
                {
                    for (int value = 0; value < paths.Length; value++)
                    {
                        int line = source * paths.Length + value;
                        string expected = sources[source] + " [" + string.Format(Resources.ChartPropertiesDefinitionLine, line + 1) + "] "
                            + rawLines[line] + " [" + (value == 0 ? Resources.ChartPropertiesAdopted : Resources.ChartPropertiesDiagnosticOnly)
                            + "] (" + reasons[value] + (value == 0 ? string.Empty : "; " + Resources.ChartPropertiesNotCounted) + ")";
                        Assert.AreEqual(1, lines.Count(item => item == expected), culture + ": " + rawLines[line]);
                    }
                }
                Assert.AreEqual(Resources.None, model.UnusedDefinitions);
                Assert.AreEqual(Resources.None, model.UndefinedReferences);
                await model.LoadAsync();
                Assert.AreEqual(1, calls);
            }
        }
        finally
        {
            model?.Close();
            model?.Dispose();
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
            directory.Delete(recursive: true);
        }
    }

    /// <summary>同じ実譜面の使用・未使用・先頭採用・後続・対象外を実診断から整形し、原因と計数文脈を区別します。表示上の計数説明は既存健康度の分母・任意画像状態と一致し、六言語の再整形でも再取得しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task LoadAsync_SeparatesClassificationReasonsFromCountingContext()
    {
        CultureInfo? previousCulture = Resources.Culture;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("chart-properties-counting-");
        ChartPropertiesViewModel? model = null;
        try
        {
            string path = System.IO.Path.Combine(directory.FullName, "chart.bms");
            File.WriteAllBytes(System.IO.Path.Combine(directory.FullName, "present.jpg"), []);
            (string Raw, ChartResourceDiagnosticReason Reason, bool? Counted, bool Unused)[] definitions =
            [
                ("#WAV01 ../used.wav", ChartResourceDiagnosticReason.Unsupported, true, false),
                ("#WAV02 bad|used.wav", ChartResourceDiagnosticReason.InvalidPath, true, false),
                ("#WAV03 .wav", ChartResourceDiagnosticReason.MissingFileName, false, false),
                ("#WAV04 ../unused.wav", ChartResourceDiagnosticReason.Unsupported, false, true),
                ("#WAV05 bad|unused.wav", ChartResourceDiagnosticReason.InvalidPath, false, true),
                ("#WAV06 .wav", ChartResourceDiagnosticReason.MissingFileName, false, true),
                ("#WAV07broken.wav", ChartResourceDiagnosticReason.MalformedSyntax, false, false),
                ("#BMP01 ../used.png", ChartResourceDiagnosticReason.Unsupported, true, false),
                ("#BMP02 bad|used.png", ChartResourceDiagnosticReason.InvalidPath, true, false),
                ("#BMP03 .png", ChartResourceDiagnosticReason.MissingFileName, false, false),
                ("#BMP04 mystery.xyz", ChartResourceDiagnosticReason.UnknownKind, false, false),
                ("#STAGEFILE ../stage.png", ChartResourceDiagnosticReason.Unsupported, true, false),
                ("#STAGEFILE ../later-stage.png", ChartResourceDiagnosticReason.Unsupported, false, false),
                ("#BACKBMP ", ChartResourceDiagnosticReason.Empty, false, false),
                ("#BACKBMP .png", ChartResourceDiagnosticReason.MissingFileName, false, false),
                ("#BACKBMP ../later-back.png", ChartResourceDiagnosticReason.Unsupported, false, false),
                ("#BANNER present.png", ChartResourceDiagnosticReason.Satisfied, null, false),
                ("#BANNER bad|later-banner.png", ChartResourceDiagnosticReason.InvalidPath, false, false)
            ];
            File.WriteAllText(path, string.Join("\n", definitions.Select(item => item.Raw)) + "\n#00111:01020307\n#00104:01020304\n", System.Text.Encoding.ASCII);
            ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
            var requirements = ChartResourceSnapshot.Create(chart);
            Assert.AreEqual(2, requirements.AudioReferenceCount);
            Assert.AreEqual(2, requirements.VisualReferenceCount);
            Assert.AreEqual(2, requirements.OptionalImageReferenceCount);
            ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);
            Assert.AreEqual(2, health.WavFilesDefined);
            Assert.AreEqual(0, health.WavFilesExisting);
            Assert.AreEqual(2, health.BgaFilesDefined);
            Assert.AreEqual(0, health.BgaFilesExisting);
            Assert.AreEqual(true, health.StagefileDefined);
            Assert.AreEqual(false, health.StagefileExisting);
            Assert.AreEqual(false, health.BackbmpDefined);
            Assert.AreEqual(true, health.BannerDefined);
            Assert.AreEqual(true, health.BannerExisting);
            int calls = 0;
            model = new ChartPropertiesViewModel(chart, () =>
            {
                Interlocked.Increment(ref calls);
                return new ChartResourceDiagnosticsService().Read(path, ChartFileKind.Bms);
            });
            await model.LoadAsync();
            File.Delete(path);
            foreach (string culture in new[] { "ja-JP", "en-US", "fr-FR", "ko-KR", "zh-CN", "zh-TW", "ja-JP" })
            {
                ResourceService.Current.ChangeCulture(culture);
                string[] issues = model.Issues.Split(Environment.NewLine);
                string[] unused = model.UnusedDefinitions.Split(Environment.NewLine);
                Assert.AreEqual(15, issues.Length);
                Assert.AreEqual(3, unused.Length);
                for (int index = 0; index < definitions.Length; index++)
                {
                    (string Raw, ChartResourceDiagnosticReason Reason, bool? Counted, bool Unused) definition = definitions[index];
                    string position = "[" + string.Format(Resources.ChartPropertiesDefinitionLine, index + 1) + "]";
                    string line = (definition.Unused ? unused : issues).Single(item => item.Contains(position, StringComparison.Ordinal));
                    Assert.IsTrue(line.Contains(definition.Raw.TrimEnd(), StringComparison.Ordinal), line);
                    string reason = definition.Reason switch
                    {
                        ChartResourceDiagnosticReason.Unsupported => Resources.ChartPropertiesUnsupported,
                        ChartResourceDiagnosticReason.InvalidPath => Resources.ChartPropertiesInvalidPath,
                        ChartResourceDiagnosticReason.MissingFileName => Resources.ChartPropertiesExcludedFileName,
                        ChartResourceDiagnosticReason.Empty => Resources.ChartPropertiesExcludedEmpty,
                        ChartResourceDiagnosticReason.UnknownKind => Resources.ChartPropertiesExcludedKind,
                        ChartResourceDiagnosticReason.MalformedSyntax => Resources.ChartPropertiesExcludedSyntax,
                        _ => Resources.ChartPropertiesSatisfied
                    };
                    Assert.IsTrue(line.Contains("(" + reason, StringComparison.Ordinal), culture + ": " + definition.Raw);
                    Assert.AreEqual(definition.Counted == true, line.Contains(Resources.ChartPropertiesCountedUnfulfilled, StringComparison.Ordinal), line);
                    Assert.AreEqual(definition.Counted == false, line.Contains(Resources.ChartPropertiesNotCounted, StringComparison.Ordinal), line);
                }
                await model.LoadAsync();
                Assert.AreEqual(1, calls);
            }
        }
        finally
        {
            model?.Close();
            model?.Dispose();
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
            directory.Delete(recursive: true);
        }
    }

    /// <summary>同CP932本文は通常理由とCP932事実を同じ定義へ添え、使用・未使用・採用任意画像・後続を各一欄で一度だけ表示します。通常充足のCP932問題も残し、六言語往復と元ファイル削除後も取得は一回です。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(null)]
    [DataRow("shift_jis")]
    public async Task LoadAsync_SameCp932InterpretationDisplaysEachDefinitionOnce(string? encoding)
    {
        CultureInfo? previousCulture = Resources.Culture;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("chart-properties-cp932-");
        ChartPropertiesViewModel? model = null;
        try
        {
            string path = System.IO.Path.Combine(directory.FullName, "chart.bms");
            byte[] name = [.. System.Text.Encoding.ASCII.GetBytes("tone"), 0x81, 0x30, .. System.Text.Encoding.ASCII.GetBytes(".wav")];
            string presentName = System.Text.Encoding.GetEncoding(932).GetString(name);
            File.WriteAllBytes(System.IO.Path.Combine(directory.FullName, System.IO.Path.ChangeExtension(presentName, ".ogg")), []);
            File.WriteAllBytes(System.IO.Path.Combine(directory.FullName, "present.jpg"), []);
            byte[] bytes = [.. System.Text.Encoding.ASCII.GetBytes("#WAV01 ../used.wav"), 0x81, 0x30,
                .. System.Text.Encoding.ASCII.GetBytes("\n#WAV02 unused.wav"), 0x81, 0x30,
                .. System.Text.Encoding.ASCII.GetBytes("\n#STAGEFILE stage.png"), 0x81, 0x30,
                .. System.Text.Encoding.ASCII.GetBytes("\n#STAGEFILE later.png"), 0x81, 0x30,
                .. System.Text.Encoding.ASCII.GetBytes("\n#BACKBMP .png\n#BANNER present.png\n#WAV03 "), .. name,
                .. System.Text.Encoding.ASCII.GetBytes("\n#00111:0103\n")];
            File.WriteAllBytes(path, bytes);
            ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path), encoding);
            ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);
            Assert.AreEqual(2, health.WavFilesDefined);
            Assert.AreEqual(1, health.WavFilesExisting);
            int calls = 0;
            model = new ChartPropertiesViewModel(chart, () =>
            {
                Interlocked.Increment(ref calls);
                return new ChartResourceDiagnosticsService().Read(path, ChartFileKind.Bms, encoding: encoding);
            });
            await model.LoadAsync();
            File.Delete(path);
            foreach (string culture in new[] { "ja-JP", "en-US", "fr-FR", "ko-KR", "zh-CN", "zh-TW", "ja-JP" })
            {
                ResourceService.Current.ChangeCulture(culture);
                string[] issues = model.Issues.Split(Environment.NewLine);
                string[] unused = model.UnusedDefinitions.Split(Environment.NewLine);
                Assert.AreEqual(6, issues.Length);
                Assert.AreEqual(1, unused.Length);
                Assert.AreEqual(4, issues.Count(line => line.Contains(Resources.ChartPropertiesCp932Unsupported, StringComparison.Ordinal)));
                Assert.IsTrue(unused[0].Contains(Resources.ChartPropertiesCp932Unsupported, StringComparison.Ordinal));
                Assert.IsFalse(model.Issues.Contains(Resources.ChartPropertiesCp932AlternativeInterpretation, StringComparison.Ordinal));
                string AtLine(int number) => issues.Concat(unused).Single(line => line.Contains("[" + string.Format(Resources.ChartPropertiesDefinitionLine, number) + "]", StringComparison.Ordinal));
                Assert.IsTrue(AtLine(1).Contains(Resources.ChartPropertiesUnsupported, StringComparison.Ordinal));
                Assert.IsTrue(AtLine(1).Contains(Resources.ChartPropertiesCountedUnfulfilled, StringComparison.Ordinal));
                Assert.IsTrue(AtLine(2).StartsWith("#WAV02 ", StringComparison.Ordinal));
                Assert.IsTrue(AtLine(3).Contains(Resources.ChartPropertiesMissing, StringComparison.Ordinal));
                Assert.IsTrue(AtLine(3).Contains(Resources.ChartPropertiesAdopted, StringComparison.Ordinal));
                Assert.IsTrue(AtLine(4).Contains(Resources.ChartPropertiesDiagnosticOnly, StringComparison.Ordinal));
                Assert.IsTrue(AtLine(4).Contains(Resources.ChartPropertiesNotCounted, StringComparison.Ordinal));
                Assert.IsTrue(AtLine(7).Contains(Resources.ChartPropertiesSatisfied, StringComparison.Ordinal));
                Assert.IsTrue(AtLine(7).Contains(presentName, StringComparison.Ordinal));
                await model.LoadAsync();
                Assert.AreEqual(1, calls);
            }
        }
        finally
        {
            model?.Close();
            model?.Dispose();
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
            directory.Delete(recursive: true);
        }
    }

    /// <summary>別採用本文のCP932問題を別解釈と明示して通常定義から区別します。改行消費の行ずれ、通常側なし、先頭BOM独立原文を代表し、別解釈には通常の採用・計数を付けず六言語で再取得しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("newline")]
    [DataRow("cp932-only")]
    [DataRow("bom")]
    public async Task LoadAsync_AlternativeCp932InterpretationKeepsIndependentOriginalLines(string scenario)
    {
        CultureInfo? previousCulture = Resources.Culture;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("chart-properties-alternative-");
        ChartPropertiesViewModel? model = null;
        try
        {
            string path = System.IO.Path.Combine(directory.FullName, "chart.bms");
            byte[] bytes = scenario switch
            {
                "newline" => [.. System.Text.Encoding.ASCII.GetBytes("#WAV01 merged.wav"), 0x81,
                    .. System.Text.Encoding.ASCII.GetBytes("\n#WAV02 swallowed.wav\n#WAV03 valid.wav\n#WAV04 later.wav"), 0x81, 0x30,
                    .. System.Text.Encoding.ASCII.GetBytes("\n#00111:01020304\n")],
                "cp932-only" => [0x81, 0x40, .. System.Text.Encoding.ASCII.GetBytes("#WAV01 ../bad"), 0x81, 0x30,
                    .. System.Text.Encoding.ASCII.GetBytes(".wav\n#00111:01\n")],
                _ => [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.ASCII.GetBytes("#WAV01 ../bad"), 0x81, 0x30,
                    .. System.Text.Encoding.ASCII.GetBytes(".wav\n#00111:01\n")]
            };
            string encoding = scenario == "bom" ? "shift_jis" : "utf-8";
            File.WriteAllBytes(path, bytes);
            ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path), encoding);
            int calls = 0;
            model = new ChartPropertiesViewModel(chart, () =>
            {
                Interlocked.Increment(ref calls);
                return new ChartResourceDiagnosticsService().Read(path, ChartFileKind.Bms, encoding: encoding);
            });
            await model.LoadAsync();
            File.Delete(path);
            foreach (string culture in new[] { "ja-JP", "en-US", "fr-FR", "ko-KR", "zh-CN", "zh-TW", "ja-JP" })
            {
                ResourceService.Current.ChangeCulture(culture);
                string prefix = "[" + Resources.ChartPropertiesCp932AlternativeInterpretation + "] ";
                string[] lines = model.Issues.Split(Environment.NewLine);
                string[] alternatives = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                string[] normal = lines.Where(line => !line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                Assert.AreEqual(scenario == "newline" ? 2 : 1, alternatives.Length);
                Assert.AreEqual(scenario == "newline" ? 4 : scenario == "bom" ? 1 : 0, normal.Length);
                foreach (string line in alternatives)
                {
                    Assert.IsTrue(line.Contains(Resources.ChartPropertiesCp932Unsupported, StringComparison.Ordinal));
                    Assert.IsFalse(line.Contains(Resources.ChartPropertiesAdopted, StringComparison.Ordinal));
                    Assert.IsFalse(line.Contains(Resources.ChartPropertiesDiagnosticOnly, StringComparison.Ordinal));
                    Assert.IsFalse(line.Contains(Resources.ChartPropertiesCountedUnfulfilled, StringComparison.Ordinal));
                    Assert.IsFalse(line.Contains(Resources.ChartPropertiesNotCounted, StringComparison.Ordinal));
                }
                Assert.IsTrue(normal.All(line => !line.Contains(Resources.ChartPropertiesCp932Unsupported, StringComparison.Ordinal)));
                if (scenario == "newline")
                {
                    string cp04 = alternatives.Single(line => line.Contains("#WAV04 ", StringComparison.Ordinal));
                    string normal04 = normal.Single(line => line.StartsWith("#WAV04 ", StringComparison.Ordinal));
                    Assert.IsTrue(cp04.Contains("[" + string.Format(Resources.ChartPropertiesDefinitionLine, 3) + "]", StringComparison.Ordinal));
                    Assert.IsTrue(normal04.Contains("[" + string.Format(Resources.ChartPropertiesDefinitionLine, 4) + "]", StringComparison.Ordinal));
                    Assert.IsTrue(alternatives.Single(line => line.Contains("#WAV01 ", StringComparison.Ordinal)).Contains("#WAV02 swallowed.wav", StringComparison.Ordinal));
                }
                else
                {
                    string raw = System.Text.Encoding.GetEncoding(932).GetString(bytes).Split('\n')[0];
                    Assert.IsTrue(alternatives[0].Contains(raw, StringComparison.Ordinal));
                    Assert.IsTrue(alternatives[0].Contains("[" + string.Format(Resources.ChartPropertiesDefinitionLine, 1) + "]", StringComparison.Ordinal));
                }
                await model.LoadAsync();
                Assert.AreEqual(1, calls);
            }
        }
        finally
        {
            model?.Close();
            model?.Dispose();
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
            directory.Delete(recursive: true);
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
