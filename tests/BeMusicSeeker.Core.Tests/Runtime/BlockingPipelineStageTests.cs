using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BlockingPipelineStageTests
{
    /// <summary>
    /// ブロッキング段の開始補助は、段をスレッドプール外の専用スレッドで実行し、例外を返したタスクへ保持する。
    /// 補助がスレッドプールで開始する誤りや、段の例外を失う誤りを検出する。各パイプラインがこの補助を使うことは保証しない。
    /// </summary>
    [TestMethod]
    public async Task StagesRunOutsideTheThreadPoolAndKeepFailures()
    {
        bool actionOnPool = true;
        await BlockingPipelineStage.Start(() => { actionOnPool = Thread.CurrentThread.IsThreadPoolThread; });
        Assert.IsFalse(actionOnPool);

        var failure = new InvalidOperationException("stage");
        Task failed = BlockingPipelineStage.Start(() => throw failure);
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failed));
    }
}
