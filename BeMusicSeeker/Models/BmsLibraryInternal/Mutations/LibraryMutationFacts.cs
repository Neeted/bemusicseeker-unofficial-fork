using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Util.Extensions;

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

/// <summary>確認前に固定した削除対象と、同じ捕捉境界で得た確認候補・未解決結果です。</summary>
internal sealed class LibraryChartRemovalPreflight
{
    /// <summary>操作単位の捕捉値をコピーし、確認後の実行まで変更不能に保持します。</summary>
    internal LibraryChartRemovalPreflight(
        IEnumerable<ChartFile> targets,
        IEnumerable<string> wholeFolderCandidatePaths,
        IEnumerable<string> unresolvedPaths,
        int inputCount,
        int pathOnlyInputCount,
        IEnumerable<string> registeredUserRoots = null,
        string installDirectory = null,
        IEnumerable<LibraryChartRemovalTarget> rejectedTargets = null)
    {
        Targets = Array.AsReadOnly((targets ?? []).Where(target => target != null).ToArray());
        WholeFolderCandidatePaths = Array.AsReadOnly((wholeFolderCandidatePaths ?? []).ToArray());
        UnresolvedPaths = Array.AsReadOnly((unresolvedPaths ?? []).ToArray());
        InputCount = inputCount;
        PathOnlyInputCount = pathOnlyInputCount;
        RegisteredUserRoots = Array.AsReadOnly((registeredUserRoots ?? []).ToArray());
        InstallDirectory = installDirectory;
        RejectedTargets = Array.AsReadOnly((rejectedTargets ?? []).ToArray());
    }

    /// <summary>確認前の所持識別・形式・配置を持つ固定共通値です。</summary>
    internal IReadOnlyList<ChartFile> Targets { get; }
    /// <summary>固定対象と同じ境界で捕捉したフォルダ削除確認候補です。</summary>
    internal IReadOnlyList<string> WholeFolderCandidatePaths { get; }
    /// <summary>事前照合が成立しなかった要求パスです。</summary>
    internal IReadOnlyList<string> UnresolvedPaths { get; }
    /// <summary>重複排除前の有効な入力件数です。</summary>
    internal int InputCount { get; }
    /// <summary>所持識別がない入力件数です。</summary>
    internal int PathOnlyInputCount { get; }

    /// <summary>確認前に捕捉した利用者登録です。欠落・親子・順序を保持します。</summary>
    internal IReadOnlyList<string> RegisteredUserRoots { get; }
    /// <summary>同じ確認入力で保護する導入先です。</summary>
    internal string InstallDirectory { get; }
    /// <summary>全体削除Yesを別操作へ読み替えず、物理計画から除いた対象です。</summary>
    internal IReadOnlyList<LibraryChartRemovalTarget> RejectedTargets { get; }

    /// <summary>当該scope内の登録childも含め、全体削除と登録解除を確認します。</summary>
    internal string GetWholeFolderConfirmationMessage(string scope)
    {
        string[] affected = RegisteredUserRoots.Where(root => IsWithin(root, scope)).ToArray();
        return affected.Length == 0 ? string.Format(Resources.Confirm_DeleteFolderWithNoBms, scope)
            : string.Format(Resources.Confirm_DeleteRegisteredBmsRoots, scope, string.Join(Environment.NewLine, affected));
    }

    /// <summary>最後の利用者登録または現在導入先を含む全体削除scopeを識別します。</summary>
    internal bool IsProtectedWholeFolder(string scope)
        => RegisteredUserRoots.Count > 0 && RegisteredUserRoots.All(root => IsWithin(root, scope))
            || !string.IsNullOrWhiteSpace(InstallDirectory) && IsWithin(InstallDirectory, scope);

    /// <summary>承認した候補のうち、保護scopeを除いた実物理集合を返します。</summary>
    internal IReadOnlyList<string> GetExecutableWholeFolderPaths(IEnumerable<string> approved)
        => Array.AsReadOnly((approved ?? []).Where(scope => WholeFolderCandidatePaths.Contains(scope, StringComparer.OrdinalIgnoreCase)
            && !IsProtectedWholeFolder(scope)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    /// <summary>Yesで拒否した直下groupだけを除き、明示選択の安全子は自身の判断を保ってP判定・停止・物理へ渡します。Noの対象は変更しません。</summary>
    internal LibraryChartRemovalPreflight ExcludeProtectedWholeFolderApprovals(IEnumerable<string> approved)
    {
        string[] blocked = (approved ?? []).Where(scope => WholeFolderCandidatePaths.Contains(scope, StringComparer.OrdinalIgnoreCase)
            && IsProtectedWholeFolder(scope)).ToArray();
        if (blocked.Length == 0) { return this; }
        ChartFile[] rejected = Targets.Where(target => blocked.Contains(DirectoryExt.GetDirectoryNameSimple(target.Path), StringComparer.OrdinalIgnoreCase)).ToArray();
        return new LibraryChartRemovalPreflight(Targets.Except(rejected), WholeFolderCandidatePaths.Except(blocked, StringComparer.OrdinalIgnoreCase),
            UnresolvedPaths, InputCount, PathOnlyInputCount, RegisteredUserRoots, InstallDirectory,
            RejectedTargets.Concat(rejected.Select(target => new LibraryChartRemovalTarget(target.Path, LibraryChartRemovalState.NotExecuted,
                new InvalidOperationException(string.Format(Resources.Warn_CannotDeleteProtectedBmsRoot, System.IO.Path.GetDirectoryName(target.Path)))))));
    }

    private static bool IsWithin(string path, string scope)
        => LongPathFileSystem.IsSameOrDescendantDirectoryPath(path, scope);
}

/// <summary>一つの変更セッションで処理する、確認前の固定対象と変更先拡張子です。</summary>
internal sealed class LibraryFileExtensionRenameBatch
{
    /// <summary>固定対象列をコピーし、元選択や並行したpreflight配列を保持しません。</summary>
    /// <param name="targets">現在の所持識別・形式・配置を持つ不変の共通値。</param>
    /// <param name="newExtension">変更先拡張子。</param>
    /// <param name="inputCount">未解決も含む入力件数。省略時は固定対象件数。</param>
    internal LibraryFileExtensionRenameBatch(
        IEnumerable<ChartFile> targets,
        string newExtension,
        int? inputCount = null)
    {
        Targets = Array.AsReadOnly((targets ?? []).Where(target => target != null).ToArray());
        NewExtension = newExtension ?? string.Empty;
        InputCount = inputCount ?? Targets.Count;
    }

    /// <summary>確認後に再確定しない固定対象列です。</summary>
    internal IReadOnlyList<ChartFile> Targets { get; }
    /// <summary>filesystem処理に渡す変更先拡張子です。</summary>
    internal string NewExtension { get; }
    /// <summary>未解決を成功扱いしないための入力件数です。</summary>
    internal int InputCount { get; }
}

/// <summary>不正な拡張子の変更処理が確定した不変の結果です。</summary>
internal sealed class LibraryFileExtensionRenameResult
{
    /// <summary>変更処理のカタログ事実と操作結果報告を保持します。</summary>
    /// <param name="catalogFacts">カタログへ適用するパス移動事実。</param>
    /// <param name="report">変更処理固有の件数・失敗・時間。</param>
    /// <param name="confirmedTargets">filesystemで確定しsessionへappendできる変更対象。</param>
    internal LibraryFileExtensionRenameResult(
        LibraryCatalogMutationFacts catalogFacts,
        LibraryFileExtensionRenameReport report,
        IEnumerable<LibraryMutationSessionTarget> confirmedTargets = null)
    {
        CatalogFacts = catalogFacts ?? LibraryCatalogMutationFacts.Empty;
        Report = report ?? LibraryFileExtensionRenameReport.Empty;
        ConfirmedTargets = Array.AsReadOnly((confirmedTargets ?? [])
            .Where(target => target != null)
            .ToArray());
    }

    /// <summary>カタログへ適用するパス移動事実。</summary>
    internal LibraryCatalogMutationFacts CatalogFacts { get; }

    /// <summary>変更処理固有の件数・失敗・時間を含む報告。</summary>
    internal LibraryFileExtensionRenameReport Report { get; }

    /// <summary>filesystemで成功し、同じoperation sessionへ登録できる対象。</summary>
    internal IReadOnlyList<LibraryMutationSessionTarget> ConfirmedTargets { get; }
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
    /// <summary>確定パスを受付権とモデル排他の解放後に公開する処理です。</summary>
    internal Action PublishPackagePaths { get; set; }
    /// <summary>状態適用前後の共通所持集合版です。独立した保存行版を保持しません。</summary>
    public OwnedChartCollectionVersionSnapshot StorageRowsVersion { get; set; }

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

/// <summary>所持譜面の確定DST更新、または保留項目の候補・配置へ適用する物理成功scopeです。</summary>
internal sealed class LibraryInstallDestinationChange
{
    /// <summary>保留項目へ順に適用する物理成功範囲です。空の場合は従来の確定DST更新です。</summary>
    public string SourceFolderPath { get; init; }

    /// <summary>改名・移動・統合の存続先です。削除の場合は空です。</summary>
    public string DestinationFolderPath { get; init; }

    /// <summary>削除・統合により、成功範囲に触れる項目の全候補を失効させます。</summary>
    public bool InvalidateCandidates { get; init; }

    /// <summary>物理フォルダの同一性と区切り境界で、当該範囲への包含を判定します。</summary>
    internal static bool IsUnderFolder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) { return false; }
        string pathKey = NormalizeDirectory(path);
        string folderKey = NormalizeDirectory(folder);
        return string.Equals(pathKey, folderKey, StringComparison.OrdinalIgnoreCase)
            || pathKey.StartsWith(folderKey + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>成功範囲にあるpathだけを同じ相対位置で移します。候補の順位や存在は再評価しません。</summary>
    internal static string RelocatePath(string path, string source, string destination)
    {
        if (!IsUnderFolder(path, source)) { return path; }
        string sourceKey = NormalizeDirectory(source);
        string pathKey = NormalizeDirectory(path);
        string destinationBase = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string destinationRoot = Path.GetPathRoot(destination);
        if (string.Equals(destinationBase, destinationRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            destinationBase = destinationRoot;
        }
        return Path.Combine(destinationBase, pathKey.Length == sourceKey.Length ? string.Empty : pathKey[(sourceKey.Length + 1)..]);
    }

    private static string NormalizeDirectory(string path)
    {
        try { path = Path.GetFullPath(path.Trim()); }
        catch { path = path.Trim(); }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

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


    /// <summary>
    /// 導入先状態と同時にパス変更を反映した譜面スナップショットを生成します。
    /// </summary>
    /// <param name="pathChanges">同じ操作で確定した譜面パス移動。</param>
    /// <returns>反映後の譜面スナップショット。対象がない場合はnull。</returns>
    internal ChartFile CreateAppliedChartSnapshot(IEnumerable<LibraryChartPathChange> pathChanges = null)
    {
        if (Entry != null && !string.IsNullOrWhiteSpace(SourceFolderPath))
        {
            // 保留の確認用snapshotも同じscope適用を使い、別主体の譜面パス変更は重ねません。
            PackageChartEntry snapshot = Entry.ToChartEntrySnapshot();
            snapshot.ApplyLibraryFolderChange(this);
            return snapshot.Chart;
        }
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

        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            ChartFile changedChart = pathChange?.Chart;
            if (changedChart == null || string.IsNullOrWhiteSpace(pathChange.NewPath))
            {
                continue;
            }
            if (source.Token != null && ReferenceEquals(changedChart.Token, source.Token))
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
