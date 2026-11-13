using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>
/// プレイリストの明示要求が受付（P）の競合で拒否されたときの案内の判定基準を共有します。
/// 規則は操作の競合ポリシーの「明示要求の競合の案内」です。
/// </summary>
internal static class PlaylistBusyNoticeAssertions
{
    /// <summary>
    /// 届いた通知が、ちょうど指定件数の競合の案内（<c>Warn_LibraryOperationBusy</c>、警告、見出し <c>Warning</c>）だけであることを確認します。
    /// 汎用の失敗（<c>Msg_error_unexpected</c>、Hand）や入力不備（<c>Msg_invalid_setting</c>）の案内が重なると、本文・重大度・件数の比較で失敗します。
    /// </summary>
    internal static void AssertOnlyBusyWarnings(IReadOnlyList<UiMessageRequest> messages, int expectedCount)
    {
        Assert.AreEqual(expectedCount, messages.Count, string.Join(" | ", messages.Select(message => message.MessageBoxText)));
        foreach (UiMessageRequest message in messages)
        {
            Assert.AreEqual(Resources.Warn_LibraryOperationBusy, message.MessageBoxText);
            Assert.AreEqual(System.Windows.MessageBoxImage.Exclamation, message.Icon);
            Assert.AreEqual(Resources.Warning, message.Caption);
        }
    }
}
