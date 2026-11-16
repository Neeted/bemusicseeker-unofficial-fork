using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Components.RegularChartRefreshViewTestFixture;

namespace BeMusicSeeker.Tests;


public sealed partial class RegularChartNormalLibraryRefreshTests
{

    /// <summary>同じ専用元集合のsort/filter再評価だけで選択識別を持ち越し、同区分でも別の群・packageや通常一覧へ選択を持ち越さない境界を確認します。</summary>
    [TestMethod]
    public void SelectionIdentityScope_DistinguishesSubsetContextsAndNormalLibrary()
    {
        WithTemporarySongDb(path =>
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            Assert.IsTrue(BuildSelectionView(fixture, true, nameof(LibraryChartRow.Title), context: "group A"));
            ChartListVirtualView first = fixture.View;
            Assert.IsTrue(BuildSelectionView(fixture, true, nameof(LibraryChartRow.stddevVal), context: "group A"));
            Assert.IsTrue(first.SharesSelectionIdentityScope(fixture.View));
            Assert.IsTrue(BuildSelectionView(fixture, true, nameof(LibraryChartRow.Title), context: "group B"));
            Assert.IsFalse(first.SharesSelectionIdentityScope(fixture.View));
            Assert.IsTrue(BuildSelectionView(fixture, false, nameof(LibraryChartRow.Title)));
            Assert.IsFalse(first.SharesSelectionIdentityScope(fixture.View));
        });
    }

}
