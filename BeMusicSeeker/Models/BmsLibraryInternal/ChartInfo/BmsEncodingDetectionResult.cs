namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>既存BMS文字コード判定の確度と種別です。</summary>
internal enum BmsEncodingDetectionOutcome
{
    Other = 0,
    ShiftJis,
    ShiftJisQuestion,
    Korean,
    KoreanQuestion,
    Utf8,
    Unknown
}

/// <summary>一回の入力判定で確定した不変結果です。復号文字列は同じ基本解析の中でだけ利用します。</summary>
internal sealed class BmsEncodingDetectionResult
{
    /// <summary>判定結果を捕捉します。保存行や現在値への参照は保持しません。</summary>
    internal BmsEncodingDetectionResult(
        string encodingName,
        BmsEncodingDetectionOutcome outcome,
        bool fastAscii,
        string decodedText)
    {
        EncodingName = string.IsNullOrWhiteSpace(encodingName) ? "unknown" : encodingName;
        Outcome = outcome;
        FastAscii = fastAscii;
        DecodedText = decodedText;
    }

    /// <summary>判定した文字コード名です。</summary>
    public string EncodingName { get; }

    /// <summary>判定種別と確度です。</summary>
    public BmsEncodingDetectionOutcome Outcome { get; }

    /// <summary>ASCIIだけの入力として簡易判定したか。</summary>
    public bool FastAscii { get; }

    /// <summary>検出過程で得た短命な復号結果です。</summary>
    public string DecodedText { get; }
}
