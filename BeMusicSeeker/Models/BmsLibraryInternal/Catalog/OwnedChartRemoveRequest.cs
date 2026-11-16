namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>所持項目の厳密削除と、旧DB配置の明示清掃を区別します。</summary>
internal enum OwnedChartRemoveMode
{
    Item,
    PathCleanup
}

/// <summary>削除時点の識別と旧配置だけを捕捉します。現在値や保存行への逆参照を保持しません。</summary>
internal sealed class OwnedChartRemoveRequest
{
    private OwnedChartRemoveRequest(OwnedChartRemoveMode mode, ChartFileKind kind,
        OwnedChartToken token, string path, string md5, string sha256, bool hasCapturedFacts)
    {
        Mode = mode;
        Kind = kind;
        Token = token;
        Path = path;
        CapturedMd5 = md5;
        CapturedSha256 = sha256;
        HasCapturedFacts = hasCapturedFacts;
    }

    internal OwnedChartRemoveMode Mode { get; }
    internal ChartFileKind Kind { get; }
    internal OwnedChartToken Token { get; }
    internal string Path { get; }
    internal string CapturedMd5 { get; }
    internal string CapturedSha256 { get; }
    internal bool HasCapturedFacts { get; }

    /// <summary>現在の所持項目だけを削除する要求を作ります。未所持の値では要求を作りません。</summary>
    internal static OwnedChartRemoveRequest FromChart(ChartFile chart) => chart?.Token == null
        ? null : new(OwnedChartRemoveMode.Item, chart.Kind, chart.Token,
            chart.Path, chart.Md5, chart.Sha256, true);

    /// <summary>確認済みの旧DB配置を、exact pathのまま清掃する要求を作ります。</summary>
    internal static OwnedChartRemoveRequest FromPathCleanup(ChartFileKind kind, string path) =>
        string.IsNullOrWhiteSpace(path) ? null : new(OwnedChartRemoveMode.PathCleanup,
            kind, null, path, null, null, false);

    /// <summary>旧配置の索引除去に必要なハッシュも捕捉します。所持項目の削除には流用しません。</summary>
    internal static OwnedChartRemoveRequest FromResolvedPathCleanup(ChartFile chart) =>
        chart == null || string.IsNullOrWhiteSpace(chart.Path) ? null :
            new(OwnedChartRemoveMode.PathCleanup, chart.Kind, null,
                chart.Path, chart.Md5, chart.Sha256, true);

    /// <summary>削除要求が捕捉した旧識別だけを、不変の変更事実として返します。</summary>
    internal ChartFile CreateChartSnapshot() => string.IsNullOrWhiteSpace(Path) ? null :
        ChartFileProjection.FromIdentitySnapshot(Kind, Path, CapturedMd5, CapturedSha256)
            with
        { Token = Token };
}
