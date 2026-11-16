using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class KeywordSearchPresentationTests
{
    /// <remarks>診断の意味は下位の検索テストが担い、ここではMainWindowの構成からworkspaceの警告公開までの接続を確認します。共有設定を使う構成のため並列実行しません。</remarks>
    /// <summary>構成済みworkspaceの一覧検索へ非対応memoを設定すると警告文と警告有無が更新されることを確認し、入力から警告表示状態への接続漏れを検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlaylistSummaryKeywordFilterChangedThroughWorkspaceUpdatesWarningPresentation()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;

        viewModel.PlaylistWorkspace.PlaylistSummaryKeywordFilter = "memo:alpha";

        StringAssert.Contains(viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchWarningText, "memo");
        Assert.IsTrue(viewModel.PlaylistWorkspace.HasPlaylistSummaryKeywordSearchWarning);
    }

}
