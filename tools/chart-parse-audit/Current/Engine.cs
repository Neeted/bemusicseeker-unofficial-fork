#nullable enable
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models.Utils;
using Ribbit.BMS;
using Ribbit.Math;

namespace ChartParseAudit;

/// <summary>通常decoderと準備済み本番入口へ静的に接続します。</summary>
internal static class Engine
{
    /// <summary>本番と同じLongPathFileSystem・FileShare.ReadWriteで一度だけ読み、同じdecoderで復号します。不在・空・拒否は準備失敗です。</summary>
    public static PreparedInput Read(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (!LongPathFileSystem.FileExists(path)) throw new FileNotFoundException("File does not exist", path);
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        string md5 = Convert.ToHexStringLower(MD5.HashData(data));
        string source = BMSFile.DecodeForAudit(data, out Encoding encoding);
        if (data.Length == 0) throw new InvalidDataException(path + " is empty file.");
        return new(path, source, encoding, md5, data.LongLength);
    }
    /// <summary>本番optionsを計時前に準備します。返す関数は準備済み本文の同期解析だけを呼びます。</summary>
    public static Func<BMSFile> PrepareParse(PreparedInput input, Queue<int>? choices, Random random)
    {
        var options = new BmsParseOptions { RandomSource = random };
        return () => BMSFile.ParsePreparedForAudit(input.Path, input.Source, input.Encoding, input.Md5, options, choices);
    }
    /// <summary>本番の厳密整数比を比較専用値へ正規化します。</summary>
    public static Number Scalar(Fraction value) => Number.Ratio(value.Numerator, value.Denominator);
    /// <summary>有限比と明示正∞を区別します。</summary>
    public static Number Scalar(BmsNumber value) => value.FiniteValue is Fraction fraction ? Scalar(fraction) : new(1, 0, 0);
    /// <summary>未定義のBPMも区別します。</summary>
    public static Number Scalar(BmsNumber? value) => value.HasValue ? Scalar(value.Value) : Number.Undefined;
}
