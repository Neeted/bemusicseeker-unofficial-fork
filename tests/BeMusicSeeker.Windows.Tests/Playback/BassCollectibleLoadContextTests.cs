namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassCollectibleLoadContextTests
{
    /// <summary>MSTestが各caseの実行前に注入するcontextから取消を受け取ります。</summary>
    // MSTestのproperty injectionでテストメソッド実行前に非nullが保証されます。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>player型の静的初期化だけではnative読込みと機器列挙を開始せず、専用collectible contextの解放を妨げない。</summary>
    [TestMethod]
    public Task BassAudioPlayer_StaticInitializationDoesNotEnumerateOrLoadNativeRuntime()
        => FreshPlaybackProbe.RunAsync("BassAudioPlayer_StaticInitializationDoesNotEnumerateOrLoadNativeRuntime", TestContext.CancellationToken);
}
