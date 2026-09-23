namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// ファイル変更サービスが扱う操作種別を表します。
/// </summary>
internal enum FileMutationKind
{
    /// <summary>
    /// ディレクトリを作成または存在確認します。
    /// </summary>
    EnsureDirectory,

    /// <summary>
    /// ファイルを移動します。
    /// </summary>
    MoveFile,

    /// <summary>
    /// ディレクトリを移動します。
    /// </summary>
    MoveDirectory,

    /// <summary>
    /// ファイルをコピーします。
    /// </summary>
    CopyFile,

    /// <summary>
    /// ディレクトリを再帰的にコピーします。
    /// </summary>
    CopyDirectory,

    /// <summary>
    /// ファイルを直接削除します。
    /// </summary>
    DeleteFileDirect,

    /// <summary>
    /// シェル API 経由でファイルを削除します。
    /// </summary>
    DeleteFileShell,

    /// <summary>
    /// ディレクトリを直接削除します。
    /// </summary>
    DeleteDirectoryDirect,

    /// <summary>
    /// シェル API 経由でディレクトリを削除します。
    /// </summary>
    DeleteDirectoryShell,

    /// <summary>
    /// ファイルまたはディレクトリのタイムスタンプを更新します。
    /// </summary>
    SetTimestamps
}
