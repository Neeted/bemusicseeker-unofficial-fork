using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// directory探索または同じ親bucketの直接file取得で見つかったパッケージ群と完全性を返します。
/// </summary>
internal sealed class ChartPackageDiscoveryResult
{
    /// <summary>
    /// 探索で見つかった chart package 一覧です。
    /// </summary>
    public List<ChartPackage> Packages { get; } = [];

    /// <summary>同じ根の再読取りを避けるため、失敗時も読めたentryを一つの収集先で保持します。</summary>
    internal List<PackageChartEntry> ObservedEntries { get; set; } = [];

    /// <summary>directoryとして一度取得した根です。直接file入力から昇格した場合も同じ探索を旧群の整理へ渡します。</summary>
    internal string SourceDirectory { get; init; }

    /// <summary>一式として読取りを完了できなかったscopeです。独立した子の成功と区別して旧群を保全します。</summary>
    internal List<string> IncompleteScopes { get; } = [];

    /// <summary>独立した成功を保持したまま終端へ渡す、実際の列挙・読取り失敗です。</summary>
    internal List<Exception> Failures { get; } = [];

    /// <summary>読取り失敗の有無と独立して、列挙済みの譜面があることを保持します。</summary>
    internal bool ContainsCharts { get; set; }

    /// <summary>列挙と全譜面読取りが完了したかを返します。不完全なscopeから新規導入や登録置換を始めません。</summary>
    internal bool IsComplete { get; set; } = true;
}
