using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the durable status lifecycle for custom-folder output.
/// Physical signature calculation remains a capability of the output owner;
/// database access and status comparison do not leak into <see cref="BMSPlaylist"/>.
/// </summary>
internal sealed class PlaylistCustomFolderOutputStatusOwner
{
    private readonly PlaylistPersistenceRepository persistenceRepository;

    private readonly PlaylistCustomFolderOutputOwner outputOwner;

    private readonly Func<CustomFolderOutputSettingsSnapshot> settingsProvider;

    private readonly Action<string> logPerformance;

    internal PlaylistCustomFolderOutputStatusOwner(
        PlaylistPersistenceRepository persistenceRepository,
        PlaylistCustomFolderOutputOwner outputOwner,
        Func<CustomFolderOutputSettingsSnapshot> settingsProvider,
        Action<string> logPerformance)
    {
        this.persistenceRepository = persistenceRepository ?? throw new ArgumentNullException(nameof(persistenceRepository));
        this.outputOwner = outputOwner ?? throw new ArgumentNullException(nameof(outputOwner));
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        this.logPerformance = logPerformance;
    }

    internal Dictionary<int, CustomFolderOutputStatusRow> ReadStatusRows()
    {
        try
        {
            return persistenceRepository.ReadCustomFolderOutputStatusRows();
        }
        catch (Exception ex) when (IsPersistenceFailure(ex))
        {
            logPerformance?.Invoke("playlist_custom_folder_output_status read_failed"
                + " exception=" + QuoteLogValue(ex.GetType().Name)
                + " message=" + QuoteLogValue(ex.Message));
            return [];
        }
    }

    internal void PersistStatuses(
        IEnumerable<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> projections,
        CustomFolderOutputPhysicalSurface physicalSurface = null,
        IEnumerable<string> ownerBoundaryDirectories = null)
    {
        List<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> projectionList = [.. (projections ?? [])
            .Where(projection => projection?.Table?.playlist_id != null)];
        if (projectionList.Count == 0)
        {
            return;
        }

        try
        {
            CustomFolderOutputSettingsSnapshot settings = projectionList
                .Select(projection => projection.Settings)
                .FirstOrDefault(snapshot => snapshot != null)
                ?? settingsProvider();
            if (settings == null)
            {
                throw new InvalidOperationException("Custom-folder output settings provider returned null.");
            }

            PlaylistCustomFolderOutputOwner.CustomFolderOutputPhysicalMtimeSignatureIndex physicalSignatureIndex =
                outputOwner.CreatePhysicalMtimeSignatureIndex(
                    projectionList.Select(projection => projection.OutputDirectory),
                    physicalSurface ?? projectionList.FirstOrDefault(projection => projection.PhysicalSurface != null)?.PhysicalSurface,
                    ownerBoundaryDirectories);
            var rows = new List<CustomFolderOutputStatusRow>();
            foreach (PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection in projectionList)
            {
                BMSTable table = projection.Table;
                if (physicalSignatureIndex.TryGetSignature(projection.OutputDirectory, out string physicalMtimeSignature) != true)
                {
                    continue;
                }

                rows.Add(new CustomFolderOutputStatusRow
                {
                    PlaylistId = table.playlist_id.Value,
                    OutputDirectory = NormalizeStatusPath(projection.OutputDirectory),
                    IsRootFolder = table.is_root_folder ? 1 : 0,
                    IgnoreFolderOutput = (int)table.ignore_folder_output,
                    EntryType = (int)table.entry_type,
                    FolderSortKey = (int)table.folder_sort_key,
                    FolderSortAscending = table.folder_sort_ascending ? 1 : 0,
                    EnableUnsent = settings.EnableDownloadLr2IrScoreAndDetectUnsent ? 1 : 0,
                    HeaderSha256 = table.header_sha256 ?? string.Empty,
                    DataSha256 = table.data_sha256 ?? string.Empty,
                    LastUpdateTicks = table.last_update.Ticks,
                    PhysicalMtimeSignature = physicalMtimeSignature
                });
            }

            persistenceRepository.PersistCustomFolderOutputStatusRows(rows);
        }
        catch (Exception ex) when (IsPersistenceFailure(ex))
        {
            logPerformance?.Invoke("playlist_custom_folder_output_status write_failed"
                + " projectionCount=" + projectionList.Count
                + " exception=" + QuoteLogValue(ex.GetType().Name)
                + " message=" + QuoteLogValue(ex.Message));
        }
    }

    internal void DeleteStatus(BMSTable table)
    {
        if (table?.playlist_id == null)
        {
            return;
        }

        try
        {
            persistenceRepository.DeleteCustomFolderOutputStatus(table.playlist_id);
        }
        catch (Exception ex) when (IsPersistenceFailure(ex))
        {
            logPerformance?.Invoke("playlist_custom_folder_output_status delete_failed"
                + " playlistId=" + table.playlist_id.Value
                + " exception=" + QuoteLogValue(ex.GetType().Name)
                + " message=" + QuoteLogValue(ex.Message));
        }
    }

    internal bool IsConfigCurrent(
        BMSTable table,
        string outputDirectory,
        CustomFolderOutputStatusRow status,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        if (table?.playlist_id == null || status == null)
        {
            return false;
        }

        settings ??= settingsProvider();
        return settings != null
            && status.PlaylistId == table.playlist_id.Value
            && string.Equals(status.OutputDirectory, NormalizeStatusPath(outputDirectory), StringComparison.OrdinalIgnoreCase)
            && status.IsRootFolder == (table.is_root_folder ? 1 : 0)
            && status.IgnoreFolderOutput == (int)table.ignore_folder_output
            && status.EntryType == (int)table.entry_type
            && status.FolderSortKey == (int)table.folder_sort_key
            && status.FolderSortAscending == (table.folder_sort_ascending ? 1 : 0)
            && status.EnableUnsent == (settings.EnableDownloadLr2IrScoreAndDetectUnsent ? 1 : 0)
            && string.Equals(status.HeaderSha256 ?? string.Empty, table.header_sha256 ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(status.DataSha256 ?? string.Empty, table.data_sha256 ?? string.Empty, StringComparison.Ordinal)
            && status.LastUpdateTicks == table.last_update.Ticks;
    }

    internal static bool IsPhysicalCurrent(
        string outputDirectory,
        CustomFolderOutputStatusRow status,
        PlaylistCustomFolderOutputOwner.CustomFolderOutputPhysicalMtimeSignatureIndex physicalSignatureIndex)
    {
        if (status == null
            || physicalSignatureIndex?.TryGetSignature(outputDirectory, out string physicalMtimeSignature) != true)
        {
            return false;
        }

        return string.Equals(status.PhysicalMtimeSignature ?? string.Empty, physicalMtimeSignature, StringComparison.Ordinal);
    }

    internal static string NormalizeStatusPath(string path)
    {
        string normalized = Lr2FolderPath.NormalizeDirectoryPath(path);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
    }

    private static bool IsPersistenceFailure(Exception exception)
    {
        return exception is IOException
            || exception is UnauthorizedAccessException
            || exception is ArgumentException
            || exception is NotSupportedException
            || exception is PathTooLongException
            || exception is SQLiteException;
    }

    private static string QuoteLogValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
