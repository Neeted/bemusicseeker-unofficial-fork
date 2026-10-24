using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Views;

/// <summary>
/// LR2 play history schema のアンインストール範囲を確認するダイアログです。
/// trigger だけを外す操作と、履歴 table も削除する操作の事故を避けるため別ウィンドウで明示します。
/// </summary>
public partial class Lr2PlayHistorySchemaUninstallDialog : ThemedWindow
{
    /// <summary>
    /// ダイアログを初期化します。
    /// </summary>
    /// <param name="candidateDiscardNotice">同じ承認で破棄する未確定設定の案内。候補がなければ省略します。</param>
    /// <param name="scoreDbPath">操作対象として表示する player score DB path。</param>
    public Lr2PlayHistorySchemaUninstallDialog(string scoreDbPath, string candidateDiscardNotice = null)
    {
        InitializeComponent();
        DataContext = new ViewModel(scoreDbPath, candidateDiscardNotice);
    }

    /// <summary>
    /// ユーザーが選択した LR2 play history schema のアンインストール範囲を返します。
    /// </summary>
    internal Lr2PlayHistorySchemaUninstallMode SelectedMode { get; private set; } = Lr2PlayHistorySchemaUninstallMode.TriggersOnly;

    private void SaveAndClose(object sender, RoutedEventArgs e)
    {
        SelectedMode = radioButtonTablesAndTriggers.IsChecked == true
            ? Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers
            : Lr2PlayHistorySchemaUninstallMode.TriggersOnly;
        DialogResult = true;
    }

    private sealed class ViewModel(string scoreDbPath, string candidateDiscardNotice)
    {
        public string CandidateDiscardNotice { get; } = candidateDiscardNotice ?? string.Empty;

        public string ScoreDbPath { get; } = scoreDbPath ?? string.Empty;
    }
}
