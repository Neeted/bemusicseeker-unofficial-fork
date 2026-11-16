using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class FileDbMutationReportTests
{
    /// <summary>
    /// 見出しの違う通知は先行の表示の完了を待って順に表示し、表示失敗を診断しても後続の組を失わない。
    /// 先行を待たずに重ねる誤りと、失敗で後続を落とす誤りを検出する。
    /// </summary>
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
            [new BMSLibrary.OperationDialogMessage("first", "caption-first", UiDialogButton.OK, UiDialogIcon.Information, UiDialogDefaultResult.OK),
             new BMSLibrary.OperationDialogMessage("second", "caption-second", UiDialogButton.OK, UiDialogIcon.Information, UiDialogDefaultResult.OK)],
            failures.Add);
        try
        {
            Assert.IsFalse(display.IsCompleted);
            Assert.AreEqual(1, dialogs.Messages.Count, "先行通知が表示中なら後続を重ねない。");
            first.SetException(displayFailure);
            await display;
            CollectionAssert.AreEqual(new[] { "first", "second" }, dialogs.Messages.Select(message => message.MessageBoxText).ToArray());
            CollectionAssert.AreEqual(new[] { displayFailure }, failures);
        }
        finally
        {
            first.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            await display;
        }
    }

    /// <summary>
    /// 同じ見出し・重要度の通知は、最初に現れた順に一件へまとめる。組の中は入力順で、
    /// 一件だけの組は元の通知のまま。同じ組を一件ずつ表示する誤りと、組の順の入れ替わりを検出する。
    /// </summary>
    [TestMethod]
    public async Task OperationMessages_GroupBySameCaptionAndSeverityInFirstAppearanceOrder()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var dialogs = new FileDbReportRecordingDialogs();
        BMSLibrary.OperationDialogMessage Message(string text, string caption, UiDialogIcon icon) =>
            new(text, caption, UiDialogButton.OK, icon, UiDialogDefaultResult.OK);
        BMSLibrary.OperationDialogMessage b1 = Message("B1", "capB", UiDialogIcon.Exclamation);
        BMSLibrary.OperationDialogMessage c1 = Message("C1", "capA", UiDialogIcon.Exclamation);

        await FileDbMutationReport.ShowOperationMessagesAsync(dialogs,
            [Message("A1", "capA", UiDialogIcon.Information), b1, Message("A2", "capA", UiDialogIcon.Information),
             c1, Message("A3", "capA", UiDialogIcon.Information)], culture: culture);

        Assert.AreEqual(3, dialogs.Messages.Count);
        Assert.AreEqual("capA", dialogs.Messages[0].Caption);
        Assert.AreEqual(MessageBoxImage.Information, dialogs.Messages[0].Icon);
        Assert.AreEqual(GroupBody(culture, 3, "A1", "A2", "A3"), dialogs.Messages[0].MessageBoxText);
        AssertSameAs(b1, dialogs.Messages[1]);
        AssertSameAs(c1, dialogs.Messages[2]);
    }

    /// <summary>
    /// 一件はそのまま、二件以上は件数の行と最大5件の標本、6件以上は「- ...」を加える。
    /// 件数の行は全件の数で、上限の境界での欠落や余分な省略記号を検出する。
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task OperationMessages_ShowCountAndAtMostFiveSamples(int count)
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var dialogs = new FileDbReportRecordingDialogs();
        string[] texts = [.. Enumerable.Range(1, count).Select(index => "item-" + index)];

        await FileDbMutationReport.ShowOperationMessagesAsync(dialogs,
            [.. texts.Select(text => new BMSLibrary.OperationDialogMessage(
                text, "caption", UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK))], culture: culture);

        UiMessageRequest request = dialogs.Messages.Single();
        string expected = count == 1
            ? texts[0]
            : GroupBody(culture, count, [.. texts.Take(5)], showsMore: count > 5);
        Assert.AreEqual(expected, request.MessageBoxText);
        Assert.AreEqual("caption", request.Caption);
        Assert.AreEqual(MessageBoxImage.Hand, request.Icon);
    }

    /// <summary>標本の一行は、改行と連続する空白を一つの空白へ畳み、長い本文を切り詰める。複数行の本文が標本の行を崩す誤りを検出する。</summary>
    [TestMethod]
    public async Task OperationMessages_SampleLinesCollapseWhitespaceAndLimitLength()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var dialogs = new FileDbReportRecordingDialogs();
        string longText = new('x', 500);

        await FileDbMutationReport.ShowOperationMessagesAsync(dialogs,
            [new BMSLibrary.OperationDialogMessage("line1\r\n\r\nline2   end", "caption", UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK),
             new BMSLibrary.OperationDialogMessage(longText, "caption", UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK)],
            culture: culture);

        string[] lines = dialogs.Messages.Single().MessageBoxText.Split(Environment.NewLine);
        Assert.AreEqual(3, lines.Length);
        Assert.AreEqual("- line1 line2 end", lines[1]);
        Assert.AreEqual("- " + new string('x', 199) + "…", lines[2]);
    }

    /// <summary>
    /// 読込み失敗の事実は表示の時点の言語で整形する。一件は従来の個別の通知と同じ本文・見出し・ボタン・アイコンで、
    /// 複数件は件数と入力順の標本5件、全件に満たなければ「- ...」。例外メッセージは捕捉時のままで、
    /// 作成時の言語や標本の順が崩れる誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    public async Task MaintenanceReadFailures_FormatWithDisplayCultureAndInputOrder(int count)
    {
        var created = CultureInfo.GetCultureInfo("en-US");
        var displayed = CultureInfo.GetCultureInfo("ja-JP");
        var collector = new MaintenanceReadFailureCollector();
        // 対象の入力順と逆の順で追加し、標本が完了順ではなく入力順になることを確かめる。
        foreach (int index in Enumerable.Range(0, count).Reverse())
        {
            collector.Add(index, @"C:\Songs\fail-" + index + ".bms", new IOException("cause-" + index));
        }
        var dialogs = new FileDbReportRecordingDialogs();

        await FileDbMutationReport.ShowMaintenanceReadFailuresAsync(dialogs, collector, culture: displayed);
        await FileDbMutationReport.ShowMaintenanceReadFailuresAsync(dialogs, collector, culture: created);

        Assert.AreEqual(2, dialogs.Messages.Count);
        Assert.AreNotEqual(dialogs.Messages[0].Caption, dialogs.Messages[1].Caption, "見出しは表示の時点の言語。");
        foreach ((UiMessageRequest request, CultureInfo culture) in new[] { (dialogs.Messages[0], displayed), (dialogs.Messages[1], created) })
        {
            Assert.AreEqual(Resources.ResourceManager.GetString(nameof(Resources.MessageBoxTitle_Error), culture), request.Caption);
            Assert.AreEqual(MessageBoxButton.OK, request.Button);
            Assert.AreEqual(MessageBoxImage.Hand, request.Icon);
            Assert.AreEqual(MessageBoxResult.OK, request.DefaultResult);
            if (count == 1)
            {
                Assert.AreEqual(string.Format(culture, Localized(nameof(Resources.Error_BmsLoadFailedSkip), culture),
                    @"C:\Songs\fail-0.bms", "cause-0"), request.MessageBoxText);
            }
            else
            {
                string[] samples = [.. Enumerable.Range(0, 5).Select(index => @"C:\Songs\fail-" + index + ".bms (cause-" + index + ")")];
                Assert.AreEqual(GroupBody(culture, count, samples, showsMore: true), request.MessageBoxText);
            }
        }
    }

    /// <summary>失敗がなければ何も表示しない。空の収集先で空の通知が出る誤りを検出する。</summary>
    [TestMethod]
    public async Task MaintenanceReadFailures_NoFailuresShowNothing()
    {
        var dialogs = new FileDbReportRecordingDialogs();

        await FileDbMutationReport.ShowMaintenanceReadFailuresAsync(dialogs, new MaintenanceReadFailureCollector());

        Assert.AreEqual(0, dialogs.Messages.Count);
    }

    /// <summary>
    /// 表示の失敗（失敗の結果、失敗したTask、同期の例外）は失敗した組ごとに元の例外を一度だけ診断へ渡し、
    /// 後続の組の表示を続ける。失敗で後続が落ちる誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("result")]
    [DataRow("faulted")]
    [DataRow("throws")]
    public async Task OperationMessages_DisplayFailureOfOneGroupDoesNotStopLaterGroups(string shape)
    {
        var failure = new IOException("display-failure-" + shape);
        var failures = new List<Exception>();
        var dialogs = new FileDbReportRecordingDialogs();
        dialogs.MessageHandler = request =>
        {
            if (request.Caption != "failing")
            {
                return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            }
            return shape switch
            {
                "result" => Task.FromResult(UiDialogResult.Failed(failure)),
                "faulted" => Task.FromException<UiDialogResult>(failure),
                _ => throw failure,
            };
        };
        BMSLibrary.OperationDialogMessage Message(string caption) =>
            new("text-" + caption, caption, UiDialogButton.OK, UiDialogIcon.Information, UiDialogDefaultResult.OK);

        await FileDbMutationReport.ShowOperationMessagesAsync(dialogs,
            [Message("failing"), Message("later")], failures.Add);

        CollectionAssert.AreEqual(new[] { "failing", "later" }, dialogs.Messages.Select(message => message.Caption).ToArray());
        Assert.AreEqual(1, failures.Count);
        Exception reported = failures[0];
        Assert.IsTrue(ReferenceEquals(failure, reported) || ReferenceEquals(failure, reported.InnerException), reported.ToString());
    }

    /// <summary>指定した文化圏の文言を返します。キーが存在しない（null）場合は、期待値を作れないため失敗にします。</summary>
    private static string Localized(string key, CultureInfo culture)
    {
        string? text = Resources.ResourceManager.GetString(key, culture);
        Assert.IsNotNull(text, key);
        return text;
    }

    private static void AssertSameAs(BMSLibrary.OperationDialogMessage expected, UiMessageRequest actual)
    {
        Assert.AreEqual(expected.MessageBoxText, actual.MessageBoxText);
        Assert.AreEqual(expected.Caption, actual.Caption);
        Assert.AreEqual(UiDialogPresentationAdapter.ToWpf(expected.Button), actual.Button);
        Assert.AreEqual(UiDialogPresentationAdapter.ToWpf(expected.Icon), actual.Icon);
        Assert.AreEqual(UiDialogPresentationAdapter.ToWpf(expected.DefaultResult), actual.DefaultResult);
    }

    private static string GroupBody(CultureInfo culture, int count, params string[] samples)
        => GroupBody(culture, count, samples, showsMore: false);

    /// <summary>件数の行と標本の行を、リソースから組み立てた期待値として返します。</summary>
    private static string GroupBody(CultureInfo culture, int count, string[] samples, bool showsMore)
    {
        List<string> lines = [string.Format(culture, Localized(nameof(Resources.Notice_GroupedCount), culture), count)];
        lines.AddRange(samples.Select(sample => "- " + sample));
        if (showsMore)
        {
            lines.Add("- ...");
        }
        return string.Join(Environment.NewLine, lines);
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

    /// <summary>六言語の長い操作名・回復候補・各失敗を上限内に縮めて案内を残し、表示の肥大化や回復案内の欠落を検出します。</summary>
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
            {
                pattern = pattern.Replace(Regex.Escape("{" + index + "}"), "(?<field" + index + @">\d+)", StringComparison.Ordinal);
            }

            Match fields = Regex.Match(FileDbMutationReport.Create("counted-operation", session, culture: culture).MessageBoxText, pattern);
            Assert.IsTrue(fields.Success, language);
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.AreEqual(expected[index], int.Parse(fields.Groups["field" + index].Value, CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>六件の保存先型衝突は五件と省略数を警告へ載せ、全件展開や省略数の取り違えを検出します。</summary>
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
        {
            StringAssert.Contains(report.MessageBoxText, conflict.DestinationPath);
        }

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
        {
            StringAssert.Contains(report.MessageBoxText, string.Format(CultureInfo.CurrentCulture, successFormat, 1));
        }
        else
        {
            Assert.IsFalse(report.MessageBoxText.Contains(string.Format(CultureInfo.CurrentCulture, successFormat, 0), StringComparison.Ordinal));
        }
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
        if (requiredFailure)
        {
            StringAssert.Contains(report.MessageBoxText, "physical-marker");
        }

        StringAssert.EndsWith(report.MessageBoxText, Resources.FileDbMutationReport_DestinationTypeConflict_Guidance);
    }

    /// <summary>型衝突・回復候補・複数失敗が長くても六言語で本文上限と末尾案内を保ち、専用表示での切り捨てを検出します。</summary>
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

    /// <summary>事前拒否の事実を持たない実行時型衝突はErrorで原因を伝え、単なる警告へ格下げする誤りを検出します。</summary>
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

    /// <summary>終端通知が失敗しても一回で終え、確定済みdurable・finalizer・cleanupの事実を変更する誤りを検出します。</summary>
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
