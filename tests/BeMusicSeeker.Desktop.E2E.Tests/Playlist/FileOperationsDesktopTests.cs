using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.Desktop.E2E.Tests.Fixtures;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.ViewModels;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using SQLite;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.DesktopMutationOracle;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.DesktopScenarioUi;
using DrawingPoint = System.Drawing.Point;

namespace BeMusicSeeker.Desktop.E2E.Tests.Playlist;

/// <summary>実Drop・F2・確認modalを入口に、ファイルと保存値を独立入力へ照合します。</summary>
[TestClass]
public sealed class FileOperationsDesktopTests
{
    /// <summary>実FileDropのZIP導入、通常行から普通folderへのdrag、物理改名の取消/確定、空playlist folderの削除拒否/承認を一回ずつ確認します。</summary>
    [TestMethod(DisplayName = "[DT-02] 実ドロップ・物理改名・playlist folder削除")]
    [TestCategory("Daily")]
    public async Task FileDropRenameAndFolderDelete_UseRealInputAndIndependentStorage()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var outerDeadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromMinutes(3));
        TimeSpan remaining = outerDeadline.Remaining;
        if (remaining <= TimeSpan.FromSeconds(10)) { throw new TimeoutException("入力と回収に必要な残期限がありません。"); }
        ProcessDeadline deadline = outerDeadline.LimitRemaining(remaining - TimeSpan.FromSeconds(10));
        ProcessDeadline? failureDeadline = null;
        void BeginFailureCleanup() => failureDeadline ??= outerDeadline.LimitRemaining(TimeSpan.FromSeconds(10));
        ProcessDeadline CleanupDeadline() => failureDeadline ?? deadline;
        using var progress = new StreamWriter(workspace.GetPath("uia-steps.log")) { AutoFlush = true };
        object progressGate = new();
        void Step(string text)
        {
            lock (progressGate)
            {
                progress.WriteLine($"{DateTime.UtcNow:O} remainingMs={deadline.Remaining.TotalMilliseconds:F0} thread={Environment.CurrentManagedThreadId} {text}");
            }
        }
        void RecordFailure(Exception failure)
        {
            try
            {
                Step("case failure: " + failure);
                foreach (System.Collections.DictionaryEntry detail in failure.Data)
                { Step($"case failure detail {detail.Key}: {detail.Value}"); }
            }
            catch (Exception diagnostic) { failure.Data["DesktopStepDiagnosticsFailure"] = diagnostic; }
        }
        DesktopPlaylistFixture? fixture = null;
        Exception? caseFailure = null;
        try
        {
            fixture = DesktopPlaylistFixture.CreateFileOperations(workspace);
            AssertInitialEmptyFolders(fixture);
            DesktopPackageInput package = fixture.Package ?? throw new InvalidOperationException("ZIP入力がありません。");
            byte[] originalZip = File.ReadAllBytes(package.ZipPath);
            DesktopApplicationSession session = await DesktopApplicationSession.StartAsync(fixture,
                workspace.GetPath("session.json"), workspace.CaseId, deadline);
            Exception? primary = null;
            try
            {
                nint ownedWindow = session.Window.Properties.NativeWindowHandle.Value;
                ActivateOwnedCaption(session.Window, session.Driver.ProcessId, ownedWindow, deadline, Step);
                RequireOwnedForeground(session.Driver.ProcessId, ownedWindow, "after initial owned caption click", Step);
                AutomationElement table = await ShowLibraryAsync(session, 2, deadline, Step);
                string[] beforeSongs = ReadTable(fixture.SongDatabasePath, "song");
                await DropZipAsync(session, fixture, package, table, deadline, Step, BeginFailureCleanup, CleanupDeadline);
                string installedPath = ReadInstalledPath(fixture, package);
                CollectionAssert.AreEqual(originalZip, File.ReadAllBytes(package.ZipPath));
                AssertChartFiles(package.Chart, package.Chart.Path);
                foreach (DesktopChartInput chart in fixture.Charts) { AssertChartFiles(chart, chart.Path); AssertSong(fixture.SongDatabasePath, chart, chart.Path); }
                AssertExistingSongsUnchanged(fixture, beforeSongs, package.Chart.Md5);
                AssertLibraryFiles(fixture, installedPath, fixture.Charts[0].Path);
                // TreeItemの展開は選択と詳細表示を開始し得る。展開を先に済ませ、Libraryの実採用後にsourceを取得する。
                TreeItem playlist = ExpandPlaylist(session, deadline, Step);
                table = await ShowLibraryAsync(session, 3, deadline, Step);
                AutomationElement imported = FindLibraryRow(table, package.Chart, installedPath);
                AutomationElement importFolder = FindUnique(playlist, "PlaylistFolder:" + DesktopPlaylistFixture.ImportFolderName, deadline, Step);
                imported.Patterns.SelectionItem.Pattern.Select();
                string importedId = imported.AutomationId;
                var dropTarget = new ScenarioPlaylistDropTarget([importedId], DesktopPlaylistFixture.PlaylistId, DesktopPlaylistFixture.ImportFolderName);
                ScenarioObservationRegistered dropRegistration = await session.Driver.RegisterObservationAsync(dropTarget, deadline);
                Assert.AreEqual("Library", dropRegistration.TableState.OperationSection);
                Assert.AreEqual("Library", dropRegistration.TableState.SourceScope);
                Assert.AreEqual(3, dropRegistration.TableState.Count);
                Step("after playlist drop ACK Library/count=3; before real row drag");
                DragRow(FindUnique(imported, "cell:Status", deadline, Step), importFolder, deadline, BeginFailureCleanup, Step);
                Step("after row gesture/owned Up; before playlist drop receipt");
                ScenarioObservationCompleted drop = await session.Driver.AwaitObservationAsync(dropRegistration.RequestId, deadline);
                Assert.AreEqual(dropRegistration.RequestId, drop.RegistrationRequestId);
                Assert.IsNull(drop.Result.TaskFailure);
                Assert.IsInstanceOfType<ScenarioPlaylistDropTarget>(drop.Result.Target);
                var echoedDrop = (ScenarioPlaylistDropTarget)drop.Result.Target;
                CollectionAssert.AreEqual(new[] { importedId }, echoedDrop.RowAutomationIds.ToArray());
                Assert.AreEqual(DesktopPlaylistFixture.PlaylistId, echoedDrop.PlaylistId);
                Assert.AreEqual(DesktopPlaylistFixture.ImportFolderName, echoedDrop.FolderName);
                AssertRegularFacts(drop.Result, package.Chart, installedPath);
                AssertMembership(fixture, package.Chart, deleteFolderPresent: true);
                Step("after real row drag Task/raw membership");
                table = await ShowLibraryAsync(session, 3, deadline, Step);
                DesktopChartInput rename = fixture.Charts[0];
                DesktopChartInput sentinel = fixture.Charts[1];
                string[] beforeRename = ReadTable(fixture.SongDatabasePath, "song");
                string[] beforeRenameOther = ReadTable(fixture.SongDatabasePath, "song", "path", "folder", "parent");
                AutomationElement renameRow = FindLibraryRow(table, rename, rename.Path);
                await RenameFolderAsync(session, table, renameRow, rename, commit: false, deadline, Step);
                AutomationElement canceledFolder = renameRow.FindFirstChild(cf => cf.ByAutomationId("cell:Folder"))
                    ?? throw new AssertFailedException("取消後の同じ対象行にFOLDERセルがありません。");
                Assert.AreEqual(DesktopPlaylistFixture.RenameOriginalBasename, canceledFolder.Patterns.Value.Pattern.Value.Value,
                    "取消後の確定表示が元のフォルダ名へ戻っていません。");
                Assert.AreEqual(0, canceledFolder.FindAllChildren(cf => cf.ByAutomationId("CellEditor")).Length,
                    "取消後も同じFOLDERセルにeditorが残っています。");
                CollectionAssert.AreEqual(beforeRename, ReadTable(fixture.SongDatabasePath, "song"));
                AssertChartFiles(rename, rename.Path);
                string renamedDirectory = Path.Combine(fixture.LibraryRoot, DesktopPlaylistFixture.RenameCommittedBasename);
                Assert.IsFalse(Directory.Exists(renamedDirectory));
                await RenameFolderAsync(session, table, renameRow, rename, commit: true, deadline, Step);
                string renamedPath = Path.Combine(renamedDirectory, Path.GetFileName(rename.Path));
                Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(rename.Path)));
                AssertChartFiles(rename, renamedPath);
                AssertSong(fixture.SongDatabasePath, rename, renamedPath);
                AssertChartFiles(sentinel, sentinel.Path);
                AssertSong(fixture.SongDatabasePath, sentinel, sentinel.Path);
                CollectionAssert.AreEqual(beforeRenameOther, ReadTable(fixture.SongDatabasePath, "song", "path", "folder", "parent"));
                table = await ShowLibraryAsync(session, 3, deadline, Step);
                AutomationElement renamedRow = FindLibraryRow(table, rename, renamedPath);
                Assert.AreEqual(DesktopPlaylistFixture.RenameCommittedBasename,
                    FindUnique(renamedRow, "cell:Folder", deadline, Step).Patterns.Value.Pattern.Value.Value);
                await DeleteEmptyFolderAsync(session, fixture, approve: false, deadline, Step);
                await DeleteEmptyFolderAsync(session, fixture, approve: true, deadline, Step);
                AssertMembership(fixture, package.Chart, deleteFolderPresent: false);
                CollectionAssert.AreEquivalent(new[] { DesktopPlaylistFixture.ImportFolderName }, ReadFolders(fixture.SongDatabasePath));
                CollectionAssert.AreEqual(originalZip, File.ReadAllBytes(package.ZipPath));
                Assert.AreEqual(installedPath, ReadInstalledPath(fixture, package));
                AssertLibraryFiles(fixture, installedPath, renamedPath);
                AssertChartFiles(sentinel, sentinel.Path);
                Step("before normal Close");
                await session.CloseAsync(deadline);
                Step("after full owned process exit");
            }
            catch (Exception failure) { BeginFailureCleanup(); primary = failure; throw; }
            finally
            {
                try { await session.DisposeAsync(CleanupDeadline()); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["DesktopCleanupFailure"] = cleanup; }
            }
        }
        catch (Exception failure) { BeginFailureCleanup(); caseFailure = failure; workspace.PreserveArtifacts(); RecordFailure(failure); throw; }
        finally
        {
            if (fixture is not null)
            {
                try { await fixture.DisposeAsync(CleanupDeadline()); }
                catch (Exception cleanup)
                {
                    workspace.PreserveArtifacts();
                    RecordFailure(cleanup);
                    if (caseFailure is null) { throw; }
                    caseFailure.Data["DesktopFixtureCleanupFailure"] = cleanup;
                }
            }
        }
    }

    /// <summary>旧schema確認を実Cancelして自然退出とDB不変を待ち、同データの次の明示processで実OK・修復・通常検索へ進めます。</summary>
    [TestMethod(DisplayName = "[DT-02] 旧schemaの実取消・自然退出と次起動の承認")]
    [TestCategory("Daily")]
    public async Task OldSchemaCancelThenApprove_PreservesDataAcrossExplicitProcesses()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var outerDeadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromMinutes(3));
        TimeSpan remaining = outerDeadline.Remaining;
        if (remaining <= TimeSpan.FromSeconds(10)) { throw new TimeoutException("入力と回収に必要な残期限がありません。"); }
        ProcessDeadline deadline = outerDeadline.LimitRemaining(remaining - TimeSpan.FromSeconds(10));
        ProcessDeadline? failureDeadline = null;
        void BeginFailureCleanup() => failureDeadline ??= outerDeadline.LimitRemaining(TimeSpan.FromSeconds(10));
        ProcessDeadline CleanupDeadline() => failureDeadline ?? deadline;
        using var progress = new StreamWriter(workspace.GetPath("uia-steps.log")) { AutoFlush = true };
        void Step(string text) => progress.WriteLine($"{DateTime.UtcNow:O} remainingMs={deadline.Remaining.TotalMilliseconds:F0} {text}");
        DesktopPlaylistFixture? fixture = null;
        Exception? caseFailure = null;
        try
        {
            fixture = DesktopPlaylistFixture.CreateOldSchemaRepair(workspace);
            Assert.AreEqual(0, SchemaVersion(fixture.SongDatabasePath));
            AssertLegacyStorage(fixture, repaired: false);
            byte[] oldBytes = File.ReadAllBytes(fixture.SongDatabasePath);
            string[] oldDatabase = ReadDatabase(fixture.SongDatabasePath);
            // CancelとApproveは別の意図したprocess。取消された起動を同processで再実行しない。
            for (int launch = 0; launch < 2; launch++)
            {
                DesktopApplicationSession session = await DesktopApplicationSession.StartConnectedAsync(fixture,
                    workspace.GetPath($"schema-session-{launch}.json"), workspace.CaseId + "-" + launch, deadline);
                Exception? primary = null;
                try
                {
                    Window modal = FindConfirmation(session, "AppSchemaRepairWarningTitle", "AppSchemaRepairWarningMessage", deadline);
                    if (launch == 0)
                    {
                        AutomationElement cancel = FindUnique(modal, "ThemedMessageBoxCancel", deadline, Step);
                        session.Driver.ExpectNaturalExit();
                        EnsureInputDeadline(deadline);
                        cancel.Patterns.Invoke.Pattern.Invoke();
                        await session.Driver.WaitForExpectedExitAsync(deadline);
                        Step("after schema Cancel natural exit/root/children/pipes");
                        Assert.AreEqual(0, SchemaVersion(fixture.SongDatabasePath));
                        CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(fixture.SongDatabasePath));
                        CollectionAssert.AreEqual(oldDatabase, ReadDatabase(fixture.SongDatabasePath));
                        AssertLegacyStorage(fixture, repaired: false);
                    }
                    else
                    {
                        AutomationElement approve = FindUnique(modal, "ThemedMessageBoxOK", deadline, Step);
                        EnsureInputDeadline(deadline);
                        approve.Patterns.Invoke.Pattern.Invoke();
                        ScenarioInitializationResult startup = await session.Driver.WaitForStartupAsync(deadline);
                        Assert.AreEqual(StartupInitializationOutcome.Succeeded, startup.Outcome);
                        Assert.IsTrue(startup.CompletionPublished);
                        Assert.IsTrue(startup.IsInitializationCompleted);
                        Assert.IsNull(startup.Failure);
                        AssertLegacyStorage(fixture, repaired: true);
                        foreach (DesktopChartInput chart in fixture.Charts) { AssertChartFiles(chart, chart.Path); AssertSong(fixture.SongDatabasePath, chart, chart.Path); }
                        nint ownedWindow = session.Window.Properties.NativeWindowHandle.Value;
                        ActivateOwnedCaption(session.Window, session.Driver.ProcessId, ownedWindow, deadline, Step);
                        RequireOwnedForeground(session.Driver.ProcessId, ownedWindow, "after approved startup caption click", Step);
                        AutomationElement table = await ShowLibraryAsync(session, fixture.Charts.Count, deadline, Step);
                        AutomationElement input = FindUnique(FindUnique(session.Window, "MainKeywordSearch", deadline, Step), "KeywordSearchInput", deadline, Step);
                        var searchTarget = new ScenarioKeywordTarget("sentinel");
                        ScenarioObservationRegistered registration = await session.Driver.RegisterObservationAsync(searchTarget, deadline);
                        EnsureInputDeadline(deadline);
                        input.Focus();
                        _ = Retry.WhileFalse(() => input.Properties.HasKeyboardFocus.Value, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
                        EnsureInputDeadline(deadline);
                        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                        Keyboard.Type("sentinel");
                        ScenarioObservationCompleted completed = await session.Driver.AwaitObservationAsync(registration.RequestId, deadline);
                        Assert.AreEqual(registration.RequestId, completed.RegistrationRequestId);
                        Assert.AreEqual(searchTarget, completed.Result.Target);
                        Assert.IsNull(completed.Result.TaskFailure);
                        Assert.IsNotNull(completed.Result.Keyword);
                        Assert.IsTrue(completed.Result.Keyword.WasCommitted);
                        _ = await WaitTableStateAsync(session, state => state.OperationSection == "Library" && state.Count == 1, deadline);
                        Assert.AreEqual(fixture.Charts[1].Title, FindLibraryRow(table, fixture.Charts[1], fixture.Charts[1].Path).Name);
                        await session.CloseAsync(deadline);
                        Step("after repaired process normal exit");
                    }
                }
                catch (Exception failure) { BeginFailureCleanup(); primary = failure; throw; }
                finally
                {
                    try { await session.DisposeAsync(CleanupDeadline()); }
                    catch (Exception cleanup) when (primary is not null) { primary.Data["DesktopCleanupFailure"] = cleanup; }
                }
            }
        }
        catch (Exception failure) { BeginFailureCleanup(); caseFailure = failure; workspace.PreserveArtifacts(); throw; }
        finally
        {
            if (fixture is not null)
            {
                try { await fixture.DisposeAsync(CleanupDeadline()); }
                catch (Exception cleanup)
                {
                    workspace.PreserveArtifacts();
                    if (caseFailure is null) { throw; }
                    caseFailure.Data["DesktopFixtureCleanupFailure"] = cleanup;
                }
            }
        }
    }

    private static async Task DropZipAsync(DesktopApplicationSession session, DesktopPlaylistFixture fixture,
        DesktopPackageInput package, AutomationElement table, ProcessDeadline deadline, Action<string> step,
        Action beginFailureCleanup, Func<ProcessDeadline> cleanupDeadline)
    {
        step("before STA FileDrop source preparation");
        DesktopFileDropSource source = await DesktopFileDropSource.CreateAsync([package.ZipPath], deadline, step, beginFailureCleanup, cleanupDeadline);
        Exception? primary = null;
        try
        {
            step("after STA FileDrop source preparation");
            ScenarioObservationRegistered registration = await session.Driver.RegisterObservationAsync(new ScenarioInstallTarget([package.ZipPath]), deadline);
            step("after Install ACK; before real STA FileDrop");
            DrawingPoint target = Center(table);
            step("after FileDrop target bounds; before gesture");
            _ = await source.DragToAsync(target);
            step("after FileDrop gesture; before owner receipt await");
            ScenarioObservationCompleted completed = await session.Driver.AwaitObservationAsync(registration.RequestId, deadline);
            step("after FileDrop owner receipt await");
            Assert.AreEqual(registration.RequestId, completed.RegistrationRequestId);
            Assert.IsNull(completed.Result.TaskFailure);
            Assert.IsInstanceOfType<ScenarioInstallTarget>(completed.Result.Target);
            CollectionAssert.AreEqual(new[] { package.ZipPath }, ((ScenarioInstallTarget)completed.Result.Target).Paths.ToArray());
            ScenarioDropCompleted install = completed.Result.Install ?? throw new AssertFailedException("導入終端がありません。");
            Assert.AreNotEqual(Guid.Empty, install.BatchId);
            Assert.IsNull(install.Failure);
            ScenarioInstallResult result = install.Result ?? throw new AssertFailedException("実導入結果がありません。");
            Assert.IsFalse(result.HasRequiredFailure);
            Assert.IsFalse(result.SynchronizationIncomplete);
            Assert.AreEqual(0, result.UnprocessedPackageCount);
            Assert.AreEqual(0, result.Failures.Length);
            Assert.IsTrue(result.Initial.DurableCommit);
            Assert.IsFalse(result.Initial.HasRequiredFailure);
            Assert.IsFalse(result.Initial.HasUnrestoredExistingDestination);
            Assert.IsFalse(result.Initial.ManualRecoveryRequired);
            Assert.IsFalse(result.Initial.CompletedWithCleanupFailure);
            Assert.AreEqual(0, result.Initial.DestinationConflictCount);
            Assert.AreEqual(0, result.Initial.Failures.Length);
            Assert.AreEqual(1, result.Initial.ConfirmedTargets.Length);
            Assert.AreEqual(0, result.Initial.UnprocessedTargets.Length);
            string installedPath = ReadInstalledPath(fixture, package);
            Assert.IsTrue(string.Equals(Path.GetDirectoryName(installedPath),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.Initial.ConfirmedTargets[0].DestinationPath)), StringComparison.OrdinalIgnoreCase));
            step("after actual Install terminal/raw files and DB");
        }
        catch (Exception failure) { beginFailureCleanup(); primary = failure; throw; }
        finally
        {
            try { await source.DisposeAsync(cleanupDeadline()); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["DesktopFileDropCleanupFailure"] = cleanup; }
        }
    }

    private static async Task RenameFolderAsync(DesktopApplicationSession session, AutomationElement table, AutomationElement row,
        DesktopChartInput chart, bool commit, ProcessDeadline deadline, Action<string> step)
    {
        AutomationElement folder = FindUnique(row, "cell:Folder", deadline, step);
        Assert.IsFalse(folder.Patterns.Value.Pattern.IsReadOnly.Value);
        EnsureInputDeadline(deadline);
        row.Patterns.SelectionItem.Pattern.Select();
        CollectionAssert.AreEqual(new[] { row.AutomationId }, SelectionIds(table));
        if (!commit)
        {
            AutomationElement status = FindUnique(row, "cell:Status", deadline, step);
            Assert.IsTrue(status.Patterns.Value.Pattern.IsReadOnly.Value);
            EnsureInputDeadline(deadline);
            Mouse.Click(Center(status));
            Mouse.Click(Center(folder));
        }
        // 取消で現在列は残る。短時間の再クリックによる行起動を避け、同じ対象のFolder列へF2を送る。
        CollectionAssert.AreEqual(new[] { row.AutomationId }, SelectionIds(table));
        EnsureInputDeadline(deadline);
        table.Focus();
        _ = Retry.WhileFalse(() => table.Properties.HasKeyboardFocus.Value, timeout: deadline.Remaining,
            throwOnTimeout: true, ignoreException: false);
        step($"before Folder F2 commit={commit} selected={row.AutomationId} tableFocus=True");
        var target = new ScenarioCellEditTarget(row.AutomationId, "Folder", DesktopPlaylistFixture.RenameCommittedBasename, commit);
        ScenarioObservationRegistered registration = await session.Driver.RegisterObservationAsync(target, deadline);
        EnsureInputDeadline(deadline);
        Keyboard.Type(VirtualKeyShort.F2);
        AutomationElement editor = FindUnique(folder, "CellEditor", deadline, step);
        _ = Retry.WhileFalse(() => editor.Properties.HasKeyboardFocus.Value, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        TypeEditorTextAndWait(editor, folder, DesktopPlaylistFixture.RenameCommittedBasename, deadline, step);
        EnsureInputDeadline(deadline);
        Keyboard.Type(commit ? VirtualKeyShort.RETURN : VirtualKeyShort.ESCAPE);
        ScenarioObservationCompleted completed = await session.Driver.AwaitObservationAsync(registration.RequestId, deadline);
        Assert.AreEqual(registration.RequestId, completed.RegistrationRequestId);
        Assert.AreEqual(target, completed.Result.Target);
        Assert.IsNull(completed.Result.TaskFailure);
        AssertRegularFacts(completed.Result, chart, chart.Path);
        step("after Folder edit actual Task commit=" + commit);
    }

    private static async Task DeleteEmptyFolderAsync(DesktopApplicationSession session, DesktopPlaylistFixture fixture,
        bool approve, ProcessDeadline deadline, Action<string> step)
    {
        TreeItem playlist = ExpandPlaylist(session, deadline, step);
        TreeItem folder = FindUnique(playlist, "PlaylistFolder:" + DesktopPlaylistFixture.DeleteFolderName, deadline, step).AsTreeItem();
        ScenarioTableState beforeSelection = (await session.Driver.TableStateAsync(deadline)).State;
        bool changesSelection = !folder.IsSelected;
        EnsureInputDeadline(deadline);
        folder.Select();
        Assert.IsTrue(folder.IsSelected);
        // 選択状態だけでは旧Library sourceが残り得る。空folderの表示採用後に観測を登録する。
        ScenarioTableState adopted = await WaitTableStateAsync(session,
            state => state.OperationSection == "Playlist" && state.Count == 0
                && (!changesSelection || state.RowsGeneration > beforeSelection.RowsGeneration), deadline);
        step($"after empty folder adoption generation={adopted.RowsGeneration} approve={approve}");
        string[] entries = ReadTable(fixture.SongDatabasePath, "playlist_entry");
        string[] entriesWithoutDeletedDummy = ReadEntriesExceptEmptyFolder(fixture.SongDatabasePath, DesktopPlaylistFixture.DeleteFolderName);
        string[] beforePlaylist = ReadTable(fixture.SongDatabasePath, "playlist");
        var target = new ScenarioPlaylistFolderDeleteTarget(DesktopPlaylistFixture.PlaylistId, DesktopPlaylistFixture.DeleteFolderName);
        ScenarioObservationRegistered registration = await session.Driver.RegisterObservationAsync(target, deadline);
        Assert.AreEqual(adopted.RowsGeneration, registration.TableState.RowsGeneration);
        Assert.AreEqual("Playlist", registration.TableState.OperationSection);
        Assert.AreEqual(0, registration.TableState.Count);
        EnsureInputDeadline(deadline);
        Mouse.Click(Center(folder), MouseButton.Right);
        AutomationElement menu = FindContextMenu(session, "DeletePlaylistFolder", deadline);
        Assert.IsTrue(menu.IsEnabled);
        EnsureInputDeadline(deadline);
        menu.Patterns.Invoke.Pattern.Invoke();
        Window modal = FindConfirmation(session, "Confirm", "Msg_remove_folder", deadline);
        AutomationElement button = FindUnique(modal, approve ? "ThemedMessageBoxOK" : "ThemedMessageBoxCancel", deadline, step);
        EnsureInputDeadline(deadline);
        button.Patterns.Invoke.Pattern.Invoke();
        ScenarioObservationCompleted completed = await session.Driver.AwaitObservationAsync(registration.RequestId, deadline);
        Assert.AreEqual(registration.RequestId, completed.RegistrationRequestId);
        Assert.AreEqual(target, completed.Result.Target);
        Assert.IsNull(completed.Result.TaskFailure);
        CollectionAssert.AreEqual(approve ? entriesWithoutDeletedDummy : entries, ReadTable(fixture.SongDatabasePath, "playlist_entry"));
        if (approve)
        {
            CollectionAssert.AreEquivalent(new[] { DesktopPlaylistFixture.ImportFolderName }, ReadFolders(fixture.SongDatabasePath));
            _ = Retry.WhileFalse(() => playlist.FindAllDescendants(cf => cf.ByAutomationId("PlaylistFolder:" + DesktopPlaylistFixture.DeleteFolderName)).Length == 0,
                timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        }
        else
        {
            CollectionAssert.AreEqual(beforePlaylist, ReadTable(fixture.SongDatabasePath, "playlist"));
            _ = FindUnique(playlist, "PlaylistFolder:" + DesktopPlaylistFixture.DeleteFolderName, deadline, step);
        }
        _ = Retry.WhileFalse(() => session.Window.IsEnabled && session.Window.ModalWindows.Length == 0,
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        step("after folder delete actual modal/Task/raw approve=" + approve);
    }

    private static async Task<AutomationElement> ShowLibraryAsync(DesktopApplicationSession session, int count,
        ProcessDeadline deadline, Action<string> step)
    {
        TreeItem library = FindUnique(session.Window, "LibraryTreeRoot", deadline, step).AsTreeItem();
        EnsureInputDeadline(deadline);
        library.Select();
        _ = await WaitTableStateAsync(session, state => state.OperationSection == "Library" && state.Count == count, deadline);
        return FindUnique(session.Window, "MainChartTable", deadline, step);
    }

    private static TreeItem ExpandPlaylist(DesktopApplicationSession session, ProcessDeadline deadline, Action<string> step)
    {
        TreeItem root = FindUnique(session.Window, "PlaylistTreeRoot", deadline, step).AsTreeItem();
        _ = Retry.WhileFalse(() => root.ExpandCollapseState != ExpandCollapseState.LeafNode, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        if (root.ExpandCollapseState == ExpandCollapseState.Collapsed) { root.Expand(); }
        TreeItem playlist = FindUnique(root, "PlaylistTable:" + DesktopPlaylistFixture.PlaylistId, deadline, step).AsTreeItem();
        if (playlist.ExpandCollapseState == ExpandCollapseState.Collapsed) { playlist.Expand(); }
        return playlist;
    }

    private static AutomationElement FindLibraryRow(AutomationElement table, DesktopChartInput chart, string expectedPath)
    {
        AutomationElement[] rows = [.. table.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Where(row => !row.IsOffscreen && row.Name == chart.Title)];
        Assert.AreEqual(1, rows.Length);
        string[] id = rows[0].AutomationId.Split(':');
        Assert.AreEqual(4, id.Length);
        Assert.AreEqual("chart", id[0]);
        Assert.AreEqual("Bms", id[2]);
        Assert.IsTrue(string.Equals(expectedPath, Encoding.UTF8.GetString(Convert.FromBase64String(id[3])), StringComparison.OrdinalIgnoreCase));
        return rows[0];
    }

    private static void AssertRegularFacts(ScenarioObservedResult result, DesktopChartInput chart, string path)
    {
        Assert.IsTrue(result.Rows.HasValue);
        System.Collections.Immutable.ImmutableArray<ScenarioObservedRow> rows = result.Rows.GetValueOrDefault();
        Assert.AreEqual(1, rows.Length);
        Assert.AreEqual("Bms", rows[0].Kind);
        Assert.AreEqual(path, rows[0].Path);
        Assert.AreEqual(chart.Md5, rows[0].Md5);
        Assert.AreEqual(chart.Sha256, rows[0].Sha256);
        Assert.IsNull(rows[0].PlaylistId);
    }

    private static void AssertInitialEmptyFolders(DesktopPlaylistFixture fixture)
    {
        using var db = new SQLiteConnection(fixture.SongDatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        Assert.AreEqual(2, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry"));
        foreach (string folder in new[] { DesktopPlaylistFixture.ImportFolderName, DesktopPlaylistFixture.DeleteFolderName })
        {
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id=? AND folder=? AND md5='00000000000000000000000000000000' AND sha256 IS NULL AND is_removed=0",
                DesktopPlaylistFixture.PlaylistId, folder));
        }
    }

    private static void AssertMembership(DesktopPlaylistFixture fixture, DesktopChartInput chart, bool deleteFolderPresent)
    {
        using var db = new SQLiteConnection(fixture.SongDatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        Assert.AreEqual(deleteFolderPresent ? 2 : 1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id=? AND folder=? AND md5=? AND sha256=? AND is_removed=0",
            DesktopPlaylistFixture.PlaylistId, DesktopPlaylistFixture.ImportFolderName, chart.Md5, chart.Sha256));
        Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id=? AND folder=? AND md5='00000000000000000000000000000000'",
            DesktopPlaylistFixture.PlaylistId, DesktopPlaylistFixture.ImportFolderName));
        Assert.AreEqual(deleteFolderPresent ? 1 : 0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id=? AND folder=? AND md5='00000000000000000000000000000000' AND sha256 IS NULL AND is_removed=0",
            DesktopPlaylistFixture.PlaylistId, DesktopPlaylistFixture.DeleteFolderName));
    }

    private static void AssertExistingSongsUnchanged(DesktopPlaylistFixture fixture, string[] before, string addedMd5)
    {
        // 新規一行だけを除く独立SQL snapshotを、導入前の二行と照合する。
        using var db = new SQLiteConnection(fixture.SongDatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        string columns = string.Join("||','||", db.GetTableInfo("song").Select(column => "quote(\"" + column.Name.Replace("\"", "\"\"") + "\")"));
        string[] remaining = [.. db.Query<RawSong>("SELECT " + columns + " AS Value FROM song WHERE hash<>?", addedMd5)
            .Select(row => row.Value).Order(StringComparer.Ordinal)];
        CollectionAssert.AreEqual(before, remaining);
    }

    private static DrawingPoint Center(AutomationElement element)
    {
        Rectangle bounds = element.BoundingRectangle;
        Assert.IsFalse(element.IsOffscreen);
        Assert.IsTrue(bounds.Width > 0 && bounds.Height > 0);
        return new DrawingPoint(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    }

    private static void DragRow(AutomationElement row, AutomationElement target, ProcessDeadline deadline, Action beginFailureCleanup, Action<string> step)
    {
        DrawingPoint start = Center(row);
        DrawingPoint destination = Center(target);
        step($"row drag source={row.AutomationId} start={start} target={target.AutomationId} destination={destination}");
        EnsureInputDeadline(deadline);
        Mouse.MoveTo(start);
        Exception? primary = null;
        try
        {
            EnsureInputDeadline(deadline);
            Mouse.Down(MouseButton.Left);
            step("row drag after Down; before threshold Move");
            Mouse.MoveTo(new DrawingPoint(start.X + 20, start.Y));
            step("row drag after threshold Move; before target Move");
            EnsureInputDeadline(deadline);
            Mouse.MoveTo(destination);
            step("row drag after target Move");
        }
        catch (Exception failure) { beginFailureCleanup(); primary = failure; throw; }
        finally
        {
            try { Mouse.Up(MouseButton.Left); step("row drag after owned LeftUp"); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["DesktopMouseReleaseFailure"] = cleanup; }
        }
    }

    private static AutomationElement FindContextMenu(DesktopApplicationSession session, string id, ProcessDeadline deadline)
        => Retry.WhileNull(() =>
        {
            AutomationElement[] found = [.. session.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(session.Driver.ProcessId))
                .SelectMany(window => window.FindAllDescendants(cf => cf.ByAutomationId(id))).Where(element => !element.IsOffscreen)];
            if (found.Length > 1) { throw new InvalidOperationException("所有PIDのcontext menuが重複しています: " + id); }
            return found.SingleOrDefault();
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false).Result
            ?? throw new InvalidOperationException("実context menuがありません: " + id);

    private static Window FindConfirmation(DesktopApplicationSession session, string titleResource, string messageResource, ProcessDeadline deadline)
    {
        string assets = Path.GetDirectoryName(DesktopPlaylistFixture.ExecutablePath) ?? throw new InvalidOperationException();
        using var language = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "lang", "ja-JP.json")));
        string title = language.RootElement.GetProperty(titleResource).GetString() ?? throw new InvalidDataException(titleResource);
        string message = language.RootElement.GetProperty(messageResource).GetString() ?? throw new InvalidDataException(messageResource);
        return Retry.WhileNull(() =>
        {
            Window[] matches = [.. session.Window.ModalWindows.Where(window => window.Properties.ProcessId.Value == session.Driver.ProcessId
                && window.Name == title && window.FindAllDescendants(cf => cf.ByName(message)).Length == 1)];
            if (matches.Length > 1) { throw new InvalidOperationException("所有MainWindowの確認modalが重複しています。"); }
            return matches.SingleOrDefault();
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "所有MainWindowに指定resourceの実確認modalがありません。").Result ?? throw new InvalidOperationException("確認modalがありません。");
    }

    private sealed class RawSong
    {
        public RawSong() { }
        public string Value { get; set; } = string.Empty;
    }
}
