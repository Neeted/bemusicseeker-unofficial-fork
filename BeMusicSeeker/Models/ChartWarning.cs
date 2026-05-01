using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// WARNING 列に表示する警告の識別子です。
/// 文字列ではなく種類で warning を扱い、翻訳文や改行に依存した削除を段階的に減らすために使います。
/// </summary>
internal enum ChartWarningKind
{
    NestedChartFileInPackage,
    AlreadyInstalled,
    SingleBmsFile,
    SingleBmsonFile,
    ResourceWavMissing,
    ResourceBgaMissing,
    ResourceMovieMissing,
    ResourceStagefileMissing,
    ResourceBackbmpMissing,
    ResourceBannerMissing,
    InstallEstimationAmbiguous,
    InstallEstimationMetadataMismatch,
    InstallEstimationReinstallNotImproved,
    InstalledDestinationResolveFailed,
    InstallEstimationLowConfidence,
    DuplicateChart,
    ZeroNoteMismatch
}

/// <summary>
/// WARNING のまとまりです。
/// カテゴリ単位で warning を入れ替え、推定 warning だけを消すなどの操作を安全にするために使います。
/// </summary>
internal enum ChartWarningCategory
{
    PackageLayout,
    InstalledState,
    ResourceHealth,
    InstallEstimation,
    Duplicate,
    ChartContent
}

/// <summary>
/// 譜面行に付与される構造化 warning です。
/// 1 行表示用の digest と tooltip 詳細を同じ source から生成するために必要な表示属性を持ちます。
/// </summary>
internal sealed class ChartWarning
{
    /// <summary>
    /// 指定 kind の既定表示属性を使って warning を作成します。
    /// </summary>
    /// <param name="kind">warning の種類。</param>
    /// <param name="message">tooltip や互換詳細表示に出す本文。</param>
    /// <returns>構造化 warning。</returns>
    internal static ChartWarning Create(ChartWarningKind kind, string message)
    {
        ChartWarningDefinition definition = ChartWarningDefinition.ForKind(kind);
        return new ChartWarning(
            kind,
            definition.Category,
            definition.Priority,
            definition.DigestLabel,
            message,
            definition.HighlightRow,
            definition.ShowInDigest,
            definition.ShowInTooltip);
    }

    /// <summary>
    /// WARNING の identity です。
    /// </summary>
    internal ChartWarningKind Kind { get; }

    /// <summary>
    /// 一括削除に使う分類です。
    /// </summary>
    internal ChartWarningCategory Category { get; }

    /// <summary>
    /// digest 表示順を決める優先度です。小さいほど先に表示します。
    /// </summary>
    internal int Priority { get; }

    /// <summary>
    /// 一覧セルの digest に出す短いラベルです。
    /// </summary>
    internal string DigestLabel { get; }

    /// <summary>
    /// tooltip と互換表示に使う詳細本文です。
    /// </summary>
    internal string Message { get; }

    /// <summary>
    /// この warning が行全体の警告色対象かどうかです。
    /// </summary>
    internal bool HighlightRow { get; }

    /// <summary>
    /// この warning を digest の label 候補に含めるかどうかです。
    /// </summary>
    internal bool ShowInDigest { get; }

    /// <summary>
    /// この warning を tooltip 詳細に含めるかどうかです。
    /// </summary>
    internal bool ShowInTooltip { get; }

    private ChartWarning(ChartWarningKind kind, ChartWarningCategory category, int priority, string digestLabel, string message, bool highlightRow, bool showInDigest, bool showInTooltip)
    {
        Kind = kind;
        Category = category;
        Priority = priority;
        DigestLabel = digestLabel ?? string.Empty;
        Message = message ?? string.Empty;
        HighlightRow = highlightRow;
        ShowInDigest = showInDigest;
        ShowInTooltip = showInTooltip;
    }
}

/// <summary>
/// 構造化 warning の既定表示属性です。
/// 仮の priority と digest label を一箇所に集約し、後続調整を局所化します。
/// </summary>
internal sealed class ChartWarningDefinition
{
    private ChartWarningDefinition(ChartWarningCategory category, int priority, string digestLabel, bool highlightRow, bool showInDigest = true, bool showInTooltip = true)
    {
        Category = category;
        Priority = priority;
        DigestLabel = digestLabel ?? string.Empty;
        HighlightRow = highlightRow;
        ShowInDigest = showInDigest;
        ShowInTooltip = showInTooltip;
    }

    /// <summary>
    /// WARNING の分類です。
    /// </summary>
    internal ChartWarningCategory Category { get; }

    /// <summary>
    /// digest の表示順です。
    /// </summary>
    internal int Priority { get; }

    /// <summary>
    /// digest に表示する短い文言です。
    /// </summary>
    internal string DigestLabel { get; }

    /// <summary>
    /// 行全体を警告色にするかどうかです。
    /// </summary>
    internal bool HighlightRow { get; }

    /// <summary>
    /// digest に含めるかどうかです。
    /// </summary>
    internal bool ShowInDigest { get; }

    /// <summary>
    /// tooltip に含めるかどうかです。
    /// </summary>
    internal bool ShowInTooltip { get; }

    /// <summary>
    /// kind に対応する既定表示属性を返します。
    /// </summary>
    /// <param name="kind">warning の種類。</param>
    /// <returns>既定表示属性。</returns>
    internal static ChartWarningDefinition ForKind(ChartWarningKind kind)
    {
        switch (kind)
        {
            case ChartWarningKind.NestedChartFileInPackage:
                return new ChartWarningDefinition(ChartWarningCategory.PackageLayout, 10, Resources.WarningDigest_NestedChart, highlightRow: false);
            case ChartWarningKind.ZeroNoteMismatch:
                return new ChartWarningDefinition(ChartWarningCategory.ChartContent, 20, Resources.WarningDigest_ZeroNoteMismatch, highlightRow: true);
            case ChartWarningKind.DuplicateChart:
                return new ChartWarningDefinition(ChartWarningCategory.Duplicate, 30, Resources.WarningDigest_DuplicateChart, highlightRow: true);
            case ChartWarningKind.InstallEstimationAmbiguous:
                return new ChartWarningDefinition(ChartWarningCategory.InstallEstimation, 40, Resources.WarningDigest_InstallEstimationAmbiguous, highlightRow: true);
            case ChartWarningKind.InstallEstimationMetadataMismatch:
                return new ChartWarningDefinition(ChartWarningCategory.InstallEstimation, 41, Resources.WarningDigest_InstallEstimationMetadataMismatch, highlightRow: true);
            case ChartWarningKind.InstallEstimationReinstallNotImproved:
                return new ChartWarningDefinition(ChartWarningCategory.InstallEstimation, 42, Resources.WarningDigest_InstallEstimationReinstallNotImproved, highlightRow: true);
            case ChartWarningKind.InstalledDestinationResolveFailed:
                return new ChartWarningDefinition(ChartWarningCategory.InstallEstimation, 43, Resources.WarningDigest_InstalledDestinationResolveFailed, highlightRow: false);
            case ChartWarningKind.InstallEstimationLowConfidence:
                return new ChartWarningDefinition(ChartWarningCategory.InstallEstimation, 44, Resources.WarningDigest_InstallEstimationLowConfidence, highlightRow: true);
            case ChartWarningKind.AlreadyInstalled:
                return new ChartWarningDefinition(ChartWarningCategory.InstalledState, 50, Resources.WarningDigest_AlreadyInstalled, highlightRow: false);
            case ChartWarningKind.SingleBmsFile:
                return new ChartWarningDefinition(ChartWarningCategory.PackageLayout, 60, Resources.WarningDigest_SingleBmsFile, highlightRow: false);
            case ChartWarningKind.SingleBmsonFile:
                return new ChartWarningDefinition(ChartWarningCategory.PackageLayout, 60, Resources.WarningDigest_SingleBmsonFile, highlightRow: false);
            case ChartWarningKind.ResourceWavMissing:
            case ChartWarningKind.ResourceBgaMissing:
            case ChartWarningKind.ResourceMovieMissing:
                return new ChartWarningDefinition(ChartWarningCategory.ResourceHealth, 80, Resources.WarningDigest_ResourceMissing, highlightRow: false);
            case ChartWarningKind.ResourceStagefileMissing:
            case ChartWarningKind.ResourceBackbmpMissing:
            case ChartWarningKind.ResourceBannerMissing:
                return new ChartWarningDefinition(ChartWarningCategory.ResourceHealth, 83, Resources.WarningDigest_ImageMissing, highlightRow: false, showInDigest: false);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}

/// <summary>
/// BMSFile が保持する構造化 warning collection です。
/// 構造化 warning と互換フラグを統合して、表示用文字列と行色を計算します。
/// </summary>
internal sealed class ChartWarningCollection
{
    private readonly BMSFile owner;
    private readonly Dictionary<ChartWarningKind, ChartWarning> structuredWarnings = new Dictionary<ChartWarningKind, ChartWarning>();

    /// <summary>
    /// 指定した譜面行に紐付く collection を作成します。
    /// </summary>
    /// <param name="owner">変更通知を出す所有譜面行。</param>
    internal ChartWarningCollection(BMSFile owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>
    /// 構造化 warning を追加または置き換えます。
    /// </summary>
    /// <param name="warning">追加する warning。</param>
    internal void Set(ChartWarning warning)
    {
        if (warning == null)
        {
            return;
        }
        structuredWarnings[warning.Kind] = warning;
        owner.RaiseWarningPresentationChanged();
    }

    /// <summary>
    /// 指定 kind の構造化 warning を削除します。
    /// </summary>
    /// <param name="kind">削除する warning kind。</param>
    internal void Remove(ChartWarningKind kind)
    {
        if (structuredWarnings.Remove(kind))
        {
            owner.RaiseWarningPresentationChanged();
        }
    }

    /// <summary>
    /// 指定カテゴリの構造化 warning を削除します。
    /// </summary>
    /// <param name="category">削除するカテゴリ。</param>
    internal void RemoveCategory(ChartWarningCategory category)
    {
        bool removed = false;
        foreach (ChartWarningKind kind in structuredWarnings.Where(pair => pair.Value.Category == category).Select(pair => pair.Key).ToList())
        {
            structuredWarnings.Remove(kind);
            removed = true;
        }
        if (removed)
        {
            owner.RaiseWarningPresentationChanged();
        }
    }

    /// <summary>
    /// 指定カテゴリの構造化 warning をまとめて入れ替えます。
    /// </summary>
    /// <param name="category">入れ替えるカテゴリ。</param>
    /// <param name="warnings">新しい warning 一覧。</param>
    internal void ReplaceCategory(ChartWarningCategory category, IEnumerable<ChartWarning> warnings)
    {
        bool changed = false;
        foreach (ChartWarningKind kind in structuredWarnings.Where(pair => pair.Value.Category == category).Select(pair => pair.Key).ToList())
        {
            structuredWarnings.Remove(kind);
            changed = true;
        }
        foreach (ChartWarning warning in warnings ?? Enumerable.Empty<ChartWarning>())
        {
            if (warning == null || warning.Category != category)
            {
                continue;
            }
            structuredWarnings[warning.Kind] = warning;
            changed = true;
        }
        if (changed)
        {
            owner.RaiseWarningPresentationChanged();
        }
    }

    /// <summary>
    /// 構造化 warning の内容をコピーします。
    /// </summary>
    /// <param name="warnings">コピー元 warning。</param>
    internal void ReplaceAll(IEnumerable<ChartWarning> warnings)
    {
        structuredWarnings.Clear();
        foreach (ChartWarning warning in warnings ?? Enumerable.Empty<ChartWarning>())
        {
            if (warning != null)
            {
                structuredWarnings[warning.Kind] = warning;
            }
        }
        owner.RaiseWarningPresentationChanged();
    }

    /// <summary>
    /// すべての構造化 warning を削除します。
    /// pending warning の完全再初期化など、種類を問わず作り直す場面で使います。
    /// </summary>
    internal void Clear()
    {
        if (structuredWarnings.Count == 0)
        {
            return;
        }
        structuredWarnings.Clear();
        owner.RaiseWarningPresentationChanged();
    }

    /// <summary>
    /// 指定 kind の構造化 warning が存在するかどうかを返します。
    /// </summary>
    /// <param name="kind">確認する kind。</param>
    /// <returns>存在する場合は true。</returns>
    internal bool Contains(ChartWarningKind kind)
    {
        return structuredWarnings.ContainsKey(kind);
    }

    /// <summary>
    /// 現在保持している構造化 warning を列挙します。
    /// </summary>
    /// <returns>構造化 warning の snapshot。</returns>
    internal IReadOnlyList<ChartWarning> ToStructuredList()
    {
        return structuredWarnings.Values.ToList();
    }

    /// <summary>
    /// WARNING 列の 1 行目に表示する digest を作成します。
    /// </summary>
    /// <returns>digest 表示文字列。</returns>
    internal string BuildDigestText()
    {
        List<ChartWarning> warnings = EnumerateEffectiveWarnings().ToList();
        if (warnings.Count == 0)
        {
            return string.Empty;
        }

        string[] labels = warnings
            .Where(warning => warning.ShowInDigest && ShouldShowInDigest(warning))
            .OrderBy(warning => warning.Priority)
            .Select(warning => warning.DigestLabel)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (labels.Length == 0)
        {
            return string.Empty;
        }
        return "[" + warnings.Select(warning => warning.Kind).Distinct().Count() + "] " + string.Join(", ", labels);
    }

    /// <summary>
    /// tooltip に表示する詳細本文を作成します。
    /// </summary>
    /// <returns>tooltip 表示文字列。</returns>
    internal string BuildTooltipText()
    {
        return string.Join(
            Environment.NewLine,
            EnumerateEffectiveWarnings()
                .Where(warning => warning.ShowInTooltip)
                .OrderBy(warning => warning.Priority)
                .Select(warning => warning.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// 互換表示用の WARNING 全文を作成します。
    /// </summary>
    /// <returns>詳細表示文字列。</returns>
    internal string BuildDisplayText()
    {
        return BuildTooltipText();
    }

    /// <summary>
    /// いずれかの warning が行ハイライトを要求しているかどうかです。
    /// </summary>
    internal bool HasHighlightedWarning => EnumerateEffectiveWarnings().Any(warning => warning.HighlightRow);

    private bool ShouldShowInDigest(ChartWarning warning)
    {
        if (warning.Category == ChartWarningCategory.ResourceHealth)
        {
            return string.IsNullOrWhiteSpace(owner.instl_dst);
        }
        return true;
    }

    private IEnumerable<ChartWarning> EnumerateEffectiveWarnings()
    {
        Dictionary<ChartWarningKind, ChartWarning> effective = new Dictionary<ChartWarningKind, ChartWarning>();
        foreach (ChartWarning warning in structuredWarnings.Values)
        {
            effective[warning.Kind] = warning;
        }

        if (owner.HasZeroNoteMismatchWarning && !effective.ContainsKey(ChartWarningKind.ZeroNoteMismatch))
        {
            effective[ChartWarningKind.ZeroNoteMismatch] = ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);
        }
        if (owner.IsHashDuplicated && !effective.ContainsKey(ChartWarningKind.DuplicateChart))
        {
            effective[ChartWarningKind.DuplicateChart] = ChartWarning.Create(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
        }
        if (owner.HasLowConfidenceInstallWarning && !effective.Values.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation))
        {
            effective[ChartWarningKind.InstallEstimationLowConfidence] = ChartWarning.Create(ChartWarningKind.InstallEstimationLowConfidence, Resources.WarningDigest_InstallEstimationLowConfidence);
        }

        return effective.Values;
    }
}
