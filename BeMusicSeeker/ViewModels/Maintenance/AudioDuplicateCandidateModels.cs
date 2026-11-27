using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 重複音源の整理候補一覧の列の幅・表示位置・表示状態です。起動中だけ保持し、設定へ保存しません。
/// 画面の表が購読するため、各列は変更を同期で通知する <see cref="PlaylistSummaryColumnSettings.ColumnLayout"/> を使います。
/// </summary>
public sealed class AudioDuplicateCandidateColumnSettings
{
    /// <summary>SELECT列（チェックボックス）。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Select { get; } = new() { Width = 64 };

    /// <summary>STATUS列（文字）。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Status { get; } = new() { Width = 200 };

    /// <summary>CONFLICTS列（衝突名の数）。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Conflicts { get; } = new() { Width = 96 };

    /// <summary>PATH列。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Path { get; } = new() { Width = 480 };

    /// <summary>FOLDER列（フォルダ名）。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Folder { get; } = new() { Width = 200 };

    /// <summary>TITLE列。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Title { get; } = new() { Width = 240 };

    /// <summary>ARTIST列。</summary>
    public PlaylistSummaryColumnSettings.ColumnLayout Artist { get; } = new() { Width = 200 };
}

/// <summary>整理候補一覧の「衝突整理」の値です。どちらの形式を削除するかを決めます。削除方法は「削除」の値によります。</summary>
public enum AudioDuplicateCollisionCleanupMode
{
    /// <summary>OGGを残し、WAVを削除します（既定）。</summary>
    KeepOgg,

    /// <summary>WAVを残し、OGGを削除します。</summary>
    KeepWav
}

/// <summary>一つの重複音源フォルダの状態です。<see cref="DuplicateGroup"/> のインスタンスに対応付けて保持し、UIスレッドだけで読み書きします。</summary>
internal sealed class AudioDuplicateCandidateState
{
    internal AudioDuplicateCandidateState(AudioDuplicateCandidateInitial initial)
    {
        Group = initial.Group;
        Folder = initial.Folder;
        CollisionNames = initial.CollisionNames;
    }

    /// <summary>この状態の持ち主のグループ（一フォルダ）です。</summary>
    internal DuplicateGroup Group { get; }

    /// <summary>フォルダのパスです。</summary>
    internal string Folder { get; }

    /// <summary>探索時点の衝突名です。探索し直しで同じインスタンスが引き継がれたときは、新しい探索のものへ更新します。</summary>
    internal IReadOnlyList<string> CollisionNames { get; set; }

    /// <summary>処理対象としてチェックされているかです。初期値はオフです。</summary>
    internal bool IsSelected { get; set; }

    /// <summary>確認結果です。未確認はnullです。</summary>
    internal AudioDuplicateFolderInspection Inspection { get; set; }

    /// <summary>SELECTを操作できるかです。整理不可のフォルダはオンにできません。</summary>
    internal bool CanSelect => Inspection?.Stage != AudioDuplicateInspectionStage.Unorganizable;

    /// <summary>CONFLICTSの値です。安全と要注意は確認が確定した数、未確認と整理不可は探索時点の数です。</summary>
    internal int ConflictCount
        => Inspection is { Stage: AudioDuplicateInspectionStage.Safe or AudioDuplicateInspectionStage.Caution }
            ? Inspection.CollisionNames.Count
            : CollisionNames.Count;

    /// <summary>表示・検索・除外・並べ替えが共用する、現在の表示言語のSTATUSです。</summary>
    internal string StatusText => AudioDuplicateStatusPresentation.FormatStatus(Inspection);
}

/// <summary>フォルダの初期状態を、構築時に一度だけ導いた不変の値です。</summary>
internal sealed record AudioDuplicateCandidateInitial(DuplicateGroup Group, string Folder, IReadOnlyList<string> CollisionNames);

/// <summary>整理の対象として捕捉した、表示中でSELECTがオンのフォルダ一つです。以後の状態の変更は影響しません。</summary>
/// <param name="Group">対象のグループ。</param>
/// <param name="Folder">フォルダのパス。</param>
/// <param name="Inspection">捕捉した時点の確認結果。未確認はnullです。</param>
/// <param name="CollisionCleanup">捕捉した時点の「衝突整理」の値。</param>
/// <param name="DeleteMethod">捕捉した時点の「削除」の値。整理の確認文・要求・結果の案内が、捨てる形式の音源の削除方法として共通に使う入力です。</param>
internal sealed record AudioDuplicateCandidateTarget(
    DuplicateGroup Group, string Folder, AudioDuplicateFolderInspection Inspection, AudioDuplicateCollisionCleanupMode CollisionCleanup,
    DuplicateOrganizeDeleteMethod DeleteMethod = DuplicateOrganizeDeleteMethod.MoveToRecycleBin)
{
    /// <summary>未確認のフォルダかです。</summary>
    internal bool IsUnverified => Inspection == null;
}

/// <summary>確認の結果を状態表へ書き込む一件です。</summary>
/// <param name="Group">書き込むフォルダのグループ。</param>
/// <param name="Inspection">確認結果。</param>
internal sealed record AudioDuplicateVerificationWrite(DuplicateGroup Group, AudioDuplicateFolderInspection Inspection);

/// <summary>一覧の一行（一フォルダ）です。不変で、状態の変化は <see cref="State"/> から読みます。</summary>
internal sealed class AudioDuplicateCandidateRow
{
    internal AudioDuplicateCandidateRow(AudioDuplicateCandidateState state, string folderName, string title, string artist, int index)
    {
        State = state;
        FolderName = folderName;
        Title = title;
        Artist = artist;
        Index = index;
    }

    /// <summary>行の状態です。</summary>
    internal AudioDuplicateCandidateState State { get; }

    /// <summary>フォルダのパスです。</summary>
    internal string Path => State.Folder;

    /// <summary>フォルダ名です。</summary>
    internal string FolderName { get; }

    /// <summary>導入先候補プロフィールの実在最頻ペアのTITLEです。有効なペアがなければ空です。</summary>
    internal string Title { get; }

    /// <summary>導入先候補プロフィールの実在最頻ペアのARTISTです。有効なペアがなければ空です。</summary>
    internal string Artist { get; }

    /// <summary>一覧の中の行番号です。</summary>
    internal int Index { get; }

    /// <summary>STATUSの表示文言です。読取りのたびに現在の表示言語で作ります。</summary>
    internal string StatusText => State.StatusText;

    /// <summary>STATUSのツールチップです。読取りのたびに現在の表示言語で作ります。未確認はnullです。</summary>
    internal string StatusTooltipText => AudioDuplicateStatusPresentation.FormatTooltip(State.Inspection, State.Folder);

    /// <summary>CONFLICTSの表示文言です。</summary>
    internal string ConflictsText => State.ConflictCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 採用済みの行集合です。表は読取り専用の一覧として扱います。一行が一つのフォルダなので、各行を単独の行グループとして返します
/// （操作できるチェックボックスのダブルクリックを、行の起動にしないために、行グループの提供元として振る舞います）。
/// </summary>
internal sealed class AudioDuplicateCandidateRowSet : IList, IReadOnlyList<AudioDuplicateCandidateRow>, ICustomTableRowGroupSource
{
    private readonly AudioDuplicateCandidateRow[] rows;

    /// <summary>行を持たない集合です。</summary>
    internal static AudioDuplicateCandidateRowSet Empty { get; } = new([]);

    internal AudioDuplicateCandidateRowSet(AudioDuplicateCandidateRow[] rows) => this.rows = rows;

    /// <inheritdoc />
    public CustomTableRowGroup GetRowGroup(int rowIndex) => new(rowIndex, 1, rowIndex);

    public int Count => rows.Length;

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public AudioDuplicateCandidateRow this[int index] => rows[index];

    object IList.this[int index]
    {
        get => rows[index];
        set => throw new NotSupportedException();
    }

    public bool Contains(object value) => IndexOf(value) >= 0;

    public int IndexOf(object value)
        => value is AudioDuplicateCandidateRow row && (uint)row.Index < (uint)rows.Length && ReferenceEquals(rows[row.Index], row)
            ? row.Index : -1;

    public void CopyTo(Array array, int index) => rows.CopyTo(array, index);

    public IEnumerator<AudioDuplicateCandidateRow> GetEnumerator() => ((IEnumerable<AudioDuplicateCandidateRow>)rows).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => rows.GetEnumerator();

    int IList.Add(object value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object value) => throw new NotSupportedException();

    void IList.Remove(object value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>
/// 整理候補一覧の確認結果を、STATUSの文言とそのツールチップへ書式化します。書式化は読取りのたびに現在の表示言語で行い、
/// 表示言語の変更を、状態を作り直さずに反映します。
/// </summary>
internal static class AudioDuplicateStatusPresentation
{
    /// <summary>ツールチップで、衝突名を表示する最大件数です。超える分は「ほか{n}件」に省略します。</summary>
    internal const int MaxTooltipNames = 10;

    /// <summary>
    /// STATUSの表示文言です。未確認は「未確認」、安全は「安全」（すべての衝突名を同等と判定していれば「安全（WAV/OGG同等）」）、
    /// 要注意は「要注意：WAV/OGG非同等」、整理不可は「整理不可：リンク」「整理不可：解析不能」です。
    /// </summary>
    internal static string FormatStatus(AudioDuplicateFolderInspection inspection)
    {
        if (inspection == null) { return Resources.Duplicate_merge_candidate_status_unverified; }
        switch (inspection.Stage)
        {
            case AudioDuplicateInspectionStage.Safe:
                return inspection.CollisionNames.Count == 0
                    ? Resources.Duplicate_merge_candidate_status_safe
                    : string.Format(
                        Resources.Duplicate_merge_candidate_status_note_format,
                        Resources.Duplicate_merge_candidate_status_safe,
                        Resources.Duplicate_merge_candidate_status_audio_equivalent);
            case AudioDuplicateInspectionStage.Caution:
                return string.Format(
                    Resources.Duplicate_merge_candidate_status_reasons_format,
                    Resources.Duplicate_merge_candidate_status_caution,
                    Resources.Audio_duplicate_candidate_reason_non_equivalent);
            default:
                return string.Format(
                    Resources.Duplicate_merge_candidate_status_reasons_format,
                    Resources.Audio_duplicate_candidate_status_unorganizable,
                    string.Join(
                        DuplicateMergeVerificationPresentation.ReasonSeparator,
                        inspection.Reasons.Select(reason => ReasonName(reason.Kind))));
        }
    }

    /// <summary>STATUSで検索できる、現在の言語の段階名と理由名を補完順に返します。</summary>
    internal static IReadOnlyList<string> GetSearchStatusValues()
        =>
        [
            Resources.Duplicate_merge_candidate_status_unverified,
            Resources.Duplicate_merge_candidate_status_safe,
            Resources.Duplicate_merge_candidate_status_caution,
            Resources.Audio_duplicate_candidate_status_unorganizable,
            Resources.Audio_duplicate_candidate_reason_non_equivalent,
            Resources.Audio_duplicate_candidate_reason_link,
            Resources.Audio_duplicate_candidate_reason_unreadable,
        ];

    private static string ReasonName(DuplicateMergeVerificationReasonKind kind) => kind switch
    {
        DuplicateMergeVerificationReasonKind.Link => Resources.Audio_duplicate_candidate_reason_link,
        DuplicateMergeVerificationReasonKind.Unreadable => Resources.Audio_duplicate_candidate_reason_unreadable,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown audio duplicate reason.")
    };

    private static string ReasonDescription(DuplicateMergeVerificationReasonKind kind) => kind switch
    {
        DuplicateMergeVerificationReasonKind.Link => Resources.Duplicate_merge_candidate_reason_link_description,
        DuplicateMergeVerificationReasonKind.Unreadable => Resources.Duplicate_merge_candidate_reason_unreadable_description,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown audio duplicate reason.")
    };

    /// <summary>
    /// STATUSのツールチップです。要注意では説明行の次に、診断を持つ確認結果なら内訳と音質の目安の行を出し、同等と判定しなかった衝突名を昇順（大文字小文字を無視）に
    /// 「- 名前：理由」（診断を持たなければ「- 名前」）で出し、10件を超える分は「ほか{n}件」と省略します。
    /// 同等と判定した衝突名が一つ以上あれば、段階によらず末尾に同等の組数の文を出します。整理不可では理由の説明と該当するパスを出します。未確認はnullです。
    /// </summary>
    /// <param name="folder">整理不可の該当のパスを組み立てる、確認したフォルダ。</param>
    internal static string FormatTooltip(AudioDuplicateFolderInspection inspection, string folder)
    {
        if (inspection == null) { return null; }
        var sections = new List<string>();
        if (inspection.Stage == AudioDuplicateInspectionStage.Caution)
        {
            var lines = new List<string> { Resources.Audio_duplicate_candidate_non_equivalent_description };
            string[] names = [.. inspection.NonEquivalentCollisionNames.Order(StringComparer.OrdinalIgnoreCase)];
            if (inspection.PairDiagnostics == null)
            {
                // 診断を持たない確認結果は、内訳・音質の目安・名前ごとの理由を出さず、従来の名前の行のままにする。
                AppendNames(lines, names);
            }
            else
            {
                IReadOnlyDictionary<string, DuplicateMergeAudioDiagnosticPresentation.NameSummary> summaries =
                    DuplicateMergeAudioDiagnosticPresentation.SummarizeNonEquivalentNames(inspection.PairDiagnostics);
                lines.AddRange(DuplicateMergeAudioDiagnosticPresentation.FormatSummaryLines(
                    [.. names.Where(summaries.ContainsKey).Select(name => summaries[name])]));
                AppendNames(lines, names.Select(name => summaries.TryGetValue(name, out DuplicateMergeAudioDiagnosticPresentation.NameSummary summary)
                    ? DuplicateMergeAudioDiagnosticPresentation.FormatNameWithReason(summary)
                    : name));
            }
            sections.Add(string.Join(Environment.NewLine, lines));
        }
        foreach (DuplicateMergeVerificationReason reason in inspection.Reasons)
        {
            var lines = new List<string> { ReasonDescription(reason.Kind) };
            AppendNames(lines, reason.Findings.Select(finding => FormatFinding(finding, folder)));
            sections.Add(string.Join(Environment.NewLine, lines));
        }
        if (inspection.EquivalentCollisionNames.Count > 0)
        {
            sections.Add(string.Format(Resources.Duplicate_merge_candidate_tooltip_audio_equivalent_format, inspection.EquivalentCollisionNames.Count));
        }
        return sections.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static void AppendNames(List<string> lines, IEnumerable<string> items)
    {
        string[] all = [.. items];
        lines.AddRange(all.Take(MaxTooltipNames).Select(item => "- " + item));
        if (all.Length > MaxTooltipNames)
        {
            lines.Add(string.Format(Resources.Duplicate_merge_candidate_tooltip_more_format, all.Length - MaxTooltipNames));
        }
    }

    private static string FormatFinding(DuplicateMergeFinding finding, string folder)
    {
        string root = string.IsNullOrEmpty(finding.Folder) ? folder ?? string.Empty : finding.Folder;
        string path = string.IsNullOrEmpty(finding.RelativePath)
            ? root
            : string.IsNullOrEmpty(root) ? finding.RelativePath : root.TrimEnd('\\', '/') + "\\" + finding.RelativePath;
        return string.IsNullOrEmpty(finding.Detail) ? path : path + " : " + finding.Detail;
    }
}
