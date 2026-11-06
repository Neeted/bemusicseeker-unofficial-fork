using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests.Infrastructure;

/// <summary>直接MTP実行でもケースの専有領域を準備します。</summary>
[TestClass]
public sealed class ModuleLifetime
{
    /// <summary>テストケースの開始前に一回だけ実行領域を確定し、スレッドプールのworker下限を設定します。</summary>
    /// <remarks>
    /// Coreは複数workerで、走査・同期・取込みの実パイプラインを同時に動かします。各パイプラインは段ごとに
    /// プールのスレッドを同期的に占有するため、既定の下限（論理CPU数）では全workerのケースが同時に停滞し、
    /// 新しいスレッドの緩やかな注入を待って数十秒止まります。小規模fixtureで同時に占有される既知の
    /// パイプライン主体7個を基準に、最大論理CPU数の並列と継続処理用の論理CPU数分を確保します。
    /// 7は既知の占有数であり、本番の最大値や厳密な上限ではありません。表明・製品の設定は変えない、
    /// テストプロセス専用の設定です。設定できない場合は停滞を隠さずに失敗させます。
    /// </remarks>
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        _ = TestRunDirectory.RootDirectory;
        ThreadPool.GetMinThreads(out int existingWorkerThreads, out int existingCompletionPortThreads);
        int workerThreads = Math.Max(existingWorkerThreads, 8 * Environment.ProcessorCount);
        if (!ThreadPool.SetMinThreads(workerThreads, existingCompletionPortThreads))
        {
            throw new InvalidOperationException($"テスト用スレッドプールのworker下限を {workerThreads} に設定できませんでした。");
        }
    }

    /// <summary>画面を起動せず、全ケースの終端後に共有設定fixtureの専有領域を回収します。</summary>
    [AssemblyCleanup]
    public static void Cleanup() => TestSettingsFactory.CleanupIsolatedSettingsDirectories();
}
