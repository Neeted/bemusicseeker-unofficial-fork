using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

/// <summary>成立済み出力の旧先整理を明示再試行するため、当該移行の捕捉済み入力だけを保持します。</summary>
internal sealed class CustomFolderMigrationReceipt(
    PlaylistCustomFolderOutputMaintenanceOwner.CustomFolderMigrationPreparation preparation)
{
    /// <summary>旧新絶対path、出力型、保護領域と当該出力条件です。</summary>
    internal PlaylistCustomFolderOutputMaintenanceOwner.CustomFolderMigrationPreparation Preparation { get; } = preparation;

    /// <summary>当該旧先整理が完了した事実です。</summary>
    internal bool CleanupCompleted { get; set; }

    /// <summary>物理整理が成立した対象です。後続DB整理の失敗でも同じ旧先を再削除しません。</summary>
    internal HashSet<PlaylistCustomFolderOutputMaintenanceOwner.MigrationPlan> CleanedDirectories { get; } = [];

    /// <summary>物理整理後、まだDB行整理の一括確定を終えていない範囲です。</summary>
    internal HashSet<string> PendingDatabasePruneScopes { get; } = new(StringComparer.OrdinalIgnoreCase);
}
