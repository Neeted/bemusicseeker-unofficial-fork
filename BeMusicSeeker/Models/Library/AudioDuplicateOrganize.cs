using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

/// <summary>重複音源の整理で、一つのフォルダに対して行う処理の指定です。</summary>
internal sealed class AudioDuplicateOrganizeFolder
{
    /// <summary>指定を固定します。</summary>
    /// <param name="folder">整理する対象フォルダ。</param>
    /// <param name="discardExtension">捨てる形式の拡張子。大文字小文字と先頭のピリオドの有無を問わず、wav または ogg だけを受け付けます。</param>
    /// <param name="collisionNames">対象の衝突名。対象フォルダからの相対ディレクトリと拡張子なしの名前を <c>\</c> でつないだ文字列で、
    /// 確認が実フォルダから確定した一覧です。大文字小文字を無視して重複を除きます。</param>
    /// <exception cref="ArgumentException">拡張子が wav・ogg でない場合、または名前が空・絶対パス・<c>..</c> を含む場合。呼出し側の誤りとして扱います。</exception>
    internal AudioDuplicateOrganizeFolder(string folder, string discardExtension, IEnumerable<string> collisionNames)
    {
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        Cleanup = new DuplicateBatchAudioCollisionCleanup(discardExtension, collisionNames);
    }

    /// <summary>整理する対象フォルダ。</summary>
    internal string Folder { get; }

    /// <summary>捨てる形式と対象の衝突名。重複グループの一括整理の衝突整理と同じ指定です。</summary>
    internal DuplicateBatchAudioCollisionCleanup Cleanup { get; }
}

/// <summary>重複音源の整理の要求です。フォルダの順序は処理順です。</summary>
internal sealed class AudioDuplicateOrganizeRequest
{
    /// <summary>要求を固定します。入力列はコピーします。</summary>
    /// <param name="folders">処理するフォルダ。</param>
    internal AudioDuplicateOrganizeRequest(IEnumerable<AudioDuplicateOrganizeFolder> folders)
    {
        Folders = Array.AsReadOnly((folders ?? throw new ArgumentNullException(nameof(folders)))
            .Select(folder => folder ?? throw new ArgumentException("A folder must not be null.", nameof(folders)))
            .ToArray());
    }

    /// <summary>処理するフォルダです。</summary>
    internal IReadOnlyList<AudioDuplicateOrganizeFolder> Folders { get; }
}

/// <summary>フォルダの開始を受け手へ知らせる進捗です。取消の確認の後、フォルダの開始ごとに一回渡します。</summary>
/// <param name="Number">何番目のフォルダか（1 始まり）。</param>
/// <param name="Total">フォルダの総数。</param>
/// <param name="Folder">開始するフォルダ。</param>
internal readonly record struct AudioDuplicateOrganizeProgress(int Number, int Total, string Folder);

/// <summary>重複音源の整理の、フォルダごとの状態です。</summary>
internal enum AudioDuplicateOrganizeStatus
{
    /// <summary>送れなかった音源（削除の失敗と、実在を確認できなかった名前）がありません。送った数が0件でも完了です。</summary>
    Completed,

    /// <summary>送れなかった音源が一件以上あります。</summary>
    PartiallyCompleted,

    /// <summary>書込み前に拒否し、ファイルを変えませんでした。理由は <see cref="AudioDuplicateOrganizeFolderResult.SkipReason"/> です。</summary>
    Skipped,

    /// <summary>取消、または操作全体の未適用で、開始しなかったフォルダです。ファイルを変えていません。</summary>
    Unprocessed
}

/// <summary>一つのフォルダの整理結果です。要求と同じ順に <see cref="AudioDuplicateOrganizeResult.Folders"/> へ並びます。</summary>
/// <param name="Folder">要求のフォルダ。</param>
/// <param name="Status">状態。</param>
/// <param name="SkipReason">
/// <see cref="AudioDuplicateOrganizeStatus.Skipped"/> の理由。リンクは <see cref="DuplicateBatchOrganizeSkipReason.LinkFound"/>、確認できないときは <see cref="DuplicateBatchOrganizeSkipReason.LinkCheckFailed"/>、
/// 存在しないときは <see cref="DuplicateBatchOrganizeSkipReason.FolderMissing"/>、パスが空・正規化できないときは <see cref="DuplicateBatchOrganizeSkipReason.InvalidPath"/> です。それ以外は <see cref="DuplicateBatchOrganizeSkipReason.None"/> です。
/// </param>
/// <param name="SkipPath">拒否の原因になったパス（リンクの位置など）。ないときは null です。</param>
/// <param name="TrashedCount">ごみ箱へ送れた音源の数。</param>
/// <param name="UnsentNames">送れなかった名前（実在を確認できなかった名前と、削除に失敗した名前）。要求の順で、その形式のファイルは残っています。</param>
internal sealed record AudioDuplicateOrganizeFolderResult(
    string Folder,
    AudioDuplicateOrganizeStatus Status,
    DuplicateBatchOrganizeSkipReason SkipReason,
    string SkipPath,
    int TrashedCount,
    IReadOnlyList<string> UnsentNames)
{
    /// <summary>送れなかった音源の数。</summary>
    internal int FailureCount => UnsentNames.Count;

    /// <summary>開始しなかったフォルダの結果を作ります。</summary>
    internal static AudioDuplicateOrganizeFolderResult CreateUnprocessed(string folder)
        => new(folder, AudioDuplicateOrganizeStatus.Unprocessed, DuplicateBatchOrganizeSkipReason.None, null, 0, []);
}

/// <summary>重複音源の整理の結果です。</summary>
internal sealed class AudioDuplicateOrganizeResult
{
    internal AudioDuplicateOrganizeResult(IEnumerable<AudioDuplicateOrganizeFolderResult> folders, bool canceled, bool admitted)
    {
        Folders = Array.AsReadOnly((folders ?? []).ToArray());
        Canceled = canceled;
        Admitted = admitted;
    }

    /// <summary>要求と同じ順のフォルダ結果です。</summary>
    internal IReadOnlyList<AudioDuplicateOrganizeFolderResult> Folders { get; }

    /// <summary>取消の合図によって、残りのフォルダを処理しなかったかどうかです。取消は失敗として扱いません。</summary>
    internal bool Canceled { get; }

    /// <summary>
    /// 共通受付と管理出力の保護を取って処理を始めたかどうかです。false は、Busy・カタログ未収束・管理出力の保護を取れなかったことによる操作全体の未適用で、
    /// 全フォルダが <see cref="AudioDuplicateOrganizeStatus.Unprocessed"/> です。管理出力の保護を取れなかった案内は、呼出元の収集先へ既に加えています。
    /// </summary>
    internal bool Admitted { get; }

    /// <summary>操作全体を未適用にした結果を、要求と同じ長さの全て未処理のフォルダ結果で作ります。</summary>
    internal static AudioDuplicateOrganizeResult NotAdmitted(AudioDuplicateOrganizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AudioDuplicateOrganizeResult(
            request.Folders.Select(folder => AudioDuplicateOrganizeFolderResult.CreateUnprocessed(folder.Folder)),
            canceled: false,
            admitted: false);
    }
}
