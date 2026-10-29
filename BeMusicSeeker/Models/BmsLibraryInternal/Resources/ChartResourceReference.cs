using System.IO;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>リソース参照の用途です。任意画像は通常のBGAの分母と区別します。</summary>
internal enum ChartResourceUsage
{
    Normal,
    Stagefile,
    Backbmp,
    Banner
}

/// <summary>要求数へ含めるか、実在照合できるかを定義の構文・用途・原文から区別します。</summary>
internal enum ChartResourceClassification
{
    Excluded,
    Matchable,
    Unmatchable
}

/// <summary>抽出時に確定した原文、用途、照合値と実際の解析状態を保持する不変の参照です。</summary>
internal readonly struct ChartResourceReference
{
    /// <summary>解析済みの値を保持します。原文を照合用の拡張子別名へ置き換えません。</summary>
    internal ChartResourceReference(ChartResourceKind kind, ChartResourceUsage usage, string rawPath,
        string normalizedPath, string lookupKey, ChartResourcePathNormalizationStatus status, bool isUsed = true)
    {
        Kind = kind;
        Usage = usage;
        RawPath = rawPath ?? string.Empty;
        NormalizedPath = normalizedPath ?? string.Empty;
        LookupKey = lookupKey ?? string.Empty;
        Status = status;
        IsUsed = isUsed;
    }

    /// <summary>既存の解析規則で採用したリソース種別です。</summary>
    public ChartResourceKind Kind { get; }
    /// <summary>通常参照と任意画像の役割を区別します。</summary>
    public ChartResourceUsage Usage { get; }
    /// <summary>実採用本文に記述された原文です。</summary>
    public string RawPath { get; }
    /// <summary>区切りと拡張子別名を正規化した、拡張子付きの照合パスです。</summary>
    public string NormalizedPath { get; }
    /// <summary>抽出時に一回だけ拡張子を除去した検索キーです。</summary>
    public string LookupKey { get; }
    /// <summary>入口で実際に得たパス解析状態です。Unknownや空キーを非対応理由へ読み替えません。</summary>
    public ChartResourcePathNormalizationStatus Status { get; }

    /// <summary>名前なし・種別不明・不正構文は要求対象外とし、名前付きの不正パスは個別の未充足とします。</summary>
    public ChartResourceClassification Classification => Status is ChartResourcePathNormalizationStatus.Empty
        or ChartResourcePathNormalizationStatus.MissingFileName or ChartResourcePathNormalizationStatus.UnknownKind
        or ChartResourcePathNormalizationStatus.MalformedSyntax
            ? ChartResourceClassification.Excluded
            : Status == ChartResourcePathNormalizationStatus.Valid && !string.IsNullOrEmpty(LookupKey)
                ? ChartResourceClassification.Matchable : ChartResourceClassification.Unmatchable;

    /// <summary>実際の使用参照かを表します。全定義は発見とLR2診断の短い取得寿命だけ共有します。</summary>
    public bool IsUsed { get; }

    /// <summary>評価へ採用する名前付き親参照だけを、自動確定・自動導入の制限理由とします。LR2診断や対象外・未使用は含めません。</summary>
    internal bool RestrictsAutomaticDestination => IsUsed && Classification == ChartResourceClassification.Unmatchable
        && Status == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported;

    /// <summary>使用状態だけを確定し、一回解析したパスと原文を共有します。</summary>
    internal ChartResourceReference WithUsage(bool isUsed) => new(Kind, Usage, RawPath, NormalizedPath, LookupKey, Status, isUsed);

    /// <summary>入力参照を一回解析します。種類不明や空キーも原文と実際の状態を保持します。</summary>
    internal static ChartResourceReference Parse(string rawPath, ChartResourceKind kind,
        ChartResourceUsage usage = ChartResourceUsage.Normal)
    {
        ChartResourceKind resolvedKind = kind == ChartResourceKind.Unknown
            ? ChartResourcePathNormalizer.ClassifyReferencePathExtension(rawPath) : kind;
        ChartResourcePathNormalizationStatus? excluded = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            excluded = ChartResourcePathNormalizationStatus.Empty;
        }
        else
        {
            // 正規化で末尾要素や単独の「.」が消える前に、原文のファイル名本体を判定します。
            string name = rawPath.Trim().Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            int dot = name.LastIndexOf('.');
            if (name is "" or "." or ".." || dot == 0)
            {
                excluded = ChartResourcePathNormalizationStatus.MissingFileName;
            }
            else if (resolvedKind == ChartResourceKind.Unknown || (kind == ChartResourceKind.Unknown && resolvedKind == ChartResourceKind.Audio))
            {
                excluded = ChartResourcePathNormalizationStatus.UnknownKind;
            }
        }
        if (excluded.HasValue)
        {
            return new ChartResourceReference(resolvedKind, usage, rawPath, string.Empty, string.Empty, excluded.Value);
        }
        ChartResourcePathNormalizationResult result = ChartResourcePathNormalizer.AnalyzeReferencePathForLookup(rawPath);
        string key = result.IsValid ? Path.ChangeExtension(result.NormalizedPath, null) : string.Empty;
        return new ChartResourceReference(
            resolvedKind,
            usage, rawPath, result.NormalizedPath, key, result.Status);
    }
}
