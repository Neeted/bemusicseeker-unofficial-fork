using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.ViewModels;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace BeMusicSeeker.Desktop.E2E.Tests.Fixtures;

/// <summary>既存driverが所有する実processへFlaUIを接続し、UIA資源とprocessの回収順だけを担当します。</summary>
/// <remarks>UiModuleLifetimeのsession lease内で使用します。業務要求や独自通信・待機frameworkは追加しません。</remarks>
internal sealed class DesktopApplicationSession : IAsyncDisposable
{
    private readonly ApplicationScenarioDriver driver;
    private Application attached;

    private DesktopApplicationSession(ApplicationScenarioDriver driver, Application attached, UIA3Automation automation, Window window)
    {
        this.driver = driver;
        this.attached = attached;
        Automation = automation;
        Window = window;
    }

    /// <summary>このsessionだけが所有するUIA3接続です。</summary>
    internal UIA3Automation Automation { get; }
    /// <summary>所有PID内で一意と確認した、可視の実製品Windowです。</summary>
    internal Window Window { get; private set; }

    /// <summary>既存のtyped観測と期待自然終了を使うための、sessionが所有するdriverです。呼出元では破棄しません。</summary>
    internal ApplicationScenarioDriver Driver => driver;

    /// <summary>通常製品へ接続し、実初期化の成功を確認してから操作可能なsessionを返します。</summary>
    internal static async Task<DesktopApplicationSession> StartAsync(DesktopPlaylistFixture fixture, string manifestPath,
        string caseId, ProcessDeadline deadline)
    {
        DesktopApplicationSession session = await StartConnectedAsync(fixture, manifestPath, caseId, deadline).ConfigureAwait(false);
        try
        {
            ScenarioInitializationResult startup = await session.driver.WaitForStartupAsync(deadline).ConfigureAwait(false);
            if (startup.Outcome != StartupInitializationOutcome.Succeeded || !startup.CompletionPublished
                || !startup.IsInitializationCompleted || startup.Failure is not null)
            { throw new InvalidOperationException($"実製品の初期化が成功していません: {startup}"); }
            if (session.attached.GetAllTopLevelWindows(session.Automation).Count(candidate => !candidate.IsOffscreen) > 1)
            { throw new InvalidOperationException("初期化後に予期しない複数Windowが表示されています。"); }
            return session;
        }
        catch (Exception primary)
        {
            try { await session.DisposeAsync(deadline).ConfigureAwait(false); }
            catch (Exception cleanup) { primary.Data["DesktopSessionCleanupFailure"] = cleanup; }
            throw;
        }
    }

    /// <summary>初期化完了を待たず、既存driverの接続と同じ所有PIDのMainWindow取得まで進めます。</summary>
    /// <remarks>起動時確認modalの実操作に使います。返却だけでは起動成功を意味せず、呼出元が実初期化または期待退出を観測します。</remarks>
    internal static Task<DesktopApplicationSession> StartConnectedAsync(DesktopPlaylistFixture fixture, string manifestPath,
        string caseId, ProcessDeadline deadline)
        => StartConnectedAsync(DesktopPlaylistFixture.ExecutablePath, fixture.ProfileDirectory, manifestPath, caseId,
            fixture.UpdateManifestUrl, deadline);

    /// <summary>
    /// 明示した実行ファイル・profile・更新URLでtest sessionを起動し、初期化完了を待たず所有PIDのMainWindow取得まで進めます。
    /// </summary>
    /// <remarks>
    /// 展開した配布物のように、日常buildと異なる専有installationを起動するケースが使います。
    /// 返却だけでは起動成功を意味せず、呼出元が実初期化または期待退出を観測します。
    /// </remarks>
    internal static async Task<DesktopApplicationSession> StartConnectedAsync(string executablePath, string profileDirectory,
        string manifestPath, string caseId, string updateManifestUrl, ProcessDeadline deadline)
    {
        ApplicationScenarioDriver driver = await ApplicationScenarioDriver.StartAsync(executablePath,
            profileDirectory, manifestPath, caseId, updateManifestUrl, deadline).ConfigureAwait(false);
        Application? attached = null;
        UIA3Automation? automation = null;
        try
        {
            attached = Application.Attach(driver.ProcessId);
            automation = new UIA3Automation();
            Window window = FindMainWindow(attached, automation, () => driver.RootExit.IsCompleted, deadline);
            return new DesktopApplicationSession(driver, attached, automation, window);
        }
        catch (Exception primary)
        {
            var failures = new List<Exception>();
            try { automation?.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
            try { attached?.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
            try { await driver.DisposeAsync(deadline).ConfigureAwait(false); } catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count > 0) { primary.Data["DesktopSessionCleanupFailure"] = new AggregateException(failures); }
            throw;
        }
    }

    /// <summary>接続済みprocessの可視な実MainWindowを、期限内に一意に取得します。</summary>
    /// <remarks>driver経由と、通常起動受入のようにdriverを使わず接続する経路が同じ一意性規則を共有するための入口です。</remarks>
    internal static Window FindMainWindow(Application attached, UIA3Automation automation, Func<bool> hasExited, ProcessDeadline deadline)
        => Retry.WhileNull(() =>
        {
            if (hasExited()) { throw new InvalidOperationException("UIA接続前に所有processが終了しました。"); }
            Window[] windows = attached.GetAllTopLevelWindows(automation)
                .Where(candidate => !candidate.IsOffscreen && candidate.AutomationId == "BeMusicSeekerMainWindow").ToArray();
            if (windows.Length > 1) { throw new InvalidOperationException("所有PIDのMainWindow識別子が重複しています。"); }
            return windows.SingleOrDefault();
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "所有PIDの実MainWindowへ接続できませんでした。").Result
            ?? throw new InvalidOperationException("所有PIDのMainWindowがありません。");

    /// <summary>
    /// 外部要因の再起動を<see cref="ApplicationScenarioDriver.ExpectRestart"/>で登録済みの前提で、driverの後継接続を待ち、
    /// FlaUIを後継PIDへ付け直して後継のMainWindowを一意に取得します。
    /// </summary>
    /// <remarks>
    /// 旧processのexit 0・所有Job内の別PIDの確認はdriverが担います。旧processのUIA参照は、後継への接続とMainWindowの取得に成功した後に解放します。取得に失敗した場合は新しい接続だけを解放します。
    /// </remarks>
    /// <returns>後継の実PIDと開始時刻です。</returns>
    internal async Task<(int ProcessId, DateTime StartTime)> RebindAfterRestartAsync(ProcessDeadline deadline)
    {
        (int ProcessId, DateTime StartTime) successor = await driver.WaitForRestartAsync(deadline).ConfigureAwait(false);
        Application previous = attached;
        var next = Application.Attach(successor.ProcessId);
        try
        {
            using var observed = System.Diagnostics.Process.GetProcessById(successor.ProcessId);
            Window = FindMainWindow(next, Automation, () => observed.HasExited, deadline);
            attached = next;
        }
        catch { next.Dispose(); throw; }
        previous.Dispose();
        return successor;
    }

    /// <summary>接続中の所有PIDで、可視な最上位Windowを列挙します。予期しない複数Windowの検出に使います。</summary>
    internal Window[] VisibleTopLevelWindows() => [.. attached.GetAllTopLevelWindows(Automation).Where(candidate => !candidate.IsOffscreen)];

    /// <summary>通常Closeから実退出・両pipe・全childの完了まで、既存OwnedProcess境界で待ちます。</summary>
    internal Task CloseAsync(ProcessDeadline deadline) => driver.CloseAsync(deadline);

    /// <summary>UIAを先に解放し、FlaUIの参照handleを閉じてから、既存driverで実processを通常終了・必要時回収します。</summary>
    public ValueTask DisposeAsync() => DisposeAsync(ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));

    /// <summary>caseが確定した同じ回収期限を所有driverへ渡します。</summary>
    internal async ValueTask DisposeAsync(ProcessDeadline cleanupDeadline)
    {
        var failures = new List<Exception>();
        try { Automation.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
        // FlaUI Application.Disposeは参照Process handleだけを解放し、プロセスの所有はdriverへ残す。
        try { attached.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
        try { await driver.DisposeAsync(cleanupDeadline).ConfigureAwait(false); } catch (Exception cleanup) { failures.Add(cleanup); }
        if (failures.Count > 0) { throw new AggregateException("Desktop sessionの回収に失敗しました。", failures); }
    }
}
