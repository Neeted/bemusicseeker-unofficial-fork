using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Tests;

/// <summary>旧実配布受入だけの通常起動・実HWND・所有後継・全終端を管理します。</summary>
internal sealed class PublishedApplicationSession
{
    private readonly List<OwnedProcess> processes = [];
    private readonly List<Process> observations = [];
    private readonly List<SingleRequestHttpServer> servers = [];
    private readonly CancellationTokenSource operation;
    private readonly Lazy<ProcessDeadline> cleanup = new(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));

    /// <summary>case全体の期限を一度だけ取り込み、可変媒体と通常childの所有を開始します。</summary>
    internal PublishedApplicationSession(int seconds)
    {
        Deadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromSeconds(seconds));
        operation = new CancellationTokenSource(Deadline.Remaining);
        Workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
    }

    /// <summary>case全段階が共有するsuite/localの早い期限です。</summary>
    internal ProcessDeadline Deadline { get; }
    /// <summary>配布コピー・profile・診断を保持する専有媒体です。</summary>
    internal TestWorkspace Workspace { get; }

    /// <summary>同じ専有子環境の.NET 10 probeで既知フォルダを確認し、不一致では通常appを起動しません。</summary>
    /// <remarks>製品のPathSnapshotも同じGetFolderPathを使います。配布runtimeのbytes同一や既知フォルダAPI内部の解決経路は保証しません。</remarks>
    internal async Task AssertIsolationAsync(ExistingDataAcceptanceFixture fixture)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, @"fixtures\windows-process-probe\WindowsProcessProbe.exe");
        OwnedProcess probe = Start(executable, ["known-folder"], fixture.AppRoot, fixture.ChildEnvironment);
        ProcessCompletion result = await probe.WaitForExitAsync(Deadline);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        JsonElement observation = document.RootElement;
        string expected = fixture.ChildEnvironment["LOCALAPPDATA"] ?? throw new InvalidDataException();
        Assert.IsTrue(string.Equals(expected, observation.GetProperty("LocalApplicationData").GetString(), StringComparison.OrdinalIgnoreCase),
            "通常childのLocalApplicationDataが専有先と一致しません。");
        string runtimeVersion = observation.GetProperty("RuntimeVersion").GetString()
            ?? throw new InvalidDataException("probeの実行runtime版がありません。");
        Assert.AreEqual(10, Version.Parse(runtimeVersion).Major, "隔離前提のprobeには.NET 10が必要です。");
        Console.WriteLine("通常childの既知フォルダ/runtime観測: " + result.StandardOutput);
        File.WriteAllText(Path.Combine(fixture.AppRoot, "known-folder-observation.json"), result.StandardOutput);
    }

    /// <summary>global singletonを維持し、専有NoUpdate入力だけを一般CLIへ渡して通常起動します。</summary>
    internal async Task<(OwnedProcess Owner, Process Process)> StartApplicationAsync(ExistingDataAcceptanceFixture fixture, bool maintenance)
    {
        Process[] existing = Process.GetProcessesByName("BeMusicSeeker");
        try { Assert.AreEqual(0, existing.Length, "既存の利用者appが占有しています。停止せず受入を失敗にします。"); }
        finally { foreach (Process process in existing) { process.Dispose(); } }
        ArchiveLogs(fixture.AppRoot);
        SingleRequestHttpServer server = StartNoUpdateServer();
        OwnedProcess owner = Start(Path.Combine(fixture.AppRoot, "BeMusicSeeker.exe"),
            ["--update-manifest-url=" + server.Address.AbsoluteUri], fixture.AppRoot, fixture.ChildEnvironment);
        var observation = Process.GetProcessById(owner.ProcessId);
        observations.Add(observation);
        _ = observation.Handle;
        Assert.IsTrue(owner.ContainsProcess(observation));
        await WaitReadyAsync(observation, fixture.AppRoot, maintenance);
        // 更新用workを準備する前に、この起動が先に行う旧work回収とNoUpdate応答を完了させます。
        await server.Completion.WaitAsync(operation.Token);
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
            foreach (string blocker in new[] { "startup_setting_validation_failed", "app_schema_preflight_prompt_show", "Startup library initialization failure notification" })
            {
                Assert.IsFalse(logs.Contains(blocker, StringComparison.Ordinal), blocker);
            }
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

    private static nint FindMainWindow(int pid)
    {
        nint found = 0;
        foreach (nint window in Windows(pid))
        {
            if (GetWindow(window, 4) != 0 || !IsWindowVisible(window)) { continue; }
            var element = AutomationElement.FromHandle(window);
            if (element.Current.AutomationId == "BeMusicSeekerMainWindow")
            {
                Assert.AreEqual((nint)0, found, "MainWindowが複数あります。");
                found = window;
            }
        }
        return found;
    }

    private static void AssertNoModal(int pid, nint main)
    {
        foreach (nint window in Windows(pid))
        {
            if (GetWindow(window, 4) != main || !IsWindowVisible(window) || !IsWindowEnabled(window)) { continue; }
            var element = AutomationElement.FromHandle(window);
            if (!element.Current.IsOffscreen && element.TryGetCurrentPattern(WindowPattern.Pattern, out object pattern)
                && ((WindowPattern)pattern).Current.IsModal) { throw new InvalidOperationException("予期しない所有modalがあります。"); }
        }
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
