using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

/// <summary>ヘッダー確定後の新先投影・旧先整理を明示再試行するため、確定済み移行の入力と成立事実を保持します。</summary>
internal sealed class CustomFolderMigrationReceipt(
    PlaylistCustomFolderOutputMaintenanceOwner.CustomFolderMigrationPreparation preparation)
{
    /// <summary>旧新絶対path、出力型、保護領域と当該出力条件です。</summary>
    internal PlaylistCustomFolderOutputMaintenanceOwner.CustomFolderMigrationPreparation Preparation { get; } = preparation;

    /// <summary>実際に投影・DB同期が成立した先です。候補修正後は元Preparationの新先とは異なる場合があります。</summary>
    internal PlaylistCustomFolderOutputMaintenanceOwner.CustomFolderMigrationPreparation MaterializationPreparation { get; set; }

    /// <summary>MaterializationPreparationの全対象について投影・DB同期が成立した事実です。</summary>
    internal bool MaterializationCompleted { get; set; }

    /// <summary>当該旧先整理が完了した事実です。</summary>
    internal bool CleanupCompleted { get; set; }

    /// <summary>物理整理が成立した対象です。後続DB整理の失敗でも同じ旧先を再削除しません。</summary>
    internal HashSet<PlaylistCustomFolderOutputMaintenanceOwner.MigrationPlan> CleanedDirectories { get; } = [];

    /// <summary>物理整理後、まだDB行整理の一括確定を終えていない範囲です。</summary>
    internal HashSet<string> PendingDatabasePruneScopes { get; } = new(StringComparer.OrdinalIgnoreCase);
}
