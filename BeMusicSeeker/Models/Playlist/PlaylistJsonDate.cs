using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>表JSONの採用日時だけを固定文法で読み、壁時計のtickと明示offsetの既存変換を保ちます。</summary>
internal static class PlaylistJsonDate
{
    private static readonly Regex AcceptedSyntax = new(
        @"\A(?:[0-9]{4}/[0-9]{1,2}/[0-9]{1,2}(?: [0-9]{1,2}:[0-9]{1,2}:[0-9]{1,2}(?:[+-][0-9]{2}:[0-9]{2})?)?|[0-9]{4}-[0-9]{2}-[0-9]{2}(?:T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})?)?)\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>新規JSONには地域・offsetを付けない7桁小数秒の壁時計を出力します。</summary>
    internal static string Format(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    /// <summary>固定旧形式とISO日時を全体解析します。空値は呼出元が省略として扱います。</summary>
    /// <exception cref="PlaylistJsonDateException">非空入力が固定文法またはGregorian日時の範囲に適合しません。</exception>
    internal static DateTime Parse(string text, string field)
    {
        if (AcceptedSyntax.IsMatch(text))
        {
            bool iso = text[4] == '-';
            bool offset = text.EndsWith('Z') || (text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-');
            string format = iso
                ? text.Length == 10 ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
                : text.Contains(' ') ? "yyyy/M/d H:m:s" : "yyyy/M/d";
            if (offset)
            {
                format += text.EndsWith('Z') ? "'Z'" : "zzz";
                if (DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out DateTimeOffset instant))
                {
                    return instant.LocalDateTime;
                }
            }
            else if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime wallClock))
            {
                return DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
            }
        }
        throw new PlaylistJsonDateException(field);
    }

    /// <summary>既存header/data分類に包まれた日時原因だけを識別し、他の取得失敗のBMT復元を維持します。</summary>
    internal static bool IsDateFailure(Exception exception) => exception is PlaylistHeaderParseException or PlaylistDataParseException
        && exception.InnerException is PlaylistJsonDateException;
}

/// <summary>利用者が訂正する必要のある採用日時の不適合を、取得・構文等の失敗から区別します。</summary>
internal sealed class PlaylistJsonDateException : FormatException
{
    internal PlaylistJsonDateException(string field)
        : base(string.Format(CultureInfo.InvariantCulture, Resources.Error_PlaylistJsonDateFormat, field)) { }
}
