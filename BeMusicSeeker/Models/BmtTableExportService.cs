using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

public sealed class BmtSongHashResolveRequest
{
    public string Md5 { get; set; }

    public string Sha256 { get; set; }

    public string Title { get; set; }
}

public enum BeatorajaBmtHashOutputMode
{
    Original,
    FillMissingMd5Sha256,
    PreferSha256Only
}

internal static class BmtTableExportService
{
    internal const string ManifestFileName = ".bemusicseeker-bmt-manifest";

    private static readonly object ManifestLock = new();

    private const int MaxParallelExportDegree = 4;

    private const int ManifestSchemaVersion = 2;

    private const int BmtExporterVersion = 1;

    internal static BeatorajaBmtHashOutputMode NormalizeHashOutputMode(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out BeatorajaBmtHashOutputMode mode)
            && Enum.IsDefined(typeof(BeatorajaBmtHashOutputMode), mode)
            ? mode
            : BeatorajaBmtHashOutputMode.Original;
    }

    internal sealed class ManifestState
    {
        public int SchemaVersion { get; set; }

        public int ExporterVersion { get; set; }

        public HashSet<string> Files { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ManifestPlaylistEntry> Playlists { get; } = new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal);
    }

    internal class ManagedTableUrlEntry
    {
        public string PlaylistIdentity { get; set; }

        public string FileName { get; set; }

        public string Url { get; set; }

        public string Name { get; set; }
    }

    internal sealed class ManifestPlaylistEntry : ManagedTableUrlEntry
    {
        public string HeaderSha256 { get; set; }

        public string DataSha256 { get; set; }

        public long LastUpdateTicks { get; set; }

        public string ProjectionInputSha256 { get; set; }

        public long BmtLastWriteTimeUtcTicks { get; set; }

        public long BmtLength { get; set; }
    }

    internal sealed class PlaylistExportMetadata
    {
        public string PlaylistIdentity { get; set; }

        public string Url { get; set; }

        public string FileName { get; set; }

        public string Name { get; set; }

        public string HeaderSha256 { get; set; }

        public string DataSha256 { get; set; }

        public long LastUpdateTicks { get; set; }

        public string ProjectionInputSha256 { get; set; }
    }

    internal sealed class ExportPlan
    {
        private readonly HashSet<string> projectionPlaylistIdentities = new(StringComparer.Ordinal);

        internal ExportPlan(ManifestState previousManifest, bool cleanupStaleManagedFiles)
        {
            PreviousManifest = previousManifest ?? new ManifestState();
            CleanupStaleManagedFiles = cleanupStaleManagedFiles;
        }

        internal ManifestState PreviousManifest { get; }

        internal bool CleanupStaleManagedFiles { get; }

        internal HashSet<string> CurrentFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, ManifestPlaylistEntry> CurrentPlaylists { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, PlaylistExportMetadata> MetadataByPlaylistIdentity { get; } = new(StringComparer.Ordinal);

        internal int SkippedWriteCount { get; set; }

        public bool RequiresProjection(PlaylistExportMetadata metadata)
        {
            return metadata != null
                && !string.IsNullOrWhiteSpace(metadata.PlaylistIdentity)
                && projectionPlaylistIdentities.Contains(metadata.PlaylistIdentity);
        }

        internal void AddProjectionTarget(PlaylistExportMetadata metadata)
        {
            if (!string.IsNullOrWhiteSpace(metadata?.PlaylistIdentity))
            {
                projectionPlaylistIdentities.Add(metadata.PlaylistIdentity);
            }
        }
    }

    private sealed class TableDataExportWorkItem
    {
        public int Index { get; set; }

        public string PlaylistIdentity { get; set; }

        public JObject TableData { get; set; }

        public string FileName { get; set; }

        public string TableName { get; set; }
    }

    private sealed class TableDataExportWorkResult
    {
        public TableDataExportWorkItem Item { get; set; }

        public BmtFileState FileState { get; set; }

        public bool WroteFile { get; set; }
    }

    private sealed class BmtFileState
    {
        public long LastWriteTimeUtcTicks { get; set; }

        public long Length { get; set; }
    }

    internal sealed class TableDataProjectionSnapshot
    {
        public string Url { get; set; }

        public string Name { get; set; }

        public string Tag { get; set; }

        public bool IsExternalSync { get; set; }

        public string CompatiblePrefix { get; set; }

        public List<string> FolderNames { get; set; } = [];

        public List<TableEntryProjectionSnapshot> Entries { get; set; } = [];

        public List<string> CourseJsonRows { get; set; } = [];
    }

    internal sealed class TableEntryProjectionSnapshot
    {
        public bool IsRemoved { get; set; }

        public string Folder { get; set; }

        public string Title { get; set; }

        public string Artist { get; set; }

        public string Md5 { get; set; }

        public string Sha256 { get; set; }

        public string Url { get; set; }

        public string UrlDiff { get; set; }

        public List<string> OrgMd5 { get; set; } = [];
    }

    internal sealed class ExportResult
    {
        public List<ManagedTableUrlEntry> PreviousManagedTables { get; } = [];

        public List<ManagedTableUrlEntry> CurrentManagedTables { get; } = [];

        public int WrittenCount { get; internal set; }

        public int SkippedWriteCount { get; internal set; }

        /// <summary>不存在を含む物理ファイルの cleanup 完了数です。失敗・共有参照・manifest は含みません。</summary>
        public int RemovedCount { get; internal set; }

        public bool Changed => WrittenCount > 0 || RemovedCount > 0;

        /// <summary>所有台帳に残した削除失敗を、lock 外の通知へ渡します。</summary>
        internal List<FileOperationFailure> Failures { get; } = [];
    }

    /// <summary>ファイル操作の対象と原因を変更不能な通知事実として保持します。</summary>
    internal sealed record FileOperationFailure(string Path, string Cause);

    internal sealed class SongHashResolution(string md5, string sha256)
    {
        public string Md5 { get; } = md5;

        public string Sha256 { get; } = sha256;
    }

    internal interface ISongHashResolver
    {
        SongHashResolution Resolve(BmtSongHashResolveRequest request);
    }

    /// <summary>所有台帳を検証して出力し、旧ファイルの cleanup 結果を返します。</summary>
    internal static ExportResult ExportTables(string tablePath, IEnumerable<BMSTable> tables, bool cleanupStaleManagedFiles)
    {
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            return new ExportResult();
        }
        LongPathFileSystem.CreateDirectory(tablePath);
        lock (ManifestLock)
        {
            ReadManifest(tablePath);
        }
        var exportedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTable table in tables ?? [])
        {
            string fileName = ExportTable(tablePath, table);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                exportedFiles.Add(fileName);
            }
        }
        return UpdateManifest(tablePath, exportedFiles, new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal), cleanupStaleManagedFiles);
    }

    internal static ExportResult ExportTableDataSet(string tablePath, IEnumerable<JObject> tableDataSet, bool cleanupStaleManagedFiles)
    {
        return ExportTableDataSet(tablePath, (tableDataSet ?? []).Select(tableData => Tuple.Create<string, JObject>(null, tableData)), cleanupStaleManagedFiles, null);
    }

    internal static ExportResult ExportTableDataSet(string tablePath, IEnumerable<Tuple<string, JObject>> tableDataSet, bool cleanupStaleManagedFiles)
    {
        return ExportTableDataSet(tablePath, tableDataSet, cleanupStaleManagedFiles, null);
    }

    internal static ExportResult ExportTableDataSet(string tablePath, IEnumerable<Tuple<string, JObject>> tableDataSet, bool cleanupStaleManagedFiles, Action<int, int, string> progressReporter)
    {
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            return new ExportResult();
        }
        LongPathFileSystem.CreateDirectory(tablePath);
        List<Tuple<string, JObject>> tableDataList = [.. tableDataSet ?? []];
        ExportPlan exportPlan = CreateExportPlan(tablePath, CreateExportMetadataFromTableDataSet(tableDataList), cleanupStaleManagedFiles);
        List<Tuple<string, JObject>> projectionDataList = [.. tableDataList.Where(item =>
        {
            if (string.IsNullOrWhiteSpace(item?.Item1))
            {
                return true;
            }
            return exportPlan.RequiresProjection(ResolveExportMetadata(item.Item1, item.Item2, exportPlan));
        })];
        return ExportTableDataSet(tablePath, projectionDataList, exportPlan, progressReporter);
    }

    internal static ExportPlan CreateExportPlan(string tablePath, IEnumerable<PlaylistExportMetadata> metadataSet, bool cleanupStaleManagedFiles)
    {
        ManifestState previousManifest;
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            previousManifest = new ManifestState();
        }
        else
        {
            LongPathFileSystem.CreateDirectory(tablePath);
            lock (ManifestLock)
            {
                previousManifest = ReadManifest(tablePath);
            }
        }
        var plan = new ExportPlan(previousManifest, cleanupStaleManagedFiles);
        foreach (PlaylistExportMetadata metadata in (metadataSet ?? []).Where(metadata => metadata != null))
        {
            if (!string.IsNullOrWhiteSpace(metadata.PlaylistIdentity))
            {
                plan.MetadataByPlaylistIdentity[metadata.PlaylistIdentity] = metadata;
                if (!string.IsNullOrWhiteSpace(metadata.Url))
                {
                    plan.CurrentPlaylists[metadata.PlaylistIdentity] = CreateManifestPlaylistEntry(metadata, null, null);
                }
            }
            if (ShouldSkipProjection(tablePath, previousManifest, metadata, out ManifestPlaylistEntry previousEntry))
            {
                if (!string.IsNullOrWhiteSpace(previousEntry.FileName))
                {
                    plan.CurrentFiles.Add(previousEntry.FileName);
                }
                plan.CurrentPlaylists[metadata.PlaylistIdentity] = previousEntry;
                plan.SkippedWriteCount++;
            }
            else
            {
                plan.AddProjectionTarget(metadata);
            }
        }
        return plan;
    }

    /// <summary>検証済みの出力計画を適用し、未削除ファイルの所有情報と失敗を保持します。</summary>
    internal static ExportResult ExportTableDataSet(string tablePath, IEnumerable<Tuple<string, JObject>> tableDataSet, ExportPlan exportPlan, Action<int, int, string> progressReporter)
    {
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            return new ExportResult();
        }
        LongPathFileSystem.CreateDirectory(tablePath);
        exportPlan ??= CreateExportPlan(tablePath, CreateExportMetadataFromTableDataSet(tableDataSet), cleanupStaleManagedFiles: true);
        var result = new ExportResult
        {
            SkippedWriteCount = exportPlan.SkippedWriteCount
        };
        var exportedFiles = new HashSet<string>(exportPlan.CurrentFiles, StringComparer.OrdinalIgnoreCase);
        var playlists = new Dictionary<string, ManifestPlaylistEntry>(exportPlan.CurrentPlaylists, StringComparer.Ordinal);
        List<TableDataExportWorkItem> items = CreateExportWorkItems(tableDataSet);
        TableDataExportWorkResult[] workResults = ExportPreparedTableDataSet(tablePath, items, progressReporter);
        foreach (TableDataExportWorkResult workResult in workResults)
        {
            if (workResult?.Item == null || string.IsNullOrWhiteSpace(workResult.Item.FileName))
            {
                continue;
            }
            exportedFiles.Add(workResult.Item.FileName);
            if (!string.IsNullOrWhiteSpace(workResult.Item.PlaylistIdentity))
            {
                PlaylistExportMetadata metadata = ResolveExportMetadata(workResult.Item.PlaylistIdentity, workResult.Item.TableData, exportPlan);
                playlists[workResult.Item.PlaylistIdentity] = CreateManifestPlaylistEntry(
                    metadata,
                    workResult.Item.FileName,
                    workResult.FileState);
            }
            if (workResult.WroteFile)
            {
                result.WrittenCount++;
            }
        }
        ExportResult manifestResult = UpdateManifest(tablePath, exportedFiles, playlists, exportPlan.CleanupStaleManagedFiles);
        result.PreviousManagedTables.Clear();
        result.PreviousManagedTables.AddRange(manifestResult.PreviousManagedTables);
        result.CurrentManagedTables.Clear();
        result.CurrentManagedTables.AddRange(manifestResult.CurrentManagedTables);
        result.RemovedCount = manifestResult.RemovedCount;
        result.Failures.AddRange(manifestResult.Failures);
        return result;
    }

    private static IEnumerable<PlaylistExportMetadata> CreateExportMetadataFromTableDataSet(IEnumerable<Tuple<string, JObject>> tableDataSet)
    {
        foreach (Tuple<string, JObject> item in tableDataSet ?? [])
        {
            PlaylistExportMetadata metadata = CreateExportMetadata(item?.Item1, item?.Item2);
            if (metadata != null)
            {
                yield return metadata;
            }
        }
    }

    private static PlaylistExportMetadata CreateExportMetadata(string playlistIdentity, JObject tableData)
    {
        if (string.IsNullOrWhiteSpace(playlistIdentity) || tableData == null)
        {
            return null;
        }
        string url = tableData.Value<string>("url");
        return new PlaylistExportMetadata
        {
            PlaylistIdentity = playlistIdentity,
            Url = url,
            FileName = GetOutputFileName(url),
            Name = tableData.Value<string>("name") ?? string.Empty
        };
    }

    private static PlaylistExportMetadata ResolveExportMetadata(string playlistIdentity, JObject tableData, ExportPlan exportPlan)
    {
        if (!string.IsNullOrWhiteSpace(playlistIdentity)
            && exportPlan?.MetadataByPlaylistIdentity.TryGetValue(playlistIdentity, out PlaylistExportMetadata metadata) == true)
        {
            return metadata;
        }
        return CreateExportMetadata(playlistIdentity, tableData);
    }

    private static PlaylistExportMetadata ResolveExportMetadata(PlaylistExportMetadata metadata, JObject tableData)
    {
        if (metadata != null)
        {
            return metadata;
        }
        return CreateExportMetadata(null, tableData);
    }

    private static List<TableDataExportWorkItem> CreateExportWorkItems(IEnumerable<Tuple<string, JObject>> tableDataSet)
    {
        int index = 0;
        return [.. (tableDataSet ?? []).Select(item =>
        {
            JObject tableData = item?.Item2;
            string playlistIdentity = item?.Item1;
            return new TableDataExportWorkItem
            {
                Index = index++,
                PlaylistIdentity = playlistIdentity,
                TableData = tableData,
                FileName = GetOutputFileName(tableData),
                TableName = tableData?.Value<string>("name") ?? playlistIdentity ?? string.Empty
            };
        })];
    }

    private static TableDataExportWorkResult[] ExportPreparedTableDataSet(
        string tablePath,
        List<TableDataExportWorkItem> items,
        Action<int, int, string> progressReporter)
    {
        if (items == null || items.Count == 0)
        {
            return [];
        }
        var workResults = new TableDataExportWorkResult[items.Count];
        int parallelDegree = ResolveParallelExportDegree(items);
        if (parallelDegree <= 1)
        {
            ExportPreparedTableDataSetSequential(tablePath, items, workResults, progressReporter);
            return workResults;
        }
        int processedCount = 0;
        object progressLock = new();
        Parallel.ForEach(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = parallelDegree },
            item =>
            {
                TableDataExportWorkResult workResult = ExportPreparedTableDataItem(tablePath, item);
                workResults[item.Index] = workResult;
                lock (progressLock)
                {
                    processedCount++;
                    progressReporter?.Invoke(processedCount, items.Count, item.TableName);
                }
            });
        return workResults;
    }

    private static void ExportPreparedTableDataSetSequential(
        string tablePath,
        List<TableDataExportWorkItem> items,
        TableDataExportWorkResult[] workResults,
        Action<int, int, string> progressReporter)
    {
        int processedCount = 0;
        foreach (TableDataExportWorkItem item in items)
        {
            workResults[item.Index] = ExportPreparedTableDataItem(tablePath, item);
            processedCount++;
            progressReporter?.Invoke(processedCount, items.Count, item.TableName);
        }
    }

    private static TableDataExportWorkResult ExportPreparedTableDataItem(string tablePath, TableDataExportWorkItem item)
    {
        var result = new TableDataExportWorkResult
        {
            Item = item
        };
        if (item == null || string.IsNullOrWhiteSpace(item.FileName))
        {
            return result;
        }
        result.FileState = WriteTableDataFile(tablePath, item.TableData, item.FileName);
        result.WroteFile = true;
        return result;
    }

    private static int ResolveParallelExportDegree(List<TableDataExportWorkItem> items)
    {
        if (items == null || items.Count <= 1 || HasDuplicateOutputFileName(items))
        {
            return 1;
        }
        return Math.Max(1, Math.Min(Math.Min(Environment.ProcessorCount, MaxParallelExportDegree), items.Count));
    }

    private static bool HasDuplicateOutputFileName(IEnumerable<TableDataExportWorkItem> items)
    {
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TableDataExportWorkItem item in items ?? [])
        {
            if (string.IsNullOrWhiteSpace(item?.FileName))
            {
                continue;
            }
            if (!fileNames.Add(item.FileName))
            {
                return true;
            }
        }
        return false;
    }

    internal static string ExportTable(string tablePath, BMSTable table)
    {
        if (string.IsNullOrWhiteSpace(tablePath) || table == null)
        {
            return null;
        }
        JObject tableData = BuildTableData(table);
        if (tableData == null)
        {
            return null;
        }
        return ExportTableData(tablePath, tableData);
    }

    internal static string ExportTableData(string tablePath, JObject tableData)
    {
        return ExportTableData(tablePath, tableData, (PlaylistExportMetadata)null);
    }

    internal static string ExportTableData(string tablePath, JObject tableData, string playlistIdentity)
    {
        PlaylistExportMetadata metadata = CreateExportMetadata(playlistIdentity, tableData);
        return ExportTableData(tablePath, tableData, metadata);
    }

    /// <summary>台帳を検証してから個別出力し、ファイル名を返します。</summary>
    internal static string ExportTableData(string tablePath, JObject tableData, PlaylistExportMetadata metadata)
    {
        return ExportTableData(tablePath, tableData, metadata, out _);
    }

    /// <summary>個別出力の物理操作結果を返し、削除失敗の通知を呼出し元へ委ねます。</summary>
    internal static string ExportTableData(string tablePath, JObject tableData, PlaylistExportMetadata metadata, out ExportResult result)
    {
        result = new ExportResult();
        if (string.IsNullOrWhiteSpace(tablePath) || tableData == null)
        {
            return null;
        }
        string fileName = GetOutputFileName(tableData);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }
        LongPathFileSystem.CreateDirectory(tablePath);
        lock (ManifestLock)
        {
            ManifestState manifest = ReadManifest(tablePath);
            result.PreviousManagedTables.AddRange(manifest.Playlists.Values.Select(CloneManagedTableUrlEntry));
            BmtFileState fileState = WriteTableDataFile(tablePath, tableData, fileName);
            AddManagedFile(tablePath, fileName, ResolveExportMetadata(metadata, tableData), fileState, manifest, result);
            result.WrittenCount = 1;
            result.CurrentManagedTables.AddRange(manifest.Playlists.Values.Select(CloneManagedTableUrlEntry));
        }
        return fileName;
    }

    /// <summary>削除できなかった物理所有情報を残し、完了件数と失敗を返します。</summary>
    internal static ExportResult CleanupManagedFiles(string tablePath)
    {
        var result = new ExportResult();
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            return result;
        }
        lock (ManifestLock)
        {
            ManifestState manifest = ReadManifest(tablePath);
            result.PreviousManagedTables.AddRange(manifest.Playlists.Values.Select(CloneManagedTableUrlEntry));
            bool hadOwnership = manifest.Files.Count > 0 || manifest.Playlists.Count > 0;
            foreach (string fileName in manifest.Files.ToArray())
            {
                CleanupManagedFile(tablePath, fileName, manifest.Files, result);
            }
            if (hadOwnership)
            {
                WriteManifest(tablePath, manifest.Files, null, result);
            }
            if (manifest.Files.Count == 0)
            {
                DeleteFileOrRecordFailure(Path.Combine(tablePath, ManifestFileName), result);
            }
        }
        return result;
    }

    /// <summary>現行 URL 所有権を外し、共有参照と削除失敗の物理台帳を保持します。</summary>
    internal static ExportResult RemoveManagedPlaylist(string tablePath, string playlistIdentity)
    {
        var result = new ExportResult();
        if (string.IsNullOrWhiteSpace(tablePath) || string.IsNullOrWhiteSpace(playlistIdentity))
        {
            return result;
        }
        lock (ManifestLock)
        {
            ManifestState manifest = ReadManifest(tablePath);
            result.PreviousManagedTables.AddRange(manifest.Playlists.Values.Select(CloneManagedTableUrlEntry));
            if (manifest.Playlists.TryGetValue(playlistIdentity, out ManifestPlaylistEntry oldEntry))
            {
                manifest.Playlists.Remove(playlistIdentity);
                if (!string.IsNullOrWhiteSpace(oldEntry.FileName) && !IsManagedFileReferenced(manifest.Playlists, oldEntry.FileName))
                {
                    CleanupManagedFile(tablePath, oldEntry.FileName, manifest.Files, result);
                }
                WriteManifest(tablePath, manifest.Files, manifest.Playlists, result);
            }
            result.CurrentManagedTables.AddRange(manifest.Playlists.Values.Select(CloneManagedTableUrlEntry));
        }
        return result;
    }

    /// <summary>URL-only 所有権へ更新し、未削除ファイルは次の cleanup のために保持します。</summary>
    internal static ExportResult UpdateManagedPlaylistUrlOwnership(string tablePath, PlaylistExportMetadata metadata)
    {
        var result = new ExportResult();
        if (string.IsNullOrWhiteSpace(tablePath)
            || string.IsNullOrWhiteSpace(metadata?.PlaylistIdentity)
            || string.IsNullOrWhiteSpace(metadata.Url))
        {
            return result;
        }
        LongPathFileSystem.CreateDirectory(tablePath);
        lock (ManifestLock)
        {
            ManifestState manifest = ReadManifest(tablePath);
            result.PreviousManagedTables.AddRange(manifest.Playlists.Values.Select(CloneManagedTableUrlEntry));
            ManifestPlaylistEntry newEntry = CreateManifestPlaylistEntry(metadata, null, null);
            bool changed = true;
            if (manifest.Playlists.TryGetValue(metadata.PlaylistIdentity, out ManifestPlaylistEntry oldEntry)
                && IsUrlOwnershipEntryMatch(oldEntry, newEntry))
            {
                changed = false;
            }
            if (changed
                && oldEntry != null
                && !string.IsNullOrWhiteSpace(oldEntry.FileName)
                && !IsManagedFileReferencedExcept(manifest.Playlists, oldEntry.FileName, metadata.PlaylistIdentity))
            {
                CleanupManagedFile(tablePath, oldEntry.FileName, manifest.Files, result);
            }
            if (changed)
            {
                manifest.Playlists[metadata.PlaylistIdentity] = newEntry;
                WriteManifest(tablePath, manifest.Files, manifest.Playlists, result);
            }
            result.CurrentManagedTables.AddRange(manifest.Playlists.Values.Select(CloneManagedTableUrlEntry));
        }
        return result;
    }

    /// <summary>現行 URL 所有権を読みます。欠落以外の読取り・形式不正は呼出し元へ返します。</summary>
    internal static List<ManagedTableUrlEntry> ReadManagedTableUrls(string tablePath)
    {
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            return [];
        }
        lock (ManifestLock)
        {
            return [.. ReadManifest(tablePath).Playlists.Values.Select(CloneManagedTableUrlEntry)];
        }
    }

    internal static PlaylistExportMetadata CreatePlaylistExportMetadata(BMSTable table)
    {
        if (table == null || !table.playlist_id.HasValue)
        {
            return null;
        }
        string url = ResolveTableUrl(table);
        return new PlaylistExportMetadata
        {
            PlaylistIdentity = table.playlist_id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Url = url,
            FileName = GetOutputFileName(url),
            Name = table.name ?? string.Empty,
            HeaderSha256 = table.header_sha256 ?? string.Empty,
            DataSha256 = table.data_sha256 ?? string.Empty,
            LastUpdateTicks = table.last_update.Ticks,
            ProjectionInputSha256 = BuildProjectionInputSha256(table)
        };
    }

    private static string BuildProjectionInputSha256(BMSTable table)
    {
        if (table == null)
        {
            return string.Empty;
        }
        var projectionInput = new JObject
        {
            ["tag"] = ResolveTag(table) ?? string.Empty,
            ["isExternalSync"] = table.is_external_sync,
            ["compatiblePrefix"] = table.compat_prefix ?? string.Empty,
            ["folderOrder"] = new JArray(table.Folder_order ?? []),
            ["courses"] = new JArray((table.Courses ?? [])
                .Select(course => course?.course_json)
                .Where(courseJson => !string.IsNullOrWhiteSpace(courseJson)))
        };
        return BMSTable.ComputeSha256Hex(projectionInput.ToString(Formatting.None));
    }

    internal static JObject BuildTableData(BMSTable table)
    {
        return BuildTableData(table, null);
    }

    internal static JObject BuildTableData(BMSTable table, ISongHashResolver hashResolver)
    {
        return BuildTableData(table, hashResolver, BeatorajaBmtHashOutputMode.FillMissingMd5Sha256);
    }

    internal static JObject BuildTableData(BMSTable table, ISongHashResolver hashResolver, BeatorajaBmtHashOutputMode hashOutputMode)
    {
        return BuildTableData(CreateProjectionSnapshot(table), hashResolver, hashOutputMode);
    }

    internal static JObject BuildTableData(TableDataProjectionSnapshot snapshot, ISongHashResolver hashResolver)
    {
        return BuildTableData(snapshot, hashResolver, BeatorajaBmtHashOutputMode.FillMissingMd5Sha256);
    }

    internal static JObject BuildTableData(TableDataProjectionSnapshot snapshot, ISongHashResolver hashResolver, BeatorajaBmtHashOutputMode hashOutputMode)
    {
        if (snapshot == null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(snapshot.Url) || string.IsNullOrWhiteSpace(snapshot.Name))
        {
            return null;
        }
        JArray folders = BuildFolders(snapshot, hashResolver, hashOutputMode);
        JArray courses = BuildCourses(snapshot);
        if (folders.Count == 0 && courses.Count == 0)
        {
            return null;
        }
        var root = new JObject
        {
            ["url"] = snapshot.Url,
            ["name"] = snapshot.Name ?? string.Empty,
            ["tag"] = snapshot.Tag ?? string.Empty,
            ["folder"] = folders,
            ["course"] = courses
        };
        return root;
    }

    internal static TableDataProjectionSnapshot CreateProjectionSnapshot(BMSTable table)
    {
        if (table == null)
        {
            return null;
        }
        return new TableDataProjectionSnapshot
        {
            Url = ResolveTableUrl(table),
            Name = table.name ?? string.Empty,
            Tag = ResolveTag(table),
            IsExternalSync = table.is_external_sync,
            CompatiblePrefix = table.compat_prefix ?? string.Empty,
            FolderNames = [.. table.folder_list ?? []],
            Entries = [.. (table.entries ?? []).Where(entry => entry != null).Select(CreateEntryProjectionSnapshot)],
            CourseJsonRows = [.. (table.Courses ?? []).Select(course => course?.course_json).Where(courseJson => !string.IsNullOrWhiteSpace(courseJson))]
        };
    }

    private static TableEntryProjectionSnapshot CreateEntryProjectionSnapshot(BMSTableEntry entry)
    {
        return new TableEntryProjectionSnapshot
        {
            IsRemoved = entry.is_removed,
            Folder = entry.folder ?? string.Empty,
            Title = entry.title,
            Artist = entry.artist,
            Md5 = entry.md5,
            Sha256 = entry.sha256,
            Url = entry.url,
            UrlDiff = entry.url_diff,
            OrgMd5 = [.. entry.Org_md5 ?? []]
        };
    }

    private static string ResolveTableUrl(BMSTable table)
    {
        string persistedPageUrl = ResolvePersistedAbsoluteUriText(table?.page_url);
        if (!string.IsNullOrWhiteSpace(persistedPageUrl))
        {
            return persistedPageUrl;
        }
        string persistedHeaderUrl = ResolvePersistedAbsoluteUriText(table?.header_url);
        if (!string.IsNullOrWhiteSpace(persistedHeaderUrl))
        {
            return persistedHeaderUrl;
        }
        Uri uri = table?.Page_url ?? table?.GetAbsoluteHeaderUrl();
        if (uri != null && uri.IsAbsoluteUri)
        {
            return uri.AbsoluteUri;
        }
        if (table.playlist_id.HasValue)
        {
            return "bemusicseeker://playlist/" + table.playlist_id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static string ResolvePersistedAbsoluteUriText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && uri.IsAbsoluteUri ? value : null;
    }

    private static string ResolveTag(BMSTable table)
    {
        if (!string.IsNullOrWhiteSpace(table.tag))
        {
            return table.tag;
        }
        if (!string.IsNullOrWhiteSpace(table.symbol))
        {
            return table.symbol;
        }
        return (table.compat_prefix ?? string.Empty).Trim();
    }

    private static JArray BuildFolders(TableDataProjectionSnapshot snapshot, ISongHashResolver hashResolver, BeatorajaBmtHashOutputMode hashOutputMode)
    {
        JArray folders = [];
        var entriesByFolder = new Dictionary<string, List<TableEntryProjectionSnapshot>>(StringComparer.Ordinal);
        foreach (TableEntryProjectionSnapshot entry in snapshot.Entries ?? [])
        {
            if (entry == null || entry.IsRemoved)
            {
                continue;
            }
            string folderKey = entry.Folder ?? string.Empty;
            if (!entriesByFolder.TryGetValue(folderKey, out List<TableEntryProjectionSnapshot> folderEntries))
            {
                folderEntries = [];
                entriesByFolder[folderKey] = folderEntries;
            }
            folderEntries.Add(entry);
        }
        List<string> folderNames = snapshot.FolderNames ?? [];
        foreach (string folderName in folderNames)
        {
            JArray songs = [];
            if (!entriesByFolder.TryGetValue(folderName ?? string.Empty, out List<TableEntryProjectionSnapshot> entries))
            {
                continue;
            }
            foreach (TableEntryProjectionSnapshot entry in entries)
            {
                JObject song = BuildSong(entry, null, hashResolver, hashOutputMode);
                if (song != null)
                {
                    songs.Add(song);
                }
            }
            if (songs.Count == 0)
            {
                continue;
            }
            folders.Add(new JObject
            {
                ["name"] = ResolveFolderName(snapshot, folderName),
                ["songs"] = songs
            });
        }
        return folders;
    }

    private static string ResolveFolderName(TableDataProjectionSnapshot snapshot, string folderName)
    {
        if (snapshot?.IsExternalSync != true)
        {
            return folderName ?? string.Empty;
        }
        string level = RemoveCompatiblePrefix(folderName ?? string.Empty, snapshot.CompatiblePrefix);
        string tag = snapshot.Tag ?? string.Empty;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return folderName ?? string.Empty;
        }
        return tag + level;
    }

    private static string RemoveCompatiblePrefix(string folderName, string compatiblePrefix)
    {
        string normalizedFolderName = folderName ?? string.Empty;
        return !string.IsNullOrEmpty(compatiblePrefix) && normalizedFolderName.StartsWith(compatiblePrefix, StringComparison.Ordinal)
            ? normalizedFolderName.Substring(compatiblePrefix.Length)
            : normalizedFolderName;
    }

    private static JObject BuildSong(TableEntryProjectionSnapshot entry, JObject sourceChart, ISongHashResolver hashResolver, BeatorajaBmtHashOutputMode hashOutputMode)
    {
        ResolveSongHashes(entry, sourceChart, hashResolver, hashOutputMode, out string md5, out string sha256);
        string title = FirstNonEmpty(entry?.Title, sourceChart?.Value<string>("title"));
        if (string.IsNullOrWhiteSpace(title) || (string.IsNullOrWhiteSpace(md5) && string.IsNullOrWhiteSpace(sha256)))
        {
            return null;
        }
        var song = new JObject
        {
            ["title"] = title
        };
        AddIfNotEmpty(song, "artist", FirstNonEmpty(entry?.Artist, sourceChart?.Value<string>("artist")));
        AddIfNotEmpty(song, "md5", md5?.ToLowerInvariant());
        AddIfNotEmpty(song, "sha256", sha256?.ToLowerInvariant());
        AddIfNotEmpty(song, "url", FirstNonEmpty(entry?.Url, sourceChart?.Value<string>("url")));
        AddIfNotEmpty(song, "appendurl", FirstNonEmpty(entry?.UrlDiff, sourceChart?.Value<string>("url_diff"), sourceChart?.Value<string>("appendurl")));
        AddIfNotEmpty(song, "ipfs", sourceChart?.Value<string>("ipfs"));
        AddIfNotEmpty(song, "appendipfs", FirstNonEmpty(sourceChart?.Value<string>("ipfs_diff"), sourceChart?.Value<string>("appendipfs")));
        JArray orgMd5 = BuildOrgMd5(entry, sourceChart);
        if (orgMd5.Count > 0)
        {
            song["org_md5"] = orgMd5;
        }
        return song;
    }

    private static void ResolveSongHashes(
        TableEntryProjectionSnapshot entry,
        JObject sourceChart,
        ISongHashResolver hashResolver,
        BeatorajaBmtHashOutputMode hashOutputMode,
        out string md5,
        out string sha256)
    {
        string savedMd5 = FirstNonEmpty(entry?.Md5, sourceChart?.Value<string>("md5"));
        string savedSha256 = FirstNonEmpty(entry?.Sha256, sourceChart?.Value<string>("sha256"));
        if (hashOutputMode == BeatorajaBmtHashOutputMode.Original)
        {
            md5 = savedMd5;
            sha256 = savedSha256;
            return;
        }
        SongHashResolution resolvedHashes = hashResolver?.Resolve(CreateSongHashResolveRequest(entry));
        if (hashOutputMode == BeatorajaBmtHashOutputMode.PreferSha256Only)
        {
            sha256 = FirstNonEmpty(savedSha256, resolvedHashes?.Sha256);
            md5 = string.IsNullOrWhiteSpace(sha256) ? savedMd5 : null;
            return;
        }
        md5 = FirstNonEmpty(
            savedMd5,
            string.IsNullOrWhiteSpace(savedMd5) ? resolvedHashes?.Md5 : null);
        sha256 = FirstNonEmpty(
            savedSha256,
            string.IsNullOrWhiteSpace(savedSha256) ? resolvedHashes?.Sha256 : null);
    }

    private static BmtSongHashResolveRequest CreateSongHashResolveRequest(TableEntryProjectionSnapshot entry)
    {
        return entry == null
            ? null
            : new BmtSongHashResolveRequest
            {
                Md5 = entry.Md5,
                Sha256 = entry.Sha256,
                Title = entry.Title
            };
    }

    private static JArray BuildOrgMd5(TableEntryProjectionSnapshot entry, JObject sourceChart)
    {
        JArray values = [];
        foreach (string value in entry?.OrgMd5 ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.ToLowerInvariant());
            }
        }
        AddSourceOrgMd5(values, sourceChart);
        return new JArray(values.Select(item => item.ToString()).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static void AddSourceOrgMd5(JArray values, JObject sourceChart)
    {
        JToken sourceOrg = sourceChart?["org_md5"];
        if (sourceOrg is JArray array)
        {
            foreach (JToken item in array)
            {
                string value = item?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value.ToLowerInvariant());
                }
            }
        }
        else
        {
            string value = sourceOrg?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.ToLowerInvariant());
            }
        }
    }

    private static JArray BuildCourses(TableDataProjectionSnapshot snapshot)
    {
        JArray courses = [];
        foreach (string courseJson in snapshot?.CourseJsonRows ?? [])
        {
            JObject course = ConvertCourse(courseJson);
            if (course != null)
            {
                courses.Add(course);
            }
        }
        return courses;
    }

    private static JObject ConvertCourse(string courseJson)
    {
        if (string.IsNullOrWhiteSpace(courseJson))
        {
            return null;
        }
        JObject source;
        try
        {
            source = JObject.Parse(courseJson);
        }
        catch
        {
            return null;
        }
        JArray songs = [];
        int index = 1;
        foreach (JToken chartToken in EnumerateCourseChartTokens(source))
        {
            JObject song = BuildCourseSong(chartToken, index++);
            if (song != null)
            {
                songs.Add(song);
            }
        }
        if (songs.Count == 0)
        {
            return null;
        }
        var course = new JObject
        {
            ["name"] = FirstNonEmpty(source.Value<string>("name"), "No Course Title"),
            ["hash"] = songs
        };
        JArray constraints = BuildCourseConstraints(source["constraint"] as JArray);
        if (constraints.Count > 0)
        {
            course["constraint"] = constraints;
        }
        JArray trophies = BuildCourseTrophies(source["trophy"] as JArray);
        if (trophies.Count > 0)
        {
            course["trophy"] = trophies;
        }
        if (source["release"] != null && bool.TryParse(source["release"].ToString(), out bool release))
        {
            course["release"] = release;
        }
        return course;
    }

    private static IEnumerable<JToken> EnumerateCourseChartTokens(JObject source)
    {
        JArray charts = source["charts"] as JArray ?? source["hash"] as JArray;
        if (charts != null)
        {
            foreach (JToken chart in charts)
            {
                yield return chart;
            }
            yield break;
        }
        foreach (JToken md5 in source["md5"] as JArray ?? [])
        {
            if (!string.IsNullOrWhiteSpace(md5?.ToString()))
            {
                yield return new JObject
                {
                    ["md5"] = md5.ToString()
                };
            }
        }
        foreach (JToken sha256 in source["sha256"] as JArray ?? [])
        {
            if (!string.IsNullOrWhiteSpace(sha256?.ToString()))
            {
                yield return new JObject
                {
                    ["sha256"] = sha256.ToString()
                };
            }
        }
    }

    private static JObject BuildCourseSong(JToken chartToken, int index)
    {
        JObject chart = ConvertCourseChartToken(chartToken);
        if (chart == null)
        {
            return null;
        }
        string md5 = chart.Value<string>("md5");
        string sha256 = chart.Value<string>("sha256");
        string title = FirstNonEmpty(chart.Value<string>("title"), "course " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(md5) && string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }
        var song = new JObject
        {
            ["title"] = title
        };
        AddIfNotEmpty(song, "artist", chart.Value<string>("artist"));
        AddIfNotEmpty(song, "md5", md5?.ToLowerInvariant());
        AddIfNotEmpty(song, "sha256", sha256?.ToLowerInvariant());
        AddIfNotEmpty(song, "url", chart.Value<string>("url"));
        AddIfNotEmpty(song, "appendurl", FirstNonEmpty(chart.Value<string>("url_diff"), chart.Value<string>("appendurl")));
        AddIfNotEmpty(song, "ipfs", chart.Value<string>("ipfs"));
        AddIfNotEmpty(song, "appendipfs", FirstNonEmpty(chart.Value<string>("ipfs_diff"), chart.Value<string>("appendipfs")));
        return song;
    }

    private static JObject ConvertCourseChartToken(JToken chartToken)
    {
        if (chartToken is JObject chart)
        {
            return chart;
        }
        string hash = chartToken?.ToString();
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }
        JObject chartObject = [];
        if (hash.Length == 64)
        {
            chartObject["sha256"] = hash;
        }
        else
        {
            chartObject["md5"] = hash;
        }
        return chartObject;
    }

    private static JArray BuildCourseConstraints(JArray source)
    {
        JArray constraints = [];
        if (source == null)
        {
            return constraints;
        }
        foreach (JToken item in source)
        {
            string value = ConvertConstraint(item?.ToString());
            if (!string.IsNullOrWhiteSpace(value) && !constraints.Any(token => string.Equals(token.ToString(), value, StringComparison.Ordinal)))
            {
                constraints.Add(value);
            }
        }
        return constraints;
    }

    private static string ConvertConstraint(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "class" or "grade" => "CLASS",
            "mirror" or "grade_mirror" => "MIRROR",
            "random" or "grade_random" => "RANDOM",
            "no_speed" => "NO_SPEED",
            "no_good" => "NO_GOOD",
            "no_great" => "NO_GREAT",
            "gauge_lr2" => "GAUGE_LR2",
            "gauge_5k" => "GAUGE_5KEYS",
            "gauge_7k" => "GAUGE_7KEYS",
            "gauge_9k" => "GAUGE_9KEYS",
            "gauge_24k" => "GAUGE_24KEYS",
            "ln" => "LN",
            "cn" => "CN",
            "hcn" => "HCN",
            _ => null,
        };
    }

    private static JArray BuildCourseTrophies(JArray source)
    {
        JArray trophies = [];
        if (source == null)
        {
            return trophies;
        }
        foreach (JToken item in source)
        {
            if (item is not JObject trophy)
            {
                continue;
            }
            string name = trophy.Value<string>("name");
            double missrate = trophy.Value<double?>("missrate") ?? 0d;
            double scorerate = trophy.Value<double?>("scorerate") ?? 100d;
            if (string.IsNullOrWhiteSpace(name) || missrate <= 0d || scorerate >= 100d)
            {
                continue;
            }
            trophies.Add(new JObject
            {
                ["name"] = name,
                ["missrate"] = missrate,
                ["scorerate"] = scorerate
            });
        }
        return trophies;
    }

    private static ExportResult UpdateManifest(string tablePath, HashSet<string> currentFiles, Dictionary<string, ManifestPlaylistEntry> playlists, bool cleanupStaleManagedFiles)
    {
        var result = new ExportResult();
        lock (ManifestLock)
        {
            ManifestState previous = ReadManifest(tablePath);
            result.PreviousManagedTables.AddRange(previous.Playlists.Values.Select(CloneManagedTableUrlEntry));
            currentFiles ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string fileName in previous.Files.Where(fileName => !currentFiles.Contains(fileName)).ToArray())
            {
                if (cleanupStaleManagedFiles && DeleteFileOrRecordFailure(Path.Combine(tablePath, fileName), result))
                {
                    result.RemovedCount++;
                }
                else
                {
                    currentFiles.Add(fileName);
                }
            }
            result.CurrentManagedTables.AddRange((playlists ?? new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal)).Values.Select(CloneManagedTableUrlEntry));
            WriteManifest(tablePath, currentFiles, playlists, result);
        }
        return result;
    }

    private static string GetOutputFileName(JObject tableData)
    {
        string url = tableData?.Value<string>("url");
        return GetOutputFileName(url);
    }

    private static string GetOutputFileName(string url)
    {
        return string.IsNullOrWhiteSpace(url) ? null : BMSTable.ComputeSha256Hex(url) + ".bmt";
    }

    private static BmtFileState WriteTableDataFile(string tablePath, JObject tableData, string fileName)
    {
        string outputPath = Path.Combine(tablePath, fileName);
        string tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fileStream = LongPathFileSystem.Open(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Compress))
            using (var writer = new StreamWriter(gzipStream, new UTF8Encoding(false)))
            {
                writer.Write(tableData.ToString(Formatting.Indented));
            }
            ReplaceFile(tempPath, outputPath);
            return ReadBmtFileState(outputPath);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static bool ShouldSkipProjection(string tablePath, ManifestState previousManifest, PlaylistExportMetadata metadata, out ManifestPlaylistEntry previousEntry)
    {
        previousEntry = null;
        if (string.IsNullOrWhiteSpace(tablePath)
            || metadata == null
            || string.IsNullOrWhiteSpace(metadata.PlaylistIdentity)
            || string.IsNullOrWhiteSpace(metadata.FileName)
            || !HasReliableNoOpMetadata(metadata)
            || previousManifest?.SchemaVersion != ManifestSchemaVersion
            || previousManifest.ExporterVersion != BmtExporterVersion
            || previousManifest.Playlists.TryGetValue(metadata.PlaylistIdentity, out previousEntry) != true
            || !IsManifestEntryMatch(previousEntry, metadata))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(previousEntry.FileName))
        {
            return true;
        }
        string outputPath = Path.Combine(tablePath, metadata.FileName);
        BmtFileState fileState = ReadBmtFileState(outputPath);
        return fileState != null
            && previousEntry.BmtLength == fileState.Length
            && previousEntry.BmtLastWriteTimeUtcTicks == fileState.LastWriteTimeUtcTicks;
    }

    private static bool HasReliableNoOpMetadata(PlaylistExportMetadata metadata)
    {
        return metadata != null
            && !string.IsNullOrWhiteSpace(metadata.ProjectionInputSha256)
            && (metadata.LastUpdateTicks > 0
                || !string.IsNullOrWhiteSpace(metadata.HeaderSha256)
                || !string.IsNullOrWhiteSpace(metadata.DataSha256));
    }

    private static bool IsManifestEntryMatch(ManifestPlaylistEntry entry, PlaylistExportMetadata metadata)
    {
        return entry != null
            && metadata != null
            && (string.IsNullOrWhiteSpace(entry.FileName)
                || string.Equals(entry.FileName, metadata.FileName, StringComparison.OrdinalIgnoreCase))
            && string.Equals(entry.Url, metadata.Url ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(entry.Name, metadata.Name ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(entry.HeaderSha256, metadata.HeaderSha256 ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(entry.DataSha256, metadata.DataSha256 ?? string.Empty, StringComparison.Ordinal)
            && entry.LastUpdateTicks == metadata.LastUpdateTicks
            && string.Equals(entry.ProjectionInputSha256, metadata.ProjectionInputSha256 ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool IsUrlOwnershipEntryMatch(ManifestPlaylistEntry left, ManifestPlaylistEntry right)
    {
        return left != null
            && right != null
            && string.IsNullOrWhiteSpace(left.FileName)
            && string.IsNullOrWhiteSpace(right.FileName)
            && string.Equals(left.Url, right.Url, StringComparison.Ordinal)
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.HeaderSha256, right.HeaderSha256, StringComparison.Ordinal)
            && string.Equals(left.DataSha256, right.DataSha256, StringComparison.Ordinal)
            && left.LastUpdateTicks == right.LastUpdateTicks
            && string.Equals(left.ProjectionInputSha256, right.ProjectionInputSha256, StringComparison.Ordinal);
    }

    private static void AddManagedFile(string tablePath, string fileName, PlaylistExportMetadata metadata, BmtFileState fileState, ManifestState manifest, ExportResult result)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.PlaylistIdentity)
            && manifest.Playlists.TryGetValue(metadata.PlaylistIdentity, out ManifestPlaylistEntry oldEntry)
            && !string.IsNullOrWhiteSpace(oldEntry.FileName)
            && !string.Equals(oldEntry.FileName, fileName, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsManagedFileReferencedExcept(manifest.Playlists, oldEntry.FileName, metadata.PlaylistIdentity))
            {
                CleanupManagedFile(tablePath, oldEntry.FileName, manifest.Files, result);
            }
        }
        manifest.Files.Add(fileName);
        if (!string.IsNullOrWhiteSpace(metadata?.PlaylistIdentity))
        {
            manifest.Playlists[metadata.PlaylistIdentity] = CreateManifestPlaylistEntry(metadata, fileName, fileState);
        }
        WriteManifest(tablePath, manifest.Files, manifest.Playlists, result);
    }

    private static bool IsManagedFileReferenced(IDictionary<string, ManifestPlaylistEntry> playlists, string fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && (playlists ?? new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal)).Values
                .Any(entry => string.Equals(entry?.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsManagedFileReferencedExcept(IDictionary<string, ManifestPlaylistEntry> playlists, string fileName, string exceptPlaylistIdentity)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && (playlists ?? new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal))
                .Where(item => !string.Equals(item.Key, exceptPlaylistIdentity, StringComparison.Ordinal))
                .Any(item => string.Equals(item.Value?.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static BmtFileState ReadBmtFileState(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !LongPathFileSystem.FileExists(path))
        {
            return null;
        }
        LongPathFileSystem.FileMetadata fileInfo = LongPathFileSystem.GetFileMetadata(path);
        return new BmtFileState
        {
            LastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks,
            Length = fileInfo.Length
        };
    }

    private static ManifestState ReadManifest(string tablePath)
    {
        string manifestPath = Path.Combine(tablePath, ManifestFileName);
        string contents;
        try
        {
            // Exists はアクセス拒否も false にするため、欠落と読取り失敗を区別できない。
            contents = ReadAllText(manifestPath, Encoding.UTF8);
        }
        catch (FileNotFoundException) { return new ManifestState(); }
        catch (DirectoryNotFoundException) { return new ManifestState(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"BMT manifest read failed: {manifestPath}", exception);
        }
        try
        {
            using var reader = new JsonTextReader(new StringReader(contents)) { DateParseHandling = DateParseHandling.None };
            var manifest = JObject.Load(reader, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
            while (reader.Read())
                if (reader.TokenType != JsonToken.Comment)
                    throw new InvalidDataException("Unexpected content after manifest object.");
            var state = new ManifestState
            {
                SchemaVersion = checked((int)ReadOptionalInteger(manifest, "schemaVersion")),
                ExporterVersion = checked((int)ReadOptionalInteger(manifest, "exporterVersion"))
            };
            if (manifest["schemaVersion"]?.Type == JTokenType.Null || state.SchemaVersion is < 0 or > ManifestSchemaVersion)
                throw new InvalidDataException("Unsupported manifest schemaVersion.");
            if (manifest["files"] is not JArray files)
                throw new InvalidDataException("Manifest files must be an array.");
            foreach (JToken item in files)
                state.Files.Add(ReadManagedFileName(item, allowEmpty: false));
            if (manifest["playlists"] is JToken playlistToken)
            {
                if (playlistToken is not JObject playlists)
                    throw new InvalidDataException("Manifest playlists must be an object.");
                foreach (JProperty property in playlists.Properties())
                {
                    if (string.IsNullOrWhiteSpace(property.Name) || property.Value is not JObject value)
                        throw new InvalidDataException("Invalid manifest playlist entry.");
                    string url = ReadOptionalString(value, "url");
                    if (string.IsNullOrWhiteSpace(url))
                        throw new InvalidDataException("Manifest playlist URL is required.");
                    string fileName = ReadManagedFileName(value["file"], allowEmpty: true);
                    ReadOptionalString(value, "contentHash"); // 旧 cache field は型だけ検証し、no-op 根拠にはしない。
                    state.Playlists.Add(property.Name, new ManifestPlaylistEntry
                    {
                        PlaylistIdentity = property.Name,
                        FileName = fileName,
                        Url = url,
                        Name = ReadOptionalString(value, "name"),
                        HeaderSha256 = ReadOptionalString(value, "headerSha256"),
                        DataSha256 = ReadOptionalString(value, "dataSha256"),
                        LastUpdateTicks = ReadOptionalInteger(value, "lastUpdateTicks"),
                        ProjectionInputSha256 = ReadOptionalString(value, "projectionInputSha256"),
                        BmtLastWriteTimeUtcTicks = ReadOptionalInteger(value, "bmtLastWriteTimeUtcTicks"),
                        BmtLength = ReadOptionalInteger(value, "bmtLength")
                    });
                    // 旧形式の playlist 参照も物理所有台帳の一部である。
                    if (fileName.Length > 0)
                        state.Files.Add(fileName);
                }
            }
            return state;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or OverflowException or FormatException)
        {
            throw new InvalidDataException($"Invalid BMT manifest: {manifestPath}", exception);
        }
    }

    private static string ReadOptionalString(JObject value, string name)
    {
        JToken token = value[name];
        if (token == null || token.Type == JTokenType.Null)
            return string.Empty;
        if (token.Type != JTokenType.String)
            throw new InvalidDataException($"Manifest {name} must be a string.");
        return token.Value<string>();
    }

    private static long ReadOptionalInteger(JObject value, string name)
    {
        JToken token = value[name];
        if (token == null || token.Type == JTokenType.Null)
            return 0L;
        if (token.Type != JTokenType.Integer)
            throw new InvalidDataException($"Manifest {name} must be an integer.");
        return token.Value<long>();
    }

    private static string ReadManagedFileName(JToken token, bool allowEmpty)
    {
        if (allowEmpty && (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.String && token.Value<string>() == string.Empty))
            return string.Empty;
        if (token?.Type != JTokenType.String)
            throw new InvalidDataException("Manifest file must be a BMT basename.");
        string name = token.Value<string>();
        if (string.IsNullOrWhiteSpace(name)
            || !name.EndsWith(".bmt", StringComparison.OrdinalIgnoreCase)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(name)
            || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
            throw new InvalidDataException("Manifest file must be a safe BMT basename.");
        return name;
    }
    private static void WriteManifest(string tablePath, IEnumerable<string> fileNames, IDictionary<string, ManifestPlaylistEntry> playlists, ExportResult result)
    {
        var manifest = new JObject
        {
            ["schemaVersion"] = ManifestSchemaVersion,
            ["exporterVersion"] = BmtExporterVersion,
            ["files"] = new JArray((fileNames ?? []).Where(fileName => !string.IsNullOrWhiteSpace(fileName)).OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase))
        };
        if (playlists != null && playlists.Count > 0)
        {
            JObject playlistJson = [];
            foreach (KeyValuePair<string, ManifestPlaylistEntry> item in playlists.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(item.Key) && item.Value != null && !string.IsNullOrWhiteSpace(item.Value.Url))
                {
                    var playlistEntry = new JObject
                    {
                        ["url"] = item.Value.Url,
                        ["name"] = item.Value.Name ?? string.Empty,
                        ["headerSha256"] = item.Value.HeaderSha256 ?? string.Empty,
                        ["dataSha256"] = item.Value.DataSha256 ?? string.Empty,
                        ["lastUpdateTicks"] = item.Value.LastUpdateTicks,
                        ["projectionInputSha256"] = item.Value.ProjectionInputSha256 ?? string.Empty,
                        ["bmtLastWriteTimeUtcTicks"] = item.Value.BmtLastWriteTimeUtcTicks,
                        ["bmtLength"] = item.Value.BmtLength
                    };
                    if (!string.IsNullOrWhiteSpace(item.Value.FileName))
                    {
                        playlistEntry["file"] = item.Value.FileName;
                    }
                    playlistJson[item.Key] = playlistEntry;
                }
            }
            manifest["playlists"] = playlistJson;
        }
        string manifestPath = Path.Combine(tablePath, ManifestFileName);
        string tempPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteAllText(tempPath, manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
            ReplaceFile(tempPath, manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"BMT manifest publish failed: {manifestPath}"
                + string.Concat(result.Failures.Select(failure => Environment.NewLine + failure.Path + ": " + failure.Cause)), exception);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static ManifestPlaylistEntry CreateManifestPlaylistEntry(PlaylistExportMetadata metadata, string fileName, BmtFileState fileState)
    {
        return new ManifestPlaylistEntry
        {
            PlaylistIdentity = metadata?.PlaylistIdentity,
            FileName = fileName,
            Url = metadata?.Url ?? string.Empty,
            Name = metadata?.Name ?? string.Empty,
            HeaderSha256 = metadata?.HeaderSha256 ?? string.Empty,
            DataSha256 = metadata?.DataSha256 ?? string.Empty,
            LastUpdateTicks = metadata?.LastUpdateTicks ?? 0L,
            ProjectionInputSha256 = metadata?.ProjectionInputSha256 ?? string.Empty,
            BmtLastWriteTimeUtcTicks = fileState?.LastWriteTimeUtcTicks ?? 0L,
            BmtLength = fileState?.Length ?? 0L
        };
    }

    private static ManagedTableUrlEntry CloneManagedTableUrlEntry(ManagedTableUrlEntry entry)
    {
        return new ManagedTableUrlEntry
        {
            PlaylistIdentity = entry?.PlaylistIdentity,
            FileName = entry?.FileName,
            Url = entry?.Url,
            Name = entry?.Name
        };
    }

    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        LongPathFileSystem.MoveFile(sourcePath, destinationPath, overwrite: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && LongPathFileSystem.FileExists(path))
            {
                LongPathFileSystem.DeleteFile(path);
            }
        }
        catch
        {
        }
    }

    private static void CleanupManagedFile(string tablePath, string fileName, HashSet<string> files, ExportResult result)
    {
        if (DeleteFileOrRecordFailure(Path.Combine(tablePath, fileName), result))
        {
            files.Remove(fileName);
            result.RemovedCount++;
        }
    }

    private static bool DeleteFileOrRecordFailure(string path, ExportResult result)
    {
        try
        {
            // DeleteFile 自身が不存在を成功として扱う。Exists の false を成功に読み替えない。
            LongPathFileSystem.DeleteFile(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.Failures.Add(new FileOperationFailure(path, exception.Message));
            return false;
        }
    }

    private static string ReadAllText(string path, Encoding encoding)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteAllText(string path, string contents, Encoding encoding)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(contents);
    }

    private static void AddIfNotEmpty(JObject obj, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            obj[name] = value;
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
