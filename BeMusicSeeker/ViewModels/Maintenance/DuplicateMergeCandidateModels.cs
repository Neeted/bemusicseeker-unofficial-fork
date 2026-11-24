using System;
using System.Collections;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.ViewModels;

/// <summary>重複ツリーの先頭に置く「統合候補一覧」のノードです。ノード自身は状態を持たず、選択した画面の種別だけを表します。</summary>
internal sealed class DuplicateMergeCandidateListNode
{
}

/// <summary>
/// 一つのグループの状態です。<see cref="DuplicateGroup"/> のインスタンスに対応付けて保持し、UIスレッドだけで読み書きします。
/// 行は不変で、SELECT・STATUS・KEEPはこの状態から読みます。
/// </summary>
internal sealed class DuplicateMergeCandidateGroupState
{
    internal DuplicateMergeCandidateGroupState(DuplicateMergeCandidateGroupInitial initial)
    {
        Group = initial.Group;
        Folders = initial.Folders;
        KeepFolder = initial.KeepFolder;
        KeepFixed = initial.KeepFixed;
        RegisteredFolders = initial.RegisteredFolders;
        Verification = initial.Unmergeable
            ? DuplicateMergeVerificationResult.UnmergeableRegisteredRoot(initial.RegisteredFolders)
            : DuplicateMergeVerificationResult.Unverified;
    }

    /// <summary>この状態の持ち主のグループです。</summary>
    internal DuplicateGroup Group { get; }

    /// <summary>グループのフォルダです。PATHの昇順（大文字小文字を無視）です。</summary>
    internal IReadOnlyList<string> Folders { get; }

    /// <summary>残すフォルダです。常に <see cref="Folders"/> のいずれかです。</summary>
    internal string KeepFolder { get; set; }

    /// <summary>KEEPが固定（登録範囲、または一フォルダだけのグループ）で、操作しても変わらないかです。</summary>
    internal bool KeepFixed { get; }

    /// <summary>処理対象としてチェックされているかです。初期値はオフです。</summary>
    internal bool IsSelected { get; set; }

    /// <summary>グループ内の登録範囲のフォルダです。一覧の構築時に捕捉した値で、確認の後も統合不可の理由として保ちます。</summary>
    internal IReadOnlyList<string> RegisteredFolders { get; }

    /// <summary>確認結果です。初期値は未確認で、登録範囲が二つ以上ある場合は統合不可です。</summary>
    internal DuplicateMergeVerificationResult Verification { get; set; }

    /// <summary>
    /// 確認で走査した事実です。確認前はnullです。KEEPが変わったときは、再走査せず、この事実から <see cref="Verification"/> を導き直します。
    /// </summary>
    internal DuplicateMergeGroupInspection Inspection { get; set; }

    /// <summary>今回採用した表示値をグループ内で比べた結果です。確認結果と独立に更新します。</summary>
    internal (bool Title, bool Artist) RepresentativeMismatch { get; set; }

    /// <summary>表示・検索・除外・並べ替えが共用する、現在の表示言語のSTATUSです。</summary>
    internal string StatusText
    {
        get
        {
            string status = Verification.FormatStatus();
            if (RepresentativeMismatch == default) { return status; }
            string title = Resources.Duplicate_merge_candidate_title_mismatch;
            string artist = Resources.Duplicate_merge_candidate_artist_mismatch;
            string note = RepresentativeMismatch switch
            {
                (true, true) => title + DuplicateMergeVerificationPresentation.ReasonSeparator + artist,
                (true, false) => title,
                _ => artist
            };
            return string.Format(Resources.Duplicate_merge_candidate_status_note_format, status, note);
        }
    }

    /// <summary>SELECTを操作できるかです。統合不可のグループはオンにできません。</summary>
    internal bool CanSelect => Verification.Stage != DuplicateMergeVerificationStage.Unmergeable;
}

/// <summary>グループの初期状態を、構築時に一度だけ導いた不変の値です。</summary>
internal sealed record DuplicateMergeCandidateGroupInitial(
    DuplicateGroup Group, IReadOnlyList<string> Folders, string KeepFolder, bool KeepFixed, bool Unmergeable,
    IReadOnlyList<string> RegisteredFolders)
{
    /// <summary>構築時の代表TITLE・ARTISTがグループ内のフォルダ間で異なるかです。</summary>
    internal (bool Title, bool Artist) RepresentativeMismatch { get; init; }
}

/// <summary>後続の単位が、表示中でSELECTがオンのグループから捕捉する変更不能な対象です。</summary>
/// <param name="Group">対象のグループ。</param>
/// <param name="Folders">グループのフォルダ。PATHの昇順（大文字小文字を無視）。</param>
/// <param name="KeepFolder">残すフォルダ。</param>
/// <param name="KeepFixed">KEEPが固定かどうか。</param>
/// <param name="RegisteredFolders">グループ内の登録範囲のフォルダ。一覧の構築時に捕捉した値です。</param>
/// <param name="Verification">捕捉した時点の確認結果。一括整理の確認文が、未確認・要注意・衝突の有無を数える入力です。</param>
internal sealed record DuplicateMergeCandidateGroupTarget(
    DuplicateGroup Group, IReadOnlyList<string> Folders, string KeepFolder, bool KeepFixed,
    IReadOnlyList<string> RegisteredFolders, DuplicateMergeVerificationResult Verification = null);

/// <summary>現在の状態表の一グループの識別とフォルダです。別のグループとの重なりを判定する入力です。</summary>
/// <param name="Group">グループ。</param>
/// <param name="Folders">グループのフォルダ。</param>
internal sealed record DuplicateMergeCandidateGroupFolders(DuplicateGroup Group, IReadOnlyList<string> Folders);

/// <summary>確認の結果を状態表へ書き込む一件です。</summary>
/// <param name="Group">書き込むグループ。</param>
/// <param name="Result">確認結果。</param>
/// <param name="Inspection">KEEPの変更で結果を導き直すための走査の事実。null の場合は、KEEPを変えても結果を変えません。</param>
internal sealed record DuplicateMergeVerificationWrite(
    DuplicateGroup Group, DuplicateMergeVerificationResult Result, DuplicateMergeGroupInspection Inspection = null);

/// <summary>一覧の一行（一フォルダ）です。不変で、状態の変化は <see cref="State"/> から読みます。</summary>
internal sealed class DuplicateMergeCandidateRow
{
    private readonly Func<DuplicateMergeOverwriteSettings> overwriteSettings;

    /// <param name="overwriteSettings">統合が使う現在の上書きの設定を返します。ツールチップを読むたびに呼びます。</param>
    internal DuplicateMergeCandidateRow(
        DuplicateMergeCandidateGroupState state, string path, string folderName, string title, string artist, int index, int groupIndex,
        Func<DuplicateMergeOverwriteSettings> overwriteSettings)
    {
        this.overwriteSettings = overwriteSettings ?? throw new ArgumentNullException(nameof(overwriteSettings));
        State = state;
        Path = path;
        FolderName = folderName;
        Title = title;
        Artist = artist;
        Index = index;
        GroupIndex = groupIndex;
    }

    /// <summary>行が属するグループの状態です。</summary>
    internal DuplicateMergeCandidateGroupState State { get; }

    /// <summary>フォルダのパスです。</summary>
    internal string Path { get; }

    /// <summary>フォルダ名です。</summary>
    internal string FolderName { get; }

    /// <summary>導入先候補プロフィールの実在最頻ペアのTITLEです。有効なペアがなければ空です。</summary>
    internal string Title { get; }

    /// <summary>導入先候補プロフィールの実在最頻ペアのARTISTです。有効なペアがなければ空です。</summary>
    internal string Artist { get; }

    /// <summary>一覧の中の行番号です。</summary>
    internal int Index { get; }

    /// <summary>表示順で数えたグループの序数（0始まり）です。</summary>
    internal int GroupIndex { get; }

    /// <summary>この行のフォルダがグループのKEEPかです。</summary>
    internal bool IsKeep => string.Equals(State.KeepFolder, Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>グループのSTATUSの表示文言です。読取りのたびに現在の表示言語で作ります。</summary>
    internal string StatusText => State.StatusText;

    /// <summary>グループのSTATUSのツールチップです。読取りのたびに現在の表示言語と現在の上書きの設定で作ります。未確認はnullです。</summary>
    internal string StatusTooltipText => State.Verification.FormatTooltip(overwriteSettings());
}

/// <summary>
/// 採用済みの行集合です。行の集合と行グループの境界を同じ一覧が持ち、行ごとの表示グループ番号から問合せをO(1)で返します。
/// 表は読取り専用の一覧として扱います。
/// </summary>
internal sealed class DuplicateMergeCandidateRowSet : IList, IReadOnlyList<DuplicateMergeCandidateRow>, ICustomTableRowGroupSource
{
    private readonly DuplicateMergeCandidateRow[] rows;
    private readonly int[] groupFirstRowIndexes;
    private readonly int[] groupRowCounts;

    /// <summary>行を持たない集合です。</summary>
    internal static DuplicateMergeCandidateRowSet Empty { get; } = new([], [], []);

    internal DuplicateMergeCandidateRowSet(DuplicateMergeCandidateRow[] rows, int[] groupFirstRowIndexes, int[] groupRowCounts)
    {
        this.rows = rows;
        this.groupFirstRowIndexes = groupFirstRowIndexes;
        this.groupRowCounts = groupRowCounts;
    }

    /// <summary>表示中のグループ数です。</summary>
    internal int GroupCount => groupFirstRowIndexes.Length;

    /// <summary>表示順の指定したグループの先頭の行です。</summary>
    internal DuplicateMergeCandidateRow GetFirstRowOfGroup(int groupIndex) => rows[groupFirstRowIndexes[groupIndex]];

    /// <summary>表示順の指定したグループの行数です。</summary>
    internal int GetRowCountOfGroup(int groupIndex) => groupRowCounts[groupIndex];

    /// <inheritdoc />
    public CustomTableRowGroup GetRowGroup(int rowIndex)
    {
        int group = rows[rowIndex].GroupIndex;
        return new CustomTableRowGroup(groupFirstRowIndexes[group], groupRowCounts[group], group);
    }

    public int Count => rows.Length;

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public DuplicateMergeCandidateRow this[int index] => rows[index];

    object IList.this[int index]
    {
        get => rows[index];
        set => throw new NotSupportedException();
    }

    public bool Contains(object value) => IndexOf(value) >= 0;

    public int IndexOf(object value)
        => value is DuplicateMergeCandidateRow row && (uint)row.Index < (uint)rows.Length && ReferenceEquals(rows[row.Index], row)
            ? row.Index : -1;

    public void CopyTo(Array array, int index) => rows.CopyTo(array, index);

    public IEnumerator<DuplicateMergeCandidateRow> GetEnumerator() => ((IEnumerable<DuplicateMergeCandidateRow>)rows).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => rows.GetEnumerator();

    int IList.Add(object value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object value) => throw new NotSupportedException();

    void IList.Remove(object value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
