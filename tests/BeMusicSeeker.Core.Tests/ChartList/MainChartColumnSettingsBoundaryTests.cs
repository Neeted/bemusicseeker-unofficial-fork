using BeMusicSeeker.Models;
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

    /// <summary>
    /// 分裂配置の一覧は、重複の一覧と同じ <c>DUPLICATE</c> の保存値の列設定を読む。
    /// 検出する誤り：分裂配置が既定の列設定へ落ちる、または別の保存値を新設して重複の列の調整が引き継がれない。
    /// </summary>
    [TestMethod]
    [DataRow("DuplicateFilterSelected")]
    [DataRow("SplitPlacementFilterSelected")]
    public void MainChartListUsesDuplicateColumnSettingsForDuplicateAndSplitPlacementViews(string modeName)
    {
        MainViewUpdateMode mode = Enum.Parse<MainViewUpdateMode>(modeName);
        var store = new FakeMainChartColumnSettingsStore();
        var mainChartList = new MainChartListViewModel(action => action(), _ => { }, store);

        MainChartListColumnSelection selection = mainChartList.LoadColumnSetting(mode, mode, isInit: false);

        Assert.AreEqual(CustomTableColumnSettings.ViewKind.DUPLICATE, store.LastMainViewKind);
        Assert.AreSame(store.MainColumns, selection.ColumnsSettings);
    }

}
