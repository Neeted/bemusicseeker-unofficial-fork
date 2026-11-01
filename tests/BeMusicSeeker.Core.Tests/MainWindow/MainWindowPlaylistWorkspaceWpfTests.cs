using System.Windows;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


/// <summary>実プレイリスト画面の編集・通知・所有モーダル・終了と、compiled操作の配送を検証します。</summary>
/// <remarks>保存・候補・受付条件の網羅はPlaylistの管理主体へ分担し、ここでは実DB/HTTPからの表示反映とWindow所有、イベント接続を観測します。</remarks>
[TestClass]
// 実MainWindowと所有モーダルがApplication資源・共通SQLite/native音声終端へ到達するため並列化しません。
[DoNotParallelize]
public sealed class MainWindowPlaylistWorkspaceWpfTests
{

    /// <summary>最小化した親を復元した後にactivationとfocusを試み、activationがfalseでもfocus要求を省きません。</summary>
    [TestMethod]
    public void MainWindowForegroundTerminal_RestoresMinimizedThenAttemptsActivationAndFocus()
    {
        WindowState state = WindowState.Minimized;
        var operations = new List<string>();
        var terminal = new MainWindowForegroundTerminal(
            () => state,
            () =>
            {
                operations.Add("restore");
                state = WindowState.Normal;
            },
            () =>
            {
                operations.Add("activate");
                return false;
            },
            () =>
            {
                operations.Add("focus");
                return false;
            });

        terminal.FocusMainWindow();

        CollectionAssert.AreEqual(new[] { "restore", "activate", "focus" }, operations);
        Assert.AreEqual(WindowState.Normal, state);
    }

}
