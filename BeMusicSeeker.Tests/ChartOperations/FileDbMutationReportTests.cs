using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FileDbMutationReportTests
{
    /// <summary>情報通知は順次 await し、表示失敗を診断しても後続通知を失わない。</summary>
    [TestMethod]
    public async Task OperationMessages_PreserveOrderAndContinueAfterDisplayFailure()
    {
        var first = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayFailure = new IOException("notification failure marker");
        var failures = new List<Exception>();
        var dialogs = new FileDbReportRecordingDialogs
        {
            MessageHandler = request => request.MessageBoxText == "first"
                ? first.Task
                : Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK))
        };
        Task display = FileDbMutationReport.ShowOperationMessagesAsync(dialogs,
            [new BMSLibrary.OperationDialogMessage("first", "caption", UiDialogButton.OK, UiDialogIcon.Information, UiDialogDefaultResult.OK),
             new BMSLibrary.OperationDialogMessage("second", "caption", UiDialogButton.OK, UiDialogIcon.Information, UiDialogDefaultResult.OK)],
            failures.Add);
        try
        {
            Assert.IsFalse(display.IsCompleted);
            Assert.AreEqual(1, dialogs.Messages.Count, "先行通知が表示中なら後続を重ねない。");
            first.SetException(displayFailure);
            await display.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "first", "second" }, dialogs.Messages.Select(message => message.MessageBoxText).ToArray());
            CollectionAssert.AreEqual(new[] { displayFailure }, failures);
        }
        finally
        {
            first.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            await display.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>成功と no-op は、操作単位でも余分な terminal を表示しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NormalSessionReceiptsAreSilent(bool durable)
    {
        var dialogs = new FileDbReportRecordingDialogs();
        var session = new LibraryMutationSessionReceipt(durable ? [Target("confirmed")] : [], durable);
        await FileDbMutationReport.ShowAsync(dialogs, "normal-operation", session);
        Assert.AreEqual(0, dialogs.Messages.Count);
    }

    /// <summary>durable 成功と required/cleanup failure は独立して報告します。</summary>
    [TestMethod]
    [DataRow(false, false, true, MessageBoxImage.Warning)]
    [DataRow(true, false, false, MessageBoxImage.Error)]
    [DataRow(false, true, true, MessageBoxImage.Error)]
    [DataRow(true, true, true, MessageBoxImage.Error)]
    public void AbnormalSeverityPreservesIndependentFailureDimensions(
        bool applyFails, bool finalizationFails, bool cleanupFails, MessageBoxImage expectedIcon)
    {
        IOException? apply = applyFails ? new IOException("apply-marker") : null;
        IOException? finalization = finalizationFails ? new IOException("finalization-marker") : null;
        IOException? cleanup = cleanupFails ? new IOException("cleanup-marker") : null;
        var session = new LibraryMutationSessionReceipt([Target("confirmed")], durableCommit: true,
            applyFailure: apply, finalizationFailure: finalization, cleanupFailure: cleanup);

        UiMessageRequest report = FileDbMutationReport.Create("selected-operation", session);

        Assert.IsNotNull(report);
        Assert.AreEqual(expectedIcon, report.Icon);
        StringAssert.Contains(report.MessageBoxText, "selected-operation");
        foreach (Exception failure in new[] { apply, finalization, cleanup }.OfType<Exception>())
        {
            StringAssert.Contains(report.MessageBoxText, failure.Message);
        }
        Assert.IsTrue(session.DurableCommit);
        Assert.AreSame(apply, session.ApplyFailure);
        Assert.AreSame(finalization, session.FinalizationFailure);
        Assert.AreSame(cleanup, session.CleanupFailure);
    }

    /// <summary>physical failure が同時に存在すれば、cleanup warning を Error に引き上げます。</summary>
    [TestMethod]
    public void PhysicalFailureAndCleanupRemainVisibleTogether()
    {
        var session = new LibraryMutationSessionReceipt([Target("confirmed")], durableCommit: true,
            physicalFailure: new IOException("physical-marker"), failedTarget: Target("failed"),
            unprocessedTargets: [Target("suffix")], cleanupFailure: new IOException("cleanup-marker"),
            recoveryCandidatePaths: [@"D:\Recovery\backup"], manualRecoveryRequired: true);

        UiMessageRequest report = FileDbMutationReport.Create("mixed-operation", session);

        Assert.AreEqual(MessageBoxImage.Error, report.Icon);
        StringAssert.Contains(report.MessageBoxText, "physical-marker");
        StringAssert.Contains(report.MessageBoxText, "cleanup-marker");
        StringAssert.Contains(report.MessageBoxText, @"D:\Recovery\backup");
    }

    [TestMethod]
    public void RendererBoundsCandidatesErrorsAndWholeBodyAcrossLanguages()
    {
        string[] candidates = Enumerable.Range(0, 7)
            .Select(index => "candidate-" + index + "-" + new string((char)('a' + index), 600)).ToArray();
        var session = new LibraryMutationSessionReceipt([Target("confirmed")], durableCommit: true,
            physicalFailure: new IOException("primary-" + new string('x', 800)),
            finalizationFailure: new IOException("finalizer-" + new string('y', 800)),
            cleanupFailure: new IOException("cleanup-" + new string('z', 800)),
            recoveryCandidatePaths: candidates);
        foreach (string language in Languages)
        {
            var culture = CultureInfo.GetCultureInfo(language);
            UiMessageRequest report = FileDbMutationReport.Create(new string('o', 5000), session, culture: culture);
            Assert.IsTrue(report.MessageBoxText.Length <= 4096, language);
            string[] shownPaths = report.MessageBoxText.Split(Environment.NewLine)
                .Where(line => line.StartsWith("candidate-", StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(3, shownPaths.Length);
            Assert.IsTrue(shownPaths.All(path => path.Length <= 240));
            foreach (string marker in new[] { "primary-", "finalizer-", "cleanup-" })
            {
                string errorLine = report.MessageBoxText.Split(Environment.NewLine)
                    .Single(line => line.Contains(marker, StringComparison.Ordinal));
                Assert.IsTrue(errorLine[errorLine.IndexOf(marker, StringComparison.Ordinal)..].Length <= 400);
            }
            StringAssert.EndsWith(report.MessageBoxText,
                Resources.ResourceManager.GetString(nameof(Resources.FileDbMutationReport_Guidance), culture));
        }
    }

    /// <summary>件数は一操作の confirmed/failed/suffix を表し、per-item の durable receipt 数ではありません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CountsDescribeSessionChangesAndKeepOverlappingFailureDimensions(bool durable)
    {
        var session = new LibraryMutationSessionReceipt(
            [Target("first"), Target("second")], durable,
            physicalFailure: new IOException("physical"), failedTarget: Target("failed"),
            unprocessedTargets: [Target("suffix1"), Target("suffix2"), Target("suffix3")],
            applyFailure: new IOException("apply"),
            finalizationFailure: durable ? new IOException("finalization") : null,
            cleanupFailure: durable ? new IOException("cleanup") : null,
            itemFailures: [new LibraryMutationSessionItemFailure(Target("missing"), new FileNotFoundException("missing"))]);
        // schema の placeholder 番号を使い、翻訳の語順や句読点には依存しません。
        int[] expected = durable ? [2, 2, 2, 2, 1, 3] : [2, 0, 4, 1, 0, 3];
        foreach (string language in Languages)
        {
            var culture = CultureInfo.GetCultureInfo(language);
            string pattern = Regex.Escape(Resources.ResourceManager.GetString(nameof(Resources.LibraryMutationSessionReport_Counts), culture)!);
            for (int index = 0; index < 6; index++)
                pattern = pattern.Replace(Regex.Escape("{" + index + "}"), "(?<field" + index + @">\d+)", StringComparison.Ordinal);
            Match fields = Regex.Match(FileDbMutationReport.Create("counted-operation", session, culture: culture).MessageBoxText, pattern);
            Assert.IsTrue(fields.Success, language);
            for (int index = 0; index < expected.Length; index++)
                Assert.AreEqual(expected[index], int.Parse(fields.Groups["field" + index].Value, CultureInfo.InvariantCulture));
        }
    }

    [TestMethod]
    public void DestinationTypeConflictsRenderWarningWithFiveDetailsAndOmittedCount()
    {
        FileDbMutationDestinationTypeConflict[] conflicts = Enumerable.Range(0, 6)
            .Select(index => new FileDbMutationDestinationTypeConflict(
                @"C:\Source\source" + index, @"D:\Destination\destination" + index,
                expectedIsDirectory: false, existingIsDirectory: true)).ToArray();
        LibraryMutationSessionReceipt session = ConflictSession(conflicts);

        UiMessageRequest report = FileDbMutationReport.Create("install-operation", session);

        Assert.AreEqual(MessageBoxImage.Warning, report.Icon);
        foreach (FileDbMutationDestinationTypeConflict conflict in conflicts.Take(5))
            StringAssert.Contains(report.MessageBoxText, conflict.DestinationPath);
        Assert.IsFalse(report.MessageBoxText.Contains(conflicts[5].DestinationPath, StringComparison.Ordinal));
        StringAssert.Contains(report.MessageBoxText, string.Format(CultureInfo.CurrentCulture,
            Resources.FileDbMutationReport_DestinationTypeConflict_More, 1));
    }

    /// <summary>型衝突は事前拒否で、通常/merge の表示は成功が存在するときだけ成功件数を示します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void DestinationTypeConflictsUseOperationWordingAndOnlyShowConfirmedDurableSuccess(bool merge, bool success)
    {
        LibraryMutationSessionReceipt session = ConflictSession([Conflict()], confirmed: success ? [Target("confirmed")] : []);
        UiMessageRequest report = FileDbMutationReport.Create("operation", session, mergeOperation: merge);

        Assert.AreEqual(MessageBoxImage.Warning, report.Icon);
        if (merge)
        {
            Assert.AreEqual(Resources.FileDbMutationReport_DestinationTypeConflict_MergeTitle, report.Caption);
            StringAssert.Contains(report.MessageBoxText, Resources.FileDbMutationReport_DestinationTypeConflict_MergeReason);
        }
        string successFormat = merge ? Resources.FileDbMutationReport_DestinationTypeConflict_MergeSuccesses
            : Resources.FileDbMutationReport_DestinationTypeConflict_Successes;
        if (success)
            StringAssert.Contains(report.MessageBoxText, string.Format(CultureInfo.CurrentCulture, successFormat, 1));
        else
            Assert.IsFalse(report.MessageBoxText.Contains(string.Format(CultureInfo.CurrentCulture, successFormat, 0), StringComparison.Ordinal));
    }

    /// <summary>型衝突の専用表示でも、別の required failure や cleanup の確認候補を捨てません。</summary>
    [TestMethod]
    [DataRow(false, MessageBoxImage.Warning)]
    [DataRow(true, MessageBoxImage.Error)]
    public void DestinationTypeConflictReportRetainsRecoveryAndFailureDimensions(bool requiredFailure, MessageBoxImage expectedIcon)
    {
        LibraryMutationSessionReceipt session = ConflictSession([Conflict()], [Target("confirmed")],
            physicalFailure: requiredFailure ? new IOException("physical-marker") : null,
            cleanupFailure: new IOException("cleanup-marker"),
            recoveryPaths: [@"D:\Recovery\backup"]);

        UiMessageRequest report = FileDbMutationReport.Create("mixed-operation", session);

        Assert.AreEqual(expectedIcon, report.Icon);
        StringAssert.Contains(report.MessageBoxText, @"D:\Recovery\backup");
        StringAssert.Contains(report.MessageBoxText, "cleanup-marker");
        if (requiredFailure) StringAssert.Contains(report.MessageBoxText, "physical-marker");
        StringAssert.EndsWith(report.MessageBoxText, Resources.FileDbMutationReport_DestinationTypeConflict_Guidance);
    }

    [TestMethod]
    public void ConflictReportBoundsWholeBodyAndRetainsGuidanceAcrossLanguages()
    {
        FileDbMutationDestinationTypeConflict[] conflicts = Enumerable.Range(0, 8).Select(index => new FileDbMutationDestinationTypeConflict(
            @"C:\" + index + new string('s', 600), @"D:\" + index + new string('d', 600), false, true)).ToArray();
        LibraryMutationSessionReceipt session = ConflictSession(conflicts, [Target("confirmed")],
            physicalFailure: new IOException("physical-" + new string('x', 800)),
            cleanupFailure: new IOException("cleanup-" + new string('y', 800)),
            recoveryPaths: Enumerable.Range(0, 8).Select(index => "recovery-" + index + new string('r', 600)));
        foreach (string language in Languages)
        {
            var culture = CultureInfo.GetCultureInfo(language);
            UiMessageRequest report = FileDbMutationReport.Create(new string('o', 5000), session, culture: culture);
            Assert.IsTrue(report.MessageBoxText.Length <= 4096, language);
            StringAssert.EndsWith(report.MessageBoxText,
                Resources.ResourceManager.GetString(nameof(Resources.FileDbMutationReport_DestinationTypeConflict_Guidance), culture));
        }
    }

    [TestMethod]
    public void ExecutorTypeConflictWithoutPreflightFactsRemainsError()
    {
        var failure = new FileDbMutationDestinationTypeConflictException(Conflict());
        var session = new LibraryMutationSessionReceipt([], durableCommit: false,
            physicalFailure: failure, failedTarget: Target("failed"));
        UiMessageRequest report = FileDbMutationReport.Create("executor-operation", session);
        Assert.AreEqual(MessageBoxImage.Error, report.Icon);
        StringAssert.Contains(report.MessageBoxText, failure.Message);
    }

    [TestMethod]
    public async Task ReporterFailureDoesNotAlterFactsOrRetry()
    {
        var finalization = new IOException("finalization-marker");
        var cleanup = new IOException("cleanup-marker");
        var session = new LibraryMutationSessionReceipt([Target("confirmed")], durableCommit: true,
            finalizationFailure: finalization, cleanupFailure: cleanup);
        var dialogs = new FileDbReportRecordingDialogs { MessageFailure = new IOException("report failed") };

        await FileDbMutationReport.ShowAsync(dialogs, "failing-report", session);

        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.IsTrue(session.DurableCommit);
        Assert.AreSame(finalization, session.FinalizationFailure);
        Assert.AreSame(cleanup, session.CleanupFailure);
    }

    private static readonly string[] Languages = ["ja-JP", "en-US", "fr-FR", "ko-KR", "zh-CN", "zh-TW"];

    private static LibraryMutationSessionTarget Target(string name) => new(@"C:\Source\" + name, @"D:\Destination\" + name);

    private static FileDbMutationDestinationTypeConflict Conflict() => new(
        @"C:\Source\BGA", @"D:\Destination\BGA", expectedIsDirectory: false, existingIsDirectory: true);

    private static LibraryMutationSessionReceipt ConflictSession(
        IEnumerable<FileDbMutationDestinationTypeConflict> conflicts,
        LibraryMutationSessionTarget[]? confirmed = null,
        Exception? physicalFailure = null,
        Exception? cleanupFailure = null,
        IEnumerable<string>? recoveryPaths = null)
    {
        FileDbMutationDestinationTypeConflict[] conflictArray = conflicts.ToArray();
        return new LibraryMutationSessionReceipt(confirmed ?? [], durableCommit: confirmed?.Length > 0,
            physicalFailure: physicalFailure, cleanupFailure: cleanupFailure,
            recoveryCandidatePaths: recoveryPaths,
            itemFailures: [new LibraryMutationSessionItemFailure(Target("refused"),
                new FileDbMutationDestinationTypeConflictException(conflictArray[0]), conflictArray)],
            destinationTypeConflicts: conflictArray);
    }
}

/// <summary>Local terminal/model dialog recorder shared by the report and real folder-rename route tests.</summary>
internal sealed class FileDbReportRecordingDialogs : IUiDialogService, IBmsLibraryDialogService
{
    internal List<UiMessageRequest> Messages { get; } = [];
    internal int ModelMessages { get; private set; }
    internal Action? OnMessage { get; set; }
    internal Exception? MessageFailure { get; set; }
    internal Func<UiMessageRequest, Task<UiDialogResult>>? MessageHandler { get; set; }

    public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
    {
        Messages.Add(request);
        OnMessage?.Invoke();
        if (MessageFailure != null) throw MessageFailure;
        if (MessageHandler != null) return MessageHandler(request);
        return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
    }

    public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon,
        UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
    {
        ModelMessages++;
        return UiDialogDefaultResult.OK;
    }

    public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
    public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request,
        CancellationToken cancellationToken = default) where TWindow : Window => throw new NotSupportedException();
    public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
