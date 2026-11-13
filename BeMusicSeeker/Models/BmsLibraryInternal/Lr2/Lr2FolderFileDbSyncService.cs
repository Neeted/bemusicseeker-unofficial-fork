using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderFileSyncItem
{
    public string FilePath { get; set; }

    public string DatabasePath { get; set; }

    public Lr2FolderFileDefinition Definition { get; set; }

    public DateTime? LastWriteTimeUtc { get; set; }

    public int FolderType { get; set; } = 2;

    public string ParentHash { get; set; }

    public bool PreserveExistingRowOnly { get; set; }
}

internal sealed class Lr2FolderFileDbSyncRequest
{
    public IReadOnlyCollection<Lr2FolderFileSyncItem> Items { get; set; } = [];

    public IReadOnlyCollection<string> ScopeDirectories { get; set; } = [];

    /// <summary>同じ物理対象の旧表記を取得する追加prefix。生成・削除判定には使わず、既存のScopeDirectoriesと管理除外を維持します。</summary>
    public IReadOnlyCollection<string> AdditionalReadDirectories { get; set; } = [];

    public IReadOnlyCollection<string> DirectoryRowScopeDirectories { get; set; } = [];

    public IReadOnlyCollection<string> DirectoryRowGenerationScopeDirectories { get; set; } = [];

    /// <summary>生成停止境界と区別する、今回の階層の実ルート集合です。</summary>
    public IReadOnlyCollection<string> DirectoryRowRootDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ScopePaths { get; set; } = [];

    public IReadOnlyCollection<string> PruneExcludedDirectories { get; set; } = [];

    public IReadOnlyCollection<string> PruneExcludedPaths { get; set; } = [];

    public Func<string, Lr2FolderDirectoryMetadata> DirectoryMetadataResolver { get; set; }

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public bool AllowPrune { get; set; }

    public bool ScopeReadLr2FolderRowsOnly { get; set; }

    public bool UpdateParentDirectoryRowsForPreservedItems { get; set; } = true;

    /// <summary>
    /// 指定時は、項目の完全一致キーの行がなく、条件の追加譜面のsong.adddateが新曲期間内の場合だけ項目を書き込みます。
    /// 局所同期の確定後に組込みの新曲フォルダを作る経路だけが指定します。条件を満たさない場合は何も書き込みません。
    /// 書き込む場合は通常の組込みfolderの書込みと同じく、同じファイルの旧版の絶対パスのキーの行を新しいキーの行へ置き換えて退役させ、
    /// 親directoryの行を整えます。整理（prune）は行いません。
    /// </summary>
    public Lr2BuiltinNewSongFolderCreationCondition CreateOnlyWhenMissingForRecentSongs { get; set; }
}

/// <summary>
/// 局所同期の確定後に組込みの新曲フォルダ行を作る条件です。今回追加したBMSの完全一致パスだけでsong.adddateを読み、全譜面を走査しません。
/// </summary>
/// <param name="addedSongPaths">今回追加したBMSの保存パス。移動・改名で追加日時を引き継いだ譜面は含めません。</param>
/// <param name="config">操作で捕捉したLR2設定。titleflashの期間に使います。</param>
/// <param name="nowUtc">期間判定の基準時刻。</param>
/// <param name="lr2RootPath">現在のLR2root。旧版の絶対パスのキーを作るために使います。空の場合は相対キーだけを照会します。</param>
internal sealed class Lr2BuiltinNewSongFolderCreationCondition(
    IReadOnlyCollection<string> addedSongPaths,
    Lr2ConfigSnapshot config,
    DateTime nowUtc,
    string lr2RootPath)
{
    /// <summary>組込みの新曲フォルダのLR2root相対のfolderキーです。</summary>
    internal static readonly string RelativeFolderKey = Path.Combine("LR2files", "CustomFolder", "newsong.lr2folder");

    public IReadOnlyCollection<string> AddedSongPaths { get; } = addedSongPaths ?? [];

    public Lr2ConfigSnapshot Config { get; } = config;

    public DateTime NowUtc { get; } = nowUtc;

    public string Lr2RootPath { get; } = lr2RootPath;

    /// <summary>
    /// 既存行の事前照会に使うfolderキーです。相対キーと、現在のLR2rootで作る旧版の絶対パスのキーを返します。
    /// </summary>
    internal IReadOnlyList<string> CreateExistingRowKeys()
    {
        var keys = new List<string> { RelativeFolderKey };
        if (!string.IsNullOrWhiteSpace(Lr2RootPath))
        {
            try
            {
                keys.Add(Path.GetFullPath(Path.Combine(Lr2RootPath, RelativeFolderKey)));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                // 絶対キーを作れないLR2rootでは、相対キーだけを照会します。
            }
        }
        return keys;
    }
}

internal sealed class Lr2FolderFileDbSyncResult(
    int itemCount,
    int existingReadCount,
    int existingExactReadCount,
    int existingScopeReadCount,
    int generatedCount,
    int upsertedCount,
    int deletedCount,
    int preservedCount,
    int skippedUnsupportedPathCount,
    int skippedMissingMetadataCount,
    long elapsedMs)
{
    public int ItemCount { get; } = itemCount;

    public int ExistingReadCount { get; } = existingReadCount;

    public int ExistingExactReadCount { get; } = existingExactReadCount;

    public int ExistingScopeReadCount { get; } = existingScopeReadCount;

    public int GeneratedCount { get; } = generatedCount;

    public int UpsertedCount { get; } = upsertedCount;

    public int DeletedCount { get; } = deletedCount;

    public int PreservedCount { get; } = preservedCount;

    public int SkippedUnsupportedPathCount { get; } = skippedUnsupportedPathCount;

    public int SkippedMissingMetadataCount { get; } = skippedMissingMetadataCount;

    public long ElapsedMs { get; } = elapsedMs;

    public bool HasChanges => UpsertedCount > 0 || DeletedCount > 0;
}

internal sealed class Lr2FolderExistingRowsReadResult(
    List<LR2SongDB.folder> rows,
    int exactReadCount,
    int scopeReadCount)
{
    public List<LR2SongDB.folder> Rows { get; } = rows ?? [];

    public int ExactReadCount { get; } = exactReadCount;

    public int ScopeReadCount { get; } = scopeReadCount;
}

internal static class Lr2FolderFileDbSyncService
{
    private static readonly IEqualityComparer<string> PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>customと必要親のraw完全一致キーを同期します。別キーへの置換は削除・新規生成とし、adddateは完全一致の存続行だけ保持します。</summary>
    internal static Lr2FolderFileDbSyncResult Sync(
        LR2SongDBExtended songDb,
        Lr2FolderFileDbSyncRequest request,
        bool commitTransaction = true)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        request ??= new Lr2FolderFileDbSyncRequest();
        var stopwatch = Stopwatch.StartNew();
        Lr2FolderExistingRowsReadResult existingRows = ReadExistingRows(songDb, request);
        List<LR2SongDB.folder> existingRowList = existingRows.Rows;
        Dictionary<string, LR2SongDB.folder> existingRowsByPath = CreateExistingRowMap(existingRowList);
        if (request.CreateOnlyWhenMissingForRecentSongs != null
            && !ShouldCreateMissingItemsForRecentSongs(songDb, request, existingRowsByPath))
        {
            stopwatch.Stop();
            return new Lr2FolderFileDbSyncResult(
                request.Items?.Count ?? 0,
                existingRowList.Count,
                existingRows.ExactReadCount,
                existingRows.ScopeReadCount,
                generatedCount: 0,
                upsertedCount: 0,
                deletedCount: 0,
                preservedCount: 0,
                skippedUnsupportedPathCount: 0,
                skippedMissingMetadataCount: 0,
                stopwatch.ElapsedMilliseconds);
        }
        var generatedPaths = new HashSet<string>(StringComparer.Ordinal);
        var generatedParentDirectoryKeys = new HashSet<string>(StringComparer.Ordinal);
        var upsertRows = new List<LR2SongDB.folder>();
        var replaceDeletePaths = new List<string>();
        int itemCount = 0;
        int generatedCount = 0;
        int preservedCount = 0;
        int skippedUnsupportedPathCount = 0;
        int skippedMissingMetadataCount = 0;
        IReadOnlyCollection<string> directoryRowGenerationScopeDirectories =
            request.DirectoryRowGenerationScopeDirectories?.Count > 0
                ? request.DirectoryRowGenerationScopeDirectories
                : request.DirectoryRowScopeDirectories;
        var directoryRowGenerationScopeMatcher =
            DirectoryScopeMatcher.Create(directoryRowGenerationScopeDirectories, request.DirectoryRowRootDirectories);

        foreach (Lr2FolderFileSyncItem item in request.Items ?? [])
        {
            if (item == null)
            {
                continue;
            }

            itemCount++;
            string databasePath = NormalizeFilePath(item.DatabasePath ?? item.FilePath);
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                skippedUnsupportedPathCount++;
                continue;
            }
            existingRowsByPath.TryGetValue(databasePath, out LR2SongDB.folder existingRow);
            string physicalPath = NormalizeFilePath(item.FilePath);
            LR2SongDB.folder physicalRow = physicalPath != null && existingRowsByPath.TryGetValue(physicalPath, out LR2SongDB.folder found)
                ? found : null;
            // 旧absoluteは削除事実だけに使います。値は新databaseキーに一致する行だけから保持します。
            string replaceDeletePath = !string.Equals(physicalPath, databasePath, StringComparison.Ordinal)
                ? physicalRow?.path : null;
            if (item.FolderType == 1 || IsNormalDirectoryRow(existingRow) || IsNormalDirectoryRow(physicalRow))
            {
                skippedUnsupportedPathCount++;
                continue;
            }
            if (item.PreserveExistingRowOnly && existingRow != null)
            {
                preservedCount++;
                generatedPaths.Add(databasePath);
                if (replaceDeletePath != null) { replaceDeletePaths.Add(replaceDeletePath); }
                if (request.UpdateParentDirectoryRowsForPreservedItems)
                {
                    UpsertParentDirectoryRows(
                        item,
                        databasePath,
                        directoryRowGenerationScopeMatcher,
                        existingRowsByPath,
                        request.GeneratedAtUtc,
                        request.DirectoryMetadataResolver,
                        generatedPaths,
                        generatedParentDirectoryKeys,
                        upsertRows);
                }
                continue;
            }

            bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
            {
                FilePath = item.FilePath,
                DatabasePath = databasePath,
                Definition = item.Definition,
                ExistingRow = existingRow,
                LastWriteTimeUtc = item.LastWriteTimeUtc,
                GeneratedAtUtc = request.GeneratedAtUtc,
                FolderType = item.FolderType,
                ParentHash = item.ParentHash
            }, out LR2SongDB.folder row);
            if (!created)
            {
                if (item.LastWriteTimeUtc == null)
                {
                    skippedMissingMetadataCount++;
                }
                else
                {
                    skippedUnsupportedPathCount++;
                }
                continue;
            }

            generatedCount++;
            generatedPaths.Add(databasePath);
            if (!string.IsNullOrWhiteSpace(replaceDeletePath)
                && !string.Equals(replaceDeletePath, row.path, StringComparison.Ordinal))
            {
                replaceDeletePaths.Add(replaceDeletePath);
            }
            if (!AreEquivalent(row, existingRow))
            {
                upsertRows.Add(row);
            }

            UpsertParentDirectoryRows(
                item,
                databasePath,
                directoryRowGenerationScopeMatcher,
                existingRowsByPath,
                request.GeneratedAtUtc,
                request.DirectoryMetadataResolver,
                generatedPaths,
                generatedParentDirectoryKeys,
                upsertRows);
        }

        List<string> deletePaths = [.. replaceDeletePaths];
        if (request.AllowPrune)
        {
            deletePaths.AddRange(CreateDeletePaths(
                existingRowList,
                generatedPaths,
                request.ScopeDirectories,
                request.DirectoryRowScopeDirectories,
                request.ScopePaths,
                request.PruneExcludedDirectories,
                request.PruneExcludedPaths));
        }
        deletePaths = [.. deletePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];

        Lr2FolderGenerationWriteResult writeResult = Lr2FolderDbWriter.ApplySyncPlan(
            songDb,
            new Lr2FolderGenerationSyncPlan(upsertRows, deletePaths),
            commitTransaction);

        stopwatch.Stop();
        return new Lr2FolderFileDbSyncResult(
            itemCount,
            existingRowList.Count,
            existingRows.ExactReadCount,
            existingRows.ScopeReadCount,
            generatedCount,
            writeResult.UpsertedCount,
            writeResult.DeletedCount,
            preservedCount,
            skippedUnsupportedPathCount,
            skippedMissingMetadataCount,
            stopwatch.ElapsedMilliseconds);
    }

    private const int SongAddDateQueryChunkSize = 500;

    /// <summary>
    /// 組込みの新曲フォルダのファイルを列挙・読み取る前に、DBだけで作成が必要かを判定します。
    /// 追加譜面のsong.adddateがいずれも新曲期間外、またはfolder表に新曲フォルダの行（相対キーか、現在のLR2rootで作る旧版の絶対キーの完全一致）が
    /// 既にある場合はfalseです。照会は主キーの完全一致だけで行い、folder表を走査しません。
    /// 作成が不要な場合にファイルの読取り失敗で局所同期を失敗させないため、呼出し元はこの結果がtrueの場合だけファイルを読みます。
    /// </summary>
    /// <param name="songDb">読み取るsong.db。</param>
    /// <param name="condition">追加譜面と期間の条件。</param>
    /// <returns>作成が必要な可能性がある場合はtrue。</returns>
    internal static bool RequiresBuiltinNewSongFolderRowCreation(
        LR2SongDBExtended songDb,
        Lr2BuiltinNewSongFolderCreationCondition condition)
    {
        ArgumentNullException.ThrowIfNull(songDb);
        if (condition == null || !HasRecentAddedSong(songDb, condition))
        {
            return false;
        }

        return Lr2FolderExistingRowLookup.QueryExactPaths(songDb, condition.CreateExistingRowKeys()).Count == 0;
    }

    /// <summary>
    /// 項目の完全一致キーの既存行がなく、追加譜面のsong.adddateのいずれかが組込みfolderと同じ規則で新曲期間内かを判定します。
    /// 呼出し元の事前判定から書込みまでの間に行が作られた場合も、ここで既存行を変更しません。
    /// </summary>
    private static bool ShouldCreateMissingItemsForRecentSongs(
        LR2SongDBExtended songDb,
        Lr2FolderFileDbSyncRequest request,
        IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath)
    {
        Lr2BuiltinNewSongFolderCreationCondition condition = request.CreateOnlyWhenMissingForRecentSongs;
        foreach (Lr2FolderFileSyncItem item in request.Items ?? [])
        {
            string databasePath = NormalizeFilePath(item?.DatabasePath ?? item?.FilePath);
            if (!string.IsNullOrWhiteSpace(databasePath) && existingRowsByPath.ContainsKey(databasePath))
            {
                return false;
            }
        }

        return HasRecentAddedSong(songDb, condition);
    }

    /// <summary>
    /// 追加譜面のsong.adddateのいずれかが組込みfolderと同じ規則で新曲期間内かを判定します。
    /// song.adddateは主キーpathの完全一致で読み、期間内の行が見つかった時点で打ち切ります。
    /// </summary>
    private static bool HasRecentAddedSong(
        LR2SongDBExtended songDb,
        Lr2BuiltinNewSongFolderCreationCondition condition)
    {
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string pathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
        string addDateColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.adddate);
        foreach (string[] chunk in condition.AddedSongPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Chunk(SongAddDateQueryChunkSize))
        {
            IEnumerable<int?> addDates = songDb.Query<LR2SongDB.song>(
                "SELECT " + pathColumn + ", " + addDateColumn + " FROM " + songTable
                + " WHERE " + pathColumn + " IN (" + string.Join(",", chunk.Select(_ => "?")) + ");",
                chunk.Cast<object>().ToArray())
                .Select(row => row.adddate);
            if (Lr2BuiltinCustomFolderSettings.CreateFromAddDates(condition.Config, addDates, condition.NowUtc).IncludeNewSongFolder)
            {
                return true;
            }
        }
        return false;
    }

    private static Lr2FolderExistingRowsReadResult ReadExistingRows(
        LR2SongDBExtended songDb,
        Lr2FolderFileDbSyncRequest request)
    {
        var rowsByPath = new Dictionary<string, LR2SongDB.folder>(StringComparer.Ordinal);
        IReadOnlyList<LR2SongDB.folder> exactRows = [.. QueryExistingRowsByExactPaths(
            songDb,
            CreateExistingRowExactPathScope(request))];
        foreach (LR2SongDB.folder row in exactRows)
        {
            AddExistingRow(rowsByPath, row);
        }

        IReadOnlyList<LR2SongDB.folder> scopeRows = [];
        if (request?.AllowPrune == true)
        {
            scopeRows = [.. QueryExistingRowsByScopeDirectories(
                songDb,
                CreateExistingRowScopeDirectories(request),
                request.PruneExcludedDirectories,
                request.ScopeReadLr2FolderRowsOnly)];
            foreach (LR2SongDB.folder row in scopeRows)
            {
                AddExistingRow(rowsByPath, row);
            }
        }

        return new Lr2FolderExistingRowsReadResult(
            [.. rowsByPath.Values],
            exactRows.Count,
            scopeRows.Count);
    }

    private static IReadOnlyCollection<string> CreateExistingRowExactPathScope(Lr2FolderFileDbSyncRequest request)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (request == null)
        {
            return result;
        }

        var directoryRowGenerationScopeMatcher =
            DirectoryScopeMatcher.Create(request.DirectoryRowGenerationScopeDirectories?.Count > 0
                ? request.DirectoryRowGenerationScopeDirectories
                : request.DirectoryRowScopeDirectories, request.DirectoryRowRootDirectories);
        foreach (Lr2FolderFileSyncItem item in request.Items ?? [])
        {
            AddExactFilePath(result, item?.DatabasePath);
            AddExactFilePath(result, item?.FilePath);
            string databasePath = NormalizeFilePath(item?.DatabasePath ?? item?.FilePath);
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                continue;
            }
            if (item.PreserveExistingRowOnly && !request.UpdateParentDirectoryRowsForPreservedItems)
            {
                continue;
            }

            foreach (ParentDirectoryRowTarget parentTarget in CreateParentDirectoryRowTargets(
                item,
                databasePath,
                directoryRowGenerationScopeMatcher))
            {
                if (TryCreateDirectoryRowPath(parentTarget.DatabaseDirectory, out string rowPath, out _)
                    && !string.IsNullOrWhiteSpace(rowPath))
                {
                    result.Add(rowPath);
                }
            }
        }

        foreach (string path in request.ScopePaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path)) { result.Add(path); }
        }

        return result;
    }

    private static IReadOnlyCollection<string> CreateExistingRowScopeDirectories(Lr2FolderFileDbSyncRequest request)
    {
        // SQLのprefix検索へ旧・新の非ASCII表記を両方渡します。物理包含の比較とは分けます。
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in request?.ScopeDirectories ?? [])
        {
            AddScopeDirectory(result, directory);
        }
        foreach (string directory in request?.DirectoryRowScopeDirectories ?? [])
        {
            AddScopeDirectory(result, directory);
        }
        foreach (string directory in request?.AdditionalReadDirectories ?? [])
        {
            AddScopeDirectory(result, directory);
        }
        return result;
    }

    private static void AddExactFilePath(ISet<string> result, string path)
    {
        string normalized = NormalizeFilePath(path);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            result?.Add(normalized);
        }
    }

    private static void AddScopeDirectory(ISet<string> result, string directory)
    {
        string normalized = NormalizeScopeDirectory(directory);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            result?.Add(normalized);
        }
    }

    private static void UpsertParentDirectoryRows(
        Lr2FolderFileSyncItem item,
        string databasePath,
        DirectoryScopeMatcher directoryRowScopeMatcher,
        IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
        DateTime generatedAtUtc,
        Func<string, Lr2FolderDirectoryMetadata> directoryMetadataResolver,
        ISet<string> generatedPaths,
        ISet<string> generatedParentDirectoryKeys,
        IList<LR2SongDB.folder> upsertRows)
    {
        foreach (ParentDirectoryRowTarget parentTarget in CreateParentDirectoryRowTargets(
            item,
            databasePath,
            directoryRowScopeMatcher))
        {
            if (!generatedParentDirectoryKeys.Add(parentTarget.DatabaseDirectory)
                || !TryCreateParentDirectoryRow(
                    parentTarget.DatabaseDirectory,
                    parentTarget.PhysicalDirectory,
                    directoryRowScopeMatcher,
                    existingRowsByPath,
                    generatedAtUtc,
                    directoryMetadataResolver,
                    out LR2SongDB.folder parentRow))
            {
                continue;
            }

            generatedPaths.Add(parentRow.path);
            LR2SongDB.folder existingParentRow = existingRowsByPath.TryGetValue(parentRow.path, out LR2SongDB.folder existingParent)
                ? existingParent
                : null;
            if (!AreEquivalent(parentRow, existingParentRow))
            {
                upsertRows.Add(parentRow);
            }
        }
    }

    private static Dictionary<string, LR2SongDB.folder> CreateExistingRowMap(IEnumerable<LR2SongDB.folder> existingRows)
    {
        return (existingRows ?? []).Where(row => row?.path != null)
            .ToDictionary(row => row.path, StringComparer.Ordinal);
    }

    private static void AddExistingRow(IDictionary<string, LR2SongDB.folder> rowsByPath, LR2SongDB.folder row)
    {
        if (rowsByPath == null || string.IsNullOrWhiteSpace(row?.path))
        {
            return;
        }

        rowsByPath[row.path] = row;
    }

    private static IEnumerable<LR2SongDB.folder> QueryExistingRowsByExactPaths(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> exactPaths)
    {
        if (songDb == null || exactPaths == null || exactPaths.Count == 0)
        {
            return [];
        }

        string[] paths = [.. exactPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        return Lr2FolderExistingRowLookup.QueryExactPaths(songDb, paths);
    }

    private static IEnumerable<LR2SongDB.folder> QueryExistingRowsByScopeDirectories(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> scopeDirectories,
        IReadOnlyCollection<string> excludedScopeDirectories,
        bool lr2FolderRowsOnly)
    {
        if (songDb == null || scopeDirectories == null || scopeDirectories.Count == 0)
        {
            return [];
        }

        var scopePaths = new List<string>();
        foreach (string scopeDirectory in scopeDirectories)
        {
            if (!TryCreateDirectoryRowPath(scopeDirectory, out string scopePath, out _)
                || string.IsNullOrWhiteSpace(scopePath))
            {
                continue;
            }

            scopePaths.Add(scopePath);
        }
        var excludedScopePaths = new List<string>();
        foreach (string excludedScopeDirectory in excludedScopeDirectories ?? [])
        {
            if (!TryCreateDirectoryRowPath(excludedScopeDirectory, out string excludedScopePath, out _)
                || string.IsNullOrWhiteSpace(excludedScopePath))
            {
                continue;
            }

            excludedScopePaths.Add(excludedScopePath);
        }
        return lr2FolderRowsOnly
            ? Lr2FolderExistingRowLookup.QueryLr2FolderPathPrefixScopes(songDb, scopePaths, excludedScopePaths)
            : Lr2FolderExistingRowLookup.QueryPathPrefixScopes(songDb, scopePaths, excludedScopePaths);
    }

    private static List<string> CreateDeletePaths(
        IEnumerable<LR2SongDB.folder> existingRows,
        IReadOnlySet<string> generatedPaths,
        IEnumerable<string> scopeDirectories,
        IEnumerable<string> directoryRowScopeDirectories,
        IEnumerable<string> scopePaths,
        IEnumerable<string> pruneExcludedDirectories,
        IEnumerable<string> pruneExcludedPaths)
    {
        var scopeMatcher = DirectoryScopeMatcher.Create(scopeDirectories);
        var directoryRowScopeMatcher = DirectoryScopeMatcher.Create(directoryRowScopeDirectories);
        var pruneExcludeMatcher = DirectoryScopeMatcher.Create(pruneExcludedDirectories);
        HashSet<string> pruneExcludedPathSet = new((pruneExcludedPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.Ordinal);
        var explicitPaths = new HashSet<string>(
            (scopePaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        var deletePaths = new List<string>();

        foreach (LR2SongDB.folder row in existingRows ?? [])
        {
            string path = row?.path;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            if (pruneExcludeMatcher.Contains(path))
            {
                continue;
            }
            if (pruneExcludedPathSet.Contains(path))
            {
                continue;
            }

            bool isLr2FolderPath = IsLr2FolderPath(path);
            bool isDirectoryRowScopePath = directoryRowScopeMatcher.Contains(path);
            if (!isLr2FolderPath && !isDirectoryRowScopePath)
            {
                continue;
            }
            if (IsNormalDirectoryRow(row) && !isDirectoryRowScopePath)
            {
                continue;
            }

            if (generatedPaths?.Contains(path) == true)
            {
                continue;
            }

            if (explicitPaths.Contains(path)
                || scopeMatcher.Contains(path)
                || isDirectoryRowScopePath)
            {
                deletePaths.Add(row.path);
            }
        }

        return deletePaths;
    }

    /// <summary>DB生成と同じ実ルート・停止境界から、所有者が先に捕捉すべき物理親だけを返します。現在FSの読取りは所有者へ委ねます。</summary>
    internal static IReadOnlyCollection<string> CreateParentDirectoryMetadataTargets(
        IEnumerable<Lr2FolderFileSyncItem> items,
        IEnumerable<string> directoryRowGenerationScopeDirectories,
        IEnumerable<string> directoryRowRootDirectories = null)
    {
        var directoryRowGenerationScopeMatcher =
            DirectoryScopeMatcher.Create(directoryRowGenerationScopeDirectories, directoryRowRootDirectories);
        var result = new HashSet<string>(PathComparer);
        foreach (Lr2FolderFileSyncItem item in items ?? [])
        {
            if (item == null)
            {
                continue;
            }

            string databasePath = NormalizeFilePath(item.DatabasePath ?? item.FilePath);
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                continue;
            }

            foreach (ParentDirectoryRowTarget target in CreateParentDirectoryRowTargets(
                item,
                databasePath,
                directoryRowGenerationScopeMatcher))
            {
                if (!string.IsNullOrWhiteSpace(target.PhysicalDirectory))
                {
                    result.Add(target.PhysicalDirectory);
                }
            }
        }

        return [.. result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool TryCreateParentDirectoryRow(
        string databaseDirectory,
        string physicalDirectory,
        DirectoryScopeMatcher directoryRowScopeMatcher,
        IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
        DateTime generatedAtUtc,
        Func<string, Lr2FolderDirectoryMetadata> directoryMetadataResolver,
        out LR2SongDB.folder row)
    {
        row = null;
        if (string.IsNullOrWhiteSpace(databaseDirectory)
            || string.IsNullOrWhiteSpace(physicalDirectory))
        {
            return false;
        }

        if (!TryCreateDirectoryRowPath(databaseDirectory, out string rowPath, out bool isKnownRelativeLr2Directory)
            || string.IsNullOrWhiteSpace(rowPath))
        {
            return false;
        }

        if (isKnownRelativeLr2Directory
            && IsKnownRelativeLr2FolderDirectoryRoot(databaseDirectory))
        {
            return false;
        }

        Lr2FolderDirectoryMetadata metadata = ResolveDirectoryMetadata(physicalDirectory, directoryMetadataResolver);
        DateTime? lastWriteTimeUtc = metadata?.LastWriteTimeUtc;
        if (!lastWriteTimeUtc.HasValue)
        {
            return false;
        }

        string parentHash = TryComputeParentDirectoryHash(databaseDirectory, isKnownRelativeLr2Directory, directoryRowScopeMatcher);
        if (string.IsNullOrWhiteSpace(parentHash))
        {
            return false;
        }

        if (!Lr2CompatibilityEvaluator.TryGetCp932ByteCount(rowPath, out _))
        {
            return false;
        }

        LR2SongDB.folder existingRow = null;
        existingRowsByPath?.TryGetValue(rowPath, out existingRow);
        row = new LR2SongDB.folder
        {
            title = metadata?.HasFolderInfoTitle == true
                ? metadata.FolderInfoTitle
                : ResolveDirectoryTitle(databaseDirectory),
            path = rowPath,
            type = isKnownRelativeLr2Directory ? 2 : 1,
            parent = parentHash,
            date = lastWriteTimeUtc.Value.ToUnixtime(),
            adddate = existingRow != null ? existingRow.adddate : generatedAtUtc.ToUnixtime()
        };
        return true;
    }

    private static bool AreEquivalent(LR2SongDB.folder expected, LR2SongDB.folder existing)
    {
        if (expected == null || existing == null)
        {
            return false;
        }

        return string.Equals(expected.path, existing.path, StringComparison.Ordinal)
            && string.Equals(expected.title, existing.title, StringComparison.Ordinal)
            && string.Equals(expected.subtitle, existing.subtitle, StringComparison.Ordinal)
            && string.Equals(expected.category, existing.category, StringComparison.Ordinal)
            && string.Equals(expected.info_a, existing.info_a, StringComparison.Ordinal)
            && string.Equals(expected.info_b, existing.info_b, StringComparison.Ordinal)
            && string.Equals(expected.command, existing.command, StringComparison.Ordinal)
            && expected.type == existing.type
            && string.Equals(expected.banner, existing.banner, StringComparison.Ordinal)
            && string.Equals(expected.parent, existing.parent, StringComparison.Ordinal)
            && expected.date == existing.date
            && expected.max == existing.max
            && expected.adddate == existing.adddate;
    }

    private static bool IsNormalDirectoryRow(LR2SongDB.folder row)
    {
        return row?.type == 1;
    }

    private static bool IsLr2FolderPath(string path)
    {
        return string.Equals(Path.GetExtension(path ?? string.Empty), ".lr2folder", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            return Lr2FolderFileProjection.NormalizeDatabasePath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeScopeDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        string trimmed = directoryPath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
        }

        string normalized = trimmed
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (IsKnownRelativeLr2FolderDirectory(normalized))
        {
            return normalized;
        }
        return null;
    }

    private sealed class DirectoryScopeMatcher
    {
        private static readonly DirectoryScopeMatcher Empty = new([]);

        private readonly List<string> scopes;

        private readonly HashSet<string> roots;

        internal IReadOnlySet<string> RealRoots { get; }

        internal IReadOnlySet<string> Boundaries => roots;

        private DirectoryScopeMatcher(List<string> scopes, IEnumerable<string> realRoots = null)
        {
            this.scopes = scopes ?? [];
            RealRoots = new HashSet<string>((realRoots ?? []).Select(Lr2FolderPhysicalParentDirectoryTargetHelper.NormalizeDirectory).Where(path => !string.IsNullOrWhiteSpace(path)), PathComparer);
            roots = new HashSet<string>(this.scopes, PathComparer);
        }

        public static DirectoryScopeMatcher Create(IEnumerable<string> scopeDirectories, IEnumerable<string> realRoots = null)
        {
            List<string> normalizedScopes = [.. (scopeDirectories ?? [])
                .Select(NormalizeScopeDirectory)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(PathComparer)
                .OrderBy(path => path.Length)];
            return normalizedScopes.Count == 0
                ? Empty
                : new DirectoryScopeMatcher(normalizedScopes, realRoots);
        }

        public bool Contains(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || scopes.Count == 0)
            {
                return false;
            }

            string normalizedPath = NormalizePathForScopeComparison(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return false;
            }

            string current = normalizedPath;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (roots.Contains(current))
                {
                    return true;
                }
                string parent = GetParentPathForScopeComparison(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
            return false;
        }

        private static string NormalizePathForScopeComparison(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }
                if (Path.IsPathRooted(path))
                {
                    return Path.IsPathFullyQualified(path) ? Lr2FolderPath.NormalizeDirectoryPath(path) : null;
                }
                return path.Trim()
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        private static string GetParentPathForScopeComparison(string normalizedPath)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return null;
            }

            try
            {
                if (Path.IsPathRooted(normalizedPath))
                {
                    return Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(normalizedPath));
                }

                int separatorIndex = normalizedPath.LastIndexOf(Path.DirectorySeparatorChar);
                return separatorIndex <= 0
                    ? null
                    : normalizedPath.Substring(0, separatorIndex);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }
    }

    private static bool IsKnownRelativeLr2FolderDirectory(string directoryPath)
    {
        return string.Equals(directoryPath, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase)
            || directoryPath.StartsWith(@"LR2files\CustomFolder\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownRelativeLr2FolderDirectoryRoot(string directoryPath)
    {
        string normalized = NormalizeKnownRelativeDirectory(directoryPath);
        return string.Equals(normalized, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ParentDirectoryRowTarget> CreateParentDirectoryRowTargets(
        Lr2FolderFileSyncItem item,
        string databasePath,
        DirectoryScopeMatcher directoryRowScopeMatcher)
    {
        List<ParentDirectoryRowTarget> targets = [.. Lr2FolderPhysicalParentDirectoryTargetHelper.EnumerateNormalizedParents(
            databasePath, item?.FilePath, directoryRowScopeMatcher.Boundaries, directoryRowScopeMatcher.RealRoots)
            .Select(target => new ParentDirectoryRowTarget(target.DatabaseDirectory, target.PhysicalDirectory))];
        targets.Reverse();
        return targets;
    }

    private sealed class ParentDirectoryRowTarget(string databaseDirectory, string physicalDirectory)
    {
        public string DatabaseDirectory { get; } = databaseDirectory;

        public string PhysicalDirectory { get; } = physicalDirectory;
    }

    private static bool TryCreateDirectoryRowPath(
        string directory,
        out string rowPath,
        out bool isKnownRelativeLr2Directory)
    {
        rowPath = null;
        isKnownRelativeLr2Directory = false;
        string relativeDirectory = NormalizeKnownRelativeDirectory(directory);
        if (!string.IsNullOrWhiteSpace(relativeDirectory))
        {
            rowPath = relativeDirectory + Path.DirectorySeparatorChar;
            isKnownRelativeLr2Directory = true;
            return true;
        }

        rowPath = Lr2FolderPath.ToFolderPath(directory);
        return !string.IsNullOrWhiteSpace(rowPath);
    }

    private static string NormalizeKnownRelativeDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        string normalized = directoryPath.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        return IsKnownRelativeLr2FolderDirectory(normalized)
            ? normalized
            : null;
    }

    private static Lr2FolderDirectoryMetadata ResolveDirectoryMetadata(
        string directoryPath,
        Func<string, Lr2FolderDirectoryMetadata> resolver)
    {
        if (resolver == null || string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        try
        {
            return resolver(directoryPath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string TryComputeParentDirectoryHash(
        string directory,
        bool isKnownRelativeLr2Directory,
        DirectoryScopeMatcher directoryRowScopeMatcher)
    {
        return Lr2FolderPhysicalParentDirectoryTargetHelper.ComputeNormalizedParentHash(directory, directoryRowScopeMatcher.RealRoots);
    }

    private static string ResolveDirectoryTitle(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string title = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(title) ? trimmed : title;
    }
}
