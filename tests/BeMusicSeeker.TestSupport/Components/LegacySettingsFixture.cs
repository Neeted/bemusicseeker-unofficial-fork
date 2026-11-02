using System.Globalization;
using System.Xml.Linq;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>現行providerを使わず、旧版の型付きXML設定を受入入力として生成します。</summary>
public static class LegacySettingsFixture
{
    /// <summary>四成分の版だけをXml、残りのscalarをStringとして保存し、XML特殊文字を保持します。</summary>
    public static void Write(string path, IReadOnlyDictionary<string, string> settings)
    {
        var section = new XElement("BeMusicSeeker.Properties.Settings");
        foreach ((string name, string text) in settings)
        {
            var value = new XElement("value");
            if (name == "AssemblyVersion")
            {
                var version = Version.Parse(text);
                if (version.Build < 0 || version.Revision < 0)
                {
                    throw new ArgumentException("AssemblyVersionには非負の四成分が必要です。", nameof(settings));
                }
                value.Add(new XElement("SerializableVersion",
                    new XElement("Major", version.Major.ToString(CultureInfo.InvariantCulture)),
                    new XElement("Minor", version.Minor.ToString(CultureInfo.InvariantCulture)),
                    new XElement("Build", version.Build.ToString(CultureInfo.InvariantCulture)),
                    new XElement("Revision", version.Revision.ToString(CultureInfo.InvariantCulture))));
            }
            else { value.Value = text; }
            section.Add(new XElement("setting", new XAttribute("name", name),
                new XAttribute("serializeAs", name == "AssemblyVersion" ? "Xml" : "String"), value));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("設定の親ディレクトリがありません。", nameof(path)));
        new XDocument(new XElement("configuration", new XElement("userSettings", section))).Save(path);
    }
}
