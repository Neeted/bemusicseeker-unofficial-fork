using System;
using Microsoft.VisualBasic.FileIO;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// ReadOnly 補正、短時間リトライ、ログ集約を伴う変更系ファイル操作を提供します。
/// </summary>
internal interface IFileMutationService
{
    /// <summary>
    /// 必要に応じてディレクトリを作成します。
    /// </summary>
    /// <param name="directoryPath">作成または存在確認するディレクトリパスです。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void EnsureDirectory(string directoryPath, FileMutationOptions options = null);

    /// <summary>
    /// ファイルを移動します。
    /// </summary>
    /// <param name="sourcePath">移動元ファイルパスです。</param>
    /// <param name="destinationPath">移動先ファイルパスです。</param>
    /// <param name="overwrite">既存ファイルを上書きする場合は true です。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null);

    /// <summary>
    /// ディレクトリを移動します。
    /// </summary>
    /// <param name="sourcePath">移動元ディレクトリパスです。</param>
    /// <param name="destinationPath">移動先ディレクトリパスです。</param>
    /// <param name="overwrite">既存ディレクトリを上書きする場合は true です。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null);

    /// <summary>
    /// ファイルを destination filesystem 内の staging path へコピーします。
    /// </summary>
    /// <param name="sourcePath">コピー元ファイルパスです。</param>
    /// <param name="destinationPath">コピー先ファイルパスです。</param>
    /// <param name="overwrite">既存ファイルを上書きする場合は true です。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null);

    /// <summary>
    /// ディレクトリを destination filesystem 内の staging path へ再帰コピーします。
    /// </summary>
    /// <param name="sourcePath">コピー元ディレクトリパスです。</param>
    /// <param name="destinationPath">コピー先ディレクトリパスです。</param>
    /// <param name="overwrite">既存ファイルを上書きする場合は true です。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null);

    /// <summary>
    /// ファイルを直接削除します。
    /// </summary>
    /// <param name="filePath">削除対象のファイルパスです。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void DeleteFileDirect(string filePath, FileMutationOptions options = null);

    /// <summary>
    /// シェル API 経由でファイルを削除します。
    /// </summary>
    /// <param name="filePath">削除対象のファイルパスです。</param>
    /// <param name="uiOption">削除時の UI 表示オプションです。</param>
    /// <param name="recycleOption">ごみ箱経由か恒久削除かの指定です。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null);

    /// <summary>
    /// ディレクトリを直接削除します。
    /// </summary>
    /// <param name="directoryPath">削除対象のディレクトリパスです。</param>
    /// <param name="recursive">配下も含めて削除する場合は true です。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null);

    /// <summary>
    /// シェル API 経由でディレクトリを削除します。
    /// </summary>
    /// <param name="directoryPath">削除対象のディレクトリパスです。</param>
    /// <param name="uiOption">削除時の UI 表示オプションです。</param>
    /// <param name="recycleOption">ごみ箱経由か恒久削除かの指定です。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null);

    /// <summary>
    /// ファイルまたはディレクトリの作成日時と更新日時を設定します。
    /// </summary>
    /// <param name="path">対象パスです。</param>
    /// <param name="isDirectory">対象がディレクトリの場合は true です。</param>
    /// <param name="creationTime">設定する作成日時です。null の場合は変更しません。</param>
    /// <param name="lastWriteTime">設定する更新日時です。null の場合は変更しません。</param>
    /// <param name="options">ReadOnly 補正とリトライの設定です。</param>
    void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null);
}
