using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>実SQLite・score setter・通常単一readerを、実UI操作Completionを手動終端する既存ownerへ接続します。</summary>
internal sealed class RegularScoreDeliveryTestFixture : IDisposable
{
    /// <summary>同じ実score読込み・通知入力を一覧とMainWindowのcaseで使うため、専有DBと手動終端schedulerを準備します。</summary>
    /// <param name="songPath">呼出元が寿命を所有するsong DB。同じ親へscore.dbを作るため、helperのDispose後に親媒体を回収します。</param>
    /// <param name="count">そのまま準備するscore・譜面の件数。</param>
    /// <remarks>schemaとseedのtransaction完了後に実InitializeScoresOnly・setter・初回配信へ進みます。初期化の失敗はそのまま呼出元へ伝播します。</remarks>
    internal RegularScoreDeliveryTestFixture(string songPath, int count = 3)
    {
        var databaseScope = new ApplicationDatabaseScope();
        string root = Path.GetDirectoryName(songPath) ?? throw new InvalidOperationException();
        string scorePath = Path.Combine(root, "score.db");
        string[] hashes = Enumerable.Range(0, count).Select(i => (i + 1).ToString("x32")).ToArray();
        using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scorePath))
        {
            // schemaとseedだけをまとめ、実score読込み・setter・通知はcommit後の本番入口へ通します。
            db.RunInTransaction(() =>
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.CreateTable<LR2ScoreDB.score>();
                db.Insert(new LR2ScoreDB.player { id = "score-delivery", irid = 123 });
                for (int i = 0; i < count; i++)
                {
                    db.Insert(new LR2ScoreDB.score { hash = hashes[i], great = i switch { 0 => 10, 1 => 30, 2 => 20, _ => 0 } });
                }
            });
        }
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true, LR2RootPath = root };
        Library = new TestBmsLibrary(databaseScope, songPath, null, scorePath, null, () => options, TestBmsFactory.MissingEverythingBridge);
        Library.InitializeScoresOnly(null);
        Scores = hashes.Select(hash => Library.GetBMSScores().Single(score => score.hash == hash)).ToArray();
        for (int i = 0; i < count; i++) { Scores[i].stddevVal = i switch { 0 => 10, 1 => 30, 2 => 20, _ => 100 + i }; }
        Library.BmsCharts = hashes.Select((hash, i) => CreateTestableBmsFile(Path.Combine(root, i + ".bms")) with
        { Md5 = hash, Title = i switch { 0 => "A", 1 => "B", 2 => "U", _ => "Background " + i } }).ToArray();
        Table = new MainChartListViewModel();
        Owner = CreateOwner(Table, CreateWorkspaceForOwner(Table), action => action(), Scheduler);
        Owner.AttachNormalLibraryRefreshSource(Library);
        Task initial = Owner.DeliverPendingLibraryChangesAsync(Library, CancellationToken.None);
        ScheduledScoreOperation operation = Scheduler.NextAsync().GetAwaiter().GetResult();
        operation.Run();
        operation.Complete();
        initial.GetAwaiter().GetResult();
    }

    internal BMSLibrary Library { get; }
    internal BMSScore[] Scores { get; }
    internal MainChartListViewModel Table { get; }
    internal RegularChartListOwner Owner { get; }
    internal ScoreDeliveryUiScheduler Scheduler { get; } = new();
    internal ChartListVirtualView View => Table.Rows as ChartListVirtualView ?? throw new InvalidOperationException("No virtual view was adopted.");

    /// <summary>通常一覧または専用subsetの正規要求を、同じ独立比較値と有効なEX score検索へ写像します。</summary>
    internal bool Build(bool filtered = false, bool subset = false)
    {
        var columns = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
            MainViewUpdateMode.FullScanAllChartsFilterSelected, Visibility.Collapsed, new PlaylistSummaryColumnSettings());
        if (subset)
        {
            return Owner.TryApplyVirtualChartSubset(new RegularVirtualChartSubsetApplyRequest
            {
                Library = Library,
                SourceCharts = Library.CaptureNormalLibraryChartSnapshot().BmsRows,
                SourceProjectionMode = ChartListSourceProjectionMode.OwnerBacked,
                SubsetName = "score_delivery",
                TreeMode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                KeywordFilter = filtered ? "score:>=20" : string.Empty,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.stddevVal),
                SortDirection = ListSortDirection.Ascending,
                ColumnSelection = columns,
                Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                Stopwatch = Stopwatch.StartNew()
            }).WasCommitted;
        }
        return Owner.TryApplyVirtualNormalLibrary(new RegularVirtualNormalLibraryApplyRequest
        {
            Library = Library,
            KeywordFilter = filtered ? "score:>=20" : string.Empty,
            ModeFilter = ChartModeFilter.All,
            SortColumnName = nameof(LibraryChartRow.stddevVal),
            SortDirection = ListSortDirection.Ascending,
            ColumnSelection = columns,
            Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
            Stopwatch = Stopwatch.StartNew(),
            Reason = "score_delivery"
        }).WasCommitted;
    }

    /// <summary>EX scoreの生値変更を、通知する実stddevVal setterの確定と同じ変更キーへまとめます。</summary>
    internal void Change(int index, int value)
    {
        Scores[index].great = value;
        Scores[index].stddevVal = value;
    }

    /// <summary>観測口を外し、owner停止Taskと保留UI操作を終端してownerを破棄し、library終了要求とscheduler破棄を既存順で行います。</summary>
    /// <remarks>親媒体とworkspaceの所有は呼出元に残ります。停止・破棄が失敗した場合はその例外を伝え、後段へ進まない元の逐次順序を維持します。</remarks>
    public void Dispose()
    {
        Library.ScoreSnapshotWorkObserver = null;
        Library.PlaylistReferenceWorkObserver = null;
        Table.RowProjection.NormalInputWorkObserver = null;
        Task stop = Owner.StopAsync();
        Scheduler.AbortOutstanding();
        while (Scheduler.TryTake(out ScheduledScoreOperation? operation)) { operation.Run(); operation.Complete(); }
        stop.GetAwaiter().GetResult();
        Owner.Dispose();
        Library.RequestShutdown("regular_score_delivery_fixture");
        Scheduler.Dispose();
    }
}

/// <summary>実際の操作Completionとcallbackの実行を分け、drainの早すぎる成功・二重予約を観測します。</summary>
internal sealed class ScoreDeliveryUiScheduler : IUiScheduler, IDisposable
{
    private readonly ConcurrentQueue<ScheduledScoreOperation> operations = new();
    private readonly ConcurrentBag<ScheduledScoreOperation> ownedOperations = [];
    private readonly SemaphoreSlim available = new(0);
    internal bool Reject { get; set; }
    internal int Count => operations.Count;
    public bool IsAvailable => true;
    public bool CanExecuteInline => false;
    public bool CheckAccess() => false;
    public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        var operation = new ScheduledScoreOperation(action, !Reject);
        if (!Reject) { ownedOperations.Add(operation); operations.Enqueue(operation); available.Release(); }
        return operation;
    }
    internal async Task<ScheduledScoreOperation> NextAsync()
    {
        await available.WaitAsync().ConfigureAwait(false);
        return operations.TryDequeue(out ScheduledScoreOperation? operation) ? operation : throw new InvalidOperationException("UI operation was lost.");
    }
    internal bool TryTake([NotNullWhen(true)] out ScheduledScoreOperation? operation)
    {
        if (available.Wait(0) && operations.TryDequeue(out operation)) { return true; }
        operation = null;
        return false;
    }
    /// <summary>失敗した表明のfinallyでも、取り出し済みを含むfixture所有の操作を取消終端へ回収します。</summary>
    internal void AbortOutstanding()
    {
        foreach (ScheduledScoreOperation operation in ownedOperations) { operation.Abort(); }
    }
    public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => action();
    public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => action();
    public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => Schedule(action, priority).Completion;
    public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => action();
    public void Dispose() => available.Dispose();
}

/// <summary>実行・受付後取消・scheduler終端を明示する、通常ownerの実IUiScheduledOperationです。</summary>
internal sealed class ScheduledScoreOperation(Action action, bool accepted) : IUiScheduledOperation
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsAccepted => accepted;
    public bool IsCompleted => completion.Task.IsCompleted;
    public bool IsAborted { get; private set; }
    public string RejectionReason => accepted ? string.Empty : "score delivery rejected";
    public Task Completion => completion.Task;
    internal void Run()
    {
        if (IsAborted) { return; }
        try { action(); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
    internal void Complete() => completion.TrySetResult();
    public void Abort() { if (completion.TrySetCanceled()) { IsAborted = true; } }
}
