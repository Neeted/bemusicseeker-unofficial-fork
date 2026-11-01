using System.IO;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TaskLoggingTests
{

    /// <summary>アプリ用の転送でもFaultedの取消型例外を実ログへ出し、真の取消Taskはエラー記録しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task ApplicationLogging_DistinguishesFaultedCancellationExceptionFromCancelledTask(bool generic)
    {
        await ApplicationLoggingTestScope.RunAsync(async scope =>
        {
            string directory = scope.RootDirectory;
            var log = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
            try
            {
                NLogWrapper.ConfigureApplicationFileLogging(Path.Combine(directory, "log"), LogLevel.Info, enableInstallPerformanceLogging: false);
                var configuration = new LoggingConfiguration();
                configuration.AddRule(LogLevel.Error, LogLevel.Fatal, log);
                LogManager.Configuration = configuration;
                var failure = new TaskCanceledException("faulted task must be diagnosed");
                Task<int> source = Task.FromException<int>(failure);
                Task propagated = generic ? source.LoggingAndPropagate() : ((Task)source).LoggingAndPropagate();

                TaskCanceledException observed = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => propagated);
                Assert.AreSame(failure, observed);
                Assert.IsTrue(propagated.IsFaulted);
                LogManager.Flush();
                Assert.AreEqual(1, log.Logs.Count);
                StringAssert.Contains(log.Logs.Single(), failure.Message);

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                Task<int> cancelledSource = Task.FromCanceled<int>(cancellation.Token);
                Task cancelled = generic ? cancelledSource.LoggingAndPropagate() : ((Task)cancelledSource).LoggingAndPropagate();
                TaskCanceledException cancelledException = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);
                Assert.IsTrue(cancelled.IsCanceled);
                Assert.AreEqual(cancellation.Token, cancelledException.CancellationToken);
                LogManager.Flush();
                Assert.AreEqual(1, log.Logs.Count, "CanceledのTaskはエラー記録を増やしません。");
            }
            finally
            {
                LogManager.Flush();

            }
        });
    }
}
