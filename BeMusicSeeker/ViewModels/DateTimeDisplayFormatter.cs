using System;
using System.Globalization;

namespace BeMusicSeeker.ViewModels;

/// <summary>一覧の秒単位の日時表示と、その表示値に対する検索候補を同じ書式へ揃えます。</summary>
internal static class DateTimeDisplayFormatter
{
    /// <summary>時刻帯を変換せず、受け取った日時を文化圏に依存しない秒までの表示へ整形します。</summary>
    /// <param name="value">表示に使う日時。</param>
    /// <returns>yyyy-MM-dd HH:mm:ss 形式の表示文字列。</returns>
    internal static string FormatTimestamp(DateTime value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
