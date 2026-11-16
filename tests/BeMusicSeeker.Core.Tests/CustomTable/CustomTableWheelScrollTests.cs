using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


/// <summary>ホイールの回転量を行数へ換算する、状態を持たない計算の確認です。位置は先頭行の位置で、正の回転量は先頭側（小さい側）へ動きます。</summary>
[TestClass]
public sealed class CustomTableWheelScrollTests
{
    private const double Maximum = 100d;

    /// <summary>一刻み（120）で設定どおりの行数だけ先頭側へ動くことを確認し、回転量の符号と移動の向きの取り違え、設定の行数の無視を検出します。</summary>
    [TestMethod]
    [DataRow(3, 47d)]
    [DataRow(1, 49d)]
    [DataRow(5, 45d)]
    public void Convert_OneNotchMovesConfiguredLinesTowardHeadForPositiveDelta(int lines, double expected)
    {
        CustomTableWheelScroll.Result result = CustomTableWheelScroll.Convert(120, 0, lines, 10, 50d, Maximum);

        Assert.AreEqual(expected, result.Position);
        Assert.AreEqual(0, result.Remainder);
    }

    /// <summary>負の回転量は末尾側へ動くことを確認し、向きの取り違えを検出します。</summary>
    [TestMethod]
    public void Convert_NegativeDeltaMovesTowardTail()
    {
        CustomTableWheelScroll.Result result = CustomTableWheelScroll.Convert(-120, 0, 3, 10, 50d, Maximum);

        Assert.AreEqual(53d, result.Position);
        Assert.AreEqual(0, result.Remainder);
    }

    /// <summary>一回の通知が複数刻みなら「刻み数×行数」動き、一刻みに満たない端数は持ち越すことを確認し、刻み数の無視や端数の取りこぼしを検出します。</summary>
    [TestMethod]
    public void Convert_MultipleNotchesMoveNotchesTimesLinesAndKeepFraction()
    {
        CustomTableWheelScroll.Result result = CustomTableWheelScroll.Convert(200, 0, 3, 10, 50d, Maximum);

        Assert.AreEqual(47d, result.Position);
        Assert.AreEqual(80, result.Remainder);

        CustomTableWheelScroll.Result threeNotches = CustomTableWheelScroll.Convert(360, 0, 2, 10, 50d, Maximum);

        Assert.AreEqual(44d, threeNotches.Position);
        Assert.AreEqual(0, threeNotches.Remainder);
    }

    /// <summary>一刻み未満の通知は動かさず累積し、三回目で一刻み分（40×3）動くことを確認し、細かい回転量の取りこぼしを検出します。</summary>
    [TestMethod]
    public void Convert_AccumulatesSubNotchDeltaUntilOneNotch()
    {
        double position = 50d;
        int carry = 0;
        double[] expectedPositions = [50d, 50d, 47d];
        int[] expectedCarries = [40, 80, 0];
        for (int i = 0; i < 3; i++)
        {
            CustomTableWheelScroll.Result result = CustomTableWheelScroll.Convert(40, carry, 3, 10, position, Maximum);
            position = result.Position;
            carry = result.Remainder;

            Assert.AreEqual(expectedPositions[i], position, "通知 " + (i + 1));
            Assert.AreEqual(expectedCarries[i], carry, "通知 " + (i + 1));
        }
    }

    /// <summary>向きが変わった時点で累積を捨てることを確認し、反転後に古い累積が残って余計に動く誤りを検出します。</summary>
    [TestMethod]
    public void Convert_DiscardsCarryWhenDirectionReverses()
    {
        CustomTableWheelScroll.Result first = CustomTableWheelScroll.Convert(-80, 0, 3, 10, 50d, Maximum);
        CustomTableWheelScroll.Result second = CustomTableWheelScroll.Convert(-40, first.Remainder, 3, 10, first.Position, Maximum);

        Assert.AreEqual(50d, first.Position);
        Assert.AreEqual(53d, second.Position, "-80 の続きの -40 で一刻みに達する。");
        Assert.AreEqual(0, second.Remainder);

        CustomTableWheelScroll.Result up = CustomTableWheelScroll.Convert(80, 0, 3, 10, 50d, Maximum);
        CustomTableWheelScroll.Result reversed = CustomTableWheelScroll.Convert(-200, up.Remainder, 3, 10, up.Position, Maximum);
        CustomTableWheelScroll.Result afterwards = CustomTableWheelScroll.Convert(-40, reversed.Remainder, 3, 10, reversed.Position, Maximum);

        Assert.AreEqual(50d, up.Position);
        Assert.AreEqual(80, up.Remainder);
        Assert.AreEqual(53d, reversed.Position, "反転した時点で +80 を捨て、-200 は一刻みと -80 の持越しになる。");
        Assert.AreEqual(-80, reversed.Remainder);
        Assert.AreEqual(56d, afterwards.Position, "持越しの -80 と -40 で二刻み目に達する。");
        Assert.AreEqual(0, afterwards.Remainder);
    }

    /// <summary>一画面ずつ（-1）の設定では完全表示行容量だけ動き、横スクロールバーの有無を反映した容量を使うことを確認し、容量でなく固定の行数で動く誤りを検出します。</summary>
    [TestMethod]
    public void Convert_PageSettingMovesFullVisibleRowCapacity()
    {
        CustomTableWheelScroll.Result withoutBar = CustomTableWheelScroll.Convert(120, 0, CustomTableWheelScroll.PageScrollLines, 12, 50d, Maximum);
        CustomTableWheelScroll.Result withBar = CustomTableWheelScroll.Convert(120, 0, CustomTableWheelScroll.PageScrollLines, 11, 50d, Maximum);
        CustomTableWheelScroll.Result twoNotches = CustomTableWheelScroll.Convert(-240, 0, CustomTableWheelScroll.PageScrollLines, 12, 50d, Maximum);

        Assert.AreEqual(38d, withoutBar.Position);
        Assert.AreEqual(39d, withBar.Position);
        Assert.AreEqual(74d, twoNotches.Position);
    }

    /// <summary>設定が0なら位置も持越しも動かさないことを確認し、0を既定の三行へ読み替える誤りを検出します。</summary>
    [TestMethod]
    public void Convert_ZeroLinesDoesNotMove()
    {
        CustomTableWheelScroll.Result result = CustomTableWheelScroll.Convert(120, 40, 0, 10, 50d, Maximum);

        Assert.AreEqual(50d, result.Position);
        Assert.AreEqual(0, result.Remainder);
    }

    /// <summary>上端を越える分は端に留めて累積を捨てることを確認します。位置1で +180（一刻みと端数60）は0に留まり、持越しは0になります。</summary>
    [TestMethod]
    public void Convert_ClampsAtHeadAndDiscardsCarry()
    {
        CustomTableWheelScroll.Result clamped = CustomTableWheelScroll.Convert(180, 0, 3, 10, 1d, Maximum);
        // 破棄されなければ、続く +60 と持越しの 60 で一刻みに達してしまう。
        CustomTableWheelScroll.Result next = CustomTableWheelScroll.Convert(60, clamped.Remainder, 3, 10, clamped.Position, Maximum);

        Assert.AreEqual(0d, clamped.Position);
        Assert.AreEqual(0, clamped.Remainder);
        Assert.AreEqual(0d, next.Position);
        Assert.AreEqual(60, next.Remainder, "端で捨てた後は、新しい累積だけが残る。");
    }

    /// <summary>下端を越える分は最大値に留めて累積を捨てることを確認し、範囲外の位置を返す誤りや持越しの残留を検出します。</summary>
    [TestMethod]
    public void Convert_ClampsAtTailAndDiscardsCarry()
    {
        CustomTableWheelScroll.Result clamped = CustomTableWheelScroll.Convert(-180, 0, 3, 10, 99d, Maximum);

        Assert.AreEqual(Maximum, clamped.Position);
        Assert.AreEqual(0, clamped.Remainder);
    }

    /// <summary>ちょうど端に届く移動は越えていないため、端数の持越しを保つことを確認し、届いただけで累積を捨てる誤りを検出します。</summary>
    [TestMethod]
    public void Convert_ExactlyReachingEdgeKeepsCarry()
    {
        CustomTableWheelScroll.Result result = CustomTableWheelScroll.Convert(200, 0, 3, 10, 3d, Maximum);

        Assert.AreEqual(0d, result.Position);
        Assert.AreEqual(80, result.Remainder);
    }

    /// <summary>最大値が0（動かせない表）でも位置を0に保ち、例外を出さないことを確認します。</summary>
    [TestMethod]
    public void Convert_ZeroMaximumKeepsPositionAtZero()
    {
        CustomTableWheelScroll.Result result = CustomTableWheelScroll.Convert(-120, 0, 3, 10, 0d, 0d);

        Assert.AreEqual(0d, result.Position);
        Assert.AreEqual(0, result.Remainder);
    }
}
