using System;
using System.IO;
using System.Text;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>捕捉済みBMS入力の文字コードだけを判定します。保存行・所持項目・リソース結果は保持しません。</summary>
internal static class BmsEncodingDetector
{
    private static readonly Encoding sjisEnc = Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback());
    private static readonly Encoding koreanEnc = Encoding.GetEncoding("ks_c_5601-1987", new EncoderExceptionFallback(), new DecoderExceptionFallback());
    private static readonly Encoding utf8Enc = Encoding.GetEncoding("utf-8", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    /// <summary>捕捉済みのバイト列から既存の判定規則で文字コードと必要な復号結果を返します。</summary>
    internal static BmsEncodingDetectionResult Detect(byte[] bytes)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }
        if (IsAsciiOnly(bytes))
        {
            return CreateEncodingDetectionResult("shift_jis", BmsEncodingDetectionOutcome.ShiftJis, fastAscii: true, decodedText: null);
        }

        if (!TryDecodeBytes(bytes, sjisEnc, out string sjisText))
        {
            if (TryDecodeBytes(bytes, koreanEnc, out string koreanText))
            {
                return CreateEncodingDetectionResult("ks_c_5601-1987", BmsEncodingDetectionOutcome.Korean, fastAscii: false, decodedText: koreanText);
            }
            if (TryDecodeBytes(bytes, utf8Enc, out string utf8Text))
            {
                return CreateEncodingDetectionResult("utf-8", BmsEncodingDetectionOutcome.Utf8, fastAscii: false, decodedText: utf8Text);
            }
            return CreateEncodingDetectionResult("unknown", BmsEncodingDetectionOutcome.Unknown, fastAscii: false, decodedText: null);
        }

        if (!ContainsNonBasicLatinNonWhitespace(sjisText))
        {
            return CreateEncodingDetectionResult("shift_jis", BmsEncodingDetectionOutcome.ShiftJis, fastAscii: false, decodedText: null);
        }
        if (HasInvalidJapaneseKanaSequence(sjisText))
        {
            return CreateEncodingDetectionResult("ks_c_5601-1987?", BmsEncodingDetectionOutcome.KoreanQuestion, fastAscii: false, decodedText: null);
        }
        if (HasJapaneseDetectionRun(sjisText))
        {
            return CreateEncodingDetectionResult("shift_jis?", BmsEncodingDetectionOutcome.ShiftJisQuestion, fastAscii: false, decodedText: null);
        }
        if (TryDecodeBytes(bytes, koreanEnc, out string koreanQuestionText))
        {
            if (HasHangulDetectionRunIgnoringWhitespace(koreanQuestionText))
            {
                return CreateEncodingDetectionResult("ks_c_5601-1987?", BmsEncodingDetectionOutcome.KoreanQuestion, fastAscii: false, decodedText: null);
            }
            return CreateEncodingDetectionResult("shift_jis?", BmsEncodingDetectionOutcome.ShiftJisQuestion, fastAscii: false, decodedText: null);
        }
        return CreateEncodingDetectionResult("shift_jis", BmsEncodingDetectionOutcome.ShiftJis, fastAscii: false, decodedText: null);
    }

    private static BmsEncodingDetectionResult CreateEncodingDetectionResult(
        string encodingName,
        BmsEncodingDetectionOutcome outcome,
        bool fastAscii,
        string decodedText)
    {
        return new BmsEncodingDetectionResult(encodingName, outcome, fastAscii, decodedText);
    }

    private static bool IsAsciiOnly(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] > 0x7F)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryDecodeBytes(byte[] bytes, Encoding encoding, out string text)
    {
        try
        {
            text = DecodeBytes(bytes, encoding);
            return true;
        }
        catch
        {
            text = null;
            return false;
        }
    }

    /// <summary>保存入力を開き直さず、指定した文字コードで同じバイト列を復号します。</summary>
    internal static string DecodeBytes(byte[] bytes, Encoding encoding)
    {
        using var stream = new MemoryStream(bytes ?? [], writable: false);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool ContainsNonBasicLatinNonWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c > '\u007F' && !char.IsWhiteSpace(c))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasJapaneseDetectionRun(string text)
    {
        int runLength = 0;
        for (int i = 0; i < (text?.Length ?? 0); i++)
        {
            if (IsJapaneseDetectionChar(text[i]))
            {
                runLength++;
                if (runLength >= 2)
                {
                    return true;
                }
            }
            else
            {
                runLength = 0;
            }
        }
        return false;
    }

    private static bool IsJapaneseDetectionChar(char c)
    {
        return (c >= '\u4E00' && c <= '\u9FFF')
            || c == '々'
            || (c >= 'ぁ' && c <= 'ん')
            || (c >= 'ァ' && c <= 'ヶ')
            || (c >= '！' && c <= '＠')
            || c == '、'
            || c == '。';
    }

    private static bool HasHangulDetectionRunIgnoringWhitespace(string text)
    {
        int runLength = 0;
        for (int i = 0; i < (text?.Length ?? 0); i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            if (c >= '\uAC00' && c <= '\uD7AF')
            {
                runLength++;
                if (runLength >= 2)
                {
                    return true;
                }
            }
            else
            {
                runLength = 0;
            }
        }
        return false;
    }

    private static bool HasInvalidJapaneseKanaSequence(string text)
    {
        for (int i = 0; i + 1 < (text?.Length ?? 0); i++)
        {
            char previous = text[i];
            char current = text[i + 1];
            if (IsHalfWidthKanaSymbol(previous) && IsHalfWidthKanaSymbol(current))
            {
                return true;
            }
            if (current == 'ﾟ' && !(previous >= 'ﾊ' && previous <= 'ﾎ'))
            {
                return true;
            }
            if (current == 'ﾞ' && !((previous >= 'ｶ' && previous <= 'ﾄ') || (previous >= 'ﾊ' && previous <= 'ﾎ')))
            {
                return true;
            }
            if (IsHalfWidthSmallYaYuYo(current)
                && previous != 'ｷ'
                && previous != 'ｼ'
                && previous != 'ﾁ'
                && previous != 'ﾆ'
                && previous != 'ﾋ'
                && previous != 'ﾐ'
                && previous != 'ﾘ'
                && previous != 'ﾞ'
                && previous != 'ﾟ')
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsHalfWidthKanaSymbol(char c)
    {
        return (c >= 'ｦ' && c <= 'ｯ') || c == 'ﾞ' || c == 'ﾟ';
    }

    private static bool IsHalfWidthSmallYaYuYo(char c)
    {
        return c == 'ｬ' || c == 'ｭ' || c == 'ｮ';
    }

}
