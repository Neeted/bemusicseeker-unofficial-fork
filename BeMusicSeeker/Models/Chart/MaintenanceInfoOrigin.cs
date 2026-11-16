namespace BeMusicSeeker.Models;

/// <summary>
/// `maintenanceInfo` がどの経路で得られた値かを表します。
/// ResourceHealth の正本判定で lazy placeholder と DB/計算済み snapshot を区別するために使います。
/// </summary>
internal enum MaintenanceInfoOrigin
{
    /// <summary>
    /// maintenance 情報がまだ materialize されていない状態です。
    /// </summary>
    None,

    /// <summary>
    /// lazy getter などで作られた未検査の placeholder です。
    /// </summary>
    Placeholder,

    /// <summary>
    /// DB の maintenance table から hydrate された snapshot です。
    /// </summary>
    DbHydrated,

    /// <summary>
    /// file diff、導入処理、または明示的な再スキャンで計算された snapshot です。
    /// </summary>
    Calculated
}
