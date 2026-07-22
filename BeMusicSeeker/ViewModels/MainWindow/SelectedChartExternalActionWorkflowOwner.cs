using System;
using System.Text.RegularExpressions;
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

internal sealed class SelectedChartExternalActionWorkflowOwner
{
    private static readonly Regex Md5HashRegex = new("^[a-f0-9]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Sha256HashRegex = new("^[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<string, bool> fileExists;
    private readonly Func<string, ExplorerOpenResult> explorerOpen;
    private readonly Action<string> associatedFileLauncher;
    private readonly Action<string> urlLauncher;

    internal SelectedChartExternalActionWorkflowOwner(
        Func<string, bool> fileExists,
        Func<string, ExplorerOpenResult> explorerOpen,
        Action<string> associatedFileLauncher,
        Action<string> urlLauncher)
    {
        this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        this.explorerOpen = explorerOpen ?? throw new ArgumentNullException(nameof(explorerOpen));
        this.associatedFileLauncher = associatedFileLauncher ?? throw new ArgumentNullException(nameof(associatedFileLauncher));
        this.urlLauncher = urlLauncher ?? throw new ArgumentNullException(nameof(urlLauncher));
    }

    internal void Execute(ChartOperationTarget target, SelectedChartExternalActionKind action)
    {
        if (target?.Chart == null)
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
                if (!target.HasCapability(ChartOperationCapabilities.UseLr2Ir))
                {
                    return;
                }
                string md5 = target.Chart.Md5?.Trim();
                if (!IsValidMd5(md5))
                {
                    return;
                }
                urlLauncher("https://bms-ir.org/new/song?songmd5=" + md5 + "&view=both");
                return;
            case SelectedChartExternalActionKind.OpenMocha:
            case SelectedChartExternalActionKind.OpenMinIr:
                if (!target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256))
                {
                    return;
                }
                string sha256 = GetRepositorySha256(target);
                if (!IsValidSha256(sha256))
                {
                    return;
                }
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
