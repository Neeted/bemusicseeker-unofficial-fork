using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Identifies the immutable progress facts emitted while a package source
/// batch is expanded.
/// </summary>
internal enum PackageInstallProgressKind
{
    SourceProcessed,
    ArchiveExtractStarted
}

/// <summary>
/// Immutable package-install progress fact. Producers may publish this value
/// without exposing a callback, dispatcher, or mutable operation state.
/// </summary>
internal readonly record struct PackageInstallProgressUpdate(
    PackageInstallProgressKind Kind,
    string Path,
    int Index,
    int Total)
{
    internal static PackageInstallProgressUpdate SourceProcessed() =>
        new(PackageInstallProgressKind.SourceProcessed, string.Empty, 0, 0);

    internal static PackageInstallProgressUpdate ArchiveExtractStarted(
        string path,
        int index,
        int total) =>
        new(
            PackageInstallProgressKind.ArchiveExtractStarted,
            path ?? string.Empty,
            Math.Max(0, index),
            Math.Max(0, total));
}

/// <summary>
/// Narrow, non-blocking package-install progress producer boundary.
/// Implementations must treat an update as best effort and never use it to
/// change the durable mutation result.
/// </summary>
internal interface IPackageInstallProgressWriter
{
    void TryWrite(PackageInstallProgressUpdate update);
}

/// <summary>
/// Immutable folder auto-rename progress fact.
/// </summary>
internal readonly record struct FolderAutoRenameProgressUpdate(
    int TotalCount,
    int ProcessedCount,
    string CurrentPath)
{
    internal FolderAutoRenameProgressUpdate Normalize()
    {
        int normalizedTotal = Math.Max(0, TotalCount);
        return new FolderAutoRenameProgressUpdate(
            normalizedTotal,
            Math.Max(0, Math.Min(ProcessedCount, normalizedTotal)),
            CurrentPath ?? string.Empty);
    }
}

/// <summary>
/// Narrow, non-blocking folder auto-rename progress producer boundary.
/// </summary>
internal interface IFolderAutoRenameProgressWriter
{
    void TryWrite(FolderAutoRenameProgressUpdate update);
}
