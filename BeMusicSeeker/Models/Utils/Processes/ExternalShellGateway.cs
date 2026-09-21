using System;
using System.Diagnostics;

namespace BeMusicSeeker.Models.Utils;

internal enum ExternalShellRequestKind
{
    Url,
    AssociatedFile
}

internal sealed class ExternalShellRequest
{
    private ExternalShellRequest(ExternalShellRequestKind kind, string target)
    {
        Kind = kind;
        Target = target;
    }

    internal ExternalShellRequestKind Kind { get; }

    internal string Target { get; }

    internal static ExternalShellRequest OpenUrl(string url)
    {
        return Create(ExternalShellRequestKind.Url, url);
    }

    internal static ExternalShellRequest OpenAssociatedFile(string path)
    {
        return Create(ExternalShellRequestKind.AssociatedFile, path);
    }

    private static ExternalShellRequest Create(ExternalShellRequestKind kind, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("An external shell target is required.", nameof(target));
        }

        return new ExternalShellRequest(kind, target);
    }
}

internal interface IExternalShellGateway
{
    void Open(ExternalShellRequest request);

    ExplorerOpenResult OpenFileAndSelect(string filePath);

    ExplorerOpenResult OpenDirectory(string directoryPath);

    bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason);
}

internal static class ExternalShellGatewayPolicy
{
    internal static IExternalShellGateway Current { get; } = new WindowsExternalShellGateway();
}

internal sealed class WindowsExternalShellGateway : IExternalShellGateway
{
    public void Open(ExternalShellRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        Process.Start(new ProcessStartInfo(request.Target)
        {
            UseShellExecute = true
        });
    }

    public ExplorerOpenResult OpenFileAndSelect(string filePath)
    {
        return ExplorerOpenService.OpenFileAndSelect(filePath);
    }

    public ExplorerOpenResult OpenDirectory(string directoryPath)
    {
        return ExplorerOpenService.OpenDirectory(directoryPath);
    }

    public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
    {
        failureReason = string.Empty;
        try
        {
            return Process.Start("EXPLORER.EXE", "\"" + directoryPath + "\"") != null;
        }
        catch (Exception ex)
        {
            failureReason = "exception:" + ex.GetType().Name + ":" + ex.Message;
            return false;
        }
    }
}
