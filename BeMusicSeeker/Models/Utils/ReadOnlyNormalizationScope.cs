namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// 変更系ファイル操作の前後で、どの範囲まで ReadOnly 属性を解除するかを表します。
/// </summary>
internal enum ReadOnlyNormalizationScope
{
    /// <summary>
    /// ReadOnly 属性を変更しません。
    /// </summary>
    None,

    /// <summary>
    /// 対象パス自身だけの ReadOnly 属性を解除します。
    /// </summary>
    TargetOnly,

    /// <summary>
    /// 対象ディレクトリ配下を再帰的に走査し、ディレクトリとファイルの ReadOnly 属性を解除します。
    /// </summary>
    RecursiveDirectoryTree
}
