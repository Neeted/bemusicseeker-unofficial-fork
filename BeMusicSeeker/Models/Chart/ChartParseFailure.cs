using System;

namespace BeMusicSeeker.Models;

/// <summary>解析と索引で共有する変更不能な解析失敗値です。保存行やDB接続を保持しません。</summary>
public sealed record ChartParseFailure
{
    private string _md5;

    private string _sha256;

    /// <summary>
    /// LR2 song.hash と対応する MD5 です。
    /// path が変わっても同一譜面内容なら再解析を避けるため主キーにします。
    /// </summary>
    public string md5
    {
        get
        {
            return _md5;
        }
        init
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _md5 = null;
                return;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-fA-F]{32}$"))
            {
                throw new FormatException(string.Format(BeMusicSeeker.Properties.Resources.Error_InvalidHashFormat, "MD5"));
            }
            _md5 = value.ToLowerInvariant();
        }
    }

    /// <summary>
    /// 譜面ファイル内容の SHA-256 です。
    /// </summary>
    public string sha256
    {
        get
        {
            return _sha256;
        }
        init
        {
            _sha256 = ChartDetails.NormalizeSha256ForInternalUse(value);
        }
    }

    /// <summary>
    /// 最後に解析失敗した path です。
    /// </summary>
    public string path { get; init; }

    /// <summary>
    /// この失敗記録を生成した解析器のバージョンです。
    /// </summary>
    public int parser_version { get; init; }

    /// <summary>
    /// 失敗種別です。parse_failed または timeout を保存します。
    /// </summary>
    public string failure_kind { get; init; }

    /// <summary>
    /// 例外型名です。
    /// </summary>
    public string exception_type { get; init; }

    /// <summary>
    /// 表示用に短縮・正規化した例外メッセージです。
    /// </summary>
    public string message { get; init; }

    /// <summary>
    /// timeout 失敗時に使用した timeout ミリ秒です。
    /// </summary>
    public int? parse_timeout_ms { get; init; }

    /// <summary>
    /// この値を最後に更新した UTC 時刻です。
    /// </summary>
    public DateTime updated_at { get; init; }
}
