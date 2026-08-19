using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Represents one queued drop-install batch and the managed ingress roots that remain owned by the queue.
/// </summary>
internal sealed class DroppedInstallBatchRequest
{
    private const int SourcesUnconsumed = 0;
    private const int SourceAbandonmentReserved = 1;
    private const int SourcesAbandoned = 2;
    private const int SourcesTransferredToInstaller = 3;

    private readonly string[] ownedIngressRoots;
    private readonly Action<string> deleteOwnedIngressRoot;
    private readonly Action<string, Exception> reportCleanupFailure;
    private readonly TaskCompletionSource<bool> dispositionCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int sourceOwnershipState;

    /// <summary>
    /// Gets the durable paths that may be consumed asynchronously by the installer.
    /// </summary>
    internal string[] Paths { get; }

    /// <summary>
    /// Gets the user-provided paths used for status and failure reporting.
    /// </summary>
    internal string[] OriginalPaths { get; }

    internal int PathCount => Paths.Length;

    /// <summary>
    /// Returns a task that completes after this request is transferred to the installer or its
    /// queue-owned ingress roots have been abandoned and cleaned up.
    /// </summary>
    internal Task WaitForDispositionAsync()
    {
        return dispositionCompletion.Task;
    }

    internal string DisplayName { get; }

    internal DroppedInstallBatchRequest(IEnumerable<string> paths)
        : this(paths, paths, [], null, null)
    {
    }

    /// <summary>
    /// Creates a request whose newly acquired ingress roots are deleted only if the queue abandons the batch before installer handoff.
    /// </summary>
    internal DroppedInstallBatchRequest(
        IEnumerable<string> durablePaths,
        IEnumerable<string> originalPaths,
        IEnumerable<string> ownedIngressRoots,
        Action<string> deleteOwnedIngressRoot,
        Action<string, Exception> reportCleanupFailure)
    {
        Paths = [.. (durablePaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        OriginalPaths = [.. (originalPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        this.ownedIngressRoots = [.. (ownedIngressRoots ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        this.deleteOwnedIngressRoot = deleteOwnedIngressRoot;
        this.reportCleanupFailure = reportCleanupFailure;
        DisplayName = GetDisplayName(OriginalPaths.FirstOrDefault() ?? Paths.FirstOrDefault());
    }

    /// <summary>
    /// Transfers source lifetime responsibility to the installer immediately before the installer is invoked.
    /// </summary>
    /// <returns><see langword="true"/> when this call performed the one-way transfer.</returns>
    internal bool TransferSourceOwnershipToInstaller()
    {
        bool transferred = Interlocked.CompareExchange(
            ref sourceOwnershipState,
            SourcesTransferredToInstaller,
            SourcesUnconsumed) == SourcesUnconsumed;
        if (transferred)
        {
            dispositionCompletion.TrySetResult(true);
        }
        return transferred;
    }

    /// <summary>
    /// Linearizes active cancellation against installer handoff without performing cleanup on the caller thread.
    /// </summary>
    /// <returns><see langword="true"/> when cancellation reserved the unconsumed sources first.</returns>
    internal bool TryReserveAbandonmentBeforeInstallerHandoff()
    {
        return Interlocked.CompareExchange(
            ref sourceOwnershipState,
            SourceAbandonmentReserved,
            SourcesUnconsumed) == SourcesUnconsumed;
    }

    /// <summary>
    /// Deletes only ingress roots created for this request when it has not yet reached the installer.
    /// </summary>
    /// <returns><see langword="true"/> when this call performed the one-time abandonment.</returns>
    internal bool TryAbandonUnconsumedSources()
    {
        Interlocked.CompareExchange(
            ref sourceOwnershipState,
            SourceAbandonmentReserved,
            SourcesUnconsumed);
        if (Interlocked.CompareExchange(
                ref sourceOwnershipState,
                SourcesAbandoned,
                SourceAbandonmentReserved) != SourceAbandonmentReserved)
        {
            return false;
        }

        if (deleteOwnedIngressRoot == null)
        {
            dispositionCompletion.TrySetResult(true);
            return true;
        }

        try
        {
            foreach (string root in ownedIngressRoots)
            {
                try
                {
                    deleteOwnedIngressRoot(root);
                }
                catch (Exception exception)
                {
                    try
                    {
                        reportCleanupFailure?.Invoke(root, exception);
                    }
                    catch
                    {
                        // Cleanup reporting must not change cancellation or shutdown semantics.
                    }
                }
            }
        }
        finally
        {
            dispositionCompletion.TrySetResult(true);
        }
        return true;
    }

    private static string GetDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }
}
