using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryFolderTreeViewModelTests
{
    /// <summary>
    /// 保証：実UI Dispatcherのない構築を拒否し、後の表示反映を実行できない所有者を成立させない。
    /// </summary>
    [TestMethod]
    public void Constructor_RequiresLiveUiDispatcher()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new LibraryFolderTreeViewModel(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => null!)));
    }

}
