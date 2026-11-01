using System.Diagnostics;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Ui;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>実Windowを生成せず、異なるprocess間のsession排他とOS回収を保証します。</summary>
[TestClass]
public sealed class TestUiSessionLeaseTests
{
    /// <summary>別processの保持中は取得できず、owner死亡後は残るfileを再利用して取得できることを保証します。</summary>
    [TestMethod]
    public async Task OwnerDeathReleasesHandleWithoutDeletingSharedFile()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string ready = workspace.GetPath("ready"), release = workspace.GetPath("release");
        string leasePath = workspace.GetPath("session.lock");
        string fixture = Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe");
        await using (var identity = OwnedProcess.Start(new ProcessStartRequest(fixture, ["lease-path"], workspace.RootDirectory)))
        {
            Assert.AreEqual(TestUiSessionLease.SessionPath, (await identity.WaitForExitAsync(ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)))).StandardOutput);
        }
        await using var owner = OwnedProcess.Start(new ProcessStartRequest(fixture, ["lease", ready, release, leasePath], AppContext.BaseDirectory));
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));
        while (!File.Exists(ready))
        {
            if (deadline.Remaining == TimeSpan.Zero)
            {
                throw new TimeoutException("lease ownerが準備を完了しません。");
            }

            await Task.Delay(10);
        }
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            using TestUiSessionLease unexpected = await TestUiSessionLease.AcquireFileAsync(leasePath, ProcessDeadline.FromTimeout(TimeSpan.FromMilliseconds(100)));
        });
        using (var process = Process.GetProcessById(owner.ProcessId))
        {
            process.Kill();
        }

        await owner.WaitForExitAsync(ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            using TestUiSessionLease unexpected = await TestUiSessionLease.AcquireFileAsync(leasePath, ProcessDeadline.FromTimeout(TimeSpan.Zero));
        });
        using (TestUiSessionLease next = await TestUiSessionLease.AcquireFileAsync(leasePath, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)))) { }
        Assert.IsTrue(File.Exists(leasePath));
        using TestUiSessionLease again = await TestUiSessionLease.AcquireFileAsync(leasePath, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
    }
}
