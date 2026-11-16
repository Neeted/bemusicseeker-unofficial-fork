using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Scenarios;

/// <summary>実配布受入の通常起動・実HWND・所有後継・全終端を管理します。</summary>
/// <remarks>Scenariosの旧実配布受入とDesktop.E2Eの通常起動受入が同じ起動・回収規則を共有するため、TestSupportに置きます。</remarks>
internal sealed class PublishedApplicationSession
{
    private readonly List<OwnedProcess> processes = [];
    private readonly List<Process> observations = [];
    private readonly List<SingleRequestHttpServer> servers = [];
    private readonly CancellationTokenSource operation;
    private readonly Lazy<ProcessDeadline> cleanup;
    private readonly Func<nint, WindowFacts?> describe;

    /// <summary>case全体の期限を一度だけ取り込み、可変媒体と通常childの所有を開始します。失敗回収は独立した10秒です。</summary>
    internal PublishedApplicationSession(int seconds)
        : this(ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromSeconds(seconds)), DescribeWithManagedUia,
            () => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)))
    {
    }

    private PublishedApplicationSession(ProcessDeadline deadline, Func<nint, WindowFacts?> describe, Func<ProcessDeadline> cleanupFactory)
    {
        Deadline = deadline;
        this.describe = describe;
        cleanup = new Lazy<ProcessDeadline>(cleanupFactory);
        operation = new CancellationTokenSource(Deadline.Remaining);
        Workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
    }

    /// <summary>
    /// Desktop.E2Eの期限規則で開始します。外側の期限(suiteと指定時間の早い方)から回収用の10秒を除いた操作期限を
    /// 全段階へ渡し、最初の失敗では外側の期限を超えない範囲で10秒の回収期限を一度だけ確定します。
    /// </summary>
    internal static PublishedApplicationSession ForDesktop(TimeSpan outerMaximum, Func<nint, WindowFacts?> describe)
    {
        var outer = ProcessDeadline.FromSuiteEnvironment(outerMaximum);
        TimeSpan remaining = outer.Remaining;
        if (remaining <= TimeSpan.FromSeconds(10)) { throw new TimeoutException("入力と回収に必要な残期限がありません。"); }
        return new PublishedApplicationSession(outer.LimitRemaining(remaining - TimeSpan.FromSeconds(10)), describe,
            () => outer.LimitRemaining(TimeSpan.FromSeconds(10)));
    }

    /// <summary>case全段階が共有する操作期限です。</summary>
    internal ProcessDeadline Deadline { get; }
    /// <summary>配布コピー・profile・診断を保持する専有媒体です。</summary>
    internal TestWorkspace Workspace { get; }

    /// <summary>起動する配布の場所、起動時のCWD、通常childへ渡す専有環境、診断の保存先です。</summary>
    /// <param name="AppRoot">展開済み実配布の専有installationです。</param>
    /// <param name="WorkingDirectory">childのCWDです。</param>
    /// <param name="ChildEnvironment">親を変更せず通常childへ渡す専有環境です。nullの値は子から除きます。</param>
    /// <param name="DiagnosticsDirectory">既知フォルダ観測など、診断fileの保存先です。</param>
    internal sealed record Launch(string AppRoot, string WorkingDirectory,
        IReadOnlyDictionary<string, string?> ChildEnvironment, string DiagnosticsDirectory);

    /// <summary>同じ専有子環境の.NET 10 probeで既知フォルダを確認し、不一致では通常appを起動しません。</summary>
    /// <remarks>製品のPathSnapshotも同じGetFolderPathを使います。配布runtimeのbytes同一や既知フォルダAPI内部の解決経路は保証しません。</remarks>
    internal async Task AssertIsolationAsync(Launch launch)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, @"fixtures\windows-process-probe\WindowsProcessProbe.exe");
        OwnedProcess probe = Start(executable, ["known-folder"], launch.WorkingDirectory, launch.ChildEnvironment);
        ProcessCompletion result = await probe.WaitForExitAsync(Deadline);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        JsonElement observation = document.RootElement;
        string expected = launch.ChildEnvironment["LOCALAPPDATA"] ?? throw new InvalidDataException();
        Assert.IsTrue(string.Equals(expected, observation.GetProperty("LocalApplicationData").GetString(), StringComparison.OrdinalIgnoreCase),
            "通常childのLocalApplicationDataが専有先と一致しません。");
        string runtimeVersion = observation.GetProperty("RuntimeVersion").GetString()
            ?? throw new InvalidDataException("probeの実行runtime版がありません。");
        Assert.AreEqual(10, Version.Parse(runtimeVersion).Major, "隔離前提のprobeには.NET 10が必要です。");
        Console.WriteLine("通常childの既知フォルダ/runtime観測: " + result.StandardOutput);
        File.WriteAllText(Path.Combine(launch.DiagnosticsDirectory, "known-folder-observation.json"), result.StandardOutput);
    }

    /// <summary>global singletonを維持し、専有NoUpdate入力だけを一般CLIへ渡して通常起動します。</summary>
    internal async Task<(OwnedProcess Owner, Process Process)> StartApplicationAsync(Launch launch, bool maintenance)
    {
        Process[] existing = Process.GetProcessesByName("BeMusicSeeker");
        try { Assert.AreEqual(0, existing.Length, "既存の利用者appが占有しています。停止せず受入を失敗にします。"); }
        finally { foreach (Process process in existing) { process.Dispose(); } }
        ArchiveLogs(launch.AppRoot);
        SingleRequestHttpServer server = StartNoUpdateServer();
        OwnedProcess owner = Start(Path.Combine(launch.AppRoot, "BeMusicSeeker.exe"),
            ["--update-manifest-url=" + server.Address.AbsoluteUri], launch.WorkingDirectory, launch.ChildEnvironment);
        var observation = Process.GetProcessById(owner.ProcessId);
        observations.Add(observation);
        _ = observation.Handle;
        Assert.IsTrue(owner.ContainsProcess(observation));
        await WaitReadyAsync(observation, launch.AppRoot, maintenance);
        // 更新用workを準備する前に、この起動が先に行う旧work回収とNoUpdate応答を完了させます。
        await server.Completion.WaitAsync(operation.Token);
        // 単一要求のserverなので、保証するのは要求が届いたことです。2回目以降の要求は検出しません。
        Assert.AreEqual(1, server.RequestTargets.Length, "起動時の更新確認の要求が届いていません。");
        return (owner, observation);
    }

    /// <summary>
    /// 現在版より小さい版（0.0.0.0）のmanifestを1回だけ返すloopback serverを、このsessionの所有・破棄対象として開始します。
    /// 起動した元アプリと更新後の後継アプリが、それぞれ別のserverで更新なしの応答を受けるために使います。
    /// </summary>
    internal SingleRequestHttpServer StartNoUpdateServer()
    {
        var server = new SingleRequestHttpServer(operation.Token, Encoding.UTF8.GetBytes("""
            {"schemaVersion":1,"version":"0.0.0.0","releaseTag":"v0.0.0.0","releasePageUrl":"https://example.invalid/release","packageFormatVersion":1,"minimumUpdaterVersion":"1","assets":[{"kind":"app","label":"fixture","fileName":"fixture.zip","url":"https://example.invalid/fixture.zip","sha256":"0000000000000000000000000000000000000000000000000000000000000000","sizeBytes":1,"includesChartInfoMetadata":false}]}
            """));
        servers.Add(server);
        return server;
    }

    /// <summary>
    /// 起動済みのprocessがserverへ更新確認を送ったことをsessionの期限内で待ちます。
    /// 要求の前にprocessが終了した場合は、期限切れを待たず失敗にします。
    /// </summary>
    internal async Task WaitRequestAsync(SingleRequestHttpServer server, Process process)
    {
        Task exited = process.WaitForExitAsync(operation.Token);
        Task finished = await Task.WhenAny(server.Completion, exited);
        if (finished == exited && !server.Completion.IsCompleted)
        {
            // 期限による取り消しはappの終了と区別し、ExitCodeを参照せず期限切れとして伝える。
            operation.Token.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"更新確認の要求前にappが終了しました: {process.ExitCode}");
            }
        }
        await server.Completion.WaitAsync(operation.Token);
        Assert.AreEqual(1, server.RequestTargets.Length, "更新確認の要求がちょうど1回届いていません。");
    }

    /// <summary>停止済みログだけを退避し、次の起動のmarkerと混同しません。</summary>
    internal static void ArchiveLogs(string appRoot)
    {
        string log = Path.Combine(appRoot, "log");
        if (!Directory.Exists(log)) { return; }
        string archive = Path.Combine(log, "prior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        foreach (string path in Directory.EnumerateFiles(log, "*.log")) { File.Move(path, Path.Combine(archive, Path.GetFileName(path))); }
    }

    /// <summary>起動前にJob所有を確立し、失敗時の共通cleanup期限を既存process主体へ渡します。</summary>
    internal OwnedProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environment)
    {
        CheckDeadline();
        var process = OwnedProcess.Start(new ProcessStartRequest(executable, arguments, workingDirectory, environment), () => cleanup.Value);
        processes.Add(process);
        return process;
    }

    /// <summary>marker・実所有MainWindow・非modalを同じ起動で観測し、起動阻害や先行終了を拒否します。</summary>
    internal async Task WaitReadyAsync(Process process, string appRoot, bool maintenance)
    {
        while (true)
        {
            CheckDeadline();
            if (process.HasExited) { throw new InvalidOperationException($"ready前にappが終了しました: {process.ExitCode}"); }
            nint main = FindMainWindow(process.Id);
            if (main != 0) { AssertNoModal(process.Id, main); }
            string logDirectory = Path.Combine(appRoot, "log");
            string logs = Directory.Exists(logDirectory)
                ? string.Join("\n", Directory.EnumerateFiles(logDirectory, "*.log").Select(ReadSharedLog)) : string.Empty;
            // schema確認と初期化失敗の通知は所有modalなので、上のAssertNoModalが検出します。初期化失敗の経路はreadyを出さないため、
            // modalを観測する前に閉じられても期限切れとして失敗します。ログで見るのは、modalを出さない設定検証の失敗だけです。
            Assert.IsFalse(logs.Contains("startup_setting_validation_failed", StringComparison.Ordinal), "startup_setting_validation_failed");
            if (main != 0 && logs.Contains("startup_ready_operable", StringComparison.Ordinal)
                && (!maintenance || logs.Contains("startup_post_initialization_maintenance_complete", StringComparison.Ordinal))) { return; }
            await Task.Delay(100, operation.Token);
        }
    }

    /// <summary>保持handleの実所有MainWindowへWM_CLOSEを送り、Killを正常終了の代用にせず実exit 0まで待ちます。</summary>
    internal async Task CloseAsync(OwnedProcess owner, Process process, bool waitWholeJob)
    {
        CheckDeadline();
        Assert.IsTrue(owner.ContainsProcess(process), "閉鎖対象が所有Job外です。");
        nint window = FindMainWindow(process.Id);
        Assert.AreNotEqual((nint)0, window, "通常閉鎖するMainWindowがありません。");
        AssertNoModal(process.Id, window);
        if (!PostMessage(window, 0x0010, 0, 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        await process.WaitForExitAsync(operation.Token);
        Assert.AreEqual(0, process.ExitCode, "通常閉鎖後の実exit codeです。");
        if (waitWholeJob) { Assert.AreEqual(0, (await owner.WaitForExitAsync(Deadline)).ExitCode); }
    }

    /// <summary>専有完全path・生成時刻・旧PID除外で候補を絞り、実Job所属を確認した単一後継handleだけを保持します。</summary>
    internal async Task<Process> FindSuccessorAsync(OwnedProcess updater, string executable, int previousPid, DateTime earliestCreationUtc)
    {
        while (true)
        {
            CheckDeadline();
            var candidates = new List<Process>();
            try
            {
                foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                {
                    bool retained = false;
                    try
                    {
                        _ = process.Handle;
                        if (process.Id == previousPid || process.HasExited) { continue; }
                        if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) { continue; }
                        if (process.StartTime.ToUniversalTime() < earliestCreationUtc) { throw new InvalidOperationException("専有pathに古い後継候補があります。"); }
                        Assert.IsTrue(updater.ContainsProcess(process), "専有pathの後継候補がupdater Job外です。");
                        candidates.Add(process);
                        retained = true;
                    }
                    finally { if (!retained) { process.Dispose(); } }
                }
                Assert.IsTrue(candidates.Count <= 1, "後継候補が複数あります。");
                if (candidates.Count == 1)
                {
                    Process successor = candidates[0];
                    observations.Add(successor);
                    candidates.Clear();
                    return successor;
                }
                if (updater.Completion.IsCompleted) { throw new InvalidOperationException("所有Jobが後継未到達で終端しました。"); }
            }
            finally { foreach (Process process in candidates) { process.Dispose(); } }
            await Task.Delay(100, operation.Token);
        }
    }

    /// <summary>公開handshakeのready fileを待ち、先行root終了と期限切れを成功にしません。</summary>
    internal async Task WaitHandshakeAsync(OwnedProcess updater, string ready)
    {
        while (!File.Exists(ready))
        {
            CheckDeadline();
            if (updater.RootExit.IsCompleted) { throw new InvalidOperationException("ready前にupdaterが終了しました。"); }
            await Task.Delay(100, operation.Token);
        }
    }

    /// <summary>正常時は操作期限内、最初の失敗からは一回だけ確定した10秒で全ownedを回収し、一次失敗と両方を保持します。</summary>
    internal async Task CompleteAsync(Exception? primary)
    {
        var failures = new List<Exception>();
        if (primary is not null) { _ = cleanup.Value; }
        if (primary is OwnedProcessStartException { CleanupFailure: not null } start) { failures.Add(start.CleanupFailure); }
        ProcessDeadline Budget() => primary is not null || failures.Count != 0 ? cleanup.Value : Deadline;
        foreach (OwnedProcess process in processes.AsEnumerable().Reverse())
        {
            try
            {
                ProcessStopResult stopped = await process.StopAsync(null, Budget());
                if (stopped.CleanupFailure is not null) { failures.Add(stopped.CleanupFailure); _ = cleanup.Value; }
            }
            catch (Exception exception) { failures.Add(exception); _ = cleanup.Value; }
            try { await process.DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); _ = cleanup.Value; }
        }
        foreach (SingleRequestHttpServer server in servers)
        {
            try
            {
                using var budget = new CancellationTokenSource(Budget().Remaining);
                await server.DisposeAsync(budget.Token);
            }
            catch (Exception exception) { failures.Add(exception); _ = cleanup.Value; }
        }
        foreach (Process process in observations)
        {
            try { process.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        operation.Dispose();
        // 同期的な媒体回収も正常時の操作期限に含め、前後で同じ期限を照合します。
        ProcessDeadline workspaceDeadline = Budget();
        bool expiredBeforeWorkspaceCleanup = workspaceDeadline.Remaining == TimeSpan.Zero;
        if (expiredBeforeWorkspaceCleanup)
        {
            failures.Add(new TimeoutException("workspace回収前に実配布受入の共通期限が切れました。"));
            _ = cleanup.Value;
        }
        if (primary is not null || failures.Count > 0) { Workspace.PreserveArtifacts(); }
        try { Workspace.Dispose(); }
        catch (Exception exception)
        {
            failures.Add(exception);
            _ = cleanup.Value;
            Workspace.PreserveArtifacts();
        }
        if (!expiredBeforeWorkspaceCleanup && workspaceDeadline.Remaining == TimeSpan.Zero)
        {
            failures.Add(new TimeoutException("workspace回収の完了が実配布受入の共通期限を超えました。"));
            _ = cleanup.Value;
            Workspace.PreserveArtifacts();
        }
        if (primary is not null)
        {
            if (failures.Count > 0)
            {
                throw new AggregateException("実配布受入の一次失敗と回収失敗が発生しました。", failures.Prepend(primary));
            }
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (failures.Count > 0) { throw new AggregateException("実配布受入の回収を確認できません。", failures); }
    }

    private void CheckDeadline()
    {
        if (Deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("実配布受入の共有期限が切れました。"); }
    }

    private static string ReadSharedLog(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }

    private nint FindMainWindow(int pid)
    {
        nint found = 0;
        foreach (nint window in Windows(pid))
        {
            if (GetWindow(window, 4) != 0 || !IsWindowVisible(window)) { continue; }
            if (describe(window)?.AutomationId == "BeMusicSeekerMainWindow")
            {
                Assert.AreEqual((nint)0, found, "MainWindowが複数あります。");
                found = window;
            }
        }
        return found;
    }

    private void AssertNoModal(int pid, nint main)
    {
        foreach (nint window in Windows(pid))
        {
            if (GetWindow(window, 4) != main || !IsWindowVisible(window) || !IsWindowEnabled(window)) { continue; }
            WindowFacts? facts = ObserveWindow(window);
            if (facts is { IsOffscreen: false, IsModal: true }) { throw new InvalidOperationException("予期しない所有modalがあります。"); }
        }
    }

    /// <summary>可視で有効な所有windowの事実を、session期限内で再観測します。読めないまま期限が来たら通過させず失敗にします。</summary>
    private WindowFacts? ObserveWindow(nint window)
    {
        while (true)
        {
            WindowFacts? facts = describe(window);
            if (facts is not null) { return facts; }
            if (Deadline.Remaining == TimeSpan.Zero) { throw new InvalidOperationException("可視で有効な所有windowのUIA情報を期限内に観測できません。"); }
            Thread.Sleep(50);
        }
    }

    /// <summary>最初の失敗を検出した時点で、失敗回収の期限を一度だけ確定します。以後の破棄や回収は、この期限内で行います。</summary>
    /// <remarks>既に確定済みなら何もしません。<see cref="CompleteAsync"/>も一次失敗があれば同じ期限を使います。</remarks>
    internal void BeginFailureCleanup() => _ = cleanup.Value;

    /// <summary>window handleから読むUIAの事実です。provider未準備で読めない間はnullを返し、呼出元は次の観測で判定します。</summary>
    /// <param name="AutomationId">UIAのAutomationIdです。</param>
    /// <param name="IsOffscreen">UIAのIsOffscreenです。</param>
    /// <param name="IsModal">WindowPatternのIsModalです。patternがない場合はfalseです。</param>
    internal sealed record WindowFacts(string AutomationId, bool IsOffscreen, bool IsModal);

    /// <summary>
    /// System.Windows.Automationで観測する既定です。同じprocessでFlaUI(UIA3)を併用すると後者のproperty取得が不安定になるため、
    /// FlaUIで操作するDesktop.E2Eは、同じUIA3接続で観測する関数を<see cref="ForDesktop"/>へ渡します。
    /// </summary>
    private static WindowFacts? DescribeWithManagedUia(nint window)
    {
        var element = AutomationElement.FromHandle(window);
        bool modal = element.TryGetCurrentPattern(WindowPattern.Pattern, out object pattern) && ((WindowPattern)pattern).Current.IsModal;
        return new WindowFacts(element.Current.AutomationId, element.Current.IsOffscreen, modal);
    }

    private static List<nint> Windows(int pid)
    {
        var windows = new List<nint>();
        if (!EnumWindows((window, _) => { GetWindowThreadProcessId(window, out uint owner); if (owner == pid) { windows.Add(window); } return true; }, 0))
        { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return windows;
    }

    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
