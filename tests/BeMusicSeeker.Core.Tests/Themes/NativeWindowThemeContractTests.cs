using BeMusicSeeker.Models;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class NativeWindowThemeContractTests
{
    /// <summary>保存値は登録された識別子へ正規化し、題名部の明暗は配色系列ごとに判定します。</summary>
    [TestMethod]
    [DataRow("Light", "Light", false)]
    [DataRow("Dark", "Dark", true)]
    [DataRow("Sepia", "Sepia", false)]
    [DataRow("Frost", "Frost", false)]
    [DataRow("NeonViolet", "NeonViolet", true)]
    [DataRow("GreenTerminal", "GreenTerminal", true)]
    [DataRow("AmberTerminal", "AmberTerminal", true)]
    [DataRow("OledBlack", "OledBlack", true)]
    [DataRow("DeepSea", "DeepSea", true)]
    [DataRow("DosBlue", "DosBlue", true)]
    [DataRow("greenterminal", "GreenTerminal", true)]
    [DataRow("unknown-theme", "Light", false)]
    public void ThemeSettings_NormalizeSavedIdentifiersAndClassifyNativeCaption(string value, string expectedTheme, bool expectedDark)
    {
        Assert.AreEqual(expectedTheme, AppThemeService.NormalizeTheme(value));
        Assert.AreEqual(expectedDark, AppThemeService.IsDarkTheme(value));
    }
}
