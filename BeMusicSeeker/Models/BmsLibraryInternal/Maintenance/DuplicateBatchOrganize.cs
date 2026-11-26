using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 重複グループの一括整理で、一つのグループに対して行う処理です。
/// 移動元が一つ以上あれば、移動元を与えられた順に KEEP へ統合して KEEP 直下の重複を整理し、
/// 移動元がなければ（一フォルダのグループ）そのフォルダ直下の重複だけを整理します。
/// </summary>
internal sealed class DuplicateBatchOrganizeGroup
{
    /// <summary>グループの処理対象を固定します。</summary>
    /// <param name="keepDirectory">残すフォルダ。一フォルダのグループでは、整理するフォルダです。</param>
    /// <param name="sourceDirectories">KEEP へ統合する移動元フォルダ。PATH 昇順で渡します。0 件で一フォルダのグループです。</param>
    /// <param name="audioCollisionCleanup">WAV・OGG の衝突整理の指定。指定しないグループは null です。一フォルダのグループでは無視します。</param>
    internal DuplicateBatchOrganizeGroup(
        string keepDirectory, IEnumerable<string> sourceDirectories, DuplicateBatchAudioCollisionCleanup audioCollisionCleanup = null)
    {
        KeepDirectory = keepDirectory ?? throw new ArgumentNullException(nameof(keepDirectory));
        SourceDirectories = Array.AsReadOnly((sourceDirectories ?? []).ToArray());
        AudioCollisionCleanup = audioCollisionCleanup;
    }

    /// <summary>
    /// WAV・OGG の衝突整理の指定です。ないときは null です。
    /// 移動元を一つ以上統合できた二フォルダ以上のグループだけが、KEEP 直下の重複整理の後に使います。
    /// </summary>
    internal DuplicateBatchAudioCollisionCleanup AudioCollisionCleanup { get; }

    /// <summary>残すフォルダ。一フォルダのグループでは、整理するフォルダです。</summary>
    internal string KeepDirectory { get; }

    /// <summary>KEEP へ統合する移動元フォルダです。与えられた順に処理します。</summary>
    internal IReadOnlyList<string> SourceDirectories { get; }

    /// <summary>移動元がなく、フォルダ直下の重複整理だけを行うグループかどうかです。</summary>
    internal bool IsSingleFolder => SourceDirectories.Count == 0;
}

/// <summary>
/// グループごとの、WAV・OGG の衝突整理の指定です。KEEP で同名の WAV と OGG が両方とも実在する名前について、
/// 捨てる形式のファイルだけをごみ箱へ送ります。音源の復号・比較は行いません。対象の名前は同等と判定したものに限らず、
/// 利用者が確認文で、同等と判定されていない名前も一方だけが残ることを承認した前提です。
/// </summary>
internal sealed class DuplicateBatchAudioCollisionCleanup
{
    /// <summary>指定を固定します。</summary>
    /// <param name="discardExtension">捨てる形式の拡張子。大文字小文字と先頭のピリオドの有無を問わず、wav または ogg だけを受け付けます。</param>
    /// <param name="names">対象の名前。KEEP からの相対ディレクトリと拡張子なしの名前を <c>\</c> でつないだ文字列で、
    /// <see cref="DuplicateMergeVerificationResult.WavOggCollisionNames"/> と同じ書式です。大文字小文字を無視して重複を除きます。</param>
    /// <exception cref="ArgumentException">拡張子が wav・ogg でない場合、または名前が空・絶対パス・<c>..</c> を含む場合。呼出し側の誤りとして扱います。</exception>
    internal DuplicateBatchAudioCollisionCleanup(string discardExtension, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string normalized = (discardExtension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        DiscardExtension = normalized switch
        {
            "wav" => ".wav",
            "ogg" => ".ogg",
            _ => throw new ArgumentException("The discard extension must be .wav or .ogg.", nameof(discardExtension))
        };
        KeepExtension = DiscardExtension == ".wav" ? ".ogg" : ".wav";
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            if (!IsValidRelativeName(name))
            {
                throw new ArgumentException("A name must be a non-empty relative path without '..'.", nameof(names));
            }
            if (seen.Add(name))
            {
                distinct.Add(name);
            }
        }
        Names = distinct.AsReadOnly();
    }

    /// <summary>捨てる形式の拡張子です。小文字で、<c>.wav</c> または <c>.ogg</c> です。</summary>
    internal string DiscardExtension { get; }

    /// <summary>残す形式の拡張子です。小文字で、<see cref="DiscardExtension"/> のもう一方です。</summary>
    internal string KeepExtension { get; }

    /// <summary>対象の名前です。重複を除いた、与えられた順です。</summary>
    internal IReadOnlyList<string> Names { get; }

    private static bool IsValidRelativeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || System.IO.Path.IsPathRooted(name) || name.StartsWith('\\') || name.StartsWith('/'))
        {
            return false;
        }
        return !name.Split('\\', '/').Any(segment => segment == "..");
    }
}

/// <summary>重複グループの一括整理の要求です。グループの順序は処理順です。</summary>
internal sealed class DuplicateBatchOrganizeRequest
{
    /// <summary>要求を固定します。</summary>
    /// <param name="groups">処理するグループ。入力列はコピーします。</param>
    internal DuplicateBatchOrganizeRequest(IEnumerable<DuplicateBatchOrganizeGroup> groups)
    {
        Groups = Array.AsReadOnly((groups ?? throw new ArgumentNullException(nameof(groups)))
            .Select(group => group ?? throw new ArgumentException("A group must not be null.", nameof(groups)))
            .ToArray());
    }

    /// <summary>処理するグループです。</summary>
    internal IReadOnlyList<DuplicateBatchOrganizeGroup> Groups { get; }
}

/// <summary>進捗の種類です。</summary>
internal enum DuplicateBatchOrganizeProgressKind
{
    /// <summary>グループの物理処理を開始します。</summary>
    GroupStarted,

    /// <summary>
    /// 物理処理のループを終え、確定（カタログ・DB・索引・LR2同期・通知への反映）と必須保守へ進みます。
    /// 操作を受理して処理を始めた場合に、取消や停止の後でも一回だけ、ロックの外で通知します（グループが0件の要求は処理を始めないため通知しません）。この後は取消の対象になりません。
    /// </summary>
    Applying
}

/// <summary>グループの開始と、反映段階への移行を受け手へ知らせる進捗です。</summary>
/// <param name="Number">グループの開始では何番目のグループか（1 始まり）。反映段階では、開始したグループの数です。</param>
/// <param name="Total">グループの総数。</param>
/// <param name="KeepDirectory">開始するグループの KEEP です。反映段階では null です。</param>
/// <param name="Kind">進捗の種類。省略時はグループの開始です。</param>
internal readonly record struct DuplicateBatchOrganizeProgress(
    int Number, int Total, string KeepDirectory, DuplicateBatchOrganizeProgressKind Kind = DuplicateBatchOrganizeProgressKind.GroupStarted);

/// <summary>グループの処理結果の状態です。</summary>
internal enum DuplicateBatchOrganizeGroupStatus
{
    /// <summary>
    /// 移動元をすべて統合でき、重複整理の削除と衝突整理の音源の削除にも失敗がありません。
    /// 一フォルダのグループは、削除に失敗がなければ、削除対象が 0 件でも完了です。
    /// </summary>
    Completed,

    /// <summary>
    /// 統合できた移動元が一つ以上あり、残った移動元があります。または、統合はすべてできたが、重複整理の個別の削除、または衝突整理の音源の個別の削除に失敗がありました。
    /// 一フォルダのグループで個別の削除に失敗があった場合も一部完了です。
    /// </summary>
    PartiallyCompleted,

    /// <summary>物理変更の前に拒否し、ファイル・DB・登録を変えませんでした。理由は <see cref="DuplicateBatchOrganizeSkipReason"/> です。</summary>
    Skipped,

    /// <summary>
    /// 二フォルダ以上のグループで、移動元を一つも統合できませんでした。ごみ箱へ送った数は別に報告します。
    /// 確定に失敗した操作では、物理的に成功していたグループも失敗です。
    /// 一フォルダのグループも、予期しない物理失敗で止まり、何も削除できなかった場合は失敗です。
    /// </summary>
    Failed,

    /// <summary>取消、または予期しない物理失敗による停止で、開始しなかったグループです。ファイルとカタログは変えていません。</summary>
    Unprocessed
}

/// <summary>グループを物理変更の前に拒否した理由の分類です。</summary>
internal enum DuplicateBatchOrganizeSkipReason
{
    /// <summary>拒否していません。</summary>
    None,

    /// <summary>手動統合と同じ検査でリンク（シンボリックリンク・ジャンクション）が見つかりました。一フォルダのグループでは、フォルダ本体がリンクの場合です。</summary>
    LinkFound,

    /// <summary>リンクの有無を確認できませんでした。</summary>
    LinkCheckFailed,

    /// <summary>移動元が、登録 BMS ルート本体、または空の登録子ルートを含む祖先です。</summary>
    RegisteredRoot,

    /// <summary>同じグループのフォルダ同士が、同一または親子です。</summary>
    NestedFolders,

    /// <summary>二フォルダ以上のグループのフォルダが、要求内の別のグループのフォルダと同一または親子です。</summary>
    OverlapsOtherGroup,

    /// <summary>一フォルダのグループのフォルダが存在しません。</summary>
    FolderMissing,

    /// <summary>フォルダのパスが空、または正規化できません。</summary>
    InvalidPath
}

/// <summary>統合できなかった移動元が、そのまま残った理由の分類です。</summary>
internal enum DuplicateBatchOrganizeSourceReason
{
    /// <summary>移動元が存在しません。</summary>
    SourceMissing,

    /// <summary>KEEP（統合先）が存在しません。</summary>
    DestinationMissing,

    /// <summary>同じ相対パスがファイルとディレクトリで衝突し、既存の統合の事前確認が拒否しました。</summary>
    TypeConflict,

    /// <summary>移動元の配下に所持譜面がありません。</summary>
    NoCharts,

    /// <summary>上記以外の、変更の開始前に確定した拒否です。</summary>
    PreflightRefused,

    /// <summary>この移動元の物理処理が予期せず失敗し、以降の処理を止めました。</summary>
    PhysicalFailure,

    /// <summary>物理処理の失敗または取消の前に、処理を始めませんでした。</summary>
    NotProcessed,

    /// <summary>物理的には統合しましたが、確定に失敗したため、結果として統合した扱いにしません。</summary>
    NotCommitted,

    /// <summary>
    /// 「既に所持」と記録された同じ譜面の実ファイルを所持側に確認できないため、移動元の最後のコピーを守るために見送りました。
    /// 該当する移動元側の譜面は <see cref="DuplicateBatchOrganizeRetainedSource.Detail"/> に入ります。
    /// </summary>
    OwnedFileMissing
}

/// <summary>統合せずに残した移動元と、その理由です。</summary>
/// <param name="Directory">残した移動元。</param>
/// <param name="Reason">理由の分類。文言ではなく分類で比べます。</param>
/// <param name="Detail">理由に添える補足のパス。<see cref="DuplicateBatchOrganizeSourceReason.OwnedFileMissing"/> では、実ファイルの裏付けが取れなかった移動元側の譜面です。ないときは null です。</param>
internal sealed record DuplicateBatchOrganizeRetainedSource(string Directory, DuplicateBatchOrganizeSourceReason Reason, string Detail = null);

/// <summary>一つのグループの処理結果です。要求と同じ順に <see cref="DuplicateBatchOrganizeResult.Groups"/> へ並びます。</summary>
internal sealed class DuplicateBatchOrganizeGroupResult
{
    internal DuplicateBatchOrganizeGroupResult(
        string keepDirectory,
        bool isSingleFolder,
        DuplicateBatchOrganizeGroupStatus status,
        DuplicateBatchOrganizeSkipReason skipReason,
        string skipPath,
        IEnumerable<string> mergedSources,
        IEnumerable<DuplicateBatchOrganizeRetainedSource> retainedSources,
        int trashedChartCount,
        int trashFailureCount,
        int trashedAudioCount = 0,
        int audioTrashFailureCount = 0)
    {
        KeepDirectory = keepDirectory;
        IsSingleFolder = isSingleFolder;
        Status = status;
        SkipReason = skipReason;
        SkipPath = skipPath;
        MergedSources = Array.AsReadOnly((mergedSources ?? []).ToArray());
        RetainedSources = Array.AsReadOnly((retainedSources ?? []).ToArray());
        TrashedChartCount = trashedChartCount;
        TrashFailureCount = trashFailureCount;
        TrashedAudioCount = trashedAudioCount;
        AudioTrashFailureCount = audioTrashFailureCount;
    }

    /// <summary>残すフォルダ。一フォルダのグループでは、整理したフォルダです。</summary>
    internal string KeepDirectory { get; }

    /// <summary>移動元がなく、フォルダ直下の重複整理だけを行うグループかどうかです。</summary>
    internal bool IsSingleFolder { get; }

    /// <summary>グループの状態です。</summary>
    internal DuplicateBatchOrganizeGroupStatus Status { get; }

    /// <summary><see cref="DuplicateBatchOrganizeGroupStatus.Skipped"/> の理由です。それ以外は <see cref="DuplicateBatchOrganizeSkipReason.None"/> です。</summary>
    internal DuplicateBatchOrganizeSkipReason SkipReason { get; }

    /// <summary>拒否の原因になったパス（リンクの位置など）です。ないときは null です。</summary>
    internal string SkipPath { get; }

    /// <summary>KEEP へ統合した移動元です。確定に失敗した操作では空です。</summary>
    internal IReadOnlyList<string> MergedSources { get; }

    /// <summary>統合せずに残した移動元とその理由です。</summary>
    internal IReadOnlyList<DuplicateBatchOrganizeRetainedSource> RetainedSources { get; }

    /// <summary>重複整理でごみ箱へ送れた譜面の数です。削除の成功を確認した実数で、確定に失敗しても変わりません。</summary>
    internal int TrashedChartCount { get; }

    /// <summary>重複整理でごみ箱へ送れなかった譜面の数です。送れなかった譜面は残します。</summary>
    internal int TrashFailureCount { get; }

    /// <summary>衝突整理でごみ箱へ送れた音源の数です。削除の成功を確認した実数で、確定に失敗しても変わりません。</summary>
    internal int TrashedAudioCount { get; }

    /// <summary>衝突整理でごみ箱へ送れなかった音源の数です。送れなかった音源は残します。</summary>
    internal int AudioTrashFailureCount { get; }
}

/// <summary>重複グループの一括整理の結果です。</summary>
internal sealed class DuplicateBatchOrganizeResult
{
    internal DuplicateBatchOrganizeResult(
        DuplicateMergeMaintenanceReceipt receipt,
        IEnumerable<DuplicateBatchOrganizeGroupResult> groups,
        bool canceled,
        bool admitted)
    {
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        Groups = Array.AsReadOnly((groups ?? []).ToArray());
        Canceled = canceled;
        Admitted = admitted;
    }

    /// <summary>
    /// 操作全体の受取りです。既存の統合と同じ、変更セッションの終端と必須保守の結果を持ちます。
    /// 操作全体を拒否した場合は、何も適用していない受取りです。
    /// </summary>
    internal DuplicateMergeMaintenanceReceipt Receipt { get; }

    /// <summary>要求と同じ順のグループ結果です。</summary>
    internal IReadOnlyList<DuplicateBatchOrganizeGroupResult> Groups { get; }

    /// <summary>取消の合図によって、残りのグループを処理しなかったかどうかです。取消は失敗として扱いません。</summary>
    internal bool Canceled { get; }

    /// <summary>
    /// 共通受付を取得して処理を始めたかどうかです。false は、Busy またはカタログ未収束による操作全体の拒否で、
    /// 全グループが <see cref="DuplicateBatchOrganizeGroupStatus.Unprocessed"/> です。拒否の案内は呼出元の収集先へ既に加えています。
    /// </summary>
    internal bool Admitted { get; }

    /// <summary>操作全体を拒否した結果を、要求と同じ長さの全て未処理のグループ結果で作ります。</summary>
    internal static DuplicateBatchOrganizeResult NotAdmitted(DuplicateBatchOrganizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new DuplicateBatchOrganizeResult(
            DuplicateMergeMaintenanceReceipt.NotApplied,
            request.Groups.Select(group => new DuplicateBatchOrganizeGroupResult(
                group.KeepDirectory,
                group.IsSingleFolder,
                DuplicateBatchOrganizeGroupStatus.Unprocessed,
                DuplicateBatchOrganizeSkipReason.None,
                null,
                [],
                [],
                0,
                0)),
            canceled: false,
            admitted: false);
    }
}
