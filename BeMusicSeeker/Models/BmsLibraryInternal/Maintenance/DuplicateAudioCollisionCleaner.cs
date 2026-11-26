using System;
using System.Collections.Generic;
using System.Diagnostics;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>衝突整理の一フォルダ分の結果です。</summary>
/// <param name="TrashedCount">ごみ箱へ送れた音源の数です。</param>
/// <param name="FailedNames">送れなかった名前（実在を確認できなかった名前と、削除に失敗した名前）です。要求の順です。</param>
/// <param name="Milliseconds">要した時間（ミリ秒）です。診断のログだけに使います。</param>
internal sealed record DuplicateAudioCollisionCleanupOutcome(int TrashedCount, IReadOnlyList<string> FailedNames, long Milliseconds)
{
    /// <summary>送れなかった音源の数です。</summary>
    internal int FailureCount => FailedNames.Count;
}

/// <summary>
/// 一つのフォルダで、同名の WAV と OGG が両方ともファイルとして実在する名前の、捨てる形式のファイルをごみ箱へ送る部品です。
/// 重複グループの一括整理の衝突整理と、重複音源の整理が共用し、観測の三分類・リンクの扱い・削除の窓口を一つに保ちます。
/// 音源を復号・比較・読取りし直しません。
/// </summary>
/// <remarks>
/// 名前ごとの失敗（パスの組立て・実在の確認・削除）は停止の理由にせず、そのファイルを残して数えます。
/// 失敗を外へ出すと、呼出し側の予期しない失敗の経路が誤って記録するため、プロセスの継続に関わる <see cref="OutOfMemoryException"/> 以外はここで受けます。
/// 音源キーの集合は変わらないため、変更セッションへ事実を加えず、索引・通知へ追加の反映をしません。
/// </remarks>
internal static class DuplicateAudioCollisionCleaner
{
    /// <summary>
    /// 指定の名前ごとに、残す形式、捨てる形式の順に実在を確かめ、両方がファイルとして実在する名前だけ、捨てる形式をごみ箱へ送ります。
    /// ファイルは一件ずつ、譜面の削除と同じ窓口・設定で送ります。
    /// </summary>
    /// <param name="folder">名前の基準になるフォルダ。</param>
    /// <param name="cleanup">捨てる形式と対象の名前。</param>
    /// <param name="fileMutationService">削除の窓口。</param>
    /// <param name="fileMutationOptions">削除の設定。</param>
    /// <param name="logWarning">名前ごとの失敗を記録する処理。診断の失敗は握りつぶします。</param>
    /// <param name="failureLogKey">失敗の記録の識別子。</param>
    internal static DuplicateAudioCollisionCleanupOutcome Cleanup(
        string folder,
        DuplicateBatchAudioCollisionCleanup cleanup,
        IFileMutationService fileMutationService,
        FileMutationOptions fileMutationOptions,
        Action<Exception, string> logWarning,
        string failureLogKey)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        var stopwatch = Stopwatch.StartNew();
        int trashed = 0;
        List<string> failed = [];
        foreach (string name in cleanup.Names)
        {
            try
            {
                string keepPath = System.IO.Path.Combine(folder, name + cleanup.KeepExtension);
                string discardPath = System.IO.Path.Combine(folder, name + cleanup.DiscardExtension);
                // 指定を作った後にファイルが変わっていても、残す形式が実在しない名前の音源を失わないよう、残す形式、捨てる形式の順に実在を確かめます。
                // 確認できなかった名前は例外のまま下で失敗として数え、削除を試みません。
                if (!ObserveAudioFileExists(keepPath) || !ObserveAudioFileExists(discardPath))
                {
                    continue;
                }
                fileMutationService.DeleteFileShell(
                    discardPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    fileMutationOptions);
                trashed++;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failed.Add(name);
                try { logWarning?.Invoke(exception, failureLogKey); }
                catch (Exception) { /* 診断の失敗は握りつぶします。 */ }
            }
        }
        return new DuplicateAudioCollisionCleanupOutcome(trashed, failed, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// 音源のパスが、ファイルとして実在するかを属性の取得で観測します。内容は読みません。
    /// ファイル・途中の親ディレクトリがない対象とディレクトリは「存在しない」として false を返します。
    /// 属性を取得できてディレクトリでない対象は true です。リンクはリンク先をたどらず、リンク自身の属性で判断するため、リンク先の有無によらずファイルとして存在すると扱います。
    /// </summary>
    /// <exception cref="Exception">属性の取得が、存在しない以外の理由（アクセス拒否・入出力の失敗・パスとして扱えない名前など）で失敗したとき。呼出し側が失敗として数えます。</exception>
    private static bool ObserveAudioFileExists(string path)
    {
        System.IO.FileAttributes attributes;
        try
        {
            attributes = LongPathFileSystem.GetAttributes(path);
        }
        catch (Exception exception) when (LongPathFileSystem.IsNotFoundFailure(exception))
        {
            return false;
        }
        return (attributes & System.IO.FileAttributes.Directory) == 0;
    }
}
