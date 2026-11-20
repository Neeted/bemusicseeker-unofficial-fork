using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Processes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

namespace BeMusicSeeker.Desktop.E2E.Tests.Fixtures;

/// <summary>Desktopの実ケースで重複する意味locator・設定Window・期限と通知の回収を共有します。</summary>
internal static class DesktopScenarioUi
{
    /// <summary>起動成功後の専有Normal Windowで、進捗欄の占有域が表へ戻った実配置を待ちます。</summary>
    /// <remarks>DT03のsyncOFF・専有128譜面fixture用です。呼出元のStartAsync成功とplaylist採用を前提とし、表示時間や寸法の連続一致は待ちません。</remarks>
    internal static void WaitStartupProgressLayout(DesktopApplicationSession session, AutomationElement table,
        ProcessDeadline deadline, Action<string> step)
    {
        step("before startup progress presentation/layout terminal wait");
        // 起動進捗は同tokenの消去後に遅延reflectが再表示しない。追加初期化の親行は
        // scheduling前に開始し、予約・実行・warmupの集計終端まで維持される。
        // このfixtureでは後続の利用者操作をまだ始めず、進捗peerの取得時機に依存しない実配置を確認する。
        bool sampled = false;
        _ = Retry.WhileFalse(() =>
        {
            if (session.Window.Patterns.Window.Pattern.WindowVisualState.Value != WindowVisualState.Normal)
            { throw new InvalidOperationException("進捗表示終端のfixtureには通常表示のMainWindowが必要です。"); }
            Rectangle windowBounds = session.Window.BoundingRectangle;
            Rectangle tableBounds = table.BoundingRectangle;
            // 現行MainWindowはNormal時の外枠余白が0で、表はBottom Dockの進捗欄以外の末端まで伸びる。
            // peer不在を成功条件にせず、可視の表が実際に下端領域を回収したことを確認する。
            bool completed = !session.Window.IsOffscreen && !table.IsOffscreen && tableBounds.Width > 0 && tableBounds.Height > 0
                && Math.Abs(tableBounds.Bottom - windowBounds.Bottom) <= 1;
            if (!sampled || completed)
            {
                step($"startup progress layout table={tableBounds} window={windowBounds} completed={completed}");
                sampled = true;
            }
            return completed;
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "起動進捗の完了表示消去と表への配置反映が終わりませんでした。");
        step("after startup progress presentation/layout terminal wait");
    }

    /// <summary>実行操作を再送せず、同じcase期限内で非投影の表示状態だけを観測します。</summary>
    internal static async Task<ScenarioTableState> WaitTableStateAsync(DesktopApplicationSession session,
        Func<ScenarioTableState, bool> matches, ProcessDeadline deadline)
    {
        while (true)
        {
            ScenarioTableState state = (await session.Driver.TableStateAsync(deadline)).State;
            if (matches(state)) { return state; }
            TimeSpan remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero) { throw new TimeoutException("一覧状態の観測期限を過ぎました。"); }
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50));
        }
    }

    /// <summary>UIA購読を解除し、callbackと解除の失敗を主失敗を保って返します。</summary>
    internal static void FinishSubscription(IDisposable subscription, ConcurrentQueue<Exception> callbackFailures, Exception? primary)
    {
        try { subscription.Dispose(); }
        catch (Exception failure) { callbackFailures.Enqueue(failure); }
        if (callbackFailures.IsEmpty) { return; }
        var failureAggregate = new AggregateException("UIA通知または購読解除が失敗しました。", callbackFailures);
        if (primary is not null) { primary.Data["DesktopNotificationFailure"] = failureAggregate; }
        else { throw failureAggregate; }
    }

    /// <summary>各実入力の直前に共有case期限を確認します。</summary>
    internal static void EnsureInputDeadline(ProcessDeadline deadline)
    {
        if (deadline.Remaining <= TimeSpan.Zero) { throw new TimeoutException("実入力前にcase期限を過ぎました。"); }
    }

    /// <summary>同じF2 editorへ一度だけ入力し、全文とfocusを観測してから呼出元の確定・取消へ進みます。</summary>
    internal static void TypeEditorTextAndWait(AutomationElement editor, AutomationElement cell, string text,
        ProcessDeadline deadline, Action<string> step, Action<string>? observeInputPhase = null)
    {
        string? draft = null;
        bool? focused = null;
        try
        {
            observeInputPhase?.Invoke("before input");
            draft = editor.Patterns.Value.Pattern.Value.Value;
            focused = editor.Properties.HasKeyboardFocus.Value;
            step($"editor before input draft={JsonSerializer.Serialize(draft)} focus={focused} expected={JsonSerializer.Serialize(text)}");
            if (focused != true) { throw new InvalidOperationException("入力前に同じeditorのkeyboard focusが失われました。"); }
            EnsureInputDeadline(deadline);
            observeInputPhase?.Invoke("before Ctrl+A");
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            observeInputPhase?.Invoke("after Ctrl+A");
            draft = editor.Patterns.Value.Pattern.Value.Value;
            focused = editor.Properties.HasKeyboardFocus.Value;
            step($"editor after Ctrl+A draft={JsonSerializer.Serialize(draft)} focus={focused}");
            if (focused != true) { throw new InvalidOperationException("Ctrl+A後に同じeditorのkeyboard focusが失われました。"); }
            EnsureInputDeadline(deadline);
            observeInputPhase?.Invoke("before text");
            Keyboard.Type(text);
            observeInputPhase?.Invoke("after text");
            bool sampled = false;
            // Keyboard.Typeの復帰だけでは、別processのTextBoxへ全文が反映されたことを保証しない。
            // 消失・focus離脱を入力の再送やeditorの再取得で隠さず、同じproviderだけを読む。
            EnsureInputDeadline(deadline);
            _ = Retry.WhileFalse(() =>
            {
                draft = editor.Patterns.Value.Pattern.Value.Value;
                focused = editor.Properties.HasKeyboardFocus.Value;
                if (!sampled)
                {
                    step($"editor first observation draft={JsonSerializer.Serialize(draft)} focus={focused}");
                    sampled = true;
                }
                if (focused != true) { throw new InvalidOperationException("全文の確認前に同じeditorのkeyboard focusが失われました。"); }
                return string.Equals(draft, text, StringComparison.Ordinal);
            }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
                timeoutMessage: "同じeditorの入力全文をcase期限内に確認できませんでした。");
            step($"editor ready draft={JsonSerializer.Serialize(draft)} focus={focused}");
        }
        catch (Exception failure)
        {
            // 最後に読めた値と現在の同provider・確定セルを有限回だけ記録し、主失敗を保持する。
            failure.Data["DesktopEditorLastDraft"] = JsonSerializer.Serialize(draft);
            failure.Data["DesktopEditorLastFocus"] = focused;
            try { observeInputPhase?.Invoke("failure"); }
            catch (Exception diagnosticFailure) { failure.Data["DesktopFocusDiagnosticFailure"] = diagnosticFailure; }
            try { step("editor failure childCount=" + cell.FindAllChildren(cf => cf.ByAutomationId("CellEditor")).Length); }
            catch (Exception diagnosticFailure) { failure.Data["DesktopEditorChildDiagnosticFailure"] = diagnosticFailure; }
            try
            {
                step($"editor failure lastDraft={JsonSerializer.Serialize(draft)} lastFocus={focused}");
                step($"editor failure currentDraft={JsonSerializer.Serialize(editor.Patterns.Value.Pattern.Value.Value)} focus={editor.Properties.HasKeyboardFocus.Value}");
            }
            catch (Exception diagnosticFailure) { failure.Data["DesktopEditorDiagnosticFailure"] = diagnosticFailure; }
            try { step("editor failure committedCell=" + JsonSerializer.Serialize(cell.Patterns.Value.Pattern.Value.Value)); }
            catch (Exception diagnosticFailure) { failure.Data["DesktopCellDiagnosticFailure"] = diagnosticFailure; }
            throw;
        }
    }

    /// <summary>Desktopケースの初期準備として、所有Normal Windowのcaptionを検査して一回だけ通常クリックします。</summary>
    /// <remarks>専有interactive desktopで外部入力・重なり変更がないことを前提とします。hit検査と入力は原子的ではなく、UI leaseは利用者操作を排他しません。</remarks>
    internal static void ActivateOwnedCaption(Window window, int ownedPid, nint mainWindow,
        ProcessDeadline deadline, Action<string> step)
    {
        EnsureInputDeadline(deadline);
        if (window.Patterns.Window.Pattern.WindowVisualState.Value != WindowVisualState.Normal
            || window.IsOffscreen || !window.IsEnabled || window.ModalWindows.Length != 0)
        { throw new InvalidOperationException("初期入力にはmodalのない可視・有効な通常表示の所有MainWindowが必要です。"); }
        Rectangle bounds = window.BoundingRectangle;
        uint dpi = GetDpiForWindow(mainWindow);
        if (dpi == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        { throw new InvalidOperationException("所有Windowの実寸法またはDPIを取得できません。"); }
        // 現行WindowChromeのCaptionHeight=23、ResizeBorder=5 DIP内の一候補。
        // 座標だけで安全とせず、実HTCAPTIONを要求してボタン・client・resizeを除外する。
        var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + (int)Math.Round(14d * dpi / 96d));
        if (!bounds.Contains(point) || point.X < short.MinValue || point.X > short.MaxValue
            || point.Y < short.MinValue || point.Y > short.MaxValue)
        { throw new InvalidOperationException("caption候補を実Window内のnative hit座標で表現できません。"); }
        step($"initial caption bounds={bounds} dpi={dpi} point={point}");
        RequireCaptionHit("before cursor placement");
        EnsureInputDeadline(deadline);
        if (!User32.SetCursorPos(point.X, point.Y))
        { throw new InvalidOperationException("captionへの一回のcursor配置が失敗しました。"); }
        if (!User32.GetCursorPos(out POINT actual) || actual.X != point.X || actual.Y != point.Y)
        { throw new InvalidOperationException("cursor実位置がcaption候補と一致しません。"); }
        RequireCaptionHit("before button input");
        EnsureInputDeadline(deadline);
        // 保持中にcursorを動かさず、例外時も一組のUpを送ってbuttonを解放する。
        try { Mouse.Down(MouseButton.Left); }
        finally { Mouse.Up(MouseButton.Left); }
        step("after initial caption button down/up");

        void RequireCaptionHit(string phase)
        {
            EnsureInputDeadline(deadline);
            nint hit = WindowFromPoint(new POINT { X = point.X, Y = point.Y });
            bool ownWindow = hit == mainWindow && hit != 0;
            bool ownProcess = ownWindow && GetWindowThreadProcessId(hit, out uint process) != 0 && process == (uint)ownedPid;
            bool ownRoot = ownWindow && GetAncestor(hit, 2) == mainWindow; // GA_ROOT
            step($"caption hit phase={phase} ownWindow={ownWindow} ownProcess={ownProcess} ownRoot={ownRoot}");
            if (!ownWindow || !ownProcess || !ownRoot)
            { throw new InvalidOperationException("caption候補の実hitが所有MainWindowと一致しないため入力しません。"); }
            long remainingMs = (long)deadline.Remaining.TotalMilliseconds;
            if (remainingMs < 1) { throw new TimeoutException("caption hit照会前にcase期限を過ぎました。"); }
            int coordinates = unchecked((ushort)(short)point.X | ((ushort)(short)point.Y << 16));
            nint response = User32.SendMessageTimeout(mainWindow, 0x0084, UIntPtr.Zero, new IntPtr(coordinates),
                SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, (uint)Math.Min(1000, remainingMs), out UIntPtr hitResult);
            bool caption = response != 0 && hitResult.ToUInt64() == 2; // WM_NCHITTEST / HTCAPTION
            step($"caption hit phase={phase} responded={response != 0} isCaption={caption}");
            if (!caption) { throw new InvalidOperationException("所有Windowが候補をcaptionと判定しないため入力しません。"); }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    /// <summary>cached所有情報とnative foregroundを照合し、外部window情報を収集せず実入力の前提を確認します。</summary>
    internal static void RequireOwnedForeground(int ownedPid, nint mainWindow, string phase, Action<string> step)
    {
        nint foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out uint foregroundPid);
        bool isMain = foreground == mainWindow;
        bool isOwned = foregroundPid == (uint)ownedPid;
        step($"input gate phase={phase} foregroundIsMain={isMain} foregroundIsOwned={isOwned}");
        if (!isMain || !isOwned) { throw new InvalidOperationException("所有MainWindowがforegroundではないため実入力を送りません。"); }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    /// <summary>実Selection providerが返す生存行の意味IDだけを読みます。</summary>
    internal static string[] SelectionIds(AutomationElement table)
        => table.Patterns.Selection.Pattern.Selection.Value.Select(row => row.AutomationId).ToArray();

    /// <summary>所有MainWindowの実設定ボタンから一意な設定modalへ到達します。</summary>
    internal static Window OpenSettings(Window mainWindow, ProcessDeadline deadline, Action<string> step)
    {
        AutomationElement open = FindUnique(mainWindow, "OpenSettings", deadline, step);
        step("before OpenSettings IsEnabled wait");
        _ = Retry.WhileFalse(() => open.IsEnabled, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        step("after OpenSettings IsEnabled wait; before Invoke OpenSettings");
        open.Patterns.Invoke.Pattern.Invoke();
        step("after Invoke OpenSettings; before owned modal SettingsClose locator wait");
        bool sampled = false;
        Window result = Retry.WhileNull(() =>
        {
            if (!sampled) { step("before first MainWindow.ModalWindows/SettingsClose query"); }
            Window[] windows = mainWindow.ModalWindows.Where(window =>
                window.FindAllDescendants(cf => cf.ByAutomationId("SettingsClose")).Length == 1).ToArray();
            if (!sampled) { step("after first MainWindow.ModalWindows/SettingsClose query matches=" + windows.Length); sampled = true; }
            if (windows.Length > 1) { throw new InvalidOperationException("所有MainWindowの設定Windowが重複しています。"); }
            return windows.SingleOrDefault();
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "所有MainWindowの設定画面が開きませんでした。").Result
            ?? throw new InvalidOperationException("所有する設定Windowがありません。");
        step("after owned modal SettingsClose locator wait; before settings NativeWindowHandle read");
        return result;
    }

    /// <summary>設定の実Closeから所有MainWindowの復帰を同じ期限で待ちます。</summary>
    internal static void CloseSettings(Window mainWindow, Window settings, ProcessDeadline deadline, Action<string> step)
    {
        AutomationElement close = FindUnique(settings, "SettingsClose", deadline, step);
        step("before SettingsClose IsEnabled wait");
        _ = Retry.WhileFalse(() => close.IsEnabled, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        step("after SettingsClose IsEnabled wait; before Invoke SettingsClose");
        close.Patterns.Invoke.Pattern.Invoke();
        step("after Invoke SettingsClose; before MainWindow enabled/noModal wait");
        _ = Retry.WhileFalse(() => mainWindow.IsEnabled && mainWindow.ModalWindows.Length == 0,
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "設定画面が終了して所有MainWindowへ戻りませんでした。");
        step("after MainWindow enabled/noModal wait");
    }

    /// <summary>設定画面の縦タブを実入力と外部クライアントの要求で操作し、選択が外れないことと、キー操作で選択とフォーカスが移ることを確認します。</summary>
    /// <remarks>
    /// 通常のWindows module（共有ホスト）は物理の修飾キーとフォーカス取得を合否にしないため、実入力はこのDesktop caseが担います。
    /// 各入力の前に設定画面がforegroundであることと期限を確認し、修飾キーは例外時も必ず離します。
    /// 否定の観測は、選択を変えない後続の確認点（フォーカスの移動）を待ってから表明します。
    /// </remarks>
    internal static void VerifySettingsTabInput(Window settings, int ownedPid, ProcessDeadline deadline, Action<string> step)
    {
        string[] ids =
        [
            "SettingsCategoryGeneral", "SettingsCategoryAppearance", "SettingsCategoryPlayback", "SettingsCategoryAudio",
            "SettingsCategoryRecording", "SettingsCategoryPlaylist", "SettingsCategoryInstall", "SettingsCategoryBackup",
            "SettingsCategoryAdvanced", "SettingsCategoryRightClick", "SettingsCategoryAbout"
        ];
        AutomationElement navigation = FindUnique(settings, "SettingsCategoryNavigation", deadline, step);
        AutomationElement[] items = [.. ids.Select(id => FindUnique(settings, id, deadline, step))];
        AutomationElement header = FindUnique(settings, "SettingsSelectedPageHeader", deadline, step);
        nint settingsHandle = settings.Properties.NativeWindowHandle.Value;
        Assert.AreEqual(ControlType.Tab, navigation.ControlType, "縦タブはTabとして公開される。");
        Assert.IsTrue(items.All(item => item.ControlType == ControlType.TabItem), "項目はTabItemとして公開される。");
        Assert.IsTrue(navigation.Patterns.Selection.Pattern.IsSelectionRequired.Value, "選択必須です。");
        Assert.IsFalse(navigation.Patterns.Selection.Pattern.CanSelectMultiple.Value, "単一選択です。");

        int SelectedIndex()
        {
            int[] selected = [.. Enumerable.Range(0, items.Length).Where(i => items[i].Patterns.SelectionItem.Pattern.IsSelected.Value)];
            Assert.AreEqual(1, selected.Length, "有効なタブがちょうど一つ選ばれている。");
            return selected[0];
        }

        void AssertSelected(int index, string phase)
        {
            Assert.AreEqual(index, SelectedIndex(), phase + ": 選択");
            Assert.AreEqual(items[index].Name, header.Name, phase + ": 見出しは選択中のタブ名と一致する。");
            Assert.AreEqual(1, navigation.Patterns.Selection.Pattern.Selection.Value.Length, phase + ": 選択は一件。");
        }

        void WaitSelectedOnly(int index, string phase)
        {
            _ = Retry.WhileFalse(() => items[index].Patterns.SelectionItem.Pattern.IsSelected.Value,
                timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
                timeoutMessage: phase + ": 期待したタブが選択されませんでした。");
            AssertSelected(index, phase);
        }

        // タブ項目からの操作: 選択とともに、キーボードフォーカスが新しく選択したタブ項目へ移る。
        void WaitSelected(int index, string phase)
        {
            WaitSelectedOnly(index, phase);
            _ = Retry.WhileFalse(() => items[index].Properties.HasKeyboardFocus.Value,
                timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
                timeoutMessage: phase + ": 選択したタブ項目へキーボードフォーカスが移りませんでした。");
        }

        // ページ内容からの操作: 選択とともに、キーボードフォーカスは新しく選択したページの内側（WPF標準ではページ内の最初のフォーカス可能な要素）へ移り、タブ列には移らない。
        // 移動先の要素はページの構成で変わるため、要素名ではなく、選択中のタブ項目が公開するページの子孫であることで判定する。
        void WaitSelectedWithFocusInPage(int index, string phase)
        {
            WaitSelectedOnly(index, phase);
            ITreeWalker walker = settings.Automation.TreeWalkerFactory.GetRawViewWalker();
            string observed = "(none)";
            _ = Retry.WhileFalse(() =>
            {
                AutomationElement? focused = settings.Automation.FocusedElement();
                observed = focused == null ? "(none)" : $"{focused.ControlType} id={focused.Properties.AutomationId.ValueOrDefault}";
                return focused != null && IsInsideSelectedPage(walker, focused, index);
            }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
                timeoutMessage: phase + ": 新しく選択したページの内側へキーボードフォーカスが移りませんでした。");
            step($"settings tab {phase} focus in page: {observed}");
            Assert.IsFalse(items.Any(item => item.Properties.HasKeyboardFocus.Value), phase + ": フォーカスはタブ列に残らない。");
        }

        // フォーカス要素から親へたどり、タブ項目より先に本文のページに出会い、そのページを公開するタブ項目が期待した項目であることを確かめる。
        bool IsInsideSelectedPage(ITreeWalker walker, AutomationElement focused, int index)
        {
            bool insidePage = false;
            for (AutomationElement? current = focused; current != null; current = walker.GetParent(current))
            {
                if (current.ControlType == ControlType.TabItem)
                {
                    return insidePage && current.Equals(items[index]);
                }

                if (current.Properties.AutomationId.ValueOrDefault == "SettingsSelectedPageContent")
                {
                    insidePage = true;
                }

                if (current.Equals(settings))
                {
                    return false;
                }
            }

            return false;
        }

        void WaitFocus(AutomationElement element, string phase)
            => _ = Retry.WhileFalse(() => element.Properties.HasKeyboardFocus.Value,
                timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
                timeoutMessage: phase + ": 期待した要素へキーボードフォーカスが移りませんでした。");

        void Gate(string phase)
        {
            EnsureInputDeadline(deadline);
            nint foreground = GetForegroundWindow();
            _ = GetWindowThreadProcessId(foreground, out uint foregroundPid);
            step($"settings input gate phase={phase} foregroundIsSettings={foreground == settingsHandle} foregroundIsOwned={foregroundPid == (uint)ownedPid}");
            if (foreground != settingsHandle || foregroundPid != (uint)ownedPid)
            { throw new InvalidOperationException("設定画面がforegroundではないため実入力を送りません。"); }
        }

        // 修飾キーは例外時も必ず離す。
        void WithModifier(VirtualKeyShort modifier, Action input)
        {
            using (Keyboard.Pressing(modifier)) { input(); }
        }

        const int appearance = 1;
        const int about = 10;
        items[appearance].Patterns.SelectionItem.Pattern.Select();
        _ = Retry.WhileFalse(() => items[appearance].Patterns.SelectionItem.Pattern.IsSelected.Value,
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        AssertSelected(appearance, "初期");
        AutomationElement body = FindUnique(settings, "SettingsTheme", deadline, step);

        // Ctrl+クリック: 事前にフォーカスを本文へ置き、クリックした項目へのフォーカス移動を待ってから、選択が外れていないことを確かめる。
        body.Focus();
        WaitFocus(body, "Ctrl+クリックの前");
        Gate("before Ctrl+click");
        WithModifier(VirtualKeyShort.CONTROL, () => items[appearance].Click());
        WaitFocus(items[appearance], "Ctrl+クリック");
        AssertSelected(appearance, "Ctrl+クリック");
        step("settings tab Ctrl+click kept selection");

        // Ctrl+Space: 直後のTabで本文側へフォーカスが移るのを待ってから、選択が外れていないことを確かめる。
        items[appearance].Focus();
        WaitFocus(items[appearance], "Ctrl+Spaceの前");
        Gate("before Ctrl+Space");
        WithModifier(VirtualKeyShort.CONTROL, () => Keyboard.Type(VirtualKeyShort.SPACE));
        Gate("before Tab after Ctrl+Space");
        Keyboard.Type(VirtualKeyShort.TAB);
        _ = Retry.WhileFalse(() => !items[appearance].Properties.HasKeyboardFocus.Value,
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "Tabで本文側へフォーカスが移りませんでした。");
        AssertSelected(appearance, "Ctrl+Space");
        step("settings tab Ctrl+Space kept selection");

        // 外部クライアントからの RemoveFromSelection は、拒否でも無操作でもよく、選択は外れない。
        try { items[appearance].Patterns.SelectionItem.Pattern.RemoveFromSelection(); }
        catch (Exception rejected) when (rejected is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            step("settings tab RemoveFromSelection rejected: " + rejected.GetType().Name);
        }
        AssertSelected(appearance, "RemoveFromSelection");

        // 矢印（縦タブは上下）、End、Home。
        items[appearance].Focus();
        WaitFocus(items[appearance], "矢印の前");
        Gate("before Down");
        Keyboard.Type(VirtualKeyShort.DOWN);
        WaitSelected(appearance + 1, "Down");
        Gate("before Up");
        Keyboard.Type(VirtualKeyShort.UP);
        WaitSelected(appearance, "Up");
        Gate("before End");
        Keyboard.Type(VirtualKeyShort.END);
        WaitSelected(about, "End");
        Gate("before Home");
        Keyboard.Type(VirtualKeyShort.HOME);
        WaitSelected(0, "Home");

        // 本文から Ctrl+Tab / Ctrl+Shift+Tab: 次・前の有効なタブへ循環し、フォーカスは新しく選択したページの内側へ移る。
        // Ctrl+Tab の後もフォーカスは本文にあるため、続く Ctrl+Shift+Tab も本文からの操作になる。
        items[about].Patterns.SelectionItem.Pattern.Select();
        _ = Retry.WhileFalse(() => items[about].Patterns.SelectionItem.Pattern.IsSelected.Value,
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false);
        AutomationElement content = FindUnique(settings, "SettingsSelectedPageContent", deadline, step);
        AutomationElement contentButton = content.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button))
            ?? throw new InvalidOperationException("最後のページに本文のフォーカス先がありません。");
        contentButton.Focus();
        WaitFocus(contentButton, "Ctrl+Tabの前");
        Gate("before Ctrl+Tab from content");
        WithModifier(VirtualKeyShort.CONTROL, () => Keyboard.Type(VirtualKeyShort.TAB));
        WaitSelectedWithFocusInPage(0, "本文からのCtrl+Tab（末尾の次は先頭）");
        Gate("before Ctrl+Shift+Tab");
        WithModifier(VirtualKeyShort.CONTROL, () => WithModifier(VirtualKeyShort.SHIFT, () => Keyboard.Type(VirtualKeyShort.TAB)));
        WaitSelectedWithFocusInPage(about, "本文からのCtrl+Shift+Tab（先頭の前は末尾）");
        step("settings tab keyboard navigation verified");
    }

    /// <summary>指定した親scopeの意味IDを厳密一件で取得します。</summary>
    internal static AutomationElement FindUnique(AutomationElement scope, string id, ProcessDeadline deadline, Action<string> step)
    {
        step("before FindUnique wait " + id);
        bool sampled = false;
        AutomationElement result = Retry.WhileNull(() =>
        {
            if (!sampled) { step("before first FindAllDescendants " + id); }
            AutomationElement[] found = scope.FindAllDescendants(cf => cf.ByAutomationId(id));
            if (!sampled) { step($"after first FindAllDescendants {id} matches={found.Length}"); sampled = true; }
            if (found.Length > 1) { throw new InvalidOperationException("親scope内でAutomationIdが重複しています: " + id); }
            return found.SingleOrDefault();
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "親scope内の要素が見つかりません: " + id).Result
            ?? throw new InvalidOperationException("UIA要素がありません: " + id);
        step("after FindUnique wait " + id);
        return result;
    }

    /// <summary>実ComboBoxの完全表示名で候補を一意に選び、選択反映を確認します。</summary>
    internal static void SelectOption(AutomationElement comboBox, string id, string name, ProcessDeadline deadline, Action<string> step)
    {
        step("before Expand " + id);
        comboBox.Patterns.ExpandCollapse.Pattern.Expand();
        step($"after Expand {id}; before option wait exactName={name}");
        if (id == "SettingsTheme")
        {
            // themeは少数の固定候補。Name接続と候補の公開状態を一回だけ診断し、期待値には使わない。
            step("before SettingsTheme direct child diagnostics");
            AutomationElement[] children = comboBox.FindAllChildren();
            step("SettingsTheme direct child count=" + children.Length);
            foreach (AutomationElement child in children.Take(16))
            {
                step($"SettingsTheme child name={child.Name} id={child.AutomationId} type={child.ControlType}");
            }
            step("after SettingsTheme direct child diagnostics");
        }
        bool sampled = false;
        AutomationElement option = Retry.WhileNull(() =>
        {
            if (!sampled) { step("before first ListItem exactName query " + id); }
            AutomationElement[] matches = comboBox.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem).And(cf.ByName(name)));
            if (!sampled) { step($"after first ListItem exactName query {id} matches={matches.Length}"); sampled = true; }
            if (matches.Length > 1) { throw new InvalidOperationException("展開した候補の完全Nameが重複しています: " + name); }
            return matches.SingleOrDefault();
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "展開した候補に完全Nameが見つかりません: " + name).Result
            ?? throw new InvalidOperationException("実候補がありません: " + name);
        step("after option wait; before Select option " + id);
        option.Patterns.SelectionItem.Pattern.Select();
        step("after Select option; before Collapse " + id);
        comboBox.Patterns.ExpandCollapse.Pattern.Collapse();
        step("after Collapse; before Selection check " + id);
        AssertSelectedOption(comboBox, name);
        step("after Selection check " + id);
    }

    /// <summary>実Selection providerが返す一意な選択名を確認します。</summary>
    internal static void AssertSelectedOption(AutomationElement comboBox, string name)
    {
        AutomationElement[] selected = comboBox.Patterns.Selection.Pattern.Selection.Value;
        Assert.AreEqual(1, selected.Length, "実ComboBoxの選択は一件でなければなりません。");
        Assert.AreEqual(name, selected[0].Name);
    }

    /// <summary>製品の原子的保存を妨げず、専有設定fileの一世代を直接読みます。</summary>
    internal static string? RawSetting(string path, string name)
    {
        // 製品のFile.Replaceによる原子的公開を妨げず、開いた一世代のfileだけを読む。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var document = XDocument.Load(stream);
        XElement section = document.Root?.Element("userSettings")?.Element("BeMusicSeeker.Properties.Settings")
            ?? throw new InvalidDataException("Portable設定の保存sectionがありません。");
        return section.Elements("setting").SingleOrDefault(element => (string?)element.Attribute("name") == name)?.Element("value")?.Value;
    }
}
