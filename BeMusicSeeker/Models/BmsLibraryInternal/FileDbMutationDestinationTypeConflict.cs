using System;
using System.IO;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 宛先の予定型と、検証時に確認した既存型を保持する immutable な拒否事実です。
/// </summary>
internal sealed class FileDbMutationDestinationTypeConflict
{
    /// <summary>
    /// 1 件の拒否について元、宛先、確認した entry の型を保持します。
    /// </summary>
    /// <param name="sourcePath">公開予定だった元 path。</param>
    /// <param name="destinationPath">型が衝突した宛先。</param>
    /// <param name="expectedIsDirectory">計画が宛先に directory を予定したかどうか。</param>
    /// <param name="existingIsDirectory">既存の entry が directory かどうか。</param>
    internal FileDbMutationDestinationTypeConflict(
        string sourcePath,
        string destinationPath,
        bool expectedIsDirectory,
        bool existingIsDirectory)
    {
        SourcePath = RequirePath(sourcePath, nameof(sourcePath));
        DestinationPath = RequirePath(destinationPath, nameof(destinationPath));
        ExpectedIsDirectory = expectedIsDirectory;
        ExistingIsDirectory = existingIsDirectory;
    }

    /// <summary>拒否した操作に関連する元 path を取得します。</summary>
    internal string SourcePath { get; }

    /// <summary>型が衝突した具体的な宛先 path を取得します。</summary>
    internal string DestinationPath { get; }

    /// <summary>操作が宛先に directory を予定したかどうかを取得します。</summary>
    internal bool ExpectedIsDirectory { get; }

    /// <summary>既存の宛先 entry が directory かどうかを取得します。</summary>
    internal bool ExistingIsDirectory { get; }

    private static string RequirePath(string path, string parameterName)
    {
        return string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A conflict path is required.", parameterName)
            : path;
    }
}

/// <summary>
/// ファイルとディレクトリの置換を拒否したことを示す専用 IOException です。
/// </summary>
/// <remarks>source、宛先、予定型、既存型を保持することが拒否事実の契約であるため、標準の簡略コンストラクターは提供しません。</remarks>
internal sealed class FileDbMutationDestinationTypeConflictException : IOException
{
    /// <summary>
    /// 宛先型衝突の例外と immutable な事実を作成します。
    /// </summary>
    /// <param name="sourcePath">公開予定だった元 path。</param>
    /// <param name="destinationPath">型が衝突した宛先。</param>
    /// <param name="expectedIsDirectory">計画が宛先に directory を予定したかどうか。</param>
    /// <param name="existingIsDirectory">既存の entry が directory かどうか。</param>
    internal FileDbMutationDestinationTypeConflictException(
        string sourcePath,
        string destinationPath,
        bool expectedIsDirectory,
        bool existingIsDirectory)
        : this(new FileDbMutationDestinationTypeConflict(
            sourcePath,
            destinationPath,
            expectedIsDirectory,
            existingIsDirectory))
    {
    }

    /// <summary>取得済みの immutable な衝突事実から例外を作成します。</summary>
    /// <param name="conflict">保持する衝突事実。</param>
    internal FileDbMutationDestinationTypeConflictException(
        FileDbMutationDestinationTypeConflict conflict)
        : base(BuildMessage(conflict))
    {
        Conflict = conflict ?? throw new ArgumentNullException(nameof(conflict));
        SourcePath = conflict.SourcePath;
        DestinationPath = conflict.DestinationPath;
        ExpectedIsDirectory = conflict.ExpectedIsDirectory;
        ExistingIsDirectory = conflict.ExistingIsDirectory;
    }

    /// <summary>この例外が保持する immutable な衝突事実を取得します。</summary>
    internal FileDbMutationDestinationTypeConflict Conflict { get; }

    /// <summary>衝突事実が保持する元 path を取得します。</summary>
    internal string SourcePath { get; }

    /// <summary>衝突した宛先 path を取得します。</summary>
    internal string DestinationPath { get; }

    /// <summary>mutation が directory を予定したかどうかを取得します。</summary>
    internal bool ExpectedIsDirectory { get; }

    /// <summary>既存の宛先が directory かどうかを取得します。</summary>
    internal bool ExistingIsDirectory { get; }

    private static string BuildMessage(FileDbMutationDestinationTypeConflict conflict)
    {
        if (conflict == null)
        {
            return "The destination type conflicts with the mutation plan.";
        }
        string expectedType = conflict.ExpectedIsDirectory ? "directory" : "file";
        string existingType = conflict.ExistingIsDirectory ? "directory" : "file";
        return "The mutation plan expects a "
            + expectedType
            + " at destination '"
            + conflict.DestinationPath
            + "', but an existing "
            + existingType
            + " was found.";
    }
}

/// <summary>
/// mutation の stage、退避、公開を行う前に、宛先と必要な親 directory の
/// 実在型を読み取ります。
/// </summary>
internal static class FileDbMutationDestinationTypeGuard
{
    /// <summary>
    /// stage 作業の前に、計画で宣言された順序ですべての path を検証します。
    /// </summary>
    /// <param name="plan">検査する immutable な mutation 計画。</param>
    internal static void ValidatePlan(FileDbMutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        foreach (FileDbMutationPathPlan path in plan.Paths)
        {
            ValidatePath(path.SourcePath, path.DestinationPath, path.IsDirectory);
        }
    }

    /// <summary>
    /// 1 つの最終宛先と、作成に必要な既存の親をすべて検証します。
    /// 存在しない親は executor による作成を許可します。
    /// </summary>
    /// <param name="sourcePath">宛先に関連する元 path。</param>
    /// <param name="destinationPath">検査する宛先 path。</param>
    /// <param name="expectedIsDirectory">操作が directory を公開するかどうか。</param>
    internal static void ValidatePath(
        string sourcePath,
        string destinationPath,
        bool expectedIsDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        }

        bool existingDirectory = LongPathFileSystem.DirectoryExists(destinationPath);
        bool existingFile = LongPathFileSystem.FileExists(destinationPath);
        if ((expectedIsDirectory && existingFile)
            || (!expectedIsDirectory && existingDirectory))
        {
            throw new FileDbMutationDestinationTypeConflictException(
                sourcePath,
                destinationPath,
                expectedIsDirectory,
                existingDirectory);
        }

        ValidateParentDirectories(sourcePath, destinationPath);
    }

    private static void ValidateParentDirectories(string sourcePath, string destinationPath)
    {
        string parentPath = Path.GetDirectoryName(destinationPath);
        while (!string.IsNullOrWhiteSpace(parentPath))
        {
            if (LongPathFileSystem.FileExists(parentPath))
            {
                throw new FileDbMutationDestinationTypeConflictException(
                    sourcePath,
                    parentPath,
                    expectedIsDirectory: true,
                    existingIsDirectory: false);
            }
            if (LongPathFileSystem.DirectoryExists(parentPath))
            {
                return;
            }

            string nextParentPath = Path.GetDirectoryName(parentPath);
            if (string.Equals(nextParentPath, parentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            parentPath = nextParentPath;
        }
    }
}
