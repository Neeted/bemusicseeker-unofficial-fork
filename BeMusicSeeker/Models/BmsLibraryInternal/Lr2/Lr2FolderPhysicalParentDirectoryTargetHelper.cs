using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>既存の実ルートと生成停止境界から、必要親とROOT判定を純粋に求めます。</summary>
internal static class Lr2FolderPhysicalParentDirectoryTargetHelper
{
    /// <summary>登録ルートと通常・追加基点から、祖先包含で落とさない実ルート集合を作ります。</summary>
    internal static IReadOnlySet<string> CreateRoots(
        IEnumerable<string> registeredRoots, string normalBase, IEnumerable<string> additionalBases)
    {
        return new HashSet<string>((registeredRoots ?? []).Concat([normalBase]).Concat(additionalBases ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>実ルートの親を停止境界にします。親を持たないドライブ・共有ルートは自分を境界とし、実ルートとして別途含めます。</summary>
    internal static string CreateBoundary(string root)
    {
        string normalized = Lr2FolderPath.NormalizeDirectoryPath(root);
        return Lr2FolderPath.SafeGetParentNormalizedDirectory(normalized) ?? normalized;
    }

    /// <summary>実ルートの親、ROOT出力基点、組込み元を生成停止境界へまとめます。</summary>
    internal static IReadOnlySet<string> CreateBoundaries(
        IEnumerable<string> roots, string rootOutputBase, IEnumerable<string> builtinDirectories)
    {
        return new HashSet<string>((roots ?? []).Select(CreateBoundary).Concat([rootOutputBase]).Concat(builtinDirectories ?? [])
            .Select(NormalizeDirectory).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>一要求で構築済みの実ROOT・境界setを再利用し、各親で全rootの再正規化やset再構築を行いません。</summary>
    internal static IEnumerable<(string DatabaseDirectory, string PhysicalDirectory)> EnumerateNormalizedParents(
        string databasePath, string physicalPath, IReadOnlySet<string> boundaries, IReadOnlySet<string> roots)
    {
        string directory = ParentDirectory(databasePath);
        string physicalDirectory = ParentDirectory(physicalPath);
        while (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(physicalDirectory))
        {
            bool isBoundary = boundaries.Contains(directory);
            if (!roots.Contains(directory) && isBoundary && !IsUnderBoundary(directory, roots)
                || !IsUnderBoundary(directory, boundaries))
            {
                yield break;
            }
            if (!string.Equals(directory, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase))
            {
                yield return (directory, physicalDirectory);
            }
            directory = ParentNormalizedDirectory(directory);
            physicalDirectory = ParentNormalizedDirectory(physicalDirectory);
        }
    }

    /// <summary>正規化済みの対象と実ROOT集合から親CRCを求めます。ROOT判定はset照合と実親の比較だけで行います。</summary>
    internal static string ComputeNormalizedParentHash(string directory, IReadOnlySet<string> roots, string rootOutputBase = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) { return null; }
        string parent = ParentNormalizedDirectory(directory);
        if (roots.Contains(directory)
            || !string.IsNullOrWhiteSpace(rootOutputBase) && string.Equals(parent, rootOutputBase, StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase))
        {
            return Lr2SongFolderParentNormalizer.RootParentHash;
        }
        try
        {
            return string.IsNullOrWhiteSpace(parent) ? null : Lr2SongFolderParentNormalizer.ComputeDirectoryHash(parent);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is EncoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>同じ物理親のファイルを先にまとめ、必要な親ディレクトリだけを返します。</summary>
    internal static IReadOnlyCollection<string> CreateTargets(
        IEnumerable<string> files, IEnumerable<string> registeredRoots, string normalBase,
        IEnumerable<string> additionalBases, string rootOutputBase, IEnumerable<string> builtinDirectories)
    {
        IReadOnlySet<string> roots = CreateRoots(registeredRoots, normalBase, additionalBases);
        IReadOnlySet<string> boundaries = CreateBoundaries(roots, rootOutputBase, builtinDirectories);
        return [.. (files ?? []).DistinctBy(ParentDirectory, StringComparer.OrdinalIgnoreCase)
            .SelectMany(file => EnumerateNormalizedParents(file, file, boundaries, roots))
            .Select(target => target.PhysicalDirectory).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>既知相対DB表記と絶対物理表記を混ぜず、階層比較用に正規化します。</summary>
    internal static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        return Path.IsPathRooted(path) ? Lr2FolderPath.NormalizeDirectoryPath(path)
            : path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>ドライブ・共有ルートの意味を保ったまま一段上の表記を返します。</summary>
    internal static string ParentDirectory(string path)
    {
        string normalized = NormalizeDirectory(path);
        return NormalizeDirectory(Lr2FolderPath.SafeGetDirectoryName(normalized));
    }

    private static string ParentNormalizedDirectory(string path) => Lr2FolderPath.SafeGetDirectoryName(path);

    private static bool IsUnderBoundary(string path, IReadOnlySet<string> boundaries)
    {
        string current = path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (boundaries.Contains(current)) { return true; }
            current = ParentNormalizedDirectory(current);
        }
        return false;
    }
}
