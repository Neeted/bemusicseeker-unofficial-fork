using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ライブラリファイル操作で確定したカタログ事実です。
/// 生成時に入力列をコピーするため、生産側の作業用リスト解放後も所有側が読み取れます。
/// </summary>
internal sealed class LibraryCatalogMutationFacts
{
    /// <summary>
    /// カタログへ適用する削除・譜面移動・フォルダー移動の事実を捕捉します。
    /// </summary>
    /// <param name="chartRemoveRequests">確定した譜面削除要求。</param>
    /// <param name="chartPathChanges">確定した譜面パス移動。</param>
    /// <param name="folderPathChanges">確定したフォルダーパス移動。</param>
    internal LibraryCatalogMutationFacts(
        IEnumerable<OwnedChartRemoveRequest> chartRemoveRequests = null,
        IEnumerable<LibraryChartPathChange> chartPathChanges = null,
        IEnumerable<LibraryFolderPathChange> folderPathChanges = null)
    {
        ChartRemoveRequests = Array.AsReadOnly((chartRemoveRequests ?? [])
            .Where(request => request != null)
            .ToArray());
        ChartPathChanges = Array.AsReadOnly((chartPathChanges ?? [])
            .Where(change => change != null)
            .ToArray());
        FolderPathChanges = Array.AsReadOnly((folderPathChanges ?? [])
            .Where(change => change != null)
            .ToArray());
    }

    /// <summary>所有者参照またはパス整理として確定した削除要求。</summary>
    internal IReadOnlyList<OwnedChartRemoveRequest> ChartRemoveRequests { get; }

    /// <summary>旧パスと新パスを対応付けたカタログ行の移動事実。</summary>
    internal IReadOnlyList<LibraryChartPathChange> ChartPathChanges { get; }

    /// <summary>旧フォルダーと新フォルダーを対応付けたカタログ行の移動事実。</summary>
    internal IReadOnlyList<LibraryFolderPathChange> FolderPathChanges { get; }

    /// <summary>いずれかのカタログ事実を保持しているかを返します。</summary>
    internal bool HasChanges => ChartRemoveRequests.Count > 0
        || ChartPathChanges.Count > 0
        || FolderPathChanges.Count > 0;

    /// <summary>カタログ事実を持たない空の事実集合です。</summary>
    internal static LibraryCatalogMutationFacts Empty { get; } = new();
}

/// <summary>
/// ファイル操作で確定したパッケージ参照と導入先参照です。
/// カタログの確定後にパッケージ所有側が適用します。
/// </summary>
internal sealed class LibraryPackageReferenceFacts
{
    /// <summary>
    /// 導入先と導入済みパッケージのパスに関する確定事実を捕捉します。
    /// </summary>
    /// <param name="installDestinationChanges">導入先の更新事実。</param>
    /// <param name="installedPackagePathChanges">導入済みパッケージのパス更新事実。</param>
    internal LibraryPackageReferenceFacts(
        IEnumerable<LibraryInstallDestinationChange> installDestinationChanges = null,
        IEnumerable<LibraryInstalledPackagePathChange> installedPackagePathChanges = null)
    {
        InstallDestinationChanges = Array.AsReadOnly((installDestinationChanges ?? [])
            .Where(change => change != null)
            .ToArray());
        InstalledPackagePathChanges = Array.AsReadOnly((installedPackagePathChanges ?? [])
            .Where(change => change != null)
            .ToArray());
    }

    /// <summary>導入先の更新または消去を表すパッケージ参照。</summary>
    internal IReadOnlyList<LibraryInstallDestinationChange> InstallDestinationChanges { get; }

    /// <summary>導入済みパッケージのパス更新を表すパッケージ参照。</summary>
    internal IReadOnlyList<LibraryInstalledPackagePathChange> InstalledPackagePathChanges { get; }

    /// <summary>いずれかのパッケージ参照事実を保持しているかを返します。</summary>
    internal bool HasChanges => InstallDestinationChanges.Count > 0
        || InstalledPackagePathChanges.Count > 0;

    /// <summary>
    /// 確定した導入先を反映した譜面スナップショットを生成します。
    /// </summary>
    /// <param name="pathChanges">同じ操作で確定した譜面パス移動。</param>
    /// <returns>導入先更新後の譜面スナップショット一覧。</returns>
    internal IReadOnlyList<ChartFile> CreateAppliedInstallDestinationChartSnapshots(
        IEnumerable<LibraryChartPathChange> pathChanges = null)
    {
        return [.. InstallDestinationChanges
            .Select(change => change.CreateAppliedChartSnapshot(pathChanges))
            .Where(chart => chart != null)];
    }

    /// <summary>パッケージ参照事実を持たない空の事実集合です。</summary>
    internal static LibraryPackageReferenceFacts Empty { get; } = new();
}

/// <summary>
/// フォルダー移動で生成したカタログ／パッケージの型付き事実と、
/// 保存行パス公開を制御する操作方針を保持します。
/// </summary>
internal sealed class LibraryFolderMoveFacts
{
    /// <summary>フォルダー移動の確定事実を捕捉します。</summary>
    /// <param name="catalogFacts">カタログ側の移動事実。</param>
    /// <param name="packageReferenceFacts">パッケージ側の移動事実。</param>
    /// <param name="storageRowPathNotificationPolicy">保存行パスの公開方針。</param>
    internal LibraryFolderMoveFacts(
        LibraryCatalogMutationFacts catalogFacts,
        LibraryPackageReferenceFacts packageReferenceFacts,
        LibraryStorageRowPathNotificationPolicy storageRowPathNotificationPolicy)
    {
        CatalogFacts = catalogFacts ?? LibraryCatalogMutationFacts.Empty;
        PackageReferenceFacts = packageReferenceFacts ?? LibraryPackageReferenceFacts.Empty;
        StorageRowPathNotificationPolicy = storageRowPathNotificationPolicy;
    }

    /// <summary>カタログ側の確定事実。</summary>
    internal LibraryCatalogMutationFacts CatalogFacts { get; }

    /// <summary>パッケージ側の確定事実。</summary>
    internal LibraryPackageReferenceFacts PackageReferenceFacts { get; }

    /// <summary>保存行パスの公開方針。</summary>
    internal LibraryStorageRowPathNotificationPolicy StorageRowPathNotificationPolicy { get; }
}

/// <summary>
/// カタログ事実にパス移動を含む操作の保存行パス公開方針です。
/// 既存コマンドの契約が行パス通知を抑制する場合も明示的に保持します。
/// </summary>
internal enum LibraryStorageRowPathNotificationPolicy
{
    /// <summary>保存行パス通知を抑制します。</summary>
    Suppressed,

    /// <summary>保存行パス通知を発行します。</summary>
    Notify
}

/// <summary>不正な拡張子の変更処理が確定した不変の結果です。</summary>
internal sealed class LibraryFileExtensionRenameResult
{
    /// <summary>変更処理のカタログ事実と操作結果報告を保持します。</summary>
    /// <param name="catalogFacts">カタログへ適用するパス移動事実。</param>
    /// <param name="report">変更処理固有の件数・失敗・時間。</param>
    internal LibraryFileExtensionRenameResult(
        LibraryCatalogMutationFacts catalogFacts,
        LibraryFileExtensionRenameReport report)
    {
        CatalogFacts = catalogFacts ?? LibraryCatalogMutationFacts.Empty;
        Report = report ?? LibraryFileExtensionRenameReport.Empty;
    }

    /// <summary>カタログへ適用するパス移動事実。</summary>
    internal LibraryCatalogMutationFacts CatalogFacts { get; }

    /// <summary>変更処理固有の件数・失敗・時間を含む報告。</summary>
    internal LibraryFileExtensionRenameReport Report { get; }
}

/// <summary>
/// 拡張子変更固有の件数・失敗・時間を保持します。
/// 共通索引所有者が読むカタログ事実とは分離します。
/// </summary>
internal sealed class LibraryFileExtensionRenameReport
{
    /// <summary>拡張子変更の操作結果を捕捉します。</summary>
    /// <param name="failures">削除などで発生した失敗一覧。</param>
    /// <param name="renamedCount">renameできた件数。</param>
    /// <param name="duplicateDeletedCount">重複削除の件数。</param>
    /// <param name="skippedCount">処理をスキップした件数。</param>
    /// <param name="totalMs">操作全体の経過時間。</param>
    internal LibraryFileExtensionRenameReport(
        IEnumerable<LibraryDeleteFailure> failures,
        int renamedCount,
        int duplicateDeletedCount,
        int skippedCount,
        long totalMs)
    {
        Failures = Array.AsReadOnly((failures ?? [])
            .Where(failure => failure != null)
            .ToArray());
        RenamedCount = renamedCount;
        DuplicateDeletedCount = duplicateDeletedCount;
        SkippedCount = skippedCount;
        TotalMs = totalMs;
    }

    /// <summary>削除などで発生した失敗一覧。</summary>
    internal IReadOnlyList<LibraryDeleteFailure> Failures { get; }

    /// <summary>変更できた件数。</summary>
    internal int RenamedCount { get; }

    /// <summary>重複削除の件数。</summary>
    internal int DuplicateDeletedCount { get; }

    /// <summary>処理をスキップした件数。</summary>
    internal int SkippedCount { get; }

    /// <summary>操作全体の経過時間。</summary>
    internal long TotalMs { get; }

    /// <summary>変更結果を持たない空の報告です。</summary>
    internal static LibraryFileExtensionRenameReport Empty { get; } = new([], 0, 0, 0, 0L);
}

/// <summary>カタログ譜面の旧パスと新パスを保持する移動事実。</summary>
internal sealed class LibraryChartPathChange
{
    /// <summary>移動対象を識別する譜面スナップショット。</summary>
    public ChartFile Chart { get; init; }

    /// <summary>変更後のパス。</summary>
    public string NewPath { get; init; }

    /// <summary>変更前のパス。</summary>
    public string OldPath { get; init; }

    /// <summary>chartが保持するBMS storage ownerを返します。</summary>
    /// <returns>対応するBMS owner。存在しない場合はnull。</returns>
    internal BMSFile GetBmsStorageOwner() => Chart?.GetBmsStorageOwner();

    /// <summary>chartが保持するBMSON storage ownerを返します。</summary>
    /// <returns>対応するBMSON owner。存在しない場合はnull。</returns>
    internal LR2SongDBExtended.bmson_song GetBmsonStorageOwner() => Chart?.GetBmsonStorageOwner();
}

/// <summary>カタログのフォルダー行の旧パスと新パスを保持する移動事実。</summary>
internal sealed class LibraryFolderPathChange
{
    /// <summary>変更後のフォルダーパス。</summary>
    public string NewFolderPath { get; init; }

    /// <summary>変更前のフォルダーパス。</summary>
    public string OldFolderPath { get; init; }
}

/// <summary>カタログとパッケージの状態適用に要した結果を保持します。</summary>
internal sealed class BmsLibraryStateApplyResult
{
    /// <summary>状態適用前後の保存行バージョン。</summary>
    public StorageRowsVersionSnapshot StorageRowsVersion { get; set; }

    /// <summary>フォルダーDB更新の経過時間。</summary>
    public long FolderDbMs { get; set; }

    /// <summary>パスのメモリ反映の経過時間。</summary>
    public long PathMemoryApplyMs { get; set; }

    /// <summary>BMSパスDB更新の経過時間。</summary>
    public long BmsPathDbMs { get; set; }

    /// <summary>BMSONパスDB更新の経過時間。</summary>
    public long BmsonPathDbMs { get; set; }

    /// <summary>BMS削除DB更新の経過時間。</summary>
    public long BmsRemovalDbMs { get; set; }

    /// <summary>BMSON削除DB更新の経過時間。</summary>
    public long BmsonRemovalDbMs { get; set; }

    /// <summary>パッケージ状態適用の経過時間。</summary>
    public long PackageApplyMs { get; set; }
}

/// <summary>譜面またはパッケージ項目の導入先更新事実。</summary>
internal sealed class LibraryInstallDestinationChange
{
    /// <summary>ライブラリ側の対象譜面。</summary>
    public ChartFile Chart { get; init; }

    /// <summary>保留パッケージ側の対象項目。</summary>
    public PackageChartEntry Entry { get; init; }

    /// <summary>設定する新しい導入先。</summary>
    public string NewInstallDestination { get; init; }

    /// <summary>導入先状態を消去するかどうか。</summary>
    public bool ClearInstallDestinationState { get; init; }

    /// <summary>変更前の導入先を返します。</summary>
    /// <returns>項目または譜面が保持する変更前の導入先。</returns>
    internal string GetCurrentInstallDestination() => Entry?.Chart?.InstallDestination
        ?? Chart?.InstallDestination;

    /// <summary>chartが保持するBMS storage ownerを返します。</summary>
    /// <returns>対応するBMS owner。存在しない場合はnull。</returns>
    internal BMSFile GetBmsStorageOwner() => Chart?.GetBmsStorageOwner();

    /// <summary>
    /// 導入先状態と同時にパス変更を反映した譜面スナップショットを生成します。
    /// </summary>
    /// <param name="pathChanges">同じ操作で確定した譜面パス移動。</param>
    /// <returns>反映後の譜面スナップショット。対象がない場合はnull。</returns>
    internal ChartFile CreateAppliedChartSnapshot(IEnumerable<LibraryChartPathChange> pathChanges = null)
    {
        ChartFile source = Entry?.Chart ?? Chart;
        if (source == null)
        {
            return null;
        }

        ChartFile appliedChart;
        if (ClearInstallDestinationState)
        {
            appliedChart = ChartFileProjection.WithPackageState(
                source,
                null,
                string.Empty,
                string.Empty,
                [],
                [.. (source.Warnings ?? []).Where(warning => warning?.Category != ChartWarningCategory.InstallEstimation)]);
        }
        else
        {
            appliedChart = ChartFileProjection.WithPackageState(
                source,
                NewInstallDestination,
                source.InstallDestinationTitle,
                source.InstallDestinationArtist,
                source.InstallDestinationSuggestions,
                source.Warnings);
        }

        string newPath = Entry == null ? ResolveNewPath(source, pathChanges) : null;
        return string.IsNullOrWhiteSpace(newPath)
            ? appliedChart
            : ChartFileProjection.WithPath(appliedChart, newPath);
    }

    /// <summary>
    /// 譜面または保存所有者の識別情報に対応する新パスを解決します。
    /// </summary>
    /// <param name="source">パスを解決する譜面。</param>
    /// <param name="pathChanges">同じ操作で確定したパス移動。</param>
    /// <returns>対応する新パス。見つからない場合はnull。</returns>
    private static string ResolveNewPath(ChartFile source, IEnumerable<LibraryChartPathChange> pathChanges)
    {
        if (source == null)
        {
            return null;
        }

        BMSFile bmsFile = source.GetBmsStorageOwner();
        LR2SongDBExtended.bmson_song bmsonSong = source.GetBmsonStorageOwner();
        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            ChartFile changedChart = pathChange?.Chart;
            if (changedChart == null || string.IsNullOrWhiteSpace(pathChange.NewPath))
            {
                continue;
            }
            if (ReferenceEquals(changedChart, source)
                || (bmsFile != null && ReferenceEquals(changedChart.GetBmsStorageOwner(), bmsFile))
                || (bmsonSong != null && ReferenceEquals(changedChart.GetBmsonStorageOwner(), bmsonSong)))
            {
                return pathChange.NewPath;
            }
        }
        return null;
    }
}

/// <summary>導入済みパッケージのパス更新事実。</summary>
internal sealed class LibraryInstalledPackagePathChange
{
    /// <summary>パスを更新するパッケージ参照。</summary>
    public ChartPackage Package { get; init; }

    /// <summary>更新後のパッケージパス。</summary>
    public string NewPath { get; init; }
}
