using System;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// 構成済みの表イベントを、行の同一性を保って再生ownerへ渡します。
/// </summary>
internal sealed class MainWindowPlaybackTerminal
{
    private readonly Action<object> handleTableSelection;

    private readonly Func<int, object, Action, Task> handleTableRowActivation;

    /// <summary>
    /// 表選択と、受付後の面選択要求を伴う行activationの接続を作ります。
    /// </summary>
    /// <param name="handleTableSelection">選択された行を同一の対象として受け取る処理。</param>
    /// <param name="handleTableRowActivation">行・indexと受付後の面要求を受け取り、再生要求の実終端を返す処理。</param>
    internal MainWindowPlaybackTerminal(
        Action<object> handleTableSelection,
        Func<int, object, Action, Task> handleTableRowActivation)
    {
        this.handleTableSelection = handleTableSelection
            ?? throw new ArgumentNullException(nameof(handleTableSelection));
        this.handleTableRowActivation = handleTableRowActivation
            ?? throw new ArgumentNullException(nameof(handleTableRowActivation));
    }

    /// <summary>
    /// 表の選択行を同一の対象として転送します。
    /// </summary>
    /// <param name="selectedRow">表イベントで渡された行。</param>
    internal void HandleTableSelection(object selectedRow)
        => handleTableSelection(selectedRow);

    /// <summary>
    /// 表の行・indexと要求内の面選択callbackを再生ownerへ転送します。
    /// </summary>
    /// <param name="rowIndex">表イベントで渡されたindex。</param>
    /// <param name="row">表イベントで渡された行。</param>
    /// <param name="requestPlayerSurface">受付後にViewが面の利用可能性と接続寿命を確認する処理。</param>
    /// <returns>転送した要求の実終端。面要求のUI配送完了は含みません。</returns>
    internal Task HandleTableRowActivation(int rowIndex, object row, Action requestPlayerSurface)
        => handleTableRowActivation(rowIndex, row, requestPlayerSurface);

    /// <summary>
    /// 既存再生ownerへ直接接続した標準terminalを作ります。
    /// </summary>
    /// <param name="viewModel">再生ownerを所有する画面ViewModel。</param>
    /// <returns>既存ownerへ転送する接続。</returns>
    internal static MainWindowPlaybackTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowPlaybackTerminal(
            viewModel.PlaybackPanel.HandleTableSelection,
            viewModel.PlaybackPanel.HandleTableRowActivation);
    }
}
