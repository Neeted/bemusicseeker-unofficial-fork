namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalPlayerProcessGatewayTests
{
    /// <summary>親の実行取消をprobe待機へ渡します。</summary>
    // MSTestのproperty injectionでテストメソッド実行前に非nullが保証されます。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>実Windows processの終了通知でadapter自身をsenderとして渡し、利用側が別のprocess instanceを参照しません。</summary>
    [TestMethod]
    public Task WindowsProcessSessionPublishesItselfAsExitSender()
        => WindowsProcessProbeRunner.RunAsync("external-player", nameof(WindowsProcessSessionPublishesItselfAsExitSender), TestContext.CancellationToken);

    /// <summary>終了の速い実processでも準備後の購読が起動前に成立し、終了通知を取り逃しません。</summary>
    [TestMethod]
    public Task WindowsGatewaySubscribesBeforeStartingPreparedSession()
        => WindowsProcessProbeRunner.RunAsync("external-player", nameof(WindowsGatewaySubscribesBeforeStartingPreparedSession), TestContext.CancellationToken);
}
