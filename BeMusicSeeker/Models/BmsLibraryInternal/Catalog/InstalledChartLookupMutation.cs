using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// installed lookup へ反映する durable mutation facts です。
/// </summary>
internal sealed class InstalledChartLookupMutation
{
    /// <summary>除去する chart facts。</summary>
    internal List<InstalledChartLookupMutationEntry> Removed { get; } = [];

    /// <summary>追加する chart facts。</summary>
    internal List<InstalledChartLookupMutationEntry> Added { get; } = [];

    /// <summary>path 移動する chart facts。</summary>
    internal List<InstalledChartLookupPathMutationEntry> Moved { get; } = [];

    /// <summary>旧 facts 不足などにより全再構築が必要か。</summary>
    internal bool RequiresFullInvalidate { get; set; }

    /// <summary>反映すべき facts が存在するか。</summary>
    internal bool HasChanges => RequiresFullInvalidate || Removed.Count > 0 || Added.Count > 0 || Moved.Count > 0;
}

/// <summary>installed lookup へ渡す単一 chart の旧新 identity facts です。</summary>
internal readonly struct InstalledChartLookupMutationEntry(
    string path,
    string md5,
    string sha256,
    ChartFileKind kind = ChartFileKind.Bms)
{
    /// <summary>chart path。</summary>
    internal string Path { get; } = path;

    /// <summary>primary MD5。</summary>
    internal string Md5 { get; } = md5;

    /// <summary>SHA-256。</summary>
    internal string Sha256 { get; } = sha256;

    /// <summary>chart kind。</summary>
    internal ChartFileKind Kind { get; } = kind;
}

/// <summary>installed lookup へ渡す単一 chart の path 移動 facts です。</summary>
internal readonly struct InstalledChartLookupPathMutationEntry(
    string oldPath,
    string newPath,
    string md5,
    string sha256,
    ChartFileKind kind = ChartFileKind.Bms)
{
    /// <summary>移動前 path。</summary>
    internal string OldPath { get; } = oldPath;

    /// <summary>移動後 path。</summary>
    internal string NewPath { get; } = newPath;

    /// <summary>primary MD5。</summary>
    internal string Md5 { get; } = md5;

    /// <summary>SHA-256。</summary>
    internal string Sha256 { get; } = sha256;

    /// <summary>chart kind。</summary>
    internal ChartFileKind Kind { get; } = kind;
}
