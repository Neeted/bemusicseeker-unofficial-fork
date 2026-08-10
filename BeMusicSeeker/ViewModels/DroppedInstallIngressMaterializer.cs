using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Creates an ingress materializer with injectable path ownership boundaries for deterministic tests.
    /// </summary>
    internal DroppedInstallIngressMaterializer(
        string systemTempRoot,
        Func<string, bool> isCurrentSessionManagedPath,
        Func<string> createManagedIngressRoot,
        Action<string> deleteManagedIngressRoot,
        Action<string, Exception> reportCleanupFailure = null)
    {
        this.systemTempRoot = NormalizeDirectoryPath(systemTempRoot);
        this.isCurrentSessionManagedPath = isCurrentSessionManagedPath
            ?? throw new ArgumentNullException(nameof(isCurrentSessionManagedPath));
        this.createManagedIngressRoot = createManagedIngressRoot
            ?? throw new ArgumentNullException(nameof(createManagedIngressRoot));
        this.deleteManagedIngressRoot = deleteManagedIngressRoot
            ?? throw new ArgumentNullException(nameof(deleteManagedIngressRoot));
        this.reportCleanupFailure = reportCleanupFailure;
    }

    /// <summary>
    /// Acquires a complete batch atomically and returns a durable request or a typed failure.
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
            foreach (string path in normalizedPaths)
            {
                if (!LongPathFileSystem.EntryExists(path))
                {
                    return DroppedInstallIngressAcquisitionResult.Failure(
                        DroppedInstallIngressFailureKind.SourceUnavailable,
                        new FileNotFoundException("A dropped install source is no longer available.", path));
                }
            }

            bool[] transient = normalizedPaths.Select(IsExternalTransientSource).ToArray();
            if (!transient.Any(value => value))
            {
                return DroppedInstallIngressAcquisitionResult.Success(
                    new DroppedInstallBatchRequest(normalizedPaths, originals, [], null, reportCleanupFailure));
            }

            foreach (string source in normalizedPaths.Where((_, index) => transient[index]))
            {
                if (string.Equals(
                    NormalizeDirectoryPath(source),
                    systemTempRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnsafeDroppedInstallSourceException("The system temporary root cannot be acquired as a drop source.");
                }
                EnsureSourceAncestorsAreNotReparsePoints(source);
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
                if (LongPathFileSystem.DirectoryExists(source)
                    && LongPathFileSystem.IsSameOrDescendantDirectoryPath(ingressRoot, source))
                {
                    throw new UnsafeDroppedInstallSourceException("The managed ingress destination is inside a dropped source directory.");
                }
                durablePaths[index] = destination;
                mappedPaths[source] = destination;
            }

            foreach (string source in GetMinimalCopySources(normalizedPaths, transient))
            {
                string destination = mappedPaths[source];
                if (LongPathFileSystem.DirectoryExists(source))
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
                reportCleanupFailure);
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

    private static IEnumerable<string> GetMinimalCopySources(string[] paths, bool[] transient)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < paths.Length; index++)
        {
            if (transient[index])
            {
                unique.Add(paths[index]);
            }
        }

        foreach (string candidate in unique)
        {
            bool coveredByDirectory = unique.Any(other =>
                !string.Equals(candidate, other, StringComparison.OrdinalIgnoreCase)
                && LongPathFileSystem.DirectoryExists(other)
                && LongPathFileSystem.IsSameOrDescendantDirectoryPath(candidate, other));
            if (!coveredByDirectory)
            {
                yield return candidate;
            }
        }
    }

    private static void CopyDirectoryWithoutReparsePoints(
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

    private void EnsureSourceAncestorsAreNotReparsePoints(string source)
    {
        string current = LongPathFileSystem.DirectoryExists(source)
            ? NormalizeDirectoryPath(source)
            : NormalizeDirectoryPath(Path.GetDirectoryName(source));
        while (LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(current, systemTempRoot))
        {
            EnsureEntryIsNotReparsePoint(current);
            if (string.Equals(current, systemTempRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            string parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = NormalizeDirectoryPath(parent);
        }
    }

    private static void EnsureEntryIsNotReparsePoint(string path)
    {
        if ((LongPathFileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnsafeDroppedInstallSourceException("Reparse points cannot be acquired as drop install sources.");
        }
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
