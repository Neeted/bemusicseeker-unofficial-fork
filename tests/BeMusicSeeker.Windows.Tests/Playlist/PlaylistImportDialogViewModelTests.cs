using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlaylistImportDialogTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>表示や通信から独立した選択・検索・照合・入力の条件を検証します。</summary>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class PlaylistImportDialogViewModelTests
{

    /// <summary>空白だけの第二分類は直下の葉として扱い、非空の分類名は前後空白を保持します。元構造の子分類を持つ親だけを初期展開し、通常開閉を非表示・カタログ・検索解除・実翻訳変更後にも復元します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void Expansion_RestoresNormalStateAfterFiltersSearchAndCultureRefresh()
    {
        System.Globalization.CultureInfo? previous = BeMusicSeeker.Properties.Resources.Culture;
        try
        {
            BeMusicSeeker.Properties.Resources.Culture = System.Globalization.CultureInfo.GetCultureInfo("ja-JP");
            using PlaylistImportDialogViewModel vm = Create();
            PlaylistImportCandidate a = C("a", "Alpha", tag1: "A", tag2: "Same child");
            PlaylistImportCandidate b = C("b", "Beta", tag1: "B", tag2: "Same child");
            PlaylistImportCandidate leafOnly = C("leaf", "Leaf only", tag1: "Leaf parent", tag2: " \t\u3000");
            PlaylistImportCandidate mixedDirect = C("mixed-direct", "Direct", tag1: "Mixed", tag2: " \t\u3000");
            const string nestedName = " \tChild\u3000 ";
            PlaylistImportCandidate mixedChild = C("mixed-child", "Nested", tag1: "Mixed", tag2: nestedName);
            PlaylistImportCandidate[] catalog = [a, b, leafOnly, mixedDirect, mixedChild];
            vm.UpdateCatalog(catalog, PlaylistImportCatalogState.Loaded);
            Assert.IsTrue(vm.ExternalGroups.Where(x => x.Name is "A" or "B" or "Mixed").All(x => x.IsExpanded));
            PlaylistImportItem leafParent = vm.ExternalGroups.Single(x => x.Name == "Leaf parent");
            Assert.IsFalse(leafParent.IsExpanded);
            Assert.IsTrue(leafParent.Children.Single().IsLeaf);
            Assert.AreEqual(leafOnly.Uri.AbsoluteUri, leafParent.Children.Single().Candidate?.Uri.AbsoluteUri);
            PlaylistImportItem initialMixed = vm.ExternalGroups.Single(x => x.Name == "Mixed");
            Assert.AreEqual(mixedDirect.Uri.AbsoluteUri, initialMixed.Children.Single(x => x.IsLeaf).Candidate?.Uri.AbsoluteUri);
            PlaylistImportItem nested = initialMixed.Children.Single(x => x.IsGroup);
            Assert.AreEqual(nestedName, nested.Name);
            Assert.AreEqual(mixedChild.Uri.AbsoluteUri, nested.Children.Single().Candidate?.Uri.AbsoluteUri);
            Assert.IsTrue(AllItems(vm.ExternalGroups).Where(x => x.IsGroup).All(x => !string.IsNullOrWhiteSpace(x.Name)), "空白名の子分類を生成しません。");
            Assert.IsTrue(AllItems(vm.ExternalGroups).Where(x => x.IsGroup && (x.Name == "Same child" || x.Name == nestedName)).All(x => !x.IsExpanded));
            Assert.IsTrue(vm.BuiltInGroups.All(x => x.IsExpanded));
            string japaneseCategoryName = vm.BuiltInGroups[0].Name;
            Assert.AreEqual("難度推定表", japaneseCategoryName, "初期表示は実際の日本語訳を使う。");
            Uri categoryFirstUri = vm.BuiltInGroups[0].Children[0].Candidate?.Uri ?? throw new AssertFailedException("内蔵カテゴリの候補が必要です。");
            // 既定と反対へ操作し、開く・閉じるの両方向の保持を確認します。
            vm.ExternalGroups[0].IsExpanded = false; vm.ExternalGroups[0].Children[0].IsExpanded = true;
            vm.ExternalGroups.Single(x => x.Name == "Leaf parent").IsExpanded = true;
            vm.BuiltInGroups[0].IsExpanded = false;
            vm.UpdateExisting([new("Registered A", a.Uri, null), new("Registered child", mixedChild.Uri, null)]);
            Assert.IsFalse(vm.ExternalGroups[0].IsVisible);
            PlaylistImportItem mixed = vm.ExternalGroups.Single(x => x.Name == "Mixed");
            Assert.IsTrue(mixed.IsVisible && mixed.IsExpanded, "子分類が全部隠れても元構造の既定展開を維持する。");
            Assert.IsFalse(mixed.Children.Single(x => x.IsGroup).IsVisible);
            vm.UpdateCatalog(catalog, PlaylistImportCatalogState.Loaded);
            Assert.IsTrue(vm.ExternalGroups.Single(x => x.Name == "Mixed").IsExpanded, "フィルター中のカタログ更新も元構造で既定を決める。");
            vm.HideRegisteredUrls = false;
            Assert.IsFalse(vm.ExternalGroups[0].IsExpanded); Assert.IsTrue(vm.ExternalGroups[0].Children[0].IsExpanded);
            Assert.IsFalse(vm.ExternalGroups[1].Children[0].IsExpanded, "別親の同名分類を混同しない。");
            Assert.IsTrue(vm.ExternalGroups.Single(x => x.Name == "Leaf parent").IsExpanded);
            vm.UpdateCatalog(catalog, PlaylistImportCatalogState.Loaded);
            Assert.IsFalse(vm.ExternalGroups[0].IsExpanded); Assert.IsTrue(vm.ExternalGroups[0].Children[0].IsExpanded);
            Assert.IsTrue(vm.ExternalGroups.Single(x => x.Name == "Leaf parent").IsExpanded);
            vm.SearchText = "Alpha";
            Assert.IsTrue(vm.ExternalGroups[0].IsExpanded && vm.ExternalGroups[0].Children[0].IsExpanded);
            Assert.IsFalse(vm.BuiltInGroups[0].IsExpanded);
            vm.ExternalGroups[0].Children[0].IsExpanded = false;
            vm.SearchText = "";
            Assert.IsFalse(vm.ExternalGroups[0].IsExpanded); Assert.IsTrue(vm.ExternalGroups[0].Children[0].IsExpanded);
            BeMusicSeeker.Properties.Resources.Culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            vm.RefreshCulture();
            Assert.AreEqual("Difficulty estimation", vm.BuiltInGroups[0].Name, "英語JSONが未配置で日本語へフォールバックしても成功しない。");
            Assert.AreNotEqual(japaneseCategoryName, vm.BuiltInGroups[0].Name, "カテゴリの翻訳名が実際に変化する。");
            Assert.AreEqual(categoryFirstUri, vm.BuiltInGroups[0].Children[0].Candidate?.Uri, "翻訳後も同じ内蔵カテゴリを観測する。");
            Assert.IsFalse(vm.BuiltInGroups[0].IsExpanded);
            Assert.IsTrue(vm.BuiltInGroups[1].IsExpanded);
            Assert.IsFalse(vm.ExternalGroups[0].IsExpanded); Assert.IsTrue(vm.ExternalGroups[0].Children[0].IsExpanded);
            Assert.IsTrue(vm.ExternalGroups.Single(x => x.Name == "Leaf parent").IsExpanded);
        }
        finally { BeMusicSeeker.Properties.Resources.Culture = previous; }
    }

}
