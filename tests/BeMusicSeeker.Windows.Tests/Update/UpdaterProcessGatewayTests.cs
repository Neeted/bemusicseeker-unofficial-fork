namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterProcessGatewayTests
{
    /// <summary>親の実行取消をprobe待機へ渡します。</summary>
    // MSTestのproperty injectionでテストメソッド実行前に非nullが保証されます。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>実producerの部分出力を保持している間は完成名を見せず、通常Gatewayを併走させて完成後の受付・内容・排他openでclose済みを確認する。</summary>
    /// <remarks>Gatewayの開始通知は読取りループ内の存在確認の観測には使わない。完成前の実FS境界と通常readerの接続を分担し、最後にreceipt取消と実fixture・両pipe・全Taskを回収する。</remarks>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    public Task WindowsGatewayAcceptsReadyOnlyAfterMarkerPublicationCompletes()
        => WindowsProcessProbeRunner.RunAsync("updater-gateway", nameof(WindowsGatewayAcceptsReadyOnlyAfterMarkerPublicationCompletes), TestContext.CancellationToken);

    /// <summary>実producerを未公開のまま保持すると、通常Gatewayの既存10秒期限で受付が失敗し、stdin待ち実fixtureを停止する。最後にproducerも解放して全資源を回収する。</summary>
    /// <remarks>時計・期限を短縮せず、process停止後のwriter終端は試験所有のgate解放で別に回収する。受付期限の厳密な時刻精度は保証しない。</remarks>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    public Task WindowsGatewayStopsUpdaterWhenReadyPublicationRemainsIncomplete()
        => WindowsProcessProbeRunner.RunAsync("updater-gateway", nameof(WindowsGatewayStopsUpdaterWhenReadyPublicationRemainsIncomplete), TestContext.CancellationToken);

    /// <summary>
    /// 取消決定ファイルを公開できないと元原因を保持して入力待ちの実fixtureを停止し、既存一時file・完成名のdirectoryを保護して失敗tempを残さない。
    /// </summary>
    /// <remarks>既存GUI fixtureをREADY後もstdinで保持し、未releaseの実rootと両pipeの終端を観測する。gateway用handleと所有handleを分けて生成identityを確認し、失敗時も所有processを回収する。</remarks>
    [TestMethod]
    public Task WindowsGatewayAbortStopsUpdaterWhenDecisionPublicationFails()
        => WindowsProcessProbeRunner.RunAsync("updater-gateway", nameof(WindowsGatewayAbortStopsUpdaterWhenDecisionPublicationFails), TestContext.CancellationToken);

    /// <summary>
    /// 続行と取消の決定公開が両方失敗すると両原因を保持して実fixtureを停止し、既存一時file・完成名のdirectoryを保護して両試行の失敗tempを残さない。
    /// </summary>
    /// <remarks>既存GUI fixtureをREADY後もstdinで保持し、未releaseの実rootと両pipeの終端を観測する。gateway用handleと所有handleを分けて生成identityを確認し、失敗時も所有processを回収する。</remarks>
    [TestMethod]
    public Task WindowsGatewayProceedNormalizesDecisionPublicationFailureAndStopsUpdater()
        => WindowsProcessProbeRunner.RunAsync("updater-gateway", nameof(WindowsGatewayProceedNormalizesDecisionPublicationFailureAndStopsUpdater), TestContext.CancellationToken);

    /// <summary>
    /// 指定復旧期限を超えた入力待ちの実fixtureは停止して可視失敗を返し、復旧故障を正常扱いしたりプロセスを残す誤りを検出する。
    /// </summary>
    /// <remarks>既存GUI fixtureをREADY後もstdinで保持し、未releaseの実rootと両pipeの終端を観測する。gateway用handleと所有handleを分けて生成identityを確認し、失敗時も所有processを回収する。</remarks>
    [TestMethod]
    public Task WindowsGatewayRecoveryTerminatesHungProcessAndReportsVisibleFailure()
        => WindowsProcessProbeRunner.RunAsync("updater-gateway", nameof(WindowsGatewayRecoveryTerminatesHungProcessAndReportsVisibleFailure), TestContext.CancellationToken);
}
