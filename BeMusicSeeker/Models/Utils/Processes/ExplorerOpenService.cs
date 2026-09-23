using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.Utils;

internal enum ExplorerOpenResultKind
{
    InvalidPath,
    NotFound,
    SelectedFile,
    OpenedDirectory,
    OpenedParentDirectory,
    Failed
}

internal sealed class ExplorerOpenResult
{
    public ExplorerOpenResultKind Kind { get; set; }

    public bool Success => Kind == ExplorerOpenResultKind.SelectedFile
        || Kind == ExplorerOpenResultKind.OpenedDirectory
        || Kind == ExplorerOpenResultKind.OpenedParentDirectory;

    public string RequestedPath { get; set; } = string.Empty;

    public string OpenedPath { get; set; } = string.Empty;

    public string FailureReason { get; set; } = string.Empty;
}

internal interface IExplorerShell
{
    bool TryResolvePath(string path, out string failureReason);

    bool TrySelectFile(string filePath, out string failureReason);

    bool TryOpenDirectory(string directoryPath, out string failureReason);

    bool TryOpenDirectoryWithExplorer(string directoryPath, out string failureReason);
}

internal static class ExplorerOpenService
{
    public static ExplorerOpenResult OpenFileAndSelect(string filePath)
    {
        return OpenFileAndSelect(filePath, ShellExplorerAdapter.Instance);
    }

    public static ExplorerOpenResult OpenDirectory(string directoryPath)
    {
        return OpenDirectory(directoryPath, ShellExplorerAdapter.Instance);
    }

    internal static ExplorerOpenResult OpenFileAndSelect(string filePath, IExplorerShell shell)
    {
        if (!TryNormalize(filePath, out string normalizedPath, out ExplorerOpenResult invalidResult))
        {
            return invalidResult;
        }

        if (!LongPathFileSystem.FileExists(normalizedPath))
        {
            return new ExplorerOpenResult
            {
                Kind = ExplorerOpenResultKind.NotFound,
                RequestedPath = normalizedPath,
                FailureReason = "file_not_found"
            };
        }

        string selectedCandidate = ResolveFirstShellPathCandidate(normalizedPath, shell, out string resolveFailureReason);
        if (!string.IsNullOrWhiteSpace(selectedCandidate))
        {
            if (shell.TrySelectFile(selectedCandidate, out string failureReason))
            {
                return new ExplorerOpenResult
                {
                    Kind = ExplorerOpenResultKind.SelectedFile,
                    RequestedPath = normalizedPath,
                    OpenedPath = normalizedPath
                };
            }

            LogFailure("select_file", normalizedPath, failureReason);
            return new ExplorerOpenResult
            {
                Kind = ExplorerOpenResultKind.Failed,
                RequestedPath = normalizedPath,
                FailureReason = string.IsNullOrWhiteSpace(failureReason) ? "select_file_failed" : failureReason
            };
        }

        string parentDirectory = Path.GetDirectoryName(normalizedPath);
        ExplorerOpenResult fallbackResult = OpenExistingDirectoryWithoutPriorOpen(
            parentDirectory,
            shell,
            ExplorerOpenResultKind.OpenedParentDirectory,
            normalizedPath);
        if (fallbackResult.Success)
        {
            return fallbackResult;
        }

        string finalFailureReason = CombineFailureReasons(resolveFailureReason, fallbackResult.FailureReason);
        LogFailure("select_file", normalizedPath, finalFailureReason);
        return new ExplorerOpenResult
        {
            Kind = ExplorerOpenResultKind.Failed,
            RequestedPath = normalizedPath,
            FailureReason = string.IsNullOrWhiteSpace(finalFailureReason)
                ? "select_and_parent_open_failed"
                : finalFailureReason
        };
    }

    internal static ExplorerOpenResult OpenDirectory(string directoryPath, IExplorerShell shell)
    {
        if (!TryNormalize(directoryPath, out string normalizedPath, out ExplorerOpenResult invalidResult))
        {
            return invalidResult;
        }

        return OpenExistingDirectoryWithoutPriorOpen(normalizedPath, shell, ExplorerOpenResultKind.OpenedDirectory, normalizedPath);
    }

    private static ExplorerOpenResult OpenExistingDirectoryWithoutPriorOpen(string directoryPath, IExplorerShell shell, ExplorerOpenResultKind successKind, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new ExplorerOpenResult
            {
                Kind = ExplorerOpenResultKind.InvalidPath,
                RequestedPath = requestedPath ?? string.Empty,
                FailureReason = "empty_directory"
            };
        }

        string normalizedDirectory;
        try
        {
            normalizedDirectory = LongPathFileSystem.NormalizePathForStorage(directoryPath);
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException)
        {
            return new ExplorerOpenResult
            {
                Kind = ExplorerOpenResultKind.InvalidPath,
                RequestedPath = requestedPath ?? string.Empty,
                FailureReason = "normalize_directory_failed:" + ex.GetType().Name
            };
        }

        if (!LongPathFileSystem.DirectoryExists(normalizedDirectory))
        {
            return new ExplorerOpenResult
            {
                Kind = ExplorerOpenResultKind.NotFound,
                RequestedPath = requestedPath ?? normalizedDirectory,
                FailureReason = "directory_not_found"
            };
        }

        string selectedCandidate = ResolveFirstShellPathCandidate(normalizedDirectory, shell, out string resolveFailureReason);
        if (!string.IsNullOrWhiteSpace(selectedCandidate))
        {
            if (shell.TryOpenDirectory(selectedCandidate, out string failureReason))
            {
                return new ExplorerOpenResult
                {
                    Kind = successKind,
                    RequestedPath = requestedPath ?? normalizedDirectory,
                    OpenedPath = normalizedDirectory
                };
            }

            LogFailure("open_directory", normalizedDirectory, failureReason);
            return new ExplorerOpenResult
            {
                Kind = ExplorerOpenResultKind.Failed,
                RequestedPath = requestedPath ?? normalizedDirectory,
                FailureReason = string.IsNullOrWhiteSpace(failureReason) ? "directory_open_failed" : failureReason
            };
        }

        if (shell.TryOpenDirectoryWithExplorer(normalizedDirectory, out string explorerFailureReason))
        {
            return new ExplorerOpenResult
            {
                Kind = successKind,
                RequestedPath = requestedPath ?? normalizedDirectory,
                OpenedPath = normalizedDirectory
            };
        }

        string reason = CombineFailureReasons(resolveFailureReason, explorerFailureReason);
        reason = string.IsNullOrWhiteSpace(reason) ? "directory_open_failed" : reason;
        LogFailure("open_directory", normalizedDirectory, reason);
        return new ExplorerOpenResult
        {
            Kind = ExplorerOpenResultKind.Failed,
            RequestedPath = requestedPath ?? normalizedDirectory,
            FailureReason = reason
        };
    }

    private static bool TryNormalize(string path, out string normalizedPath, out ExplorerOpenResult result)
    {
        normalizedPath = string.Empty;
        result = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            result = new ExplorerOpenResult
            {
                Kind = ExplorerOpenResultKind.InvalidPath,
                FailureReason = "empty_path"
            };
            return false;
        }

        try
        {
            normalizedPath = LongPathFileSystem.NormalizePathForStorage(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException)
        {
            result = new ExplorerOpenResult
            {
                Kind = ExplorerOpenResultKind.InvalidPath,
                RequestedPath = path,
                FailureReason = "normalize_failed:" + ex.GetType().Name
            };
            return false;
        }
    }

    private static string ResolveFirstShellPathCandidate(string normalizedPath, IExplorerShell shell, out string failureReason)
    {
        failureReason = string.Empty;
        foreach (string candidatePath in CreateShellPathCandidates(normalizedPath))
        {
            if (shell.TryResolvePath(candidatePath, out string candidateFailureReason))
            {
                return candidatePath;
            }
            failureReason = candidateFailureReason;
        }
        return string.Empty;
    }

    private static IEnumerable<string> CreateShellPathCandidates(string normalizedPath)
    {
        string extendedPath;
        try
        {
            extendedPath = LongPathFileSystem.ToExtendedPath(normalizedPath);
        }
        catch
        {
            yield break;
        }

        if (ShouldPreferExtendedPath(normalizedPath) && !string.Equals(normalizedPath, extendedPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return extendedPath;
            yield return normalizedPath;
            yield break;
        }

        yield return normalizedPath;
        if (!string.Equals(normalizedPath, extendedPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return extendedPath;
        }
    }

    private static bool ShouldPreferExtendedPath(string normalizedPath)
    {
        return !string.IsNullOrWhiteSpace(normalizedPath) && normalizedPath.Length >= 260;
    }

    private static void LogFailure(string operation, string path, string reason)
    {
        NLogWrapper.FileLogger?.Warn("explorer_open failed operation=" + operation + " reason=" + (reason ?? string.Empty) + " path=" + (path ?? string.Empty));
    }

    private static string CombineFailureReasons(string primary, string secondary)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            return secondary ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(secondary))
        {
            return primary;
        }
        return primary + "; " + secondary;
    }

    private sealed class ShellExplorerAdapter : IExplorerShell
    {
        internal static ShellExplorerAdapter Instance { get; } = new ShellExplorerAdapter();

        public bool TryResolvePath(string path, out string failureReason)
        {
            failureReason = string.Empty;
            try
            {
                return ShellApi.TryResolvePath(path, out failureReason);
            }
            catch (Exception ex)
            {
                failureReason = "exception:" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        public bool TrySelectFile(string filePath, out string failureReason)
        {
            failureReason = string.Empty;
            try
            {
                return ShellApi.TrySelectFile(filePath, out failureReason);
            }
            catch (Exception ex)
            {
                failureReason = "exception:" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        public bool TryOpenDirectory(string directoryPath, out string failureReason)
        {
            return TryOpenDirectoryWithExplorer(directoryPath, out failureReason);
        }

        public bool TryOpenDirectoryWithExplorer(string directoryPath, out string failureReason)
        {
            return ExternalShellGatewayPolicy.Current.TryOpenDirectoryWithExplorerProcess(directoryPath, out failureReason);
        }
    }

    private static class ShellApi
    {
        private const int S_OK = 0;

        internal static bool TryResolvePath(string path, out string failureReason)
        {
            failureReason = string.Empty;
            IntPtr pidl = IntPtr.Zero;
            try
            {
                int parseResult = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
                if (parseResult == S_OK && pidl != IntPtr.Zero)
                {
                    return true;
                }
                failureReason = "parse_failed_hresult=0x" + parseResult.ToString("X8");
                return false;
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                {
                    ILFree(pidl);
                }
            }
        }

        internal static bool TrySelectFile(string filePath, out string failureReason)
        {
            failureReason = string.Empty;
            IntPtr absolutePidl = IntPtr.Zero;
            IntPtr parentPidl = IntPtr.Zero;
            IntPtr childArray = IntPtr.Zero;
            try
            {
                int parseResult = SHParseDisplayName(filePath, IntPtr.Zero, out absolutePidl, 0, out _);
                if (parseResult != S_OK || absolutePidl == IntPtr.Zero)
                {
                    failureReason = "select_parse_failed_hresult=0x" + parseResult.ToString("X8");
                    return false;
                }

                parentPidl = ILClone(absolutePidl);
                if (parentPidl == IntPtr.Zero || !ILRemoveLastID(parentPidl))
                {
                    failureReason = "select_parent_pidl_failed";
                    return false;
                }

                IntPtr childPidl = ILFindLastID(absolutePidl);
                if (childPidl == IntPtr.Zero)
                {
                    failureReason = "select_child_pidl_failed";
                    return false;
                }

                childArray = Marshal.AllocCoTaskMem(IntPtr.Size);
                Marshal.WriteIntPtr(childArray, childPidl);
                int openResult = SHOpenFolderAndSelectItems(parentPidl, 1, childArray, 0);
                if (openResult == S_OK)
                {
                    return true;
                }
                failureReason = "select_open_failed_hresult=0x" + openResult.ToString("X8");
                return false;
            }
            finally
            {
                if (childArray != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(childArray);
                }
                if (parentPidl != IntPtr.Zero)
                {
                    ILFree(parentPidl);
                }
                if (absolutePidl != IntPtr.Zero)
                {
                    ILFree(absolutePidl);
                }
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHParseDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            IntPtr pbc,
            out IntPtr ppidl,
            uint sfgaoIn,
            out uint psfgaoOut);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr pidlFolder,
            uint cidl,
            IntPtr apidl,
            uint dwFlags);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern IntPtr ILClone(IntPtr pidl);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ILRemoveLastID(IntPtr pidl);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern IntPtr ILFindLastID(IntPtr pidl);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern void ILFree(IntPtr pidl);
    }
}
