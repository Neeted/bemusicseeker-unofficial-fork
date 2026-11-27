using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

/// <summary>
/// GitHubのスター依頼の所有者を、偽のストア・固定の時計と、テストが終端を操作する初期化・更新確認で確認します。
/// </summary>
[TestClass]
public sealed class GitHubStarPromptWorkflowOwnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>この起動で回数が10へ達し、予定を過ぎている保存値です。</summary>
    private static readonly GitHubStarPromptRecord DueAfterThisStartup = new(GitHubStarPromptState.Unfinished, 9, Now.AddDays(-1), 0);

    private static StartupInitializationResult Success() => new(StartupInitializationOutcome.Succeeded, CompletionPublished: true);

    /// <summary>
    /// 保証：表示できる起動では、起動の回数を更新確認の終端を待つ前に保存し、終端が「更新なし」なら一度だけ表示を依頼して、
    /// 選んだ操作を反映した値を保存する。計数を更新確認の後へ回して更新を案内した起動の回数を失う誤りと、選択の取り違えを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(nameof(GitHubStarPromptChoice.Star), DisplayName = "スター")]
    [DataRow(nameof(GitHubStarPromptChoice.Later), DisplayName = "あとで")]
    [DataRow(nameof(GitHubStarPromptChoice.Decline), DisplayName = "今後表示しない")]
    public async Task EligibleStartup_SavesCountBeforeUpdateTerminal_ThenPresentsOnceAndSavesChoice(string choiceName)
    {
        GitHubStarPromptChoice choice = Enum.Parse<GitHubStarPromptChoice>(choiceName);
        var harness = new Harness(DueAfterThisStartup) { Respond = request => request.Complete(choice) };
        harness.Start();
        try
        {
            harness.Initialization.SetResult(Success());
            GitHubStarPromptRecord counted = await harness.AwaitFirstSaveAsync();

            // 更新確認の終端はまだ届けていないため、ここでの保存は終端を待つ前に行われています。
            Assert.AreEqual(DueAfterThisStartup with { StartupCount = 10 }, counted);
            Assert.IsFalse(harness.UpdateTerminal.Task.IsCompleted);
            Assert.AreEqual(0, harness.Requests.Count, "更新確認の終端より前に表示を依頼してはいけません。");

            harness.UpdateTerminal.SetResult(StartupUpdateWorkflowOutcome.NoUpdate);
            Assert.AreEqual(GitHubStarPromptWorkflowOutcome.Answered, await harness.Owner.Completion);

            Assert.AreEqual(1, harness.Requests.Count);
            Assert.AreEqual(1, harness.CanPresentCount);
            Assert.IsTrue(harness.CanPresentCalledOnUi && harness.HandlerCalledOnUi, "表示の確認とイベントはUIへの受け渡しの中で行います。");
            CollectionAssert.AreEqual(
                new[] { counted, GitHubStarPromptPolicy.ApplyChoice(counted, choice, Now) },
                harness.Store.Saves.ToArray());
            Assert.AreEqual(0, harness.Warnings.Count);
        }
        finally
        {
            await harness.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：初期化の終端と更新確認の終端は、どちらが先に届いても同じ保存と表示になる。
    /// 一方の終端を先に読んだ値で判断を固定する誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "更新確認が先")]
    [DataRow(false, DisplayName = "初期化が先")]
    public async Task InitializationAndUpdateTerminalOrder_ProduceSameResult(bool updateFirst)
    {
        var harness = new Harness(DueAfterThisStartup) { Respond = request => request.Complete(GitHubStarPromptChoice.Later) };
        harness.Start();
        try
        {
            if (updateFirst)
            {
                harness.UpdateTerminal.SetResult(StartupUpdateWorkflowOutcome.NoUpdate);
                harness.Initialization.SetResult(Success());
            }
            else
            {
                harness.Initialization.SetResult(Success());
                harness.UpdateTerminal.SetResult(StartupUpdateWorkflowOutcome.NoUpdate);
            }

            Assert.AreEqual(GitHubStarPromptWorkflowOutcome.Answered, await harness.Owner.Completion);
            Assert.AreEqual(1, harness.Requests.Count);
            Assert.AreEqual(new GitHubStarPromptRecord(GitHubStarPromptState.Unfinished, 0, Now.AddDays(30), 1), harness.Store.Current);
        }
        finally
        {
            await harness.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：数える起動でも、LR2の失敗・LR2ルート未設定の警告・「更新なし」以外の更新確認の終端（終端の記録なしと待機の失敗を含む）・
    /// 表示時点で画面が表示できない状態・保存値が未達のいずれかなら、起動の回数は保存したうえで表示を依頼しない。
    /// 他の案内や失敗を出した起動に重ねて表示する誤りと、見送った起動の回数を失う誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task CountedStartupFailingDisplayCondition_SavesCountWithoutPresenting()
    {
        var lr2Failure = new StartupRequiredInitializationResult(new LibraryFileInitializationResult(), new InvalidOperationException("lr2"));
        var rows = new (string Name, StartupInitializationResult Result, Action<TaskCompletionSource<StartupUpdateWorkflowOutcome?>> Update, bool CanPresent, GitHubStarPromptRecord Saved)[]
        {
            ("LR2の失敗", Success() with { RequiredResult = lr2Failure }, update => update.SetResult(StartupUpdateWorkflowOutcome.NoUpdate), true, DueAfterThisStartup),
            ("LR2ルート未設定の警告", Success() with { ShowRootWarning = true }, update => update.SetResult(StartupUpdateWorkflowOutcome.NoUpdate), true, DueAfterThisStartup),
            ("更新を取り消した", Success(), update => update.SetResult(StartupUpdateWorkflowOutcome.Cancelled), true, DueAfterThisStartup),
            ("更新を適用した", Success(), update => update.SetResult(StartupUpdateWorkflowOutcome.Applied), true, DueAfterThisStartup),
            ("更新確認の失敗", Success(), update => update.SetResult(StartupUpdateWorkflowOutcome.Failed), true, DueAfterThisStartup),
            ("更新確認中の終了", Success(), update => update.SetResult(StartupUpdateWorkflowOutcome.Closing), true, DueAfterThisStartup),
            ("終端の記録なし", Success(), update => update.SetResult(null), true, DueAfterThisStartup),
            ("待機の失敗", Success(), update => update.SetException(new InvalidOperationException("wait")), true, DueAfterThisStartup),
            ("表示できない画面", Success(), update => update.SetResult(StartupUpdateWorkflowOutcome.NoUpdate), false, DueAfterThisStartup),
            ("保存値が未達", Success(), update => update.SetResult(StartupUpdateWorkflowOutcome.NoUpdate), true, new(GitHubStarPromptState.Unfinished, 3, Now.AddDays(-1), 0))
        };

        foreach ((string name, StartupInitializationResult result, Action<TaskCompletionSource<StartupUpdateWorkflowOutcome?>> update, bool canPresent, GitHubStarPromptRecord saved) in rows)
        {
            var harness = new Harness(saved) { CanPresentValue = canPresent };
            harness.Start();
            try
            {
                harness.Initialization.SetResult(result);
                update(harness.UpdateTerminal);

                Assert.AreNotEqual(GitHubStarPromptWorkflowOutcome.Answered, await harness.Owner.Completion, name);
                Assert.AreEqual(0, harness.Requests.Count, name);
                CollectionAssert.AreEqual(new[] { GitHubStarPromptPolicy.RecordCountedStartup(saved, Now) }, harness.Store.Saves.ToArray(), name);
            }
            finally
            {
                await harness.FinishAsync();
            }
        }
    }

    /// <summary>
    /// 保証：数える起動に当たらない起動（成功以外の各分類、完了未公開、失敗を伴う成功、通常起動以外の操作種別、初期化の例外・取消）は、
    /// 保存値を読まず、保存も表示もしない。設定修復や再読込みで回数を進める誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task NonCountedStartup_NeitherSavesNorPresents()
    {
        var failure = new InvalidOperationException("initialization failed");
        var rows = new (string Name, Action<TaskCompletionSource<StartupInitializationResult>> Complete)[]
        {
            ("設定の修復が必要", tcs => tcs.SetResult(new(StartupInitializationOutcome.SettingsRequired))),
            ("処理の失敗", tcs => tcs.SetResult(new(StartupInitializationOutcome.OperationFailed, Failure: failure))),
            ("終了の要求", tcs => tcs.SetResult(new(StartupInitializationOutcome.ShutdownRequested))),
            ("終了の選択", tcs => tcs.SetResult(new(StartupInitializationOutcome.ExitRequested))),
            ("完了未公開の成功", tcs => tcs.SetResult(new(StartupInitializationOutcome.Succeeded, CompletionPublished: false))),
            ("失敗を伴う成功", tcs => tcs.SetResult(new(StartupInitializationOutcome.Succeeded, CompletionPublished: true, Failure: failure))),
            ("全再初期化", tcs => tcs.SetResult(Success() with { OperationKind = StartupProgressOperationKind.FullReinitialize })),
            ("スコアだけの再読込み", tcs => tcs.SetResult(Success() with { OperationKind = StartupProgressOperationKind.ScoreOnly })),
            ("初期化の例外", tcs => tcs.SetException(failure)),
            ("初期化の取消", tcs => tcs.SetCanceled())
        };

        foreach ((string name, Action<TaskCompletionSource<StartupInitializationResult>> complete) in rows)
        {
            var harness = new Harness(DueAfterThisStartup);
            harness.Start();
            try
            {
                complete(harness.Initialization);
                harness.UpdateTerminal.SetResult(StartupUpdateWorkflowOutcome.NoUpdate);

                Assert.AreEqual(GitHubStarPromptWorkflowOutcome.NotCounted, await harness.Owner.Completion, name);
                Assert.AreEqual(0, harness.Store.ReadCount, name);
                Assert.AreEqual(0, harness.Store.SaveAttemptCount, name);
                Assert.AreEqual(0, harness.Requests.Count, name);
            }
            finally
            {
                await harness.FinishAsync();
            }
        }
    }

    /// <summary>
    /// 保証：依頼の終了後（スター・今後表示しない・未知の状態）と表示回数の上限到達では、数える起動でも保存も表示もしない。
    /// 未知の状態を上書きして新しい版の値を消す誤りと、上限後の再表示を検出する。
    /// </summary>
    [TestMethod]
    public async Task FinishedOrLimitStateOnCountedStartup_NeitherSavesNorPresents()
    {
        GitHubStarPromptRecord[] records =
        [
            new(GitHubStarPromptState.Starred, 10, Now.AddDays(-1), 1),
            new(GitHubStarPromptState.Declined, 10, Now.AddDays(-1), 1),
            new(GitHubStarPromptState.Unknown, 10, Now.AddDays(-1), 0),
            new(GitHubStarPromptState.Unfinished, 10, Now.AddDays(-1), 3)
        ];

        foreach (GitHubStarPromptRecord record in records)
        {
            var harness = new Harness(record);
            harness.Start();
            try
            {
                harness.Initialization.SetResult(Success());
                harness.UpdateTerminal.SetResult(StartupUpdateWorkflowOutcome.NoUpdate);

                Assert.AreEqual(GitHubStarPromptWorkflowOutcome.Finished, await harness.Owner.Completion, record.ToString());
                Assert.AreEqual(0, harness.Store.SaveAttemptCount, record.ToString());
                Assert.AreEqual(0, harness.Requests.Count, record.ToString());
            }
            finally
            {
                await harness.FinishAsync();
            }
        }
    }

    /// <summary>
    /// 保証：無効な構成（検証起動）では、成功した起動でも保存値の読取り・保存・時計・更新確認の待機・表示のどれにも触れない。
    /// 検証起動のプロファイルへ起動の回数を書く誤りと、検証中に画面を出す誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task ValidationStartup_NeverAccessesStoreOrPresents()
    {
        GitHubStarPromptWorkflowOwner owner = GitHubStarPromptTestSupport.CreateDisabledOwner();
        int requests = 0;
        owner.PresentationRequested += _ => requests++;

        Assert.IsTrue(owner.Start(Task.FromResult(Success()), () => true));

        Assert.AreEqual(GitHubStarPromptWorkflowOutcome.Disabled, await owner.Completion);
        Assert.AreEqual(0, requests);
    }

    /// <summary>
    /// 保証：保存値の読取りか起動の回数・予定の保存が失敗した起動では、元の例外を診断へ残し、表示も以降の保存もしない。
    /// 設定ファイルに問題がある状態で表示へ進み、選択の保存でも失敗を重ねる誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "読取りの失敗")]
    [DataRow(false, DisplayName = "計数の保存の失敗")]
    public async Task CountSaveFailure_SkipsPresentationAndRecordsDiagnostic(bool readFails)
    {
        var failure = new IOException("config locked");
        var harness = new Harness(DueAfterThisStartup);
        if (readFails)
        {
            harness.Store.ReadFailure = failure;
        }
        else
        {
            harness.Store.SaveFailure = (1, failure);
        }
        harness.Start();
        try
        {
            harness.Initialization.SetResult(Success());
            harness.UpdateTerminal.SetResult(StartupUpdateWorkflowOutcome.NoUpdate);

            Assert.AreEqual(GitHubStarPromptWorkflowOutcome.CountSaveFailed, await harness.Owner.Completion);
            Assert.AreEqual(0, harness.Requests.Count);
            Assert.AreEqual(0, harness.Store.Saves.Count);
            Assert.AreEqual(readFails ? 0 : 1, harness.Store.SaveAttemptCount);
            Assert.IsTrue(harness.Warnings.Any(warning => ReferenceEquals(warning.Exception, failure)), "元の例外を診断へ残します。");
        }
        finally
        {
            await harness.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：回数が頭打ちで予定も設定済みなら、起動の計数では保存せずに表示へ進み、保存は選択の反映の一回だけになる。
    /// 起動のたびに無条件で保存して不要な書込みを繰り返す誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task CappedCount_PresentsWithoutSavingCount()
    {
        GitHubStarPromptRecord capped = new(GitHubStarPromptState.Unfinished, 10, Now.AddDays(-1), 1);
        var harness = new Harness(capped) { Respond = request => request.Complete(GitHubStarPromptChoice.Decline) };
        harness.Start();
        try
        {
            harness.Initialization.SetResult(Success());
            harness.UpdateTerminal.SetResult(StartupUpdateWorkflowOutcome.NoUpdate);

            Assert.AreEqual(GitHubStarPromptWorkflowOutcome.Answered, await harness.Owner.Completion);
            Assert.AreEqual(1, harness.Requests.Count);
            CollectionAssert.AreEqual(new[] { capped with { State = GitHubStarPromptState.Declined, ShownCount = 2 } }, harness.Store.Saves.ToArray());
        }
        finally
        {
            await harness.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：表示が成立しなかった場合（購読者なし、表示側の例外・失敗、表示しなかった応答、UIへの受け渡しの拒否・例外）は、
    /// 起動の計数のほかに保存値を変えない。失敗した場合は元の例外を診断へ残す。表示できなかった起動を表示回数に数える誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task PresentationNotEstablished_KeepsSavedValues()
    {
        var failure = new InvalidOperationException("presentation");
        var rows = new (string Name, bool Subscribe, Action<GitHubStarPromptPresentationRequest>? Respond, Func<Action, bool>? Dispatch, bool Diagnosed)[]
        {
            ("購読者なし", false, null, null, true),
            ("表示側の例外", true, _ => throw failure, null, true),
            ("表示の失敗", true, request => request.Fail(failure), null, true),
            ("表示しなかった", true, request => request.CompleteNotPresented(), null, false),
            ("受け渡しの拒否", true, null, _ => false, false),
            ("受け渡しの例外", true, null, _ => throw failure, true)
        };

        foreach ((string name, bool subscribe, Action<GitHubStarPromptPresentationRequest>? respond, Func<Action, bool>? dispatch, bool diagnosed) in rows)
        {
            var harness = new Harness(DueAfterThisStartup, subscribe, dispatch) { Respond = respond ?? (request => request.Complete(GitHubStarPromptChoice.Star)) };
            harness.Start();
            try
            {
                harness.Initialization.SetResult(Success());
                harness.UpdateTerminal.SetResult(StartupUpdateWorkflowOutcome.NoUpdate);

                GitHubStarPromptWorkflowOutcome outcome = await harness.Owner.Completion;
                Assert.IsTrue(outcome is GitHubStarPromptWorkflowOutcome.NotPresented or GitHubStarPromptWorkflowOutcome.PresentationFailed, name + ": " + outcome);
                CollectionAssert.AreEqual(new[] { DueAfterThisStartup with { StartupCount = 10 } }, harness.Store.Saves.ToArray(), name);
                Assert.AreEqual(diagnosed, harness.Warnings.Count != 0, name);
            }
            finally
            {
                await harness.FinishAsync();
            }
        }
    }

    /// <summary>
    /// 保証：選択の保存が失敗した場合は元の例外を診断へ残すだけで、同じ起動で再表示しない（所有者は一度だけ開始できる）。
    /// 保存の失敗から表示を繰り返す誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task ChoiceSaveFailure_RecordsDiagnosticAndDoesNotPresentAgain()
    {
        var failure = new IOException("config locked");
        var harness = new Harness(DueAfterThisStartup) { Respond = request => request.Complete(GitHubStarPromptChoice.Star) };
        harness.Store.SaveFailure = (2, failure);
        harness.Start();
        try
        {
            harness.Initialization.SetResult(Success());
            harness.UpdateTerminal.SetResult(StartupUpdateWorkflowOutcome.NoUpdate);

            Assert.AreEqual(GitHubStarPromptWorkflowOutcome.ChoiceSaveFailed, await harness.Owner.Completion);
            Assert.IsFalse(harness.Owner.Start(Task.FromResult(Success()), () => true), "同じ起動で再び開始しません。");
            Assert.AreEqual(1, harness.Requests.Count);
            Assert.IsTrue(harness.Warnings.Any(warning => ReferenceEquals(warning.Exception, failure)));
            Assert.AreEqual(DueAfterThisStartup with { StartupCount = 10 }, harness.Store.Current, "失敗した選択は確定値になりません。");
        }
        finally
        {
            await harness.FinishAsync();
        }
    }

    /// <summary>所有者と、テストが操作する初期化・更新確認・UIへの受け渡し・表示の応答をまとめます。</summary>
    private sealed class Harness
    {
        private readonly List<GitHubStarPromptPresentationRequest> requests = [];

        private bool onUi;

        internal Harness(GitHubStarPromptRecord saved, bool subscribe = true, Func<Action, bool>? dispatch = null)
        {
            Store = new RecordingGitHubStarPromptStore(saved);
            Owner = new GitHubStarPromptWorkflowOwner(
                enabled: true,
                Store,
                () => Now,
                () =>
                {
                    UpdateTerminalRequested.TrySetResult();
                    return UpdateTerminal.Task;
                },
                dispatch ?? (action =>
                {
                    onUi = true;
                    try
                    {
                        action();
                    }
                    finally
                    {
                        onUi = false;
                    }
                    return true;
                }),
                (exception, context) => Warnings.Add((exception, context)));
            if (subscribe)
            {
                Owner.PresentationRequested += request =>
                {
                    HandlerCalledOnUi = onUi;
                    lock (requests)
                    {
                        requests.Add(request);
                    }
                    Respond(request);
                };
            }
        }

        internal RecordingGitHubStarPromptStore Store { get; }

        internal GitHubStarPromptWorkflowOwner Owner { get; }

        internal TaskCompletionSource<StartupInitializationResult> Initialization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<StartupUpdateWorkflowOutcome?> UpdateTerminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource UpdateTerminalRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<(Exception Exception, string Context)> Warnings { get; } = [];

        internal Action<GitHubStarPromptPresentationRequest> Respond { get; init; } = request => request.Complete(GitHubStarPromptChoice.Later);

        internal bool CanPresentValue { get; init; } = true;

        internal int CanPresentCount { get; private set; }

        internal bool CanPresentCalledOnUi { get; private set; }

        internal bool HandlerCalledOnUi { get; private set; }

        internal IReadOnlyList<GitHubStarPromptPresentationRequest> Requests
        {
            get
            {
                lock (requests)
                {
                    return requests.ToArray();
                }
            }
        }

        internal void Start()
        {
            Assert.IsTrue(Owner.Start(Initialization.Task, () =>
            {
                CanPresentCount++;
                CanPresentCalledOnUi = onUi;
                return CanPresentValue;
            }));
        }

        internal async Task<GitHubStarPromptRecord> AwaitFirstSaveAsync()
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(Store.FirstSaveAttempted.Task, Owner.Completion, "GitHub star prompt startup count save");
            return await Store.FirstSaveAttempted.Task;
        }

        /// <summary>保留中の入力を解放し、所有者の終端まで回収します。</summary>
        internal async Task FinishAsync()
        {
            Initialization.TrySetResult(new StartupInitializationResult(StartupInitializationOutcome.ShutdownRequested));
            UpdateTerminal.TrySetResult(StartupUpdateWorkflowOutcome.Closing);
            foreach (GitHubStarPromptPresentationRequest request in Requests)
            {
                request.CompleteNotPresented();
            }
            await Owner.Completion;
        }
    }
}
