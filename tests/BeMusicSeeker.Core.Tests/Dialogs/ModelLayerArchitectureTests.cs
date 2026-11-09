using System.Text.RegularExpressions;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

/// <summary>
/// モデルの層が画面の層へ依存しない向きを保ちます。
/// ソース文字列を走査する理由: Models・Properties・ViewModels・Views は同じアセンブリにあり、コンパイル後の型・参照からは
/// 名前空間の層を区別できる境界がありません。メンバーの本体が参照する型まで型情報だけで網羅する手段もないため、例外として
/// ソースの参照記述を検査します。
/// 固定範囲: <c>BeMusicSeeker/Models</c> 配下の .cs（bin・obj とコメント行を除く）が <c>BeMusicSeeker.Views</c> と
/// <c>BeMusicSeeker.ViewModels</c> の名前空間を、<c>BeMusicSeeker/Properties</c> 配下の .cs が <c>BeMusicSeeker.Views</c> と
/// <c>BeMusicSeeker.ViewModels</c> を、それぞれ using・別名・完全修飾名・名前空間宣言、および名前空間内の相対参照（<c>Views.X</c>、<c>ViewModels.X</c>、
/// <c>using Views;</c>、<c>using ViewModels;</c>）として含まないことだけです。
/// 既知の見逃し: Models・Properties の外の型（ルートの BeMusicSeeker、Runtime、Diagnostics、Ribbit）のメンバーが
/// 画面の層の型を公開し、それを <c>var</c> や連鎖で使う依存、行をまたいで分けた完全修飾名、他のディレクトリで
/// Models・Properties の名前空間を宣言するファイルは検出しません。文字列リテラル、ブロックコメント内の記述、
/// Views・ViewModels という名前の別名、行末のコメントに書かれた参照は安全側として検出します。
/// 管理主体: 層の分担を定めるアーキテクチャ仕様の管理主体。
/// 退役条件: Models・Properties と Views・ViewModels が別アセンブリに分かれ、層の向きをプロジェクト参照とコンパイルで強制できたとき。
/// </summary>
[TestClass]
public sealed class ModelLayerArchitectureTests
{
    // 名前空間全体 BeMusicSeeker.Views(.X) を、using・別名・global::・namespace 宣言を問わず拾う。ViewModels には一致しない。
    private static readonly Regex QualifiedViews = new(@"\bBeMusicSeeker\s*\.\s*Views\b", RegexOptions.CultureInvariant);

    // 名前空間の内側で書ける相対参照 Views.X と using Views を拾う。chartViews.Count のような識別子の一部は拾わない。
    private static readonly Regex RelativeViews = new(@"(?<![\w.])Views\.[A-Za-z_]|^using\s+(?:\w+\s*=\s*)?Views\b", RegexOptions.CultureInvariant);

    // 名前空間全体 BeMusicSeeker.ViewModels(.X) を拾う。ViewModelsSupport など語の続く名前空間には一致しない。
    private static readonly Regex QualifiedViewModels = new(@"\bBeMusicSeeker\s*\.\s*ViewModels\b", RegexOptions.CultureInvariant);

    // 名前空間の内側で書ける相対参照 ViewModels.X と using ViewModels を拾う。chartViewModels.Count や owner.ViewModels.Count は拾わない。
    private static readonly Regex RelativeViewModels = new(@"(?<![\w.])ViewModels\.[A-Za-z_]|^using\s+(?:\w+\s*=\s*)?ViewModels\b", RegexOptions.CultureInvariant);

    /// <summary>Models配下のソースが Views 名前空間へ依存せず、窓口の実装を構成が注入する層の向きを保ちます。</summary>
    [TestMethod]
    public void ModelsSources_DoNotReferenceViewsNamespace()
    {
        List<string> violations = FindViolations("Models", ReferencesViews);
        Assert.AreEqual(0, violations.Count, "Models は Views に依存しません:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Models配下のソースが ViewModels 名前空間へ依存せず、Models と画面の両方が使う永続の値の型を Models に置く層の向きを保ちます。</summary>
    [TestMethod]
    public void ModelsSources_DoNotReferenceViewModelsNamespace()
    {
        List<string> violations = FindViolations("Models", ReferencesViewModels);
        Assert.AreEqual(0, violations.Count, "Models は ViewModels に依存しません:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Properties配下のソースが Views 名前空間へ依存しない層の向きを保ちます。</summary>
    [TestMethod]
    public void PropertiesSources_DoNotReferenceViewsNamespace()
    {
        List<string> violations = FindViolations("Properties", ReferencesViews);
        Assert.AreEqual(0, violations.Count, "Properties は Views に依存しません:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Properties配下のソースが ViewModels 名前空間へ依存せず、設定の型を Models に置く層の向きを保ちます。</summary>
    [TestMethod]
    public void PropertiesSources_DoNotReferenceViewModelsNamespace()
    {
        List<string> violations = FindViolations("Properties", ReferencesViewModels);
        Assert.AreEqual(0, violations.Count, "Properties は ViewModels に依存しません:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>検出規則が Views への各種の参照記述を拾い、ViewModels や識別子の一部を拾わないことを確かめます。</summary>
    [TestMethod]
    [DataRow("using BeMusicSeeker.Views;", true)]
    [DataRow("using BeMusicSeeker.Views.Dialogs;", true)]
    [DataRow("using V = BeMusicSeeker.Views;", true)]
    [DataRow("var x = global::BeMusicSeeker.Views.Dialogs.UiDialogCoordinator.Instance;", true)]
    [DataRow("namespace BeMusicSeeker.Views.Dialogs;", true)]
    [DataRow("namespace BeMusicSeeker.Views;", true)]
    [DataRow("var x = Views.Dialogs.UiDialogCoordinator.Instance;", true)]
    [DataRow("using Views;", true)]
    [DataRow("var n = chartViews.Count;", false)]
    [DataRow("using BeMusicSeeker.ViewModels;", false)]
    [DataRow("using BeMusicSeeker.ViewModels.Settings;", false)]
    [DataRow("// using BeMusicSeeker.Views;", false)]
    public void ViewsReferenceDetector_SeparatesViewsFromSimilarNames(string line, bool expected)
    {
        Assert.AreEqual(expected, ReferencesViews(line), line);
    }

    /// <summary>検出規則が ViewModels への各種の参照記述を拾い、似た名前・コメント・他の層の記述を拾わないことを確かめます。</summary>
    [TestMethod]
    [DataRow("using BeMusicSeeker.ViewModels;", true)]
    [DataRow("using BeMusicSeeker.ViewModels.Settings;", true)]
    [DataRow("using VM = BeMusicSeeker.ViewModels.PlaybackPanelViewModel;", true)]
    [DataRow("using static BeMusicSeeker.ViewModels.ResourceService;", true)]
    [DataRow("var x = global::BeMusicSeeker.ViewModels.ResourceService.Current;", true)]
    [DataRow("private BeMusicSeeker.ViewModels.PlayerPanelState state;", true)]
    [DataRow("namespace BeMusicSeeker.ViewModels;", true)]
    [DataRow("var x = ViewModels.ResourceService.Current;", true)]
    [DataRow("using ViewModels;", true)]
    [DataRow("using VM = ViewModels.PlaybackPanelViewModel;", true)]
    [DataRow("var n = chartViewModels.Count;", false)]
    [DataRow("var n = owner.ViewModels.Count;", false)]
    [DataRow("/// Owns application-level lifecycle actions consumed by ViewModels.", false)]
    [DataRow("/// 短文と詳細は ViewModels の整形処理が読取り時に現在の表示言語で作るため、言語を変えても警告の同一性は変わりません。", false)]
    [DataRow("// using BeMusicSeeker.ViewModels;", false)]
    [DataRow("using BeMusicSeeker.Views;", false)]
    [DataRow("using BeMusicSeeker.Models;", false)]
    [DataRow("namespace BeMusicSeeker.Properties;", false)]
    [DataRow("using BeMusicSeeker.ViewModelsSupport;", false)]
    public void ViewModelsReferenceDetector_SeparatesViewModelsFromSimilarNames(string line, bool expected)
    {
        Assert.AreEqual(expected, ReferencesViewModels(line), line);
    }

    private static List<string> FindViolations(string directoryName, Func<string, bool> references)
    {
        string root = Path.Combine(ThemeTestFiles.FindRepositoryRoot(), "BeMusicSeeker", directoryName);
        Assert.IsTrue(Directory.Exists(root), root);
        List<string> violations = [];
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string[] segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
            if (segments.Any(segment => segment is "bin" or "obj"))
            {
                continue;
            }
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (references(lines[i]))
                {
                    violations.Add(directoryName + "/" + Path.GetRelativePath(root, path) + ":" + (i + 1) + " " + lines[i].Trim());
                }
            }
        }
        return violations;
    }

    // コメント行を除き、完全修飾と相対参照の二本立てで判定する。本体の走査とこのテストが同じ関数を使う。
    private static bool ReferencesViews(string line) => References(line, QualifiedViews, RelativeViews);

    private static bool ReferencesViewModels(string line) => References(line, QualifiedViewModels, RelativeViewModels);

    private static bool References(string line, Regex qualified, Regex relative)
    {
        string trimmed = line.TrimStart();
        return !trimmed.StartsWith("//", StringComparison.Ordinal)
            && (qualified.IsMatch(trimmed) || relative.IsMatch(trimmed));
    }
}
