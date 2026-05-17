using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ディレクトリ探索で見つかった Pending パッケージ群と、
/// 再統合候補として扱ってよいソースディレクトリ群を返します。
/// </summary>
internal sealed class ChartPackageDiscoveryResult
{
    /// <summary>
    /// 探索で見つかった chart package 一覧です。
    /// </summary>
    public List<ChartPackage> Packages { get; } = new List<ChartPackage>();

    /// <summary>
    /// フォルダ走査由来で split file-package が生成されたソースディレクトリ一覧です。
    /// </summary>
    public List<string> RegroupEligibleSourceDirectories { get; } = new List<string>();
}
