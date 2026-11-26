using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 一括整理の確認文・進捗の代表名・結果の案内を、読取りのたびに現在の表示言語で組み立てます。
/// 文言はすべて表示リソースから作り、モデルの分類値（状態・理由）ごとに文言を対応させます。
/// </summary>
internal static class DuplicateBatchOrganizePresentation
{
    /// <summary>結果の詳細に出す、完了以外のグループの最大件数です。超える分は「ほか{n}グループ…」に省略します。</summary>
    internal const int MaxDetailGroups = 20;

    /// <summary>
    /// 捕捉した対象から、モデルの要求を作ります。グループの順は渡された順（一覧の表示順）で、
    /// 各グループはKEEPと、KEEP以外のフォルダをPATH昇順（大文字小文字を無視）に並べた移動元を持ちます。一フォルダのグループの移動元は空です。
    /// </summary>
    internal static DuplicateBatchOrganizeRequest CreateRequest(IReadOnlyList<DuplicateMergeCandidateGroupTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return new DuplicateBatchOrganizeRequest(targets.Select(target =>
        {
            string keep = target.KeepFolder ?? string.Empty;
            return new DuplicateBatchOrganizeGroup(
                keep,
                target.Folders
                    .Where(folder => !string.Equals(folder, target.KeepFolder, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase),
                CreateAudioCollisionCleanup(target));
        }));
    }

    /// <summary>
    /// 捕捉した対象から、WAV・OGGの衝突整理の指定を作ります。指定するのは、捕捉時点の「衝突整理」が両方残す以外で、二フォルダ以上あり、
    /// 捕捉時点の確認結果がWAV・OGGの衝突名を一つ以上持つグループです（段階と、同等性検査のオン・オフは問いません）。
    /// 名前は衝突名のすべてで、同等と判定していない名前も含みます。それ以外はnullで、両方を残します。
    /// </summary>
    private static DuplicateBatchAudioCollisionCleanup CreateAudioCollisionCleanup(DuplicateMergeCandidateGroupTarget target)
    {
        if (target.CollisionCleanup == DuplicateMergeCollisionCleanupMode.KeepBoth
            || !target.Folders.Any(folder => !string.Equals(folder, target.KeepFolder, StringComparison.OrdinalIgnoreCase))
            || target.Verification is not { WavOggCollisionNames: { Count: > 0 } names })
        {
            return null;
        }
        return new DuplicateBatchAudioCollisionCleanup(
            target.CollisionCleanup == DuplicateMergeCollisionCleanupMode.KeepOgg ? ".wav" : ".ogg", names);
    }

    /// <summary>進捗に出す代表名です。KEEPのフォルダ名（一覧のFOLDER列と同じ末尾の名前）です。</summary>
    internal static string GetRepresentativeFolderName(string keepDirectory)
    {
        string keep = (keepDirectory ?? string.Empty).TrimEnd('\\', '/');
        string name = System.IO.Path.GetFileName(keep);
        return string.IsNullOrEmpty(name) ? keep : name;
    }

    /// <summary>進捗の「何番目／総数」とKEEPのフォルダ名の文言です。</summary>
    internal static string CreateGroupProgressMessage(DuplicateBatchOrganizeProgress progress)
        => string.Format(
            Resources.Duplicate_batch_organize_progress_group_format,
            progress.Number,
            progress.Total,
            GetRepresentativeFolderName(progress.KeepDirectory));

    /// <summary>
    /// 確認文を作ります。通常の本文は、グループ数の行、移動元を統合するグループがあるときだけ統合の行、ごみ箱の行、
    /// 衝突整理を指定するグループがあるときだけ残す形式ごとの衝突整理の行の順です。
    /// 警告用の本文（警告色で表示する部分）は、未確認・要注意のグループがあるときだけ0件でない側の件数の行（このとき警告のアイコン）、
    /// 衝突整理の指定の名前に同等と判定していない名前を含むグループがあるときだけその件数の行、一方だけが残る衝突（別内容ファイルの衝突、または連番名で保持されない.bmx/.txtの衝突）があるときだけ上書きの行、
    /// 連番名で保持される.bmx/.txtの衝突があるときだけ連番名の行を、この順に並べ、該当がなければnullです。
    /// </summary>
    /// <param name="overwrite">確認文を作る時点の上書きの設定です。衝突の行の文言を決めます。</param>
    /// <param name="hasWarning">未確認または要注意のグループを含む（確認ダイアログのアイコンを警告にする）場合はtrueです。</param>
    /// <param name="warningMessage">警告用の本文です。該当する行がなければnullです。</param>
    internal static string CreateConfirmationMessage(
        IReadOnlyList<DuplicateMergeCandidateGroupTarget> targets, DuplicateMergeOverwriteSettings overwrite,
        out bool hasWarning, out string warningMessage)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var lines = new List<string> { string.Format(Resources.Duplicate_batch_organize_confirm_groups_format, targets.Count) };
        int mergeGroups = 0;
        int sourceFolders = 0;
        int unverified = 0;
        int caution = 0;
        bool contentConflict = false;
        bool protectedCollision = false;
        foreach (DuplicateMergeCandidateGroupTarget target in targets)
        {
            int sources = target.Folders.Count(folder => !string.Equals(folder, target.KeepFolder, StringComparison.OrdinalIgnoreCase));
            if (sources > 0)
            {
                mergeGroups++;
                sourceFolders += sources;
            }
            DuplicateMergeVerificationResult verification = target.Verification ?? DuplicateMergeVerificationResult.Unverified;
            switch (verification.Stage)
            {
                case DuplicateMergeVerificationStage.Unverified:
                    unverified++;
                    break;
                case DuplicateMergeVerificationStage.Caution:
                    caution++;
                    break;
            }
            protectedCollision |= verification.HasProtectedNameCollision;
            contentConflict |= verification.Reasons.Any(reason => reason.Kind == DuplicateMergeVerificationReasonKind.ContentConflict);
        }
        if (mergeGroups > 0)
        {
            lines.Add(string.Format(Resources.Duplicate_batch_organize_confirm_merge_format, mergeGroups, sourceFolders));
        }
        lines.Add(Resources.Duplicate_batch_organize_confirm_trash);
        // 衝突整理の値は全対象で捕捉時点の同じ値です。指定を作る条件はCreateRequestと同じ規則で数えます。
        DuplicateBatchAudioCollisionCleanup[] audioCleanups = [.. targets.Select(CreateAudioCollisionCleanup)];
        int audioCleanupGroups = audioCleanups.Count(cleanup => cleanup != null);
        // 同等と判定していない名前も一方だけが残るため、そのような名前を含むグループの数を警告用の本文で示します。
        int unequalCleanupGroups = 0;
        for (int index = 0; index < targets.Count; index++)
        {
            if (audioCleanups[index] == null) { continue; }
            IReadOnlyList<string> equivalent = targets[index].Verification?.EquivalentAudioNames ?? [];
            if (audioCleanups[index].Names.Any(name => !equivalent.Contains(name, StringComparer.OrdinalIgnoreCase))) { unequalCleanupGroups++; }
        }
        if (audioCleanupGroups > 0)
        {
            lines.Add(string.Format(
                targets[0].CollisionCleanup == DuplicateMergeCollisionCleanupMode.KeepOgg
                    ? Resources.Duplicate_batch_organize_confirm_audio_keep_ogg_format
                    : Resources.Duplicate_batch_organize_confirm_audio_keep_wav_format,
                audioCleanupGroups));
        }
        hasWarning = unverified + caution > 0;
        var warnings = new List<string>();
        if (unverified > 0 && caution > 0)
        {
            warnings.Add(string.Format(Resources.Duplicate_batch_organize_confirm_unverified_caution_format, unverified, caution));
        }
        else if (unverified > 0)
        {
            warnings.Add(string.Format(Resources.Duplicate_batch_organize_confirm_unverified_format, unverified));
        }
        else if (caution > 0)
        {
            warnings.Add(string.Format(Resources.Duplicate_batch_organize_confirm_caution_format, caution));
        }
        if (unequalCleanupGroups > 0)
        {
            warnings.Add(string.Format(Resources.Duplicate_batch_organize_confirm_audio_unequal_format, unequalCleanupGroups));
        }
        if (contentConflict || (protectedCollision && !overwrite.KeepsBothProtectedFiles))
        {
            warnings.Add(overwrite.SmartOverwrite
                ? Resources.Duplicate_batch_organize_confirm_collision_newer
                : Resources.Duplicate_batch_organize_confirm_collision_source);
        }
        if (protectedCollision && overwrite.KeepsBothProtectedFiles)
        {
            warnings.Add(Resources.Duplicate_batch_organize_confirm_collision_renamed);
        }
        warningMessage = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 結果の案内を作ります。見出しは五区分（0件の区分も含む）の件数、ごみ箱へ送った譜面があれば合計、衝突整理で音源をごみ箱へ送っていればその件数、取消されていれば取消の行を続け、
    /// 完了以外のグループを要求順に最大 <see cref="MaxDetailGroups"/> 件まで「KEEP：状態（理由）」と残った移動元ごとの「移動元：理由」で並べます。
    /// </summary>
    /// <param name="allCompleted">全グループが完了で、取消も削除の失敗もない場合はtrueです。案内のアイコンの選択に使います。</param>
    internal static string CreateResultMessage(DuplicateBatchOrganizeResult result, out bool allCompleted)
    {
        ArgumentNullException.ThrowIfNull(result);
        IReadOnlyList<DuplicateBatchOrganizeGroupResult> groups = result.Groups;
        int Count(DuplicateBatchOrganizeGroupStatus status) => groups.Count(group => group.Status == status);
        var lines = new List<string>
        {
            string.Format(
                Resources.Duplicate_batch_organize_result_format,
                groups.Count,
                Count(DuplicateBatchOrganizeGroupStatus.Completed),
                Count(DuplicateBatchOrganizeGroupStatus.PartiallyCompleted),
                Count(DuplicateBatchOrganizeGroupStatus.Skipped),
                Count(DuplicateBatchOrganizeGroupStatus.Failed),
                Count(DuplicateBatchOrganizeGroupStatus.Unprocessed))
        };
        int trashed = groups.Sum(group => group.TrashedChartCount);
        if (trashed > 0)
        {
            lines.Add(string.Format(Resources.Duplicate_batch_organize_result_trashed_format, trashed));
        }
        int trashedAudio = groups.Sum(group => group.TrashedAudioCount);
        if (trashedAudio > 0)
        {
            lines.Add(string.Format(Resources.Duplicate_batch_organize_result_trashed_audio_format, trashedAudio));
        }
        if (result.Canceled)
        {
            lines.Add(Resources.Duplicate_batch_organize_result_canceled);
        }

        // 確定に失敗した操作では、物理的に成功していたグループがすべて失敗になり、統合した移動元は確定していない側へ移ります。
        bool commitFailed = !result.Receipt.SessionReceipt.DurableCommit
            && groups.Any(group => group.TrashedChartCount > 0
                || group.RetainedSources.Any(retained => retained.Reason == DuplicateBatchOrganizeSourceReason.NotCommitted));
        DuplicateBatchOrganizeGroupResult[] incomplete = [.. groups.Where(group => group.Status != DuplicateBatchOrganizeGroupStatus.Completed)];
        if (incomplete.Length > 0)
        {
            lines.Add(string.Empty);
        }
        foreach (DuplicateBatchOrganizeGroupResult group in incomplete.Take(MaxDetailGroups))
        {
            string status = StatusText(group.Status);
            string reason = GetGroupReason(group, commitFailed);
            lines.Add(reason == null
                ? string.Format(Resources.Duplicate_batch_organize_result_group_format, group.KeepDirectory, status)
                : string.Format(Resources.Duplicate_batch_organize_result_group_reason_format, group.KeepDirectory, status, reason));
            foreach (DuplicateBatchOrganizeRetainedSource retained in group.RetainedSources)
            {
                lines.Add(string.Format(
                    Resources.Duplicate_batch_organize_result_source_format, retained.Directory, SourceReasonText(retained.Reason, retained.Detail)));
            }
        }
        if (incomplete.Length > MaxDetailGroups)
        {
            lines.Add(string.Format(Resources.Duplicate_batch_organize_result_more_format, incomplete.Length - MaxDetailGroups));
        }
        allCompleted = incomplete.Length == 0 && !result.Canceled && groups.All(group => group.TrashFailureCount == 0 && group.AudioTrashFailureCount == 0);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// グループの状態に添える理由です。スキップは拒否の理由、確定の失敗による失敗は確定の失敗、そうでなく個別の削除に失敗があれば削除の失敗です（重なる場合は確定の失敗を優先します）。
    /// それ以外は理由を付けません。
    /// </summary>
    private static string GetGroupReason(DuplicateBatchOrganizeGroupResult group, bool commitFailed)
    {
        if (group.Status == DuplicateBatchOrganizeGroupStatus.Skipped)
        {
            return group.SkipReason == DuplicateBatchOrganizeSkipReason.None ? null : SkipReasonText(group.SkipReason);
        }
        // 確定の失敗によって失敗になったグループ（統合済みの移動元が確定していない扱いになった、またはごみ箱へ送った譜面がある）だけに付けます。
        bool failedByCommit = commitFailed && group.Status == DuplicateBatchOrganizeGroupStatus.Failed
            && (group.TrashedChartCount > 0
                || group.RetainedSources.Any(retained => retained.Reason == DuplicateBatchOrganizeSourceReason.NotCommitted));
        if (failedByCommit)
        {
            // 個別の削除の失敗と重なっても、確定されていないことが利用者にとって重要なため、確定の失敗を優先します。
            return Resources.Duplicate_batch_organize_reason_commit_failure;
        }
        return group.TrashFailureCount > 0 || group.AudioTrashFailureCount > 0 ? Resources.Duplicate_batch_organize_reason_delete_failure : null;
    }

    /// <summary>グループの状態の文言です。</summary>
    internal static string StatusText(DuplicateBatchOrganizeGroupStatus status) => status switch
    {
        DuplicateBatchOrganizeGroupStatus.Completed => Resources.Duplicate_batch_organize_status_completed,
        DuplicateBatchOrganizeGroupStatus.PartiallyCompleted => Resources.Duplicate_batch_organize_status_partial,
        DuplicateBatchOrganizeGroupStatus.Skipped => Resources.Duplicate_batch_organize_status_skipped,
        DuplicateBatchOrganizeGroupStatus.Failed => Resources.Duplicate_batch_organize_status_failed,
        DuplicateBatchOrganizeGroupStatus.Unprocessed => Resources.Duplicate_batch_organize_status_unprocessed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown duplicate batch organize group status.")
    };

    /// <summary>グループを物理変更の前に拒否した理由の文言です。拒否していない（None）場合はnullです。</summary>
    internal static string SkipReasonText(DuplicateBatchOrganizeSkipReason reason) => reason switch
    {
        DuplicateBatchOrganizeSkipReason.None => null,
        DuplicateBatchOrganizeSkipReason.LinkFound => Resources.Duplicate_batch_organize_reason_link,
        DuplicateBatchOrganizeSkipReason.LinkCheckFailed => Resources.Duplicate_batch_organize_reason_link_unchecked,
        DuplicateBatchOrganizeSkipReason.RegisteredRoot => Resources.Duplicate_batch_organize_reason_registered_root,
        DuplicateBatchOrganizeSkipReason.NestedFolders => Resources.Duplicate_batch_organize_reason_nested,
        DuplicateBatchOrganizeSkipReason.OverlapsOtherGroup => Resources.Duplicate_batch_organize_reason_overlap,
        DuplicateBatchOrganizeSkipReason.FolderMissing => Resources.Duplicate_batch_organize_reason_folder_missing,
        DuplicateBatchOrganizeSkipReason.InvalidPath => Resources.Duplicate_batch_organize_reason_invalid_path,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown duplicate batch organize skip reason.")
    };

    /// <summary>統合せずに残した移動元の理由の文言です。</summary>
    /// <param name="detail">理由に含める譜面のパス。<see cref="DuplicateBatchOrganizeSourceReason.OwnedFileMissing"/> だけが使います。</param>
    internal static string SourceReasonText(DuplicateBatchOrganizeSourceReason reason, string detail = null) => reason switch
    {
        DuplicateBatchOrganizeSourceReason.OwnedFileMissing => string.Format(Resources.Duplicate_batch_organize_reason_owned_file_missing, detail),
        DuplicateBatchOrganizeSourceReason.SourceMissing => Resources.Duplicate_batch_organize_reason_source_missing,
        DuplicateBatchOrganizeSourceReason.DestinationMissing => Resources.Duplicate_batch_organize_reason_destination_missing,
        DuplicateBatchOrganizeSourceReason.TypeConflict => Resources.Duplicate_batch_organize_reason_type_conflict,
        DuplicateBatchOrganizeSourceReason.NoCharts => Resources.Duplicate_batch_organize_reason_charts_missing,
        DuplicateBatchOrganizeSourceReason.PreflightRefused => Resources.Duplicate_batch_organize_reason_preflight,
        DuplicateBatchOrganizeSourceReason.PhysicalFailure => Resources.Duplicate_batch_organize_reason_physical_failure,
        DuplicateBatchOrganizeSourceReason.NotProcessed => Resources.Duplicate_batch_organize_reason_not_processed,
        DuplicateBatchOrganizeSourceReason.NotCommitted => Resources.Duplicate_batch_organize_reason_commit_failure,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown duplicate batch organize source reason.")
    };
}
