using System;
using System.Threading;
using BeMusicSeeker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MSTestSettings
{
    /// <summary>
    /// テスト共通のランタイムとスレッドプール下限を初期化します。
    /// </summary>
    /// <remarks>
    /// 小規模fixtureで同時に占有される既知のパイプライン主体7個を基準に、通常ホストの
    /// 最大ProcessorCount並列と継続処理用のProcessorCount分を確保します。7は小規模fixture
    /// の既知の占有数であり、本番の最大値や厳密な上限ではありません。同期的なテスト待機で
    /// workerの補充が遅れることを避ける、テスト環境専用の設定です。
    /// </remarks>
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        RuntimeBootstrap.Initialize();

        ThreadPool.GetMinThreads(
            out int existingWorkerThreads,
            out int existingCompletionPortThreads);
        int requiredWorkerThreads =
            7 * Environment.ProcessorCount + Environment.ProcessorCount;
        int workerThreads = Math.Max(existingWorkerThreads, requiredWorkerThreads);
        if (!ThreadPool.SetMinThreads(workerThreads, existingCompletionPortThreads))
        {
            throw new InvalidOperationException(
                $"テスト用スレッドプールのworker下限を {workerThreads} に設定できませんでした。");
        }
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        TestUiDispatcherHost.ShutdownApplication();
    }
}
