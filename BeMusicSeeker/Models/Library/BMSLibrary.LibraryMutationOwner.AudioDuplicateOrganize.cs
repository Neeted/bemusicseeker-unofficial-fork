using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

internal sealed partial class LibraryMutationOwner
{
    /// <summary>
    /// 重複音源フォルダを、一つの共通受付でまとめて整理します。フォルダごとに、同名の WAV と OGG が両方ともファイルとして実在する名前の、捨てる形式のファイルを、要求の削除方法（ごみ箱へ移動、または完全に削除）で削除します。
    /// 音源キーの集合は変わらないため、変更セッションを開かず、カタログ・DB・索引・LR2同期への反映と必須保守を行いません。
    /// </summary>
    /// <remarks>
    /// Busy・カタログ未収束・管理出力（プレイリスト出力）の保護を取れない場合は、物理変更の前に操作全体を未適用で返し、全フォルダを未処理にします。
    /// 管理出力の保護は、要求の全フォルダの和集合で、最初の物理変更の前に一回だけ取ります。
    /// 名前ごとの観測と削除は、重複グループの一括整理の衝突整理と同じ部品（<see cref="DuplicateAudioCollisionCleaner"/>）を使い、個別の失敗は削除できなかった数に数えて続行します。
    /// この部品が受けない例外は、結果を返さずに呼出し側へ伝え、<paramref name="publishResult"/> を呼びません。
    /// </remarks>
    /// <param name="request">処理するフォルダ。順序が処理順です。</param>
    /// <param name="operationId">診断識別子。</param>
    /// <param name="notices">この操作の案内を集める入れ物。カタログが収束していない場合の警告と、管理出力の保護を取れない案内を加え、呼出元が受付の解放後に表示します。</param>
    /// <param name="progress">取消の確認の後、フォルダの開始ごとに同期で呼ぶ進捗の受け手。待たずに戻る前提で、例外は結果を変えません。モデルのロックは保持しません。</param>
    /// <param name="cancellationToken">取消の合図。フォルダの開始前にだけ確認し、処理中のフォルダは最後まで進めます。</param>
    /// <param name="publishResult">
    /// 全フォルダの処理の後、受付を保持している間に結果を渡す処理です。保持している探索の結果の更新に使い、受付の解放後に呼ぶ通知を返します（通知がなければ null）。
    /// 操作全体を未適用にした場合は呼びません。
    /// </param>
    /// <param name="capability">同じ受付の生存権限。省略時は新規の非待機受付を取得し、結果の更新・後片付けまで保持します。</param>
    internal AudioDuplicateOrganizeResult OrganizeAudioDuplicateFolders(
        AudioDuplicateOrganizeRequest request,
        long operationId,
        LibraryOperationNotices notices,
        Action<AudioDuplicateOrganizeProgress> progress,
        CancellationToken cancellationToken,
        Func<AudioDuplicateOrganizeResult, Action> publishResult = null,
        LibraryFileMutationCapability capability = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(notices);
        if (request.Folders.Count == 0)
        {
            return new AudioDuplicateOrganizeResult([], canceled: false, admitted: true);
        }
        if (TryBlockCatalogMutation(nameof(BMSLibrary.OrganizeAudioDuplicateFolders), notices, capability: capability))
        {
            return AudioDuplicateOrganizeResult.NotAdmitted(request);
        }

        var totalStopwatch = Stopwatch.StartNew();
        LogInstallPerformance("audio_duplicate_organize start op=" + operationId + " folders=" + request.Folders.Count + " deleteMethod=" + request.DeleteMethod);
        List<Action> postLeaseNotifications = [];
        AudioDuplicateOrganizeResult result;
        long[] cleanupMilliseconds;
        using (LibraryFileMutationLease mutationLease = synchronization.EnterMergeWriteScope(notices, capability))
        {
            if (mutationLease == null)
            {
                return AudioDuplicateOrganizeResult.NotAdmitted(request);
            }
            string[] protectedPaths = [.. request.Folders
                .Select(folder => folder.Folder)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (!TryEnterManagedOutputMutation(protectedPaths, recursive: true, notices, out LibraryFileMutationLease playlistLease, capability))
            {
                return AudioDuplicateOrganizeResult.NotAdmitted(request);
            }
            using LibraryFileMutationLease playlistOperation = playlistLease;
            (result, cleanupMilliseconds) = RunAudioDuplicateOrganize(request, progress, cancellationToken);
            Action publication = publishResult?.Invoke(result);
            if (publication != null)
            {
                postLeaseNotifications.Add(publication);
            }
        }

        postLeaseNotifications.Add(() => LogAudioDuplicateOrganizeResults(operationId, request, result, cleanupMilliseconds));
        InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        LogInstallPerformance("audio_duplicate_organize done op=" + operationId
            + " folders=" + request.Folders.Count
            + " canceled=" + result.Canceled
            + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>受付と管理出力の保護を保持した状態で、フォルダを要求の順に処理します。取消はフォルダの開始前にだけ確認します。</summary>
    private (AudioDuplicateOrganizeResult Result, long[] CleanupMilliseconds) RunAudioDuplicateOrganize(
        AudioDuplicateOrganizeRequest request, Action<AudioDuplicateOrganizeProgress> progress, CancellationToken cancellationToken)
    {
        int total = request.Folders.Count;
        var results = new AudioDuplicateOrganizeFolderResult[total];
        long[] cleanupMilliseconds = new long[total];
        bool canceled = false;
        for (int index = 0; index < total; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            AudioDuplicateOrganizeFolder folder = request.Folders[index];
            ReportAudioDuplicateOrganizeProgress(progress, new AudioDuplicateOrganizeProgress(index + 1, total, folder.Folder));
            // 部品が受けない例外は、結果を返さずに呼出し側へ伝えます。
            results[index] = OrganizeAudioDuplicateFolder(folder, request.DeleteMethod, out cleanupMilliseconds[index]);
        }
        for (int index = 0; index < total; index++)
        {
            results[index] ??= AudioDuplicateOrganizeFolderResult.CreateUnprocessed(request.Folders[index].Folder);
        }
        return (new AudioDuplicateOrganizeResult(results, canceled, admitted: true), cleanupMilliseconds);
    }

    private void ReportAudioDuplicateOrganizeProgress(Action<AudioDuplicateOrganizeProgress> progress, AudioDuplicateOrganizeProgress value)
    {
        if (progress == null)
        {
            return;
        }
        try
        {
            progress(value);
        }
        catch (Exception exception)
        {
            // 受け手の失敗で、整理の結果を変えません。
            try { logFileWarning(exception, "audio_duplicate_organize_progress_failed"); }
            catch (Exception) { /* 診断の失敗は握りつぶします。 */ }
        }
    }

    /// <summary>
    /// 一つのフォルダを処理します。パスが空・正規化できない、存在しない、本体がリンクまたはリンクを確認できない場合は、ファイルを変えずにスキップします。
    /// </summary>
    private AudioDuplicateOrganizeFolderResult OrganizeAudioDuplicateFolder(AudioDuplicateOrganizeFolder folder, DuplicateOrganizeDeleteMethod deleteMethod, out long cleanupMilliseconds)
    {
        cleanupMilliseconds = 0;
        string path = folder.Folder;
        if (Lr2FolderPath.NormalizeDirectoryPath(path) == null)
        {
            return SkipAudioDuplicateFolder(folder, DuplicateBatchOrganizeSkipReason.InvalidPath, null);
        }
        if (!LongPathFileSystem.DirectoryExists(path))
        {
            return SkipAudioDuplicateFolder(folder, DuplicateBatchOrganizeSkipReason.FolderMissing, path);
        }
        FolderLinkInspection inspection = FolderLinkInspector.InspectFolder(path);
        if (inspection.Outcome != FolderLinkInspectionOutcome.Clear)
        {
            return SkipAudioDuplicateFolder(
                folder,
                inspection.Outcome == FolderLinkInspectionOutcome.LinkFound
                    ? DuplicateBatchOrganizeSkipReason.LinkFound
                    : DuplicateBatchOrganizeSkipReason.LinkCheckFailed,
                inspection.Path);
        }

        DuplicateAudioCollisionCleanupOutcome outcome = DuplicateAudioCollisionCleaner.Cleanup(
            path,
            folder.Cleanup,
            deleteMethod,
            fileMutationService,
            targetOnlyFileMutationOptions,
            logFileWarning,
            "audio_duplicate_organize_delete_failed");
        cleanupMilliseconds = outcome.Milliseconds;
        return new AudioDuplicateOrganizeFolderResult(
            path,
            outcome.FailureCount == 0 ? AudioDuplicateOrganizeStatus.Completed : AudioDuplicateOrganizeStatus.PartiallyCompleted,
            DuplicateBatchOrganizeSkipReason.None,
            null,
            outcome.DeletedCount,
            outcome.FailedNames);
    }

    private static AudioDuplicateOrganizeFolderResult SkipAudioDuplicateFolder(
        AudioDuplicateOrganizeFolder folder, DuplicateBatchOrganizeSkipReason reason, string skipPath)
        => new(folder.Folder, AudioDuplicateOrganizeStatus.Skipped, reason, skipPath, 0, []);

    /// <summary>フォルダごとの結果を診断へ記録します。受付の解放後に呼びます。</summary>
    private void LogAudioDuplicateOrganizeResults(
        long operationId, AudioDuplicateOrganizeRequest request, AudioDuplicateOrganizeResult result, long[] cleanupMilliseconds)
    {
        for (int index = 0; index < result.Folders.Count; index++)
        {
            AudioDuplicateOrganizeFolderResult folder = result.Folders[index];
            LogInstallPerformance("audio_duplicate_organize folder op=" + operationId
                + " no=" + (index + 1)
                + " folder=" + folder.Folder
                + " names=" + request.Folders[index].Cleanup.Names.Count
                + " status=" + folder.Status
                + (folder.SkipReason == DuplicateBatchOrganizeSkipReason.None ? string.Empty : " skipReason=" + folder.SkipReason + " skipPath=" + folder.SkipPath)
                + " deleted=" + folder.DeletedCount
                + " failed=" + folder.FailureCount
                + " ms=" + cleanupMilliseconds[index]);
        }
    }
}
