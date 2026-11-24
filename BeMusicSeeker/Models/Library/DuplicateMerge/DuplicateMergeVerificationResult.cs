using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>統合候補一覧のグループごとの確認結果の段階です。</summary>
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

/// <summary>
/// 確認結果の理由の種類です。宣言順が、結果の理由の並びと表示の順です。
/// <see cref="Link"/> から <see cref="Unreadable"/> までが統合不可、それ以降が要注意です。
/// </summary>
internal enum DuplicateMergeVerificationReasonKind
{
    /// <summary>いずれかのフォルダの本体または配下に、シンボリックリンクかジャンクションがあります。</summary>
    Link,
    /// <summary>グループ内に登録範囲（登録BMSルート本体、または空の登録子ルートを含む祖先）のフォルダが二つ以上あります。</summary>
    RegisteredRoot,
    /// <summary>グループ内のフォルダ同士が同一か親子です。</summary>
    ParentChild,
    /// <summary>同じ相対パスが、あるフォルダではファイル、別のフォルダではディレクトリです。</summary>
    TypeConflict,
    /// <summary>グループのフォルダが、現在の重複結果の別のグループのフォルダと同一か親子です。</summary>
    OtherGroup,
    /// <summary>権限・列挙・読取りの失敗で、安全性を判断できません。</summary>
    Unreadable,
    /// <summary>移動元になるフォルダの所持譜面が、使用中のリソースに親ディレクトリ参照を持ちます。</summary>
    ParentReference,
    /// <summary>統合により、同じ相対ディレクトリ・同じ名前の音源を持つフォルダのいずれかで、再生に使われる音源の候補が増えます。</summary>
    AudioExtension,
    /// <summary>同じ相対パスの資源が、グループ内の二つのフォルダで別内容と判定されました。</summary>
    ContentConflict,
    /// <summary>資源を持つフォルダが、同一内容の資源を共有する関係で一つに連結しません。</summary>
    NoSharedContent,
    /// <summary>移動元になるフォルダの配下の子孫フォルダに所持譜面があります。</summary>
    NestedChartFolder
}

/// <summary>理由に該当する一件です。ツールチップの並びは、フォルダ・相対パスの昇順（大文字小文字を無視）です。</summary>
/// <param name="Folder">該当するグループのフォルダ。フォルダに紐づかない場合は null です。</param>
/// <param name="RelativePath">フォルダからの相対パス。フォルダ自身は空です。親ディレクトリ参照では譜面の相対パスです。</param>
/// <param name="Detail">補足。親ディレクトリ参照では参照の原文、別のグループとの重なりと親子関係では相手のフォルダです。</param>
internal sealed record DuplicateMergeFinding(string Folder, string RelativePath, string Detail = null);

/// <summary>確認結果の一つの理由と、該当する一覧です。</summary>
internal sealed record DuplicateMergeVerificationReason(
    DuplicateMergeVerificationReasonKind Kind, IReadOnlyList<DuplicateMergeFinding> Findings)
{
    /// <summary>この理由が統合不可かです。</summary>
    internal bool IsUnmergeable => Kind <= DuplicateMergeVerificationReasonKind.Unreadable;
}

/// <summary>内容の比較方法です。</summary>
internal enum DuplicateMergeComparisonMethod
{
    /// <summary>サイズと更新日時の一致だけで、読まずに同一内容と判定しました。</summary>
    SizeAndTime,
    /// <summary>全量を読み、XXH3で比べました。</summary>
    Full,
    /// <summary>先頭・中央・末尾の区間を読み、XXH3で比べました。</summary>
    Partial
}

/// <summary>同一内容と判定した同名の資源の組の数を、比較方法ごとに持ちます。</summary>
internal sealed record DuplicateMergeComparisonSummary(int SizeAndTime, int Full, int Partial)
{
    /// <summary>比較していない結果です。</summary>
    internal static DuplicateMergeComparisonSummary None { get; } = new(0, 0, 0);

    /// <summary>組の合計です。</summary>
    internal int Total => SizeAndTime + Full + Partial;
}

/// <summary>グループごとの確認結果です。段階と理由の一覧を不変の値として持ちます。</summary>
/// <param name="Stage">段階。</param>
/// <param name="Reasons">同時に成立した理由。<see cref="DuplicateMergeVerificationReasonKind"/> の宣言順です。</param>
/// <param name="ProtectedNameCollisions">同じ相対パスの保護対象（.bmx・.pmx・.txt）が複数のフォルダにあり、別内容と判定された組がある場合の、その相対パスです。段階を変えない付記です。</param>
/// <param name="Comparisons">同一内容と判定した同名の資源の組の数です。安全の説明に使います。</param>
internal sealed record DuplicateMergeVerificationResult(
    DuplicateMergeVerificationStage Stage,
    IReadOnlyList<DuplicateMergeVerificationReason> Reasons,
    IReadOnlyList<string> ProtectedNameCollisions = null,
    DuplicateMergeComparisonSummary Comparisons = null)
{
    /// <summary>未確認の結果です。新しいグループの初期値です。</summary>
    internal static DuplicateMergeVerificationResult Unverified { get; } = new(DuplicateMergeVerificationStage.Unverified, []);

    /// <summary>登録範囲のフォルダが二つ以上あるため統合できない結果を作ります。</summary>
    internal static DuplicateMergeVerificationResult UnmergeableRegisteredRoot(IReadOnlyList<string> registeredFolders)
    {
        ArgumentNullException.ThrowIfNull(registeredFolders);
        return new(DuplicateMergeVerificationStage.Unmergeable,
        [
            new DuplicateMergeVerificationReason(
                DuplicateMergeVerificationReasonKind.RegisteredRoot,
                [.. registeredFolders.Select(folder => new DuplicateMergeFinding(folder, string.Empty))])
        ]);
    }

    /// <summary>別内容の保護対象の衝突（.bmx/.txtの衝突）があるかです。</summary>
    internal bool HasProtectedNameCollision => ProtectedNameCollisions is { Count: > 0 };
}
