using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ILibraryFileOperationMutationBoundary
{
    IDisposable EnterMutationSequence();

    IDisposable TryBeginMutation(string operation, bool showMessage);

    bool TryBlockMutation(string operation, bool showMessage);

    IDisposable BeginCollectionMutationScope();
}

/// <summary>
/// Owns the lock ordering and mutation reservation scopes for file operations.
/// Callers receive one disposable scope and never enumerate foreign locks.
/// </summary>
internal sealed class LibraryFileOperationSynchronization
{
    private readonly ILibraryFileOperationMutationBoundary mutationBoundary;

    private readonly ReaderWriterLockSlimWrapper bmsFilesInitializedAll;

    private readonly ReaderWriterLockSlimWrapper bmsFilesInitializedMin;

    private readonly ReaderWriterLockSlimWrapper pendingInstallCharts;

    private readonly ReaderWriterLockSlimWrapper bmsFiles;

    private readonly ReaderWriterLockSlimWrapper songDbInstall;

    internal LibraryFileOperationSynchronization(
        ILibraryFileOperationMutationBoundary mutationBoundary,
        ReaderWriterLockSlimWrapper bmsFilesInitializedAll,
        ReaderWriterLockSlimWrapper bmsFilesInitializedMin,
        ReaderWriterLockSlimWrapper pendingInstallCharts,
        ReaderWriterLockSlimWrapper bmsFiles,
        ReaderWriterLockSlimWrapper songDbInstall)
    {
        this.mutationBoundary = mutationBoundary ?? throw new ArgumentNullException(nameof(mutationBoundary));
        this.bmsFilesInitializedAll = bmsFilesInitializedAll ?? throw new ArgumentNullException(nameof(bmsFilesInitializedAll));
        this.bmsFilesInitializedMin = bmsFilesInitializedMin ?? throw new ArgumentNullException(nameof(bmsFilesInitializedMin));
        this.pendingInstallCharts = pendingInstallCharts ?? throw new ArgumentNullException(nameof(pendingInstallCharts));
        this.bmsFiles = bmsFiles ?? throw new ArgumentNullException(nameof(bmsFiles));
        this.songDbInstall = songDbInstall ?? throw new ArgumentNullException(nameof(songDbInstall));
    }

    internal IDisposable EnterFolderMoveWriteScope()
    {
        return EnterWriteScope(
            "library_folder_move",
            includePendingInstallCharts: true,
            includeSongDbInstall: false,
            showMessage: true,
            includeInitializedAll: false);
    }

    internal IDisposable EnterFolderMoveReadScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => bmsFiles.GetReaderGuard());
    }

    internal IDisposable EnterNormalInvalidExtensionRenameWriteScope()
    {
        return EnterWriteScope(
            "library_invalid_extension_rename",
            includePendingInstallCharts: false,
            includeSongDbInstall: false,
            showMessage: true,
            includeInitializedAll: false);
    }

    internal IDisposable EnterPendingInvalidExtensionRenameWriteScope()
    {
        return AcquireScopes(
            () => mutationBoundary.BeginCollectionMutationScope(),
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => pendingInstallCharts.GetWriterGuard(),
            () => songDbInstall.GetWriterGuard());
    }

    internal IDisposable EnterLibraryChartRemovalWriteScope()
    {
        return EnterWriteScope(
            "library_chart_removal",
            includePendingInstallCharts: true,
            includeSongDbInstall: false,
            showMessage: true,
            includeInitializedAll: false);
    }

    internal IDisposable EnterFixInstallationDirectoryWriteScope()
    {
        return EnterWriteScope(
            nameof(BMSLibrary.FixInstallationDirectoryCharts),
            includePendingInstallCharts: true,
            includeSongDbInstall: false,
            showMessage: true,
            includeInitializedAll: true);
    }

    internal IDisposable EnterMergeWriteScope(long operationId)
    {
        List<IDisposable> existingScopes = [];
        try
        {
            existingScopes.Add(mutationBoundary.EnterMutationSequence());
            IDisposable reservation = mutationBoundary.TryBeginMutation(
                "duplicate_merge_catalog_transition",
                showMessage: true);
            if (reservation == null)
            {
                new CompositeDisposable(existingScopes).Dispose();
                return null;
            }
            existingScopes.Add(reservation);
        }
        catch
        {
            CompositeDisposable.DisposeScopesSafely(existingScopes);
            throw;
        }
        return AcquireScopesWithExisting(
            existingScopes,
            new List<Func<IDisposable>>
            {
                () => mutationBoundary.BeginCollectionMutationScope(),
                () => bmsFilesInitializedMin.GetReaderGuard(),
                () => pendingInstallCharts.GetWriterGuard(),
                () => bmsFiles.GetWriterGuard()
            });
    }

    internal bool TryBlockMutation(string operation, bool showMessage)
        => mutationBoundary.TryBlockMutation(operation, showMessage);

    internal IDisposable EnterWriteScope(
        string operation,
        bool includePendingInstallCharts,
        bool includeSongDbInstall,
        bool showMessage,
        bool includeInitializedAll)
    {
        List<IDisposable> existingScopes = [];
        try
        {
            existingScopes.Add(mutationBoundary.EnterMutationSequence());
            IDisposable reservation = mutationBoundary.TryBeginMutation(operation, showMessage);
            if (reservation == null)
            {
                new CompositeDisposable(existingScopes).Dispose();
                return null;
            }
            existingScopes.Add(reservation);
        }
        catch
        {
            CompositeDisposable.DisposeScopesSafely(existingScopes);
            throw;
        }

        List<Func<IDisposable>> acquisitions = [
            () => mutationBoundary.BeginCollectionMutationScope(),
            () => includeInitializedAll
                ? bmsFilesInitializedAll.GetReaderGuard()
                : bmsFilesInitializedMin.GetReaderGuard()
        ];
        if (includePendingInstallCharts)
        {
            acquisitions.Add(() => pendingInstallCharts.GetWriterGuard());
        }
        acquisitions.Add(() => bmsFiles.GetWriterGuard());
        if (includeSongDbInstall)
        {
            acquisitions.Add(() => songDbInstall.GetWriterGuard());
        }
        return AcquireScopesWithExisting(existingScopes, acquisitions);
    }

    private static IDisposable AcquireScopes(
        params Func<IDisposable>[] acquisitions)
    {
        return AcquireScopesWithExisting(new List<IDisposable>(), acquisitions);
    }

    private static IDisposable AcquireScopesWithExisting(
        IReadOnlyList<IDisposable> existingScopes,
        IReadOnlyList<Func<IDisposable>> acquisitions)
    {
        List<IDisposable> scopes = [.. (existingScopes ?? [])];
        try
        {
            foreach (Func<IDisposable> acquire in acquisitions ?? [])
            {
                scopes.Add(acquire());
            }
            return new CompositeDisposable(scopes);
        }
        catch
        {
            CompositeDisposable.DisposeScopesSafely(scopes);
            throw;
        }
    }

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IReadOnlyList<IDisposable> scopes;

        internal CompositeDisposable(params IDisposable[] scopes)
            : this((IReadOnlyList<IDisposable>)scopes)
        {
        }

        internal CompositeDisposable(IReadOnlyList<IDisposable> scopes)
        {
            this.scopes = scopes ?? [];
        }

        public void Dispose()
        {
            Exception firstException = null;
            for (int index = scopes.Count - 1; index >= 0; index--)
            {
                try
                {
                    scopes[index]?.Dispose();
                }
                catch (Exception exception)
                {
                    firstException ??= exception;
                }
            }
            if (firstException != null)
            {
                throw firstException;
            }
        }

        internal static void DisposeScopesSafely(IEnumerable<IDisposable> scopes)
        {
            try
            {
                new CompositeDisposable([.. (scopes ?? [])]).Dispose();
            }
            catch
            {
                // Preserve the acquisition failure while still attempting every release.
            }
        }
    }
}
