using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ディレクトリ探索で見つかった保留パッケージ群を返します。
/// </summary>
internal sealed class ChartPackageDiscoveryResult
{
    /// <summary>
    /// 探索で見つかった chart package 一覧です。
    /// </summary>
    public List<ChartPackage> Packages { get; } = [];

    /// <summary>一式として読取りを完了できなかったscopeです。独立した子の成功と区別して旧群を保全します。</summary>
    internal List<string> IncompleteScopes { get; } = [];

    /// <summary>独立した成功を保持したまま終端へ渡す、実際の列挙・読取り失敗です。</summary>
    internal List<Exception> Failures { get; } = [];

    /// <summary>読取り失敗の有無と独立して、列挙済みの譜面があることを保持します。</summary>
    internal bool ContainsCharts { get; set; }

    /// <summary>列挙と全譜面読取りが完了したかを返します。不完全なscopeから新規導入や登録置換を始めません。</summary>
    internal bool IsComplete { get; set; } = true;
}
