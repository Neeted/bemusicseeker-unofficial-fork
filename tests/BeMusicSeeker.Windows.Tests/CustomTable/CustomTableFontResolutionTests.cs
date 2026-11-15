using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

/// <summary>一覧の文字に使うフォントの解決（候補の取出し、第一候補の選択、字形のない文字の代替、スコア用のフォントの読込み、固定した行の寸法）を確かめます。</summary>
/// <remarks>ArialとMeiryo UIのインストールを前提にします。</remarks>
[TestClass]
public sealed class CustomTableFontResolutionTests
{
    private static readonly Uri FontsFolder = new("pack://application:,,,/BeMusicSeeker;component/Assets/Fonts/");

    /// <summary>候補の名前が、選択されたフォント・既定のフォントの優先順で取り出され、名前の中のコンマと物理フォントの指定も扱えることを確認します。候補の並びの規則を描画側で複製して食い違う誤りを検出します。</summary>
    [TestMethod]
    public void GetCandidateFamilyNames_ReturnsCandidatesInPriorityOrder()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CollectionAssert.AreEqual(new[] { "Arial", "Meiryo UI" }, AppFontService.GetCandidateFamilyNames(AppFontService.CreateFontFamily("Arial")).ToArray());
            CollectionAssert.AreEqual(new[] { "Meiryo UI" }, AppFontService.GetCandidateFamilyNames(AppFontService.DefaultFontFamily).ToArray());
            CollectionAssert.AreEqual(new[] { "Meiryo UI" }, AppFontService.GetCandidateFamilyNames(AppFontService.CreateFontFamily(string.Empty)).ToArray());
            CollectionAssert.AreEqual(new[] { "Arial" }, AppFontService.GetCandidateFamilyNames(new FontFamily("Arial")).ToArray());
            CollectionAssert.AreEqual(new[] { "Arial", "Times New Roman" }, AppFontService.GetCandidateFamilyNames(new FontFamily("Arial, Times New Roman")).ToArray());
            CollectionAssert.AreEqual(new[] { "A,B", "Meiryo UI" }, AppFontService.GetCandidateFamilyNames(AppFontService.CreateFontFamily("A,B")).ToArray());
            Assert.AreEqual(0, AppFontService.GetCandidateFamilyNames(null!).Count);
        });
    }

    /// <summary>第一候補がインストール済みならそれを書式のファミリーにし、未インストールや空の名前なら既定のフォントにすることを確認します。存在しない名前で代替のフォントへ落ちる誤りを検出します。</summary>
    [TestMethod]
    public void ResolveTextFont_UsesTheFirstCandidateOnlyWhenInstalled()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Assert.AreEqual("Arial", CustomTableRenderResources.ResolveTextFont(AppFontService.CreateFontFamily("Arial")).FamilyName);
            Assert.AreEqual("Arial", CustomTableRenderResources.ResolveTextFont(new FontFamily("Arial")).FamilyName);
            Assert.AreEqual("Meiryo UI", CustomTableRenderResources.ResolveTextFont(AppFontService.DefaultFontFamily).FamilyName);
            Assert.AreEqual("Meiryo UI", CustomTableRenderResources.ResolveTextFont(AppFontService.CreateFontFamily("BeMusicSeeker No Such Font")).FamilyName);
            Assert.AreEqual("Meiryo UI", CustomTableRenderResources.ResolveTextFont(AppFontService.CreateFontFamily(string.Empty)).FamilyName);
            Assert.AreEqual("Meiryo UI", CustomTableRenderResources.ResolveTextFont(null!).FamilyName);
            Assert.IsNotNull(CustomTableRenderResources.ResolveTextFont(AppFontService.CreateFontFamily("Arial")).Fallback, "字形のない文字を補う規則を持ちます。");
        });
    }

    /// <summary>
    /// 選択したフォントにない文字は既定のフォントで描かれることを、Arialにない日本語の文字の字形が、既定のMeiryo UIで描いた字形と一致することで確認します。
    /// Arialにある文字は、Arialで描かれて既定のフォントと異なります。
    /// </summary>
    /// <remarks>字形のない文字を代替のフォントや空白で描く誤りと、第一候補のフォントを無視する誤りを検出します。</remarks>
    [TestMethod]
    public void Fallback_DrawsCharactersMissingFromTheSelectedFontWithTheDefaultFont()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Color background = ((SolidColorBrush)CustomTablePalette.Current.RowBackground).Color;
            (SurfacePixels Pixels, Int32Rect Ink) Render(FontFamily font, string text)
            {
                var column = new CustomTableColumn("A", string.Empty, new CustomTableColumnSettings.ColumnLayout { Width = 100, Visibility = Visibility.Visible }, 0, null, TextAlignment.Left, _ => text);
                var table = new CustomTableView { Width = 100, Height = 60, HeaderHeight = 20, RowHeight = 25, TextFontFamily = font, Columns = [column], ItemsSource = new object[] { new() } };
                CustomTableRenderProbe.SetDpi(table, 1d);
                CustomTableRenderProbe.Arrange(table, 100d, 60d);
                try
                {
                    SurfacePixels pixels = CustomTableRenderProbe.Read(table);
                    Int32Rect? ink = pixels.FindInkBounds(new Int32Rect(0, 20, 99, 24), background);
                    Assert.IsNotNull(ink, text);
                    return (pixels, ink.Value);
                }
                finally { table.ItemsSource = null; }
            }
            FontFamily arial = AppFontService.CreateFontFamily("Arial");
            (SurfacePixels arialKana, Int32Rect arialKanaInk) = Render(arial, "あ漢");
            (SurfacePixels defaultKana, Int32Rect defaultKanaInk) = Render(AppFontService.DefaultFontFamily, "あ漢");
            Assert.AreEqual(defaultKanaInk.Width, arialKanaInk.Width);
            Assert.AreEqual(defaultKanaInk.Height, arialKanaInk.Height);
            Assert.IsTrue(defaultKana.RegionEquals(defaultKanaInk, arialKana, arialKanaInk.X, arialKanaInk.Y), "Arialにない文字は既定のフォントの字形で描かれます。");

            (SurfacePixels arialLatin, Int32Rect arialLatinInk) = Render(arial, "Rag");
            (SurfacePixels defaultLatin, Int32Rect defaultLatinInk) = Render(AppFontService.DefaultFontFamily, "Rag");
            Assert.IsFalse(arialLatinInk.Width == defaultLatinInk.Width && defaultLatin.RegionEquals(defaultLatinInk, arialLatin, arialLatinInk.X, arialLatinInk.Y), "Arialにある文字はArialの字形で描かれます。");
        });
    }

    /// <summary>
    /// 行の高さが文字サイズの1.27倍に固定され、物理フォントを直接渡した場合も渡されたフォントの値（文字サイズに対する倍率）に文字サイズを掛けた値になることを、100%と125%で確認します。
    /// フォントが申告する行の高さをそのまま使う誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    public void LineHeight_IsTheFontLineSpacingTimesTheFontSize(double pixelsPerDip)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const double fontSize = 11d;
            CultureInfo culture = CultureInfo.CurrentCulture;
            foreach (FontFamily font in new[] { AppFontService.DefaultFontFamily, AppFontService.CreateFontFamily("Arial"), AppFontService.CreateFontFamily("BeMusicSeeker No Such Font"), new FontFamily("Arial") })
            {
                using var cache = new CustomTableTextLayoutCache();
                cache.Configure(new CustomTableTextEnvironment(font, null!, fontSize, pixelsPerDip, culture, culture));
                CustomTableTextLayout layout = cache.GetOrCreate("Hxg", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _);
                Assert.AreEqual(font.LineSpacing * fontSize * pixelsPerDip, layout.LineHeight, 0.01d, font.Source);
                Assert.AreEqual(layout.LineHeight, layout.Layout.Metrics.Height, 0.01d, "固定した行の高さで整形されます。");
                if (font.FamilyMaps.Count > 0)
                {
                    Assert.AreEqual(AppFontService.LineSpacing * fontSize * pixelsPerDip, layout.LineHeight, 0.01d, "表示フォントの行の高さは文字サイズの1.27倍です。");
                }
            }
        });
    }

    /// <summary>同梱のスコア用のフォントを、字形の書体のバイト列からメモリ上で読み込み、同じフォントでは一度だけ読み込み、読み込めない書体とnullはnullを返して例外にしないことを確認します。</summary>
    /// <remarks>毎回読み込んで資源が増える誤り、読み込めない書体で例外にする誤り、代わりのフォントを同梱フォントとして使う誤りを検出します。</remarks>
    [TestMethod]
    public void TryGetScoreFont_LoadsTheBundledFontOnceAndReturnsNullForUnloadableFonts()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var bundled = new FontFamily(FontsFolder, "./#Sovjet Box");
            CustomTableScoreFont first = CustomTableRenderResources.TryGetScoreFont(bundled);
            Assert.IsNotNull(first);
            StringAssert.Contains(first.FamilyName.ToLowerInvariant(), "sovjet");
            Assert.AreSame(first, CustomTableRenderResources.TryGetScoreFont(bundled), "同じフォントでは一度だけ読み込みます。");
            Assert.IsNull(CustomTableRenderResources.TryGetScoreFont(null!));
            Assert.IsNull(CustomTableRenderResources.TryGetScoreFont(new FontFamily(FontsFolder, "./#BeMusicSeeker No Such Score Font")));
            Assert.IsNull(CustomTableRenderResources.TryGetScoreFont(new FontFamily(new Uri("pack://application:,,,/NoSuchAssembly;component/Assets/"), "./#Missing")));
            TestUiDispatcherHost.AssertNoUnhandledExceptions("score-font-load");
        });
    }
}
