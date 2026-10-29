using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>明示読取りだけで収集する定義元・定義番号と参照です。定義元は解析時の命令またはBMSON項目から保持し、拡張子から推定しません。</summary>
internal sealed record ChartResourceDefinition(string Source, string Id, ChartResourceReference Reference,
    int? LineNumber = null, string RawLine = null, bool IsAdopted = true, bool Cp932DecodeUnsupported = false);

/// <summary>対応する定義のない参照番号です。定義元ごとの名前空間を保ちます。</summary>
internal sealed record ChartResourceReferenceNumber(string Source, string Id);

/// <summary>一回の軽量解析中だけ診断詳細を受け取ります。通常解析は生成しません。</summary>
internal sealed class ChartResourceParseDetails
{
    /// <summary>同じIDの複数定義も原文のまま保持します。</summary>
    internal List<ChartResourceDefinition> Definitions { get; } = [];
    /// <summary>採用本文とは別のCP932解釈でだけ得た問題原文です。同本文の事実はDefinitionsへ添え、別解釈をIDや行番号で結合しません。</summary>
    internal List<ChartResourceDefinition> AlternativeCp932Definitions { get; } = [];
    /// <summary>定義元ごとに重複を除いた未定義参照番号です。</summary>
    internal List<ChartResourceReferenceNumber> UndefinedReferences { get; } = [];
}

/// <summary>定義の分類・存在確認の結果です。正常な任意画像もSatisfiedとして保持します。</summary>
internal enum ChartResourceDiagnosticReason
{
    Missing,
    InvalidPath,
    Unsupported,
    Unverifiable,
    Empty,
    MissingFileName,
    UnknownKind,
    MalformedSyntax,
    Satisfied
}

/// <summary>定義と、その分類・存在確認の結果です。</summary>
internal sealed record ChartResourceDiagnosticIssue(ChartResourceDefinition Definition, ChartResourceDiagnosticReason Reason);

/// <summary>一画面の寿命だけで使用する診断結果です。通常参照の問題とCP932事実だけの使用定義、任意画像の全定義を各一回保持し、別解釈のCP932原文は対応付けずに分けます。ライブラリへ書き戻しません。</summary>
internal sealed record ChartResourceDiagnostics(
    string Title,
    string Artist,
    ImmutableArray<ChartResourceDiagnosticIssue> Issues,
    ImmutableArray<ChartResourceDefinition> UnusedDefinitions,
    ImmutableArray<ChartResourceReferenceNumber> UndefinedReferences,
    ImmutableArray<ChartResourceDiagnosticIssue> OptionalImageDefinitions = default,
    ImmutableArray<ChartResourceDefinition> AlternativeCp932Definitions = default);

/// <summary>選択譜面一件を既存の軽量パーサーで読み、捕捉した配置文脈で資源を照合します。DB、管理主体、索引へ依存しません。</summary>
internal sealed class ChartResourceDiagnosticsService
{
    /// <summary>一回捕捉したファイルを診断します。取得・解析の失敗を呼出元へ伝えます。</summary>
    internal ChartResourceDiagnostics Read(string path, ChartFileKind kind, ChartResourcePlacement placement = null, string encoding = null)
        => Diagnose(ChartFileContentReader.ReadSnapshot(path), kind, placement, encoding);

    /// <summary>捕捉済み内容の全行を既存規則で解析します。未定義IDから必要ファイルを生成しません。</summary>
    internal ChartResourceDiagnostics Diagnose(ChartFileSnapshot snapshot, ChartFileKind kind, ChartResourcePlacement placement = null, string encoding = null)
    {
        var details = new ChartResourceParseDetails();
        ChartFile chart;
        if (kind == ChartFileKind.Bmson)
        {
            chart = BmsonChartFileParser.ParseSnapshot(snapshot, details);
        }
        else
        {
            chart = BmsChartFileParser.ParseSnapshot(snapshot, encoding, details);
        }

        placement ??= new ChartResourcePlacement(ChartResourcePlacementBasis.Current, Path.GetDirectoryName(snapshot.Path));
        ImmutableArray<ChartResourceDiagnosticIssue>.Builder issues = ImmutableArray.CreateBuilder<ChartResourceDiagnosticIssue>();
        ImmutableArray<ChartResourceDiagnosticIssue>.Builder optionalImages = ImmutableArray.CreateBuilder<ChartResourceDiagnosticIssue>();
        foreach (ChartResourceDefinition definition in details.Definitions.Where(item => item.Reference.IsUsed))
        {
            ChartResourceReference reference = definition.Reference;
            ChartResourceDiagnosticReason? reason = reference.Status switch
            {
                ChartResourcePathNormalizationStatus.Valid => null,
                ChartResourcePathNormalizationStatus.ParentTraversalUnsupported => ChartResourceDiagnosticReason.Unsupported,
                ChartResourcePathNormalizationStatus.Empty => ChartResourceDiagnosticReason.Empty,
                ChartResourcePathNormalizationStatus.MissingFileName => ChartResourceDiagnosticReason.MissingFileName,
                ChartResourcePathNormalizationStatus.UnknownKind => ChartResourceDiagnosticReason.UnknownKind,
                ChartResourcePathNormalizationStatus.MalformedSyntax => ChartResourceDiagnosticReason.MalformedSyntax,
                _ => ChartResourceDiagnosticReason.InvalidPath
            };
            if (reason == null && reference.Usage == ChartResourceUsage.Normal && reference.Kind == ChartResourceKind.Unknown)
            {
                reason = ChartResourceDiagnosticReason.Unsupported;
            }
            if (reason == null)
            {
                bool? exists = placement.Exists(reference);
                reason = exists == true ? null : exists == false ? ChartResourceDiagnosticReason.Missing : ChartResourceDiagnosticReason.Unverifiable;
            }
            if (reference.Usage != ChartResourceUsage.Normal)
            {
                optionalImages.Add(new ChartResourceDiagnosticIssue(definition, reason ?? ChartResourceDiagnosticReason.Satisfied));
            }
            else if (reason.HasValue || definition.Cp932DecodeUnsupported)
            {
                issues.Add(new ChartResourceDiagnosticIssue(definition, reason ?? ChartResourceDiagnosticReason.Satisfied));
            }
        }
        return new ChartResourceDiagnostics(chart.Title, chart.Artist, issues.ToImmutable(),
            details.Definitions.Where(item => item.Reference.Usage == ChartResourceUsage.Normal && !item.Reference.IsUsed).ToImmutableArray(),
            details.UndefinedReferences.ToImmutableArray(), optionalImages.ToImmutable(), details.AlternativeCp932Definitions.ToImmutableArray());
    }
}
