using System.Collections.Concurrent;
using System.IO;
using System.Text;
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
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using SQLite;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.DesktopPlaylistOracle;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.DesktopScenarioUi;

namespace BeMusicSeeker.Desktop.E2E.Tests.Playlist;

/// <summary>通常の実検索・選択・並べ替えと、同期ON表のMEMO保存を別processの読戻しまで確認します。</summary>
[TestClass]
public sealed class SearchAndMemoDesktopTests
{
    /// <summary>500ms Bindingを通る検索と二行選択を実入力し、同期ONのMEMOだけをValue providerで確定して、正常終了後の同profileで保持を確認します。</summary>
    [TestMethod(DisplayName = "[DT-01] 実検索・選択と同期ON MEMOの保存・再起動")]
    [TestCategory("Daily")]
    public async Task SearchSelectionAndMemo_SurviveAnExplicitRestart()
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
        void Step(string message) => progress.WriteLine($"{DateTime.UtcNow:O} remainingMs={deadline.Remaining.TotalMilliseconds:F0} {message}");
        DesktopPlaylistFixture? fixture = null;
        Exception? caseFailure = null;
        try
        {
            fixture = DesktopPlaylistFixture.CreateSearchAndMemo(workspace);
            AssertSyncStorage(fixture, DesktopPlaylistFixture.InitialMemo, hashesExpected: false);
            string[]? originalLibrary = null;
            // 二回は意図した保存と読戻し。失敗したprocessの再試行やfixture再seedは行わない。
            for (int launch = 0; launch < 2; launch++)
            {
                Step("before explicit process start " + launch);
                DesktopApplicationSession session = await DesktopApplicationSession.StartAsync(fixture,
                    workspace.GetPath($"session-{launch}.json"), workspace.CaseId + "-" + launch, deadline);
                Exception? primary = null;
                try
                {
                    ScenarioInitialPlaylistSyncCompleted sync = await session.Driver.WaitForInitialPlaylistSyncAsync(deadline);
                    Assert.IsTrue(sync.Succeeded);
                    Assert.IsFalse(sync.WasSkipped);
                    Assert.IsNull(sync.TaskFailure);
                    Step("after actual initial playlist sync Task " + launch);
                    nint ownedWindow = session.Window.Properties.NativeWindowHandle.Value;
                    ActivateOwnedCaption(session.Window, session.Driver.ProcessId, ownedWindow, deadline, Step);
                    RequireOwnedForeground(session.Driver.ProcessId, ownedWindow, "after initial owned caption click", Step);
                    AssertSyncStorage(fixture, launch == 0 ? DesktopPlaylistFixture.InitialMemo : DesktopPlaylistFixture.EditedMemo,
                        hashesExpected: true);
                    if (launch == 0)
                    {
                        originalLibrary = ReadLibrarySnapshot(fixture);
                    }
                    await SearchSelectAndSortAsync(session, fixture, deadline, Step);
                    AutomationElement row = await ShowPlaylistEntryAsync(session, fixture, deadline, Step);
                    AutomationElement memo = FindUnique(row, "cell:Memo", deadline, Step);
                    Assert.IsFalse(memo.Patterns.Value.Pattern.IsReadOnly.Value, "外部同期ONでもMEMOは編集可能です。");
                    if (launch == 0)
                    {
                        Assert.AreEqual(DesktopPlaylistFixture.InitialMemo, memo.Patterns.Value.Pattern.Value.Value);
                        await SetMemoAsync(session, fixture, row, memo, deadline, Step);
                        ChangeOrVerifyTheme(session, fixture, deadline, Step, change: true);
                    }
                    else
                    {
                        Assert.AreEqual(DesktopPlaylistFixture.EditedMemo, memo.Patterns.Value.Pattern.Value.Value,
                            "同profileの明示再起動でMEMOが再読込されませんでした。");
                        ChangeOrVerifyTheme(session, fixture, deadline, Step, change: false);
                    }
                    AssertSyncStorage(fixture, DesktopPlaylistFixture.EditedMemo, hashesExpected: true);
                    Assert.IsNotNull(originalLibrary);
                    CollectionAssert.AreEqual(originalLibrary, ReadLibrarySnapshot(fixture), "非対象libraryの保存値が変わっています。");
                    foreach (DesktopChartInput chart in fixture.Charts)
                    {
                        CollectionAssert.AreEqual(chart.Bytes.ToArray(), File.ReadAllBytes(chart.Path));
                        CollectionAssert.AreEqual(chart.Resource.Bytes.ToArray(), File.ReadAllBytes(chart.Resource.Path));
                    }
                    Step("before normal Close " + launch);
                    await session.CloseAsync(deadline);
                    Step("after full owned process exit " + launch);
                }
                catch (Exception failure)
                {
                    BeginFailureCleanup(); primary = failure;
                    throw;
                }
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

    private static void ChangeOrVerifyTheme(DesktopApplicationSession session, DesktopPlaylistFixture fixture,
        ProcessDeadline deadline, Action<string> step, bool change)
    {
        string locale = RawSetting(fixture.SettingsPath, "Lang") ?? throw new InvalidDataException("保存localeがありません。");
        string assets = Path.GetDirectoryName(DesktopPlaylistFixture.ExecutablePath) ?? throw new InvalidOperationException();
        using var dictionary = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "lang", locale + ".json")));
        string themeName = dictionary.RootElement.GetProperty("Appearance_theme_green_terminal").GetString()
            ?? throw new InvalidDataException("現localeのテーマ表示名がありません。");
        Window settings = OpenSettings(session, deadline, step);
        FindUnique(settings, "SettingsCategoryAppearance", deadline, step).Patterns.SelectionItem.Pattern.Select();
        AutomationElement theme = FindUnique(settings, "SettingsTheme", deadline, step);
        if (change)
        {
            SelectOption(theme, "SettingsTheme", themeName, deadline, step);
            _ = Retry.WhileFalse(() => RawSetting(fixture.SettingsPath, "AppearanceTheme") == "GreenTerminal",
                timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
                timeoutMessage: "通常Close前にテーマが実保存されませんでした。");
        }
        else
        {
            AssertSelectedOption(theme, themeName);
            Assert.AreEqual("GreenTerminal", RawSetting(fixture.SettingsPath, "AppearanceTheme"));
        }
        CloseSettings(session, settings, deadline, step);
    }

    private static async Task SearchSelectAndSortAsync(DesktopApplicationSession session, DesktopPlaylistFixture fixture,
        ProcessDeadline deadline, Action<string> step)
    {
        _ = await WaitTableStateAsync(session, state => state.OperationSection == "Library" && state.Count == fixture.Charts.Count, deadline);
        AutomationElement table = FindUnique(session.Window, "MainChartTable", deadline, step);
        AutomationElement host = FindUnique(session.Window, "MainKeywordSearch", deadline, step);
        AutomationElement input = FindUnique(host, "KeywordSearchInput", deadline, step);
        DesktopChartInput[] expected = fixture.Charts.Where(chart => chart.Title.Contains(DesktopPlaylistFixture.SearchText, StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(4, expected.Length);
        var target = new ScenarioKeywordTarget(DesktopPlaylistFixture.SearchText);
        ScenarioObservationRegistered registration = await session.Driver.RegisterObservationAsync(target, deadline);
        step("after keyword registration ACK; before real keyboard input");
        EnsureInputDeadline(deadline);
        input.Focus();
        _ = Retry.WhileFalse(() => input.Properties.HasKeyboardFocus.Value, timeout: deadline.Remaining,
            throwOnTimeout: true, ignoreException: false);
        EnsureInputDeadline(deadline);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(DesktopPlaylistFixture.SearchText);
        ScenarioObservationCompleted completed = await session.Driver.AwaitObservationAsync(registration.RequestId, deadline);
        Assert.AreEqual(registration.RequestId, completed.RegistrationRequestId);
        Assert.AreEqual(target, completed.Result.Target);
        Assert.IsNull(completed.Result.TaskFailure);
        ScenarioObservedKeyword keyword = completed.Result.Keyword ?? throw new AssertFailedException("実検索結果がありません。");
        Assert.IsTrue(keyword.WasCommitted);
        Assert.IsTrue(keyword.ViewRequestId > 0);
        Assert.AreEqual(keyword.ViewRequestId, keyword.DisplayRequestId);
        Assert.AreEqual(keyword.RowsGeneration, keyword.DisplayRowsGeneration);
        Assert.IsTrue(keyword.RowsGeneration > registration.TableState.RowsGeneration);
        ScenarioTableState state = await WaitTableStateAsync(session,
            current => current.OperationSection == "Library" && current.Count == expected.Length && current.RowsGeneration == keyword.RowsGeneration, deadline);
        Assert.AreEqual("Library", state.SourceScope);
        // 保存されたsort状態に依存せず、四行の先頭二行を選ぶ前に昇順を確定する。
        foreach (string id in new[] { "header:Folder", "header:Title" })
        {
            EnsureInputDeadline(deadline);
            FindUnique(table, id, deadline, step).Patterns.Invoke.Pattern.Invoke();
        }
        string[] ascending = expected.Select(chart => chart.Title).Order(StringComparer.Ordinal).ToArray();
        _ = Retry.WhileFalse(() => VisibleRows(table).Select(row => row.Name).SequenceEqual(ascending),
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        AutomationElement[] rows = VisibleRows(table);
        Assert.AreEqual(expected.Length, rows.Length);
        CollectionAssert.AreEquivalent(expected.Select(chart => chart.Title).ToArray(), rows.Select(row => row.Name).ToArray());
        foreach (AutomationElement row in rows) { AssertLibraryIdentity(row, expected.Single(chart => chart.Title == row.Name)); }
        AutomationElement[] selectedRows = rows.Take(2).ToArray();
        string[] selectedIds = selectedRows.Select(row => row.AutomationId).ToArray();
        Assert.AreEqual(2, selectedIds.Distinct(StringComparer.Ordinal).Count());
        var selected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailures = new ConcurrentQueue<Exception>();
        PropertyId selectedProperty = rows[1].Patterns.SelectionItem.Pattern.PropertyIds.IsSelected;
        PropertyChangedEventHandlerBase subscription = rows[1].RegisterPropertyChangedEvent(TreeScope.Element, (sender, property, value) =>
        {
            try
            {
                if (property == selectedProperty && value is true && session.Automation.Compare(sender, rows[1]))
                { selected.TrySetResult(true); }
            }
            catch (Exception failure) { callbackFailures.Enqueue(failure); selected.TrySetException(failure); }
        }, selectedProperty);
        Exception? primary = null;
        try
        {
            EnsureInputDeadline(deadline);
            rows[0].Patterns.SelectionItem.Pattern.Select();
            rows[1].Patterns.SelectionItem.Pattern.AddToSelection();
            await selected.Task.WaitAsync(deadline.Remaining);
            AssertSelection(table, rows, selectedIds);
            EnsureInputDeadline(deadline);
            FindUnique(table, "header:Title", deadline, step).Patterns.Invoke.Pattern.Invoke();
            string[] expectedDescending = expected.Select(chart => chart.Title).OrderDescending(StringComparer.Ordinal).ToArray();
            _ = Retry.WhileFalse(() => VisibleRows(table).Select(row => row.Name).SequenceEqual(expectedDescending),
                timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
                timeoutMessage: "実Title見出しの降順が表示へ反映されませんでした。");
            AutomationElement[] sorted = VisibleRows(table);
            Assert.IsFalse(sorted.Take(2).Select(row => row.AutomationId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(selectedIds), "選択した二行の位置が変わる入力で添字保持の誤りを検出します。");
            foreach (AutomationElement row in sorted) { AssertLibraryIdentity(row, expected.Single(chart => chart.Title == row.Name)); }
            AssertSelection(table, sorted, selectedIds);
            step("after actual keyword Task/two-row selection/Title sort");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally { FinishSubscription(subscription, callbackFailures, primary); }
    }

    private static AutomationElement[] VisibleRows(AutomationElement table)
        => table.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Where(row => !row.IsOffscreen)
            .OrderBy(row => row.BoundingRectangle.Top).ToArray();

    private static void AssertLibraryIdentity(AutomationElement row, DesktopChartInput chart)
    {
        string[] parts = row.AutomationId.Split(':');
        Assert.AreEqual(4, parts.Length);
        Assert.AreEqual("chart", parts[0]);
        Assert.AreEqual("Bms", parts[2]);
        Assert.IsTrue(string.Equals(chart.Path, Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])), StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(chart.Title, row.Name);
    }

    private static void AssertSelection(AutomationElement table, AutomationElement[] rows, string[] ids)
    {
        CollectionAssert.AreEquivalent(ids, SelectionIds(table));
        foreach (AutomationElement row in rows)
        { Assert.AreEqual(ids.Contains(row.AutomationId, StringComparer.Ordinal), row.Patterns.SelectionItem.Pattern.IsSelected.Value); }
    }

    private static async Task<AutomationElement> ShowPlaylistEntryAsync(DesktopApplicationSession session,
        DesktopPlaylistFixture fixture, ProcessDeadline deadline, Action<string> step)
    {
        TreeItem root = FindUnique(session.Window, "PlaylistTreeRoot", deadline, step).AsTreeItem();
        _ = Retry.WhileFalse(() => root.ExpandCollapseState != ExpandCollapseState.LeafNode,
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        if (root.ExpandCollapseState == ExpandCollapseState.Collapsed) { root.Expand(); }
        TreeItem playlist = FindUnique(root, "PlaylistTable:" + DesktopPlaylistFixture.PlaylistId, deadline, step).AsTreeItem();
        ScenarioTableState before = (await session.Driver.TableStateAsync(deadline)).State;
        EnsureInputDeadline(deadline);
        playlist.Select();
        _ = await WaitTableStateAsync(session, state => state.OperationSection == "Playlist" && state.Count == 1
            && state.RowsGeneration > before.RowsGeneration, deadline);
        AutomationElement table = FindUnique(session.Window, "MainChartTable", deadline, step);
        AutomationElement row = table.Patterns.ItemContainer.Pattern.FindItemByProperty(null,
            session.Automation.PropertyLibrary.Element.AutomationId, fixture.EntryAutomationId(0))
            ?? throw new AssertFailedException("単一entryの実providerがありません。");
        EnsureInputDeadline(deadline);
        row.Patterns.VirtualizedItem.Pattern.Realize();
        _ = Retry.WhileFalse(() => !row.IsOffscreen, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        Assert.AreEqual(fixture.Charts[0].Title, row.Name);
        return row;
    }

    private static async Task SetMemoAsync(DesktopApplicationSession session, DesktopPlaylistFixture fixture,
        AutomationElement row, AutomationElement memo, ProcessDeadline deadline, Action<string> step)
    {
        DesktopChartInput chart = fixture.Charts[0];
        StoredEntry before = ReadEntries(fixture.SongDatabasePath).Single().Value;
        AssertSyncStorage(fixture, DesktopPlaylistFixture.InitialMemo, hashesExpected: true);
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailures = new ConcurrentQueue<Exception>();
        PropertyId propertyId = memo.Patterns.Value.Pattern.PropertyIds.Value;
        PropertyChangedEventHandlerBase subscription = memo.RegisterPropertyChangedEvent(TreeScope.Element, (sender, property, value) =>
        {
            try
            {
                if (property == propertyId && value is string text && text == DesktopPlaylistFixture.EditedMemo
                    && session.Automation.Compare(sender, memo)) { changed.TrySetResult(true); }
            }
            catch (Exception failure) { callbackFailures.Enqueue(failure); changed.TrySetException(failure); }
        }, propertyId);
        Exception? primary = null;
        try
        {
            var target = new ScenarioCellEditTarget(row.AutomationId, "memo", DesktopPlaylistFixture.EditedMemo, true);
            ScenarioObservationRegistered registration = await session.Driver.RegisterObservationAsync(target, deadline);
            Assert.AreEqual("Playlist", registration.TableState.OperationSection);
            Assert.AreEqual(1, registration.TableState.Count);
            step("after MEMO registration ACK; before Value.SetValue");
            EnsureInputDeadline(deadline);
            memo.Patterns.Value.Pattern.SetValue(DesktopPlaylistFixture.EditedMemo);
            ScenarioObservationCompleted completed = await session.Driver.AwaitObservationAsync(registration.RequestId, deadline);
            Assert.AreEqual(registration.RequestId, completed.RegistrationRequestId);
            Assert.AreEqual(target, completed.Result.Target);
            Assert.IsNull(completed.Result.TaskFailure);
            Assert.IsTrue(completed.Result.Rows.HasValue);
            System.Collections.Immutable.ImmutableArray<ScenarioObservedRow> facts = completed.Result.Rows.GetValueOrDefault();
            Assert.AreEqual(1, facts.Length);
            Assert.AreEqual("PlaylistEntry", facts[0].Kind);
            Assert.IsNull(facts[0].Path);
            Assert.AreEqual(chart.Md5, facts[0].Md5);
            Assert.AreEqual(chart.Sha256, facts[0].Sha256);
            Assert.AreEqual(DesktopPlaylistFixture.PlaylistId, facts[0].PlaylistId);
            Assert.AreEqual("1", facts[0].Folder);
            StoredEntry after = ReadEntries(fixture.SongDatabasePath).Single().Value;
            Assert.AreEqual(before.OtherValues, after.OtherValues);
            Assert.AreEqual(DesktopPlaylistFixture.EditedMemo, after.Memo);
            await changed.Task.WaitAsync(deadline.Remaining);
            Assert.AreEqual(DesktopPlaylistFixture.EditedMemo, memo.Patterns.Value.Pattern.Value.Value);
            AssertSyncStorage(fixture, DesktopPlaylistFixture.EditedMemo, hashesExpected: true);
            step("after MEMO actual Task/raw DB/ValueChanged");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally { FinishSubscription(subscription, callbackFailures, primary); }
    }

    private static void AssertSyncStorage(DesktopPlaylistFixture fixture, string expectedMemo, bool hashesExpected)
    {
        using var db = new SQLiteConnection(fixture.SongDatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT is_external_sync FROM playlist WHERE playlist_id=?", DesktopPlaylistFixture.PlaylistId));
        foreach (string column in new[] { "header_sha256", "data_sha256" })
        {
            string? hash = db.ExecuteScalar<string?>("SELECT " + column + " FROM playlist WHERE playlist_id=?", DesktopPlaylistFixture.PlaylistId);
            if (hashesExpected) { Assert.IsFalse(string.IsNullOrWhiteSpace(hash), column); }
            else { Assert.IsNull(hash, column); }
        }
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id=?", DesktopPlaylistFixture.PlaylistId));
        DesktopChartInput chart = fixture.Charts[0];
        Assert.AreEqual(expectedMemo, db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id=? AND md5=? AND sha256=?",
            DesktopPlaylistFixture.PlaylistId, chart.Md5, chart.Sha256));
    }

    private static string[] ReadLibrarySnapshot(DesktopPlaylistFixture fixture)
    {
        using var db = new SQLiteConnection(fixture.SongDatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        Assert.AreEqual(fixture.Charts.Count, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song"));
        foreach (DesktopChartInput chart in fixture.Charts)
        {
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path=? AND hash=? AND title=?", chart.Path, chart.Md5, chart.Title));
            Assert.AreEqual(chart.Sha256, db.ExecuteScalar<string>("SELECT sha256 FROM chart_digest_map WHERE md5=?", chart.Md5));
        }
        // 利用者列を含む非対象song全列を保存値のまま比較する。期待値は製品readerで再生成しない。
        string columns = string.Join("||','||", db.GetTableInfo("song").Select(column => "quote(\"" + column.Name.Replace("\"", "\"\"") + "\")"));
        return db.Query<RawSong>("SELECT " + columns + " AS Value FROM song ORDER BY path").Select(row => row.Value).ToArray();
    }

    private sealed class RawSong
    {
        public RawSong() { }
        public string Value { get; set; } = string.Empty;
    }
}
