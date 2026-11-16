using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

internal enum DroppedInstallIngressFailureKind
{
    InvalidInput,
    SourceUnavailable,
    UnsafeSource,
    CopyFailed,
    QueueRejected
}

/// <summary>
/// Describes the result of synchronously acquiring borrowed FileDrop paths.
/// </summary>
internal sealed class DroppedInstallIngressAcquisitionResult
{
    private DroppedInstallIngressAcquisitionResult(
        DroppedInstallBatchRequest request,
        DroppedInstallIngressFailureKind? failureKind,
        Exception exception)
    {
        Request = request;
        FailureKind = failureKind;
        Exception = exception;
    }

    internal bool Succeeded => Request != null;

    internal DroppedInstallBatchRequest Request { get; }

    internal DroppedInstallIngressFailureKind? FailureKind { get; }

    internal Exception Exception { get; }

    internal static DroppedInstallIngressAcquisitionResult Success(DroppedInstallBatchRequest request)
    {
        return new DroppedInstallIngressAcquisitionResult(
            request ?? throw new ArgumentNullException(nameof(request)),
            null,
            null);
    }

    internal static DroppedInstallIngressAcquisitionResult Failure(
        DroppedInstallIngressFailureKind failureKind,
        Exception exception)
    {
        return new DroppedInstallIngressAcquisitionResult(
            null,
            failureKind,
            exception ?? throw new ArgumentNullException(nameof(exception)));
    }
}

/// <summary>
/// Materializes external system-temporary FileDrop sources before the WPF Drop callback returns.
/// </summary>
internal sealed class DroppedInstallIngressMaterializer
{
    private readonly string systemTempRoot;
    private readonly Func<string, bool> isCurrentSessionManagedPath;
    private readonly Func<string> createManagedIngressRoot;
    private readonly Action<string> deleteManagedIngressRoot;
    private readonly Action<string, Exception> reportCleanupFailure;
    private readonly Func<string, FileAttributes> getAttributes;

    /// <summary>
    /// Creates an ingress materializer with injectable path ownership boundaries for deterministic tests.
    /// </summary>
    internal DroppedInstallIngressMaterializer(
        string systemTempRoot,
        Func<string, bool> isCurrentSessionManagedPath,
        Func<string> createManagedIngressRoot,
        Action<string> deleteManagedIngressRoot,
        Action<string, Exception> reportCleanupFailure = null,
        Func<string, FileAttributes> getAttributes = null)
    {
        this.systemTempRoot = NormalizeDirectoryPath(systemTempRoot);
        this.isCurrentSessionManagedPath = isCurrentSessionManagedPath
            ?? throw new ArgumentNullException(nameof(isCurrentSessionManagedPath));
        this.createManagedIngressRoot = createManagedIngressRoot
            ?? throw new ArgumentNullException(nameof(createManagedIngressRoot));
        this.deleteManagedIngressRoot = deleteManagedIngressRoot
            ?? throw new ArgumentNullException(nameof(deleteManagedIngressRoot));
        this.reportCleanupFailure = reportCleanupFailure;
        this.getAttributes = getAttributes ?? LongPathFileSystem.GetAttributes;
    }

    /// <summary>
    /// 実体のルート・祖先・子孫を検査してバッチ全体を確保し、永続する要求または分類付き失敗を返します。信頼済みTEMPの子孫だけは起点属性を検査しません。
    /// </summary>
    internal DroppedInstallIngressAcquisitionResult Acquire(IEnumerable<string> paths)
    {
        string ingressRoot = null;
        try
        {
            string[] originals = [.. (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
            if (originals.Length == 0)
            {
                return DroppedInstallIngressAcquisitionResult.Failure(
                    DroppedInstallIngressFailureKind.InvalidInput,
                    new ArgumentException("The drop did not contain any install paths.", nameof(paths)));
            }

            string[] normalizedPaths = originals.Select(LongPathFileSystem.NormalizePathForStorage).ToArray();
            bool[] transient = normalizedPaths.Select(IsExternalTransientSource).ToArray();
            var transientSources = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < normalizedPaths.Length; index++)
            {
                string source = normalizedPaths[index];
                if (string.Equals(
                    NormalizeDirectoryPath(source),
                    systemTempRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnsafeDroppedInstallSourceException("The system temporary root cannot be acquired as a drop source.");
                }

                try
                {
                    FileAttributes attributes = transient[index] || IsUnderSystemTempRoot(source)
                        ? GetTrustedRootDescendantAttributes(source)
                        : GetStableSourceAttributesWithAncestors(source);
                    EnsureSourceTreeIsNotReparsePoints(source, attributes);
                    if (transient[index])
                    {
                        transientSources.TryAdd(
                            LongPathFileSystem.TrimTrailingDirectorySeparators(source),
                            (attributes & FileAttributes.Directory) != 0);
                    }
                }
                catch (Exception exception) when (exception is FileNotFoundException
                    || exception is DirectoryNotFoundException
                    || exception is UnauthorizedAccessException)
                {
                    return SourceUnavailable(source, exception);
                }
            }

            IReadOnlySet<string> fullySelectedDirectories = PackageInstallInputSelection.Capture(normalizedPaths);

            if (!transient.Any(value => value))
            {
                return DroppedInstallIngressAcquisitionResult.Success(
                    new DroppedInstallBatchRequest(normalizedPaths, originals, [], null, reportCleanupFailure, fullySelectedDirectories));
            }

            ingressRoot = NormalizeDirectoryPath(createManagedIngressRoot());
            string[] durablePaths = new string[normalizedPaths.Length];
            var mappedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < normalizedPaths.Length; index++)
            {
                string source = normalizedPaths[index];
                if (!transient[index])
                {
                    durablePaths[index] = source;
                    continue;
                }

                string relativePath = Path.GetRelativePath(systemTempRoot, source);
                ValidateRelativePath(relativePath);
                string destination = LongPathFileSystem.NormalizePathForStorage(
                    Path.Combine(ingressRoot, relativePath));
                EnsureDestinationWithinIngressRoot(destination, ingressRoot);
                string copySource = LongPathFileSystem.TrimTrailingDirectorySeparators(source);
                if (transientSources[copySource]
                    && LongPathFileSystem.IsSameOrDescendantDirectoryPath(ingressRoot, source))
                {
                    throw new UnsafeDroppedInstallSourceException("The managed ingress destination is inside a dropped source directory.");
                }
                durablePaths[index] = destination;
                mappedPaths[copySource] = destination;
            }

            foreach ((string source, bool isDirectory) in GetMinimalCopySources(transientSources))
            {
                string destination = mappedPaths[source];
                if (isDirectory)
                {
                    CopyDirectoryWithoutReparsePoints(source, destination, ingressRoot);
                }
                else
                {
                    EnsureEntryIsNotReparsePoint(source);
                    LongPathFileSystem.CreateDirectory(Path.GetDirectoryName(destination));
                    LongPathFileSystem.CopyFile(source, destination, overwrite: false);
                }
            }

            var request = new DroppedInstallBatchRequest(
                durablePaths,
                originals,
                [ingressRoot],
                deleteManagedIngressRoot,
                reportCleanupFailure,
                fullySelectedDirectories.Select(directory => IsExternalTransientSource(directory)
                    ? LongPathFileSystem.NormalizePathForStorage(Path.Combine(ingressRoot, Path.GetRelativePath(systemTempRoot, directory)))
                    : directory).ToFrozenSet(StringComparer.OrdinalIgnoreCase));
            ingressRoot = null;
            return DroppedInstallIngressAcquisitionResult.Success(request);
        }
        catch (UnsafeDroppedInstallSourceException exception)
        {
            CleanupPartialIngressRoot(ingressRoot);
            return DroppedInstallIngressAcquisitionResult.Failure(
                DroppedInstallIngressFailureKind.UnsafeSource,
                exception);
        }
        catch (Exception exception)
        {
            CleanupPartialIngressRoot(ingressRoot);
            return DroppedInstallIngressAcquisitionResult.Failure(
                DroppedInstallIngressFailureKind.CopyFailed,
                exception);
        }
    }

    private bool IsExternalTransientSource(string normalizedPath)
    {
        return LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(
                NormalizeDirectoryPath(normalizedPath),
                systemTempRoot)
            && !isCurrentSessionManagedPath(normalizedPath);
    }

    private bool IsUnderSystemTempRoot(string normalizedPath)
    {
        return LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(
            NormalizeDirectoryPath(normalizedPath),
            systemTempRoot);
    }

    /// <summary>
    /// 末尾区切りを除いたコピー元と取得済みの種別から、選択された一時ディレクトリに包含されないものだけを返します。
    /// </summary>
    private static IEnumerable<KeyValuePair<string, bool>> GetMinimalCopySources(
        Dictionary<string, bool> sources)
    {
        var directories = sources.Where(source => source.Value)
            .Select(source => source.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, bool> source in sources)
        {
            // ファイル同士は包含しません。全入力の再探索を避け、祖先だけを集合で照合します。
            bool coveredByDirectory = false;
            if (directories.Count != 0)
            {
                string ancestor = Path.GetDirectoryName(source.Key);
                while (!string.IsNullOrEmpty(ancestor))
                {
                    if (directories.Contains(ancestor))
                    {
                        coveredByDirectory = true;
                        break;
                    }
                    ancestor = Path.GetDirectoryName(ancestor);
                }
            }
            if (!coveredByDirectory)
            {
                yield return source;
            }
        }
    }

    private void CopyDirectoryWithoutReparsePoints(
        string source,
        string destination,
        string ingressRoot)
    {
        EnsureEntryIsNotReparsePoint(source);
        EnsureDestinationWithinIngressRoot(destination, ingressRoot);
        LongPathFileSystem.CreateDirectory(destination);

        foreach (string entry in LongPathFileSystem.EnumerateFileSystemEntries(source))
        {
            EnsureEntryIsNotReparsePoint(entry);
            string childDestination = LongPathFileSystem.NormalizePathForStorage(
                Path.Combine(destination, Path.GetFileName(entry)));
            EnsureDestinationWithinIngressRoot(childDestination, ingressRoot);
            if (LongPathFileSystem.DirectoryExists(entry))
            {
                CopyDirectoryWithoutReparsePoints(entry, childDestination, ingressRoot);
            }
            else
            {
                LongPathFileSystem.CopyFile(entry, childDestination, overwrite: false);
            }
        }
    }

    private FileAttributes GetTrustedRootDescendantAttributes(string source)
    {
        string relativePath = Path.GetRelativePath(systemTempRoot, source);
        ValidateRelativePath(relativePath);
        string[] components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = systemTempRoot;
        FileAttributes attributes = default;
        for (int index = 0; index < components.Length; index++)
        {
            current = LongPathFileSystem.NormalizePathForStorage(
                Path.Combine(current, components[index]));
            attributes = getAttributes(current);
            EnsureAttributesAreNotReparsePoint(attributes);
            if (index < components.Length - 1
                && (attributes & FileAttributes.Directory) == 0)
            {
                throw new DirectoryNotFoundException(
                    "A dropped install source ancestor is not a directory: " + current);
            }
        }
        return attributes;
    }

    private FileAttributes GetStableSourceAttributesWithAncestors(string source)
    {
        string root = Path.GetPathRoot(source);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new UnsafeDroppedInstallSourceException("A dropped install source must have a filesystem root.");
        }

        // 実体の起点は相対配置ではありません。ルート自身も一度属性を検査します。
        FileAttributes attributes = getAttributes(root);
        EnsureAttributesAreNotReparsePoint(attributes);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new DirectoryNotFoundException("A dropped install source root is not a directory: " + root);
        }
        string relativePath = Path.GetRelativePath(root, source);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return attributes;
        }
        ValidateRelativePath(relativePath);
        string[] components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        for (int index = 0; index < components.Length; index++)
        {
            current = LongPathFileSystem.NormalizePathForStorage(
                Path.Combine(current, components[index]));
            attributes = getAttributes(current);
            EnsureAttributesAreNotReparsePoint(attributes);
            if (index < components.Length - 1
                && (attributes & FileAttributes.Directory) == 0)
            {
                throw new DirectoryNotFoundException(
                    "A dropped install source ancestor is not a directory: " + current);
            }
        }
        return attributes;
    }

    private void EnsureSourceTreeIsNotReparsePoints(string source, FileAttributes attributes)
    {
        EnsureAttributesAreNotReparsePoint(attributes);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            return;
        }
        foreach (string entry in LongPathFileSystem.EnumerateFileSystemEntries(source))
        {
            FileAttributes entryAttributes = getAttributes(entry);
            EnsureSourceTreeIsNotReparsePoints(entry, entryAttributes);
        }
    }

    private void EnsureEntryIsNotReparsePoint(string path)
    {
        EnsureAttributesAreNotReparsePoint(getAttributes(path));
    }

    private static void EnsureAttributesAreNotReparsePoint(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnsafeDroppedInstallSourceException("Reparse points cannot be acquired as drop install sources.");
        }
    }

    private static DroppedInstallIngressAcquisitionResult SourceUnavailable(
        string source,
        Exception exception = null)
    {
        return DroppedInstallIngressAcquisitionResult.Failure(
            DroppedInstallIngressFailureKind.SourceUnavailable,
            exception ?? new FileNotFoundException(
                "A dropped install source is no longer available.",
                source));
    }

    private static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || string.Equals(relativePath, ".", StringComparison.Ordinal)
            || string.Equals(relativePath, "..", StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnsafeDroppedInstallSourceException("The dropped source cannot be mapped inside the managed ingress root.");
        }
    }

    private static void EnsureDestinationWithinIngressRoot(string destination, string ingressRoot)
    {
        if (!LongPathFileSystem.IsSameOrDescendantDirectoryPath(destination, ingressRoot))
        {
            throw new UnsafeDroppedInstallSourceException("The dropped source maps outside the managed ingress root.");
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return LongPathFileSystem.TrimTrailingDirectorySeparators(
            LongPathFileSystem.NormalizePathForStorage(path));
    }

    private void CleanupPartialIngressRoot(string ingressRoot)
    {
        if (string.IsNullOrWhiteSpace(ingressRoot))
        {
            return;
        }
        try
        {
            deleteManagedIngressRoot(ingressRoot);
        }
        catch (Exception exception)
        {
            try
            {
                reportCleanupFailure?.Invoke(ingressRoot, exception);
            }
            catch
            {
                // The original acquisition failure remains authoritative.
            }
        }
    }

    /// <summary>
    /// Identifies a dropped install source that cannot be copied without crossing the ingress safety boundary.
    /// </summary>
    private sealed class UnsafeDroppedInstallSourceException : IOException
    {
        /// <summary>
        /// Initializes an unsafe-source exception without additional diagnostic context.
        /// </summary>
        internal UnsafeDroppedInstallSourceException()
        {
        }

        /// <summary>
        /// Initializes an unsafe-source exception with the diagnostic message presented by the ingress owner.
        /// </summary>
        /// <param name="message">The message that describes the rejected source.</param>
        internal UnsafeDroppedInstallSourceException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes an unsafe-source exception with a diagnostic message and platform error code.
        /// </summary>
        /// <param name="message">The message that describes the rejected source.</param>
        /// <param name="hresult">The platform error code associated with the rejection.</param>
        internal UnsafeDroppedInstallSourceException(string message, int hresult)
            : base(message, hresult)
        {
        }

        /// <summary>
        /// Initializes an unsafe-source exception with the diagnostic message and originating failure.
        /// </summary>
        /// <param name="message">The message that describes the rejected source.</param>
        /// <param name="innerException">The failure that caused the source to be rejected.</param>
        internal UnsafeDroppedInstallSourceException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
