using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfPickerBoundaryTests
{
    /// <summary>有効filterの組と重複拡張子を保持し、不正filterだけを全ファイルの有効な組へ正規化します。</summary>
    [TestMethod]
    public void WpfPickerUtilityPreservesPairsAndNormalizesMalformedFilter()
    {
        Assert.AreEqual(
            "song.db (*.db)|*.db|すべてのファイル(*.*)|*.*",
            UiFilePickerUtilities.BuildFilter("song.db (*.db)|*.db|すべてのファイル(*.*)|*.*"));
        Assert.AreEqual("*.*|*.*", UiFilePickerUtilities.BuildFilter("broken"));
        CollectionAssert.AreEqual(
            new[] { "*.db", "*.db" },
            UiFilePickerUtilities.ParseFilterPairs("db|*.db|all|*.db").Select(pair => pair.Item2).ToArray());


    }
}
