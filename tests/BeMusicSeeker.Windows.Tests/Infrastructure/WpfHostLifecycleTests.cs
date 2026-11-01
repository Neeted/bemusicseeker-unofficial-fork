using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Windows.Tests.Infrastructure;

[TestClass]
public sealed class WpfHostLifecycleTests
{
    /// <summary>共有hostを使った子が生存したままHWND・実STA thread・temp sessionを終端し、release後に全childと両pipeを回収することから、process終了のOS回収で成功を装う誤りを検出します。</summary>
    /// <remarks>親moduleのlease内で同じSupport hostを使います。moduleの順序・例外時の継続回収・lease解放配線は静的確認へ分担します。</remarks>
    [TestMethod]
    public async Task SharedHostShutdown_ReclaimsWindowsThreadAndTemporarySessionBeforeProcessExit()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string probe = Path.Combine(AppContext.BaseDirectory, "fixtures", "windows-process-probe", "WindowsProcessProbe.exe");
        Assert.IsTrue(File.Exists(probe), "同じbuildの共通probeが必要です: " + probe);
        string ready = workspace.GetPath("ready.json");
        string release = workspace.GetPath("release");
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(45));
        var process = OwnedProcess.Start(new ProcessStartRequest(probe,
            ["wpf-host-lifecycle", workspace.GetPath("profile"), ready, release], workspace.RootDirectory));
        Exception? primary = null;
        try
        {
            while (!File.Exists(ready))
            {
                if (process.Completion.IsCompleted)
                {
                    ProcessCompletion early = await process.WaitForExitAsync(deadline);
                    if (File.Exists(ready)) { break; }
                    Assert.Fail($"ready公開前に終了しました。exit={early.ExitCode}, stderr={early.StandardError}");
                }
                TimeSpan remaining = deadline.Remaining;
                if (remaining == TimeSpan.Zero) { throw new TimeoutException("host終端のready公開が期限を超えました。"); }
                await Task.WhenAny(process.Completion,
                    Task.Delay(remaining < TimeSpan.FromMilliseconds(20) ? remaining : TimeSpan.FromMilliseconds(20)));
            }
            Assert.AreNotEqual(TimeSpan.Zero, deadline.Remaining);
            using var result = JsonDocument.Parse(File.ReadAllText(ready));
            JsonElement value = result.RootElement;
            Assert.IsFalse(process.Completion.IsCompleted, "OSのprocess終了前に回収結果を観測します。");
            nint window = (nint)value.GetProperty("WindowHandle").GetInt64();
            nint popup = (nint)value.GetProperty("PopupHandle").GetInt64();
            Assert.AreNotEqual(nint.Zero, window);
            Assert.AreNotEqual(nint.Zero, popup);
            Assert.IsFalse(IsWindow(window));
            Assert.IsFalse(IsWindow(popup));
            Assert.IsFalse(value.GetProperty("DispatcherThreadIsAlive").GetBoolean(), "捕捉した実Threadはjoin済みです。");
            Assert.IsFalse(Directory.Exists(RequiredPath(value, "LeafPath")));
            Assert.IsFalse(Directory.Exists(RequiredPath(value, "SessionPath")));
            Assert.IsFalse(process.Completion.IsCompleted);
            File.WriteAllText(release, "release");
            ProcessCompletion completion = await process.WaitForExitAsync(deadline);
            Assert.AreEqual(0, completion.ExitCode, completion.StandardError);
            Assert.AreEqual(string.Empty, completion.StandardError);
            using var finalResult = JsonDocument.Parse(completion.StandardOutput);
            Assert.AreEqual(value.GetRawText(), finalResult.RootElement.GetRawText());
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            List<Exception> failures = [];
            try
            {
                ProcessStopResult stopped = await process.StopAsync(null, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(15)));
                if (stopped.CleanupFailure is { } failure) { failures.Add(failure); }
            }
            catch (Exception failure) { failures.Add(failure); }
            try { await process.DisposeAsync(); }
            catch (Exception failure) { failures.Add(failure); }
            if (failures.Count != 0)
            {
                var cleanup = new AggregateException("共有host検査のprocess回収に失敗しました。", failures);
                if (primary is null) { throw cleanup; }
                primary.Data["WpfHostLifecycleTests.CleanupFailure"] = cleanup;
                Console.Error.WriteLine(cleanup);
            }
        }

        string RequiredPath(JsonElement value, string name)
        {
            string path = value.GetProperty(name).GetString() ?? throw new AssertFailedException("パスがnullです: " + name);
            Assert.IsTrue(path.StartsWith(workspace.RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            return path;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
}
