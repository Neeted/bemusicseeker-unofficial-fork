namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlayerPanelStateSettingsCompatibilityTests
{
    /// <summary>MSTestが各caseの実行前に注入するcontextから取消を受け取ります。</summary>
    // MSTestのproperty injectionでテストメソッド実行前に非nullが保証されます。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 保証：既存の表示状態名と組合せを実設定ストアで読み保存再読込して同じ状態へ戻し、永続キーと既存enum値の互換性を守る。
    /// </summary>
    /// <remarks>専用設定ファイルと本番providerを使い、生成SettingsとXML保存値の両方で旧形式の受理を確認する。</remarks>
    [TestMethod]
    public Task PlayerPanelState_PreservesPersistedKeyNamesAndValues()
        => WindowsProcessProbeRunner.RunAsync("playback", "PlayerPanelState_PreservesPersistedKeyNamesAndValues", TestContext.CancellationToken);

    /// <summary>
    /// 保証：旧数値の正規化保存が置換ロックで失敗すると元ファイルを保持し、Settingsの読取りにはメモリ内の正規化値を渡す。
    /// </summary>
    /// <remarks>実ファイルの共有モードで置換を失敗させ、原本バイトと本番設定providerの読取りを観測する。</remarks>
    [TestMethod]
    public Task PlayerPanelState_AtomicNormalizationFailurePreservesOriginalAndMaterializesCanonicalValue()
        => WindowsProcessProbeRunner.RunAsync("playback", "PlayerPanelState_AtomicNormalizationFailurePreservesOriginalAndMaterializesCanonicalValue", TestContext.CancellationToken);
}
