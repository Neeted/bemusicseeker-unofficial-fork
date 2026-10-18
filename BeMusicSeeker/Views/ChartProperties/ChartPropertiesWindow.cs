using System;
using System.Windows.Input;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>一件の譜面診断を通常の選択・コピー操作で確認する、読取り専用の画面です。</summary>
public partial class ChartPropertiesWindow : ThemedWindow
{
    private readonly ChartPropertiesViewModel viewModel;

    /// <summary>専用の表示モデルを接続します。読取り開始と実Taskの所有は専用managerが担当します。</summary>
    internal ChartPropertiesWindow(ChartPropertiesViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
    }

    private void WindowClosed(object sender, EventArgs e) => viewModel.Close();

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
