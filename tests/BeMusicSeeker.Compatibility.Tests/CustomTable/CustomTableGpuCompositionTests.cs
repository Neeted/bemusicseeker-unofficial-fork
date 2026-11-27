using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 一覧の表示面のGPU合成（Direct3D 11の共有テクスチャ、Direct3D 9Ex、<c>D3DImage</c>）を、実際のGPUで確かめます。
/// WPFが合成する画像の画素を読み、描画先の作り直しの要求・デバイスの消失からの復旧、CPU経路への切替え、表どうしの独立を確認します。
/// </summary>
/// <remarks>
/// <c>Suite.table-gpu</c> で明示実行します。WPFの描画の階層が1以上で、プロセスがソフトウェア描画でない環境を前提にし、満たさなければskipせず失敗にします。
/// 窓のない表だけを使い、描画の失敗は表示面の故障注入で再現します。待機やsleepは使わず、<c>UpdateLayout</c> の中で完了する描画を直接確認します。
/// </remarks>
[TestClass]
[TestCategory("Suite.table-gpu")]
[TestCategory("EnvironmentIntegration")]
public sealed class CustomTableGpuCompositionTests
{
    private const string SwitchLogText = "custom table display switched to the CPU path";
    private const string PresentTimeoutLogText = "custom table GPU present did not confirm rendering completion";

    /// <summary>前提の環境（WPFがGPUで合成する）でなければ、skipせず失敗にします。</summary>
    private static void AssertGpuEnvironment()
    {
        Assert.IsTrue((RenderCapability.Tier >> 16) >= 1, "WPFの描画の階層が1以上であることが前提です。Tier=" + (RenderCapability.Tier >> 16));
        Assert.AreNotEqual(RenderMode.SoftwareOnly, RenderOptions.ProcessRenderMode, "プロセスがソフトウェア描画でないことが前提です。");
    }

    private static void AssertGpuPath(CustomTableView table, string message)
    {
        Assert.IsInstanceOfType<D3DImage>(table.SurfaceImage, message);
    }

    private static void AssertCpuPath(CustomTableView table, string message)
    {
        Assert.IsInstanceOfType<System.Windows.Media.Imaging.WriteableBitmap>(table.SurfaceImage, message);
    }

    private static async Task<int> RunAndCountSwitchLogsAsync(Action body)
    {
        int count = 0;
        await ApplicationLoggingTestScope.RunAsync(scope =>
        {
            string logPath = scope.ConfigureFileLog();
            body();
            count = Regex.Count(ApplicationLoggingTestScope.ReadFileLog(logPath), SwitchLogText);
            return Task.CompletedTask;
        });
        return count;
    }

    /// <summary>診断ログの中の、CPU経路への切替えの記録と、描画の完了の確認の上限超過の警告の件数を数えます。</summary>
    private static async Task<(int Switches, int Timeouts)> RunAndCountLogsAsync(Action body)
    {
        int switches = 0;
        int timeouts = 0;
        await ApplicationLoggingTestScope.RunAsync(scope =>
        {
            string logPath = scope.ConfigureFileLog();
            body();
            string log = ApplicationLoggingTestScope.ReadFileLog(logPath);
            switches = Regex.Count(log, SwitchLogText);
            timeouts = Regex.Count(log, PresentTimeoutLogText);
            return Task.CompletedTask;
        });
        return (switches, timeouts);
    }

    /// <summary>
    /// 窓のない表が、配置の確定（<c>UpdateLayout</c>）だけでGPU経路（合成する画像が <c>D3DImage</c>）で描かれ、全画素が不透明で、画素が契約どおりであることを、100%と175%で確認します。
    /// 状態の変更も <c>UpdateLayout</c> だけで新しい画素になり、寸法を変えた後もGPU経路のまま、新しい寸法を新しく描いた表と画素まで一致します。
    /// </summary>
    /// <remarks>GPU経路を使わない誤り、描画が <c>UpdateLayout</c> で完結しない誤り、アルファが255でない画素、倍率と画像の寸法のずれ、寸法の変更でCPU経路へ落ちる誤りを検出します。</remarks>
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.75d)]
    public void WindowlessTable_IsComposedByD3DImageAndStaysOnTheGpuPathAfterResize(double dpi)
    {
        AssertGpuEnvironment();
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView? table = null;
            CustomTableView? fresh = null;
            try
            {
                table = CustomTableDisplayPathTestKit.Create(dpi);
                fresh = CustomTableDisplayPathTestKit.Create(dpi, width: 360d, height: 130d);
                AssertGpuPath(table, "窓のない表もGPU経路で描きます。");
                CustomTableDisplayPathTestKit.AssertContract(table, CustomTableRenderProbe.Read(table), dpi);

                table.Width = 360d;
                table.Height = 130d;
                CustomTableRenderProbe.Arrange(table, 360d, 130d);
                AssertGpuPath(table, "寸法を変えてもGPU経路のままです。");
                SurfacePixels resized = CustomTableRenderProbe.Read(table);
                SurfacePixels expected = CustomTableRenderProbe.Read(fresh);
                Assert.IsTrue(resized.SameAs(expected), "新しい寸法で全面を描きます。" + resized.DescribeDifference(expected));

                table.SelectedIndex = 2;
                table.UpdateLayout();
                SurfacePixels selected = CustomTableRenderProbe.Read(table);
                CustomTableRowGeometry geometry = table.RowGeometry;
                int y = geometry.HeaderHeightPx + 2 * geometry.RowHeightPx + geometry.RowHeightPx / 2;
                Assert.AreEqual(((SolidColorBrush)CustomTablePalette.Current.SelectedRowBackground).Color,
                    selected.GetColor(CustomTablePixelGeometry.ToPixels(150d, dpi), y), "状態の変更も配置の確定だけで新しい画素になります。");
                AssertGpuPath(table, "状態の変更でもGPU経路のままです。");
            }
            finally
            {
                CustomTableDisplayPathTestKit.Release(fresh);
                CustomTableDisplayPathTestKit.Release(table);
            }
        });
    }

    /// <summary>
    /// 同じ状態を、GPU経路の表と、生成の失敗でCPU経路にした表で描いて比べ、背景・罫線・選択・見出しは画素が完全に一致し、文字は外接矩形が±1画素以内で赤と青の有無が一致することを、100%と175%で確認します。
    /// 文字の赤と青を入れ替えた画像は、同等とみなされないことも確認します。
    /// </summary>
    /// <remarks>二つの経路の描画内容がずれる誤り、チャネルの順序（赤と青）の取り違え、文字の位置のずれを検出します。字形のアンチエイリアスの画素単位の差は許容します。</remarks>
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.75d)]
    public void GpuAndCpuPaths_DrawEquivalentPixels(double dpi)
    {
        AssertGpuEnvironment();
        TestUiDispatcherHost.Invoke(() =>
        {
            var faults = new CustomTableDisplayFaults();
            faults.FailCreation(CustomTableDisplayFaultPoint.Direct3D11Device);
            CustomTableView? gpu = null;
            CustomTableView? cpu = null;
            try
            {
                gpu = CustomTableDisplayPathTestKit.Create(dpi);
                cpu = CustomTableDisplayPathTestKit.Create(dpi, beforeFirstRender: surface => surface.DisplayFaults = faults);
                AssertGpuPath(gpu, "既定の表はGPU経路です。");
                AssertCpuPath(cpu, "生成に失敗させた表はCPU経路です。");
                SurfacePixels gpuPixels = CustomTableRenderProbe.Read(gpu);
                SurfacePixels cpuPixels = CustomTableRenderProbe.Read(cpu);
                CustomTableDisplayPathTestKit.AssertContract(gpu, gpuPixels, dpi);
                CustomTableDisplayPathTestKit.AssertContract(cpu, cpuPixels, dpi);
                CustomTableDisplayPathTestKit.AssertEquivalent(gpuPixels, cpuPixels, gpu, dpi);

                SurfacePixels swapped = SwapRedAndBlueInText(gpuPixels, gpu, dpi);
                Assert.Throws<AssertFailedException>(() => CustomTableDisplayPathTestKit.AssertEquivalent(swapped, cpuPixels, gpu, dpi), "赤と青の入替えを検出します。");
            }
            finally
            {
                CustomTableDisplayPathTestKit.Release(cpu);
                CustomTableDisplayPathTestKit.Release(gpu);
            }
        });
    }

    /// <summary>
    /// 描画の終端で、描画先の作り直しの要求またはデバイスの消失を一回だけ返すと、一回の <c>UpdateLayout</c> の中で資源を作り直して描き直し、GPU経路のまま正しい画素になることを確認します。
    /// 描画の完了の計数は1だけ増え、文字のレイアウトの新規作成と列の値の取得は0で、例外は出ません。画像と描画先は作り直されます。
    /// </summary>
    /// <remarks>復旧せず表示を失う・例外を出す誤り、復旧で文字を整形し直す・行の値を取り直す誤り、描画の完了を二回数える誤り、資源を作り直さず再利用する誤りを検出します。</remarks>
    [TestMethod]
    [DataRow(nameof(CustomTableDisplayLoss.RecreateTarget))]
    [DataRow(nameof(CustomTableDisplayLoss.DeviceRemoved))]
    public void RecoverableLoss_RebuildsAndRedrawsOnceOnTheGpuPath(string lossName)
    {
        AssertGpuEnvironment();
        CustomTableDisplayLoss loss = Enum.Parse<CustomTableDisplayLoss>(lossName);
        TestUiDispatcherHost.Invoke(() =>
        {
            var faults = new CustomTableDisplayFaults();
            var fetches = new StrongBox<int>();
            CustomTableView table = CustomTableDisplayPathTestKit.Create(1d, valueFetches: fetches);
            try
            {
                AssertGpuPath(table, "最初はGPU経路です。");
                ImageSource before = table.SurfaceImage!;
                int fetchesBefore = fetches.Value;
                int renderCount = table.LastRenderStats.RenderCount;
                CustomTableRenderProbe.Surface(table).DisplayFaults = faults;
                faults.LoseAtEndDraw(loss);

                SurfacePixels pixels = CustomTableRenderProbe.Redraw(table);

                Assert.AreEqual(0, faults.RemainingEndDrawLosses, "描画の終端で消失を報告しました。");
                Assert.AreEqual(renderCount + 1, table.LastRenderStats.RenderCount, "描画要求一回につき描画の完了は一回です。");
                AssertGpuPath(table, "復旧に成功したのでGPU経路のままです。");
                Assert.AreNotSame(before, table.SurfaceImage, "デバイスに依存する資源を作り直しました。");
                Assert.AreEqual(0, table.LastRenderStats.TextCacheMisses, "復旧で文字を整形し直しません。");
                Assert.AreEqual(fetchesBefore, fetches.Value, "復旧で列の値を取り直しません。");
                CustomTableDisplayPathTestKit.AssertContract(table, pixels, 1d);
            }
            finally { CustomTableDisplayPathTestKit.Release(table); }
        });
        TestUiDispatcherHost.AssertNoUnhandledExceptions("描画資源の復旧");
    }

    /// <summary>
    /// 消失を二回注入すると、作り直した二回目の試行も消失を報告するため、復旧の失敗として同じ描画要求の中でCPU経路へ切り替わり、正しい画素を描き、切替えを診断ログへ一回記録することを確認します。
    /// 描画の完了の計数は1だけ増え、文字のレイアウトの新規作成と列の値の取得は0で、例外は出ません。
    /// </summary>
    /// <remarks>復旧を無限に繰り返す誤り、二度目の消失で例外を出す・表示を失う誤り、切替えを記録しない誤り、切替えで文字を整形し直す誤りを検出します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task RepeatedLoss_SwitchesToTheCpuPathWithinTheSameRequest()
    {
        AssertGpuEnvironment();
        int logs = await RunAndCountSwitchLogsAsync(() =>
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                var faults = new CustomTableDisplayFaults();
                var fetches = new StrongBox<int>();
                CustomTableView table = CustomTableDisplayPathTestKit.Create(1d, valueFetches: fetches);
                try
                {
                    AssertGpuPath(table, "最初はGPU経路です。");
                    int fetchesBefore = fetches.Value;
                    int renderCount = table.LastRenderStats.RenderCount;
                    CustomTableRenderProbe.Surface(table).DisplayFaults = faults;
                    faults.LoseAtEndDraw(CustomTableDisplayLoss.DeviceRemoved, 2);

                    SurfacePixels pixels = CustomTableRenderProbe.Redraw(table);

                    Assert.AreEqual(0, faults.RemainingEndDrawLosses, "二回の試行とも消失を報告しました。");
                    Assert.AreEqual(renderCount + 1, table.LastRenderStats.RenderCount, "描画要求一回につき描画の完了は一回です。");
                    AssertCpuPath(table, "復旧に失敗したのでCPU経路へ切り替えます。");
                    Assert.AreEqual(0, table.LastRenderStats.TextCacheMisses, "切替えで文字を整形し直しません。");
                    Assert.AreEqual(fetchesBefore, fetches.Value, "切替えで列の値を取り直しません。");
                    CustomTableDisplayPathTestKit.AssertContract(table, pixels, 1d);
                }
                finally { CustomTableDisplayPathTestKit.Release(table); }
            });
            TestUiDispatcherHost.AssertNoUnhandledExceptions("復旧の失敗");
        });
        Assert.AreEqual(1, logs, "切替えは診断ログへ一回だけ記録します。");
    }

    /// <summary>
    /// 最初の描画が成功した後に、消失と、Direct3D 11のデバイスの生成の失敗を一回ずつ設定すると、次の描画で復旧の作り直しが失敗して同じ描画要求の中でCPU経路になり、
    /// 正しい画素・切替えのログ一回・文字のレイアウトの新規作成0・例外なしになることを確認します。
    /// </summary>
    /// <remarks>復旧中の生成の失敗で例外を出す・表示を失う誤り、復旧の生成の失敗に生成の注入が効かない誤り、切替えの記録の欠落を検出します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task RecreationFailureDuringRecovery_SwitchesToTheCpuPath()
    {
        AssertGpuEnvironment();
        int logs = await RunAndCountSwitchLogsAsync(() =>
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                var faults = new CustomTableDisplayFaults();
                var fetches = new StrongBox<int>();
                CustomTableView table = CustomTableDisplayPathTestKit.Create(1d, valueFetches: fetches);
                try
                {
                    AssertGpuPath(table, "最初はGPU経路です。");
                    int fetchesBefore = fetches.Value;
                    int renderCount = table.LastRenderStats.RenderCount;
                    CustomTableRenderProbe.Surface(table).DisplayFaults = faults;
                    faults.LoseAtEndDraw(CustomTableDisplayLoss.DeviceRemoved);
                    faults.FailCreation(CustomTableDisplayFaultPoint.Direct3D11Device);

                    SurfacePixels pixels = CustomTableRenderProbe.Redraw(table);

                    Assert.AreEqual(0, faults.RemainingEndDrawLosses);
                    Assert.AreEqual(0, faults.RemainingCreationFailures(CustomTableDisplayFaultPoint.Direct3D11Device), "復旧の作り直しで生成が失敗しました。");
                    Assert.AreEqual(renderCount + 1, table.LastRenderStats.RenderCount, "描画要求一回につき描画の完了は一回です。");
                    AssertCpuPath(table, "作り直しに失敗したのでCPU経路へ切り替えます。");
                    Assert.AreEqual(0, table.LastRenderStats.TextCacheMisses, "切替えで文字を整形し直しません。");
                    Assert.AreEqual(fetchesBefore, fetches.Value, "切替えで列の値を取り直しません。");
                    CustomTableDisplayPathTestKit.AssertContract(table, pixels, 1d);
                }
                finally { CustomTableDisplayPathTestKit.Release(table); }
            });
            TestUiDispatcherHost.AssertNoUnhandledExceptions("復旧の作り直しの失敗");
        });
        Assert.AreEqual(1, logs, "切替えは診断ログへ一回だけ記録します。");
    }

    /// <summary>
    /// 反映で描画の完了を上限まで確認できなかったものとして扱う（実際には待たない）ことを一回ずつ起こすと、表はGPU経路のまま同じ画像の実体を保ち、描画要求一回につき注入が一回消費され、
    /// 描画の完了の計数は1だけ増えて追加の再描画はなく、描画の画素は通常どおり描いた表と一致することを確認します。
    /// 警告は、解放の前の二回では診断ログへ一件、非表示から再表示で資源を解放した後の一回でもう一件（合計二件）で、CPU経路への切替えのログは0件、例外は出ません。
    /// </summary>
    /// <remarks>
    /// 上限超過でCPU経路へ切り替える誤り、共有のデバイスを手放す誤り、再描画で補う誤り、
    /// 警告を毎回または全体で一度しか記録しない誤り、注入を消費しない・二重に消費する誤りを検出します。画素の比較は、上限超過の描画の内容が正しいことの確認です。
    /// 更新領域の通知を省く誤りは、実測で見分けられなかった（通知を省いても画素が一致した）ため、このケースの保証に含めません。
    /// 終端 → 待ち → 通知の順序、通知の有無、上限200ミリ秒、クエリの世代と解放順は、ここでは固定せず静的レビューで確認します。
    /// </remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task PresentCompletionTimeout_PresentsOnTheGpuPathWithoutRedrawAndWarnsOncePerRelease()
    {
        AssertGpuEnvironment();
        (int switches, int timeouts) = await RunAndCountLogsAsync(() =>
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                var faults = new CustomTableDisplayFaults();
                CustomTableView? table = null;
                CustomTableView? expected = null;
                try
                {
                    table = CustomTableDisplayPathTestKit.Create(1d);
                    expected = CustomTableDisplayPathTestKit.Create(1d);
                    AssertGpuPath(table, "最初はGPU経路です。");
                    // 比較の対象は、注入より前に、同じ操作で通常どおり描いた表です。
                    expected.SelectedIndex = 2;
                    expected.UpdateLayout();
                    SurfacePixels expectedPixels = CustomTableRenderProbe.Read(expected);

                    ImageSource image = table.SurfaceImage!;
                    CustomTableRenderProbe.Surface(table).DisplayFaults = faults;
                    int renderCount = table.LastRenderStats.RenderCount;

                    faults.TimeOutPresentCompletion();
                    table.SelectedIndex = 2;
                    table.UpdateLayout();
                    Assert.AreEqual(0, faults.RemainingPresentCompletionTimeouts, "描画要求一回につき注入は一回だけ消費されます。");
                    AssertGpuPath(table, "上限を超えてもGPU経路のままです。");
                    Assert.AreSame(image, table.SurfaceImage, "上限を超えても面を解放・作り直しません。");
                    Assert.AreEqual(renderCount + 1, table.LastRenderStats.RenderCount, "描画の完了は一回だけ増えます。");
                    SurfacePixels pixels = CustomTableRenderProbe.Read(table);
                    Assert.IsTrue(pixels.SameAs(expectedPixels), "上限超過の描画も、描いた内容は通常どおりの画素です。" + pixels.DescribeDifference(expectedPixels));
                    table.UpdateLayout();
                    Assert.AreEqual(renderCount + 1, table.LastRenderStats.RenderCount, "追加の再描画を要求しません。");

                    // 解放の前の二回目は、警告を増やさない。
                    faults.TimeOutPresentCompletion();
                    table.SelectedIndex = 3;
                    table.UpdateLayout();
                    Assert.AreEqual(0, faults.RemainingPresentCompletionTimeouts, "二回目の描画要求でも注入は一回だけ消費されます。");
                    Assert.AreSame(image, table.SurfaceImage, "二回目でも面を作り直しません。");
                    Assert.AreEqual(renderCount + 2, table.LastRenderStats.RenderCount, "二回目の描画の完了も一回だけ増えます。");

                    // 非表示で資源を解放し、再表示した後の上限超過は、改めて警告する。
                    table.ApplyVisibilityChanged(false);
                    table.ApplyVisibilityChanged(true);
                    faults.TimeOutPresentCompletion();
                    table.SelectedIndex = 1;
                    table.UpdateLayout();
                    Assert.AreEqual(0, faults.RemainingPresentCompletionTimeouts, "再表示の後の描画要求でも注入は一回だけ消費されます。");
                    AssertGpuPath(table, "再表示した表もGPU経路です。");
                }
                finally
                {
                    CustomTableDisplayPathTestKit.Release(expected);
                    CustomTableDisplayPathTestKit.Release(table);
                }
            });
            TestUiDispatcherHost.AssertNoUnhandledExceptions("描画の完了の確認の上限超過");
        });
        Assert.AreEqual(0, switches, "上限超過でCPU経路へ切り替えません。");
        Assert.AreEqual(2, timeouts, "警告は、表の解放まで一度だけ記録します。");
    }

    /// <summary>
    /// GPU経路の表を二つ用意し（共有のデバイスを使う）、表Aを非表示にする、または <c>ItemsSource</c> をnullにすると、Aの <c>D3DImage</c> の裏面が外れ、表Bの再描画はGPU経路で正しい画素になり文字のレイアウトの新規作成は0であることを確認します。
    /// Aを再表示してAへ消失を一回注入して描くと、AはGPU経路のまま非表示の間に変えた最新の状態を描き、共有のデバイスの作り直しにBが配置の確定だけで追従して、GPU経路のまま正しい画素を描き（文字の整形と列の値の取得は0、例外なし）ます。
    /// </summary>
    /// <remarks>表どうしで面・ブラシ・<c>D3DImage</c> を共有し、片方の解放がもう片方を壊す誤り、消失の後に他方の表へ古い面が残り続ける（世代の変更に追従しない）誤り、解放しても <c>D3DImage</c> の裏面を外さない誤り、再表示で古い状態を描く誤りを検出します。</remarks>
    [TestMethod]
    [DataRow("hide")]
    [DataRow("items-null")]
    public void TwoTables_AreIndependentWhenOneIsReleasedOrLost(string releaseKind)
    {
        AssertGpuEnvironment();
        TestUiDispatcherHost.Invoke(() =>
        {
            var faultsA = new CustomTableDisplayFaults();
            var fetchesOfB = new StrongBox<int>();
            CustomTableView? a = null;
            CustomTableView? b = null;
            CustomTableView? expectedA = null;
            CustomTableView? expectedB = null;
            try
            {
                a = CustomTableDisplayPathTestKit.Create(1d);
                b = CustomTableDisplayPathTestKit.Create(1d, selectedIndex: 2, valueFetches: fetchesOfB);
                // 比較の対象は、解放を挟まずに同じ操作をした表です。現在セルなどの状態が、初期の選択だけの表とは異なるためです。
                expectedA = CustomTableDisplayPathTestKit.Create(1d, selectedIndex: 1);
                expectedB = CustomTableDisplayPathTestKit.Create(1d, selectedIndex: 2);
                AssertGpuPath(a, "表AはGPU経路です。");
                AssertGpuPath(b, "表BはGPU経路です。");
                var imageA = (D3DImage)a.SurfaceImage!;
                var imageB = (D3DImage)b.SurfaceImage!;
                Assert.IsTrue(imageA.PixelWidth > 0 && imageB.PixelWidth > 0, "どちらも裏面を持ちます。");
                System.Collections.IList rows = a.ItemsSource;

                if (releaseKind == "hide")
                {
                    a.ApplyVisibilityChanged(false);
                    Assert.IsNull(a.SurfaceImage, "非表示にした表は画像を保持しません。");
                }
                else
                {
                    a.ItemsSource = null;
                }
                Assert.AreEqual(0, imageA.PixelWidth, "Aの解放で、Aの画像の裏面が外れます。");
                Assert.IsTrue(imageB.PixelWidth > 0, "Aの解放はBの画像に影響しません。");

                b.SelectedIndex = 0;
                expectedB.SelectedIndex = 0;
                SurfacePixels pixelsB = CustomTableRenderProbe.Redraw(b);
                SurfacePixels expectedPixelsB = CustomTableRenderProbe.Redraw(expectedB);
                AssertGpuPath(b, "Aを解放してもBはGPU経路です。");
                Assert.AreSame(imageB, b.SurfaceImage, "Aの解放でBの面は作り直されません。");
                Assert.AreEqual(0, b.LastRenderStats.TextCacheMisses, "Bの再描画で文字を整形し直しません。");
                Assert.IsTrue(pixelsB.SameAs(expectedPixelsB), "Bの再描画は正しい画素です。" + pixelsB.DescribeDifference(expectedPixelsB));

                int renderCountB = b.LastRenderStats.RenderCount;
                int fetchesB = fetchesOfB.Value;
                // Aの状態を変え、Aの次の描画で消失を一回起こす。ItemsSourceをnullにすると選択は失われるため、戻した後に選択する。
                CustomTableRenderProbe.Surface(a).DisplayFaults = faultsA;
                faultsA.LoseAtEndDraw(CustomTableDisplayLoss.DeviceRemoved);
                if (releaseKind == "hide")
                {
                    a.SelectedIndex = 2;
                    expectedA.SelectedIndex = 2;
                    a.ApplyVisibilityChanged(true);
                }
                else
                {
                    System.Collections.IList expectedRows = expectedA.ItemsSource;
                    expectedA.ItemsSource = null;
                    expectedA.ItemsSource = expectedRows;
                    expectedA.SelectedIndex = 2;
                    a.ItemsSource = rows;
                    a.SelectedIndex = 2;
                }
                a.UpdateLayout();
                Assert.AreEqual(0, faultsA.RemainingEndDrawLosses, "Aの描画で消失を起こしました。");
                AssertGpuPath(a, "再表示したAは、消失から復旧してGPU経路で描きます。");
                SurfacePixels pixelsA = CustomTableRenderProbe.Read(a);
                SurfacePixels expectedPixelsA = CustomTableRenderProbe.Redraw(expectedA);
                Assert.IsTrue(pixelsA.SameAs(expectedPixelsA), "Aは非表示の間に変えた最新の状態を描きます。" + pixelsA.DescribeDifference(expectedPixelsA));

                // Aの消失で共有のデバイスが作り直されたため、Bは明示的に再描画を要求しなくても、配置の確定（Aの配置の確定と同じ処理）で世代の変更に追従して描き直す。
                b.UpdateLayout();
                Assert.AreEqual(renderCountB + 1, b.LastRenderStats.RenderCount, "世代の変更の通知で、Bは一回だけ再描画されます。");
                AssertGpuPath(b, "BはGPU経路のままです。");
                Assert.AreNotSame(imageB, b.SurfaceImage, "Bは新しいデバイスで面を作り直しました。");
                Assert.AreEqual(0, b.LastRenderStats.TextCacheMisses, "Bの追従で文字を整形し直しません。");
                Assert.AreEqual(fetchesB, fetchesOfB.Value, "Bの追従で列の値を取り直しません。");
                SurfacePixels laterB = CustomTableRenderProbe.Read(b);
                SurfacePixels expectedLaterB = CustomTableRenderProbe.Read(expectedB);
                Assert.IsTrue(laterB.SameAs(expectedLaterB), "Aの消失の後もBは正しい画素です。" + laterB.DescribeDifference(expectedLaterB));
            }
            finally
            {
                CustomTableDisplayPathTestKit.Release(expectedB);
                CustomTableDisplayPathTestKit.Release(expectedA);
                CustomTableDisplayPathTestKit.Release(b);
                CustomTableDisplayPathTestKit.Release(a);
            }
        });
        TestUiDispatcherHost.AssertNoUnhandledExceptions("二つの表の独立");
    }

    /// <summary>文字のセルの中の、背景と異なる画素（文字）だけ、赤と青のチャネルを入れ替えた画素列を返します。背景や罫線は変えません。</summary>
    private static SurfacePixels SwapRedAndBlueInText(SurfacePixels source, CustomTableView table, double dpi)
    {
        byte[] bytes = (byte[])source.Bgra.Clone();
        foreach (Int32Rect cell in CustomTableDisplayPathTestKit.CellInteriors(table, dpi))
        {
            Color background = source.GetColor(cell.X, cell.Y);
            for (int y = cell.Y; y < cell.Y + cell.Height; y++)
            {
                for (int x = cell.X; x < cell.X + cell.Width; x++)
                {
                    if (source.GetColor(x, y) == background) { continue; }
                    int offset = (y * source.Width + x) * 4;
                    (bytes[offset], bytes[offset + 2]) = (bytes[offset + 2], bytes[offset]);
                }
            }
        }
        return new SurfacePixels(source.Width, source.Height, bytes);
    }
}
