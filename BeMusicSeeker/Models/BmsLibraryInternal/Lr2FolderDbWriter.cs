using System;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal readonly struct Lr2FolderGenerationWriteResult(int upsertedCount, int deletedCount)
{
    public int UpsertedCount { get; } = upsertedCount;

    public int DeletedCount { get; } = deletedCount;

    public bool HasChanges => UpsertedCount > 0 || DeletedCount > 0;
}

internal static class Lr2FolderDbWriter
{
    internal static Lr2FolderGenerationWriteResult ApplySyncPlan(
        LR2SongDBExtended songDb,
        Lr2FolderGenerationSyncPlan plan,
        bool commitTransaction = true)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (plan == null || !plan.HasChanges)
        {
            return new Lr2FolderGenerationWriteResult(0, 0);
        }

        string[] deletePaths = [.. (plan.DeletePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        LR2SongDB.folder[] upsertRows = [.. (plan.UpsertRows ?? [])
            .Where(row => !string.IsNullOrWhiteSpace(row?.path))];
        if (deletePaths.Length == 0 && upsertRows.Length == 0)
        {
            return new Lr2FolderGenerationWriteResult(0, 0);
        }

        int deleted = 0;
        int upserted = 0;
        string savepoint = commitTransaction ? songDb.SaveTransactionPoint() : null;
        try
        {
            foreach (string deletePath in deletePaths)
            {
                deleted += songDb.Delete<LR2SongDB.folder>(deletePath);
            }
            foreach (LR2SongDB.folder row in upsertRows)
            {
                upserted += songDb.InsertOrReplace(row, typeof(LR2SongDB.folder));
            }
            if (commitTransaction)
            {
                songDb.Commit();
            }
            return new Lr2FolderGenerationWriteResult(upserted, deleted);
        }
        catch
        {
            if (commitTransaction)
            {
                try
                {
                    songDb.RollbackTo(savepoint);
                }
                catch (Exception rollbackException)
                {
                    try
                    {
                        Debug.WriteLine(
                            "LR2 folder sync rollback failed: "
                            + rollbackException.GetType().Name
                            + ": "
                            + rollbackException.Message);
                    }
                    catch
                    {
                        // Rollback diagnostics must never replace the original folder write exception.
                    }
                }
            }
            throw;
        }
    }
}
