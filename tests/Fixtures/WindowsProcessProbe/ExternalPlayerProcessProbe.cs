using System.Diagnostics;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.WindowsProcessProbe;

/// <summary>親Job内で外部プレイヤーの実起動・終了通知の接続を検証します。</summary>
internal sealed class ExternalPlayerProcessProbe
{
    /// <summary>実Windows processの終了通知でadapter自身をsenderとして渡し、利用側が別のprocess instanceを参照しません。</summary>
    internal void WindowsProcessSessionPublishesItselfAsExitSender()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c exit 0",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        var session = new WindowsExternalPlayerProcessSession(process, started: false);
        using var exited = new ManualResetEventSlim(false);
        object? observedSender = null;
        session.Exited += (sender, _) =>
        {
            observedSender = sender;
            exited.Set();
        };

        session.Start();

        Assert.IsTrue(SpinWait.SpinUntil(
            () => session.HasExited && exited.IsSet,
            TimeSpan.FromSeconds(5)));
        Assert.AreSame(session, observedSender);
    }

    /// <summary>終了の速い実processでも準備後の購読が起動前に成立し、終了通知を取り逃しません。</summary>
    internal void WindowsGatewaySubscribesBeforeStartingPreparedSession()
    {
        string? commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandProcessor))
        {
            throw new InvalidOperationException("The Windows command processor is unavailable.");
        }

        var gateway = new WindowsExternalPlayerProcessGateway();
        IExternalPlayerProcessSession session = gateway.Prepare(
            ExternalPlayerProcessLaunchRequest.Create(
                commandProcessor,
                "/c exit 0",
                ProcessWindowStyle.Hidden));
        bool exitObserved = false;
        session.Exited += (_, _) => exitObserved = true;

        session.Start();

        Assert.IsTrue(SpinWait.SpinUntil(() => session.HasExited, TimeSpan.FromSeconds(5)));
        Assert.IsTrue(SpinWait.SpinUntil(() => exitObserved, TimeSpan.FromSeconds(5)));
    }
}
