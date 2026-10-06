#nullable enable
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ChartParseAudit;

/// <summary>計測外のJSONL保存に共通のcamelCase形式を使います。</summary>
public static class Wire
{
    /// <summary>現行の簡易比較形式の版です。</summary>
    public const int Schema = 2;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    /// <summary>一件の記録を保存用JSONへ変換します。</summary>
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
}

/// <summary>担当行で一度だけ読込・判別・復号した不変入力です。全試行の旧新解析で共有します。</summary>
public sealed record PreparedInput(string Path, string Source, Encoding Encoding, string Md5, long Bytes);
/// <summary>担当行の実読込み識別です。</summary>
public sealed record InputIdentity(string Md5, long Bytes, string Encoding);
/// <summary>元RandomPatternリストの一項目です。解析後にだけ列挙します。</summary>
public readonly record struct Choice(int Range, int Value, bool Used);
/// <summary>入力行に属する試行・反復と旧新の先行順です。</summary>
public sealed record AuditCase(string Id, long RowId, string OriginalPath, string? Path, int Sample, int Repeat, int Seed, int[]? Choices, string? SkipStatus, string[] Order);
/// <summary>解析呼出しだけの時間・割当と既知のDurationを保存します。未着手の値はnullです。</summary>
public sealed record Execution(string CaseId, string Engine, string Status, InputIdentity? Input, long? DurationTicks, double? ParseMilliseconds, long? AllocatedBytes, string? Error);
/// <summary>raw解析結果の寿命を一対の比較までに限定します。RowsとRandomPatternは元結果から遅延列挙します。</summary>
public sealed record ParseOutcome(Execution Record, IEnumerable<PositionRow>? Rows = null, IEnumerable<Choice>? RandomPattern = null);

/// <summary>旧Fractionの演算へ依存しない既約整数比と特殊値です。Tagは有限0、負∞-1、正∞1、NaN2、未定義3です。</summary>
public readonly record struct Number(int Tag, BigInteger Numerator, BigInteger Denominator) : IComparable<Number>
{
    /// <summary>有限値の符号と整数比を正規化し、分母0は明示タグにします。</summary>
    public static Number Ratio(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero) return new(numerator.Sign == 0 ? 2 : numerator.Sign, 0, 0);
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        BigInteger gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return new(0, numerator / gcd, denominator / gcd);
    }
    /// <summary>未定義を有限ゼロと区別します。</summary>
    public static Number Undefined => new(3, 0, 0);
    /// <summary>数値位置の整列を任意精度整数で行い、特殊タグの同一性も保持します。</summary>
    public int CompareTo(Number other) => Tag == other.Tag ? Tag == 0 ? (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator) : 0 : Tag.CompareTo(other.Tag);
    /// <summary>保存する差例と最終場所だけを文字列化します。</summary>
    public override string ToString() => Tag switch { 0 => $"rational:{Numerator.ToString(CultureInfo.InvariantCulture)}/{Denominator.ToString(CultureInfo.InvariantCulture)}", -1 => "negative-infinity", 1 => "positive-infinity", 2 => "nan", _ => "undefined" };
}

/// <summary>元Valueの型を保つ小さな値です。未対応型は射影側で失敗させます。</summary>
public readonly record struct NoteValue(string Kind, Number Number = default, long Integer = 0, double Real = 0)
{
    /// <summary>未定義以外の既知数値型を数値差として分類します。</summary>
    public bool IsNumeric => Kind != "undefined" && (Kind != "rational" || Number.Tag != 3);
    /// <summary>保存する差例だけをInvariantCultureで文字列化します。</summary>
    public override string ToString() => Kind switch { "rational" => Number.ToString(), "double" => "double:" + Real.ToString("R", CultureInfo.InvariantCulture), "int" or "ticks" => Kind + ":" + Integer.ToString(CultureInfo.InvariantCulture), _ => Kind };
}
/// <summary>時間に依存しない位置・種類・Indexと、連続同キーの出現番号です。</summary>
public readonly record struct PositionKey(int Measure, int Category, Number Position, uint Kind, int Index, int Occurrence) : IComparable<PositionKey>
{
    /// <summary>初期BPM、小節、元ノーツの位置順に整数キーを突合します。</summary>
    public int CompareTo(PositionKey other)
    {
        int result = Measure.CompareTo(other.Measure);
        if (result == 0) result = Category.CompareTo(other.Category);
        if (result == 0) result = Position.CompareTo(other.Position);
        if (result == 0) result = Kind.CompareTo(other.Kind);
        if (result == 0) result = Index.CompareTo(other.Index);
        if (result == 0) result = Occurrence.CompareTo(other.Occurrence);
        return result;
    }
    /// <summary>差の場所を必要になった時点でだけ保存形式へ変換します。</summary>
    public override string ToString() => Category == 0 ? "bpm" : Category == 1 ? FormattableString.Invariant($"measure:{Measure}") : FormattableString.Invariant($"measure:{Measure}/{Position}/kind:{Kind}/index:{Index}/occurrence:{Occurrence}");
}
/// <summary>raw結果から一件ずつ取得する位置・Value・整数tickです。</summary>
public readonly record struct PositionRow(PositionKey Key, string Kind, long? Tick, NoteValue Value);
