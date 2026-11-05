using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using BeMusicSeeker.Desktop.E2E.Tests.Fixtures;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.Core.Identifiers;
using FlaUI.Core.Input;
using FlaUI.Core.Patterns;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.DesktopPlaylistOracle;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.DesktopScenarioUi;

namespace BeMusicSeeker.Desktop.E2E.Tests.Playlist;

/// <summary>実アプリの外側から、プレイリスト仮想行・実編集・削除・設定の接続を確認します。</summary>
/// <remarks>投影は軽量counterで全件実体化を拒否します。厳密な投影identity集合は局所UIA契約、テーマの色・辞書適用はWindows契約へ分担します。</remarks>
[TestClass]
public sealed class VirtualPlaylistRowDesktopTests
{
    /// <summary>専有syncOFF表の画面外検索で投影・選択・scrollを保ち、実F2編集と削除のTask・DB・旧peer失効、言語/テーマの即時保存と再表示を確認します。</summary>
    [TestMethod(DisplayName = "[DT-03] 仮想playlist行の実編集・削除と設定保存")]
    [TestCategory("Daily")]
    public async Task FindAndRealize_UseExternalUiaForAnInitiallyOffscreenPlaylistRow()
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
        string mainHandle = "unattached";
        // 操作境界とeditor直下の各照会をcase writerから即時保存する。全行propertyは記録しない。
        void Step(string message) => progress.WriteLine($"{DateTime.UtcNow:O} remainingMs={deadline.Remaining.TotalMilliseconds:F0} mainHwnd={mainHandle} {message}");
        DesktopPlaylistFixture? fixture = null;
        Exception? caseFailure = null;
        try
        {
            fixture = DesktopPlaylistFixture.Create(workspace);
            Step("before StartAsync/WaitForStartup/MainWindow attach");
            DesktopApplicationSession session = await DesktopApplicationSession.StartAsync(fixture,
                workspace.GetPath("session.json"), workspace.CaseId, deadline);
            Exception? primary = null;
            try
            {
                Step("after StartAsync; before MainWindow.NativeWindowHandle");
                nint ownedWindow = session.Window.Properties.NativeWindowHandle.Value;
                int ownedPid = session.Driver.ProcessId;
                mainHandle = ownedWindow.ToString();
                Step("after MainWindow.NativeWindowHandle; before initial owned caption click");
                EnsureInputDeadline(deadline);
                ActivateOwnedCaption(session.Window, ownedPid, ownedWindow, deadline, Step);
                void InputGate(string phase) => RequireOwnedForeground(ownedPid, ownedWindow, phase, Step);
                InputGate("after initial owned caption click");
                TreeItem playlistRoot = FindUnique(session.Window, "PlaylistTreeRoot", deadline, Step).AsTreeItem();
                Step("before PlaylistTreeRoot nonLeaf wait");
                _ = Retry.WhileFalse(() => playlistRoot.ExpandCollapseState != ExpandCollapseState.LeafNode,
                    timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
                    timeoutMessage: "登録済みプレイリストがtreeへ公開されませんでした。");
                Step("after PlaylistTreeRoot nonLeaf wait; before Expand state/action");
                if (playlistRoot.ExpandCollapseState == ExpandCollapseState.Collapsed) { playlistRoot.Expand(); }
                Step("after PlaylistTreeRoot Expand state/action");
                TreeItem playlist = FindUnique(playlistRoot, "PlaylistTable:" + DesktopPlaylistFixture.PlaylistId, deadline, Step).AsTreeItem();
                ScenarioTableState beforeSelection = (await session.Driver.TableStateAsync(deadline)).State;
                Step("before PlaylistTable Select");
                playlist.Select();
                Step("after PlaylistTable Select");
                AutomationElement table = FindUnique(session.Window, "MainChartTable", deadline, Step);
                Step("before nonprojecting Playlist adoption wait");
                ScenarioTableState adopted = await WaitTableStateAsync(session,
                    state => state.OperationSection == "Playlist" && state.Count == fixture.Charts.Count
                        && state.RowsGeneration > beforeSelection.RowsGeneration, deadline);
                Assert.AreEqual("Library", adopted.SourceScope);
                WaitStartupProgressLayout(session, table, deadline, Step);
                using (var language = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                    Path.GetDirectoryName(DesktopPlaylistFixture.ExecutablePath) ?? throw new InvalidOperationException(), "lang", "ja-JP.json"))))
                { Assert.AreEqual(language.RootElement.GetProperty("Playlist").GetString(), table.Name); }
                IItemContainerPattern container = table.Patterns.ItemContainer.Pattern;
                Step("after Playlist adoption; before viewport/selection/scroll reads");
                IScrollPattern scroll = table.Patterns.Scroll.Pattern;
                Assert.IsTrue(scroll.VerticallyScrollable.Value, "fixtureが実際のviewportを超えていません。");
                string[] visibleIds = [.. table.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
                    .Where(row => !row.IsOffscreen).Select(row => row.AutomationId)];
                // EntryOrderと特定titleの対応は仮定せず、実表示から除外した一意な既存locatorを使う。
                string targetId = Enumerable.Range(0, fixture.Charts.Count).Select(fixture.EntryAutomationId)
                    .Last(id => !visibleIds.Contains(id, StringComparer.Ordinal));
                string[] selectedBefore = SelectionIds(table);
                double horizontalBefore = scroll.HorizontalScrollPercent.Value;
                double verticalBefore = scroll.VerticalScrollPercent.Value;
                ScenarioTableState beforeFind = (await session.Driver.TableStateAsync(deadline)).State;
                Assert.IsTrue(beforeFind.RealizedRowCount < fixture.Charts.Count, "検索前に全行を投影しています。");
                Step("after viewport/selection/scroll reads; before FindItemByProperty " + targetId);
                AutomationElement target = container.FindItemByProperty(null, session.Automation.PropertyLibrary.Element.AutomationId, targetId)
                    ?? throw new AssertFailedException("指定した表scopeの画面外providerがありません。");
                Step("after FindItemByProperty; before offscreen/selection/scroll checks " + targetId);
                Assert.IsTrue(target.IsOffscreen, "表示済みの行を仮想行の代わりに使いません。");
                Assert.AreEqual(targetId, target.AutomationId);
                CollectionAssert.AreEquivalent(selectedBefore, SelectionIds(table));
                Assert.AreEqual(horizontalBefore, scroll.HorizontalScrollPercent.Value);
                Assert.AreEqual(verticalBefore, scroll.VerticalScrollPercent.Value);
                Assert.AreEqual(0, target.FindAllChildren().Length, "画面外placeholderに実セルが生成されています。");
                Assert.AreEqual(1, table.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
                    .Count(row => row.AutomationId == targetId), "明示要求したplaceholderが表の子に公開されません。");
                ScenarioTableState afterFind = (await session.Driver.TableStateAsync(deadline)).State;
                Assert.AreEqual(beforeFind.RowsGeneration, afterFind.RowsGeneration);
                Assert.AreEqual(beforeFind.RealizedRowCount, afterFind.RealizedRowCount, "画面外検索だけで行を投影しています。");

                Step("after offscreen checks; before Realize " + targetId);
                target.Patterns.VirtualizedItem.Pattern.Realize();
                Step("after Realize; before visible wait " + targetId);
                _ = Retry.WhileFalse(() => !target.IsOffscreen, timeout: deadline.Remaining, throwOnTimeout: true,
                    ignoreException: false, timeoutMessage: "同じ行providerのRealizeで可視化しませんでした。");
                Step("after visible wait; before ScrollIntoView " + targetId);
                target.Patterns.ScrollItem.Pattern.ScrollIntoView();
                Step("after ScrollIntoView; before row metadata/selection checks " + targetId);
                Assert.IsFalse(target.IsOffscreen);
                Assert.IsTrue(target.BoundingRectangle.Width > 0 && target.BoundingRectangle.Height > 0);
                Assert.AreEqual(targetId, target.AutomationId);
                string title = target.Name;
                Assert.AreEqual(1, fixture.Charts.Count(chart => string.Equals(chart.Title, title, StringComparison.Ordinal)),
                    "Realize後の表示は宣言した一意な譜面集合の一行でなければなりません。");
                CollectionAssert.AreEquivalent(selectedBefore, SelectionIds(table));
                DesktopChartInput targetChart = fixture.Charts.Single(chart => chart.Title == title);
                ScenarioTableState afterRealize = (await session.Driver.TableStateAsync(deadline)).State;
                Assert.AreEqual(beforeFind.RowsGeneration, afterRealize.RowsGeneration);
                Assert.IsTrue(afterRealize.RealizedRowCount > beforeFind.RealizedRowCount);
                Assert.IsTrue(afterRealize.RealizedRowCount < fixture.Charts.Count, "一行のRealizeで全行を投影しています。");
                Assert.IsTrue(target.FindAllChildren().Length > 0, "Realizeした行に実セルがありません。");
                Step("after row metadata/selection checks; before real edit/delete");
                await EditAndDeleteAsync(session, fixture, table, target, targetChart, deadline, Step, InputGate);
                Step("after real edit/delete");
                ChangeSettingsAndReopen(session, fixture, table, deadline, Step);
                Step("before session CloseAsync");
                await session.CloseAsync(deadline);
                Step("after session CloseAsync");
            }
            catch (Exception failure)
            {
                BeginFailureCleanup(); primary = failure;
                try
                {
                    Step("case failed: " + failure.GetType().Name);
                }
                catch (Exception diagnostic) { failure.Data["DesktopStepDiagnosticsFailure"] = diagnostic; }
                throw;
            }
            finally
            {
                try { await session.DisposeAsync(CleanupDeadline()); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["DesktopCleanupFailure"] = cleanup; }
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

    private static async Task EditAndDeleteAsync(DesktopApplicationSession session, DesktopPlaylistFixture fixture,
        AutomationElement table, AutomationElement target, DesktopChartInput chart, ProcessDeadline deadline, Action<string> step, Action<string> inputGate)
    {
        string targetId = target.AutomationId;
        Dictionary<string, StoredEntry> before = ReadEntries(fixture.SongDatabasePath);
        Assert.AreEqual(fixture.Charts.Count, before.Count);
        foreach (DesktopChartInput input in fixture.Charts)
        {
            Assert.IsTrue(before.TryGetValue(input.Md5, out StoredEntry? entry));
            Assert.IsNotNull(entry);
            Assert.AreEqual(input.Sha256, entry.Sha256);
            Assert.AreEqual(input.Title, entry.Title);
            Assert.AreEqual("before " + input.Title, entry.Memo);
        }
        // 実表示の近傍だけを読む。ordinalから恒久titleを逆算せず、fixtureの一意titleで照合する。
        AutomationElement neighbor = table.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
            .Where(row => !row.IsOffscreen && row.AutomationId != targetId)
            .OrderBy(row => Math.Abs(row.BoundingRectangle.Top - target.BoundingRectangle.Top)).First();
        string neighborId = neighbor.AutomationId;
        DesktopChartInput neighborChart = fixture.Charts.Single(input => input.Title == neighbor.Name);
        Assert.AreNotEqual(chart.Md5, neighborChart.Md5);
        AutomationElement status = FindUnique(target, "cell:Status", deadline, step);
        AutomationElement memo = FindUnique(target, "cell:Memo", deadline, step);
        Assert.IsTrue(status.Patterns.Value.Pattern.IsReadOnly.Value);
        Assert.IsFalse(memo.Patterns.Value.Pattern.IsReadOnly.Value);
        Assert.AreEqual(before[chart.Md5].Memo, memo.Patterns.Value.Pattern.Value.Value);
        Assert.IsFalse(target.Patterns.SelectionItem.Pattern.IsSelected.Value, "実クリック前の対象行は未選択でなければなりません。");
        var selectionChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionFailures = new ConcurrentQueue<Exception>();
        PropertyId selectedProperty = target.Patterns.SelectionItem.Pattern.PropertyIds.IsSelected;
        PropertyChangedEventHandlerBase selectionSubscription = target.RegisterPropertyChangedEvent(TreeScope.Element, (sender, property, value) =>
        {
            try
            {
                if (property == selectedProperty && value is true && session.Automation.Compare(sender, target))
                { selectionChanged.TrySetResult(true); }
            }
            catch (Exception failure) { selectionFailures.Enqueue(failure); selectionChanged.TrySetException(failure); }
        }, selectedProperty);
        Exception? selectionPrimary = null;
        try
        {
            ClickCell(status, deadline, step, inputGate);
            await selectionChanged.Task.WaitAsync(deadline.Remaining);
            CollectionAssert.AreEqual(new[] { targetId }, SelectionIds(table), "Statusクリック後の対象行が異なります。");
            Assert.IsTrue(target.Patterns.SelectionItem.Pattern.IsSelected.Value);
        }
        catch (Exception failure) { selectionPrimary = failure; throw; }
        finally { FinishSubscription(selectionSubscription, selectionFailures, selectionPrimary); }
        ClickCell(memo, deadline, step, inputGate);
        CollectionAssert.AreEqual(new[] { targetId }, SelectionIds(table), "Memoクリック後の対象行が異なります。");
        var editTarget = new ScenarioCellEditTarget(targetId, "memo", DesktopPlaylistFixture.EditedMemo, true);
        var valueChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailures = new ConcurrentQueue<Exception>();
        PropertyId valueProperty = memo.Patterns.Value.Pattern.PropertyIds.Value;
        PropertyChangedEventHandlerBase valueSubscription = memo.RegisterPropertyChangedEvent(TreeScope.Element, (sender, property, value) =>
        {
            try
            {
                if (property == valueProperty && value is string text && text == DesktopPlaylistFixture.EditedMemo) { valueChanged.TrySetResult(true); }
            }
            catch (Exception failure) { callbackFailures.Enqueue(failure); valueChanged.TrySetException(failure); }
        }, valueProperty);
        Exception? primary = null;
        try
        {
            step("before CellEdit registration ACK");
            ScenarioObservationRegistered registration = await session.Driver.RegisterObservationAsync(editTarget, deadline);
            Assert.AreEqual("Playlist", registration.TableState.OperationSection);
            Assert.AreEqual(fixture.Charts.Count, registration.TableState.Count);
            step("after CellEdit ACK; before F2");
            EnsureInputDeadline(deadline);
            // logical cellのHasKeyboardFocusは既存providerのTable.IsKeyboardFocusWithin。
            // 現在列の証明には使わず、F2後の同Memo直下editorで列を限定する。
            Assert.IsTrue(memo.Properties.HasKeyboardFocus.Value, "F2前に表内keyboard focusがありません。");
            inputGate("before F2");
            Keyboard.Type(VirtualKeyShort.F2);
            inputGate("after F2");
            int editorQuery = 0;
            AutomationElement editor = Retry.WhileNull(() =>
            {
                int query = ++editorQuery;
                step($"before editor target selection query={query}");
                CollectionAssert.AreEqual(new[] { targetId }, SelectionIds(table), "editor取得中に対象行が変わりました。");
                step($"after target selection; before CellEditor direct child query={query}");
                AutomationElement[] editors = memo.FindAllChildren(cf => cf.ByAutomationId("CellEditor"));
                step($"after CellEditor direct child query={query} count={editors.Length}");
                if (editors.Length > 1) { throw new InvalidOperationException("同Memo直下のeditorが重複しています。"); }
                return editors.SingleOrDefault();
            }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
                timeoutMessage: "同じ対象行のMemo直下にeditorがありません。").Result
                ?? throw new InvalidOperationException("同Memo直下のeditorがありません。");
            Assert.AreEqual(before[chart.Md5].Memo, editor.Patterns.Value.Pattern.Value.Value);
            Assert.IsTrue(editor.Properties.HasKeyboardFocus.Value, "実F2の同editorへkeyboard focusがありません。");
            TypeEditorTextAndWait(editor, memo, DesktopPlaylistFixture.EditedMemo, deadline, step, inputGate);
            EnsureInputDeadline(deadline);
            Assert.IsTrue(editor.Properties.HasKeyboardFocus.Value, "Enter前に同editorのfocusが失われました。");
            inputGate("before Enter");
            Keyboard.Type(VirtualKeyShort.RETURN);
            ScenarioObservationCompleted completed = await session.Driver.AwaitObservationAsync(registration.RequestId, deadline);
            Assert.AreEqual(registration.RequestId, completed.RegistrationRequestId);
            Assert.AreEqual(editTarget, completed.Result.Target);
            AssertObservedRow(completed.Result, chart);
            Dictionary<string, StoredEntry> afterEdit = ReadEntries(fixture.SongDatabasePath);
            Assert.AreEqual(before.Count, afterEdit.Count);
            foreach ((string md5, StoredEntry old) in before)
            {
                Assert.IsTrue(afterEdit.TryGetValue(md5, out StoredEntry? current));
                Assert.IsNotNull(current);
                Assert.AreEqual(old.OtherValues, current.OtherValues, "MEMO以外の保存列が変わっています: " + md5);
                Assert.AreEqual(md5 == chart.Md5 ? DesktopPlaylistFixture.EditedMemo : old.Memo, current.Memo);
            }
            await valueChanged.Task.WaitAsync(deadline.Remaining);
            Assert.AreEqual(DesktopPlaylistFixture.EditedMemo, memo.Patterns.Value.Pattern.Value.Value,
                "editorのドラフトではなく、同じセルの確定表示値を確認します。");
            Assert.AreEqual(0, memo.FindAllChildren(cf => cf.ByAutomationId("CellEditor")).Length);
            step("after real F2 edit Task/raw DB/ValueChanged");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally { FinishSubscription(valueSubscription, callbackFailures, primary); }

        Dictionary<string, StoredEntry> beforeDelete = ReadEntries(fixture.SongDatabasePath);
        target.Patterns.SelectionItem.Pattern.Select();
        CollectionAssert.AreEqual(new[] { targetId }, SelectionIds(table));
        int[] targetRuntimeId = [.. target.Properties.RuntimeId.Value];
        Assert.IsTrue(targetRuntimeId.Length > 0, "削除前の対象行にRuntimeIdがありません。");
        var structureChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int deleteInputStarted = 0;
        StructureChangedEventHandlerBase structureSubscription = table.RegisterStructureChangedEvent(TreeScope.Children, (_, changeType, runtimeId) =>
        {
            try
            {
                // 同じ表の直接の子に限定し、削除前の同じ行の通知だけを採用する。callback内でCOM照会を行わない。
                if (Volatile.Read(ref deleteInputStarted) != 0 && changeType == StructureChangeType.ChildRemoved
                    && runtimeId is not null && runtimeId.SequenceEqual(targetRuntimeId))
                {
                    structureChanged.TrySetResult(true);
                }
            }
            catch (Exception failure) { callbackFailures.Enqueue(failure); structureChanged.TrySetException(failure); }
        });
        primary = null;
        try
        {
            var deleteTarget = new ScenarioPlaylistEntryDeleteTarget([targetId]);
            step("before EntryDelete registration ACK");
            ScenarioObservationRegistered registration = await session.Driver.RegisterObservationAsync(deleteTarget, deadline);
            Assert.AreEqual("Playlist", registration.TableState.OperationSection);
            Assert.AreEqual(fixture.Charts.Count, registration.TableState.Count);
            step("after EntryDelete ACK; before real right click");
            ClickCell(status, deadline, step, inputGate, MouseButton.Right);
            step("after real right click; before DeletePlaylistEntry lookup");
            CollectionAssert.AreEqual(new[] { targetId }, SelectionIds(table), "右クリック後の対象行が異なります。");
            AutomationElement menu = FindDeleteMenu(session, deadline, step);
            step("after DeletePlaylistEntry lookup; before IsEnabled");
            Assert.IsTrue(menu.IsEnabled);
            EnsureInputDeadline(deadline);
            Volatile.Write(ref deleteInputStarted, 1);
            step("before DeletePlaylistEntry Invoke");
            menu.Patterns.Invoke.Pattern.Invoke();
            step("after DeletePlaylistEntry Invoke; before EntryDelete AwaitObservation");
            ScenarioObservationCompleted completed = await session.Driver.AwaitObservationAsync(registration.RequestId, deadline);
            step("after EntryDelete AwaitObservation; before receipt/raw checks");
            Assert.AreEqual(registration.RequestId, completed.RegistrationRequestId);
            Assert.IsInstanceOfType<ScenarioPlaylistEntryDeleteTarget>(completed.Result.Target);
            var echoed = (ScenarioPlaylistEntryDeleteTarget)completed.Result.Target;
            CollectionAssert.AreEqual(new[] { targetId }, echoed.RowAutomationIds.ToArray());
            AssertObservedRow(completed.Result, chart);
            Dictionary<string, StoredEntry> afterDelete = ReadEntries(fixture.SongDatabasePath);
            Assert.AreEqual(beforeDelete.Count - 1, afterDelete.Count);
            Assert.IsFalse(afterDelete.ContainsKey(chart.Md5));
            foreach ((string md5, StoredEntry old) in beforeDelete.Where(pair => pair.Key != chart.Md5))
            {
                Assert.IsTrue(afterDelete.TryGetValue(md5, out StoredEntry? current));
                Assert.IsNotNull(current);
                Assert.AreEqual(old, current, "対象外entryの保存値が変わっています: " + md5);
            }
            step("after delete raw non-target checks; before TableState count wait");
            _ = await WaitTableStateAsync(session,
                state => state.OperationSection == "Playlist" && state.Count == fixture.Charts.Count - 1, deadline);
            step("after TableState count wait; before StructureChanged wait");
            await structureChanged.Task.WaitAsync(deadline.Remaining);
            step("after StructureChanged wait; before old row/cell invalidation checks");
            AssertUnavailable(() => _ = target.Name);
            AssertUnavailable(() => _ = memo.Name);
            AssertCurrentValuePatternUnavailable(memo);
            step("after old row/cell invalidation checks; before neighbor ScrollIntoView");
            neighbor.Patterns.ScrollItem.Pattern.ScrollIntoView();
            Assert.AreEqual(neighborId, neighbor.AutomationId);
            Assert.AreEqual(neighborChart.Title, neighbor.Name);
            Assert.AreEqual(neighborChart.Sha256, afterDelete[neighborChart.Md5].Sha256);
            Assert.AreEqual(0, session.Window.ModalWindows.Length, "entry削除に確認modalはありません。");
            step("after real delete Task/raw DB/StructureChanged/old provider invalidation");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally { FinishSubscription(structureSubscription, callbackFailures, primary); }
    }

    private static void AssertObservedRow(ScenarioObservedResult result, DesktopChartInput chart)
    {
        Assert.IsNull(result.TaskFailure, "実業務Taskが失敗しています。");
        Assert.IsTrue(result.Rows.HasValue);
        System.Collections.Immutable.ImmutableArray<ScenarioObservedRow> rows = result.Rows.GetValueOrDefault();
        Assert.AreEqual(1, rows.Length);
        Assert.AreEqual("PlaylistEntry", rows[0].Kind);
        Assert.IsNull(rows[0].Path);
        Assert.AreEqual(chart.Md5, rows[0].Md5);
        Assert.AreEqual(chart.Sha256, rows[0].Sha256);
        Assert.AreEqual(DesktopPlaylistFixture.PlaylistId, rows[0].PlaylistId);
        Assert.AreEqual(DesktopPlaylistFixture.FolderName, rows[0].Folder);
    }

    private static void AssertUnavailable(Action readCurrentProperty)
    {
        try { readCurrentProperty(); }
        catch (FlaUI.Core.Exceptions.ElementNotAvailableException) { return; }
        catch (COMException failure) when (failure.HResult == unchecked((int)0x80040201)) { return; }
        Assert.Fail("削除された旧providerがElementNotAvailableを返しませんでした。");
    }

    /// <summary>旧cellの外部UIA pattern再取得がnullまたは厳密なElementNotAvailableで拒否されることを確認します。</summary>
    private static void AssertCurrentValuePatternUnavailable(AutomationElement cell)
    {
        // 保持済みclient patternのCurrentValueの例外変換は保証しない。
        // 全例外を握り潰すFlaUI TryGet系を使わず、同じ旧elementのnative取得結果だけを判定する。
        object? pattern;
        try
        {
            pattern = ((FlaUI.UIA3.UIA3FrameworkAutomationElement)cell.FrameworkAutomationElement)
                .NativeElement.GetCurrentPattern(FlaUI.UIA3.Patterns.ValuePattern.Pattern.Id);
        }
        catch (COMException failure) when (failure.HResult == unchecked((int)0x80040201)) { return; }
        Assert.IsNull(pattern, "削除された旧cellからValue patternを再取得できました。");
    }

    private static void ClickCell(AutomationElement cell, ProcessDeadline deadline, Action<string> step, Action<string> inputGate, MouseButton button = MouseButton.Left)
    {
        Rectangle bounds = cell.BoundingRectangle;
        Assert.IsFalse(cell.IsOffscreen);
        Assert.IsTrue(bounds.Width > 0 && bounds.Height > 0);
        EnsureInputDeadline(deadline);
        var point = new System.Drawing.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        step($"cell click id={cell.AutomationId} bounds={bounds} point={point} button={button}");
        inputGate("before cell click");
        Mouse.Click(point, button);
    }

    private static AutomationElement FindDeleteMenu(DesktopApplicationSession session, ProcessDeadline deadline, Action<string> step)
    {
        int query = 0;
        return Retry.WhileNull(() =>
        {
            int current = ++query;
            step($"before owned DeletePlaylistEntry query={current}");
            AutomationElement[] roots = session.Automation.GetDesktop()
                .FindAllChildren(cf => cf.ByProcessId(session.Driver.ProcessId));
            step($"after owned roots query={current} count={roots.Length}; before menu descendants");
            AutomationElement[] found = [.. roots
                .SelectMany(window => window.FindAllDescendants(cf => cf.ByAutomationId("DeletePlaylistEntry")))
                .Where(item => !item.IsOffscreen)];
            step($"after owned DeletePlaylistEntry query={current} count={found.Length}");
            if (found.Length > 1) { throw new InvalidOperationException("所有PID内の削除menuが重複しています。"); }
            return found.SingleOrDefault();
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "実右クリックのDeletePlaylistEntry menuがありません。").Result
            ?? throw new InvalidOperationException("削除menuを取得できませんでした。");
    }

    /// <summary>同梱辞書の一意な候補を実UIAで選び、Settings singletonを使わず保存fileと再表示結果を照合します。</summary>
    private static void ChangeSettingsAndReopen(DesktopApplicationSession session, DesktopPlaylistFixture fixture,
        AutomationElement table, ProcessDeadline deadline, Action<string> step)
    {
        string assets = Path.GetDirectoryName(DesktopPlaylistFixture.ExecutablePath)
            ?? throw new InvalidOperationException("実製品assetsの親がありません。");
        using var english = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "lang", "en-US.json")));
        string Resource(string key) => english.RootElement.GetProperty(key).GetString()
            ?? throw new InvalidDataException("同梱辞書の値が空です: " + key);
        string languageName = Resource("_language_name");
        string themeName = Resource("Appearance_theme_green_terminal");
        step("before MainChartTable original Name read");
        string originalTableName = table.Name;
        step("after MainChartTable original Name read");
        Window settings = OpenSettings(session.Window, deadline, step);
        string settingsHandle = settings.Properties.NativeWindowHandle.Value.ToString();
        void SettingsStep(string message) => step($"settingsHwnd={settingsHandle} {message}");
        SelectCategory(settings, "SettingsCategoryGeneral", deadline, SettingsStep);
        // ContentControlにはUIA peerがない。現在のpageだけを採用する所有Window内で意味IDの全一致を確認する。
        AutomationElement language = FindUnique(settings, "SettingsLanguage", deadline, SettingsStep);
        SelectOption(language, "SettingsLanguage", languageName, deadline, SettingsStep);
        SettingsStep("before raw Lang/LangDisplayName wait");
        _ = Retry.WhileFalse(() => RawSetting(fixture.SettingsPath, "Lang") == "en-US"
                && RawSetting(fixture.SettingsPath, "LangDisplayName") == languageName,
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "設定を閉じる前に言語の実保存値が公開されませんでした。");
        SettingsStep("after raw Lang/LangDisplayName wait; before MainChartTable/SettingsLanguage Name wait");
        bool namesSampled = false;
        _ = Retry.WhileFalse(() =>
        {
            if (!namesSampled) { SettingsStep("before first MainChartTable/SettingsLanguage Name query"); }
            string tableName = table.Name;
            string fieldName = language.Name;
            if (!namesSampled)
            {
                SettingsStep($"after first MainChartTable/SettingsLanguage Name query table={tableName} field={fieldName}");
                namesSampled = true;
            }
            return tableName == Resource("Playlist") && fieldName == Resource("Language");
        },
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "実言語変更が既存controlのNameへ反映されませんでした。");
        SettingsStep("after MainChartTable/SettingsLanguage Name wait; before metadata checks");
        Assert.AreNotEqual(originalTableName, table.Name);
        Assert.AreEqual("MainChartTable", table.AutomationId);

        SettingsStep("after metadata checks");
        SelectCategory(settings, "SettingsCategoryAppearance", deadline, SettingsStep);
        AutomationElement theme = FindUnique(settings, "SettingsTheme", deadline, SettingsStep);
        SelectOption(theme, "SettingsTheme", themeName, deadline, SettingsStep);
        SettingsStep("before raw AppearanceTheme wait");
        _ = Retry.WhileFalse(() => RawSetting(fixture.SettingsPath, "AppearanceTheme") == "GreenTerminal",
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "設定を閉じる前にテーマの実保存値が公開されませんでした。");
        SettingsStep("after raw AppearanceTheme wait");
        CloseSettings(session.Window, settings, deadline, SettingsStep);

        settings = OpenSettings(session.Window, deadline, step);
        settingsHandle = settings.Properties.NativeWindowHandle.Value.ToString();
        SelectCategory(settings, "SettingsCategoryGeneral", deadline, SettingsStep);
        language = FindUnique(settings, "SettingsLanguage", deadline, SettingsStep);
        SettingsStep("before reopened SettingsLanguage Selection check");
        AssertSelectedOption(language, languageName);
        SettingsStep("after reopened SettingsLanguage Selection check");
        SelectCategory(settings, "SettingsCategoryAppearance", deadline, SettingsStep);
        theme = FindUnique(settings, "SettingsTheme", deadline, SettingsStep);
        SettingsStep("before reopened SettingsTheme Selection check");
        AssertSelectedOption(theme, themeName);
        SettingsStep("after reopened SettingsTheme Selection check; before raw setting checks");
        Assert.AreEqual("en-US", RawSetting(fixture.SettingsPath, "Lang"));
        Assert.AreEqual(languageName, RawSetting(fixture.SettingsPath, "LangDisplayName"));
        Assert.AreEqual("GreenTerminal", RawSetting(fixture.SettingsPath, "AppearanceTheme"));
        SettingsStep("after raw setting checks");
        CloseSettings(session.Window, settings, deadline, SettingsStep);
    }

    private static void SelectCategory(Window settings, string id, ProcessDeadline deadline, Action<string> step)
    {
        AutomationElement category = FindUnique(settings, id, deadline, step);
        step("before Select " + id);
        category.Patterns.SelectionItem.Pattern.Select();
        step("after Select " + id);
    }

}
