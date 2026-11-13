using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using static BeMusicSeeker.TestSupport.Components.InstallServiceComponentTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed partial class BmsLibraryPackageInstallServiceTests
{
    /// <summary>直接file指定から補助画像・明示PREVIEWの実在・相対範囲・音声互換を確認し、通常音声の参照数・不足とPREVIEW非保存を維持します。全選択や通常音源による偶然のfolder化を使いません。</summary>
    [TestMethod]
    [DataRow("stagefile", true)]
    [DataRow("backbmp", true)]
    [DataRow("banner", true)]
    [DataRow("preview", true)]
    [DataRow("preview-sub", true)]
    [DataRow("preview-case", true)]
    [DataRow("preview-first", true)]
    [DataRow("preview-cp932", true)]
    [DataRow("preview-utf8", true)]
    [DataRow("preview-unknown-extension", true)]
    [DataRow("stagefile-later-excluded", false)]
    [DataRow("stagefile-later-missing", false)]
    [DataRow("unknown-bmp", false)]
    [DataRow("preview-missing", false)]
    [DataRow("preview-parent", false)]
    [DataRow("preview-rooted", false)]
    [DataRow("preview-invalid", false)]
    public void PrepareAutoInstallWorkflow_SupplementEvidencePromotesOnlyActualAdjacentScope(string kind, bool promoted)
    {
        WithTemporaryDirectory(root =>
        {
            string directory = Path.Combine(root, "Input");
            Directory.CreateDirectory(directory);
            string directive = string.Empty;
            string resource = "trailer.ogg";
            if (kind is "stagefile" or "backbmp" or "banner")
            {
                directive = "#" + kind.ToUpperInvariant() + " artwork.png\n";
                resource = "artwork.png";
            }
            else if (kind.StartsWith("stagefile-later", StringComparison.Ordinal))
            {
                directive = "#STAGEFILE " + (kind.EndsWith("excluded", StringComparison.Ordinal) ? ".png" : "missing.png") + "\n#STAGEFILE artwork.png\n";
                resource = "artwork.png";
            }
            else if (kind == "unknown-bmp") { directive = "#BMP01 mystery.xyz\n#00104:01\n"; resource = "mystery.png"; }
            else if (kind.StartsWith("preview", StringComparison.Ordinal))
            {
                string reference = kind switch
                {
                    "preview-sub" => "media/trailer.wav",
                    "preview-case" => "TRAILER.WAV",
                    "preview-parent" => "../trailer.wav",
                    "preview-rooted" => Path.Combine(root, "trailer.wav"),
                    "preview-invalid" => "bad|name.wav",
                    "preview-cp932" or "preview-utf8" => "試聴.wav",
                    "preview-unknown-extension" => "trailer.xyz",
                    _ => "trailer.wav"
                };
                directive = "  #pReViEw\t" + reference + "\n";
                if (kind == "preview-first") { directive = "#PREVIEW   \n" + directive + "#PREVIEW missing.wav\n"; }
                if (kind == "preview-sub") { resource = Path.Combine("media", "trailer.mp3"); }
                if (kind is "preview-cp932" or "preview-utf8") { resource = "試聴.flac"; }
                if (kind == "preview-missing") { resource = "unrelated.ogg"; }
            }
            string physical = kind is "preview-parent" or "preview-rooted" ? Path.Combine(root, "trailer.wav") : Path.Combine(directory, resource);
            Directory.CreateDirectory(Path.GetDirectoryName(physical) ?? throw new AssertFailedException());
            File.WriteAllBytes(physical, [9, 7]);
            string first = Path.Combine(directory, "a.pms");
            string second = Path.Combine(directory, "b.bms");
            File.WriteAllText(first, "#TITLE Song\n#ARTIST Artist\n#WAV01 missing-a.wav\n#00111:01\n" + directive, kind == "preview-utf8" ? new UTF8Encoding(true) : Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(second, "#TITLE Other\n#WAV01 missing-b.wav\n#00111:01\n", Encoding.ASCII);
            File.WriteAllText(Path.Combine(directory, "readme.txt"), "unselected");
            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow([first], [], [], _ => false);
            ChartPackage package = result.DiscoveredPackages.Single();
            Assert.AreEqual(promoted ? directory : first, package.path);
            CollectionAssert.AreEquivalent(promoted ? new[] { first, second } : new[] { first }, package.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
            ChartFile chart = package.ChartEntries.Single(entry => entry.Chart.Path == first).Chart;
            var resources = ChartResourceSnapshot.Create(chart.Resources, includeUnused: true);
            Assert.AreEqual(1, resources.AudioReferenceCount);
            Assert.AreEqual(0, chart.WAVHealth);
            Assert.AreEqual(string.Empty, chart.PreviewMusic);
            Assert.IsFalse(resources.HasUnsupportedParentTraversalReference, "発見専用PREVIEWの親参照を通常資源へ混入させません。");
        });
    }

    /// <summary>直下のpreview・_preview音源だけを両発見入口の根拠とし、親直下の兄弟と子譜面を欠落・重複なく一式へ含めます。暗黙音源の要求化・PREVIEW保存と、非音声・中間一致・子階層・同名directoryによる誤昇格を検出します。</summary>
    [TestMethod]
    [DataRow("_preview.wav", false, true)]
    [DataRow("_PrEvIeW_demo.FLAC", false, true)]
    [DataRow("preview.ogg", false, true)]
    [DataRow("preview_demo.wav", false, true)]
    [DataRow("PrEvIeW_demo.FLAC", false, true)]
    [DataRow("_preview_demo.mp3", false, true)]
    [DataRow("_preview.png", false, false)]
    [DataRow("preview.png", false, false)]
    [DataRow("x_preview.wav", false, false)]
    [DataRow("__preview.wav", false, false)]
    [DataRow("media/_preview.wav", false, false)]
    [DataRow("media/preview.wav", false, false)]
    [DataRow("_preview.wav", true, false)]
    public void ImplicitPreviewAudio_PromotesOnlyAdjacentAudioAndIncludesEachChartOnce(string relativePath, bool directoryResource, bool promoted)
    {
        WithTemporaryDirectory(root =>
        {
            string parent = Path.Combine(root, "Input");
            string a = CreateBmsFile(parent, "a.bms", "#TITLE A\n#WAV01 missing-a.wav\n#00111:01\n");
            string b = CreateBmsFile(parent, "b.bms", "#TITLE B\n#WAV01 missing-b.wav\n#00111:01\n");
            string child = Path.Combine(parent, "sub");
            string c = CreateBmsFile(child, "c.bms", "#TITLE C\n#WAV01 missing-c.wav\n#00111:01\n");
            File.WriteAllText(Path.Combine(parent, "readme.txt"), "unselected");
            string physical = Path.Combine(parent, relativePath);
            Directory.CreateDirectory(directoryResource ? physical : Path.GetDirectoryName(physical) ?? throw new AssertFailedException());
            if (!directoryResource) { File.WriteAllBytes(physical, [9, 7]); }
            var service = new BmsLibraryPackageInstallService();

            ChartPackageDiscoveryResult discovery = service.SearchChartPackagesRecursivelyWithMetadata(parent);
            Assert.IsTrue(discovery.IsComplete);
            AssertPackages(discovery.Packages, promoted ? [parent] : [a, b, child], [a, b, c]);

            AutoInstallWorkflowResult workflow = service.PrepareAutoInstallWorkflow([a], [], [], _ => false);
            AssertPackages(workflow.DiscoveredPackages, promoted ? [parent] : [a], promoted ? [a, b, c] : [a]);
            // 健康度は発見だけの入口では計算せず、自動導入準備で評価した結果を観測します。
            foreach (PackageChartEntry entry in workflow.DiscoveredPackages.SelectMany(package => package.ChartEntries))
            { Assert.AreEqual(0, entry.Chart.WAVHealth); }
            if (relativePath == "_preview.wav" && promoted)
            {
                AutoInstallWorkflowResult overlapping = service.PrepareAutoInstallWorkflow([a, c, a], [], [], _ => false);
                AssertPackages(overlapping.DiscoveredPackages, [parent], [a, b, c]);
            }

            void AssertPackages(IEnumerable<ChartPackage> packages, string[] packagePaths, string[] chartPaths)
            {
                ChartPackage[] actual = packages.ToArray();
                CollectionAssert.AreEquivalent(packagePaths, actual.Select(package => package.path).ToArray());
                CollectionAssert.AreEquivalent(chartPaths, actual.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart.Path).ToArray());
                foreach (PackageChartEntry entry in actual.SelectMany(package => package.ChartEntries))
                {
                    Assert.AreEqual(1, ChartResourceSnapshot.Create(entry.Chart.Resources, includeUnused: true).AudioReferenceCount);
                    Assert.AreEqual(string.Empty, entry.Chart.PreviewMusic);
                }
            }
        });
    }

    /// <summary>暗黙previewと同じ音源を使用WAVで明示した場合も通常要求一件として充足させ、暗黙検出による二重計数や通常要求の除外を防ぎます。</summary>
    [TestMethod]
    public void PrepareAutoInstallWorkflow_ExplicitWavUsingImplicitPreviewAudioRemainsOneSatisfiedRequest()
    {
        WithTemporaryDirectory(root =>
        {
            string parent = Path.Combine(root, "Input");
            string path = CreateBmsFile(parent, "a.bms", "#TITLE A\n#WAV01 _preview.wav\n#00111:01\n");
            File.WriteAllBytes(Path.Combine(parent, "_preview.wav"), [9, 7]);
            File.WriteAllText(Path.Combine(parent, "readme.txt"), "unselected");

            AutoInstallWorkflowResult result = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow([path], [], [], _ => false);

            ChartPackage package = result.DiscoveredPackages.Single();
            Assert.AreEqual(parent, package.path);
            ChartFile chart = package.ChartEntries.Single().Chart;
            Assert.AreEqual(1, ChartResourceSnapshot.Create(chart.Resources, includeUnused: true).AudioReferenceCount);
            Assert.AreEqual(100, chart.WAVHealth);
            Assert.AreEqual(string.Empty, chart.PreviewMusic);
        });
    }

    /// <summary>
    /// 推定前に入力単位を確定し、フォルダ指定と直下全項目選択を同じ規則で分けます。資源なし・譜面を持つ子なしで同じ作品（同じTITLE・ARTIST）なら非譜面の子（readme、sub/omakefile）を含むDの一式、別曲なら規則5でA/Bの二単体です。
    /// 部分選択は作品の同一性によらず二単体です。譜面を持つ子を含む差分集では親直下を単体化し（規則2）、親資源があれば子を含む全一式を優先します（規則1）。
    /// 検出する誤り: フォルダ指定と全項目選択で単位がずれる、部分選択を規則4で一式へ広げる、別曲を一式にまとめる。
    /// </summary>
    [TestMethod]
    [DataRow("folder", true, false, false)]
    [DataRow("all", true, false, false)]
    [DataRow("partial", true, false, false)]
    [DataRow("folder", false, false, false)]
    [DataRow("all", false, false, false)]
    [DataRow("partial", false, false, false)]
    [DataRow("folder", false, true, false)]
    [DataRow("all", false, true, false)]
    [DataRow("folder", false, true, true)]
    [DataRow("all", false, true, true)]
    public void PrepareAutoInstallWorkflow_PreservesInputUnitBeforeDestinationEstimation(string selection, bool sameWork, bool chartChildren, bool parentResource)
    {
        WithTemporaryDirectory(root =>
        {
            string directory = Path.Combine(root, "Input");
            Directory.CreateDirectory(directory);
            string a = CreateBmsFile(directory, "a.bms", "#TITLE " + (sameWork ? "Same" : "A") + "\n#WAV01 a.wav\n#00111:01\n" + (parentResource ? "#BACKBMP cover.png\n" : ""));
            string b = CreateBmsFile(directory, "b.bms", "#TITLE " + (sameWork ? "Same" : "B") + "\n#WAV01 b.wav\n#00111:01\n");
            File.WriteAllText(Path.Combine(directory, "readme.txt"), "readme");
            string sub = Path.Combine(directory, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, "omakefile.txt"), [2]);
            var expected = new List<string>();
            if (sameWork || parentResource) { expected.Add(directory); }
            else { expected.AddRange([a, b]); }
            if (chartChildren)
            {
                string song1 = Path.Combine(directory, "song1");
                string song2 = Path.Combine(directory, "song2");
                CreateBmsFile(song1, "c.bms", "#TITLE C\n#WAV01 c.wav\n#00111:01\n");
                CreateBmsFile(song2, "d.bms", "#TITLE D\n#WAV01 add.ogg\n#00111:01\n");
                File.WriteAllBytes(Path.Combine(song2, "add.ogg"), [3]);
                if (!parentResource) { expected = [a, b, song1, song2]; }
            }
            if (parentResource) { File.WriteAllBytes(Path.Combine(directory, "cover.png"), [4]); }
            if (selection == "partial") { expected = [a, b]; }
            string[] paths = selection switch { "folder" => [directory], "all" => Directory.GetFileSystemEntries(directory), _ => [a, b] };
            AutoInstallWorkflowResult result = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow(paths, [], [], _ => false);
            CollectionAssert.AreEquivalent(expected, result.DiscoveredPackages.Select(package => package.path).ToArray());
            Assert.AreEqual(chartChildren ? 4 : 2, result.DiscoveredPackages.SelectMany(package => package.ChartEntries).Count());
        });
    }

    /// <summary>
    /// 子を常に再帰して発見し、直下に譜面があるディレクトリを規則1〜5の順で最初に当たった規則で分けます。全行で readme と未参照の png を同居させ、どの譜面も参照しないファイルを一式の根拠にしないことを確かめます。
    /// 規則4の境界（正規化で吸収する大小文字・全角・副役割・SUBTITLE・SUBARTISTの差、吸収しないMIX名・リミックス作者・タイトルの差、過半数一致、空TITLE、空ARTIST、bmson、BMSとbmsonの混在）、規則の優先順、合成したStella型差分集の同梱物の有無、複数曲が共有する補助画像、子ディレクトリ内の規則5を観測します。
    /// 観測は発見の完全性、パッケージpath（Dか譜面ファイルか）、パッケージごとの譜面集合（全譜面がちょうど1回）、親削除指定です。
    /// 検出する誤り: 別曲を一式にまとめる旧規則の直下一式化、表記差だけの同じ作品を単体へ割る、類似度や過半数で一式にする、空TITLE同士を同じ作品とみなす、規則の適用順の取り違え、子ディレクトリ内の単体の親削除指定の欠落。
    /// </summary>
    [TestMethod]
    [DataRow("case-width")]
    [DataRow("artist-role")]
    [DataRow("subtitle-only")]
    [DataRow("subartist-only")]
    [DataRow("bracket-difficulty")]
    [DataRow("mix-name")]
    [DataRow("remixer")]
    [DataRow("different-title")]
    [DataRow("majority")]
    [DataRow("empty-title-missing")]
    [DataRow("empty-title-blank")]
    [DataRow("empty-title-both")]
    [DataRow("artist-both-empty")]
    [DataRow("artist-one-empty")]
    [DataRow("bmson-same")]
    [DataRow("bmson-artist-differs")]
    [DataRow("bms-bmson")]
    [DataRow("bmson-subartist")]
    [DataRow("rule1-over-rule5")]
    [DataRow("rule2-over-rule4")]
    [DataRow("rule3-empty-title")]
    [DataRow("stella-bundled")]
    [DataRow("stella-unbundled")]
    [DataRow("shared-banner-multi-song")]
    [DataRow("nested-rule5")]
    public void SearchChartPackagesRecursivelyWithMetadata_DirectChartsFollowUnitRulesInOrder(string condition)
    {
        WithTemporaryDirectory(root =>
        {
            string directory = Path.Combine(root, "Input");
            string charts = condition == "nested-rule5" ? Path.Combine(directory, "Child") : directory;
            Directory.CreateDirectory(charts);
            foreach (string owner in new[] { directory, charts }.Distinct())
            {
                File.WriteAllText(Path.Combine(owner, "readme.txt"), "readme");
                File.WriteAllBytes(Path.Combine(owner, "unused.png"), [1]);
            }
            var created = new List<string>();
            string Bms(string name, string? title, string? artist, string extra = "")
            {
                string path = Path.Combine(charts, name);
                File.WriteAllText(path, "#PLAYER 1\n" + (title == null ? string.Empty : "#TITLE " + title + "\n")
                    + (artist == null ? string.Empty : "#ARTIST " + artist + "\n")
                    + "#WAV01 missing-" + name + ".wav\n#00111:01\n" + extra, Encoding.GetEncoding("shift_jis"));
                created.Add(path);
                return path;
            }
            string Bmson(string name, string title, string artist, string subartists = "")
            {
                string path = Path.Combine(charts, name);
                File.WriteAllText(path, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"" + title + "\",\"artist\":\"" + artist + "\",\"subartists\":[" + subartists
                    + "],\"mode_hint\":\"beat-7k\"},\"sound_channels\":[{\"name\":\"missing-" + name + ".wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]}", new UTF8Encoding(false));
                created.Add(path);
                return path;
            }
            void Stella(bool bundled)
            {
                (string Title, string Artist)[] songs = [("Stella (Alpha remix)", "AAA"), ("Stella (Beta remix)", "BBB vs. CCC"), ("Stella -Gamma mix-", "DDD"), ("Stella", "AAA")];
                for (int index = 0; index < songs.Length; index++)
                {
                    int number = index + 1;
                    string extra = !bundled ? string.Empty : "#PREVIEW preview_" + number + ".ogg\n#BACKBMP _backbmp_" + number + ".png\n" + (index == 0 ? "#STAGEFILE title_Another.png\n" : string.Empty);
                    Bms("chart" + number + ".bms", songs[index].Title, songs[index].Artist, extra);
                    if (bundled)
                    {
                        File.WriteAllBytes(Path.Combine(charts, "preview_" + number + ".ogg"), [2]);
                        File.WriteAllBytes(Path.Combine(charts, "_backbmp_" + number + ".png"), [3]);
                    }
                }
                if (bundled)
                {
                    File.WriteAllBytes(Path.Combine(charts, "title_Another.png"), [4]);
                    File.WriteAllBytes(Path.Combine(charts, "preview.ogg"), [5]);
                }
            }

            // true は直下の全譜面をDの一式、false は直下の各譜面を単体にする期待です。
            bool whole;
            string? child = null;
            switch (condition)
            {
                case "case-width": Bms("a.bms", "Song", "X"); Bms("b.bms", "ＳＯＮＧ", "X"); whole = true; break;
                case "artist-role": Bms("a.bms", "Song", "X"); Bms("b.bms", "Song", "X / obj:Y"); whole = true; break;
                case "subtitle-only": Bms("a.bms", "Song", "X", "#SUBTITLE [Normal]\n"); Bms("b.bms", "Song", "X", "#SUBTITLE [Another]\n"); whole = true; break;
                case "subartist-only": Bms("a.bms", "Song", "X", "#SUBARTIST obj:P\n"); Bms("b.bms", "Song", "X", "#SUBARTIST obj:Q\n"); whole = true; break;
                case "bracket-difficulty": Bms("a.bms", "Title [Another]", "X"); Bms("b.bms", "Title [Hyper]", "X"); whole = true; break;
                case "mix-name": Bms("a.bms", "Song (A mix)", "X"); Bms("b.bms", "Song (B mix)", "X"); whole = false; break;
                case "remixer": Bms("a.bms", "Song", "X"); Bms("b.bms", "Song", "X vs. Y"); whole = false; break;
                case "different-title": Bms("a.bms", "SongA", "X"); Bms("b.bms", "SongB", "X"); whole = false; break;
                case "majority": Bms("a.bms", "Song", "X"); Bms("b.bms", "Song", "X"); Bms("c.bms", "Other", "X"); whole = false; break;
                case "empty-title-missing": Bms("a.bms", null, "X"); Bms("b.bms", "Song", "X"); whole = false; break;
                case "empty-title-blank": Bms("a.bms", "   ", "X"); Bms("b.bms", "Song", "X"); whole = false; break;
                case "empty-title-both": Bms("a.bms", null, "X"); Bms("b.bms", "   ", "X"); whole = false; break;
                case "artist-both-empty": Bms("a.bms", "Song", null); Bms("b.bms", "Song", null); whole = true; break;
                case "artist-one-empty": Bms("a.bms", "Song", "X"); Bms("b.bms", "Song", null); whole = false; break;
                case "bmson-same": Bmson("a.bmson", "Song", "X"); Bmson("b.bmson", "Song", "X"); whole = true; break;
                case "bmson-artist-differs": Bmson("a.bmson", "Song", "X"); Bmson("b.bmson", "Song", "Y"); whole = false; break;
                case "bms-bmson": Bms("a.bms", "Song", "X"); Bmson("b.bmson", "Song", "X"); whole = true; break;
                case "bmson-subartist": Bmson("a.bmson", "Song", string.Empty, "\"X\""); Bms("b.bms", "Song", "X"); whole = true; break;
                case "rule1-over-rule5":
                    Bms("a.bms", "SongA", "X", "#WAV02 a.wav\n#00112:02\n"); Bms("b.bms", "SongB", "Y");
                    File.WriteAllBytes(Path.Combine(charts, "a.wav"), [6]);
                    whole = true;
                    break;
                case "rule2-over-rule4":
                    Bms("a.bms", "Song", "X"); Bms("b.bms", "Song", "X");
                    child = Path.Combine(directory, "Child");
                    CreateBmsFile(child, "c.bms", "#TITLE Other\n#WAV01 missing-c.wav\n#00111:01\n");
                    whole = false;
                    break;
                case "rule3-empty-title": Bms("a.bms", null, "X"); whole = true; break;
                case "stella-bundled": Stella(bundled: true); whole = true; break;
                case "stella-unbundled": Stella(bundled: false); whole = false; break;
                case "shared-banner-multi-song":
                    Bms("a.bms", "SongA", "X", "#BANNER banner.png\n"); Bms("b.bms", "SongB", "Y", "#BANNER banner.png\n");
                    File.WriteAllBytes(Path.Combine(charts, "banner.png"), [7]);
                    whole = true;
                    break;
                case "nested-rule5": Bms("a.bms", "SongA", "X"); Bms("b.bms", "SongB", "Y"); whole = false; break;
                default: throw new AssertFailedException(condition);
            }
            var expected = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (whole) { expected.Add(charts, [.. created]); }
            else { foreach (string path in created) { expected.Add(path, [path]); } }
            if (child != null) { expected.Add(child, [Path.Combine(child, "c.bms")]); }

            ChartPackageDiscoveryResult result = new BmsLibraryPackageInstallService().SearchChartPackagesRecursivelyWithMetadata(directory);

            Assert.IsTrue(result.IsComplete);
            CollectionAssert.AreEquivalent(expected.Keys.ToArray(), result.Packages.Select(package => package.path).ToArray());
            string[] discovered = [.. result.Packages.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart.Path)];
            CollectionAssert.AreEquivalent(expected.Values.SelectMany(paths => paths).ToArray(), discovered, "全譜面がちょうど1回だけ含まれます。");
            foreach (ChartPackage package in result.Packages)
            {
                CollectionAssert.AreEquivalent(expected[package.path], package.ChartEntries.Select(entry => entry.Chart.Path).ToArray(), package.path);
                bool single = File.Exists(package.path);
                Assert.AreEqual(!Directory.Exists(package.path), single);
                Assert.AreEqual(single && condition == "nested-rule5", package.delete_parent, "再帰中の子ディレクトリの単体だけが親削除指定を持ちます。");
            }
        });
    }
}
