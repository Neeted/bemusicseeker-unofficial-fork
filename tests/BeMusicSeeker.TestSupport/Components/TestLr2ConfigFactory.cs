using System.Text;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>呼出し側が所有する領域へ実LR2設定を準備し、設定ファイルの読込み境界を共有します。</summary>
internal static class TestLr2ConfigFactory
{
    /// <summary>所有領域に既存形式のconfig.xmlを作り、空でない各rootを末尾区切り付きで登録します。</summary>
    internal static LR2Config CreateLr2Config(string lr2RootPath, params string[] bmsRoots)
    {
        string configDirectory = Path.Combine(lr2RootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectory);
        string pathElements = string.Join(
            string.Empty,
            (bmsRoots ?? [])
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => "<path>" + EscapeXml(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar) + "</path>"));
        string configPath = Path.Combine(configDirectory, "config.xml");
        File.WriteAllText(
            configPath,
            "<config><system><customfolder>0</customfolder><titleflash>24</titleflash></system><jukebox>" + pathElements + "</jukebox></config>",
            Encoding.UTF8);
        return new LR2Config(configPath, lr2RootPath);
    }

    /// <summary>
    /// 既存の設定XMLを読み込みます。LR2ルートは設定XMLの場所から直接は決めず、同じLR2環境の標準配置の song.db を持つ
    /// フィクスチャとして、本番の実効LR2ルートの規則で導出します。標準配置でない設定XMLはルートなしになります。
    /// </summary>
    internal static LR2Config Load(string configPath)
    {
        string lr2FilesDirectory = Path.GetDirectoryName(Path.GetDirectoryName(configPath)) ?? string.Empty;
        string standardSongDbPath = Path.Combine(lr2FilesDirectory, "Database", "song.db");
        return new LR2Config(configPath, Lr2EffectiveRoot.Resolve(true, string.Empty, standardSongDbPath, configPath));
    }

    /// <summary>設定に埋め込む値のXML特殊文字をエスケープします。</summary>
    internal static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }
}
