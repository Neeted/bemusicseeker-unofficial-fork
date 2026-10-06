#nullable enable
using Ribbit.BMS;
using Ribbit.Math;

namespace ChartParseAudit;

/// <summary>凍結解析の本文入口へ静的に接続します。</summary>
internal static class Engine
{
    /// <summary>呼出し専用Queueと乱数源を計時前に閉包へ渡します。返す関数は凍結された同期解析だけを呼びます。</summary>
    public static Func<BMSFile> PrepareParse(PreparedInput input, Queue<int>? choices, Random random) => () => BMSFile.ParsePreparedForAudit(input.Path, input.Source, input.Encoding, input.Md5, choices, random);
    /// <summary>旧Fractionの演算・比較・hashを使わず、整数成分から比較値を正規化します。</summary>
    public static Number Scalar(Fraction value) => Number.Ratio(value.Numerator, value.Denominator);
    /// <summary>未定義と有限ゼロを区別します。</summary>
    public static Number Scalar(Fraction? value) => value.HasValue ? Scalar(value.Value) : Number.Undefined;
}
