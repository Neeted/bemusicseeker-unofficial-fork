using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Ribbit.Windows;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowPlacementRestoreTests
{
    private const int Normal = (int)Win32API.ShowWindowCommands.Normal;
    private const int Minimized = (int)Win32API.ShowWindowCommands.ShowMinimized;
    private const int Maximized = (int)Win32API.ShowWindowCommands.ShowMaximized;
    private const int RestoreToMaximized = 0x0002;

    /// <summary>
    /// 保存した表示状態と「元に戻すと最大化」のフラグから、次回起動で渡す表示状態が決まることを確認します。
    /// 最小化はフラグがあれば最大化、なければ通常、最大化と通常はそのままで、通常にフラグが付いていても通常のままです。
    /// 契約外の値は従来どおり素通しし、復元結果が最小化になることはありません。
    /// 最大化から最小化して終了した次回に通常サイズへ戻る誤りと、最小化のまま起動する誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(Minimized, RestoreToMaximized, Maximized, DisplayName = "最小化とフラグは最大化")]
    [DataRow(Minimized, 0, Normal, DisplayName = "フラグのない最小化は通常")]
    [DataRow(Minimized, 0x0001 | RestoreToMaximized, Maximized, DisplayName = "他のフラグが混ざっても最大化")]
    [DataRow(Minimized, 0x0001, Normal, DisplayName = "別のフラグだけの最小化は通常")]
    [DataRow(Maximized, 0, Maximized, DisplayName = "最大化は最大化")]
    [DataRow(Maximized, RestoreToMaximized, Maximized, DisplayName = "フラグ付きの最大化は最大化")]
    [DataRow(Normal, 0, Normal, DisplayName = "通常は通常")]
    [DataRow(Normal, RestoreToMaximized, Normal, DisplayName = "フラグがあっても通常は通常")]
    [DataRow(0, RestoreToMaximized, 0, DisplayName = "契約外の値は素通し")]
    [DataRow(7, 0, 7, DisplayName = "最小化の別の指定は素通し")]
    public void RestorePlacement_MapsSavedShowCommand(int savedShowCommand, int savedFlags, int expectedShowCommand)
    {
        var saved = new WindowPlacement(savedFlags, savedShowCommand, -1, -1, -2, -2, 30, 40, 930, 740);

        WindowPlacement restored = MainWindowViewSettingsPolicy.ResolveRestorePlacement(saved);

        Assert.AreEqual(expectedShowCommand, restored.ShowCommand);
        Assert.AreNotEqual(Minimized, restored.ShowCommand, "復元結果が最小化になってはならない。");
        Assert.AreEqual(0, restored.Flags, "SetWindowPlacementへ渡すFlagsは従来どおり0。");
    }

    /// <summary>
    /// 通常へ変換した場合も、通常時の位置と大きさ、最小化・最大化の位置を変えないことを確認します。
    /// 表示状態の変換が矩形を取り違える誤りと、保存した通常時の矩形を失う誤りを検出します。
    /// </summary>
    [TestMethod]
    public void RestorePlacement_KeepsNormalRectangleAndPositions()
    {
        var saved = new WindowPlacement(RestoreToMaximized, Minimized, 1, 2, 3, 4, 30, 40, 930, 740);

        WindowPlacement restored = MainWindowViewSettingsPolicy.ResolveRestorePlacement(saved);

        Assert.AreEqual(1, restored.MinX);
        Assert.AreEqual(2, restored.MinY);
        Assert.AreEqual(3, restored.MaxX);
        Assert.AreEqual(4, restored.MaxY);
        Assert.AreEqual(30, restored.Left);
        Assert.AreEqual(40, restored.Top);
        Assert.AreEqual(930, restored.Right);
        Assert.AreEqual(740, restored.Bottom);
    }
}
