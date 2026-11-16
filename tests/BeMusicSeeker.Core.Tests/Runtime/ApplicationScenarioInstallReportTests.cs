using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Core.Tests.Runtime;

/// <summary>実Windowを作らず、手動導入の固定回答の許可・拒否・一回消費だけを検査します。</summary>
[TestClass]
public sealed class ApplicationScenarioInstallReportTests
{
    private const string Destination = @"C:\scenario\missing";

    /// <summary>非armは通常委譲し、同じ要求・所有者・既知原因の一回だけを回答して権限を回収します。</summary>
    [TestMethod]
    public async Task InstallReport_AnswersOnceAndDoesNotCarryPermissionToNextRequest()
    {
        var dialogs = new ApplicationScenarioDialogs();
        object owner = new object();
        Assert.IsFalse(dialogs.TryAnswerInstallReport(Request(), owner, out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => dialogs.CompleteInstallFailureReport(1));
        dialogs.ArmInstallFailureReport(1, owner, Destination);
        Assert.ThrowsExactly<InvalidOperationException>(() => dialogs.ArmInstallFailureReport(2, owner, Destination));
        Assert.ThrowsExactly<InvalidOperationException>(() => dialogs.ArmProperty("other", null));
        Assert.IsTrue(dialogs.TryAnswerInstallReport(Request(), owner, out Task<UiDialogResult>? answer));
        Assert.IsNotNull(answer);
        Assert.AreEqual(UiDialogStatus.Accepted, (await answer).Status);
        dialogs.CompleteInstallFailureReport(1);
        Assert.IsFalse(dialogs.TryAnswerInstallReport(Request(), owner, out _));
        var otherInput = new ApplicationScenarioDialogs();
        otherInput.ArmProperty("other", null);
        Assert.ThrowsExactly<InvalidOperationException>(() => otherInput.ArmInstallFailureReport(3, owner, Destination));
        dialogs.ArmInstallFailureReport(2, owner, Destination);
        Assert.ThrowsExactly<InvalidOperationException>(() => dialogs.CompleteInstallFailureReport(2));
        Assert.IsFalse(dialogs.TryAnswerInstallReport(Request(), owner, out _));
    }

    /// <summary>別通知・別所有者・複数回の通知は失敗を保持し、後の正しい通知でも成功に戻しません。</summary>
    [TestMethod]
    [DataRow("owner")]
    [DataRow("caption")]
    [DataRow("operation")]
    [DataRow("cause")]
    [DataRow("destination")]
    [DataRow("destination-prefix")]
    [DataRow("buttons")]
    [DataRow("default")]
    [DataRow("icon")]
    [DataRow("multiple")]
    public async Task InstallReport_RejectsUnexpectedNotificationAndRetainsFailure(string invalid)
    {
        var dialogs = new ApplicationScenarioDialogs();
        object owner = new object();
        dialogs.ArmInstallFailureReport(1, owner, Destination);
        if (invalid == "multiple")
        {
            Assert.IsTrue(dialogs.TryAnswerInstallReport(Request(), owner, out Task<UiDialogResult>? first));
            Assert.IsNotNull(first);
            await first;
        }
        UiMessageRequest request = Request(invalid);
        Assert.IsTrue(dialogs.TryAnswerInstallReport(request, invalid == "owner" ? new object() : owner, out Task<UiDialogResult>? rejected));
        Assert.IsNotNull(rejected);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rejected);
        Assert.IsTrue(dialogs.TryAnswerInstallReport(Request(), owner, out Task<UiDialogResult>? later));
        Assert.IsNotNull(later);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => later);
        Assert.ThrowsExactly<InvalidOperationException>(() => dialogs.CompleteInstallFailureReport(1));
        Assert.IsFalse(dialogs.TryAnswerInstallReport(Request(), owner, out _));
    }

    /// <summary>同じ通知を消費しても別要求による終了は失敗し、その権限は残しません。</summary>
    [TestMethod]
    public async Task InstallReport_RejectsDifferentOperationCompletion()
    {
        var dialogs = new ApplicationScenarioDialogs();
        object owner = new object();
        dialogs.ArmInstallFailureReport(1, owner, Destination);
        Assert.IsTrue(dialogs.TryAnswerInstallReport(Request(), owner, out Task<UiDialogResult>? answer));
        Assert.IsNotNull(answer);
        await answer;
        Assert.ThrowsExactly<InvalidOperationException>(() => dialogs.CompleteInstallFailureReport(2));
        Assert.IsFalse(dialogs.TryAnswerInstallReport(Request(), owner, out _));
    }

    private static UiMessageRequest Request(string? invalid = null)
    {
        string operation = invalid == "operation" ? Resources.FileDbMutationReport_Move : Resources.Install;
        string cause = invalid == "cause" ? "Other failure" : Resources.Error_InstallDestinationMissing;
        string destination = invalid == "destination" ? @"C:\scenario\other" : invalid == "destination-prefix" ? Destination + "-other" : Destination;
        return new UiMessageRequest(string.Format(Resources.FileDbMutationReport_Operation, operation) + Environment.NewLine
            + cause + Environment.NewLine + destination,
            invalid == "caption" ? Resources.Error : Resources.FileDbMutationReport_Title,
            invalid == "buttons" ? MessageBoxButton.OKCancel : MessageBoxButton.OK,
            invalid == "icon" ? MessageBoxImage.Information : MessageBoxImage.Hand,
            invalid == "default" ? MessageBoxResult.Cancel : MessageBoxResult.OK);
    }
}
