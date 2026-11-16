using System;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>解析と索引で共有する変更不能な譜面詳細値です。保存行やDB接続を保持しません。</summary>
public sealed record ChartDetails
{
    private string _sha256;

    private string _md5;

    private string _charthash;

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
            _sha256 = NormalizeSha256(value);
        }
    }

    /// <summary>
    /// 内容を識別する MD5 です。
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
    /// 譜面配置内容から算出した beatoraja 互換の chart hash です。
    /// </summary>
    public string charthash
    {
        get
        {
            return _charthash;
        }
        init
        {
            _charthash = NormalizeSha256(value);
        }
    }

    /// <summary>
    /// 表記レベルです。
    /// </summary>
    public int? level { get; init; }

    /// <summary>
    /// 譜面難易度種別です。
    /// </summary>
    public int? difficulty { get; init; }

    /// <summary>
    /// 譜面難易度種別が譜面内で明示されていたかどうかです。
    /// </summary>
    public bool difficulty_defined { get; init; }

    /// <summary>
    /// ノーツ数が最も多い BPM です。
    /// </summary>
    public double? mainbpm { get; init; }

    /// <summary>
    /// 最大 BPM です。
    /// </summary>
    public double? maxbpm { get; init; }

    /// <summary>
    /// 最小 BPM です。
    /// </summary>
    public double? minbpm { get; init; }

    /// <summary>
    /// 譜面終端までの長さをミリ秒で保持します。
    /// </summary>
    public int? length { get; init; }

    /// <summary>
    /// 鍵盤数に相当するモード値です。
    /// </summary>
    public int? mode { get; init; }

    /// <summary>
    /// beatoraja 互換の判定幅倍率値です。
    /// </summary>
    public int? judge { get; init; }

    /// <summary>
    /// LR2 song.bga へ反映する BGA 使用有無です。
    /// </summary>
    public int? bga { get; init; }

    /// <summary>
    /// LR2 song.exlevel へ反映する #EXLEVEL の raw 値です。
    /// </summary>
    public int? exlevel { get; init; }

    /// <summary>
    /// 譜面特徴を表す bit flag です。
    /// </summary>
    public int feature { get; init; }

    /// <summary>
    /// 総ノーツ数です。
    /// </summary>
    public int notes { get; init; }

    /// <summary>
    /// 通常鍵盤ノーツ数です。
    /// </summary>
    public int n { get; init; }

    /// <summary>
    /// ロング鍵盤ノーツ数です。
    /// </summary>
    public int ln { get; init; }

    /// <summary>
    /// 通常スクラッチノーツ数です。
    /// </summary>
    public int s { get; init; }

    /// <summary>
    /// ロングスクラッチノーツ数です。
    /// </summary>
    public int ls { get; init; }

    /// <summary>
    /// 表示・ソートに使う TOTAL 有効値です。
    /// </summary>
    public double? total { get; init; }

    /// <summary>
    /// TOTAL が譜面内で明示されていたかどうかです。
    /// </summary>
    public bool total_defined { get; init; }

    /// <summary>
    /// 平均密度です。
    /// </summary>
    public double? density { get; init; }

    /// <summary>
    /// 最大密度です。
    /// </summary>
    public double? peakdensity { get; init; }

    /// <summary>
    /// 終盤最大密度です。
    /// </summary>
    public double? enddensity { get; init; }

    /// <summary>
    /// beatoraja songinfo 互換のノーツ分布文字列です。
    /// </summary>
    public string distribution { get; init; }

    /// <summary>
    /// beatoraja songinfo 互換の変速列です。
    /// </summary>
    public string speedchange { get; init; }

    /// <summary>
    /// 一覧表示向けに保持する変速回数です。
    /// </summary>
    public int speedchange_count { get; init; }

    /// <summary>
    /// beatoraja songinfo 互換のレーン別ノーツ数です。
    /// </summary>
    public string lanenotes { get; init; }

    /// <summary>
    /// この値を生成した解析器のバージョンです。
    /// </summary>
    public int parser_version { get; init; }

    /// <summary>
    /// この値を最後に更新した UTC 時刻です。
    /// </summary>
    public DateTime updated_at { get; init; }

    internal static string NormalizeSha256ForInternalUse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new FormatException(string.Format(BeMusicSeeker.Properties.Resources.Error_InvalidHashFormat, "SHA256"));
        }
        return value.ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        return NormalizeSha256ForInternalUse(value);
    }
}
