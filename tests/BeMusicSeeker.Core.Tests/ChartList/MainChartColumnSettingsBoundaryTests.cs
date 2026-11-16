using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.MainChartColumnSettingsTestDoubles;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class MainChartColumnSettingsBoundaryTests
{

    /// <summary>初期一覧の読み込みで列resetとsummary互換補完を保存主体へ要求し、取得した列を実選択結果へ渡します。</summary>
    [TestMethod]
    public void MainChartListUsesStoreForInitializationAndSummaryCompatibility()
    {
        var store = new FakeMainChartColumnSettingsStore();
        var mainChartList = new MainChartListViewModel(action => action(), _ => { }, store);

        MainChartListColumnSelection selection = mainChartList.LoadColumnSetting(
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected,
            isInit: true);

        Assert.AreSame(store.MainColumns, selection.ColumnsSettings);
        Assert.AreSame(store.PlaylistSummaryColumns, selection.PlaylistSummaryColumnsSettings);
        Assert.IsTrue(store.LastReset);
        Assert.IsTrue(store.LastEnsurePlaylistSummary);
    }

}
