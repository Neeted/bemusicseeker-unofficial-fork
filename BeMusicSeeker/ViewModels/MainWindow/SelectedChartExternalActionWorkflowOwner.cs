using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal enum SelectedChartExternalActionKind
{
    OpenExplorer,
    OpenFile
}

/// <summary>
/// Kind carried by a generated context-menu action. The stable action ID is
/// resolved again at click time so stale menu entries cannot launch another row.
/// </summary>
internal enum ConfiguredExternalActionKind
{
    /// <summary>browser/shell URL action。</summary>
    Web,
    /// <summary>local chart file を渡す program action。</summary>
    Program
}

/// <summary>
/// Typed failure categories for configured web/program actions.
/// </summary>
internal enum ExternalConfiguredActionFailureKind
{
    /// <summary>成功または failure なし。</summary>
    None,
    /// <summary>persisted settings が strict parse に失敗した。</summary>
    InvalidSettings,
    /// <summary>action または exact row が stale になった。</summary>
    ActionUnavailable,
    /// <summary>shell gateway が URL を開けなかった。</summary>
    WebLaunchFailed,
    /// <summary>設定された program executable が click 時点で存在しなかった。</summary>
    ProgramExecutableMissing,
    /// <summary>program に渡す local chart が click 時点で存在しなかった。</summary>
    ProgramChartMissing,
    /// <summary>program gateway が chart を起動できなかった。</summary>
    ProgramLaunchFailed
}

/// <summary>
/// Immutable result returned by configured external-action execution.
/// </summary>
internal sealed class ExternalConfiguredActionResult
{
    private ExternalConfiguredActionResult(
        bool succeeded,
        ExternalConfiguredActionFailureKind failureKind,
        string diagnostic,
        Exception exception)
    {
        Succeeded = succeeded;
        FailureKind = failureKind;
        Diagnostic = diagnostic ?? string.Empty;
        Exception = exception;
    }

    /// <summary>external action が成功したか。</summary>
    internal bool Succeeded { get; }

    /// <summary>失敗時の typed category。</summary>
    internal ExternalConfiguredActionFailureKind FailureKind { get; }

    /// <summary>terminal/UI へ渡す診断。</summary>
    internal string Diagnostic { get; }

    /// <summary>gateway が返した例外。通常の stale/missing では null。</summary>
    internal Exception Exception { get; }

    internal static ExternalConfiguredActionResult Success { get; } =
        new(true, ExternalConfiguredActionFailureKind.None, string.Empty, null);

    internal static ExternalConfiguredActionResult Failure(
        ExternalConfiguredActionFailureKind failureKind,
        string diagnostic,
        Exception exception = null)
    {
        return new(false, failureKind, diagnostic, exception);
    }
}

internal enum RelatedDocumentQueryStatus
{
    Unavailable,
    Empty,
    Available,
    Failed,
    Canceled
}

internal sealed class RelatedDocumentQueryReceipt
{
    private RelatedDocumentQueryReceipt(RelatedDocumentQueryStatus status, IReadOnlyList<string> paths)
    {
        Status = status;
        Paths = paths;
    }

    internal RelatedDocumentQueryStatus Status { get; }

    internal IReadOnlyList<string> Paths { get; }

    internal static RelatedDocumentQueryReceipt Unavailable { get; } =
        new(RelatedDocumentQueryStatus.Unavailable, Array.Empty<string>());

    internal static RelatedDocumentQueryReceipt Empty { get; } =
        new(RelatedDocumentQueryStatus.Empty, Array.Empty<string>());

    internal static RelatedDocumentQueryReceipt Failed { get; } =
        new(RelatedDocumentQueryStatus.Failed, Array.Empty<string>());

    internal static RelatedDocumentQueryReceipt Canceled { get; } =
        new(RelatedDocumentQueryStatus.Canceled, Array.Empty<string>());

    internal static RelatedDocumentQueryReceipt Available(IReadOnlyList<string> paths) =>
        new(
            RelatedDocumentQueryStatus.Available,
            paths == null ? Array.Empty<string>() : Array.AsReadOnly([.. paths]));
}

internal sealed class SelectedChartExternalActionWorkflowOwner
{
    private readonly Func<string, bool> fileExists;
    private readonly IExternalShellGateway externalShellGateway;
    private readonly IExternalProgramLaunchGateway externalProgramLaunchGateway;
    private readonly RightClickActionSettingsStore rightClickActionSettingsStore;
    private readonly Func<string, string> directoryNameResolver;
    private readonly Func<string, string, IEnumerable<string>> relatedDocumentFileEnumerator;

    internal SelectedChartExternalActionWorkflowOwner(
        Func<string, bool> fileExists,
        IExternalShellGateway externalShellGateway,
        Func<string, string> directoryNameResolver = null,
        Func<string, string, IEnumerable<string>> relatedDocumentFileEnumerator = null,
        Func<Settings> settingsProvider = null,
        IExternalProgramLaunchGateway externalProgramLaunchGateway = null)
    {
        this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        this.externalShellGateway = externalShellGateway
            ?? throw new ArgumentNullException(nameof(externalShellGateway));
        this.externalProgramLaunchGateway = externalProgramLaunchGateway
            ?? ExternalProgramLaunchGatewayPolicy.Current;
        rightClickActionSettingsStore = new RightClickActionSettingsStore(settingsProvider);
        this.directoryNameResolver = directoryNameResolver ?? DirectoryExt.GetDirectoryNameSimple;
        this.relatedDocumentFileEnumerator = relatedDocumentFileEnumerator
            ?? ((directory, pattern) => LongPathFileSystem.EnumerateFiles(directory, pattern));
    }

    internal bool CanQueryRelatedDocuments(ChartOperationTarget target)
    {
        return TryGetExistingChartPath(target, out _);
    }

    internal async Task<RelatedDocumentQueryReceipt> QueryRelatedDocumentsAsync(
        ChartOperationTarget target,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return RelatedDocumentQueryReceipt.Canceled;
        }
        if (!CanQueryRelatedDocuments(target))
        {
            return RelatedDocumentQueryReceipt.Unavailable;
        }

        try
        {
            RelatedDocumentQueryReceipt receipt = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string directory = directoryNameResolver(target.Chart.Path);
                    cancellationToken.ThrowIfCancellationRequested();
                    List<string> paths = [.. relatedDocumentFileEnumerator(directory, "*.txt")];
                    cancellationToken.ThrowIfCancellationRequested();
                    paths.AddRange(relatedDocumentFileEnumerator(directory, "*.htm?"));
                    cancellationToken.ThrowIfCancellationRequested();
                    return paths.Count == 0
                        ? RelatedDocumentQueryReceipt.Empty
                        : RelatedDocumentQueryReceipt.Available(paths);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return RelatedDocumentQueryReceipt.Failed;
                }
            }, cancellationToken).ConfigureAwait(false);

            return cancellationToken.IsCancellationRequested
                ? RelatedDocumentQueryReceipt.Canceled
                : receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RelatedDocumentQueryReceipt.Canceled;
        }
    }

    internal void OpenRelatedDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !fileExists(path))
        {
            return;
        }
        externalShellGateway.Open(ExternalShellRequest.OpenAssociatedFile(path));
    }

    /// <summary>
    /// Converts one exact row target into the immutable context consumed by the
    /// shared right-click resolver. A local path is supplied only for an owned,
    /// existing chart; hash-only and playlist-missing targets never get a path.
    /// </summary>
    internal bool TryCreateResolutionInput(
        ChartOperationTarget target,
        out RightClickActionResolutionInput input)
    {
        input = null;
        if (target?.Chart == null)
        {
            return false;
        }

        string localFilePath = target.IsOwned
            && !target.IsPlaylistMissing
            && target.HasCapability(ChartOperationCapabilities.OpenFile)
            && !string.IsNullOrWhiteSpace(target.Chart.Path)
            && fileExists(target.Chart.Path)
                ? target.Chart.Path
                : null;
        input = new RightClickActionResolutionInput(
            target.Chart.Md5,
            GetExternalActionSha256(target),
            localFilePath,
            target.Chart.Kind == ChartFileKind.Bmson
                ? ExternalChartKind.BmsonOnly
                : ExternalChartKind.BmsOnly);
        return true;
    }

    internal RightClickActionResolutionInput CreateResolutionInput(ChartOperationTarget target)
    {
        return TryCreateResolutionInput(target, out RightClickActionResolutionInput input)
            ? input
            : null;
    }

    /// <summary>
    /// Resolves enabled configured actions in persisted order. Invalid settings
    /// intentionally produce an empty menu; the settings page owns diagnosis and
    /// reset presentation.
    /// </summary>
    internal RightClickActionResolution ResolveConfiguredActions(RightClickActionResolutionInput input)
    {
        if (input == null)
        {
            return new RightClickActionResolution([], []);
        }

        RightClickActionSettingsParseResult parsed = rightClickActionSettingsStore.Load();
        return parsed.Succeeded
            ? RightClickActionResolver.Resolve(parsed.Settings, input)
            : new RightClickActionResolution([], []);
    }

    internal RightClickActionResolution ResolveConfiguredActions(ChartOperationTarget target)
    {
        return TryCreateResolutionInput(target, out RightClickActionResolutionInput input)
            ? ResolveConfiguredActions(input)
            : new RightClickActionResolution([], []);
    }

    /// <summary>
    /// Re-reads settings and re-resolves the supplied exact context before a
    /// generated menu action is launched.
    /// </summary>
    internal ExternalConfiguredActionResult ExecuteConfiguredAction(
        RightClickActionResolutionInput input,
        ConfiguredExternalActionKind actionKind,
        string actionId)
    {
        if (input == null || string.IsNullOrWhiteSpace(actionId))
        {
            return ExternalConfiguredActionResult.Failure(
                ExternalConfiguredActionFailureKind.ActionUnavailable,
                string.Empty);
        }

        RightClickActionSettingsParseResult parsed = rightClickActionSettingsStore.Load();
        if (!parsed.Succeeded)
        {
            return ExternalConfiguredActionResult.Failure(
                ExternalConfiguredActionFailureKind.InvalidSettings,
                parsed.Error?.ToString());
        }

        RightClickActionResolution resolution = RightClickActionResolver.Resolve(parsed.Settings, input);
        if (actionKind == ConfiguredExternalActionKind.Web)
        {
            ResolvedRightClickWebAction action = resolution.WebActions.FirstOrDefault(
                candidate => string.Equals(candidate.Id, actionId, StringComparison.Ordinal));
            if (action == null)
            {
                return ExternalConfiguredActionResult.Failure(
                    ExternalConfiguredActionFailureKind.ActionUnavailable,
                    string.Empty);
            }

            try
            {
                externalShellGateway.Open(ExternalShellRequest.OpenUrl(action.Url));
                return ExternalConfiguredActionResult.Success;
            }
            catch (Exception exception)
            {
                return ExternalConfiguredActionResult.Failure(
                    ExternalConfiguredActionFailureKind.WebLaunchFailed,
                    exception.Message,
                    exception);
            }
        }

        ResolvedRightClickProgramAction program = resolution.ProgramActions.FirstOrDefault(
            candidate => string.Equals(candidate.Id, actionId, StringComparison.Ordinal));
        if (program == null || string.IsNullOrWhiteSpace(input.LocalFilePath))
        {
            return ExternalConfiguredActionResult.Failure(
                ExternalConfiguredActionFailureKind.ActionUnavailable,
                string.Empty);
        }

        ExternalProgramLaunchResult launch = externalProgramLaunchGateway.Launch(
            new ExternalProgramLaunchRequest(
                program.Id,
                program.ExecutablePath,
                input.LocalFilePath,
                program.Arguments));
        return launch.Succeeded
            ? ExternalConfiguredActionResult.Success
            : ExternalConfiguredActionResult.Failure(
                MapProgramLaunchFailure(launch.FailureKind),
                launch.Diagnostic,
                launch.Exception);
    }

    private static ExternalConfiguredActionFailureKind MapProgramLaunchFailure(
        ExternalProgramLaunchFailureKind failureKind)
    {
        return failureKind switch
        {
            ExternalProgramLaunchFailureKind.InvalidExecutablePath => ExternalConfiguredActionFailureKind.ProgramExecutableMissing,
            ExternalProgramLaunchFailureKind.MissingExecutable => ExternalConfiguredActionFailureKind.ProgramExecutableMissing,
            ExternalProgramLaunchFailureKind.MissingChart => ExternalConfiguredActionFailureKind.ProgramChartMissing,
            _ => ExternalConfiguredActionFailureKind.ProgramLaunchFailed
        };
    }

    internal bool CanExecute(ChartOperationTarget target, SelectedChartExternalActionKind action)
    {
        if (target?.Chart == null)
        {
            return false;
        }

        switch (action)
        {
            case SelectedChartExternalActionKind.OpenExplorer:
                return TryGetExistingPath(target, ChartOperationCapabilities.OpenFolder, out _);
            case SelectedChartExternalActionKind.OpenFile:
                return TryGetExistingPath(target, ChartOperationCapabilities.OpenFile, out _);
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    internal void Execute(ChartOperationTarget target, SelectedChartExternalActionKind action)
    {
        if (!CanExecute(target, action))
        {
            return;
        }

        switch (action)
        {
            case SelectedChartExternalActionKind.OpenExplorer:
                if (TryGetExistingPath(target, ChartOperationCapabilities.OpenFolder, out string explorerPath))
                {
                    externalShellGateway.OpenFileAndSelect(explorerPath);
                }
                return;
            case SelectedChartExternalActionKind.OpenFile:
                if (TryGetExistingPath(target, ChartOperationCapabilities.OpenFile, out string filePath))
                {
                    try
                    {
                        externalShellGateway.Open(ExternalShellRequest.OpenAssociatedFile(filePath));
                    }
                    catch
                    {
                    }
                }
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private bool TryGetExistingPath(
        ChartOperationTarget target,
        ChartOperationCapabilities capability,
        out string path)
    {
        path = target?.Chart?.Path;
        return target != null
            && target.HasCapability(capability)
            && !string.IsNullOrWhiteSpace(path)
            && fileExists(path);
    }

    private bool TryGetExistingChartPath(ChartOperationTarget target, out string path)
    {
        path = target?.Chart?.Path;
        return target?.Chart != null
            && !string.IsNullOrWhiteSpace(path)
            && fileExists(path);
    }

    private static string GetExternalActionSha256(ChartOperationTarget target)
    {
        return FirstNonEmpty(target.Chart.Sha256, target.Chart.ChartInfo?.sha256);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }
}
