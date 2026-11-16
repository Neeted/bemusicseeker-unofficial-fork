using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 更新前検査で対象を分類します。
/// </summary>
internal enum LibraryDirectoryPreflightUse
{
    /// <summary>譜面を読み取る登録ルートです。</summary>
    BmsRoot,
    /// <summary>LR2 カスタムフォルダを書き出すベースです。</summary>
    Lr2OutputBase
}

/// <summary>
/// LR2 の出力ベースを設定上の用途で分類します。
/// </summary>
internal enum LibraryDirectoryPreflightOutputBaseKind
{
    /// <summary>通常のカスタムフォルダ出力先です。</summary>
    Normal,
    /// <summary>追加登録されたカスタムフォルダ出力先です。</summary>
    Additional,
    /// <summary>ルート型カスタムフォルダの出力先です。</summary>
    RootType
}

/// <summary>
/// 更新前検査が失敗した原因を分類します。
/// </summary>
internal enum LibraryDirectoryPreflightFailureCause
{
    /// <summary>対象を見つけられません。</summary>
    NotFound,
    /// <summary>読取りアクセスが拒否されました。</summary>
    AccessDenied,
    /// <summary>対象はディレクトリではありません。</summary>
    NotDirectory,
    /// <summary>設定されたパスを解釈できません。</summary>
    InvalidPath,
    /// <summary>出力先一覧の形式または受理する設定の配置条件が不正です。</summary>
    InvalidConfiguration,
    /// <summary>属性取得または列挙で入出力エラーが発生しました。</summary>
    Io
}

/// <summary>
/// 必須ディレクトリの検査に失敗したことを表します。
/// </summary>
/// <remarks>用途、パス、原因を保持し、復旧案内に必要な情報を欠く簡略コンストラクターは提供しません。</remarks>
internal sealed class LibraryDirectoryPreflightException : IOException
{
    /// <summary>
    /// 用途・パス・原因を保持し、shell の通知と診断へ渡します。
    /// </summary>
    internal LibraryDirectoryPreflightException(
        LibraryDirectoryPreflightUse use,
        string directoryPath,
        LibraryDirectoryPreflightFailureCause cause,
        string reason,
        LibraryDirectoryPreflightOutputBaseKind? outputBaseKind = null,
        Exception innerException = null)
        : base(
            CreateMessage(use, directoryPath, cause, reason),
            innerException)
    {
        Use = use;
        DirectoryPath = directoryPath ?? string.Empty;
        Cause = cause;
        Reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        OutputBaseKind = outputBaseKind;
    }

    /// <summary>検査対象の用途です。</summary>
    internal LibraryDirectoryPreflightUse Use { get; }

    /// <summary>検査対象のディレクトリです。</summary>
    internal string DirectoryPath { get; }

    /// <summary>主たる失敗原因です。</summary>
    internal LibraryDirectoryPreflightFailureCause Cause { get; }

    /// <summary>診断・表示用の理由です。</summary>
    internal string Reason { get; }

    /// <summary>出力 base の設定上の用途です。</summary>
    internal LibraryDirectoryPreflightOutputBaseKind? OutputBaseKind { get; }

    private static string CreateMessage(
        LibraryDirectoryPreflightUse use,
        string directoryPath,
        LibraryDirectoryPreflightFailureCause cause,
        string reason)
    {
        return "Required library directory preflight failed."
            + " use=" + use
            + " path=" + (directoryPath ?? string.Empty)
            + " cause=" + cause
            + " reason=" + (string.IsNullOrWhiteSpace(reason) ? "unknown" : reason);
    }
}

/// <summary>
/// 検査対象の LR2 出力ベースと用途を保持します。
/// </summary>
internal sealed class LibraryDirectoryPreflightOutputBase
{
    /// <summary>出力先のパスと設定上の用途を不可変な対象として保持します。</summary>
    internal LibraryDirectoryPreflightOutputBase(
        string directoryPath,
        LibraryDirectoryPreflightOutputBaseKind kind)
    {
        DirectoryPath = directoryPath ?? throw new ArgumentNullException(nameof(directoryPath));
        Kind = kind;
    }

    /// <summary>検査対象のディレクトリです。</summary>
    internal string DirectoryPath { get; }

    /// <summary>設定上の出力ベース用途です。</summary>
    internal LibraryDirectoryPreflightOutputBaseKind Kind { get; }
}

/// <summary>
/// 更新入口で必要なディレクトリ検査の対象を表します。
/// </summary>
internal sealed class LibraryDirectoryPreflightRequest
{
    /// <summary>入口の対象集合を複製し、走査中の再取得による登録脱落を防ぎます。</summary>
    internal LibraryDirectoryPreflightRequest(
        IReadOnlyList<string> bmsRootDirectories,
        IReadOnlyList<string> scanRootDirectories,
        IReadOnlyList<LibraryDirectoryPreflightOutputBase> outputBaseTargets)
    {
        BmsRootDirectories = Freeze(bmsRootDirectories);
        ScanRootDirectories = Freeze(scanRootDirectories);
        OutputBaseTargets = Freeze(outputBaseTargets);
        OutputBaseDirectories = Array.AsReadOnly(
            OutputBaseTargets.Select(target => target.DirectoryPath).ToArray());
    }

    /// <summary>属性・直下列挙を行う BMS root です。</summary>
    internal IReadOnlyList<string> BmsRootDirectories { get; }

    /// <summary>scanner に渡す同一操作の root 集合です。</summary>
    internal IReadOnlyList<string> ScanRootDirectories { get; }

    /// <summary>BMS root と同じ属性・直下列挙を行う出力 base です。</summary>
    internal IReadOnlyList<LibraryDirectoryPreflightOutputBase> OutputBaseTargets { get; }

    /// <summary>用途を除いた出力 base path の read view です。</summary>
    internal IReadOnlyList<string> OutputBaseDirectories { get; }

    private static IReadOnlyList<string> Freeze(IReadOnlyList<string> paths)
    {
        return Array.AsReadOnly((paths ?? []).ToArray());
    }

    private static IReadOnlyList<LibraryDirectoryPreflightOutputBase> Freeze(
        IReadOnlyList<LibraryDirectoryPreflightOutputBase> targets)
    {
        return Array.AsReadOnly((targets ?? []).ToArray());
    }
}

/// <summary>
/// 更新前検査が利用する、読取りだけの狭い filesystem 境界です。
/// </summary>
internal interface ILibraryDirectoryPreflightFileSystem
{
    /// <summary>ファイルまたはディレクトリの属性を取得します。</summary>
    FileAttributes GetAttributes(string path);

    /// <summary>対象ディレクトリの直下エントリ列挙を開始します。</summary>
    IEnumerable<string> EnumerateDirectoryEntries(string path);
}

/// <summary>
/// BMS root と LR2 output base を更新境界で検査します。
/// どちらも属性取得と直下列挙だけの読取り検査です。
/// </summary>
/// <remarks>
/// output base の書込み可否は検査しません。確認用ファイルの作成・削除は base の更新日時を変え、
/// 実ディレクトリの時刻と一致させる LR2 の folder.date を毎回不一致にするためです。
/// 書込み不能は実際の出力時点の失敗として扱います。
/// </remarks>
internal sealed class LibraryDirectoryPreflightService
{
    /// <summary>既知のリソース化済み予約領域理由だけを通知へ渡す診断識別子です。任意の内側例外の表示には使いません。</summary>
    internal const string ReservedBuiltinCustomFolderReason = "reserved_builtin_custom_folder";

    private readonly ILibraryDirectoryPreflightFileSystem fileSystem;

    /// <summary>検査に使用する filesystem 境界を選びます。省略時は実 filesystem を使用します。</summary>
    internal LibraryDirectoryPreflightService(
        ILibraryDirectoryPreflightFileSystem fileSystem = null)
    {
        this.fileSystem = fileSystem ?? new LongPathLibraryDirectoryPreflightFileSystem();
    }

    /// <summary>
    /// 設定済み root と output base から操作専用の immutable request を作成します。
    /// </summary>
    internal LibraryDirectoryPreflightRequest CreateRequest(
        IEnumerable<string> registeredBmsRootDirectories,
        IEnumerable<string> scanRootDirectories,
        BmsLibraryOptionsSnapshot options)
    {
        List<string> registeredRoots = NormalizeDirectories(
            registeredBmsRootDirectories,
            LibraryDirectoryPreflightUse.BmsRoot);
        List<string> scanRoots = NormalizeDirectories(
            scanRootDirectories,
            LibraryDirectoryPreflightUse.BmsRoot);
        List<LibraryDirectoryPreflightOutputBase> outputBases =
            CreateConfiguredOutputBaseTargets(options);
        // output base 配下の生成対象を BMS root として別検査すると、まだ作成
        // されていない playlist child まで登録 root と誤認するため除外します。
        // output base 自体は下の output 検査で必ず検査します。
        var requiredBmsRoots = registeredRoots
            .Where(root => !outputBases.Any(outputBase =>
                LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(
                    root,
                    outputBase.DirectoryPath)))
            .ToList();
        return new LibraryDirectoryPreflightRequest(requiredBmsRoots, scanRoots, outputBases);
    }

    /// <summary>
    /// request の全 root と output base を読取り検査します。対象と配下を変更しません。
    /// </summary>
    internal void EnsureAvailable(LibraryDirectoryPreflightRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        foreach (string directoryPath in request.BmsRootDirectories)
        {
            ProbeDirectoryReadOnly(
                directoryPath,
                LibraryDirectoryPreflightUse.BmsRoot,
                outputBaseKind: null);
        }
        foreach (LibraryDirectoryPreflightOutputBase outputBase in request.OutputBaseTargets)
        {
            ProbeDirectoryReadOnly(
                outputBase.DirectoryPath,
                LibraryDirectoryPreflightUse.Lr2OutputBase,
                outputBase.Kind);
        }
    }

    /// <summary>起動の設定受理時に、出力基点による利用可能性検査の除外前の登録全体と出力基点を一度だけ検査します。通常走査では呼びません。</summary>
    internal static void ValidateSettingsAcceptance(IEnumerable<string> registeredRoots, LibraryDirectoryPreflightRequest request, BmsLibraryOptionsSnapshot options)
    {
        foreach (string root in NormalizeDirectories(registeredRoots, LibraryDirectoryPreflightUse.BmsRoot))
        { ValidateReservedDirectory(root, options.LR2RootPath, LibraryDirectoryPreflightUse.BmsRoot, null); }
        foreach (LibraryDirectoryPreflightOutputBase outputBase in request.OutputBaseTargets)
        { ValidateReservedDirectory(outputBase.DirectoryPath, options.LR2RootPath, LibraryDirectoryPreflightUse.Lr2OutputBase, outputBase.Kind); }
    }

    private static void ValidateReservedDirectory(string path, string lr2Root, LibraryDirectoryPreflightUse use, LibraryDirectoryPreflightOutputBaseKind? kind)
    {
        try
        {
            CustomFolderOutputBaseSearchRootSyncService.ValidateOutsideBuiltinCustomFolder(path, lr2Root,
                use == LibraryDirectoryPreflightUse.BmsRoot ? BeMusicSeeker.Properties.Resources.LibraryDirectoryPreflightBmsRootRole
                    : BeMusicSeeker.Properties.Resources.Label_CustomFolderOutputBase,
                isOutputBase: use == LibraryDirectoryPreflightUse.Lr2OutputBase);
        }
        catch (ArgumentException failure)
        {
            throw CreateFailure(use, path, LibraryDirectoryPreflightFailureCause.InvalidConfiguration, ReservedBuiltinCustomFolderReason, kind, innerException: failure);
        }
    }

    private void ProbeDirectoryReadOnly(
        string directoryPath,
        LibraryDirectoryPreflightUse use,
        LibraryDirectoryPreflightOutputBaseKind? outputBaseKind)
    {
        try
        {
            FileAttributes attributes = fileSystem.GetAttributes(directoryPath);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw CreateFailure(
                    use,
                    directoryPath,
                    LibraryDirectoryPreflightFailureCause.NotDirectory,
                    "not_directory",
                    outputBaseKind);
            }

            using IEnumerator<string> entries = fileSystem
                .EnumerateDirectoryEntries(directoryPath)
                .GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (LibraryDirectoryPreflightException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateFailure(
                use,
                directoryPath,
                ClassifyFilesystemFailure(ex),
                "read_failed",
                outputBaseKind,
                innerException: ex);
        }
    }

    private static List<LibraryDirectoryPreflightOutputBase> CreateConfiguredOutputBaseTargets(
        BmsLibraryOptionsSnapshot options)
    {
        if (options?.OperationModeLR2DB != true)
        {
            return [];
        }

        IReadOnlyList<string> additionalDirectories;
        if (options.LR2CustomFolderAdditionalOutputBaseDirsSerialized != null)
        {
            try
            {
                additionalDirectories = CustomFolderOutputBaseRegistry
                    .DeserializeBaseDirectoriesStrict(
                        options.LR2CustomFolderAdditionalOutputBaseDirsSerialized);
            }
            catch (Exception ex)
            {
                throw CreateFailure(
                    LibraryDirectoryPreflightUse.Lr2OutputBase,
                    options.LR2CustomFolderAdditionalOutputBaseDirsSerialized,
                    LibraryDirectoryPreflightFailureCause.InvalidConfiguration,
                    "invalid_additional_output_base_configuration",
                    LibraryDirectoryPreflightOutputBaseKind.Additional,
                    innerException: ex);
            }
        }
        else
        {
            additionalDirectories = options.LR2CustomFolderAdditionalOutputBaseDirs ?? [];
        }

        List<(string path, LibraryDirectoryPreflightOutputBaseKind kind)> configured =
        [
            (options.LR2CustomFolderOutputBaseDir, LibraryDirectoryPreflightOutputBaseKind.Normal),
            .. (additionalDirectories ?? [])
                .Select(path => (path, LibraryDirectoryPreflightOutputBaseKind.Additional)),
            (options.LR2CustomFolderOutputBaseDirRootType, LibraryDirectoryPreflightOutputBaseKind.RootType)
        ];
        List<LibraryDirectoryPreflightOutputBase> targets = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, LibraryDirectoryPreflightOutputBaseKind kind) in configured)
        {
            string normalizedPath = NormalizeDirectory(
                path,
                LibraryDirectoryPreflightUse.Lr2OutputBase,
                kind);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !seen.Add(normalizedPath))
            {
                continue;
            }
            targets.Add(new LibraryDirectoryPreflightOutputBase(normalizedPath, kind));
        }
        return targets;
    }

    private static List<string> NormalizeDirectories(
        IEnumerable<string> directories,
        LibraryDirectoryPreflightUse use)
    {
        List<string> normalized = [];
        foreach (string directoryPath in directories ?? [])
        {
            string normalizedPath = NormalizeDirectory(directoryPath, use, outputBaseKind: null);
            if (!string.IsNullOrWhiteSpace(normalizedPath)
                && !normalized.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(normalizedPath);
            }
        }
        return normalized;
    }

    private static string NormalizeDirectory(
        string directoryPath,
        LibraryDirectoryPreflightUse use,
        LibraryDirectoryPreflightOutputBaseKind? outputBaseKind)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return string.Empty;
        }

        try
        {
            return LongPathFileSystem.TrimTrailingDirectorySeparators(
                LongPathFileSystem.NormalizePathForStorage(directoryPath.Trim()));
        }
        catch (Exception ex)
        {
            throw CreateFailure(
                use,
                directoryPath,
                LibraryDirectoryPreflightFailureCause.InvalidPath,
                "invalid_path",
                outputBaseKind,
                innerException: ex);
        }
    }

    private static LibraryDirectoryPreflightFailureCause ClassifyFilesystemFailure(Exception exception)
    {
        return exception switch
        {
            Exception failure when LongPathFileSystem.IsNotFoundFailure(failure) =>
                LibraryDirectoryPreflightFailureCause.NotFound,
            UnauthorizedAccessException => LibraryDirectoryPreflightFailureCause.AccessDenied,
            ArgumentException or NotSupportedException or PathTooLongException =>
                LibraryDirectoryPreflightFailureCause.InvalidPath,
            IOException => LibraryDirectoryPreflightFailureCause.Io,
            _ => LibraryDirectoryPreflightFailureCause.Io
        };
    }

    private static LibraryDirectoryPreflightException CreateFailure(
        LibraryDirectoryPreflightUse use,
        string directoryPath,
        LibraryDirectoryPreflightFailureCause cause,
        string reason,
        LibraryDirectoryPreflightOutputBaseKind? outputBaseKind = null,
        Exception innerException = null)
    {
        return new LibraryDirectoryPreflightException(
            use,
            directoryPath,
            cause,
            reason,
            outputBaseKind,
            innerException);
    }

    private sealed class LongPathLibraryDirectoryPreflightFileSystem
        : ILibraryDirectoryPreflightFileSystem
    {
        /// <inheritdoc />
        public FileAttributes GetAttributes(string path) => LongPathFileSystem.GetAttributes(path);

        /// <inheritdoc />
        public IEnumerable<string> EnumerateDirectoryEntries(string path) =>
            LongPathFileSystem.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly);
    }
}
