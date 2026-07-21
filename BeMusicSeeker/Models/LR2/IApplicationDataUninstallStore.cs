namespace BeMusicSeeker.Models.LR2;

/// <summary>
/// BeMusicSeeker が LR2 song DB へ保存した application-owned data を削除する durable boundary です。
/// </summary>
internal interface IApplicationDataUninstallStore
{
    /// <summary>
    /// 指定された song DB から application-owned data だけを削除します。
    /// </summary>
    /// <param name="songDbPath">対象 song DB の path。</param>
    void Uninstall(string songDbPath);
}
