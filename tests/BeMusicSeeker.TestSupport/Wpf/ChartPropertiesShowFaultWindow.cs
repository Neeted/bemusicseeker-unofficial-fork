using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>基底のHWND初期化とテーマ接続後にShow内部で失敗・終了再入を発生させる実Windowです。通常表示のcompiled画面はメニューとbindingテストが検証します。</summary>
internal sealed class ChartPropertiesShowFaultWindow : ThemedWindow
{
    private readonly Action? duringShow;
    private readonly bool fail;
    internal bool ThemeCloseCompleted { get; private set; }

    internal ChartPropertiesShowFaultWindow(ChartPropertiesViewModel model, Action? duringShow = null, bool fail = true)
    {
        this.duringShow = duringShow;
        this.fail = fail;
        DataContext = model;
        Width = 300;
        Height = 200;
        Content = new System.Windows.Controls.TextBlock { Text = "Property presentation fault" };
        Closed += (_, _) => model.Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        duringShow?.Invoke();
        if (fail) { throw new InvalidOperationException("chart properties real Show failure"); }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ThemeCloseCompleted = true;
    }
}
