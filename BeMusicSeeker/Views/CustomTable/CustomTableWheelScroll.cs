using System;

namespace BeMusicSeeker.Views;

/// <summary>
/// マウスホイールの回転量を、一覧の縦スクロールの行数へ換算します。状態を持たず、回転量の持越しは呼出し側が保持します。
/// 一刻み（<see cref="NotchDelta"/>）で OSの設定 <c>WheelScrollLines</c> 行動かし、高精度のホイールが送る一刻み未満の回転量も累積して失いません。
/// </summary>
internal static class CustomTableWheelScroll
{
    /// <summary>ホイール一刻みの回転量（Windowsの <c>WHEEL_DELTA</c>）です。</summary>
    internal const int NotchDelta = 120;

    /// <summary>OSの設定が「一画面ずつ」を表す値です。</summary>
    internal const int PageScrollLines = -1;

    /// <summary>換算の結果です。</summary>
    /// <param name="Position">新しい先頭行の位置。</param>
    /// <param name="Remainder">次の入力へ持ち越す、一刻みに満たない回転量。</param>
    internal readonly record struct Result(double Position, int Remainder);

    /// <summary>
    /// 回転量を累積して、新しい先頭行の位置と持越しを求めます。正の回転量は先頭側（位置が小さい側）へ動かします。
    /// 向きが前回の持越しと逆なら持越しを捨て、範囲の端を越える分は端に留めて持越しを捨てます。
    /// </summary>
    /// <param name="delta">今回の回転量。0以外。</param>
    /// <param name="carry">前回までの持越し。</param>
    /// <param name="linesPerNotch">一刻みで動かす行数。<see cref="PageScrollLines"/>（負の値）は完全表示行容量、0は動かしません。</param>
    /// <param name="fullVisibleRows">完全表示行容量。横スクロールバーの有無を反映した値です。</param>
    /// <param name="position">現在の先頭行の位置。</param>
    /// <param name="maximum">先頭行の位置の最大値。最小値は0です。</param>
    internal static Result Convert(int delta, int carry, int linesPerNotch, int fullVisibleRows, double position, double maximum)
    {
        if (linesPerNotch == 0)
        {
            return new Result(position, 0);
        }
        if (carry != 0 && Math.Sign(carry) != Math.Sign(delta))
        {
            carry = 0;
        }
        long total = (long)carry + delta;
        int notches = (int)(total / NotchDelta);
        int remainder = (int)(total % NotchDelta);
        if (notches == 0)
        {
            return new Result(position, remainder);
        }
        int lines = linesPerNotch < 0 ? Math.Max(1, fullVisibleRows) : linesPerNotch;
        double target = position - (double)notches * lines;
        double clamped = Math.Clamp(target, 0d, Math.Max(0d, maximum));
        return new Result(clamped, clamped == target ? remainder : 0);
    }
}
