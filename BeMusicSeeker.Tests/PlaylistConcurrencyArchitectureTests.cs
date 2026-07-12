using System;
using System.IO;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistConcurrencyArchitectureTests
{
    [TestMethod]
    public void ExternalTableRegistration_DoesNotMutateVisibleCollectionInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal async Task<BMSTable> RegistrateExternalTableAsync(BMSTable bMSTable");

        StringAssert.Contains(method, "await AddCommittedBMSTableToVisibleCollectionAsync(bMSTable).ConfigureAwait(false)");
        Assert.IsFalse(
            method.Contains("BMSTables.Add(bMSTable);"),
            "External registration must not call DispatcherCollection.Add while the registration writer lock is held.");
    }

    [TestMethod]
    public void ExternalTableRegistration_DoesNotCommitDatabaseInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal async Task<BMSTable> RegistrateExternalTableAsync(BMSTable bMSTable");
        string writerBlock = ExtractBlockBody(method, "using (rwlockBMSTables.GetWriterGuard())");

        Assert.IsFalse(
            writerBlock.Contains("CommitBMSTable("),
            "External registration writer lock must not cover playlist DB persistence.");
        StringAssert.Contains(method, "CommitBMSTable(bMSTable);");
        StringAssert.Contains(method, "await AddCommittedBMSTableToVisibleCollectionAsync(bMSTable).ConfigureAwait(false)");
    }

    [TestMethod]
    public void ExternalTableBatchRegistration_DoesNotCommitDatabaseInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal async Task<RegisteredExternalTableBatchResult> RegistrateExternalTablesAsync");
        string writerBlock = ExtractBlockBody(method, "using (rwlockBMSTables.GetWriterGuard())");

        Assert.IsFalse(
            writerBlock.Contains("CommitBMSTable("),
            "Batch external registration writer lock must not cover playlist DB persistence.");
        StringAssert.Contains(method, "CommitBMSTable(tableList);");
        StringAssert.Contains(method, "await AddCommittedBMSTablesToVisibleCollectionAsync(tableList).ConfigureAwait(false)");
        StringAssert.Contains(method, "ApplyCachedPlaylistUrlCompletionToTables(tableList, operationReason);");
    }

    [TestMethod]
    public void PlaylistEntryBatchCommit_AcquiresTableWriterLocksBeforeDatabaseTransaction()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal void CommitBMSTablesWithEntriesToDB");
        int tableLockIndex = method.IndexOf("writerGuards.Add(table.ReaderWriterLock.GetWriterGuard())", StringComparison.Ordinal);
        int databaseOpenIndex = method.IndexOf("new LR2SongDBExtended(lr2SongDBPath)", StringComparison.Ordinal);

        Assert.IsTrue(tableLockIndex >= 0, "Batch entry commit must acquire table writer locks explicitly.");
        Assert.IsTrue(databaseOpenIndex >= 0, "Batch entry commit must open the playlist database explicitly.");
        Assert.IsTrue(
            tableLockIndex < databaseOpenIndex,
            "Batch entry commit must keep the existing lock order: table writer lock before playlist DB transaction.");
        StringAssert.Contains(method, ".OrderBy(table => table.playlist_id ?? int.MaxValue)");
    }

    [TestMethod]
    public void PlaylistVisibleCollectionMutations_AreDispatchedThroughDedicatedBoundary()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));

        StringAssert.Contains(source, "InvokeBMSTablesCollectionMutation");
        StringAssert.Contains(source, "InvokeBMSTablesCollectionMutationAsync");
        StringAssert.Contains(source, "GetBMSTablesDispatcher");
    }

    [TestMethod]
    public void PlaylistUrlCompletionSettings_UseDedicatedProviderBoundary()
    {
        string playlistSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string urlCompletionSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.UrlCompletion.cs"));

        Assert.IsFalse(playlistSource.Contains("Settings.Default.EnablePlaylistUrlCompletion"));
        Assert.IsFalse(urlCompletionSource.Contains("Settings.Default."));
        StringAssert.Contains(playlistSource, "IsPlaylistUrlCompletionEnabled()");
        StringAssert.Contains(urlCompletionSource, "GetPlaylistUrlCompletionOptions()");
        StringAssert.Contains(urlCompletionSource, "tsvResult.Snapshot.Candidates");
        StringAssert.Contains(urlCompletionSource, "stellaResult.Snapshot.Candidates");
    }

    [TestMethod]
    public void BeatorajaBmtSettings_UseDedicatedProviderBoundary()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));

        Assert.IsFalse(source.Contains("Settings.Default.EnableBeatorajaBmtOutput"));
        Assert.IsFalse(source.Contains("Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled"));
        Assert.IsFalse(source.Contains("Settings.Default.BeatorajaRootPath"));
        Assert.IsFalse(source.Contains("Settings.Default.BeatorajaBmtTablePath"));
        Assert.IsFalse(source.Contains("Settings.Default.RegisterBeatorajaBmtUrls"));
        Assert.IsFalse(source.Contains("Settings.Default.BeatorajaBmtHashOutputMode"));
        StringAssert.Contains(source, "GetBeatorajaBmtOptions()");
    }

    [TestMethod]
    public void CommittedPlaylistVisibleCollectionReflection_IsNotCanceledAfterDatabaseCommit()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string addMethod = ExtractMethodBody(source, "private async Task AddCommittedBMSTableToVisibleCollectionAsync");
        string invokeAsyncMethod = ExtractMethodBody(source, "private async Task<T> InvokeBMSTablesCollectionMutationAsync<T>");

        Assert.IsFalse(addMethod.Contains("CancellationToken"), "Post-commit visible collection reflection must not accept a cancellation token.");
        Assert.IsFalse(invokeAsyncMethod.Contains("CancellationToken"), "Post-commit dispatcher reflection must not be canceled after DB commit.");
    }

    [TestMethod]
    public void ExternalTableImportContinuation_DoesNotReturnReferenceIndexWorkToUiThread()
    {
        string source = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string drainMethod = ExtractMethodBody(source, "private async Task DrainExternalPlaylistImportQueueAsync");
        string completionMethod = ExtractMethodBody(source, "private void CompleteImportedPlaylistRegistrations");
        string duplicatePreparationMethod = ExtractMethodBody(source, "private List<ExternalPlaylistImportWorkItem> PrepareExternalPlaylistImportRegistrationItems");
        string duplicateSkipMethod = ExtractMethodBody(source, "private static void RecordExternalPlaylistImportDuplicateNameSkip");

        StringAssert.Contains(drainMethod, ".ConfigureAwait(false)");
        StringAssert.Contains(drainMethod, "externalPlaylistImportQueue.DequeueBatch()");
        StringAssert.Contains(drainMethod, "await tables.LoadExternalTableSnapshotsAsync(");
        StringAssert.Contains(drainMethod, "schedulePlaylistUrlCompletionRefresh: false");
        StringAssert.Contains(drainMethod, "await tables.RegistrateExternalTablesAsync(");
        StringAssert.Contains(drainMethod, "catch (PlaylistAlreadyExistsException ex)");
        StringAssert.Contains(drainMethod, "RecordExternalPlaylistImportDuplicateNameSkip(item, ex.PlaylistName, ex, outcomes)");
        StringAssert.Contains(drainMethod, "Playlist_import_progress_phase_check_duplicates");
        StringAssert.Contains(drainMethod, "Playlist_import_progress_phase_update_references");
        StringAssert.Contains(drainMethod, "PlaylistSyncAttemptResult.CreateFailure(item.LoadedTable, item.LoadedTable, item.Uri, referenceUpdateException)");
        StringAssert.Contains(completionMethod, "files.AddReferenceBMSTablesIncremental(tableList);");
        StringAssert.Contains(completionMethod, "QueuePlaylistSummaryRefreshIfVisible(reason ?? \"playlist_registered\", invalidateTableCountCache: true)");
        StringAssert.Contains(duplicatePreparationMethod, "GetExternalPlaylistImportExistingNamesSnapshot()");
        StringAssert.Contains(duplicateSkipMethod, "ExternalPlaylistImportOutcome.SkippedDuplicateName");
        Assert.IsFalse(
            drainMethod.Contains("await tables.RegistrateExternalTableAsync("),
            "Bulk URL import should not serialize external requests through the single-table registration API.");
        Assert.IsFalse(
            completionMethod.Contains("tables.AcquireReaderLockBMSTables();"),
            "Import reference index updates must not hold the playlist collection reader lock while scanning library state.");
    }

    [TestMethod]
    public void BeatorajaTableUrlImport_ParallelizesExternalLoadAndBatchesRegistrationWork()
    {
        string source = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string importMethod = ExtractMethodBody(source, "private async Task ImportBeatorajaTableUrlsAsync");
        string completionMethod = ExtractMethodBody(source, "private void CompleteImportedPlaylistRegistrations");

        StringAssert.Contains(importMethod, "await tables.LoadExternalTableSnapshotsAsync(");
        StringAssert.Contains(importMethod, "schedulePlaylistUrlCompletionRefresh: false");
        StringAssert.Contains(importMethod, "await tables.RegistrateExternalTablesAsync(");
        StringAssert.Contains(importMethod, "tables.CommitBMSTableHeadersToDB(rawUrlChangedTables);");
        StringAssert.Contains(importMethod, "tables.QueueBeatorajaBmtExportForTables(rawUrlChangedTables");
        StringAssert.Contains(completionMethod, "files.AddReferenceBMSTablesIncremental(tableList);");
        StringAssert.Contains(importMethod, "BeatorajaTableUrlImportPostProgressStepCount");
        StringAssert.Contains(importMethod, "Beatoraja_table_url_import_progress_phase_register_playlists");
        StringAssert.Contains(importMethod, "Beatoraja_table_url_import_progress_phase_update_references");
        Assert.IsFalse(
            importMethod.Contains("await tables.RegistrateExternalTableAsync("),
            "beatoraja Table URL import should not serialize external requests through the single-table registration API.");
    }

    [TestMethod]
    public void ReaderWriterLockSlimWrapper_RawLockApiUpdatesObservableCounts()
    {
        var rwlock = new ReaderWriterLockSlimWrapper();

        rwlock.EnterReadLock();
        try
        {
            Assert.AreEqual(1u, rwlock.LockingReadCount);
        }
        finally
        {
            rwlock.ExitReadLock();
        }
        Assert.AreEqual(0u, rwlock.LockingReadCount);

        rwlock.EnterWriteLock();
        try
        {
            Assert.AreEqual(1u, rwlock.LockingWriteCount);
        }
        finally
        {
            rwlock.ExitWriteLock();
        }
        Assert.AreEqual(0u, rwlock.LockingWriteCount);

        Assert.IsTrue(rwlock.TryEnterUpgradeableReadLock(TimeSpan.Zero));
        try
        {
            Assert.AreEqual(1u, rwlock.LockingWriteCount);
        }
        finally
        {
            rwlock.ExitUpgradeableReadLock();
        }
        Assert.AreEqual(0u, rwlock.LockingWriteCount);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        int nameIndex = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.IsTrue(nameIndex >= 0, methodName + " was not found.");
        int braceIndex = source.IndexOf('{', nameIndex);
        Assert.IsTrue(braceIndex >= 0, methodName + " body was not found.");

        int depth = 0;
        for (int i = braceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(braceIndex, i - braceIndex + 1);
                }
            }
        }

        Assert.Fail(methodName + " body was not closed.");
        return string.Empty;
    }

    private static string ExtractBlockBody(string source, string blockHeader)
    {
        int headerIndex = source.IndexOf(blockHeader, StringComparison.Ordinal);
        Assert.IsTrue(headerIndex >= 0, blockHeader + " was not found.");
        int braceIndex = source.IndexOf('{', headerIndex);
        Assert.IsTrue(braceIndex >= 0, blockHeader + " body was not found.");

        int depth = 0;
        for (int i = braceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(braceIndex, i - braceIndex + 1);
                }
            }
        }

        Assert.Fail(blockHeader + " body was not closed.");
        return string.Empty;
    }

    private static string FindRepositoryRoot()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "BeMusicSeeker.sln")))
            {
                return directory;
            }
            DirectoryInfo parent = Directory.GetParent(directory);
            directory = parent == null ? string.Empty : parent.FullName;
        }
        Assert.Fail("Repository root was not found.");
        return string.Empty;
    }
}
