using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>
/// WARNING 列に表示する警告の識別子です。
/// 文字列ではなく種類で warning を扱い、翻訳文や改行に依存した削除を段階的に減らすために使います。
/// </summary>
internal enum ChartWarningKind
{
    NestedChartFileInPackage,
    ResourceBearingNestedCharts,
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
    InstallEstimationAudioMissing,
    InstallEstimationUnrelated,
    InstallEstimationMetadataUnavailable,
    InstallEstimationAudioUnverifiable,
    InstallEstimationReinstallNotImproved,
    InstalledDestinationAmbiguous,
    InstalledDestinationAutoAppliedAmbiguous,
    UnsupportedResourcePath,
    InstalledDestinationResolveFailed,
    SourceSurfaceScanLimitExceeded,
    InstallEstimationLowConfidence,
    DuplicateChart,
    ZeroNoteMismatch,
    ChartInfoParseFailure,
    Lr2PathEncodingUnsupported,
    Lr2PathTooLong,
    Lr2ResourcePathUnsupported,
    Lr2ResourcePathTooLong
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
    ChartContent,
    ChartMetadata,
    Lr2Compatibility
}

/// <summary>
/// 警告が保持する事実の形です。
/// 種別ごとに必要な引数を固定し、形の合わない引数でファクトリが警告を作らないようにするために使います。
/// </summary>
internal enum ChartWarningShape
{
    /// <summary>事実を持たない種別です。</summary>
    None,

    /// <summary>パスを1つ持つ種別です。</summary>
    Path,

    /// <summary>パスを2つ持つ種別です。</summary>
    PathPair,

    /// <summary>導入先の候補パスの一覧を持つ種別です。</summary>
    Candidates,

    /// <summary>件数を持つ種別です。</summary>
    Count,

    /// <summary>資源健康度の割合と、存在数・定義数を持つ種別です。</summary>
    ResourceHealth,

    /// <summary>解析失敗の診断原文を持つ種別です。</summary>
    ParseFailure
}

/// <summary>
/// 譜面行に付与される構造化 warning です。
/// 翻訳済みの文字列ではなく、種別と生成時の事実（パス・候補・件数・資源健康度・診断原文）だけを保持します。
/// 短文と詳細は ViewModels の整形処理が読取り時に現在の表示言語で作るため、言語を変えても警告の同一性は変わりません。
/// </summary>
internal sealed class ChartWarning : IEquatable<ChartWarning>
{
    /// <summary>候補一覧の事実として保持する最大件数です。</summary>
    internal const int MaxCandidateCount = 3;

    /// <summary>候補一覧の事実として保持する最小件数です。</summary>
    internal const int MinCandidateCount = 2;

    // 事実を持たない種別は値が変わらないため、全件走査で大量に作られる場合に備えて共有します。
    private static readonly ChartWarning[] sharedFactless = new ChartWarning[ChartWarningDefinition.KindCount];

    private readonly ChartWarningDefinition definition;

    private ChartWarning(
        ChartWarningKind kind,
        ChartWarningDefinition definition,
        string path = "",
        string secondaryPath = "",
        ImmutableArray<string> candidates = default,
        int count = 0,
        int health = 0,
        int? existingCount = null,
        int? definedCount = null,
        string exceptionType = "",
        string failureKind = "",
        string diagnosticMessage = "")
    {
        Kind = kind;
        this.definition = definition;
        Path = path;
        SecondaryPath = secondaryPath;
        Candidates = candidates.IsDefault ? [] : candidates;
        Count = count;
        Health = health;
        ExistingCount = existingCount;
        DefinedCount = definedCount;
        ExceptionType = exceptionType;
        FailureKind = failureKind;
        DiagnosticMessage = diagnosticMessage;
    }

    /// <summary>
    /// 事実を持たない種別の警告を返します。同じ種別では同じインスタンスを共有します。
    /// </summary>
    /// <param name="kind">事実を持たない種別。</param>
    /// <returns>構造化 warning。</returns>
    /// <exception cref="ArgumentException">種別が事実を持つ形のときです。</exception>
    internal static ChartWarning Create(ChartWarningKind kind)
    {
        ChartWarningDefinition definition = RequireShape(kind, ChartWarningShape.None);
        return sharedFactless[(int)kind] ??= new ChartWarning(kind, definition);
    }

    /// <summary>
    /// パスを1つ持つ種別の警告を作ります。
    /// </summary>
    /// <param name="kind">パスを1つ持つ種別。</param>
    /// <param name="path">対象の候補パス。null は空文字として扱います。</param>
    /// <returns>構造化 warning。</returns>
    /// <exception cref="ArgumentException">種別がパスを1つ持つ形でないときです。</exception>
    internal static ChartWarning CreateWithPath(ChartWarningKind kind, string path)
    {
        ChartWarningDefinition definition = RequireShape(kind, ChartWarningShape.Path);
        return new ChartWarning(kind, definition, path: path ?? string.Empty);
    }

    /// <summary>
    /// 選択した候補と、競合する候補の2つのパスを持つ警告を作ります。
    /// </summary>
    /// <param name="kind">パスを2つ持つ種別。</param>
    /// <param name="path">選択した候補パス。null は空文字として扱います。</param>
    /// <param name="secondaryPath">競合する候補パス。null は空文字として扱います。</param>
    /// <returns>構造化 warning。</returns>
    /// <exception cref="ArgumentException">種別がパスを2つ持つ形でないときです。</exception>
    internal static ChartWarning CreateWithPathPair(ChartWarningKind kind, string path, string secondaryPath)
    {
        ChartWarningDefinition definition = RequireShape(kind, ChartWarningShape.PathPair);
        return new ChartWarning(kind, definition, path: path ?? string.Empty, secondaryPath: secondaryPath ?? string.Empty);
    }

    /// <summary>
    /// 導入先の候補パスの一覧を持つ警告を作ります。
    /// </summary>
    /// <param name="kind">候補一覧を持つ種別。</param>
    /// <param name="candidates">候補パス。2件以上3件以下で、null の項目を含めません。</param>
    /// <returns>構造化 warning。</returns>
    /// <exception cref="ArgumentException">種別が候補一覧を持つ形でない、または候補の件数・項目が不正なときです。</exception>
    internal static ChartWarning CreateWithCandidates(ChartWarningKind kind, IEnumerable<string> candidates)
    {
        ChartWarningDefinition definition = RequireShape(kind, ChartWarningShape.Candidates);
        ArgumentNullException.ThrowIfNull(candidates);
        ImmutableArray<string> values = [.. candidates];
        if (values.Length < MinCandidateCount || values.Length > MaxCandidateCount || values.Any(value => value == null))
        {
            throw new ArgumentException("候補は null を含まない2件以上3件以下で指定してください。", nameof(candidates));
        }
        return new ChartWarning(kind, definition, candidates: values);
    }

    /// <summary>
    /// 導入元の走査上限を超えたことを示す警告を作ります。
    /// </summary>
    /// <param name="maxVisitedCount">走査を打ち切った上限件数。</param>
    /// <returns>構造化 warning。</returns>
    internal static ChartWarning CreateSourceSurfaceScanLimitExceeded(int maxVisitedCount)
    {
        ChartWarningDefinition definition = RequireShape(ChartWarningKind.SourceSurfaceScanLimitExceeded, ChartWarningShape.Count);
        return new ChartWarning(ChartWarningKind.SourceSurfaceScanLimitExceeded, definition, count: maxVisitedCount);
    }

    /// <summary>
    /// 資源の健康度（割合・存在数・定義数）を持つ警告を作ります。
    /// </summary>
    /// <param name="kind">資源健康度の種別。</param>
    /// <param name="health">健康度の割合。</param>
    /// <param name="existingCount">存在する資源の数。</param>
    /// <param name="definedCount">譜面が定義する資源の数。</param>
    /// <returns>構造化 warning。</returns>
    /// <exception cref="ArgumentException">種別が資源健康度の形でないときです。</exception>
    internal static ChartWarning CreateResourceHealth(ChartWarningKind kind, int health, int? existingCount, int? definedCount)
    {
        ChartWarningDefinition definition = RequireShape(kind, ChartWarningShape.ResourceHealth);
        return new ChartWarning(kind, definition, health: health, existingCount: existingCount, definedCount: definedCount);
    }

    /// <summary>
    /// 譜面情報の解析失敗の診断原文を持つ警告を作ります。
    /// 診断は記録された原文のまま保持し、欠落時の代替語は表示時に現在の言語で解決します。
    /// </summary>
    /// <param name="exceptionType">記録された例外型名。null は空文字として扱います。</param>
    /// <param name="failureKind">記録された失敗分類。null は空文字として扱います。</param>
    /// <param name="message">記録された本文。null は空文字として扱います。</param>
    /// <returns>構造化 warning。</returns>
    internal static ChartWarning CreateChartInfoParseFailure(string exceptionType, string failureKind, string message)
    {
        ChartWarningDefinition definition = RequireShape(ChartWarningKind.ChartInfoParseFailure, ChartWarningShape.ParseFailure);
        return new ChartWarning(
            ChartWarningKind.ChartInfoParseFailure,
            definition,
            exceptionType: exceptionType ?? string.Empty,
            failureKind: failureKind ?? string.Empty,
            diagnosticMessage: message ?? string.Empty);
    }

    /// <summary>
    /// WARNING の identity です。
    /// </summary>
    internal ChartWarningKind Kind { get; }

    /// <summary>
    /// 一括削除に使う分類です。
    /// </summary>
    internal ChartWarningCategory Category => definition.Category;

    /// <summary>
    /// digest 表示順を決める優先度です。小さいほど先に表示します。
    /// </summary>
    internal int Priority => definition.Priority;

    /// <summary>
    /// この warning が行全体の警告色対象かどうかです。
    /// </summary>
    internal bool HighlightRow => definition.HighlightRow;

    /// <summary>
    /// この warning を digest の label 候補に含めるかどうかです。
    /// </summary>
    internal bool ShowInDigest => definition.ShowInDigest;

    /// <summary>
    /// この warning を tooltip 詳細に含めるかどうかです。
    /// </summary>
    internal bool ShowInTooltip => definition.ShowInTooltip;

    /// <summary>
    /// 種別が持つ事実の形です。
    /// </summary>
    internal ChartWarningShape Shape => definition.Shape;

    /// <summary>
    /// パスを持つ種別の（選択した）パスです。持たない種別では空文字です。
    /// </summary>
    internal string Path { get; }

    /// <summary>
    /// パスを2つ持つ種別の、競合する候補パスです。持たない種別では空文字です。
    /// </summary>
    internal string SecondaryPath { get; }

    /// <summary>
    /// 候補一覧を持つ種別の候補パスです（2件以上3件以下）。持たない種別では空です。
    /// </summary>
    internal ImmutableArray<string> Candidates { get; }

    /// <summary>
    /// 件数を持つ種別の件数です。持たない種別では 0 です。
    /// </summary>
    internal int Count { get; }

    /// <summary>
    /// 資源健康度の種別の健康度（割合）です。持たない種別では 0 です。
    /// </summary>
    internal int Health { get; }

    /// <summary>
    /// 資源健康度の種別の存在数です。
    /// </summary>
    internal int? ExistingCount { get; }

    /// <summary>
    /// 資源健康度の種別の定義数です。
    /// </summary>
    internal int? DefinedCount { get; }

    /// <summary>
    /// 解析失敗の種別で記録された例外型名の原文です。
    /// </summary>
    internal string ExceptionType { get; }

    /// <summary>
    /// 解析失敗の種別で記録された失敗分類の原文です。
    /// </summary>
    internal string FailureKind { get; }

    /// <summary>
    /// 解析失敗の種別で記録された本文の原文です。
    /// </summary>
    internal string DiagnosticMessage { get; }

    /// <summary>
    /// 種別と事実が同じ警告かどうかを返します。表示言語には依存しません。
    /// </summary>
    /// <param name="other">比較先。</param>
    /// <returns>種別と事実が等しい場合は true。</returns>
    public bool Equals(ChartWarning other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return other != null
            && Kind == other.Kind
            && Count == other.Count
            && Health == other.Health
            && ExistingCount == other.ExistingCount
            && DefinedCount == other.DefinedCount
            && string.Equals(Path, other.Path, StringComparison.Ordinal)
            && string.Equals(SecondaryPath, other.SecondaryPath, StringComparison.Ordinal)
            && string.Equals(ExceptionType, other.ExceptionType, StringComparison.Ordinal)
            && string.Equals(FailureKind, other.FailureKind, StringComparison.Ordinal)
            && string.Equals(DiagnosticMessage, other.DiagnosticMessage, StringComparison.Ordinal)
            && Candidates.AsSpan().SequenceEqual(other.Candidates.AsSpan());
    }

    /// <inheritdoc />
    public override bool Equals(object obj) => Equals(obj as ChartWarning);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Path, StringComparer.Ordinal);
        hash.Add(SecondaryPath, StringComparer.Ordinal);
        hash.Add(Count);
        hash.Add(Health);
        hash.Add(ExistingCount);
        hash.Add(DefinedCount);
        hash.Add(ExceptionType, StringComparer.Ordinal);
        hash.Add(FailureKind, StringComparer.Ordinal);
        hash.Add(DiagnosticMessage, StringComparer.Ordinal);
        foreach (string candidate in Candidates)
        {
            hash.Add(candidate, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    private static ChartWarningDefinition RequireShape(ChartWarningKind kind, ChartWarningShape expected)
    {
        var definition = ChartWarningDefinition.ForKind(kind);
        if (definition.Shape != expected)
        {
            throw new ArgumentException($"警告の種別 {kind} は {expected} の形では作成できません。", nameof(kind));
        }
        return definition;
    }
}

/// <summary>
/// 構造化 warning の既定属性です。
/// 分類・優先度・強調・表示対象・事実の形を種別ごとに一箇所へ集約します。
/// 表示文言は持たず、現在の表示言語での文言は ViewModels の整形処理が選びます。
/// </summary>
internal sealed class ChartWarningDefinition
{
    private static readonly ChartWarningDefinition[] definitions = BuildDefinitions();

    private ChartWarningDefinition(ChartWarningCategory category, int priority, ChartWarningShape shape, bool highlightRow, bool showInDigest = true)
    {
        Category = category;
        Priority = priority;
        Shape = shape;
        HighlightRow = highlightRow;
        ShowInDigest = showInDigest;
    }

    /// <summary>
    /// 種別の総数です。種別は 0 から連続した値です。
    /// </summary>
    internal static int KindCount => definitions.Length;

    /// <summary>
    /// WARNING の分類です。
    /// </summary>
    internal ChartWarningCategory Category { get; }

    /// <summary>
    /// digest の表示順です。
    /// </summary>
    internal int Priority { get; }

    /// <summary>
    /// 種別が持つ事実の形です。
    /// </summary>
    internal ChartWarningShape Shape { get; }

    /// <summary>
    /// 行全体を警告色にするかどうかです。
    /// </summary>
    internal bool HighlightRow { get; }

    /// <summary>
    /// digest に含めるかどうかです。
    /// </summary>
    internal bool ShowInDigest { get; }

    /// <summary>
    /// tooltip に含めるかどうかです。すべての種別で含めます。
    /// </summary>
    internal bool ShowInTooltip => true;

    /// <summary>
    /// kind に対応する既定属性を返します。
    /// </summary>
    /// <param name="kind">warning の種類。</param>
    /// <returns>既定属性。</returns>
    internal static ChartWarningDefinition ForKind(ChartWarningKind kind)
    {
        int index = (int)kind;
        if ((uint)index >= (uint)definitions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
        return definitions[index];
    }

    private static ChartWarningDefinition[] BuildDefinitions()
    {
        ChartWarningKind[] kinds = Enum.GetValues<ChartWarningKind>();
        var table = new ChartWarningDefinition[kinds.Length];
        foreach (ChartWarningKind kind in kinds)
        {
            table[(int)kind] = Create(kind);
        }
        return table;
    }

    private static ChartWarningDefinition Create(ChartWarningKind kind)
    {
        return kind switch
        {
            ChartWarningKind.NestedChartFileInPackage => new(ChartWarningCategory.PackageLayout, 10, ChartWarningShape.None, highlightRow: false),
            ChartWarningKind.ResourceBearingNestedCharts => new(ChartWarningCategory.PackageLayout, 9, ChartWarningShape.None, highlightRow: true),
            ChartWarningKind.ZeroNoteMismatch => new(ChartWarningCategory.ChartContent, 20, ChartWarningShape.None, highlightRow: true),
            ChartWarningKind.ChartInfoParseFailure => new(ChartWarningCategory.ChartMetadata, 15, ChartWarningShape.ParseFailure, highlightRow: true),
            ChartWarningKind.Lr2PathEncodingUnsupported => new(ChartWarningCategory.Lr2Compatibility, 18, ChartWarningShape.None, highlightRow: true),
            ChartWarningKind.Lr2PathTooLong => new(ChartWarningCategory.Lr2Compatibility, 19, ChartWarningShape.None, highlightRow: true),
            ChartWarningKind.Lr2ResourcePathUnsupported => new(ChartWarningCategory.Lr2Compatibility, 21, ChartWarningShape.None, highlightRow: true),
            ChartWarningKind.Lr2ResourcePathTooLong => new(ChartWarningCategory.Lr2Compatibility, 22, ChartWarningShape.None, highlightRow: true),
            ChartWarningKind.DuplicateChart => new(ChartWarningCategory.Duplicate, 30, ChartWarningShape.None, highlightRow: true),
            ChartWarningKind.InstallEstimationAmbiguous => new(ChartWarningCategory.InstallEstimation, 40, ChartWarningShape.PathPair, highlightRow: true),
            ChartWarningKind.InstallEstimationMetadataMismatch => new(ChartWarningCategory.InstallEstimation, 41, ChartWarningShape.Path, highlightRow: true),
            ChartWarningKind.InstallEstimationUnrelated => new(ChartWarningCategory.InstallEstimation, 42, ChartWarningShape.Path, highlightRow: true),
            ChartWarningKind.InstallEstimationMetadataUnavailable => new(ChartWarningCategory.InstallEstimation, 42, ChartWarningShape.Path, highlightRow: true),
            ChartWarningKind.InstallEstimationAudioMissing => new(ChartWarningCategory.InstallEstimation, 42, ChartWarningShape.Path, highlightRow: true),
            ChartWarningKind.InstallEstimationAudioUnverifiable => new(ChartWarningCategory.InstallEstimation, 42, ChartWarningShape.Path, highlightRow: true),
            ChartWarningKind.InstallEstimationReinstallNotImproved => new(ChartWarningCategory.InstallEstimation, 43, ChartWarningShape.Path, highlightRow: true),
            ChartWarningKind.InstalledDestinationAmbiguous => new(ChartWarningCategory.InstallEstimation, 43, ChartWarningShape.Candidates, highlightRow: true),
            ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous => new(ChartWarningCategory.InstallEstimation, 43, ChartWarningShape.Candidates, highlightRow: true),
            ChartWarningKind.UnsupportedResourcePath => new(ChartWarningCategory.InstallEstimation, 44, ChartWarningShape.None, highlightRow: false),
            ChartWarningKind.InstalledDestinationResolveFailed => new(ChartWarningCategory.InstallEstimation, 45, ChartWarningShape.None, highlightRow: false),
            ChartWarningKind.SourceSurfaceScanLimitExceeded => new(ChartWarningCategory.InstallEstimation, 46, ChartWarningShape.Count, highlightRow: false),
            ChartWarningKind.InstallEstimationLowConfidence => new(ChartWarningCategory.InstallEstimation, 47, ChartWarningShape.None, highlightRow: true),
            ChartWarningKind.AlreadyInstalled => new(ChartWarningCategory.InstalledState, 50, ChartWarningShape.None, highlightRow: false),
            ChartWarningKind.SingleBmsFile => new(ChartWarningCategory.PackageLayout, 60, ChartWarningShape.None, highlightRow: false),
            ChartWarningKind.SingleBmsonFile => new(ChartWarningCategory.PackageLayout, 60, ChartWarningShape.None, highlightRow: false),
            ChartWarningKind.ResourceWavMissing or ChartWarningKind.ResourceBgaMissing or ChartWarningKind.ResourceMovieMissing => new(ChartWarningCategory.ResourceHealth, 80, ChartWarningShape.ResourceHealth, highlightRow: false),
            ChartWarningKind.ResourceStagefileMissing or ChartWarningKind.ResourceBackbmpMissing or ChartWarningKind.ResourceBannerMissing => new(ChartWarningCategory.ResourceHealth, 83, ChartWarningShape.None, highlightRow: false, showInDigest: false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}
