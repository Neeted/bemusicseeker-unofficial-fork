using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>一つのメイン画面から開く独立プロパティ窓と、個別閉鎖後も継続する実読取りTaskを所有します。UIスレッドから使用します。</summary>
internal sealed class ChartPropertiesWindowManager
{
    private readonly Window mainWindow;
    private readonly Func<ChartFile, Window> createWindow;
    private readonly Action<Window> prepareWindowForShow;
    private readonly HashSet<Window> windows = [];
    private readonly HashSet<Task> reads = [];
    private bool shuttingDown;

    /// <summary>現在所有する実読取りがすべて終端するTaskです。新しい表示を含める場合はその受付後に取得します。</summary>
    internal Task ReadCompletion => Task.WhenAll(reads.ToArray());

    /// <summary>初期配置の基準となるメイン画面を接続します。任意の生成はChartPropertiesViewModelをDataContextへ接続した実Windowを返し、表示前準備はその寿命を置き換えずに接続します。</summary>
    internal ChartPropertiesWindowManager(Window mainWindow,
        Func<ChartFile, Window> createWindow = null,
        Action<Window> prepareWindowForShow = null)
    {
        this.mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        this.createWindow = createWindow ?? (chart => new ChartPropertiesWindow(new ChartPropertiesViewModel(chart)));
        this.prepareWindowForShow = prepareWindowForShow;
    }

    /// <summary>毎回新しい窓を非モーダル表示し、表示が続いていれば一度だけ読取りを開始します。終了受付後は生成も読取りもしません。</summary>
    internal void Open(ChartFile chart)
    {
        if (shuttingDown) { return; }
        Window window = createWindow(chart);
        windows.Add(window);
        window.Closed += WindowClosed;
        try
        {
            window.Owner = mainWindow;
            prepareWindowForShow?.Invoke(window);
            if (!windows.Contains(window) || shuttingDown) { return; }
            window.Show();
            // Show内のイベントから終了が再入すると、登録済み窓は既にCloseされています。
            if (!windows.Contains(window) || shuttingDown) { return; }
            window.Owner = null;
            Task read = ((ChartPropertiesViewModel)window.DataContext).LoadAsync();
            reads.Add(read);
            read.ObserveFault("ChartPropertiesWindowManager.Read");
            read.GetAwaiter().OnCompleted(() => reads.Remove(read));
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    private void WindowClosed(object sender, EventArgs args)
    {
        var window = (Window)sender;
        window.Closed -= WindowClosed;
        windows.Remove(window);
    }

    /// <summary>新規受付を止め、現在の全窓を閉じます。UIスレッドを読取り完了まで待機させません。</summary>
    internal void BeginShutdown()
    {
        shuttingDown = true;
        foreach (Window window in windows.ToArray()) { window.Close(); }
    }

    /// <summary>冪等に終了を受け付け、先に個別Closeした窓を含む開始済み読取りの実終端を返します。</summary>
    internal Task CompleteShutdownAsync()
    {
        BeginShutdown();
        return ReadCompletion;
    }
}
