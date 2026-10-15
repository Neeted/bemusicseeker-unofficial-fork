using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NLogWrapperTests
{
    /// <summary>実ログの形式・日本語UTF-8・内例外とスタック・基本環境を確認し、名前付き通常診断と性能の分離、レベル抑制、回転を分担します。</summary>
    [TestMethod]
    public async Task ConfigureApplicationFileLogging_PreservesChannelsEncodingAndRollingArchives()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-NLog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        LoggingConfiguration? originalConfiguration = LogManager.Configuration;
        try
        {
            NLogWrapper.ConfigureApplicationFileLogging(rootPath, LogLevel.Info, enableInstallPerformanceLogging: true);
            NLogWrapper.FileLogger.Info("application-message");
            NLogWrapper.GetLogger("Feature.Diagnostic").Warn("日本語診断");
            Exception failure = CreateFailure();
            NLogWrapper.FileLogger.Error(failure, "original-exception");
            await ApplicationEnvironmentDiagnostics.Queue("main_window_awareness=PerMonitorV2 main_window_dpi=144dpi");
            NLogWrapper.GetLogger("InstallPerformance.Test").Info("performance-message");
            NLogWrapper.FlushBeforeExit(TimeSpan.FromSeconds(5));

            string logPath = Path.Combine(rootPath, "log", "application.log");
            string performanceLogPath = Path.Combine(rootPath, "log", "install-performance.log");
            Assert.IsTrue(File.Exists(logPath));
            Assert.IsTrue(File.Exists(performanceLogPath));
            string applicationLog = ReadSharedText(logPath);
            string performanceLog = ReadSharedText(performanceLogPath);
            Assert.IsTrue(applicationLog.Contains("application-message", StringComparison.Ordinal));
            StringAssert.Matches(applicationLog, new System.Text.RegularExpressions.Regex(@"\d{4}-\d{2}-\d{2} .* \[INFO\] application-message"));
            StringAssert.Contains(applicationLog, "[WARN] 日本語診断");
            StringAssert.Contains(applicationLog, "[ERROR] original-exception");
            StringAssert.Contains(applicationLog, "outer-failure");
            StringAssert.Contains(applicationLog, "inner-failure");
            StringAssert.Contains(applicationLog, nameof(CreateFailure));
            StringAssert.Contains(applicationLog, "[INFO] application_environment");
            StringAssert.Contains(applicationLog, "windows=");
            StringAssert.Contains(applicationLog, "dotnet=");
            StringAssert.Contains(applicationLog, "wpf_file_version=");
            StringAssert.Contains(applicationLog, "process_architecture=");
            StringAssert.Contains(applicationLog, "coordinate_unit=physical_px");
            StringAssert.Contains(applicationLog, "work_px=");
            Assert.AreEqual(1, applicationLog.Split("original-exception").Length - 1, "元例外を二重配送しません。");
            Assert.IsFalse(applicationLog.Contains("performance-message", StringComparison.Ordinal));
            Assert.IsTrue(performanceLog.Contains("performance-message", StringComparison.Ordinal));
            Assert.IsFalse(performanceLog.Contains("application-message", StringComparison.Ordinal));
            Assert.IsFalse(ReadSharedBytes(logPath).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));

            Assert.AreEqual(20L * 1024L * 1024L, NLogWrapper.FileTarget.ArchiveAboveSize);
            Assert.AreEqual(5, NLogWrapper.FileTarget.MaxArchiveFiles);
            Assert.AreEqual(".{0}", NLogWrapper.FileTarget.ArchiveSuffixFormat);
            Assert.AreEqual(logPath, NLogWrapper.FileTarget.FileName.ToString());
            StringAssert.EndsWith(NLogWrapper.FileTarget.ArchiveFileName!.ToString(), Path.Combine("log", "archive", "application.log"));

            NLogWrapper.FileTarget.ArchiveAboveSize = 256;
            for (int index = 0; index < 1000; index++)
            {
                NLogWrapper.FileLogger.Info(new string('x', 256));
            }
            LogManager.Flush();

            string archivePath = Path.Combine(rootPath, "log", "archive");
            string[] archives = Directory.GetFiles(archivePath, "application.*.log");
            Assert.IsTrue(archives.Length > 0, "Rolling should create an application archive.");
            Assert.IsTrue(archives.Length <= 5, "Rolling should retain at most five archives.");

            NLogWrapper.FileTarget.SetMinLogLevel(LogLevel.Error);
            NLogWrapper.FileLogger.Warn("suppressed-file-warning");
            NLogWrapper.GetLogger("Feature.Diagnostic").Warn("suppressed-named-warning");
            NLogWrapper.FileLogger.Error("retained-file-error");
            NLogWrapper.FlushBeforeExit(TimeSpan.FromSeconds(5));
            applicationLog = ReadSharedText(logPath);
            Assert.IsFalse(applicationLog.Contains("suppressed-file-warning", StringComparison.Ordinal));
            Assert.IsFalse(applicationLog.Contains("suppressed-named-warning", StringComparison.Ordinal));
            StringAssert.Contains(applicationLog, "retained-file-error");
        }
        finally
        {
            LogManager.Flush();
            LogManager.Configuration = originalConfiguration;
            TryDeleteDirectory(rootPath);
        }
    }

    /// <summary>保存先を停止しても通常ロガーの呼出元は進み、終了フラッシュも上限後に戻ります。解放後は実ファイルへの保存を回収します。</summary>
    /// <remarks>NLogのプロセス共通構成を交換するため非並列です。ゲートはfinallyで解除し、配送と構成を回収します。</remarks>
    [TestMethod]
    public async Task ApplicationFileOutputGateDoesNotBlockCallerOrBoundedFlush()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-NLog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        LoggingConfiguration? originalConfiguration = LogManager.Configuration;
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task caller = Task.CompletedTask;
        try
        {
            LogManager.Configuration = new LoggingConfiguration();
            var target = new GatedFileTarget(entered, gate)
            {
                Name = "ApplicationFileTarget",
                FileName = Path.Combine(rootPath, "application.log"),
                Layout = NLogWrapper.DefaultLayout
            };
            NLogWrapper.AddApplicationFileTarget(target, LogLevel.Info);
            caller = Task.Run(() => NLogWrapper.FileLogger.Info("queued-before-startup-return"));
            await caller;
            // 標準NLog配送にはTaskがないため、出力先自身の到達通知を使います。
            await entered.Task;
            Assert.IsFalse(gate.IsSet);
            NLogWrapper.FlushBeforeExit(TimeSpan.FromMilliseconds(50));
            Assert.IsFalse(gate.IsSet, "保存中でも上限付きフラッシュから戻ります。");
            gate.Set();
            NLogWrapper.FlushBeforeExit(TimeSpan.FromSeconds(5));
            StringAssert.Contains(ReadSharedText(Path.Combine(rootPath, "application.log")), "queued-before-startup-return");
        }
        finally
        {
            gate.Set();
            await caller;
            NLogWrapper.FlushBeforeExit(TimeSpan.FromSeconds(5));
            LogManager.Configuration = originalConfiguration;
            TryDeleteDirectory(rootPath);
        }
    }

    private static Exception CreateFailure()
    {
        try { throw new InvalidOperationException("outer-failure", new IOException("inner-failure")); }
        catch (Exception exception) { return exception; }
    }

    private sealed class GatedFileTarget(TaskCompletionSource entered, ManualResetEventSlim gate) : FileTarget
    {
        protected override void Write(AsyncLogEventInfo logEvent)
        {
            entered.TrySetResult();
            gate.Wait();
            base.Write(logEvent);
        }

        protected override void Write(IList<AsyncLogEventInfo> logEvents)
        {
            entered.TrySetResult();
            gate.Wait();
            base.Write(logEvents);
        }
    }

    /// <summary>不正なnetwork出力先を登録して実ログ送信し、出力先失敗がアプリの呼出元へ漏れないことを確認します。</summary>
    [TestMethod]
    public void NetworkTargetFailureDoesNotEscapeLoggingCall()
    {
        LoggingConfiguration? originalConfiguration = LogManager.Configuration;
        try
        {
            LogManager.Configuration = new LoggingConfiguration();
            var target = new NetworkTarget
            {
                Address = "http://[invalid"
            };

            NLogWrapper.AddTarget(target, LogLevel.Error, asDefault: false);

            Assert.AreSame(target, NLogWrapper.NetworkTarget);
            Assert.IsTrue(LogManager.Configuration.AllTargets.Contains(target));
            Assert.IsTrue(LogManager.Configuration.LoggingRules.Any(rule => rule.Targets.Contains(target)));
            Assert.IsTrue(InvokeWithoutThrowing(() =>
            {
                NLogWrapper.NetworkLogger.Error("network-message");
                LogManager.Flush();
            }));
        }
        finally
        {
            LogManager.Configuration = originalConfiguration;
        }
    }

    private static bool InvokeWithoutThrowing(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
