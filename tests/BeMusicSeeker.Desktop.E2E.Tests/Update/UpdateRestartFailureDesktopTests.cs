using System.Diagnostics;
using System.IO;
using BeMusicSeeker.Desktop.E2E.Tests.Fixtures;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Components.Update;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;
using BeMusicSeeker.ViewModels;
using FlaUI.Core.AutomationElements;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.UpdateScenarioAssertions;

namespace BeMusicSeeker.Desktop.E2E.Tests.Update;

/// <summary>
/// 配布物のCurrentで更新後の再起動に失敗した場面を実アプリで通し、前版への巻き戻し・失敗記録・次回起動の通知・その後の正常な更新までを確認します。
/// </summary>
[TestClass]
public sealed class UpdateRestartFailureDesktopTests
{
    /// <summary>
    /// 条件: catalogのCurrentを専有コピーへ展開し、保持領域・管理外・旧管理ファイルの目印と、appの外のprofile・譜面3件を置く。
    /// ケース所有のloopback配信が、世代1ではNext ZIPの実行ファイルを実行できないbytesへ差し替えpackage専用ファイルを足した
    /// 手作りpackage(実bytesのサイズ・SHA-256のmanifest)を配る。test sessionでCurrentを起動し、更新ダイアログの適用をUIAで実行する。
    /// アプリの終了後、更新ツールの再起動先の起動が失敗する。その後に同じappとprofileでCurrentを明示起動し、通知をUIAで閉じて通常終了し、
    /// 配信を世代2(本物のNext)へ切り替えて再度起動し、更新を適用する。
    /// 期待結果: 手作りpackageでもアプリ側の取得・検証・終了合意は通常どおり終わり(Applied・終了準備済み・Currentがexit 0)、
    /// 所有Job全体が自動起動なしに終わる。更新前の構成へ戻り(管理一覧は更新前の一覧+旧管理ファイル、各管理ファイルは更新前のhash、
    /// 旧管理ファイルは元のbytes、package専用ファイルなし)、保持領域・管理外は変わらず、回復登録が残らず、再起動先の起動失敗を示す失敗記録が公開される。
    /// 次の明示起動では所有modalの通知が現れ、本文が失敗記録の全文を含み、表示中は記録が残り、OKを閉じた後だけ記録が消える。
    /// 通知した起動では更新確認を続けず(Failed終端・配信の要求なし)、前版は初期化に成功して操作でき、通常Closeでexit 0になる。
    /// 配信を世代2へ切り替えた次の起動では更新ダイアログだけが現れ(通知の再表示なし)、適用でNextへ再起動し、管理ファイルがNext ZIPと一致して
    /// 旧管理ファイルが除去され、保持領域・設定・永続DBが保たれる。配信の要求は5件(世代1: manifest・手作りpackage、世代2: manifest・Next package・manifest)。
    /// 検出する誤り: 再起動の失敗を確定扱いにする・一部だけ復元する・新規パスを取り残す・旧管理ファイルや一覧を復元しない、
    /// 巻き戻し後の旧アプリの自動起動、更新ツール・監視のprocess残留、回復登録の取り残し、失敗の記録漏れ、
    /// 再起動まで到達せず本体変更前に失敗した試行を巻き戻し成功と取り違えること、
    /// 次回起動で通知されない・別の内容を通知する・閉じる前に記録を消す・閉じても消えない、通知が起動を止める、
    /// 通知した起動での更新確認、失敗した試行の残留物による次の更新の失敗、失敗の再通知、巻き戻した一覧が次の更新に使われない。
    /// </summary>
    [TestMethod(DisplayName = "[SC-15C] 更新後の再起動に失敗すると前版へ戻り、次の起動で通知してから次の更新が通る")]
    [TestCategory("ReleaseAcceptance")]
    [TestCategory("SC-15C")]
    public async Task RestartFailureRollsBackNotifiesOnNextLaunchAndLaterUpdateSucceeds()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        // Desktopの期限規則: 外側の期限から回収用の10秒を除いた操作期限を全段階へ渡し、最初の失敗で回収期限を一度だけ確定する。
        var outerDeadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromSeconds(240));
        TimeSpan remaining = outerDeadline.Remaining;
        if (remaining <= TimeSpan.FromSeconds(10)) { throw new TimeoutException("入力と回収に必要な残期限がありません。"); }
        ProcessDeadline deadline = outerDeadline.LimitRemaining(remaining - TimeSpan.FromSeconds(10));
        ProcessDeadline? failureDeadline = null;
        void BeginFailureCleanup() => failureDeadline ??= outerDeadline.LimitRemaining(TimeSpan.FromSeconds(10));
        ProcessDeadline CleanupDeadline() => failureDeadline ?? deadline;
        using var progress = new StreamWriter(workspace.GetPath("uia-steps.log")) { AutoFlush = true };
        var elapsed = Stopwatch.StartNew();
        void Step(string message) => progress.WriteLine($"{DateTime.UtcNow:O} elapsedMs={elapsed.ElapsedMilliseconds} remainingMs={deadline.Remaining.TotalMilliseconds:F0} {message}");
        using var operation = new CancellationTokenSource(deadline.Remaining);
        CurrentToNextUpdateFixture? fixture = null;
        DesktopApplicationSession? desktop = null;
        Exception? primary = null;
        try
        {
            string repository = UpdateDeploymentTestSupport.FindRepositoryRoot();
            DistributionCatalog catalog = await DistributionCatalog.ReadFromEnvironmentAsync(repository, deadline);
            Console.WriteLine($"受入入力: Current={catalog.Current.Version}, Next={catalog.Next.Version}, Next ZIP={catalog.Next.PackagePath}; ソースbuildの実行体とは別のcatalog配布を使用します。");
            fixture = await CurrentToNextUpdateFixture.CreateAsync(workspace, catalog, operation.Token, deadline);
            string current = catalog.Current.Version;
            string next = catalog.Next.Version;
            IReadOnlyDictionary<string, string> currentHashes = CurrentToNextUpdateFixture.ReadZipHashes(catalog.Current.PackagePath);
            IReadOnlyDictionary<string, string> nextHashes = CurrentToNextUpdateFixture.ReadZipHashes(catalog.Next.PackagePath);
            RestartFailurePackage broken = await RestartFailurePackage.CreateAsync(catalog.Next.PackagePath, next, currentHashes, nextHashes);
            Assert.AreNotEqual(fixture.NextPackageFileName, broken.FileName, "手作りpackageのファイル名がNext ZIPと同じでは配信の要求を区別できません。");
            Assert.AreEqual(1, fixture.PublishPackage(broken.FileName, broken.Bytes, "SC-15C restart failure"), "手作りpackageを世代1として公開できていません。");
            Step($"prepared broken package bytes={broken.Bytes.Length}");
            string failureRecord = Path.Combine(fixture.AppRoot, "update_work", "update-failure.txt");
            string failureRecordTemporary = failureRecord + ".tmp";

            // ---- 起動1: 手作りpackageを適用する。アプリは通常どおり終了し、失敗は終了後の更新ツールで起こる。 ----
            Step("before Current start #1");
            desktop = await DesktopApplicationSession.StartConnectedAsync(fixture.ExecutablePath, fixture.ProfileDirectory,
                workspace.GetPath("session-1.json"), workspace.CaseId, fixture.UpdateManifestUrl, deadline);
            Task<ScenarioInitializationResult> startupTask = desktop.Driver.WaitForStartupAsync(deadline);
            Window dialog = WaitForUpdateDialog(desktop, startupTask, deadline, Step);
            AssertStartupSucceeded(await startupTask);
            StringAssert.Contains(desktop.Window.Title, current, "Currentのタイトルに現在版がありません。");
            Assert.IsFalse(desktop.Window.Title.Contains(next, StringComparison.Ordinal), "Currentのタイトルに次版が含まれています。");

            ScenarioSnapshotCompleted before = await desktop.Driver.SnapshotAsync(deadline);
            Assert.AreEqual(3, before.Snapshot.Catalog.Length, "Currentの起動走査で3譜面が登録されていません。");
            CurrentToNextUpdateFixture.PersistedTables persistedBefore = fixture.ReadPersistedTables();
            Assert.AreEqual(3, persistedBefore.Song.Count, "Currentの永続DBに3譜面がありません。");

            IReadOnlyList<UpdateDialogUi.PackageItem> items = UpdateDialogUi.ReadPackages(dialog, deadline, Step);
            Assert.AreEqual(1, items.Count, "package一覧は手作りpackage1件でなければなりません。");
            Assert.IsTrue(items[0].Texts.Contains(broken.FileName, StringComparer.Ordinal), "package項目に手作りpackageのファイル名がありません。");
            UpdateDialogUi.SelectPackage(dialog, broken.FileName, deadline, Step);
            AutomationElement apply = UpdateDialogUi.WaitApplyEnabled(dialog, deadline, Step);
            Assert.AreEqual(1, fixture.Feed.Requests.Count, "適用前の配信要求はmanifest 1件だけでなければなりません。");
            AssertRequest(fixture.Feed.Requests[0], CurrentToNextUpdateFixture.ManifestPath);

            // UI入力より先に、自然終了の期待と更新確認の終端要求を登録する。
            desktop.Driver.ExpectNaturalExit();
            Task<ScenarioStartupUpdateCompleted> appliedTask = desktop.Driver.WaitForStartupUpdateAsync(deadline);
            _ = await desktop.Driver.TableStateAsync(deadline);
            UpdateDialogUi.Apply(apply, deadline, Step);
            ScenarioStartupUpdateCompleted applied = await appliedTask;
            Step($"first update terminal outcome={applied.Outcome} prepared={applied.ShutdownPrepared}");
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Applied, applied.Outcome, "手作りpackageの更新確認がAppliedで終端していません。");
            Assert.IsTrue(applied.ShutdownPrepared, "終了準備を完了せずに終端しています。");
            Assert.IsNull(applied.Failure, applied.Failure?.ToString());
            ScenarioExternalShellRecord opened = applied.ExternalShell.Single();
            Assert.AreEqual("Open", opened.Operation);
            Assert.AreEqual("Url", opened.Kind);
            Assert.AreEqual(fixture.ReleasePageUrl, opened.Target, "適用が開いたURLがmanifestの配布ページと一致しません。");

            // Currentのexit 0と、所有Job(更新ツール・監視を含む)が自動起動なしに空になることを待つ。
            await desktop.Driver.WaitForExpectedExitAsync(deadline);
            Step("after first owned process exit");
            await RetireAsync();

            // 所有Job終端後の点検: 更新前への巻き戻し、保持、自動起動なし、回復登録なし、失敗記録の公開。
            fixture.AssertInstallationRolledBackTo(catalog.Current.PackagePath, RestartFailurePackage.PackageOnlyFileName);
            fixture.AssertSentinelsPreserved();
            AssertNoResidue(fixture.AppRoot);
            fixture.AssertNoRecoveryRegistrations();
            Assert.IsTrue(File.Exists(failureRecord), "失敗記録 update-failure.txt が公開されていません。");
            Assert.IsFalse(File.Exists(failureRecordTemporary), "失敗記録が一時名のまま残っています。");
            string record = File.ReadAllText(failureRecord);
            Assert.IsFalse(string.IsNullOrWhiteSpace(record), "失敗記録の内容が空です。");
            // 再起動先の起動失敗(Process.Start)でだけ生じる例外型で、本体変更前の検査の失敗など別原因の失敗と区別する。
            Assert.IsTrue(record.Contains("System.ComponentModel.Win32Exception", StringComparison.Ordinal),
                "失敗記録が再起動先の起動失敗(Win32Exception)ではありません。記録の先頭: " + record[..Math.Min(record.Length, 600)]);
            Step($"failure record chars={record.Length}");
            IReadOnlyList<LoopbackUpdateFeed.FeedRequest> afterFirst = fixture.Feed.Requests;
            Assert.AreEqual(2, afterFirst.Count, "最初の更新の配信要求はmanifestと手作りpackageの2件でなければなりません: " + Describe(afterFirst));
            AssertRequest(afterFirst[0], CurrentToNextUpdateFixture.ManifestPath);
            AssertRequest(afterFirst[1], CurrentToNextUpdateFixture.PackagePathOf(broken.FileName));
            Assert.AreEqual(broken.Bytes.LongLength, afterFirst[1].SentBytes, "手作りpackageの全bytesを送信できていません。");
            Assert.IsTrue(afterFirst.All(request => request.Completed && request.Failure is null), "配信の応答が完了していません: " + Describe(afterFirst));

            // ---- 起動2: 同じappとprofileでCurrentを明示起動する。通知が現れ、OKで閉じた後だけ記録が消える。 ----
            Step("before Current start #2");
            desktop = await DesktopApplicationSession.StartConnectedAsync(fixture.ExecutablePath, fixture.ProfileDirectory,
                workspace.GetPath("session-2.json"), workspace.CaseId, fixture.UpdateManifestUrl, deadline);
            Task<ScenarioInitializationResult> startupTask2 = desktop.Driver.WaitForStartupAsync(deadline);
            Task<ScenarioStartupUpdateCompleted> failedTask = desktop.Driver.WaitForStartupUpdateAsync(deadline);
            Window notification = UpdateFailureNotificationUi.WaitForNotification(desktop.Window, deadline, Step,
                () => startupTask2.IsCompleted && !(startupTask2.IsCompletedSuccessfully && IsStartupSuccess(startupTask2.Result)));
            string message = UpdateFailureNotificationUi.ReadMessage(notification);
            Step($"notification message chars={message.Length}");
            StringAssert.Contains(NormalizeText(message), NormalizeText(record), "通知の本文が失敗記録の全文を含んでいません。");
            Assert.IsTrue(File.Exists(failureRecord), "通知の表示中に失敗記録が消えています。");
            ScenarioStartupUpdateCompleted failed = await failedTask;
            Step($"second update terminal outcome={failed.Outcome}");
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, failed.Outcome, "失敗記録を通知した起動の更新確認がFailedで終端していません。");
            Assert.IsFalse(failed.ShutdownPrepared);
            Assert.IsNotNull(failed.Failure, "Failedの終端にfailureがありません。");
            StringAssert.Contains(NormalizeText(failed.Failure.Message), NormalizeText(record), "終端のfailureが失敗記録の内容を含んでいません。");
            Assert.AreEqual(0, failed.ExternalShell.Length, "失敗通知の起動が外部shellを要求しています。");

            Assert.IsTrue(File.Exists(failureRecord), "OKの直前に失敗記録が消えています。");
            UpdateFailureNotificationUi.Acknowledge(notification, deadline, Step);
            UpdateFailureNotificationUi.WaitForDismissed(desktop.Window, deadline, Step);
            WaitUntil(() => !File.Exists(failureRecord) && !File.Exists(failureRecordTemporary), deadline, "OKで閉じた後に失敗記録が消えません。");
            Step("failure record consumed");

            AssertStartupSucceeded(await startupTask2);
            StringAssert.Contains(desktop.Window.Title, current, "前版のタイトルに現在版がありません。");
            Assert.IsFalse(desktop.Window.Title.Contains(next, StringComparison.Ordinal), "前版のタイトルに次版が含まれています。");
            Assert.AreEqual(0, desktop.Window.ModalWindows.Length, "通知を閉じた後に所有modalが残っています。");
            ScenarioSnapshotCompleted restored = await desktop.Driver.SnapshotAsync(deadline);
            Assert.AreEqual(3, restored.Snapshot.Catalog.Length, "巻き戻した前版で3譜面が登録されていません。");
            ScenarioKeywordCompleted keyword = await desktop.Driver.SetKeywordAsync(CurrentToNextUpdateFixture.SearchText, deadline);
            Assert.AreEqual(CurrentToNextUpdateFixture.SearchText, keyword.Keyword);
            CollectionAssert.AreEquivalent(
                fixture.Matching.Select(chart => Path.GetFullPath(chart.Path).ToUpperInvariant()).ToArray(),
                keyword.Snapshot.Visible.Select(chart => Path.GetFullPath(chart.Path).ToUpperInvariant()).ToArray(),
                "検索結果が一致譜面だけになっていません。");
            Assert.AreEqual(2, fixture.Feed.Requests.Count, "通知した起動が配信へ要求しています(更新確認を続けてはいけません): " + Describe(fixture.Feed.Requests));

            await desktop.CloseAsync(deadline);
            Step("after second owned process exit");
            await RetireAsync();
            fixture.AssertInstallationRolledBackTo(catalog.Current.PackagePath, RestartFailurePackage.PackageOnlyFileName);
            fixture.AssertSentinelsPreserved();
            AssertNoResidue(fixture.AppRoot);
            fixture.AssertNoRecoveryRegistrations();
            Assert.IsFalse(File.Exists(failureRecord) || File.Exists(failureRecordTemporary), "終了後に失敗記録が残っています。");

            // ---- 起動3: 配信を本物のNextへ切り替える。通知の再表示なしに更新ダイアログだけが現れ、更新が通る。 ----
            Assert.AreEqual(2, fixture.PublishNextPackage(), "Nextを世代2として公開できていません。");
            Step("before Current start #3");
            desktop = await DesktopApplicationSession.StartConnectedAsync(fixture.ExecutablePath, fixture.ProfileDirectory,
                workspace.GetPath("session-3.json"), workspace.CaseId, fixture.UpdateManifestUrl, deadline);
            Task<ScenarioInitializationResult> startupTask3 = desktop.Driver.WaitForStartupAsync(deadline);
            Window dialog3 = WaitForUpdateDialog(desktop, startupTask3, deadline, Step);
            AssertStartupSucceeded(await startupTask3);
            Assert.AreEqual(next, UpdateDialogUi.ReadLatestVersion(dialog3, deadline, Step), "ダイアログの最新版がmanifestの版と一致しません。");
            IReadOnlyList<UpdateDialogUi.PackageItem> items3 = UpdateDialogUi.ReadPackages(dialog3, deadline, Step);
            Assert.AreEqual(1, items3.Count, "package一覧は通常版1件でなければなりません。");
            Assert.IsTrue(items3[0].Texts.Contains(fixture.NextPackageFileName, StringComparer.Ordinal), "package項目にNext ZIPのファイル名がありません。");
            Assert.IsTrue(items3[0].IsSelected, "package項目が初期選択されていません。");
            UpdateDialogUi.SelectPackage(dialog3, fixture.NextPackageFileName, deadline, Step);
            AutomationElement apply3 = UpdateDialogUi.WaitApplyEnabled(dialog3, deadline, Step);
            int currentPid3 = desktop.Driver.ProcessId;

            desktop.Driver.ExpectRestart();
            Task<ScenarioStartupUpdateCompleted> appliedTask3 = desktop.Driver.WaitForStartupUpdateAsync(deadline);
            _ = await desktop.Driver.TableStateAsync(deadline);
            UpdateDialogUi.Apply(apply3, deadline, Step);
            ScenarioStartupUpdateCompleted applied3 = await appliedTask3;
            Step($"third update terminal outcome={applied3.Outcome} prepared={applied3.ShutdownPrepared}");
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Applied, applied3.Outcome, "Nextの更新確認がAppliedで終端していません。");
            Assert.IsTrue(applied3.ShutdownPrepared, "終了準備を完了せずに終端しています。");
            Assert.IsNull(applied3.Failure, applied3.Failure?.ToString());
            ScenarioExternalShellRecord opened3 = applied3.ExternalShell.Single();
            Assert.AreEqual("Open", opened3.Operation);
            Assert.AreEqual("Url", opened3.Kind);
            Assert.AreEqual(fixture.ReleasePageUrl, opened3.Target, "適用が開いたURLがmanifestの配布ページと一致しません。");

            (int successorPid, _) = await desktop.RebindAfterRestartAsync(deadline);
            Step("after successor rebind pid=" + successorPid);
            Assert.AreNotEqual(currentPid3, successorPid, "後継が同じPIDです。");
            using (var successor = Process.GetProcessById(successorPid))
            {
                Assert.IsTrue(string.Equals(Path.GetFullPath(fixture.ExecutablePath), successor.MainModule?.FileName, StringComparison.OrdinalIgnoreCase),
                    "後継の実行ファイルが `app` の BeMusicSeeker.exe ではありません: " + successor.MainModule?.FileName);
            }
            ScenarioInitializationResult restarted = await desktop.Driver.WaitForStartupAsync(deadline);
            AssertStartupSucceeded(restarted);
            ScenarioStartupUpdateCompleted noUpdate = await desktop.Driver.WaitForStartupUpdateAsync(deadline);
            Assert.AreEqual(StartupUpdateWorkflowOutcome.NoUpdate, noUpdate.Outcome, "新版の更新確認がNoUpdateで終端していません。");
            Assert.IsNull(noUpdate.Failure, noUpdate.Failure?.ToString());
            Assert.IsFalse(noUpdate.ShutdownPrepared);
            Assert.AreEqual(0, noUpdate.ExternalShell.Length, "新版が外部shellを要求しています。");
            Assert.AreEqual(0, desktop.Window.ModalWindows.Length, "新版に予期しない所有modalがあります。");
            StringAssert.Contains(desktop.Window.Title, next, "新版のタイトルに次版がありません。");
            Assert.IsFalse(desktop.Window.Title.Contains(current, StringComparison.Ordinal), "新版のタイトルに旧版が含まれています。");

            await desktop.CloseAsync(deadline);
            Step("after third owned process exit");

            // 終了後の最終点検。
            CurrentToNextUpdateFixture.AssertPersistedTablesEqual(persistedBefore, fixture.ReadPersistedTables());
            fixture.AssertInstallationMatches(catalog.Next.PackagePath);
            Assert.IsTrue(fixture.ExecutableMatches(catalog.Next.PackagePath), "実行ファイルがNext ZIPと一致しません。");
            Assert.IsFalse(fixture.ExecutableMatches(catalog.Current.PackagePath), "実行ファイルがCurrent ZIPのままです。");
            Assert.IsFalse(File.Exists(Path.Combine(fixture.AppRoot, RestartFailurePackage.PackageOnlyFileName)), "手作りpackage専用ファイルが残っています。");
            fixture.AssertSentinelsPreserved();
            fixture.AssertSettingsPreserved(fixture.ReadExecutableFileVersion());
            fixture.AssertNoRecoveryRegistrations();
            AssertNoResidue(fixture.AppRoot);
            Assert.IsFalse(File.Exists(failureRecord) || File.Exists(failureRecordTemporary), "最終的に失敗記録が残っています。");
            AssertFeed(fixture, broken, catalog);
            Step("completed");
            Console.WriteLine($"SC-15C 所要: {elapsed.Elapsed.TotalSeconds:F1}秒");
        }
        catch (Exception exception)
        {
            // 失敗を検出した時点で回収期限を確定し、以後の破棄と回収を同じ期限内で行う。
            BeginFailureCleanup();
            primary = exception;
        }
        await CleanUpAsync();
        if (primary is not null) { workspace.PreserveArtifacts(); System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); }

        // 次の起動の前に、現在のsessionのUIAとdriverを回収する。
        async Task RetireAsync()
        {
            DesktopApplicationSession? retired = desktop;
            desktop = null;
            if (retired is not null) { await retired.DisposeAsync(CleanupDeadline()); }
        }

        async Task CleanUpAsync()
        {
            var failures = new List<Exception>();
            if (desktop is not null)
            {
                try { await desktop.DisposeAsync(CleanupDeadline()); }
                catch (Exception cleanup) { failures.Add(cleanup); }
            }
            if (fixture is not null)
            {
                // 全所有processの終端後に、残った回復登録だけを消す。成功時は0件のはずなので何も消さない。
                try { fixture.DeleteRecoveryRegistrations(); }
                catch (Exception cleanup) { failures.Add(cleanup); }
                try { await fixture.DisposeAsync(CleanupDeadline()); }
                catch (Exception cleanup) { failures.Add(cleanup); }
            }
            if (failures.Count == 0) { return; }
            workspace.PreserveArtifacts();
            if (primary is null) { primary = new AggregateException("更新受入の回収に失敗しました。", failures); }
            else { primary.Data["DesktopCleanupFailure"] = new AggregateException(failures); }
        }
    }

    /// <summary>起動完了と更新ダイアログを並行に待ちます。起動が失敗で終われば、ダイアログの期限を待たず失敗にします。</summary>
    private static Window WaitForUpdateDialog(DesktopApplicationSession desktop, Task<ScenarioInitializationResult> startupTask,
        ProcessDeadline deadline, Action<string> step)
    {
        try
        {
            return UpdateDialogUi.WaitForDialog(desktop.Window, deadline, step,
                () => startupTask.IsCompleted && !(startupTask.IsCompletedSuccessfully && IsStartupSuccess(startupTask.Result)));
        }
        catch (Exception dialogFailure)
        {
            if (startupTask.IsCompleted)
            {
                try { AssertStartupSucceeded(startupTask.GetAwaiter().GetResult()); }
                catch (Exception startupFailure) { throw new AggregateException("起動が成功せず、更新ダイアログも現れませんでした。", dialogFailure, startupFailure); }
            }
            throw;
        }
    }

    /// <summary>改行表記の差(CRLF・CR・LF)と全体の前後の空白だけを吸収します。行ごとの空白は変えません。</summary>
    private static string NormalizeText(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static void WaitUntil(Func<bool> condition, ProcessDeadline deadline, string timeoutMessage)
        => _ = FlaUI.Core.Tools.Retry.WhileFalse(condition, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false, timeoutMessage: timeoutMessage);

    private static string Describe(IReadOnlyList<LoopbackUpdateFeed.FeedRequest> requests)
        => string.Join(" | ", requests.Select(request => $"#{request.Sequence} g{request.Generation} {request.Method} {request.Target} {request.Status}"));

    /// <summary>
    /// 配信の全記録が、世代1(manifest・手作りpackage)と世代2(manifest・Next package・manifest)の5件だけであることを確認します。
    /// </summary>
    private static void AssertFeed(CurrentToNextUpdateFixture fixture, RestartFailurePackage broken, DistributionCatalog catalog)
    {
        IReadOnlyList<LoopbackUpdateFeed.FeedRequest> requests = fixture.Feed.Requests;
        Console.WriteLine("配信の要求: " + Describe(requests));
        Assert.AreEqual(5, requests.Count, "配信の要求が5件ではありません: " + Describe(requests));
        AssertRequest(requests[0], CurrentToNextUpdateFixture.ManifestPath, generation: 1);
        AssertRequest(requests[1], CurrentToNextUpdateFixture.PackagePathOf(broken.FileName), generation: 1);
        AssertRequest(requests[2], CurrentToNextUpdateFixture.ManifestPath, generation: 2);
        AssertRequest(requests[3], fixture.PackagePath, generation: 2);
        AssertRequest(requests[4], CurrentToNextUpdateFixture.ManifestPath, generation: 2);
        Assert.AreEqual(broken.Bytes.LongLength, requests[1].SentBytes, "手作りpackageの全bytesを送信できていません。");
        Assert.AreEqual(catalog.Next.SizeBytes, requests[3].SentBytes, "Next packageの全bytesを送信できていません。");
        foreach (LoopbackUpdateFeed.FeedRequest request in requests)
        {
            Assert.IsTrue(request.Completed && request.Failure is null, $"配信の応答が完了していません: {request.Target} {request.Failure}");
        }
    }
}
