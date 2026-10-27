using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BeMusicSeeker.Properties;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

/// <summary>モデル直接読取りとローカルJSON初回の厳格性を、外部表の任意日時の互換読取りから区別します。</summary>
internal enum PlaylistJsonDatePolicy
{
    /// <summary>固定文法以外の採用日時で表を拒否します。</summary>
    Strict,
    /// <summary>限定した互換文法を追加し、不適合日時の代入だけを省略します。</summary>
    Compatible
}

/// <summary>表JSONの採用日時だけを固定文法で読み、壁時計のtickと明示offsetの既存変換を保ちます。</summary>
internal static class PlaylistJsonDate
{
    private static readonly Regex AcceptedSyntax = new(
        @"\A(?:[0-9]{4}/[0-9]{1,2}/[0-9]{1,2}(?: [0-9]{1,2}:[0-9]{1,2}:[0-9]{1,2}(?:[+-][0-9]{2}:[0-9]{2})?)?|[0-9]{4}-[0-9]{2}-[0-9]{2}(?:T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})?)?)\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CompatibleSyntax = new(
        @"\A(?:[0-9]{8}|[0-9]{4}\.[0-9]{1,2}\.[0-9]{1,2}|[0-9]{4}/[0-9]{1,2}/[0-9]{1,2} [0-9]{1,2}:[0-9]{1,2})\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex YearLastSyntax = new(@"\A[0-9]{1,2}([/.])[0-9]{1,2}\1[0-9]{4}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WeekdaySyntax = new(@"\A([0-9]{4}/[0-9]{1,2}/[0-9]{1,2}) \((日|月|火|水|木|金|土|Sun|Mon|Tue|Wed|Thu|Fri|Sat)\)\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>新規JSONには地域・offsetを付けない7桁小数秒の壁時計を出力します。</summary>
    internal static string Format(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    /// <summary>採用tokenを読み、欠落または互換読取りの不適合では代入を省略します。原tokenは変更しません。</summary>
    /// <param name="token">DateParseHandling.Noneで読んだ解析前の値。</param>
    /// <param name="field">採用する日時の欄名。</param>
    /// <param name="policy">操作元で確定した日時方針。</param>
    /// <param name="diagnostics">文書内の不適合値を集約する一時診断。単独行の直接読取りではnull。</param>
    /// <param name="rowIndex">元data配列のindex。headerでは-1。</param>
    /// <param name="identity">不適合の初出行を追う識別値。</param>
    /// <returns>採用日時。欠落又はCompatibleの不適合はnull。</returns>
    /// <exception cref="PlaylistJsonDateException">Strictで採用する非空日時が固定文法・範囲に適合しません。</exception>
    internal static DateTime? Read(JToken token, string field, PlaylistJsonDatePolicy policy, Diagnostics diagnostics, int rowIndex = -1, string identity = null)
    {
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.String && string.IsNullOrWhiteSpace(token.Value<string>())) { return null; }
        string text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
        string reason = "syntax_or_range";
        if (token.Type == JTokenType.String || policy == PlaylistJsonDatePolicy.Compatible && token.Type == JTokenType.Integer)
        {
            string input = policy == PlaylistJsonDatePolicy.Compatible ? text.Trim() : text;
            // 数値tokenの受理は8桁整数だけ。浮動小数や指数を丸めて日付へ変換しません。
            if ((token.Type == JTokenType.String || Regex.IsMatch(input, @"\A[0-9]{8}\z", RegexOptions.CultureInvariant))
                && TryParse(input, policy, out DateTime value, out reason)) { return value; }
        }
        else { reason = "unsupported_token"; }
        if (diagnostics != null) { diagnostics.Record(field, token.Type, text, reason, rowIndex, identity); }
        else
        {
            using var single = new Diagnostics("entry", null);
            single.Record(field, token.Type, text, reason, rowIndex, identity);
        }
        if (policy == PlaylistJsonDatePolicy.Strict) { throw new PlaylistJsonDateException(field); }
        return null;
    }

    private static bool TryParse(string text, PlaylistJsonDatePolicy policy, out DateTime value, out string reason)
    {
        value = default;
        reason = "syntax_or_range";
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
                if (DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset instant))
                { value = instant.LocalDateTime; return true; }
                return false;
            }
            return DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }
        if (policy != PlaylistJsonDatePolicy.Compatible) { return false; }
        if (CompatibleSyntax.IsMatch(text))
        {
            string format = text.Length == 8 && !text.Contains('.') && !text.Contains('/') ? "yyyyMMdd"
                : text.Contains('.') ? "yyyy.M.d" : "yyyy/M/d H:m";
            return DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }
        if (YearLastSyntax.IsMatch(text))
        {
            string separator = text.Contains('/') ? "/" : ".";
            bool dayFirst = DateTime.TryParseExact(text, "d" + separator + "M" + separator + "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dmy);
            bool monthFirst = DateTime.TryParseExact(text, "M" + separator + "d" + separator + "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime mdy);
            if (dayFirst && monthFirst && dmy != mdy) { reason = "ambiguous_month_day"; return false; }
            value = dayFirst ? dmy : mdy;
            return dayFirst || monthFirst;
        }
        Match weekday = WeekdaySyntax.Match(text);
        if (weekday.Success && DateTime.TryParseExact(weekday.Groups[1].Value, "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            string[] japanese = ["日", "月", "火", "水", "木", "金", "土"];
            string[] english = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
            string name = weekday.Groups[2].Value;
            if (name == japanese[(int)value.DayOfWeek] || name == english[(int)value.DayOfWeek]) { return true; }
            reason = "weekday_mismatch";
        }
        return false;
    }

    /// <summary>文書の不適合値をfield・型・decoded値ごとに集約し、全異値と初出行・件数をWARNへ渡します。永続状態は持ちません。</summary>
    /// <param name="context">文書種類・表・操作・取得元を追う診断文脈。</param>
    /// <param name="logWarning">既存診断窓口。nullではFileLoggerを使います。</param>
    internal sealed class Diagnostics(string context, Action<Exception, string> logWarning) : IDisposable
    {
        private readonly Dictionary<(string Field, JTokenType Type, string Text), (string Reason, int Index, string Identity, int Count)> invalid = [];

        /// <summary>解析前の値を文書内で集約し、同じfield・型・値の初出行と件数を保持します。</summary>
        internal void Record(string field, JTokenType type, string text, string reason, int rowIndex, string identity)
        {
            (string field, JTokenType type, string text) key = (field, type, text);
            if (invalid.TryGetValue(key, out (string Reason, int Index, string Identity, int Count) old)) { invalid[key] = (old.Reason, old.Index, old.Identity, old.Count + 1); }
            else { invalid.Add(key, (reason, rowIndex, identity, 1)); }
        }

        /// <summary>行ごとの同期ログを避け、一文書一回で全不適合値を通知します。診断故障は本来の読取り結果へ伝播しません。</summary>
        public void Dispose()
        {
            if (invalid.Count == 0) { return; }
            var values = new JArray();
            foreach (KeyValuePair<(string Field, JTokenType Type, string Text), (string Reason, int Index, string Identity, int Count)> item in invalid)
            {
                values.Add(new JObject
                {
                    ["field"] = item.Key.Field,
                    ["tokenType"] = item.Key.Type.ToString(),
                    ["value"] = item.Key.Text,
                    ["reason"] = item.Value.Reason,
                    ["firstIndex"] = item.Value.Index,
                    ["identity"] = item.Value.Identity,
                    ["count"] = item.Value.Count
                });
            }
            invalid.Clear();
            try
            {
                string message = "playlist_json_date_invalid " + context + " values=" + values.ToString(Formatting.None);
                if (logWarning != null) { logWarning(null, message); }
                else { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(message); }
            }
            catch { /* 日時診断先の故障で元の取得・構文・保存結果を変えません。 */ }
        }
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
