using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Ui;

namespace BeMusicSeeker.Tests.Infrastructure;

/// <summary>直接実行を含め、画面資源の初期化前から全ケースの終端までsession排他を所有します。</summary>
[TestClass]
public sealed class UiModuleLifetime
{
    private static TestUiSessionLease? lease;

    /// <summary>共有画面資源を作る前に実行領域とsession排他を準備します。</summary>
    [AssemblyInitialize]
    public static async Task Initialize(TestContext context)
    {
        _ = TestRunDirectory.RootDirectory;
        lease = await TestUiSessionLease.AcquireAsync(ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3)));
    }

    /// <summary>全ケースのWindow・子process回収後にsession排他を解放します。</summary>
    [AssemblyCleanup]
    public static void Cleanup() => lease?.Dispose();
}
