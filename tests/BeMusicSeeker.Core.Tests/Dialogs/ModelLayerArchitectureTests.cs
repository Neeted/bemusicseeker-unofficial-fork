using System.Text.RegularExpressions;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

/// <summary>
/// モデルの層が画面の層へ依存しない向きを保ちます。
/// ソース文字列を走査する理由: Models と Views は同じアセンブリにあり、コンパイル後の型・参照からは名前空間の層を
/// 区別できる境界がありません。メンバーの本体が参照する型まで型情報だけで網羅する手段もないため、例外として
/// ソースの参照記述を検査します。
/// 固定範囲: <c>BeMusicSeeker/Models</c> 配下の .cs（bin・obj とコメント行を除く）が、<c>BeMusicSeeker.Views</c> 名前空間の
/// using・別名・完全修飾名・名前空間宣言、および Models 名前空間内の相対参照 <c>Views.X</c> を含まないことだけです。
/// 管理主体: 層の分担を定めるアーキテクチャ仕様の管理主体。
/// 退役条件: Models と Views が別アセンブリに分かれ、層の向きをプロジェクト参照とコンパイルで強制できたとき。
/// </summary>
[TestClass]
public sealed class ModelLayerArchitectureTests
{
    // 名前空間全体 BeMusicSeeker.Views(.X) を、using・別名・global::・namespace 宣言を問わず拾う。ViewModels には一致しない。
    private static readonly Regex QualifiedViews = new(@"\bBeMusicSeeker\s*\.\s*Views\b", RegexOptions.CultureInvariant);

    // Models 名前空間の内側で書ける相対参照 Views.X を拾う。chartViews.Count のような識別子の一部は拾わない。
    private static readonly Regex RelativeViews = new(@"(?<![\w.])Views\.[A-Za-z_]", RegexOptions.CultureInvariant);

    /// <summary>Models配下のソースが Views 名前空間へ依存せず、窓口の実装を構成が注入する層の向きを保ちます。</summary>
    [TestMethod]
    public void ModelsSources_DoNotReferenceViewsNamespace()
    {
        string modelsRoot = Path.Combine(ThemeTestFiles.FindRepositoryRoot(), "BeMusicSeeker", "Models");
        Assert.IsTrue(Directory.Exists(modelsRoot), modelsRoot);
        List<string> violations = [];
        foreach (string path in Directory.EnumerateFiles(modelsRoot, "*.cs", SearchOption.AllDirectories))
        {
            string[] segments = Path.GetRelativePath(modelsRoot, path).Split(Path.DirectorySeparatorChar);
            if (segments.Any(segment => segment is "bin" or "obj"))
            {
                continue;
            }
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (ReferencesViews(lines[i]))
                {
                    violations.Add(Path.GetRelativePath(modelsRoot, path) + ":" + (i + 1) + " " + lines[i].Trim());
                }
            }
        }
        Assert.AreEqual(0, violations.Count, "Models は Views に依存しません:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
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
    [DataRow("var n = chartViews.Count;", false)]
    [DataRow("using BeMusicSeeker.ViewModels;", false)]
    [DataRow("using BeMusicSeeker.ViewModels.Settings;", false)]
    [DataRow("// using BeMusicSeeker.Views;", false)]
    public void ViewsReferenceDetector_SeparatesViewsFromSimilarNames(string line, bool expected)
    {
        Assert.AreEqual(expected, ReferencesViews(line), line);
    }

    // コメント行を除き、完全修飾と相対参照の二本立てで判定する。本体の走査とこのテストが同じ関数を使う。
    private static bool ReferencesViews(string line)
    {
        string trimmed = line.TrimStart();
        return !trimmed.StartsWith("//", StringComparison.Ordinal)
            && (QualifiedViews.IsMatch(trimmed) || RelativeViews.IsMatch(trimmed));
    }
}
