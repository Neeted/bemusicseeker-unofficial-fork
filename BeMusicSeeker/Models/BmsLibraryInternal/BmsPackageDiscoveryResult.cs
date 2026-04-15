using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ディレクトリ探索で見つかった Pending パッケージ群と、
/// 再統合候補として扱ってよいソースディレクトリ群を返します。
/// </summary>
internal sealed class BmsPackageDiscoveryResult
{
    /// <summary>
    /// 探索で見つかった BMS パッケージ一覧です。
    /// </summary>
    public List<BMSPackage> Packages { get; } = new List<BMSPackage>();

    /// <summary>
    /// フォルダ走査由来で split file-package が生成されたソースディレクトリ一覧です。
    /// </summary>
    public List<string> RegroupEligibleSourceDirectories { get; } = new List<string>();
}
