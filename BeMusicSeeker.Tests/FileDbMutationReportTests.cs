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
    [TestMethod]
    public async Task NormalReceiptsAreSilent()
    {
        var dialogs = new FileDbReportRecordingDialogs();
        await FileDbMutationReport.ShowAsync(dialogs, "normal-operation",
            new FileDbMutationBatchReceipt([Receipt(FileDbMutationTerminalState.Completed)]));
        Assert.AreEqual(0, dialogs.Messages.Count);
    }

    [TestMethod]
    [DataRow((int)FileDbMutationTerminalState.CompletedWithCleanupFailure, MessageBoxImage.Warning)]
    [DataRow((int)FileDbMutationTerminalState.Failed, MessageBoxImage.Error)]
    [DataRow((int)FileDbMutationTerminalState.ManualRecoveryRequired, MessageBoxImage.Error)]
    [DataRow((int)FileDbMutationTerminalState.DurableFinalizationFailed, MessageBoxImage.Error)]
    public void AbnormalSeverityPreservesDurableAndIndependentFailureDimensions(
        int terminalState, MessageBoxImage expectedIcon)
    {
        var state = (FileDbMutationTerminalState)terminalState;
        FileDbMutationReceipt receipt = Receipt(state);
        var batch = new FileDbMutationBatchReceipt([Receipt(FileDbMutationTerminalState.Completed), receipt]);
        UiMessageRequest report = FileDbMutationReport.Create("selected-operation", batch);
        Assert.IsNotNull(report);
        Assert.AreEqual(expectedIcon, report.Icon);
        StringAssert.Contains(report.MessageBoxText, "selected-operation");
        StringAssert.Contains(report.MessageBoxText, receipt.Failure!.Message);
        if (state == FileDbMutationTerminalState.DurableFinalizationFailed)
            StringAssert.Contains(report.MessageBoxText, receipt.CleanupFailure!.Message);
        Assert.IsTrue(batch.HasDurableCommit);
        Assert.AreSame(receipt, batch.Receipts[1]);
    }

    [TestMethod]
    public void MixedReceiptSeverityUsesWorstOutcomeInEitherOrder()
    {
        var cleanup = Receipt(FileDbMutationTerminalState.CompletedWithCleanupFailure);
        var failure = Receipt(FileDbMutationTerminalState.ManualRecoveryRequired);
        foreach (FileDbMutationReceipt[] receipts in new[] { new[] { cleanup, failure }, new[] { failure, cleanup } })
        {
            UiMessageRequest report = FileDbMutationReport.Create("mixed-operation", new FileDbMutationBatchReceipt(receipts));
            Assert.AreEqual(MessageBoxImage.Error, report.Icon);
            StringAssert.Contains(report.MessageBoxText, cleanup.CleanupFailure!.Message);
            StringAssert.Contains(report.MessageBoxText, failure.Failure!.Message);
        }
    }

    [TestMethod]
    public void RendererBoundsCandidatesErrorsAndWholeBodyAcrossLanguages()
    {
        string[] candidates = Enumerable.Range(0, 7).Select(index => "candidate-" + index + "-" + new string((char)('a' + index), 600)).ToArray();
        var receipt = new FileDbMutationReceipt(Guid.NewGuid(), FileDbMutationTerminalState.DurableFinalizationFailed,
            true, 0, 1, [], [], [], [], candidates,
            new IOException("primary-" + new string('x', 800)),
            new IOException("finalizer-" + new string('y', 800)),
            new IOException("cleanup-" + new string('z', 800)));
        foreach (string language in new[] { "ja-JP", "en-US", "fr-FR", "ko-KR", "zh-CN", "zh-TW" })
        {
            UiMessageRequest report = FileDbMutationReport.Create(new string('o', 5000),
                new FileDbMutationBatchReceipt([receipt]), culture: CultureInfo.GetCultureInfo(language));
            Assert.IsTrue(report.MessageBoxText.Length <= 4096, language);
            string[] shownPaths = report.MessageBoxText.Split(Environment.NewLine)
                .Where(line => line.StartsWith("candidate-", StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(3, shownPaths.Length);
            Assert.IsTrue(shownPaths.All(path => path.Length <= 240));
            foreach (string marker in new[] { "primary-", "finalizer-", "cleanup-" })
            {
                string errorLine = report.MessageBoxText.Split(Environment.NewLine).Single(line => line.Contains(marker, StringComparison.Ordinal));
                Assert.IsTrue(errorLine[errorLine.IndexOf(marker, StringComparison.Ordinal)..].Length <= 400);
            }
        }
    }

    [TestMethod]
    public void CountsDescribeRecordedOperationsAndKeepOverlappingFailureDimensions()
    {
        // Placeholder indices are the formatter's schema, not localized copy.
        // Decode those fields so translation order/punctuation can vary freely.
        // The expected values come from the five operations in this scenario.
        var batch = new FileDbMutationBatchReceipt([
            new FileDbMutationReceipt(Guid.NewGuid(), FileDbMutationTerminalState.Completed, true, 0, 0,
                Enumerable.Range(0, 8).Select(index => @"C:\Source\" + index), [@"D:\Destination"], [], [], []),
            Receipt(FileDbMutationTerminalState.Completed),
            Receipt(FileDbMutationTerminalState.CompletedWithCleanupFailure),
            Receipt(FileDbMutationTerminalState.DurableFinalizationFailed),
            Receipt(FileDbMutationTerminalState.ManualRecoveryRequired)
        ]);
        foreach (string language in new[] { "ja-JP", "en-US", "fr-FR", "ko-KR", "zh-CN", "zh-TW" })
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(language);
            string pattern = Regex.Escape(Resources.ResourceManager.GetString(nameof(Resources.FileDbMutationReport_Counts), culture)!);
            for (int index = 0; index < 6; index++)
                pattern = pattern.Replace(Regex.Escape("{" + index + "}"), "(?<field" + index + @">\d+)", StringComparison.Ordinal);
            Match fields = Regex.Match(FileDbMutationReport.Create("counted-operation", batch, culture: culture).MessageBoxText, pattern);
            Assert.IsTrue(fields.Success, language);
            int[] expected = [5, 4, 1, 1, 2, 1];
            for (int index = 0; index < expected.Length; index++)
                Assert.AreEqual(expected[index], int.Parse(fields.Groups["field" + index].Value, CultureInfo.InvariantCulture));
        }
    }

    [TestMethod]
    public void DestinationTypeConflictsRenderWarningWithFiveDetailsAndOmittedCount()
    {
        FileDbMutationDestinationTypeConflict[] conflicts = Enumerable.Range(0, 6)
            .Select(index => new FileDbMutationDestinationTypeConflict(
                @"C:\Source\source" + index,
                @"D:\Destination\destination" + index,
                expectedIsDirectory: false,
                existingIsDirectory: true))
            .ToArray();
        var receipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            [@"C:\Source\package"],
            [@"D:\Destination\package"],
            [],
            [],
            [],
            new FileDbMutationDestinationTypeConflictException(conflicts[0]),
            destinationTypeConflicts: conflicts);

        UiMessageRequest report = FileDbMutationReport.Create(
            "install-operation",
            new FileDbMutationBatchReceipt([receipt]));

        Assert.IsNotNull(report);
        Assert.AreEqual(MessageBoxImage.Warning, report.Icon);
        Assert.AreEqual(Resources.FileDbMutationReport_Title, report.Caption);
        StringAssert.Contains(report.MessageBoxText, @"C:\Source\package");
        StringAssert.Contains(report.MessageBoxText, @"D:\Destination\package");
        foreach (FileDbMutationDestinationTypeConflict conflict in conflicts.Take(5))
            StringAssert.Contains(report.MessageBoxText, conflict.DestinationPath);
        Assert.IsFalse(report.MessageBoxText.Contains(conflicts[5].DestinationPath, StringComparison.Ordinal));
        string omittedText = string.Format(
            CultureInfo.CurrentCulture,
            Resources.FileDbMutationReport_DestinationTypeConflict_More,
            1);
        StringAssert.Contains(report.MessageBoxText, omittedText);
        Assert.IsTrue(report.MessageBoxText.Length <= 4096);
    }

    [TestMethod]
    public void DestinationTypeConflictReportRetainsCleanupFailureAsWarning()
    {
        var conflict = new FileDbMutationDestinationTypeConflict(
            @"C:\Source\BGA",
            @"D:\Destination\BGA",
            expectedIsDirectory: false,
            existingIsDirectory: true);
        var conflictReceipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            [@"C:\Source\package"],
            [@"D:\Destination\package"],
            [],
            [],
            [],
            new FileDbMutationDestinationTypeConflictException(conflict),
            destinationTypeConflicts: [conflict]);
        FileDbMutationReceipt cleanupReceipt = Receipt(FileDbMutationTerminalState.CompletedWithCleanupFailure);

        UiMessageRequest report = FileDbMutationReport.Create(
            "mixed-cleanup-operation",
            new FileDbMutationBatchReceipt([conflictReceipt, cleanupReceipt]));

        Assert.AreEqual(MessageBoxImage.Warning, report.Icon);
        string cleanupText = string.Format(
            CultureInfo.CurrentCulture,
            Resources.FileDbMutationReport_DestinationTypeConflict_Cleanup,
            1);
        StringAssert.Contains(report.MessageBoxText, cleanupText);
        StringAssert.Contains(report.MessageBoxText, cleanupReceipt.CleanupFailure!.Message);
        foreach (string path in cleanupReceipt.RecoveryPaths)
            StringAssert.Contains(report.MessageBoxText, path);
    }

    [TestMethod]
    public void DestinationTypeConflictReportKeepsFiveDetailsAndRecoverySummaryWithinBudget()
    {
        FileDbMutationDestinationTypeConflict[] conflicts = Enumerable.Range(0, 6)
            .Select(index => new FileDbMutationDestinationTypeConflict(
                @"C:\Source\package-" + index + new string('s', 800),
                @"D:\Destination\package-" + index + new string('d', 800),
                expectedIsDirectory: false,
                existingIsDirectory: true))
            .ToArray();
        var conflictReceipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            [@"C:\Source\package-root-" + new string('p', 800)],
            [@"D:\Destination\package-root-" + new string('q', 800)],
            [],
            [],
            [],
            new FileDbMutationDestinationTypeConflictException(conflicts[0]),
            destinationTypeConflicts: conflicts);
        const string recoveryPath = @"D:\Recovery\manual-";
        FileDbMutationReceipt recoveryReceipt = new(
            Guid.NewGuid(),
            FileDbMutationTerminalState.ManualRecoveryRequired,
            durableCommit: false,
            compensationAttemptCount: 1,
            cleanupAttemptCount: 0,
            [@"C:\Source\manual"],
            [@"D:\Destination\manual"],
            [],
            [],
            [recoveryPath + new string('r', 800)],
            new IOException("long-manual-recovery-marker"));

        UiMessageRequest report = FileDbMutationReport.Create(
            "long-mixed-operation",
            new FileDbMutationBatchReceipt([conflictReceipt, recoveryReceipt]));

        Assert.AreEqual(MessageBoxImage.Error, report.Icon);
        Assert.IsTrue(report.MessageBoxText.Length <= 4096);
        foreach (FileDbMutationDestinationTypeConflict conflict in conflicts.Take(5))
        {
            string sourcePrefix = conflict.SourcePath[..Math.Min(conflict.SourcePath.Length, 60)];
            StringAssert.Contains(report.MessageBoxText, sourcePrefix);
        }
        Assert.IsFalse(report.MessageBoxText.Contains(conflicts[5].SourcePath, StringComparison.Ordinal));
        StringAssert.Contains(
            report.MessageBoxText,
            string.Format(
                CultureInfo.CurrentCulture,
                Resources.FileDbMutationReport_DestinationTypeConflict_More,
                1));
        StringAssert.Contains(report.MessageBoxText, recoveryPath);
        StringAssert.Contains(report.MessageBoxText, "long-manual-recovery-marker");
        StringAssert.Contains(
            report.MessageBoxText,
            Resources.FileDbMutationReport_DestinationTypeConflict_Guidance);
    }

    [TestMethod]
    public void DestinationTypeConflictsUseMergeTerminalWordingWhenRequested()
    {
        var conflict = new FileDbMutationDestinationTypeConflict(
            @"C:\Source\source",
            @"D:\Destination\destination",
            expectedIsDirectory: false,
            existingIsDirectory: true);
        var receipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            [@"C:\Source\package"],
            [@"D:\Destination\package"],
            [],
            [],
            [],
            new FileDbMutationDestinationTypeConflictException(conflict),
            destinationTypeConflicts: [conflict]);

        UiMessageRequest report = FileDbMutationReport.Create(
            Resources.FileDbMutationReport_Merge,
            new FileDbMutationBatchReceipt([receipt]),
            mergeOperation: true);

        Assert.AreEqual(MessageBoxImage.Warning, report.Icon);
        Assert.AreEqual(Resources.FileDbMutationReport_DestinationTypeConflict_MergeTitle, report.Caption);
        StringAssert.Contains(
            report.MessageBoxText,
            Resources.FileDbMutationReport_DestinationTypeConflict_MergeReason);
        string zeroSuccessText = string.Format(
            CultureInfo.CurrentCulture,
            Resources.FileDbMutationReport_DestinationTypeConflict_MergeSuccesses,
            0);
        Assert.IsFalse(report.MessageBoxText.Contains(zeroSuccessText, StringComparison.Ordinal));
    }

    [TestMethod]
    public void DestinationTypeConflictsShowSuccessfulCountOnlyWhenPresent()
    {
        var conflict = new FileDbMutationDestinationTypeConflict(
            @"C:\Source\source",
            @"D:\Destination\destination",
            expectedIsDirectory: false,
            existingIsDirectory: true);
        var conflictReceipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            [@"C:\Source\package"],
            [@"D:\Destination\package"],
            [],
            [],
            [],
            new FileDbMutationDestinationTypeConflictException(conflict),
            destinationTypeConflicts: [conflict]);

        UiMessageRequest report = FileDbMutationReport.Create(
            "install-operation",
            new FileDbMutationBatchReceipt([
                conflictReceipt,
                Receipt(FileDbMutationTerminalState.Completed)
            ]));

        string successText = string.Format(
            CultureInfo.CurrentCulture,
            Resources.FileDbMutationReport_DestinationTypeConflict_Successes,
            1);
        StringAssert.Contains(report.MessageBoxText, successText);
    }

    [TestMethod]
    public void DestinationTypeConflictReportRetainsRecoveryPathsForMixedManualRecovery()
    {
        var conflict = new FileDbMutationDestinationTypeConflict(
            @"C:\Source\source",
            @"D:\Destination\destination",
            expectedIsDirectory: false,
            existingIsDirectory: true);
        var conflictReceipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            [@"C:\Source\package"],
            [@"D:\Destination\package"],
            [],
            [],
            [],
            new FileDbMutationDestinationTypeConflictException(conflict),
            destinationTypeConflicts: [conflict]);
        const string recoveryPath = @"D:\Recovery\first-backup";
        FileDbMutationReceipt recoveryReceipt = new(
            Guid.NewGuid(),
            FileDbMutationTerminalState.ManualRecoveryRequired,
            durableCommit: false,
            compensationAttemptCount: 1,
            cleanupAttemptCount: 0,
            [@"C:\Source\other"],
            [@"D:\Destination\other"],
            [],
            [],
            [recoveryPath],
            new IOException("manual-recovery-marker"));

        UiMessageRequest report = FileDbMutationReport.Create(
            "mixed-operation",
            new FileDbMutationBatchReceipt([conflictReceipt, recoveryReceipt]));

        Assert.AreEqual(MessageBoxImage.Error, report.Icon);
        StringAssert.Contains(report.MessageBoxText, recoveryPath);
        StringAssert.Contains(report.MessageBoxText, "manual-recovery-marker");
    }

    [TestMethod]
    public void ExecutorTypeConflictWithoutPreflightFactsRemainsError()
    {
        var conflict = new FileDbMutationDestinationTypeConflict(
            @"C:\source\BGA",
            @"D:\destination\BGA",
            expectedIsDirectory: false,
            existingIsDirectory: true);
        var receipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.Failed,
            durableCommit: false,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            [conflict.SourcePath],
            [conflict.DestinationPath],
            [],
            [],
            [],
            new FileDbMutationDestinationTypeConflictException(conflict));

        UiMessageRequest report = FileDbMutationReport.Create(
            "executor-operation",
            new FileDbMutationBatchReceipt([receipt]));

        Assert.IsNotNull(report);
        Assert.AreEqual(MessageBoxImage.Error, report.Icon);
        StringAssert.Contains(report.MessageBoxText, receipt.Failure!.Message);
    }

    [TestMethod]
    public async Task ReporterFailureDoesNotAlterFactsOrRetry()
    {
        var receipt = Receipt(FileDbMutationTerminalState.DurableFinalizationFailed);
        var batch = new FileDbMutationBatchReceipt([receipt]);
        var dialogs = new FileDbReportRecordingDialogs { MessageFailure = new IOException("report failed") };
        await FileDbMutationReport.ShowAsync(dialogs, "failing-report", batch);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreSame(receipt, batch.Receipts[0]);
        Assert.IsTrue(receipt.DurableCommit);
        Assert.IsNotNull(receipt.FinalizationFailure);
        Assert.IsNotNull(receipt.CleanupFailure);
    }

    /// <summary>Builds the packet's reachable terminal facts for local owner/report adapters.</summary>
    internal static FileDbMutationReceipt Receipt(FileDbMutationTerminalState state)
    {
        bool durable = state is FileDbMutationTerminalState.Completed or FileDbMutationTerminalState.CompletedWithCleanupFailure
            or FileDbMutationTerminalState.DurableFinalizationFailed;
        Exception? cleanup = state is FileDbMutationTerminalState.CompletedWithCleanupFailure or FileDbMutationTerminalState.DurableFinalizationFailed
            ? new IOException("cleanup-marker") : null;
        Exception? finalization = state == FileDbMutationTerminalState.DurableFinalizationFailed ? new IOException("finalization-marker") : null;
        return new FileDbMutationReceipt(Guid.NewGuid(), state, durable, 0, 1,
            [@"C:\Source"], [@"D:\Destination"], [], [],
            cleanup != null || state == FileDbMutationTerminalState.ManualRecoveryRequired ? [@"C:\Candidate"] : [],
            finalization ?? cleanup ?? (durable ? null : new IOException("not-committed-marker")), finalization, cleanup);
    }
}

/// <summary>Local terminal/model dialog recorder shared by the report and real folder-rename route tests.</summary>
internal sealed class FileDbReportRecordingDialogs : IUiDialogService, IBmsLibraryDialogService
{
    internal List<UiMessageRequest> Messages { get; } = [];
    internal int ModelMessages { get; private set; }
    internal Action? OnMessage { get; set; }
    internal Exception? MessageFailure { get; set; }

    public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
    {
        Messages.Add(request);
        OnMessage?.Invoke();
        if (MessageFailure != null) throw MessageFailure;
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
