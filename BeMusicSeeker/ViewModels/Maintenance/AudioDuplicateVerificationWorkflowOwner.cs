using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Parago.Windows;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>整理候補一覧の確認の結果です。件数は、状態表へ書き込んだフォルダだけを数え、確認中に状態表から消えて捨てたフォルダを含みません。</summary>
/// <param name="Status">結末。</param>
/// <param name="Checked">確認して書き込んだフォルダ数。</param>
/// <param name="Safe">安全のフォルダ数。</param>
/// <param name="Caution">要注意のフォルダ数。</param>
/// <param name="Unorganizable">整理不可のフォルダ数。</param>
/// <param name="Unverified">取消で着手せず、確認前のままのフォルダ数。</param>
internal sealed record AudioDuplicateVerificationRunResult(
    DuplicateMergeVerificationRunStatus Status, int Checked, int Safe, int Caution, int Unorganizable, int Unverified)
{
    internal static AudioDuplicateVerificationRunResult NoTargets { get; } = new(DuplicateMergeVerificationRunStatus.NoTargets, 0, 0, 0, 0, 0);

    internal static AudioDuplicateVerificationRunResult Busy { get; } = new(DuplicateMergeVerificationRunStatus.Busy, 0, 0, 0, 0, 0);

    internal static AudioDuplicateVerificationRunResult NotStarted { get; } = new(DuplicateMergeVerificationRunStatus.NotStarted, 0, 0, 0, 0, 0);
}

/// <summary>
/// 整理候補一覧の確認の手順を管理します。確認は読み取りだけで、共通のライブラリ変更受付を非待機で取得して検査の開始から結果の書込みまで保持し、
/// モーダルの進捗の下でフォルダごとに逐次、UIスレッド外で検査します。結果の書込みは一括で行い、受付を解放してから結果を一度だけ案内します。
/// </summary>
/// <remarks>
/// 受付が使用中ならBusyを一度だけ案内し、受付が閉じている（終了の要求中など）なら案内せずに、どちらも状態を変えずに終えます。
/// 取消は、フォルダの開始前にだけ確認し、処理中のフォルダを終えてから止まります。着手しなかったフォルダの確認結果とSELECTは変えません。
/// 確認を終えたフォルダのSELECTは、段階によらずオフにします。入出力以外の予期しない例外は、完了済みのフォルダの結果を書き込み、受付を解放した後に呼出元へ伝えます。
/// 再生の停止も、ライブラリの変更の準備も行いません。状態表の読み書きはUIスレッドで行い、検査の間はモデルのロックもDBトランザクションも保持しません。
/// </remarks>
internal sealed class AudioDuplicateVerificationWorkflowOwner
{
    /// <summary>実行側の進捗を画面へ渡す間隔です。最新の位置だけを間引いて渡します。</summary>
    private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly AudioDuplicateCandidateListOwner list;
    private readonly Func<IAudioDuplicateFolderInspector> inspectorFactory;
    private readonly Func<Func<string, bool>> targetFolderPredicateCapture;
    private readonly ChartFileOperationSynchronizer operationAdmission;
    private readonly IUiDialogService dialogs;

    /// <param name="list">一覧の管理主体。対象の捕捉と結果の書込みに使います。</param>
    /// <param name="inspectorFactory">確認ごとに一回だけ呼び、検査器を返します。</param>
    /// <param name="targetFolderPredicateCapture">
    /// 確認の開始時に一回だけ、UIスレッド外で呼びます。その時点の所持集合から、配下の別の対象フォルダかを判定する述語（<see cref="AudioDuplicateInspectionRequest.IsOtherTargetFolder"/>）を作って返します。
    /// </param>
    /// <param name="operationAdmission">共通のライブラリ変更受付。</param>
    /// <param name="dialogs">進捗と案内の窓口。</param>
    internal AudioDuplicateVerificationWorkflowOwner(
        AudioDuplicateCandidateListOwner list,
        Func<IAudioDuplicateFolderInspector> inspectorFactory,
        Func<Func<string, bool>> targetFolderPredicateCapture,
        ChartFileOperationSynchronizer operationAdmission,
        IUiDialogService dialogs)
    {
        this.list = list ?? throw new ArgumentNullException(nameof(list));
        this.inspectorFactory = inspectorFactory ?? throw new ArgumentNullException(nameof(inspectorFactory));
        this.targetFolderPredicateCapture = targetFolderPredicateCapture ?? throw new ArgumentNullException(nameof(targetFolderPredicateCapture));
        this.operationAdmission = operationAdmission ?? throw new ArgumentNullException(nameof(operationAdmission));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    /// <summary>ツールバーの「選択フォルダを確認」です。表示中でSELECTがオンのフォルダを確認します。</summary>
    /// <param name="owner">進捗画面の所有者です。メインウィンドウを渡します。</param>
    internal Task<AudioDuplicateVerificationRunResult> VerifySelectedAsync(Window owner = null)
        => RunAsync(list.CaptureSelectedFolders(), owner);

    /// <summary>右クリックメニューの「このフォルダを確認」です。行のフォルダを、フォルダごとに一度、SELECTの状態によらず確認します。</summary>
    /// <param name="rows">選択行。</param>
    /// <param name="owner">進捗画面の所有者です。メインウィンドウを渡します。</param>
    internal Task<AudioDuplicateVerificationRunResult> VerifyFoldersOfRowsAsync(IEnumerable<AudioDuplicateCandidateRow> rows, Window owner = null)
        => RunAsync(list.CaptureFoldersOfRows(rows), owner);

    /// <summary>確認の実行状態です。検査の別スレッドとUIスレッドが共有するため、読み書きは明示的に行います。</summary>
    private sealed class Run(IReadOnlyList<AudioDuplicateCandidateTarget> targets, IAudioDuplicateFolderInspector inspector)
    {
        internal IReadOnlyList<AudioDuplicateCandidateTarget> Targets { get; } = targets;
        internal IAudioDuplicateFolderInspector Inspector { get; } = inspector;

        internal Task ScanTask;
        internal int CurrentIndex;
        internal int CompletedCount;
        internal int CancelRequestedFlag;
        internal Exception Failure;

        /// <summary>検査を終えたフォルダです。検査の完了後だけUIスレッドが読みます。</summary>
        internal List<(AudioDuplicateCandidateTarget Target, AudioDuplicateFolderInspection Inspection)> Completed { get; } = [];

        internal bool CancelRequested => Volatile.Read(ref CancelRequestedFlag) != 0;

        internal void RequestCancel() => Volatile.Write(ref CancelRequestedFlag, 1);
    }

    private async Task<AudioDuplicateVerificationRunResult> RunAsync(IReadOnlyList<AudioDuplicateCandidateTarget> targets, Window owner)
    {
        if (targets.Count == 0) { return AudioDuplicateVerificationRunResult.NoTargets; }
        if (!operationAdmission.TryEnter(out IDisposable lease))
        {
            // 受付の閉鎖（終了の要求中など）による不成立は案内しない。他の受付を使う手順と同じ扱いにする。
            if (operationAdmission.IsAdmissionClosed) { return AudioDuplicateVerificationRunResult.NotStarted; }
            await ShowOptionalNotificationAsync(
                UiMessageRequest.CreateWarning(Resources.Warn_LibraryOperationBusy, Resources.Warning),
                "Audio duplicate verification Busy notification");
            return AudioDuplicateVerificationRunResult.Busy;
        }

        Exception failure = null;
        AudioDuplicateVerificationRunResult result;
        bool cancelled;
        try
        {
            Run run = null;
            try
            {
                run = new Run(
                    targets,
                    inspectorFactory() ?? throw new InvalidOperationException("Audio duplicate inspector was not provided."));
                UiProgressResult progressResult = await dialogs.RunWithProgressAsync(
                    new UiProgressRequest(
                        Resources.Audio_duplicate_verify_progress_title,
                        Resources.Audio_duplicate_verify_progress_label,
                        new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false),
                        owner),
                    context => RunProgressOperationAsync(run, context));
                if (progressResult == null)
                {
                    failure = new InvalidOperationException("Audio duplicate verification progress route returned no result.");
                }
                else if (progressResult.Status == UiDialogStatus.CancelledByUser)
                {
                    run.RequestCancel();
                }
                else if (progressResult.Status != UiDialogStatus.Accepted)
                {
                    failure = new InvalidOperationException(
                        "Audio duplicate verification progress route failed: " + progressResult.Status, progressResult.Error);
                }
                else if (run.ScanTask == null)
                {
                    failure = new InvalidOperationException("Audio duplicate verification progress route did not run the operation.");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            // 検査の終端まで待つ。進捗が失敗や取消で終わっても、処理中のフォルダは最後まで処理する。
            if (run?.ScanTask != null)
            {
                if (failure != null) { run.RequestCancel(); }
                try { await run.ScanTask; }
                catch (Exception exception) { failure ??= exception; }
                failure ??= run.Failure;
            }

            (result, cancelled) = WriteResults(run, targets);
        }
        finally
        {
            lease.Dispose();
        }

        if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        if (result.Checked > 0)
        {
            await ShowOptionalNotificationAsync(
                UiMessageRequest.CreateInformation(BuildResultMessage(result), Resources.Confirm, completedSuccessfully: !cancelled),
                "Audio duplicate verification result notification");
        }
        return result;
    }

    /// <summary>進捗画面の操作です。検査を開始し、終端まで進捗を表示します。</summary>
    private async Task RunProgressOperationAsync(Run run, UiProgressContext context)
    {
        run.ScanTask = Task.Run(() => Scan(run), CancellationToken.None);
        int reported = -1;
        while (!run.ScanTask.IsCompleted)
        {
            try
            {
                int current = Volatile.Read(ref run.CurrentIndex);
                if (current != reported)
                {
                    reported = current;
                    int total = run.Targets.Count;
                    context.ReportWithCancellationCheck(
                        100 * Volatile.Read(ref run.CompletedCount) / total,
                        "[{0}/{1}] {2}",
                        Math.Min(current + 1, total),
                        total,
                        GetFolderName(run.Targets[Math.Min(current, total - 1)].Folder));
                }
                else
                {
                    context.ThrowIfCancellationPending();
                }
            }
            catch (ProgressDialogCancellationExcpetion)
            {
                // 処理中のフォルダは最後まで処理して、画面を閉じる。
                run.RequestCancel();
                await Task.WhenAny(run.ScanTask);
                return;
            }
            await Task.WhenAny(run.ScanTask, Task.Delay(ProgressPollInterval));
        }
    }

    /// <summary>フォルダごとに逐次、検査します。UIスレッド外で動き、予期しない例外は検査を止めて記録します。</summary>
    private void Scan(Run run)
    {
        try
        {
            // 確認の開始時に、所持集合から配下の別の対象フォルダの判定を一回だけ捕捉する。
            Func<string, bool> isOtherTargetFolder = targetFolderPredicateCapture();
            for (int position = 0; position < run.Targets.Count; position++)
            {
                // 取消はフォルダの開始前にだけ確認し、検査器の途中では止めない。
                if (run.CancelRequested) { break; }
                Volatile.Write(ref run.CurrentIndex, position);
                AudioDuplicateCandidateTarget target = run.Targets[position];
                AudioDuplicateFolderInspection inspection = run.Inspector.Inspect(new AudioDuplicateInspectionRequest(target.Folder, isOtherTargetFolder));
                run.Completed.Add((target, inspection));
                Volatile.Write(ref run.CompletedCount, position + 1);
            }
        }
        catch (Exception exception)
        {
            run.Failure = exception;
        }
    }

    private static string GetFolderName(string folder)
    {
        string trimmed = (folder ?? string.Empty).TrimEnd('\\', '/');
        string name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>
    /// 完了したフォルダの結果を一括で書き込みます。UIスレッドで呼びます。
    /// 状態表から消えたフォルダの結果は捨て、どの件数にも数えません。
    /// </summary>
    private (AudioDuplicateVerificationRunResult Result, bool Cancelled) WriteResults(
        Run run, IReadOnlyList<AudioDuplicateCandidateTarget> targets)
    {
        var writes = new List<AudioDuplicateVerificationWrite>();
        foreach ((AudioDuplicateCandidateTarget target, AudioDuplicateFolderInspection inspection) in run?.Completed ?? [])
        {
            if (!list.TryGetState(target.Group, out _)) { continue; }
            writes.Add(new AudioDuplicateVerificationWrite(target.Group, inspection));
        }
        IReadOnlyList<AudioDuplicateVerificationWrite> accepted = list.WriteVerifications(writes);
        int completedCount = run?.Completed.Count ?? 0;
        int unverified = targets.Skip(completedCount).Count(target => list.TryGetState(target.Group, out _));
        bool cancelled = unverified > 0;
        return (
            new AudioDuplicateVerificationRunResult(
                cancelled ? DuplicateMergeVerificationRunStatus.Cancelled : DuplicateMergeVerificationRunStatus.Completed,
                accepted.Count,
                accepted.Count(write => write.Inspection.Stage == AudioDuplicateInspectionStage.Safe),
                accepted.Count(write => write.Inspection.Stage == AudioDuplicateInspectionStage.Caution),
                accepted.Count(write => write.Inspection.Stage == AudioDuplicateInspectionStage.Unorganizable),
                unverified),
            cancelled);
    }

    /// <summary>結果の案内です。安全の件数によらず同じ書式で、取消で着手しなかったフォルダがあれば「未確認」の件数を加えます。</summary>
    private static string BuildResultMessage(AudioDuplicateVerificationRunResult result)
    {
        string format = result.Unverified > 0
            ? Resources.Audio_duplicate_verify_result_cancelled_format
            : Resources.Audio_duplicate_verify_result_format;
        return string.Format(format, result.Checked, result.Safe, result.Caution, result.Unorganizable, result.Unverified);
    }

    /// <summary>任意の案内です。表示の失敗は診断へ残すだけで、確認の結果へ影響させません。</summary>
    private async Task ShowOptionalNotificationAsync(UiMessageRequest request, string routeName)
    {
        try
        {
            UiDialogResult result = await dialogs.ShowMessageAsync(request);
            UiDialogRoute.ThrowIfNotShown(result, routeName);
        }
        catch (Exception exception)
        {
            try { NLogWrapper.FileLogger?.Warn(exception, routeName); }
            catch { /* 任意の案内の診断失敗を確認の結果へ合成しません。 */ }
        }
    }
}
