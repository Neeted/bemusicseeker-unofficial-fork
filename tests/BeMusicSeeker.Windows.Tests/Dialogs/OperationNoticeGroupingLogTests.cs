using System.IO;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using Ribbit.Logging;

namespace BeMusicSeeker.Tests;

/// <summary>まとめた表示で標本に載らない通知が、全件のどこかのログに必ず残ることを確認します。</summary>
[TestClass]
public sealed class OperationNoticeGroupingLogTests
{
    private const string OmittedTag = "operation_notice_group_omitted";

    /// <summary>
    /// 標本（5件）に載らない件が出る組は、組ごとに一つのログ項目へ全メンバーの本文を残す。
    /// 5件以下の組はログを増やさない。載らない件のログ漏れ、一件ずつのログ、不要なログを検出する。
    /// </summary>
    /// <remarks>共有のログ構成を一時的に差し替えるため、並列実行しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task OperationMessages_GroupWithOmittedMembers_LogsEveryMemberInOneEventPerGroup()
    {
        await ApplicationLoggingTestScope.RunAsync(async loggingScope =>
        {
            string logPath = ConfigureFileLog(loggingScope);
            var dialogs = new FileDbReportRecordingDialogs();
            BMSLibrary.OperationDialogMessage Message(string text, string caption) =>
                new(text, caption, UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK);

            await FileDbMutationReport.ShowOperationMessagesAsync(dialogs,
            [
                .. Enumerable.Range(0, 7).Select(index => Message("big-group-member-" + index, "big")),
                .. Enumerable.Range(0, 5).Select(index => Message("small-group-member-" + index, "small")),
            ]);

            Assert.AreEqual(2, dialogs.Messages.Count, "表示は組ごとに一件。");
            string log = ReadLog(logPath);
            Assert.AreEqual(1, Regex.Count(log, OmittedTag), "載らない件が出る組だけ、組ごとに一つのログ項目。");
            foreach (int index in Enumerable.Range(0, 7))
            {
                StringAssert.Contains(log, "big-group-member-" + index);
            }
            Assert.IsFalse(log.Contains("small-group-member-", StringComparison.Ordinal), "全件が表示される組はログへ重ねない。");
        });
    }

    /// <summary>
    /// 読込み失敗の組は収集した時点で全件をログへ残しているため、まとめる時点で重ねて残さない。
    /// 同じ組へ入った別の通知だけを、まとめる時点のログに残す。全件がどちらかのログに残ることを確認し、二重のログを検出する。
    /// </summary>
    /// <remarks>共有のログ構成を一時的に差し替えるため、並列実行しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task OperationMessages_ReadFailureGroupIsNotLoggedTwiceButOtherMembersAre()
    {
        await ApplicationLoggingTestScope.RunAsync(async loggingScope =>
        {
            string logPath = ConfigureFileLog(loggingScope);
            var collector = new MaintenanceReadFailureCollector();
            foreach (int index in Enumerable.Range(0, 7))
            {
                collector.Add(index, @"C:\Songs\read-failure-" + index + ".bms", new IOException("cause-" + index));
            }
            collector.FlushLog();
            BMSLibrary.OperationDialogMessage readFailures = MaintenanceReadFailureNotice.Create(collector)
                ?? throw new AssertFailedException("読込み失敗の通知が作られる。");
            var dialogs = new FileDbReportRecordingDialogs();

            await FileDbMutationReport.ShowOperationMessagesAsync(dialogs,
            [
                readFailures,
                new BMSLibrary.OperationDialogMessage("other-error-A", readFailures.Caption, UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK),
                new BMSLibrary.OperationDialogMessage("other-error-B", readFailures.Caption, UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK),
            ]);

            Assert.AreEqual(1, dialogs.Messages.Count);
            string log = ReadLog(logPath);
            Assert.AreEqual(1, Regex.Count(log, "maintenance_read_failed"), "読込み失敗は収集時の一つのログ項目。");
            foreach (int index in Enumerable.Range(0, 7))
            {
                StringAssert.Contains(log, "read-failure-" + index + ".bms");
            }
            Assert.AreEqual(1, Regex.Count(log, OmittedTag), "まとめる時点では、読込み失敗以外の通知だけを組ごとに一つのログ項目へ残す。");
            StringAssert.Contains(log, "other-error-A");
            StringAssert.Contains(log, "other-error-B");
            Assert.AreEqual(1, Regex.Count(log, "read-failure-0.bms"), "読込み失敗のパスを二重に残さない。");
        });
    }

    private static string ConfigureFileLog(ApplicationLoggingTestScope loggingScope)
    {
        string logPath = Path.Combine(loggingScope.RootDirectory, "application.log");
        NLog.LogManager.Configuration = new NLog.Config.LoggingConfiguration();
        NLogWrapper.AddApplicationFileTarget(new NLog.Targets.FileTarget
        {
            Name = "ApplicationFileTarget",
            FileName = logPath,
            Layout = NLogWrapper.DefaultLayout
        }, NLog.LogLevel.Info);
        return logPath;
    }

    private static string ReadLog(string logPath)
    {
        NLogWrapper.FlushBeforeExit(TimeSpan.FromSeconds(5));
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
