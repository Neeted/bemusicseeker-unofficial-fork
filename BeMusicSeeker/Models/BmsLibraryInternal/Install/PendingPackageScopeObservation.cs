using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一要求内で共有する保留sourceの属性取得事実です。種別不能をfileやdirectoryへ読み替えず、正常不存在と実失敗を分離します。</summary>
internal sealed class PendingPackageScopeObservation
{
    private PendingPackageScopeObservation(string path, bool? directory, bool missing, Exception failure)
    { Path = path; IsDirectory = directory; IsMissing = missing; Failure = failure; }

    /// <summary>属性を取得したcanonical物理pathです。</summary>
    internal string Path { get; }
    /// <summary>取得成功時だけfile/directory種別を持ち、未確定はnullです。</summary>
    internal bool? IsDirectory { get; }
    /// <summary>属性取得で正常NotFoundを確認した場合だけ真です。</summary>
    internal bool IsMissing { get; }
    /// <summary>短命な要求内で既存不完全scopeへ渡す、非NotFoundの実原因です。成功・正常不存在ではnullです。</summary>
    internal Exception Failure { get; }
    /// <summary>既知file以外は保全関係としてだけ子範囲を覆えます。未知種別にdirectoryの成功資格や物理操作権限を与えません。</summary>
    internal bool ProtectsDescendants => IsDirectory != false;

    /// <summary>canonical pathの属性を一回取得します。既存filesystem失敗だけを値として返し、取消などの終端は伝播します。</summary>
    internal static PendingPackageScopeObservation Capture(string path)
    {
        try { return new(path, (LongPathFileSystem.GetAttributes(path) & FileAttributes.Directory) != 0, false, null); }
        catch (Exception failure) when (LongPathFileSystem.IsNotFoundFailure(failure)) { return new(path, null, true, null); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(path, null, false, failure); }
    }

    /// <summary>既存入力分類が確定した入力種別だけを群へ渡します。source観測がある同pathを上書きしません。</summary>
    internal static PendingPackageScopeObservation FromInput(string path, bool directory) => new(path, directory, false, null);

    /// <summary>source canonical単位で一回だけ属性を読みます。譜面getterや兄弟を探索せず、別名sourceは同じ取得事実を共用します。</summary>
    internal static Dictionary<string, PendingPackageScopeObservation> CaptureSources(IEnumerable<ChartPackage> sources)
    {
        var scopes = new Dictionary<string, PendingPackageScopeObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackage source in sources ?? [])
        {
            if (source != null && LongPathFileSystem.TryNormalizePhysicalScopePath(source.path, out string path) && !scopes.ContainsKey(path))
            { scopes.Add(path, Capture(path)); }
        }
        return scopes;
    }

    /// <summary>取得済みの非空package結果から物理単位種別を読むだけで、source属性を再取得しません。単一fileはその同path譜面一個、directoryは配下譜面を持ちます。</summary>
    internal static bool IsDirectoryPackage(ChartPackage package)
    {
        IReadOnlyList<PackageChartEntry> entries = package.ChartEntries;
        return entries.Count != 1 || !string.Equals(package.path, entries[0].Chart.Path, StringComparison.OrdinalIgnoreCase);
    }
}
