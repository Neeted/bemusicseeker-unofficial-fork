using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using Ribbit.Logging;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NLogWrapperTests
{
    [TestMethod]
    public void ConfigureApplicationFileLogging_PreservesChannelsEncodingAndRollingArchives()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-NLog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        LoggingConfiguration? originalConfiguration = LogManager.Configuration;
        try
        {
            NLogWrapper.ConfigureApplicationFileLogging(rootPath, LogLevel.Info, enableInstallPerformanceLogging: true);
            NLogWrapper.FileLogger.Info("application-message");
            NLogWrapper.GetLogger("InstallPerformance.Test").Info("performance-message");
            LogManager.Flush();

            string logPath = Path.Combine(rootPath, "log", "application.log");
            string performanceLogPath = Path.Combine(rootPath, "log", "install-performance.log");
            Assert.IsTrue(File.Exists(logPath));
            Assert.IsTrue(File.Exists(performanceLogPath));
            string applicationLog = ReadSharedText(logPath);
            string performanceLog = ReadSharedText(performanceLogPath);
            Assert.IsTrue(applicationLog.Contains("application-message", StringComparison.Ordinal));
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
        }
        finally
        {
            LogManager.Flush();
            LogManager.Configuration = originalConfiguration;
            TryDeleteDirectory(rootPath);
        }
    }

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
