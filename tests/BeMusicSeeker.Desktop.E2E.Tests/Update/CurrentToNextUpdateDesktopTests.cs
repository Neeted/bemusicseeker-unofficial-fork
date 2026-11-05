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

namespace BeMusicSeeker.Desktop.E2E.Tests.Update;

/// <summary>配布物のCurrentを実UIで次版へ更新し、再起動した新版で作業を続けられることと、同じ配信で更新が循環しないことを確認します。</summary>
[TestClass]
public sealed class CurrentToNextUpdateDesktopTests
{
    /// <summary>
    /// 条件: catalogのCurrentを専有コピーへ展開し、保持領域・管理外・旧管理ファイルの目印と、appの外のprofile・譜面3件を置く。
    /// ケース所有のloopback配信がNextの実bytesと実サイズ・SHA-256のmanifest(通常版1件)を返す。test sessionでCurrentを起動し、
    /// 更新ダイアログの適用をUIAで実行する。外部shellは記録窓口へ差し替わる。
    /// 期待結果: ダイアログは現在版=Current・最新版=Next・Next ZIPの項目1件(選択済み)を表示し、起動成功後に適用が有効になる。
    /// 適用で配布ページのURLがちょうど1回記録され、packageを1回取得し、更新確認はAppliedで終端してCurrentがexit 0で退出する。
    /// 別PIDの新版が`app`の実行ファイルから同じ引数で再起動し、タイトルがNextになる。新版の更新確認はNoUpdateで、配信の要求は
    /// manifest・package・manifestの3件だけである。検索が通り、通常Closeで新版もexit 0、所有Job全体が終端する。
    /// 終了後、管理ファイルがNext ZIPと一致し旧管理ファイルが除去され、保持領域・管理外のbytesと設定の全キーが変わらず、
    /// 回復登録が残らない。
    /// 検出する誤り: 版の比較・表示の取り違え、適用が配布ページを開かない・別URLを開く・実ブラウザを起動する、取得の重複や別資産、
    /// 失敗しつつ終了する経路、再起動の欠落・引数の欠落・旧binaryの継続、置換漏れ・旧管理ファイルの残存、保持領域や設定の消失、
    /// 同じ更新の再提示(循環)、新版が操作できない・異常終了・process残留・回復登録の漏れ。
    /// </summary>
    [TestMethod(DisplayName = "[SC-15A] 実アプリの更新ダイアログから次版へ更新し再起動後も更新が循環しない")]
    [TestCategory("ReleaseAcceptance")]
    [TestCategory("SC-15A")]
    public async Task PublishedCurrent_UpdatesToNextThroughTheDialogAndDoesNotLoop()
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
        void Step(string message) => progress.WriteLine($"{DateTime.UtcNow:O} remainingMs={deadline.Remaining.TotalMilliseconds:F0} {message}");
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

            // 起動完了を待たずに接続する。
            Step("before Current start");
            desktop = await DesktopApplicationSession.StartConnectedAsync(fixture.ExecutablePath, fixture.ProfileDirectory,
                fixture.SessionManifestPath, workspace.CaseId, fixture.UpdateManifestUrl, deadline);
            int currentPid = desktop.Driver.ProcessId;

            // 起動完了と更新ダイアログを並行に待つ。出現の前後は仮定しない。
            Task<ScenarioInitializationResult> startupTask = desktop.Driver.WaitForStartupAsync(deadline);
            Window dialog;
            try
            {
                // 起動が失敗で終わったら、ダイアログ待ちの期限を待たず失敗にする。
                dialog = UpdateDialogUi.WaitForDialog(desktop.Window, deadline, Step,
                    () => startupTask.IsCompleted && !(startupTask.IsCompletedSuccessfully && IsStartupSuccess(startupTask.Result)));
            }
            catch (Exception dialogFailure)
            {
                if (startupTask.IsCompleted)
                {
                    try { AssertStartupSucceeded(await startupTask); }
                    catch (Exception startupFailure) { throw new AggregateException("起動が成功せず、更新ダイアログも現れませんでした。", dialogFailure, startupFailure); }
                }
                throw;
            }
            ScenarioInitializationResult startup = await startupTask;
            AssertStartupSucceeded(startup);
            Step("after Current startup succeeded");
            StringAssert.Contains(desktop.Window.Title, current, "Currentのタイトルに現在版がありません。");
            Assert.IsFalse(desktop.Window.Title.Contains(next, StringComparison.Ordinal), "Currentのタイトルに次版が含まれています。");

            // 更新前のcatalogを記録する。
            ScenarioSnapshotCompleted before = await desktop.Driver.SnapshotAsync(deadline);
            Assert.AreEqual(3, before.Snapshot.Catalog.Length, "Currentの起動走査で3譜面が登録されていません。");
            // 更新前の基準は永続側(song.db)から取る。メモリ上のcatalogは新規走査のままで、永続値と一部の列(Tag・追加日時)が異なる。
            CurrentToNextUpdateFixture.PersistedTables persistedBefore = fixture.ReadPersistedTables();
            Assert.AreEqual(3, persistedBefore.Song.Count, "Currentの永続DBに3譜面がありません。");

            // 現在版・最新版・package項目を読み、項目を選択して適用の有効化を待つ。
            Assert.AreEqual(current, UpdateDialogUi.ReadCurrentVersion(dialog, deadline, Step), "ダイアログの現在版が実版と一致しません。");
            Assert.AreEqual(next, UpdateDialogUi.ReadLatestVersion(dialog, deadline, Step), "ダイアログの最新版がmanifestの版と一致しません。");
            IReadOnlyList<UpdateDialogUi.PackageItem> items = UpdateDialogUi.ReadPackages(dialog, deadline, Step);
            Step("package items " + string.Join(" | ", items.Select(item => string.Join(",", item.Texts) + " selected=" + item.IsSelected)));
            Assert.AreEqual(1, items.Count, "package一覧は通常版1件でなければなりません。");
            Assert.IsTrue(items[0].Texts.Contains(fixture.NextPackageFileName, StringComparer.Ordinal), "package項目にNext ZIPのファイル名がありません。");
            Assert.IsTrue(items[0].IsSelected, "package項目が初期選択されていません。");
            UpdateDialogUi.SelectPackage(dialog, fixture.NextPackageFileName, deadline, Step);
            AutomationElement apply = UpdateDialogUi.WaitApplyEnabled(dialog, deadline, Step);
            IReadOnlyList<LoopbackUpdateFeed.FeedRequest> beforeApply = fixture.Feed.Requests;
            Assert.AreEqual(1, beforeApply.Count, "適用前の配信要求はmanifest 1件だけでなければなりません。");
            AssertRequest(beforeApply[0], CurrentToNextUpdateFixture.ManifestPath);

            // UI入力より先に、再起動の期待と更新確認の終端要求を登録する。
            desktop.Driver.ExpectRestart();
            Task<ScenarioStartupUpdateCompleted> appliedTask = desktop.Driver.WaitForStartupUpdateAsync(deadline);
            // 同じwriterの後続要求の応答で、終端要求が製品に受付済みであることを確認する。
            _ = await desktop.Driver.TableStateAsync(deadline);

            // 適用をUIAで実行し、更新確認の終端を受け取る。
            UpdateDialogUi.Apply(apply, deadline, Step);
            ScenarioStartupUpdateCompleted applied = await appliedTask;
            Step($"Current update terminal outcome={applied.Outcome} prepared={applied.ShutdownPrepared}");
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Applied, applied.Outcome, "Currentの更新確認がAppliedで終端していません。");
            Assert.IsTrue(applied.ShutdownPrepared, "終了準備を完了せずに終端しています。");
            Assert.IsNull(applied.Failure, applied.Failure?.ToString());
            ScenarioExternalShellRecord opened = applied.ExternalShell.Single();
            Assert.AreEqual("Open", opened.Operation);
            Assert.AreEqual("Url", opened.Kind);
            Assert.AreEqual(fixture.ReleasePageUrl, opened.Target, "適用が開いたURLがmanifestの配布ページと一致しません。");

            // 旧processのexit 0・別PIDの所有Job内の後継への再接続を確認する。
            (int successorPid, _) = await desktop.RebindAfterRestartAsync(deadline);
            Step("after successor rebind pid=" + successorPid);
            Assert.AreNotEqual(currentPid, successorPid, "後継が同じPIDです。");
            using (var successor = Process.GetProcessById(successorPid))
            {
                Assert.IsTrue(string.Equals(Path.GetFullPath(fixture.ExecutablePath), successor.MainModule?.FileName, StringComparison.OrdinalIgnoreCase),
                    "後継の実行ファイルが `app` の BeMusicSeeker.exe ではありません: " + successor.MainModule?.FileName);
            }

            // 後継の起動完了と更新確認の終端を待つ。
            ScenarioInitializationResult restarted = await desktop.Driver.WaitForStartupAsync(deadline);
            AssertStartupSucceeded(restarted);
            ScenarioStartupUpdateCompleted noUpdate = await desktop.Driver.WaitForStartupUpdateAsync(deadline);
            Step($"Next update terminal outcome={noUpdate.Outcome}");
            Assert.AreEqual(StartupUpdateWorkflowOutcome.NoUpdate, noUpdate.Outcome, "新版の更新確認がNoUpdateで終端していません。");
            Assert.IsNull(noUpdate.Failure, noUpdate.Failure?.ToString());
            Assert.IsFalse(noUpdate.ShutdownPrepared);
            Assert.AreEqual(0, noUpdate.ExternalShell.Length, "新版が外部shellを要求しています。");
            Window[] visible = desktop.VisibleTopLevelWindows();
            Assert.AreEqual(1, visible.Length, "新版の可視最上位WindowはMainWindowだけでなければなりません。");
            Assert.AreEqual("BeMusicSeekerMainWindow", visible[0].AutomationId);
            Assert.AreEqual(0, desktop.Window.ModalWindows.Length, "新版に予期しない所有modalがあります。");
            StringAssert.Contains(desktop.Window.Title, next, "新版のタイトルに次版がありません。");
            Assert.IsFalse(desktop.Window.Title.Contains(current, StringComparison.Ordinal), "新版のタイトルに旧版が含まれています。");

            // 同じprofileのcatalogが継続し、通常の検索が確定する。
            ScenarioSnapshotCompleted after = await desktop.Driver.SnapshotAsync(deadline);
            Assert.AreEqual(3, after.Snapshot.Catalog.Length, "更新後に3譜面が登録されていません。");
            ScenarioKeywordCompleted keyword = await desktop.Driver.SetKeywordAsync(CurrentToNextUpdateFixture.SearchText, deadline);
            Assert.AreEqual(CurrentToNextUpdateFixture.SearchText, keyword.Keyword);
            CollectionAssert.AreEquivalent(
                fixture.Matching.Select(chart => Path.GetFullPath(chart.Path).ToUpperInvariant()).ToArray(),
                keyword.Snapshot.Visible.Select(chart => Path.GetFullPath(chart.Path).ToUpperInvariant()).ToArray(),
                "検索結果が一致譜面だけになっていません。");

            // 通常Closeから所有Job全体の終端まで待つ。
            Step("before normal Close");
            await desktop.CloseAsync(deadline);
            Step("after full owned process exit");

            // 終了後に配信の記録、配置、保持、設定、回復登録を確認する。
            CurrentToNextUpdateFixture.AssertPersistedTablesEqual(persistedBefore, fixture.ReadPersistedTables());
            AssertFeed(fixture, catalog);
            fixture.AssertInstallationMatches(catalog.Next.PackagePath);
            Assert.IsTrue(fixture.ExecutableMatches(catalog.Next.PackagePath), "実行ファイルがNext ZIPと一致しません。");
            Assert.IsFalse(fixture.ExecutableMatches(catalog.Current.PackagePath), "実行ファイルがCurrent ZIPのままです。");
            fixture.AssertSentinelsPreserved();
            fixture.AssertSettingsPreserved(fixture.ReadExecutableFileVersion());
            fixture.AssertNoRecoveryRegistrations();
            AssertNoResidue(fixture.AppRoot);
        }
        catch (Exception exception)
        {
            // 失敗を検出した時点で回収期限を確定し、以後の破棄と回収を同じ期限内で行う。
            BeginFailureCleanup();
            primary = exception;
        }
        await CleanUpAsync();
        if (primary is not null) { workspace.PreserveArtifacts(); System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); }

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

    private static bool IsStartupSuccess(ScenarioInitializationResult startup)
        => startup.Outcome == StartupInitializationOutcome.Succeeded && startup.CompletionPublished
            && startup.IsInitializationCompleted && startup.Failure is null;

    private static void AssertStartupSucceeded(ScenarioInitializationResult startup)
        => Assert.IsTrue(IsStartupSuccess(startup), $"実製品の初期化が成功していません: {startup}");

    private static void AssertRequest(LoopbackUpdateFeed.FeedRequest request, string path)
    {
        Assert.AreEqual("GET", request.Method);
        Assert.AreEqual(path, request.Path);
        Assert.AreEqual(200, request.Status);
        Assert.AreEqual(1, request.Generation);
    }

    /// <summary>配信の全記録が、manifest(旧版)→package(全bytes送信済み)→manifest(新版)の3件だけであることを確認します。</summary>
    private static void AssertFeed(CurrentToNextUpdateFixture fixture, DistributionCatalog catalog)
    {
        IReadOnlyList<LoopbackUpdateFeed.FeedRequest> requests = fixture.Feed.Requests;
        Step(requests);
        Assert.AreEqual(3, requests.Count, "配信の要求がmanifest・package・manifestの3件ではありません: "
            + string.Join(" | ", requests.Select(request => $"{request.Method} {request.Target} {request.Status}")));
        AssertRequest(requests[0], CurrentToNextUpdateFixture.ManifestPath);
        AssertRequest(requests[1], fixture.PackagePath);
        AssertRequest(requests[2], CurrentToNextUpdateFixture.ManifestPath);
        Assert.AreEqual(catalog.Next.SizeBytes, requests[1].SentBytes, "packageの全bytesを送信できていません。");
        foreach (LoopbackUpdateFeed.FeedRequest request in requests)
        {
            Assert.IsTrue(request.Completed && request.Failure is null, $"配信の応答が完了していません: {request.Target} {request.Failure}");
        }

        static void Step(IReadOnlyList<LoopbackUpdateFeed.FeedRequest> all)
            => Console.WriteLine("配信の要求: " + string.Join(" | ", all.Select(request => $"#{request.Sequence} {request.Method} {request.Target} -> {request.Status}")));
    }

    /// <summary>この配布コピーの実行ファイルから起動したprocessが残っていないことを確認します。</summary>
    private static void AssertNoResidue(string appRoot)
    {
        var residue = new List<string>();
        foreach (string name in new[] { "BeMusicSeeker", "BeMusicSeeker.Updater" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (path is not null && path.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase)) { residue.Add($"{process.Id}:{path}"); }
                    }
                    catch (System.ComponentModel.Win32Exception) { /* 終了済みまたは参照できないprocessは、この配布コピーの残留と判定できません。 */ }
                    catch (InvalidOperationException) { /* 列挙中に終了したprocessです。 */ }
                }
            }
        }
        Assert.AreEqual(0, residue.Count, "配布コピーのprocessが残留しています: " + string.Join(", ", residue));
    }
}
