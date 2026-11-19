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

/// <summary>統合候補一覧のグループごとの確認結果の段階です。この単位で現れるのは <see cref="Unverified"/> と、登録ルートによる <see cref="Unmergeable"/> だけです。</summary>
internal enum DuplicateMergeVerificationStage
{
    /// <summary>確認していません。</summary>
    Unverified,
    /// <summary>統合できません。SELECTをオンにできません。</summary>
    Unmergeable,
    /// <summary>統合できますが、確認が必要な点があります。</summary>
    Caution,
    /// <summary>統合しても作品として意味が崩れないと判断しました。</summary>
    Safe
}

/// <summary>確認結果の理由の種類です。後続の安全性確認が種類を追加します。</summary>
internal enum DuplicateMergeVerificationReasonKind
{
    /// <summary>グループ内に登録範囲（登録BMSルート本体、または空の登録子ルートを含む祖先）のフォルダが二つ以上あります。</summary>
    RegisteredRoot
}

/// <summary>確認結果の一つの理由と、該当するパスです。</summary>
internal sealed record DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind Kind, IReadOnlyList<string> Paths);

/// <summary>グループごとの確認結果です。段階と理由の一覧を不変の値として持ちます。</summary>
internal sealed record DuplicateMergeVerificationResult(
    DuplicateMergeVerificationStage Stage, IReadOnlyList<DuplicateMergeVerificationReason> Reasons)
{
    /// <summary>未確認の結果です。新しいグループの初期値です。</summary>
    internal static DuplicateMergeVerificationResult Unverified { get; } = new(DuplicateMergeVerificationStage.Unverified, []);

    /// <summary>登録範囲のフォルダが二つ以上あるため統合できない結果を作ります。</summary>
    internal static DuplicateMergeVerificationResult UnmergeableRegisteredRoot(IReadOnlyList<string> registeredFolders)
        => new(DuplicateMergeVerificationStage.Unmergeable,
            [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.RegisteredRoot, registeredFolders)]);

    /// <summary>
    /// STATUSの表示文言を、読取りのたびに現在の表示言語で返します。後続の単位が段階・理由を追加するときは、ここへ対応を足します。
    /// 未対応の組合せは「未確認」を返します。
    /// </summary>
    internal string FormatStatus()
        => Stage == DuplicateMergeVerificationStage.Unmergeable
            && Reasons.Count > 0 && Reasons[0].Kind == DuplicateMergeVerificationReasonKind.RegisteredRoot
            ? Resources.Duplicate_merge_candidate_status_unmergeable_registered_root
            : Resources.Duplicate_merge_candidate_status_unverified;
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

    /// <summary>確認結果です。初期値は未確認で、登録範囲が二つ以上ある場合は統合不可です。</summary>
    internal DuplicateMergeVerificationResult Verification { get; set; }

    /// <summary>SELECTを操作できるかです。統合不可のグループはオンにできません。</summary>
    internal bool CanSelect => Verification.Stage != DuplicateMergeVerificationStage.Unmergeable;
}

/// <summary>グループの初期状態を、構築時に一度だけ導いた不変の値です。</summary>
internal sealed record DuplicateMergeCandidateGroupInitial(
    DuplicateGroup Group, IReadOnlyList<string> Folders, string KeepFolder, bool KeepFixed, bool Unmergeable,
    IReadOnlyList<string> RegisteredFolders);

/// <summary>後続の単位が、表示中でSELECTがオンのグループから捕捉する変更不能な対象です。</summary>
/// <param name="Group">対象のグループ。</param>
/// <param name="Folders">グループのフォルダ。PATHの昇順（大文字小文字を無視）。</param>
/// <param name="KeepFolder">残すフォルダ。</param>
/// <param name="KeepFixed">KEEPが固定かどうか。</param>
internal sealed record DuplicateMergeCandidateGroupTarget(
    DuplicateGroup Group, IReadOnlyList<string> Folders, string KeepFolder, bool KeepFixed);

/// <summary>一覧の一行（一フォルダ）です。不変で、状態の変化は <see cref="State"/> から読みます。</summary>
internal sealed class DuplicateMergeCandidateRow
{
    internal DuplicateMergeCandidateRow(
        DuplicateMergeCandidateGroupState state, string path, string folderName, string title, string artist, int index, int groupIndex)
    {
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
    internal string StatusText => State.Verification.FormatStatus();
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
