using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// 分裂配置の探索を実ライブラリで試す各実行区分が、同じ入力（所持する譜面と、音源のキーを持つリソース索引）を作るための補助です。
/// </summary>
internal static class SplitPlacementTestSupport
{
    /// <summary>リソース索引へ設定するフォルダと、そのフォルダが自身で所有する音声キーです。</summary>
    /// <param name="Path">譜面フォルダのパス。</param>
    /// <param name="Self">自身で所有する音声キー。</param>
    /// <param name="All">子の譜面フォルダの分を含む全体の音声キー。省略時は <paramref name="Self"/> と同じです。</param>
    internal sealed record IndexFolder(string Path, uint[] Self, uint[]? All = null);

    internal static string Md5(int n) => n.ToString("x32");

    internal static uint[] Keys(uint start, int count) => [.. Enumerable.Range(0, count).Select(i => start + (uint)i)];

    /// <summary>主ハッシュを数値で指定した所持譜面を作ります。BMSON を指定すると BMSON の譜面になります。</summary>
    internal static ChartFile Chart(string path, int md5, string? title = null, ChartFileKind kind = ChartFileKind.Bms)
    {
        ChartFile chart = kind == ChartFileKind.Bms
            ? CreateFile(Md5(md5), path)
            : ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = path, Md5 = Md5(md5), Token = new OwnedChartToken() };
        return title == null ? chart : chart with { Title = title, RawTitle = title };
    }

    /// <summary>指定した自身の所有分の音声キーを持つ索引を、実ライブラリの管理主体へ設定します。世代が進みます。</summary>
    internal static LibraryResourceIndexSnapshot SetIndex(BMSLibrary library, params IndexFolder[] folders)
    {
        var scan = new ChartScanResult();
        foreach (IndexFolder folder in folders)
        {
            scan.ChartDirectories.Add(folder.Path);
            scan.SelfOwnedAudioRelativePathHashesByChartDirectory[folder.Path] = folder.Self;
            scan.AudioRelativePathHashesByChartDirectory[folder.Path] = folder.All ?? folder.Self;
        }
        return LibraryResourceIndexTestSupport.GetOwner(library).Replace(LibraryResourceIndex.CreateFromScanResult(scan));
    }

    /// <summary>A と B が音源を共有して一つのグループになり、C は無関係な、三フォルダの所持譜面です。</summary>
    internal static ChartFile[] TwoRelatedFolders() =>
        [Chart(@"C:\BMS\A\a.bms", 1, "A"), Chart(@"C:\BMS\B\b.bms", 2, "B"), Chart(@"C:\BMS\C\c.bms", 3, "C")];

    /// <summary><see cref="TwoRelatedFolders"/> に対応する索引を設定します。</summary>
    internal static void SetTwoRelatedIndex(BMSLibrary library)
        => SetIndex(library, new(@"C:\BMS\A", Keys(1, 10)), new(@"C:\BMS\B", Keys(1, 10)), new(@"C:\BMS\C", Keys(500, 10)));
}
