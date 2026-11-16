using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Tests;

/// <summary>実効LR2ルートの規則を、ファイルを使わず入力の組だけで確認します。</summary>
[TestClass]
public sealed class Lr2EffectiveRootTests
{
    private const string StandardSongDb = @"D:\LR2\LR2files\Database\song.db";
    private const string StandardConfigXml = @"D:\LR2\LR2files\Config\config.xml";

    /// <summary>
    /// 保証：保存したLR2ディレクトリが空でなければ、モードや song.db・設定XMLの場所に依らずその値を返す。
    /// 推測が保存値より優先される誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(true, StandardSongDb, StandardConfigXml, DisplayName = "LR2連携で別のフォルダが標準配置")]
    [DataRow(true, @"E:\Data\song.db", @"E:\Data\config.xml", DisplayName = "LR2連携で個別指定")]
    [DataRow(false, StandardSongDb, StandardConfigXml, DisplayName = "単独動作")]
    [DataRow(true, "", "", DisplayName = "パス未設定")]
    public void Resolve_ReturnsSavedRootWhenConfigured(bool operationModeLR2DB, string songDbPath, string configXmlPath)
    {
        Assert.AreEqual(@"C:\Saved\LR2", Lr2EffectiveRoot.Resolve(operationModeLR2DB, @"C:\Saved\LR2", songDbPath, configXmlPath));
    }

    /// <summary>
    /// 保証：保存値が空のLR2連携で、song.db と設定XMLが同じフォルダの標準配置にあれば、そのフォルダを返す。
    /// config.xmh、大文字小文字・<c>.</c> 要素・区切りの違いも同じフォルダとして扱い、ファイルの実在は条件にしない。
    /// </summary>
    /// <remarks>入力は実在しないドライブ上のパスで、ディスクを読む実装や実行ファイルの存在を条件にする実装では成立しない。</remarks>
    [TestMethod]
    [DataRow(StandardSongDb, StandardConfigXml, DisplayName = "config.xml")]
    [DataRow(StandardSongDb, @"D:\LR2\LR2files\Config\config.xmh", DisplayName = "config.xmh")]
    [DataRow(@"d:\lr2\lr2FILES\database\SONG.DB", @"D:\LR2\LR2files\CONFIG\Config.XML", DisplayName = "大文字小文字の違い")]
    [DataRow(@"D:\LR2\.\LR2files\Database\song.db", @"D:\Other\..\LR2\LR2files\Config\config.xml", DisplayName = "相対要素を含む完全修飾")]
    [DataRow(@"D:/LR2/LR2files/Database/song.db", StandardConfigXml, DisplayName = "区切り文字の違い")]
    public void Resolve_InfersRootWhenBothPathsShareStandardLayout(string songDbPath, string configXmlPath)
    {
        string resolved = Lr2EffectiveRoot.Resolve(true, string.Empty, songDbPath, configXmlPath);

        Assert.AreEqual(@"D:\LR2", Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved)), ignoreCase: true);
    }

    /// <summary>
    /// 保証：起動時の <c>song</c>・<c>folder</c> 表の補正は、LR2連携モードで song.db が実効LR2ルートの標準配置にあるときだけ、実効LR2ルートを基準に行う。
    /// 別の場所の song.db、別のLR2フォルダの song.db、単独動作、実効ルートなしでは補正の基準を返さない。
    /// 保存したルートがあるだけで別の場所の song.db を補正する誤りと、song.db の場所だけから基準を決める誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(true, @"D:\LR2", StandardSongDb, @"D:\LR2", DisplayName = "実効ルートの標準配置")]
    [DataRow(true, @"d:\lr2\", @"D:\LR2\.\LR2files\DATABASE\Song.db", @"d:\lr2\", DisplayName = "表記が違う同じ場所")]
    [DataRow(true, @"D:\LR2", @"E:\Data\song.db", "", DisplayName = "別の場所の song.db")]
    [DataRow(true, @"C:\Other", StandardSongDb, "", DisplayName = "別のLR2フォルダの song.db")]
    [DataRow(true, @"D:\LR2", @"LR2files\Database\song.db", "", DisplayName = "相対パスの song.db")]
    [DataRow(true, "", StandardSongDb, "", DisplayName = "実効ルートなし")]
    [DataRow(false, @"D:\LR2", StandardSongDb, "", DisplayName = "単独動作")]
    public void ResolveSongTableCorrectionRoot_RequiresSongDbInEffectiveRootStandardLayout(
        bool operationModeLR2DB, string effectiveRootPath, string songDbPath, string expected)
    {
        Assert.AreEqual(expected, Lr2EffectiveRoot.ResolveSongTableCorrectionRoot(operationModeLR2DB, effectiveRootPath, songDbPath));
    }

    /// <summary>
    /// 保証：保存値が空で、両方が同じフォルダの標準配置という条件を満たさない組は推測せず空を返す。
    /// 片方のパスだけから推測する旧い個別推測の残存、モードの未確認、現在ディレクトリでの補完を検出する。
    /// </summary>
    [TestMethod]
    [DataRow(true, StandardSongDb, @"E:\Config\config.xml", DisplayName = "song.db だけ標準配置")]
    [DataRow(true, @"E:\Data\song.db", StandardConfigXml, DisplayName = "設定XMLだけ標準配置")]
    [DataRow(true, StandardSongDb, @"E:\LR2\LR2files\Config\config.xml", DisplayName = "別々のフォルダの標準配置")]
    [DataRow(true, @"E:\Data\song.db", @"E:\Data\config.xml", DisplayName = "どちらも非標準")]
    [DataRow(true, StandardSongDb, @"D:\LR2\LR2files\Config\other.xml", DisplayName = "設定XMLのファイル名が対象外")]
    [DataRow(true, @"D:\LR2\LR2files\Config\song.db", StandardConfigXml, DisplayName = "song.db が Database 配下でない")]
    [DataRow(true, "", StandardConfigXml, DisplayName = "song.db が空")]
    [DataRow(true, StandardSongDb, "", DisplayName = "設定XMLが空")]
    [DataRow(true, null, null, DisplayName = "どちらも未設定")]
    [DataRow(true, @"LR2files\Database\song.db", @"LR2files\Config\config.xml", DisplayName = "相対パス")]
    [DataRow(true, @"D:LR2files\Database\song.db", @"D:LR2files\Config\config.xml", DisplayName = "ドライブ相対パス")]
    [DataRow(true, @"\LR2files\Database\song.db", @"\LR2files\Config\config.xml", DisplayName = "ルート相対パス")]
    [DataRow(false, StandardSongDb, StandardConfigXml, DisplayName = "単独動作")]
    public void Resolve_ReturnsEmptyWhenRootCannotBeInferred(bool operationModeLR2DB, string? songDbPath, string? configXmlPath)
    {
        foreach (string? savedRootPath in new[] { string.Empty, "  ", null })
        {
            Assert.AreEqual(string.Empty, Lr2EffectiveRoot.Resolve(operationModeLR2DB, savedRootPath!, songDbPath!, configXmlPath!));
        }
    }
}
