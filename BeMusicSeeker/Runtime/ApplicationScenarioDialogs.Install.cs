#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioDialogs
{
    private InstallReportInput? installReport;

    /// <summary>配送直前の手動導入要求と表示所有者に、既知の失敗報告への一回だけのOKを結びます。</summary>
    internal void ArmInstallFailureReport(long requestId, object owner, string destination)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (installReport is not null || pending is not null)
        { throw new InvalidOperationException("A dialog input is already armed."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        installReport = new(requestId, owner, destination);
    }

    /// <summary>実Taskの終端後に入力を必ず回収し、未通知・別通知・多重通知を失敗として返します。</summary>
    internal void CompleteInstallFailureReport(long requestId)
    {
        InstallReportInput input = installReport ?? throw new InvalidOperationException("Install report input is not armed.");
        installReport = null;
        if (input.RequestId != requestId || !input.Consumed || input.Invalid)
        { throw new InvalidOperationException("The declared install failure report was not consumed exactly once by its operation."); }
    }

    /// <summary>arm中だけ所有者と既知の導入報告を照合します。非armの通知は通常委譲のためfalseを返します。</summary>
    internal bool TryAnswerInstallReport(UiMessageRequest request, object? effectiveOwner,
        [NotNullWhen(true)] out Task<UiDialogResult>? answer)
    {
        answer = null;
        if (installReport is not { } input) { return false; }
        string operation = string.Format(Resources.FileDbMutationReport_Operation, Resources.Install) + Environment.NewLine;
        string missingDestination = Resources.Error_InstallDestinationMissing + Environment.NewLine + input.Destination;
        if (input.Consumed || input.Invalid || !ReferenceEquals(input.Owner, effectiveOwner)
            || request.Caption != Resources.FileDbMutationReport_Title
            || !request.MessageBoxText.StartsWith(operation, StringComparison.Ordinal)
            || !(request.MessageBoxText.Contains(missingDestination + Environment.NewLine, StringComparison.Ordinal)
                || request.MessageBoxText.EndsWith(missingDestination, StringComparison.Ordinal))
            || request.Button != MessageBoxButton.OK || request.DefaultResult != MessageBoxResult.OK
            || request.Icon != MessageBoxImage.Hand)
        {
            input.Invalid = true;
            answer = Task.FromException<UiDialogResult>(new InvalidOperationException("Unexpected notification during the declared manual install."));
            return true;
        }
        input.Consumed = true;
        answer = Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        return true;
    }

    private sealed record InstallReportInput(long RequestId, object Owner, string Destination)
    {
        internal bool Consumed { get; set; }
        internal bool Invalid { get; set; }
    }
}
