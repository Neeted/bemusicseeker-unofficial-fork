using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 統合が同名のファイルを扱うときの上書きの設定です。衝突の説明を、設定によって変わると述べるだけにせず、実際の扱いで示すために使います。
/// </summary>
/// <param name="SmartOverwrite">スマート上書きが有効かです。無効なら移動元のファイルで上書きし、有効なら更新日時の新しい方を残します。</param>
/// <param name="KeepProtectedByRenaming">スマート上書き時に、同名の .bmx・.pmx・.txt を連番名で保持する設定が有効かです。スマート上書きが無効なら効きません。</param>
internal readonly record struct DuplicateMergeOverwriteSettings(bool SmartOverwrite, bool KeepProtectedByRenaming)
{
    /// <summary>内容が異なる同名の .bmx・.pmx・.txt を、連番付きの名前で両方残すかです。</summary>
    internal bool KeepsBothProtectedFiles => SmartOverwrite && KeepProtectedByRenaming;
}

/// <summary>
/// グループの確認結果を、STATUSの文言とそのツールチップへ書式化します。書式化は読取りのたびに現在の表示言語で行い、
/// 表示言語の変更を、状態を作り直さずに反映します。
/// </summary>
internal static class DuplicateMergeVerificationPresentation
{
    /// <summary>ツールチップで、一つの理由につき表示する該当の最大件数です。</summary>
    internal const int MaxTooltipFindingsPerReason = 10;

    /// <summary>STATUSで理由を区切る文字列です。譜面一覧のWARNING列と同じく、表示言語によらず「, 」です。</summary>
    internal const string ReasonSeparator = ", ";

    /// <summary>
    /// STATUSの表示文言です。未確認は「未確認」、安全は「安全」（.bmx/.txtの衝突があれば付記つき）、
    /// 要注意と統合不可は「要注意：（理由, 理由…）」「統合不可：（理由, 理由…）」で、成立した理由をすべて結果の順に並べます。
    /// 区切りは表示言語によらない「, 」で、譜面一覧のWARNING列と同じです。理由の順は <see cref="DuplicateMergeVerificationReasonKind"/> の宣言順（結果が保つ順）です。
    /// .bmx/.txtの衝突は、要注意と統合不可の文言には含めません。
    /// </summary>
    internal static string FormatStatus(this DuplicateMergeVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Stage)
        {
            case DuplicateMergeVerificationStage.Safe:
                return result.HasProtectedNameCollision
                    ? Resources.Duplicate_merge_candidate_status_safe_protected_collision
                    : Resources.Duplicate_merge_candidate_status_safe;
            case DuplicateMergeVerificationStage.Caution:
            case DuplicateMergeVerificationStage.Unmergeable:
                if (result.Reasons.Count == 0) { break; }
                return string.Format(
                    result.Stage == DuplicateMergeVerificationStage.Unmergeable
                        ? Resources.Duplicate_merge_candidate_status_unmergeable_format
                        : Resources.Duplicate_merge_candidate_status_caution_format,
                    string.Join(ReasonSeparator, result.Reasons.Select(reason => ReasonName(reason.Kind))));
        }
        return Resources.Duplicate_merge_candidate_status_unverified;
    }

    /// <summary>
    /// STATUSのツールチップです。理由ごとに説明と該当するフォルダ・相対パスを出し、一つの理由につき10件を超える分は「ほか{n}件」と省略します。
    /// 安全では比較した同名の資源の数と方法を出します。別内容ファイル衝突と.bmx/.txtの衝突（ファイル名を併記）には、統合したときの扱いを添えます。未確認はnullです。
    /// </summary>
    /// <param name="overwrite">読む時点の上書きの設定です。衝突に添える扱いの説明を決めます。</param>
    internal static string FormatTooltip(this DuplicateMergeVerificationResult result, DuplicateMergeOverwriteSettings overwrite)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Stage == DuplicateMergeVerificationStage.Unverified) { return null; }
        var sections = new List<string>();
        foreach (DuplicateMergeVerificationReason reason in result.Reasons)
        {
            var lines = new List<string> { ReasonDescription(reason.Kind) };
            if (reason.Kind == DuplicateMergeVerificationReasonKind.ContentConflict)
            {
                lines.Add(OverwriteNote(overwrite));
            }
            AppendFindings(lines, reason.Findings.Select(FormatFinding));
            sections.Add(string.Join(Environment.NewLine, lines));
        }
        if (result.HasProtectedNameCollision)
        {
            var lines = new List<string>
            {
                Resources.Duplicate_merge_candidate_protected_collision_description,
                overwrite.KeepsBothProtectedFiles ? Resources.Duplicate_merge_candidate_overwrite_note_renamed : OverwriteNote(overwrite)
            };
            AppendFindings(lines, result.ProtectedNameCollisions);
            sections.Add(string.Join(Environment.NewLine, lines));
        }
        if (result.Stage == DuplicateMergeVerificationStage.Safe)
        {
            sections.Add(FormatComparisons(result.Comparisons ?? DuplicateMergeComparisonSummary.None));
        }
        return sections.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    /// <summary>内容が異なる同名のファイルの一方だけが残る場合の、統合したときの扱いの説明です。</summary>
    private static string OverwriteNote(DuplicateMergeOverwriteSettings overwrite) => overwrite.SmartOverwrite
        ? Resources.Duplicate_merge_candidate_overwrite_note_newer
        : Resources.Duplicate_merge_candidate_overwrite_note_source;

    private static void AppendFindings(List<string> lines, IEnumerable<string> items)
    {
        string[] all = [.. items];
        lines.AddRange(all.Take(MaxTooltipFindingsPerReason).Select(item => "- " + item));
        if (all.Length > MaxTooltipFindingsPerReason)
        {
            lines.Add(string.Format(Resources.Duplicate_merge_candidate_tooltip_more_format, all.Length - MaxTooltipFindingsPerReason));
        }
    }

    private static string FormatFinding(DuplicateMergeFinding finding)
    {
        string path = string.IsNullOrEmpty(finding.RelativePath)
            ? finding.Folder ?? string.Empty
            : string.IsNullOrEmpty(finding.Folder) ? finding.RelativePath : finding.Folder.TrimEnd('\\', '/') + "\\" + finding.RelativePath;
        return string.IsNullOrEmpty(finding.Detail) ? path : path + " : " + finding.Detail;
    }

    private static string FormatComparisons(DuplicateMergeComparisonSummary comparisons)
    {
        if (comparisons.Total == 0) { return Resources.Duplicate_merge_candidate_safe_not_compared; }
        (string Name, int Count)[] methods =
        [
            (Resources.Duplicate_merge_candidate_method_size_and_time, comparisons.SizeAndTime),
            (Resources.Duplicate_merge_candidate_method_full, comparisons.Full),
            (Resources.Duplicate_merge_candidate_method_partial, comparisons.Partial)
        ];
        (string Name, int Count)[] used = [.. methods.Where(method => method.Count > 0)];
        if (used.Length == 1)
        {
            return string.Format(Resources.Duplicate_merge_candidate_safe_compared_single_format, comparisons.Total, used[0].Name);
        }
        var text = new StringBuilder(string.Format(Resources.Duplicate_merge_candidate_safe_compared_header_format, comparisons.Total));
        foreach ((string name, int count) in used)
        {
            text.AppendLine().Append("- ").Append(string.Format(Resources.Duplicate_merge_candidate_safe_compared_method_format, name, count));
        }
        return text.ToString();
    }

    /// <summary>理由の短い名前です。STATUSに使います。</summary>
    internal static string ReasonName(DuplicateMergeVerificationReasonKind kind) => kind switch
    {
        DuplicateMergeVerificationReasonKind.Link => Resources.Duplicate_merge_candidate_reason_link,
        DuplicateMergeVerificationReasonKind.RegisteredRoot => Resources.Duplicate_merge_candidate_reason_registered_root,
        DuplicateMergeVerificationReasonKind.ParentChild => Resources.Duplicate_merge_candidate_reason_parent_child,
        DuplicateMergeVerificationReasonKind.TypeConflict => Resources.Duplicate_merge_candidate_reason_type_conflict,
        DuplicateMergeVerificationReasonKind.OtherGroup => Resources.Duplicate_merge_candidate_reason_other_group,
        DuplicateMergeVerificationReasonKind.Unreadable => Resources.Duplicate_merge_candidate_reason_unreadable,
        DuplicateMergeVerificationReasonKind.ParentReference => Resources.Duplicate_merge_candidate_reason_parent_reference,
        DuplicateMergeVerificationReasonKind.AudioExtension => Resources.Duplicate_merge_candidate_reason_audio_extension,
        DuplicateMergeVerificationReasonKind.ContentConflict => Resources.Duplicate_merge_candidate_reason_content_conflict,
        DuplicateMergeVerificationReasonKind.NoSharedContent => Resources.Duplicate_merge_candidate_reason_no_shared_content,
        DuplicateMergeVerificationReasonKind.NestedChartFolder => Resources.Duplicate_merge_candidate_reason_nested_chart_folder,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown duplicate merge reason.")
    };

    /// <summary>理由の説明です。ツールチップの見出しに使います。</summary>
    internal static string ReasonDescription(DuplicateMergeVerificationReasonKind kind) => kind switch
    {
        DuplicateMergeVerificationReasonKind.Link => Resources.Duplicate_merge_candidate_reason_link_description,
        DuplicateMergeVerificationReasonKind.RegisteredRoot => Resources.Duplicate_merge_candidate_reason_registered_root_description,
        DuplicateMergeVerificationReasonKind.ParentChild => Resources.Duplicate_merge_candidate_reason_parent_child_description,
        DuplicateMergeVerificationReasonKind.TypeConflict => Resources.Duplicate_merge_candidate_reason_type_conflict_description,
        DuplicateMergeVerificationReasonKind.OtherGroup => Resources.Duplicate_merge_candidate_reason_other_group_description,
        DuplicateMergeVerificationReasonKind.Unreadable => Resources.Duplicate_merge_candidate_reason_unreadable_description,
        DuplicateMergeVerificationReasonKind.ParentReference => Resources.Duplicate_merge_candidate_reason_parent_reference_description,
        DuplicateMergeVerificationReasonKind.AudioExtension => Resources.Duplicate_merge_candidate_reason_audio_extension_description,
        DuplicateMergeVerificationReasonKind.ContentConflict => Resources.Duplicate_merge_candidate_reason_content_conflict_description,
        DuplicateMergeVerificationReasonKind.NoSharedContent => Resources.Duplicate_merge_candidate_reason_no_shared_content_description,
        DuplicateMergeVerificationReasonKind.NestedChartFolder => Resources.Duplicate_merge_candidate_reason_nested_chart_folder_description,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown duplicate merge reason.")
    };
}
