using System.IO;
using System.Text;
using BeMusicSeeker.TestSupport.Components;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationLoggingTestScopeTests
{
    /// <summary>正常・本体例外後に元構成と規則順・level・target/logger選択を戻し、保持済loggerの実配送を再開します。元targetの再初期化失敗は成功にせず、本体失敗と回収失敗を分離します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ScopeRestoresOriginalDeliveryOrReportsReinitializationFailure(bool failBody, bool failReinitialization)
    {
        await ApplicationLoggingTestScope.RunAsync(async outer =>
        {
            NLogWrapper.ConfigureApplicationFileLogging(outer.RootDirectory, LogLevel.Warn, enableInstallPerformanceLogging: true);
            var network = new NetworkTarget { Name = "original-network", Address = "udp://127.0.0.1:9" };
            NLogWrapper.AddTarget(network, LogLevel.Fatal, asDefault: false);
            LoggingConfiguration originalConfiguration = LogManager.Configuration ?? throw new AssertFailedException("元構成がありません。");
            var initialization = new ReinitializationTarget { Name = "initialization" };
            originalConfiguration.AddRule(LogLevel.Fatal, LogLevel.Fatal, initialization, "initialization-only");
            LogManager.ReconfigExistingLoggers();
            LoggingRule[] rules = originalConfiguration.LoggingRules.ToArray();
            LogLevel[][] levels = rules.Select(rule => rule.Levels.ToArray()).ToArray();
            Target[][] targets = rules.Select(rule => rule.Targets.ToArray()).ToArray();
            FileTarget fileTarget = NLogWrapper.FileTarget;
            Logger fileLogger = NLogWrapper.FileLogger;
            Logger networkLogger = NLogWrapper.NetworkLogger;
            bool? originalThrowConfigExceptions = LogManager.ThrowConfigExceptions;
            var bodyFailure = new InvalidOperationException("body failure");
            Exception? observed = null;
            fileLogger.Warn("before-scope");
            LogManager.Flush();
            try
            {
                await ApplicationLoggingTestScope.RunAsync(inner =>
                {
                    NLogWrapper.ConfigureApplicationFileLogging(inner.RootDirectory, LogLevel.Error, enableInstallPerformanceLogging: false);
                    var replacement = new NetworkTarget { Address = "udp://127.0.0.1:9" };
                    NLogWrapper.AddTarget(replacement, LogLevel.Error, asDefault: false);
                    Assert.AreSame(replacement, NLogWrapper.NetworkTarget);
                    initialization.FailInitialization = failReinitialization;
                    if (failBody) { throw bodyFailure; }
                    return Task.CompletedTask;
                });
            }
            catch (Exception exception) { observed = exception; }
            finally { initialization.FailInitialization = false; }

            Assert.AreEqual(originalThrowConfigExceptions, LogManager.ThrowConfigExceptions);
            Assert.AreSame(fileTarget, NLogWrapper.FileTarget);
            Assert.AreSame(network, NLogWrapper.NetworkTarget);
            Assert.AreSame(fileLogger, NLogWrapper.FileLogger);
            Assert.AreSame(networkLogger, NLogWrapper.NetworkLogger);
            if (failBody)
            {
                Assert.AreSame(bodyFailure, observed);
                Assert.AreEqual(failReinitialization, bodyFailure.Data["LoggingCleanupFailures"] is AggregateException);
            }
            else if (failReinitialization) { Assert.IsInstanceOfType<AggregateException>(observed); }
            else { Assert.IsNull(observed); }

            if (!failReinitialization)
            {
                Assert.AreSame(originalConfiguration, LogManager.Configuration);
                CollectionAssert.AreEqual(rules, originalConfiguration.LoggingRules.ToArray());
                for (int index = 0; index < rules.Length; index++)
                {
                    CollectionAssert.AreEqual(levels[index], rules[index].Levels.ToArray());
                    CollectionAssert.AreEqual(targets[index], rules[index].Targets.ToArray());
                }
                Assert.IsFalse(fileLogger.IsInfoEnabled);
                Assert.IsTrue(fileLogger.IsWarnEnabled);
                fileLogger.Warn("after-scope");
                NLogWrapper.FlushBeforeExit(TimeSpan.FromSeconds(5));
                // 元targetを開いたまま配送再開を観測し、writerの共有条件を妨げません。
                using var stream = new FileStream(Path.Combine(outer.RootDirectory, "application.log"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();
                StringAssert.Contains(text, "before-scope");
                StringAssert.Contains(text, "after-scope");
            }
        });
    }

    private sealed class ReinitializationTarget : Target
    {
        internal bool FailInitialization { get; set; }

        protected override void InitializeTarget()
        {
            if (FailInitialization) { throw new InvalidOperationException("original target initialization failed"); }
            base.InitializeTarget();
        }

        protected override void Write(LogEventInfo logEvent) { }
    }
}
