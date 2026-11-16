using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.Tests;

/// <summary>待たない通知の投函が、表示できなかった通知を診断のログから追えるように残すことを確認します。</summary>
[TestClass]
public sealed class BmsLibraryDialogServiceLogTests
{
    private const string FailureCauseText = "post-display-failure-cause";

    /// <summary>
    /// 窓口の失敗結果・失敗したTask・同期の例外は元の例外の型と本文を含むログ項目を、予約の拒否は拒否であることが分かる項目を、予約中の例外は例外の型と本文を残す。
    /// いずれも投函した処理へ例外を出さない。表示できなかった通知が、ログからも追えなくなる誤りを検出する。
    /// </summary>
    /// <remarks>共有のログ構成を一時的に差し替えるため、並列実行しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    [DataRow("failed-result")]
    [DataRow("faulted-task")]
    [DataRow("synchronous-throw")]
    [DataRow("rejected")]
    [DataRow("schedule-throws")]
    public async Task Post_FailureShapes_AreTraceableFromLog(string shape)
    {
        await ApplicationLoggingTestScope.RunAsync(loggingScope =>
        {
            string logPath = ConfigureFileLog(loggingScope);
            var cause = new InvalidOperationException(FailureCauseText);
            var dialogs = new FailingDialogs(shape, cause);
            var scheduler = new SwitchableUiScheduler
            {
                Mode = shape switch
                {
                    "rejected" => SwitchableUiSchedulerMode.Reject,
                    "schedule-throws" => SwitchableUiSchedulerMode.Throw,
                    _ => SwitchableUiSchedulerMode.Accept,
                }
            };
            var service = new BmsLibraryDialogService(dialogs, scheduler);

            service.Post(
                [new BMSLibrary.OperationDialogMessage("body", "caption", UiDialogButton.OK, UiDialogIcon.Exclamation, UiDialogDefaultResult.OK)],
                () => false);

            string log = ReadLog(logPath);
            if (shape == "rejected")
            {
                Assert.AreEqual(1, Regex.Count(log, "bms_library_notice_post_rejected"), "拒否の形では、拒否であることが分かる項目が一件ある。");
            }
            else
            {
                StringAssert.Contains(log, nameof(InvalidOperationException), "元の例外の型を含む。");
                StringAssert.Contains(log, shape == "schedule-throws" ? "予約に失敗しました。" : FailureCauseText, "元の例外の本文を含む。");
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 表示の開始の時点で抑止された通知は表示せず、見送りがログに一件残る。
    /// 抑止した通知が、表示もログも残さず消える誤りを検出する。
    /// </summary>
    /// <remarks>共有のログ構成を一時的に差し替えるため、並列実行しない。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public async Task Post_SuppressedAtDisplayStart_IsLoggedOnce()
    {
        await ApplicationLoggingTestScope.RunAsync(loggingScope =>
        {
            string logPath = ConfigureFileLog(loggingScope);
            var dialogs = new FailingDialogs("none", new InvalidOperationException(FailureCauseText));
            var service = new BmsLibraryDialogService(dialogs, new SwitchableUiScheduler());

            service.Post(
                [new BMSLibrary.OperationDialogMessage("body", "caption", UiDialogButton.OK, UiDialogIcon.Exclamation, UiDialogDefaultResult.OK)],
                () => true);

            Assert.AreEqual(0, dialogs.RequestCount);
            Assert.AreEqual(1, Regex.Count(ReadLog(logPath), "bms_library_notice_post_suppressed"));
            return Task.CompletedTask;
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

    /// <summary>指定した形で通知の表示に失敗する表示窓口の記録用実装です。</summary>
    private sealed class FailingDialogs(string shape, Exception cause) : IUiDialogService
    {
        internal int RequestCount { get; private set; }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return shape switch
            {
                "failed-result" => Task.FromResult(UiDialogResult.Failed(cause)),
                "faulted-task" => Task.FromException<UiDialogResult>(cause),
                "synchronous-throw" => throw cause,
                _ => Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)),
            };
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();
        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
