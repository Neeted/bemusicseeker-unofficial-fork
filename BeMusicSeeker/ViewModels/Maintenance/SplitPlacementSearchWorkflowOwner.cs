using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Parago.Windows;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 分裂配置のツリーを選んだときの探索の手順と、探索条件の調整による再探索の手順を管理します。有効な結果があれば、受付も進捗も使わずに一覧へ遷移します。
/// 結果がなければ、共通のライブラリ変更受付を非待機で取得して探索の終端まで保持し、取消できるモーダルの進捗の下で、UIスレッド外で探索します。
/// 受付を解放してから、成功・取消・失敗のいずれでも一覧への遷移を一回だけ呼びます。
/// </summary>
/// <remarks>
/// 受付が使用中ならBusyを一度だけ案内して遷移せず、閉じている（終了の要求中など）なら案内も遷移もしません。予約や自動の再試行はしません。
/// 取消は案内せず、取消を要求してもモデルが結果を格納して戻った場合は成功として扱います。失敗はこの主体が一回だけ報告し、呼出元へ投げ直しません。
/// 一覧の中身は遷移の先がモデルの結果から決めます（結果がなければ空、世代だけが古い結果が残っていればその結果）。
/// 受付を保持したまま、UIの完了を同期的に待ちません。進捗画面が先に閉じても、探索が終わるまで受付を解放しません。
/// 適用中の探索条件と入力中の値は、この主体が一つだけ保持します（アプリの起動中だけ。保存せず、ライブラリの交換と画面の行き来で保ちます）。
/// 選択による探索と、一括整理の後の探索し直しは適用中の探索条件を使い、入力中の値を使うのは <see cref="ResearchAsync"/> だけです。
/// 入力を変えても適用中の探索条件・結果・一覧は変わらず、探索も案内も遷移もしません。入力と適用はUIスレッドから行います。
/// </remarks>
public sealed class SplitPlacementSearchWorkflowOwner : INotifyPropertyChanged
{
    /// <summary>探索の進捗を画面へ渡す間隔です。最新の値だけを間引いて渡します。</summary>
    private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly IReadOnlyList<int> SharePercentChoiceValues =
    [
        .. Enumerable.Range(0, (SplitPlacementSearchCriteria.MaximumSharePercent - SplitPlacementSearchCriteria.MinimumSharePercent)
            / SplitPlacementSearchCriteria.SharePercentStep + 1)
            .Select(step => SplitPlacementSearchCriteria.MinimumSharePercent + step * SplitPlacementSearchCriteria.SharePercentStep)
    ];

    private static readonly IReadOnlyList<int> GenericFolderLimitChoiceValues =
    [
        .. Enumerable.Range(
            SplitPlacementSearchCriteria.MinimumGenericFolderLimit,
            SplitPlacementSearchCriteria.MaximumGenericFolderLimit - SplitPlacementSearchCriteria.MinimumGenericFolderLimit + 1)
    ];

    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer operationAdmission;
    private readonly IUiDialogService dialogs;
    private readonly Func<DuplicateViewContext, Task> navigate;

    private SplitPlacementSearchCriteria appliedCriteria = SplitPlacementSearchCriteria.Default;
    private SplitPlacementSearchCriteria inputCriteria = SplitPlacementSearchCriteria.Default;

    /// <param name="libraryProvider">現在のライブラリを返します。未接続なら <see langword="null"/> です。</param>
    /// <param name="operationAdmission">共通のライブラリ変更受付。</param>
    /// <param name="dialogs">進捗と案内の窓口。</param>
    /// <param name="navigate">分裂配置の一覧への遷移です。選択時に捕捉した文脈を渡し、探索の結果を問わず一回だけ呼びます。再探索の成功では統合候補一覧の文脈で一回呼びます。</param>
    internal SplitPlacementSearchWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer operationAdmission,
        IUiDialogService dialogs,
        Func<DuplicateViewContext, Task> navigate)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.operationAdmission = operationAdmission ?? throw new ArgumentNullException(nameof(operationAdmission));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>適用中の探索条件です。<see cref="ResearchAsync"/> が成功したときだけ変わります。起動直後は既定値です。</summary>
    internal SplitPlacementSearchCriteria AppliedCriteria => appliedCriteria;

    /// <summary>入力中の探索条件です。三つの入力の値から作ります。</summary>
    internal SplitPlacementSearchCriteria InputCriteria => inputCriteria;

    /// <summary>共有率の選択肢（百分率）です。50〜100を5刻みで並べます。画面の選択式の入力が読みます。</summary>
    public IReadOnlyList<int> SharePercentChoices { get; } = SharePercentChoiceValues;

    /// <summary>「汎用名とみなすフォルダ数」の選択肢です。2〜100の整数を並べます。画面の選択式の入力が読みます。</summary>
    public IReadOnlyList<int> GenericFolderLimitChoices { get; } = GenericFolderLimitChoiceValues;

    /// <summary>入力中の共有率（百分率）です。範囲の外の値は <see cref="ArgumentOutOfRangeException"/> で拒否します。</summary>
    public int SharePercent
    {
        get => inputCriteria.SharePercent;
        set => SetInput(value, inputCriteria.GenericFolderLimit, inputCriteria.Basis);
    }

    /// <summary>入力中の「汎用名とみなすフォルダ数」です。範囲の外の値は <see cref="ArgumentOutOfRangeException"/> で拒否します。</summary>
    public int GenericFolderLimit
    {
        get => inputCriteria.GenericFolderLimit;
        set => SetInput(inputCriteria.SharePercent, value, inputCriteria.Basis);
    }

    /// <summary>入力中の共有率の基準です。</summary>
    public SplitPlacementShareBasis Basis
    {
        get => inputCriteria.Basis;
        set => SetInput(inputCriteria.SharePercent, inputCriteria.GenericFolderLimit, value);
    }

    /// <summary>入力中の値が適用中の探索条件と異なるかです。画面が「再探索」を強調するために読みます。</summary>
    public bool HasUnappliedInput => !inputCriteria.Equals(appliedCriteria);

    /// <summary>
    /// 現在のライブラリに、適用中の探索条件に対する有効な結果（0件を含む）があるかです。画面が、選択済みのルートの再操作で探索の要否を判断するために読みます。
    /// 入力中の値は使いません。
    /// </summary>
    internal bool HasCurrentResult => libraryProvider()?.HasCurrentSplitPlacementGroups(appliedCriteria) == true;

    private void SetInput(int sharePercent, int genericFolderLimit, SplitPlacementShareBasis basis)
    {
        SplitPlacementSearchCriteria previous = inputCriteria;
        var next = new SplitPlacementSearchCriteria(sharePercent, genericFolderLimit, basis);
        if (next.Equals(previous))
        {
            return;
        }
        bool hadUnapplied = HasUnappliedInput;
        inputCriteria = next;
        if (next.SharePercent != previous.SharePercent) { RaisePropertyChanged(nameof(SharePercent)); }
        if (next.GenericFolderLimit != previous.GenericFolderLimit) { RaisePropertyChanged(nameof(GenericFolderLimit)); }
        if (next.Basis != previous.Basis) { RaisePropertyChanged(nameof(Basis)); }
        if (hadUnapplied != HasUnappliedInput) { RaisePropertyChanged(nameof(HasUnappliedInput)); }
    }

    private void RaisePropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>一回の探索の終わり方です。</summary>
    /// <param name="Started">探索を開始できたか。受付の閉鎖・Busyで開始できなかった場合は偽です。</param>
    /// <param name="Succeeded">探索が結果を格納して終わったか。取消を要求しても格納して終わった場合は真です。</param>
    /// <param name="Failure">失敗。取消は失敗ではありません。</param>
    private readonly record struct SearchOutcome(bool Started, bool Succeeded, Exception Failure);

    /// <summary>探索の実行状態です。探索のスレッドとUIスレッドが共有するため、読み書きは明示的に行います。</summary>
    private sealed class Run
    {
        internal Task SearchTask;
        internal SplitPlacementSearchProgress Latest;
        internal bool HasLatest;
        internal (int Percent, string Message)? Posted;
    }

    /// <summary>
    /// ツリーのルート・グループ・フォルダの選択です。結果が現在のものでなければ、適用中の探索条件で探索してから、選択時に捕捉した文脈で一覧へ遷移します。
    /// </summary>
    /// <param name="parameter">ルートは <see langword="null"/>、グループは <see cref="DuplicateGroup"/>、フォルダはそのパスです。</param>
    /// <param name="owner">進捗画面の所有者です。メインウィンドウを渡します。</param>
    internal async Task SelectAsync(object parameter, Window owner = null)
    {
        // グループはインスタンスではなく所属フォルダの値で捕捉し、探索による結果の置換から切り離す。
        DuplicateViewContext context = RegularChartListOwner.CaptureDuplicateContext(parameter);
        SplitPlacementSearchCriteria criteria = appliedCriteria;
        BMSLibrary library = libraryProvider();
        if (library == null || library.HasCurrentSplitPlacementGroups(criteria))
        {
            await navigate(context);
            return;
        }
        SearchOutcome outcome = await RunSearchAsync(library, criteria, owner);
        if (!outcome.Started)
        {
            return;
        }
        if (outcome.Failure != null)
        {
            await ReportFailureAsync(outcome.Failure);
        }
        await navigate(context);
    }

    /// <summary>
    /// 「再探索」です。入力中の探索条件に対する有効な結果があれば、受付を取らず、進捗・案内・遷移を行わず、何も変えずに戻ります。
    /// なければ <see cref="SelectAsync"/> の探索と同じ手順で、入力中の値により探索します。
    /// 成功（取消を要求しても結果を格納して終わった場合を含む）なら、入力中の値を適用中の探索条件にし、受付の解放後に統合候補一覧の文脈で一回だけ遷移して一覧を作り直します。
    /// 取消（案内なし）と失敗（一回だけ報告し、投げ直さない）では、適用中の探索条件・結果・入力中の値・一覧を変えず、遷移もしません。
    /// </summary>
    /// <param name="owner">進捗画面の所有者です。メインウィンドウを渡します。</param>
    internal async Task ResearchAsync(Window owner = null)
    {
        SplitPlacementSearchCriteria criteria = inputCriteria;
        BMSLibrary library = libraryProvider();
        if (library == null || library.HasCurrentSplitPlacementGroups(criteria))
        {
            return;
        }
        SearchOutcome outcome = await RunSearchAsync(library, criteria, owner);
        if (!outcome.Started)
        {
            return;
        }
        if (outcome.Failure != null)
        {
            await ReportFailureAsync(outcome.Failure);
            return;
        }
        if (!outcome.Succeeded)
        {
            return;
        }
        bool hadUnapplied = HasUnappliedInput;
        appliedCriteria = criteria;
        if (hadUnapplied != HasUnappliedInput) { RaisePropertyChanged(nameof(HasUnappliedInput)); }
        await navigate(DuplicateViewContext.ForCandidateList());
    }

    /// <summary>
    /// 共通受付の非待機取得、取消できるモーダルの進捗、UIスレッド外の探索を行い、受付を探索の終端まで保持して解放してから、終わり方を返します。
    /// 失敗の報告と遷移は呼出し側が、受付の解放後に行います。
    /// </summary>
    private async Task<SearchOutcome> RunSearchAsync(BMSLibrary library, SplitPlacementSearchCriteria criteria, Window owner)
    {
        if (!operationAdmission.TryEnter(out IDisposable lease))
        {
            // 受付の閉鎖（終了の要求中など）による不成立は案内しない。他の受付を使う手順と同じ扱いにする。
            if (operationAdmission.IsAdmissionClosed) { return new SearchOutcome(false, false, null); }
            await ShowOptionalNotificationAsync(
                UiMessageRequest.CreateWarning(Resources.Warn_LibraryOperationBusy, Resources.Warning),
                "Split placement search Busy notification");
            return new SearchOutcome(false, false, null);
        }

        Exception failure = null;
        bool succeeded = false;
        try
        {
            using LibraryFileMutationCapability capability = operationAdmission.CreateMutationCapability(lease);
            var run = new Run();
            CancellationTokenSource cancellation = new();
            try
            {
                UiProgressResult progressResult = await dialogs.RunWithProgressAsync(
                    new UiProgressRequest(
                        Resources.Split_placement_search_progress_title,
                        Resources.Split_placement_search_progress_label,
                        new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false)
                        {
                            // 取消を要求しても、探索が結果を格納して戻れば成功として扱うため、取消の扱いを操作へ任せる。
                            OperationOwnsCancellation = true
                        },
                        owner),
                    context => RunProgressOperationAsync(run, context, library, criteria, capability, cancellation));
                if (progressResult == null)
                {
                    failure = new InvalidOperationException("Split placement search progress route returned no result.");
                }
                else if (progressResult.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser))
                {
                    failure = new InvalidOperationException(
                        "Split placement search progress route failed: " + progressResult.Status, progressResult.Error);
                }
                else if (run.SearchTask == null)
                {
                    failure = new InvalidOperationException("Split placement search progress route did not run the operation.");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            // 探索の終端まで待つ。進捗画面が先に閉じても、受付は探索が終わるまで解放しない。
            if (run.SearchTask != null)
            {
                if (failure != null) { cancellation.Cancel(); }
                try
                {
                    await run.SearchTask;
                    succeeded = true;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception) { failure ??= exception; }
            }
            cancellation.Dispose();
        }
        finally
        {
            lease.Dispose();
        }
        return new SearchOutcome(true, succeeded && failure == null, failure);
    }

    /// <summary>進捗画面の操作です。探索を開始し、終端まで取消の要求と進捗を仲介します。</summary>
    private async Task RunProgressOperationAsync(
        Run run,
        UiProgressContext context,
        BMSLibrary library,
        SplitPlacementSearchCriteria criteria,
        LibraryFileMutationCapability capability,
        CancellationTokenSource cancellation)
    {
        // 操作の開始前に取消が要求されていれば、モデルは捕捉の直後に取消として終わる。
        if (context.CancellationRequested) { cancellation.Cancel(); }
        var progress = new LatestProgress(run);
        CancellationToken token = cancellation.Token;
        run.SearchTask = Task.Run(
            () =>
            {
                // 受付は呼出し側が探索の終端まで保持している。ここでは解放しない借用だけを使う。
                using LibraryFileMutationLease borrowed = operationAdmission.Borrow(capability);
                library.SearchSplitPlacementGroups(criteria, progress, token);
            },
            CancellationToken.None);
        while (!run.SearchTask.IsCompleted)
        {
            if (context.CancellationRequested) { cancellation.Cancel(); }
            PostLatestProgress(run, context);
            await Task.WhenAny(run.SearchTask, Task.Delay(ProgressPollInterval)).ConfigureAwait(false);
        }
        PostLatestProgress(run, context);
        // 結果と例外は、呼出し側が同じTaskから読みます。画面の操作としては、終端まで待つだけです。
        try { await run.SearchTask.ConfigureAwait(false); }
        catch { /* 呼出し側が同じTaskから失敗を読みます。 */ }
    }

    /// <summary>最新の進捗だけを保持して待たずに戻る受け手です。画面への投稿は <see cref="PostLatestProgress"/> が間引きます。</summary>
    private sealed class LatestProgress(Run run) : IProgress<SplitPlacementSearchProgress>
    {
        public void Report(SplitPlacementSearchProgress value)
        {
            lock (run)
            {
                run.Latest = value;
                run.HasLatest = true;
            }
        }
    }

    /// <summary>保持している最新の値を、前回投稿した値と異なる場合だけ画面へ投稿します。投稿は一つの流れだけが行います。</summary>
    private static void PostLatestProgress(Run run, UiProgressContext context)
    {
        SplitPlacementSearchProgress latest;
        lock (run)
        {
            if (!run.HasLatest) { return; }
            latest = run.Latest;
        }
        (int Percent, string Message) next = latest.Stage == SplitPlacementSearchStage.Match
            ? (latest.Total <= 0 ? 100 : (int)(100L * Math.Min(latest.Completed, latest.Total) / latest.Total),
                string.Format(Resources.Split_placement_search_progress_match_format, latest.Completed, latest.Total))
            : (0, Resources.Split_placement_search_progress_prepare);
        if (run.Posted == next) { return; }
        run.Posted = next;
        context.Report(next.Percent, next.Message);
    }

    /// <summary>失敗を診断へ記録し、利用者へ一回だけ報告します。表示の失敗は診断へ残すだけです。</summary>
    private async Task ReportFailureAsync(Exception failure)
    {
        try { NLogWrapper.FileLogger?.Warn(failure, "split placement search failed"); }
        catch { /* 診断の失敗で報告を止めない。 */ }
        await ShowOptionalNotificationAsync(
            UiMessageRequest.CreateError(Resources.Msg_error_unexpected, Resources.Error),
            "Split placement search failure notification");
    }

    /// <summary>任意の案内です。表示の失敗は診断へ残すだけで、手順の結果へ影響させません。</summary>
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
            catch { /* 任意の案内の診断失敗を手順の結果へ合成しません。 */ }
        }
    }
}
