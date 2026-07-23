using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

internal enum SelectedChartExternalActionKind
{
    OpenExplorer,
    OpenFile,
    OpenLr2Ir,
    OpenMocha,
    OpenMinIr
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
    private static readonly Regex Md5HashRegex = new("^[a-f0-9]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Sha256HashRegex = new("^[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<string, bool> fileExists;
    private readonly Func<string, ExplorerOpenResult> explorerOpen;
    private readonly Action<string> associatedFileLauncher;
    private readonly Action<string> urlLauncher;
    private readonly Action<string> relatedDocumentLauncher;
    private readonly Func<string, string> directoryNameResolver;
    private readonly Func<string, string, IEnumerable<string>> relatedDocumentFileEnumerator;

    internal SelectedChartExternalActionWorkflowOwner(
        Func<string, bool> fileExists,
        Func<string, ExplorerOpenResult> explorerOpen,
        Action<string> associatedFileLauncher,
        Action<string> urlLauncher,
        Action<string> relatedDocumentLauncher = null,
        Func<string, string> directoryNameResolver = null,
        Func<string, string, IEnumerable<string>> relatedDocumentFileEnumerator = null)
    {
        this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        this.explorerOpen = explorerOpen ?? throw new ArgumentNullException(nameof(explorerOpen));
        this.associatedFileLauncher = associatedFileLauncher ?? throw new ArgumentNullException(nameof(associatedFileLauncher));
        this.urlLauncher = urlLauncher ?? throw new ArgumentNullException(nameof(urlLauncher));
        this.relatedDocumentLauncher = relatedDocumentLauncher ?? LaunchRelatedDocument;
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
        relatedDocumentLauncher(path);
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
            case SelectedChartExternalActionKind.OpenLr2Ir:
                return target.HasCapability(ChartOperationCapabilities.UseLr2Ir)
                    && IsValidMd5(target.Chart.Md5?.Trim());
            case SelectedChartExternalActionKind.OpenMocha:
            case SelectedChartExternalActionKind.OpenMinIr:
                return target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256)
                    && IsValidSha256(GetRepositorySha256(target));
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
                    explorerOpen(explorerPath);
                }
                return;
            case SelectedChartExternalActionKind.OpenFile:
                if (TryGetExistingPath(target, ChartOperationCapabilities.OpenFile, out string filePath))
                {
                    try
                    {
                        associatedFileLauncher(filePath);
                    }
                    catch
                    {
                    }
                }
                return;
            case SelectedChartExternalActionKind.OpenLr2Ir:
                string md5 = target.Chart.Md5?.Trim();
                urlLauncher("https://bms-ir.org/new/song?songmd5=" + md5 + "&view=both");
                return;
            case SelectedChartExternalActionKind.OpenMocha:
            case SelectedChartExternalActionKind.OpenMinIr:
                string sha256 = GetRepositorySha256(target);
                sha256 = sha256.Trim().ToLowerInvariant();
                string url = action == SelectedChartExternalActionKind.OpenMocha
                    ? "https://mocha-repository.info/song.php?sha256=" + sha256
                    : "https://www.gaftalk.com/minir/#/viewer/song/" + sha256 + "/0";
                urlLauncher(url);
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
        path = target.Chart.Path;
        return target.HasCapability(capability)
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

    private static void LaunchRelatedDocument(string path)
    {
        System.Diagnostics.Process.Start(path);
    }

    private static string GetRepositorySha256(ChartOperationTarget target)
    {
        return FirstNonEmpty(target.Chart.Sha256, target.Chart.ChartInfo?.sha256);
    }

    private static bool IsValidMd5(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && Md5HashRegex.IsMatch(value);
    }

    private static bool IsValidSha256(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && Sha256HashRegex.IsMatch(value.Trim());
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
