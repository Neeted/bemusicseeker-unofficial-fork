using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    /// <summary>出力先一覧の設定形式を解釈できません。</summary>
    InvalidConfiguration,
    /// <summary>属性取得または列挙で入出力エラーが発生しました。</summary>
    Io,
    /// <summary>出力先の確認用ファイルを作成・書込みできません。</summary>
    Write,
    /// <summary>自己所有の確認用ファイルを削除できません。</summary>
    Cleanup
}

/// <summary>
/// 必須ディレクトリの検査に失敗したことを表します。
/// </summary>
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "用途・パス・原因を必須とし、復旧案内に必要な情報を欠く例外の生成を防ぐため。")]
internal sealed class LibraryDirectoryPreflightException : IOException
{
    /// <summary>
    /// 主原因と後始末の失敗を分けて保持し、shell の通知と診断へ渡します。
    /// </summary>
    internal LibraryDirectoryPreflightException(
        LibraryDirectoryPreflightUse use,
        string directoryPath,
        LibraryDirectoryPreflightFailureCause cause,
        string reason,
        LibraryDirectoryPreflightOutputBaseKind? outputBaseKind = null,
        string probePath = null,
        Exception innerException = null,
        Exception cleanupException = null)
        : base(
            CreateMessage(use, directoryPath, cause, reason, probePath, cleanupException),
            innerException)
    {
        Use = use;
        DirectoryPath = directoryPath ?? string.Empty;
        Cause = cause;
        Reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        OutputBaseKind = outputBaseKind;
        ProbePath = probePath;
        CleanupException = cleanupException;
        CleanupCause = cleanupException == null
            ? null
            : LibraryDirectoryPreflightFailureCause.Cleanup;
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

    /// <summary>自己所有 probe file の予定または残留 path です。</summary>
    internal string ProbePath { get; }

    /// <summary>cleanup に失敗した例外です。</summary>
    internal Exception CleanupException { get; }

    /// <summary>cleanup 失敗がある場合の原因です。</summary>
    internal LibraryDirectoryPreflightFailureCause? CleanupCause { get; }

    private static string CreateMessage(
        LibraryDirectoryPreflightUse use,
        string directoryPath,
        LibraryDirectoryPreflightFailureCause cause,
        string reason,
        string probePath,
        Exception cleanupException)
    {
        return "Required library directory preflight failed."
            + " use=" + use
            + " path=" + (directoryPath ?? string.Empty)
            + " cause=" + cause
            + " reason=" + (string.IsNullOrWhiteSpace(reason) ? "unknown" : reason)
            + (string.IsNullOrWhiteSpace(probePath) ? string.Empty : " probe=" + probePath)
            + (cleanupException == null ? string.Empty : " cleanup=failed");
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

    /// <summary>属性・直下列挙と write probe を行う出力 base です。</summary>
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
/// 更新前検査が利用する狭い filesystem 境界です。
/// </summary>
internal interface ILibraryDirectoryPreflightFileSystem
{
    /// <summary>ファイルまたはディレクトリの属性を取得します。</summary>
    FileAttributes GetAttributes(string path);

    /// <summary>対象ディレクトリの直下エントリ列挙を開始します。</summary>
    IEnumerable<string> EnumerateDirectoryEntries(string path);

    /// <summary>対象 base 内に自己所有 probe file の path を生成します。</summary>
    string CreateOwnedProbePath(string directoryPath, string purpose);

    /// <summary>指定された path を指定された mode で開きます。</summary>
    Stream Open(string path, FileMode mode, FileAccess access, FileShare share);

    /// <summary>自己所有 probe file を削除します。</summary>
    void DeleteFile(string path);
}

/// <summary>
/// BMS root と LR2 output base を更新境界で検査します。
/// BMS root は read-only、output base は初回だけ自己所有 probe file を
/// 作成・書込み・削除します。
/// </summary>
internal sealed class LibraryDirectoryPreflightService
{
    private static readonly byte[] OutputProbeBytes = [0x42, 0x4D, 0x53];

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
        List<string> requiredBmsRoots = registeredRoots
            .Where(root => !outputBases.Any(outputBase =>
                LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(
                    root,
                    outputBase.DirectoryPath)))
            .ToList();
        return new LibraryDirectoryPreflightRequest(requiredBmsRoots, scanRoots, outputBases);
    }

    /// <summary>
    /// request の全 root を read-only 検査し、必要なら output base の write probe
    /// も一度だけ実行します。
    /// </summary>
    internal void EnsureAvailable(
        LibraryDirectoryPreflightRequest request,
        bool probeOutputBases)
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
            ProbeOutputBase(outputBase, probeOutputBases);
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

    private void ProbeOutputBase(
        LibraryDirectoryPreflightOutputBase outputBase,
        bool writeProbe)
    {
        ProbeDirectoryReadOnly(
            outputBase.DirectoryPath,
            LibraryDirectoryPreflightUse.Lr2OutputBase,
            outputBase.Kind);
        if (!writeProbe)
        {
            return;
        }

        string probePath = null;
        bool probeCreated = false;
        Exception writeFailure = null;
        try
        {
            probePath = fileSystem.CreateOwnedProbePath(
                outputBase.DirectoryPath,
                "directory-preflight");
            using Stream stream = fileSystem.Open(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            probeCreated = true;
            stream.Write(OutputProbeBytes, 0, OutputProbeBytes.Length);
            stream.Flush();
        }
        catch (Exception ex)
        {
            writeFailure = ex;
        }

        Exception cleanupFailure = null;
        if (probeCreated)
        {
            try
            {
                fileSystem.DeleteFile(probePath);
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }
        }

        if (writeFailure == null && cleanupFailure == null)
        {
            return;
        }

        LibraryDirectoryPreflightFailureCause cause = writeFailure == null
            ? LibraryDirectoryPreflightFailureCause.Cleanup
            : LibraryDirectoryPreflightFailureCause.Write;
        string reason = writeFailure == null
            ? "cleanup_failed"
            : cleanupFailure == null
                ? "write_failed"
                : "write_failed;cleanup_failed";
        throw CreateFailure(
            LibraryDirectoryPreflightUse.Lr2OutputBase,
            outputBase.DirectoryPath,
            cause,
            reason,
            outputBase.Kind,
            probePath,
            writeFailure ?? cleanupFailure,
            cleanupFailure);
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

    private List<string> NormalizeDirectories(
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
            FileNotFoundException or DirectoryNotFoundException =>
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
        string probePath = null,
        Exception innerException = null,
        Exception cleanupException = null)
    {
        return new LibraryDirectoryPreflightException(
            use,
            directoryPath,
            cause,
            reason,
            outputBaseKind,
            probePath,
            innerException,
            cleanupException);
    }

    private sealed class LongPathLibraryDirectoryPreflightFileSystem
        : ILibraryDirectoryPreflightFileSystem
    {
        /// <inheritdoc />
        public FileAttributes GetAttributes(string path) => LongPathFileSystem.GetAttributes(path);

        /// <inheritdoc />
        public IEnumerable<string> EnumerateDirectoryEntries(string path) =>
            LongPathFileSystem.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly);

        /// <inheritdoc />
        public string CreateOwnedProbePath(string directoryPath, string purpose)
        {
            string normalizedDirectoryPath = LongPathFileSystem.NormalizePathForStorage(directoryPath);
            string normalizedPurpose = string.IsNullOrWhiteSpace(purpose)
                ? "probe"
                : new string(purpose.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedPurpose))
            {
                normalizedPurpose = "probe";
            }

            string candidatePath;
            do
            {
                candidatePath = Path.Combine(
                    normalizedDirectoryPath,
                    ".bemusicseeker-" + normalizedPurpose + "-" + Guid.NewGuid().ToString("N") + ".tmp");
            }
            while (LongPathFileSystem.EntryExists(candidatePath));
            return candidatePath;
        }

        /// <inheritdoc />
        public Stream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
            LongPathFileSystem.Open(path, mode, access, share);

        /// <inheritdoc />
        public void DeleteFile(string path) => LongPathFileSystem.DeleteFile(path);
    }
}
