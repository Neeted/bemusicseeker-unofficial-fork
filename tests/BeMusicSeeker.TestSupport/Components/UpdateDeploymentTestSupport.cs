using System.Xml.Linq;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>更新配布境界のXML検査とリポジトリ位置の取得を共用します。</summary>
internal static class UpdateDeploymentTestSupport
{
    /// <summary>公開profileの指定プロパティが期待値と一致することを確認します。</summary>
    internal static void AssertProfileValue(XDocument profile, string propertyName, string expectedValue)
    {
        XElement property = profile
            .Descendants()
            .SingleOrDefault(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.Ordinal))
            ?? throw new AssertFailedException($"Publish profile property is missing: {propertyName}");
        Assert.AreEqual(expectedValue, property.Value, propertyName);
    }

    /// <summary>公開profileに退役した指定が残っていないことを確認します。</summary>
    internal static void AssertProfileValueAbsent(XDocument profile, string propertyName)
    {
        Assert.IsFalse(
            profile.Descendants().Any(element => string.Equals(
                element.Name.LocalName,
                propertyName,
                StringComparison.Ordinal)),
            $"Publish profile property must remain absent: {propertyName}");
    }

    /// <summary>出力位置からsolutionが存在するリポジトリルートを取得します。</summary>
    internal static string FindRepositoryRoot()
    {
        string? directoryPath = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (File.Exists(Path.Combine(directoryPath, "BeMusicSeeker.sln")))
            {
                return directoryPath!;
            }

            directoryPath = Directory.GetParent(directoryPath)?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
