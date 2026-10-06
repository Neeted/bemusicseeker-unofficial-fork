using System.IO;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>リソース参照の用途です。任意画像は通常のBGAの分母と区別します。</summary>
internal enum ChartResourceUsage
{
    Normal,
    Stagefile,
    Backbmp,
    Banner,
    InputDiagnostic
}

/// <summary>抽出時に確定した原文、用途、照合値と実際の解析状態を保持する不変の参照です。</summary>
internal readonly struct ChartResourceReference
{
    /// <summary>解析済みの値を保持します。原文を照合用の拡張子別名へ置き換えません。</summary>
    internal ChartResourceReference(ChartResourceKind kind, ChartResourceUsage usage, string rawPath,
        string normalizedPath, string lookupKey, ChartResourcePathNormalizationStatus status)
    {
        Kind = kind;
        Usage = usage;
        RawPath = rawPath ?? string.Empty;
        NormalizedPath = normalizedPath ?? string.Empty;
        LookupKey = lookupKey ?? string.Empty;
        Status = status;
    }

    /// <summary>既存の解析規則で採用したリソース種別です。</summary>
    public ChartResourceKind Kind { get; }
    /// <summary>通常参照、任意画像の役割、入力診断を区別します。</summary>
    public ChartResourceUsage Usage { get; }
    /// <summary>譜面に記述された原文です。パスのない入力診断は空文字列です。</summary>
    public string RawPath { get; }
    /// <summary>区切りと拡張子別名を正規化した、拡張子付きの照合パスです。</summary>
    public string NormalizedPath { get; }
    /// <summary>抽出時に一回だけ拡張子を除去した検索キーです。</summary>
    public string LookupKey { get; }
    /// <summary>入口で実際に得たパス解析状態です。Unknownや空キーを非対応理由へ読み替えません。</summary>
    public ChartResourcePathNormalizationStatus Status { get; }

    /// <summary>入力参照を一回解析します。種類不明や空キーも原文と実際の状態を保持します。</summary>
    internal static ChartResourceReference Parse(string rawPath, ChartResourceKind kind,
        ChartResourceUsage usage = ChartResourceUsage.Normal)
    {
        ChartResourcePathNormalizationResult result = ChartResourcePathNormalizer.AnalyzeReferencePathForLookup(rawPath);
        string key = result.IsValid ? Path.ChangeExtension(result.NormalizedPath, null) : string.Empty;
        return new ChartResourceReference(
            kind == ChartResourceKind.Unknown ? ChartResourcePathNormalizer.ClassifyReferencePathExtension(rawPath) : kind,
            usage, rawPath, result.NormalizedPath, key, result.Status);
    }
}
