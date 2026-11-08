using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>観測の境界を持たないmodule（Core）で、未宣言のダイアログ窓口を作れないことを保証します。</summary>
[TestClass]
public sealed class UndeclaredUiDialogServiceBoundaryTests
{
    /// <summary>
    /// 条件：共有ホストの観測を有効にしていないCore moduleで、窓口を作ります。
    /// 期待結果：作成時に <see cref="InvalidOperationException"/> を投げます。
    /// 検出する誤り：検査がなく黙って作れる（Coreでは記録が消費されず、未宣言の要求が通る）、最初の呼出しまで検査を遅らせる。
    /// 前提の崩れ（Coreが観測を有効にした場合）は、誤った成功ではなく前提の表明で失敗させます。
    /// </summary>
    [TestMethod]
    public void Constructor_WhenHostObservationIsDisabled_Throws()
    {
        Assert.IsFalse(HostResidueBoundary.IsEnabled, "このmoduleは共有ホストの観測を有効にしない前提です。");

        Assert.ThrowsExactly<InvalidOperationException>(() => new UndeclaredUiDialogService());
    }
}
