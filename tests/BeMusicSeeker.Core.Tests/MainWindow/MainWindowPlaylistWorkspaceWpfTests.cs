using System.Windows;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


/// <summary>局所delegateとWindowState値で、復元・activation・focus要求の順序を検証します。</summary>
[TestClass]
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
