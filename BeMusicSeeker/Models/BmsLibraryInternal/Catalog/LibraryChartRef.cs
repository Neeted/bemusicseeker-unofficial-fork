namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>所持項目の短命な識別と、要求の捕捉時点の配置・ハッシュだけを保持します。</summary>
internal sealed class LibraryChartRef
{
    private LibraryChartRef(ChartFileKind kind, OwnedChartToken token, string path, string md5, string sha256)
    {
        Kind = kind;
        Token = token;
        Path = string.IsNullOrWhiteSpace(path) ? null : path;
        Md5 = NormalizeHash(md5);
        Sha256 = NormalizeHash(sha256);
    }

    /// <summary>捕捉元の所持項目識別です。識別から現在値や保存行へ戻りません。</summary>
    internal OwnedChartToken Token { get; }

    /// <summary>捕捉した形式です。共通基本値と同じ形式定義を使います。</summary>
    public ChartFileKind Kind { get; }

    /// <summary>捕捉時点のDB完全一致パスです。</summary>
    public string Path { get; }

    /// <summary>捕捉パスから得る物理的な親フォルダです。</summary>
    public string Directory => string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetDirectoryName(Path);

    /// <summary>捕捉時点のMD5です。所持項目の識別には使いません。</summary>
    public string Md5 { get; }

    /// <summary>捕捉時点のSHA-256です。所持項目の識別には使いません。</summary>
    public string Sha256 { get; }

    /// <summary>読取り値が保持する所持識別と捕捉事実を引き継ぎます。</summary>
    internal static LibraryChartRef FromChartFile(ChartFile chart, string pathOverride = null)
        => chart == null ? null : new LibraryChartRef(
            chart.Kind,
            chart.Token, string.IsNullOrWhiteSpace(pathOverride) ? chart.Path : pathOverride, chart.Md5, chart.Sha256);

    /// <summary>所持tokenを持たないパスだけの参照を作成します。</summary>
    internal static LibraryChartRef FromPath(ChartFileKind kind, string path, string md5, string sha256)
        => string.IsNullOrWhiteSpace(path) ? null : new LibraryChartRef(kind, null, path, md5, sha256);

    /// <summary>不変参照を再利用します。現在値や保存行への解決は行いません。</summary>
    internal static LibraryChartRef FromImmutableSnapshot(LibraryChartRef source) => source;

    /// <summary>捕捉した基本識別を共通の要求値へ変換します。現在値の解決はカタログが担当します。</summary>
    internal ChartFile ToChartFileIdentity()
        => new ChartFile(Kind,
            Path, Md5, Sha256, string.Empty, string.Empty, string.Empty, string.Empty,
            null, string.Empty, string.Empty, null, null, null)
        {
            Token = Token
        };

    private static string NormalizeHash(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
