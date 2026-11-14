using System;
using System.IO;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.LR2;

/// <summary>
/// 実行時の全処理が使うLR2ルート（実効LR2ルート）を、設定値から一か所で決めます。
/// 推測値は設定へ保存せず、保存値を扱う処理（設定画面の入力欄、環境の確定・比較）は保存値のまま扱います。
/// </summary>
internal static class Lr2EffectiveRoot
{
    private const string Lr2FilesDirectoryName = "LR2files";

    /// <summary>確定設定の実効LR2ルートを返します。決められなければ空文字です。</summary>
    internal static string Resolve(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Resolve(settings.OperationModeLR2DB, settings.LR2RootPath, settings.LR2SongDBPath, settings.LR2ConfigXmlPath);
    }

    /// <summary>確定前の候補を含む環境の組から、その環境の実効LR2ルートを返します。</summary>
    internal static string Resolve(EnvironmentSettingsChange environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Resolve(environment.OperationModeLR2DB, environment.LR2RootPath, environment.LR2SongDBPath, environment.LR2ConfigXmlPath);
    }

    /// <summary>
    /// 保存ルートが空白でなければその値を返します。空白の場合は、LR2連携モードで song.db と設定XMLが
    /// 同じ <c>X</c> の標準配置（<c>X\LR2files\Database\song.db</c>、<c>X\LR2files\Config\config.xml</c> または <c>config.xmh</c>）に
    /// あるときだけ <c>X</c> を返します。片方だけ標準配置、<c>X</c> の食い違い、完全修飾でないパスでは推測しません。
    /// </summary>
    /// <remarks>
    /// ファイルや実行ファイルの存在は確認せず、ディスクを読みません。完全修飾でないパスを現在ディレクトリで補うと
    /// 起動場所によって結果が変わるため、推測の対象から外します。
    /// </remarks>
    /// <param name="operationModeLR2DB">LR2連携モードかどうか。</param>
    /// <param name="savedRootPath">設定に保存されたLR2ディレクトリ。</param>
    /// <param name="songDbPath">設定に保存された song.db のパス。</param>
    /// <param name="configXmlPath">設定に保存された設定XMLのパス。</param>
    /// <returns>実効LR2ルート。決められなければ空文字。</returns>
    internal static string Resolve(bool operationModeLR2DB, string savedRootPath, string songDbPath, string configXmlPath)
    {
        if (!string.IsNullOrWhiteSpace(savedRootPath))
        {
            return savedRootPath;
        }
        if (!operationModeLR2DB)
        {
            return string.Empty;
        }

        string songDbRoot = GetStandardLayoutRoot(songDbPath, "Database", "song.db");
        if (songDbRoot == null)
        {
            return string.Empty;
        }
        string configRoot = GetStandardLayoutRoot(configXmlPath, "Config", "config.xml", "config.xmh");
        return string.Equals(songDbRoot, configRoot, StringComparison.OrdinalIgnoreCase) ? songDbRoot : string.Empty;
    }

    /// <summary>
    /// 起動時にLR2の <c>song</c>・<c>folder</c> 表を補正する基準のルートを返します。LR2連携モードで、song.db が実効LR2ルートの
    /// 標準配置（<c>実効ルート\LR2files\Database\song.db</c>）にあるときだけ実効LR2ルートを返し、それ以外は空を返します。
    /// </summary>
    /// <remarks>
    /// 別の場所に置いた song.db の相対パスは、どのLR2フォルダを基準に書かれたか分からないため、保存したLR2ルートがあっても補正しません。
    /// 既存の song.db を新たに書き換えないための条件で、song.db の場所からルートを推測するものではありません。
    /// </remarks>
    /// <param name="operationModeLR2DB">LR2連携モードかどうか。</param>
    /// <param name="effectiveRootPath"><see cref="Resolve(bool, string, string, string)"/> が決めた実効LR2ルート。</param>
    /// <param name="songDbPath">読み込む song.db のパス。</param>
    /// <returns>補正の基準にする実効LR2ルート。補正しない場合は空文字。</returns>
    internal static string ResolveSongTableCorrectionRoot(bool operationModeLR2DB, string effectiveRootPath, string songDbPath)
    {
        if (!operationModeLR2DB || string.IsNullOrWhiteSpace(effectiveRootPath))
        {
            return string.Empty;
        }
        string songDbRoot = GetStandardLayoutRoot(songDbPath, "Database", "song.db");
        return songDbRoot != null && IsSameRoot(songDbRoot, effectiveRootPath) ? effectiveRootPath : string.Empty;
    }

    /// <summary>二つのLR2ルートが同じ場所を指すかを、空白同士も含めて判定します。</summary>
    internal static bool IsSameRoot(string left, string right)
    {
        bool leftEmpty = string.IsNullOrWhiteSpace(left);
        bool rightEmpty = string.IsNullOrWhiteSpace(right);
        if (leftEmpty || rightEmpty)
        {
            return leftEmpty && rightEmpty;
        }
        return string.Equals(NormalizeOrOriginal(left), NormalizeOrOriginal(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOrOriginal(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary><c>X\LR2files\{directoryName}\{fileName}</c> の <c>X</c> を返します。標準配置でなければ null です。</summary>
    private static string GetStandardLayoutRoot(string filePath, string directoryName, params string[] fileNames)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }
        try
        {
            if (!Path.IsPathFullyQualified(filePath))
            {
                return null;
            }
            string normalized = Path.GetFullPath(filePath);
            if (!Array.Exists(fileNames, name => string.Equals(Path.GetFileName(normalized), name, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
            string directory = Path.GetDirectoryName(normalized);
            if (!string.Equals(Path.GetFileName(directory), directoryName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string lr2Files = Path.GetDirectoryName(directory);
            if (!string.Equals(Path.GetFileName(lr2Files), Lr2FilesDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string root = Path.GetDirectoryName(lr2Files);
            return string.IsNullOrWhiteSpace(root) ? null : root;
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
