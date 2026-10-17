using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartResourceSnapshotTests
{
    /// <summary>実BMS/PMSの全行から使用チャンネル・特殊ID・BASE62・同IDの全定義先を収集します。LNOBJを除外せず、未定義IDと地雷ダメージをファイルへ加えません。</summary>
    [TestMethod]
    [DataRow("bms", "")]
    [DataRow("pms", "")]
    [DataRow("bms", " \t")]
    [DataRow("pms", " \t")]
    [DataRow("bms", "\u3000")]
    [DataRow("pms", "\u3000")]
    public void ParseSnapshot_CollectsUsedResourceIdsWithoutCountingUnusedDefinitions(string extension, string prefix)
    {
        const string text = """
            #BASE 62
            #LNOBJ ZZ
            #WAV01 same.wav
            #WAV02 same.ogg
            #WAV03 hidden.wav
            #WAV04 long.wav
            #WAV05 unused.wav
            #WAV06 bgm.wav
            #WAVZZ terminal.wav
            #WAV0F damage.wav
            #WAV00 mine.wav
            #WAVAA upper.wav
            #WAVaa lower.wav
            #BMP00 poor.png
            #BMP01 bg.png
            #BMP02 movie.mp4
            #BMP03 unused.png
            #00111:0102ZZAA
            #00131:03
            #00151:04
            #00101:06
            #001D1:0F
            #00104:01
            #00106:02
            #00107:01
            #0010A:02
            #RANDOM 2
            #IF 1
            #WAV07 branch1.wav
            #00101:07
            #ELSE
            #WAV07 branch2.wav
            #ENDIF
            #WAV07 unconditional.wav
            #WAV07 unconditional.ogg
            #00121:09
            #00104:0A
            """;
        string prefixed = string.Join("\n", text.Split('\n').Select(line => prefix + line));
        ChartFile chart = BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"C:\Pending\chart." + extension,
            Encoding.GetEncoding("shift_jis").GetBytes(prefixed), DateTime.UtcNow, "md5", "sha256"));
        var used = ChartResourceSnapshot.Create(chart);
        CollectionAssert.AreEquivalent(new[] { "same", "hidden", "long", "bgm", "mine", "upper", "branch1", "branch2", "unconditional", "terminal" }, used.AudioRelativePaths.ToArray());
        CollectionAssert.AreEquivalent(new[] { "poor", "bg" }, used.VisualRelativePaths.ToArray());
        CollectionAssert.AreEquivalent(new[] { "movie" }, used.MovieRelativePaths.ToArray());
        Assert.IsFalse(used.HasUnresolvedAudio);
        Assert.IsFalse(used.HasUnresolvedVisual);
        Assert.IsFalse(used.HasUnresolvedMovie);
        var all = ChartResourceSnapshot.Create(chart.Resources, includeUnused: true);
        Assert.AreEqual(used.AudioReferenceCount + 3, all.AudioReferenceCount);
        Assert.AreEqual(used.VisualReferenceCount + 1, all.VisualReferenceCount);
        Assert.IsFalse(used.AudioRelativePaths.Contains("unused"));
        Assert.IsFalse(used.AudioRelativePaths.Contains("lower"));
    }

    /// <summary>軽量検査はRANDOM上限・不成立IF・入れ子の相関を評価せず、LNOBJ定義先も通常の使用音声として収集します。</summary>
    [TestMethod]
    [DataRow("#RANDOM 1\n#IF 2\n#LNOBJ 01\n#00111:01\n#ENDIF\n#ENDRANDOM\n", "r01")]
    [DataRow("#RANDOM 2\n#IF 1\n#IF 2\n#00111:02\n#ENDIF\n#ELSE\n#00111:03\n#ENDIF\n#ENDRANDOM\n#LNOBJ 02\n", "r02,r03")]
    public void ParseSnapshot_CollectsAllLinesWithoutEvaluatingConditions(string body, string expected)
    {
        foreach (string extension in new[] { "bms", "pms" })
        {
            string definitions = "#WAV01 r01.wav\n#WAV02 r02.wav\n#WAV03 r03.wav\n";
            ChartFile chart = BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"C:\Pending\branch." + extension,
                Encoding.ASCII.GetBytes(definitions + body), DateTime.UnixEpoch, "md5", "sha256"));
            CollectionAssert.AreEquivalent(expected.Split(','), ChartResourceSnapshot.Create(chart).AudioRelativePaths.ToArray());
        }
    }
    /// <summary>軽量検査では全音声チャンネルでLNOBJの定義先を要求します。</summary>
    [TestMethod]
    [DataRow("01", true)]
    [DataRow("11", true)]
    [DataRow("21", true)]
    [DataRow("31", true)]
    [DataRow("41", true)]
    [DataRow("51", true)]
    [DataRow("61", true)]
    public void ParseSnapshot_LnobjDefinitionIsRequiredForEveryAudioChannel(string channel, bool required)
    {
        string text = "#WAV01 sound.wav\n#LNOBJ 01\n#001" + channel + ":01\n";
        ChartFile chart = BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"C:\Pending\role.bms",
            Encoding.ASCII.GetBytes(text), DateTime.UnixEpoch, "md5", "sha256"));
        var resources = ChartResourceSnapshot.Create(chart);
        Assert.AreEqual(required ? 1 : 0, resources.AudioReferenceCount);
        Assert.IsFalse(resources.HasUnresolvedAudio);
    }

    /// <summary>チャンネル行頭の空白を許容して原文を二文字ずつ切り、コロン後の空白除去や末尾一文字の補修で別IDを作りません。コロンなしは従来どおり行全体をデータとして解釈します。</summary>
    [TestMethod]
    [DataRow("#00111: 100", "")]
    [DataRow("#00011: 0102", "10")]
    [DataRow("#00111:0 10", "10")]
    [DataRow("#00111:1", "")]
    [DataRow("#00111:010", "01")]
    [DataRow("#00111 :01", "01")]
    [DataRow("#00111 10", "01,11")]
    [DataRow(" \t#00111:01", "01")]
    [DataRow("\u3000#00111:01", "01")]
    [DataRow(" \t#00111: 100", "")]
    [DataRow("\u3000#00111: 100", "")]
    [DataRow(" \t#00111:0 10", "10")]
    [DataRow("\u3000#00111:010", "01")]
    [DataRow("\u3000#00111 10", "01,11")]
    public void ParseSnapshot_ResourceUsagePreservesChannelPairBoundaries(string channelLine, string expectedIds)
    {
        string text = "#WAV01 one.wav\n#WAV02 two.wav\n#WAV10 ten.wav\n#WAV11 eleven.wav\n" + channelLine + "\n";
        ChartFile chart = BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"C:\Pending\pairs.bms",
            Encoding.GetEncoding("shift_jis").GetBytes(text), DateTime.UtcNow, "md5", "sha256"));
        var resources = ChartResourceSnapshot.Create(chart);
        string[] expectedPaths = expectedIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => id switch
        { "01" => "one", "10" => "ten", _ => "eleven" }).ToArray();
        CollectionAssert.AreEquivalent(expectedPaths, resources.AudioRelativePaths.ToArray());
        Assert.IsFalse(resources.HasUnresolvedAudio);
    }

    /// <summary>WAV00は地雷がある時だけ、BMP00は定義だけでも要求し、LNOBJ終端は要求へ含め、地雷ダメージと同じIDの定義は除きます。</summary>
    [TestMethod]
    public void ParseSnapshot_SpecialResourceIdsFollowTheirPlaybackRole()
    {
        foreach (bool mine in new[] { false, true })
        {
            string text = "#WAV00 mine.wav\n#WAVZZ terminal.wav\n#WAV12 damage.wav\n#BMP00 poor.png\n#LNOBJ ZZ\n#00111:ZZ\n" + (mine ? "#001E1:12\n" : "");
            ChartFile chart = BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"C:\Pending\chart.bms", Encoding.ASCII.GetBytes(text), DateTime.UtcNow, "md5", "sha256"));
            var resources = ChartResourceSnapshot.Create(chart);
            Assert.AreEqual(mine ? 2 : 1, resources.AudioReferenceCount);
            Assert.IsTrue(resources.AudioRelativePaths.Contains("terminal"));
            Assert.IsFalse(resources.AudioRelativePaths.Contains("damage"));
            Assert.AreEqual(1, resources.VisualReferenceCount);
            Assert.IsFalse(resources.HasUnresolvedAudio);
        }
    }

    /// <summary>
    /// 保証：リソース集合の捕捉後に元の資源を追加しても既存のパス・ハッシュ集合を変更せず、操作途中の入力を固定する。
    /// </summary>
    [TestMethod]
    public void ResourcesSnapshotsRemainImmutableAfterOwnerMutation()
    {
        var resources = new Ribbit.BMS.Resources();
        resources.AddFilePath("sound.wav");
        ImmutableHashSet<string> paths = resources.FilePaths;
        ImmutableHashSet<uint> hashes = resources.FilePathsHashSet;

        resources.AddFilePath("image.png");

        Assert.AreEqual(1, paths.Count);
        Assert.AreEqual(1, hashes.Count);
        CollectionAssert.Contains(paths.ToArray(), "sound.wav");
        CollectionAssert.DoesNotContain(paths.ToArray(), "image.png");
    }

    /// <summary>
    /// 保証：音声・画像・動画の集約は最後の拡張子だけを除き、名前途中の点を保持して別リソースへ照合しない。
    /// </summary>
    [TestMethod]
    public void CreateAggregate_PreservesExtensionlessResourceKeysWithDotsInStem()
    {
        ChartFile file = CreateBmsFile(
            "C:\\Pending\\dot-stem.bms",
            ["4.org1_1.wav", "4.org1_2.wav", "5.bell_1.wav"],
            ["bg.final.png", "movie.opening.mpg"]);
        ChartFile chart = (file);

        var single = ChartResourceSnapshot.Create(chart);
        var aggregate = ChartResourceSnapshot.CreateAggregate([chart]);

        Assert.AreEqual(3, single.AudioReferenceCount);
        Assert.AreEqual(1, single.VisualReferenceCount);
        Assert.AreEqual(1, single.MovieReferenceCount);
        Assert.AreEqual(single.AudioReferenceCount, aggregate.AudioReferenceCount);
        Assert.AreEqual(single.VisualReferenceCount, aggregate.VisualReferenceCount);
        Assert.AreEqual(single.MovieReferenceCount, aggregate.MovieReferenceCount);
        CollectionAssert.AreEquivalent(single.AudioRelativePaths.ToArray(), aggregate.AudioRelativePaths.ToArray());
        CollectionAssert.Contains(aggregate.AudioRelativePaths.ToArray(), "4.org1_1");
        CollectionAssert.Contains(aggregate.AudioRelativePaths.ToArray(), "4.org1_2");
        CollectionAssert.DoesNotContain(aggregate.AudioRelativePaths.ToArray(), "4");
        CollectionAssert.Contains(aggregate.VisualRelativePaths.ToArray(), "bg.final");
        CollectionAssert.DoesNotContain(aggregate.VisualRelativePaths.ToArray(), "bg");
        CollectionAssert.Contains(aggregate.MovieRelativePaths.ToArray(), "movie.opening");
        CollectionAssert.DoesNotContain(aggregate.MovieRelativePaths.ToArray(), "movie");
    }

    /// <summary>
    /// 保証：階層付きリソース名から照合用のbasenameを作るときも途中の点を保持し、拡張子除去で名前を短縮しない。
    /// </summary>
    [TestMethod]
    public void GetLookupFileName_PreservesDotsInStem()
    {
        Assert.AreEqual("4.org1_1", ChartResourcePathNormalizer.GetLookupFileName(Path.Combine("sound", "4.org1_1.wav")));
        Assert.AreEqual("bg.final", ChartResourcePathNormalizer.GetLookupFileName(Path.Combine("visual", "bg.final.png")));
    }

    /// <summary>
    /// 保証：相対リソースの現在ディレクトリ記号だけを除き、先頭・途中に記号があっても同じ階層付きキーへ正規化する。
    /// </summary>
    [TestMethod]
    public void NormalizeReferencePathForLookup_SkipsCurrentDirectorySegments()
    {
        Assert.AreEqual("foo", ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(@".\foo.wav"));
        Assert.AreEqual(Path.Combine("sound", "foo"), ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(@".\sound\foo.wav"));
        Assert.AreEqual(Path.Combine("sound", "foo"), ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(@"sound\.\foo.wav"));
    }

    /// <summary>
    /// 保証：親参照のあるリソースを推定非対応として保持し、有効な照合キーを作って別フォルダへ誤推定しない。
    /// </summary>
    [TestMethod]
    public void AnalyzeReferencePathForLookup_RejectsParentTraversalAsUnsupported()
    {
        ChartResourcePathNormalizationResult result = ChartResourcePathNormalizer.AnalyzeReferencePathForLookup(@"..\pkg\foo.wav");

        Assert.AreEqual(ChartResourcePathNormalizationStatus.ParentTraversalUnsupported, result.Status);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(string.Empty, result.NormalizedPath);
    }

    /// <summary>
    /// 保証：親参照・空・NUL・絶対パスを区別し、名前中の二連点や先頭区切りは従来の有効入力として保持する。
    /// </summary>
    [TestMethod]
    [DataRow(@"sound\..\foo.wav", "ParentTraversalUnsupported", "")]
    [DataRow("foo..bar.wav", "Valid", "foo..bar.wav")]
    [DataRow("", "Empty", "")]
    [DataRow("bad\0.wav", "InvalidPath", "")]
    [DataRow(@"C:\sound\foo.wav", "RootedOrAbsolute", "")]
    [DataRow(@"\sound\foo.wav", "Valid", @"sound\foo.wav")]
    public void AnalyzeReferencePathForLookup_PreservesExistingPathClassification(string path, string status, string normalizedPath)
    {
        ChartResourcePathNormalizationResult result = ChartResourcePathNormalizer.AnalyzeReferencePathForLookup(path);

        Assert.AreEqual(status, result.Status.ToString());
        Assert.AreEqual(normalizedPath, result.NormalizedPath);
    }

    /// <summary>
    /// 保証：未知拡張子では照合件数を増やさず、実際に親参照する記述だけを非対応理由として単体・集約へ保持する。
    /// </summary>
    [TestMethod]
    [DataRow("mystery.xyz", false)]
    [DataRow("mystery..xyz", false)]
    [DataRow(@".\mystery.xyz", false)]
    [DataRow(@"sound\..\mystery.xyz", true)]
    [DataRow("", false)]
    [DataRow("bad\0.xyz", false)]
    [DataRow(@"C:\sound\mystery.xyz", false)]
    public void Create_UnknownResourcePreservesOnlyActualParentTraversal(string path, bool hasParentTraversal)
    {
        ChartFile file = CreateBmsFile(@"C:\Pending\unknown.bms", [], [path]);
        ChartFile chart = (file);
        foreach (ChartResourceSnapshot snapshot in new[] { ChartResourceSnapshot.Create(chart), ChartResourceSnapshot.CreateAggregate([chart]) })
        {
            Assert.AreEqual(0, snapshot.TotalReferenceCount);
            Assert.AreEqual(hasParentTraversal, snapshot.HasUnsupportedParentTraversalReference);
            Assert.AreEqual(hasParentTraversal ? 1 : 0, snapshot.UnsupportedResourceReferenceCount);
        }
    }

    /// <summary>
    /// 保証：実BMSの拡張子だけの空キーは照合件数へ加えず、使用カテゴリを未判定にする。通常キーと未知記述の取得・正規化・親参照判定を維持する。
    /// </summary>
    /// <remarks>本番のファイル読取り・解析から資源分類へ接続し、合成済みreferenceでは分からない空キーの生産を確認する。</remarks>
    [TestMethod]
    public void Create_ExtensionOnlyBmsResourcesKeepNormalKeysWithoutParentTraversal()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "ChartResourceSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string chartPath = Path.Combine(tempDirectory, "resources.bms");
            File.WriteAllText(chartPath,
                "#PLAYER 1\r\n#TITLE Resources\r\n"
                + "#WAVAA .\\sound\\kick.wav\r\n#WAVAB .wav\r\n#00101:AB\r\n#WAVAC .ogg\r\n#00101:AC\r\n"
                + "#BMPAA bg.final.png\r\n#00104:AA\r\n#BMPAB movie.mpg\r\n#00104:AB\r\n#BMPAC .png\r\n#00104:AC\r\n#BMPAE .mp4\r\n#00104:AE\r\n#BMPAD mystery.xyz\r\n#00104:AD\r\n"
                + "#BANNER .png\r\n#BACKBMP \r\n#STAGEFILE .png\r\n#00111:AA\r\n");
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            Assert.IsTrue(file.Resources.Any(reference => reference.NormalizedPath == "mystery.xyz"));
            Assert.IsTrue(file.Resources.Any(reference => reference.Kind == ChartResourceKind.Unknown && reference.RawPath == "mystery.xyz"));

            ChartFile chart = (file);
            foreach (ChartResourceSnapshot snapshot in new[] { ChartResourceSnapshot.Create(chart), ChartResourceSnapshot.CreateAggregate([chart]) })
            {
                Assert.AreEqual(1, snapshot.AudioReferenceCount);
                Assert.AreEqual(1, snapshot.VisualReferenceCount);
                Assert.AreEqual(1, snapshot.MovieReferenceCount);
                Assert.AreEqual(0, snapshot.OptionalImageReferenceCount);
                Assert.IsTrue(snapshot.HasUnresolvedAudio);
                Assert.IsTrue(snapshot.HasUnresolvedVisual);
                Assert.IsTrue(snapshot.HasUnresolvedMovie);
                CollectionAssert.AreEquivalent(new[] { Path.Combine("sound", "kick"), "bg.final", "movie" }, snapshot.EnumerateAllRelativePaths().ToArray());
                CollectionAssert.AreEquivalent(new[] { ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick")), ChartResourceKeyHash.GetLookupHash("bg.final"), ChartResourceKeyHash.GetLookupHash("movie") }, snapshot.EnumerateAllRelativePathHashes().ToArray());
                Assert.AreEqual(0, snapshot.UnsupportedResourceReferenceCount);
                Assert.IsFalse(snapshot.HasUnsupportedParentTraversalReference);
            }
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 保証：実BMSの音声・動画の親参照は種別付きで非対応集合へ保持し、通常の照合キーや充足件数へ混入しない。
    /// </summary>
    /// <remarks>本番解析で原文を取得し、集約側の分類との接続を確認する。</remarks>
    [TestMethod]
    public void CreateAggregate_PreservesUnsupportedParentTraversalReferencesWithoutLookupKeys()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "ChartResourceSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string chartPath = Path.Combine(tempDirectory, "parent-resource.bms");
        try
        {
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                + "#TITLE Parent Resource\r\n"
                + "#WAVAA ..\\Base\\sound.wav\r\n"
                + "#BMPAA ..\\Base\\movie.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:AA\r\n");
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile chart = (file);

            var snapshot = ChartResourceSnapshot.CreateAggregate([chart]);

            Assert.AreEqual(0, snapshot.AudioReferenceCount);
            Assert.AreEqual(0, snapshot.MovieReferenceCount);
            Assert.AreEqual(2, snapshot.UnsupportedResourceReferenceCount);
            Assert.IsTrue(snapshot.HasUnsupportedParentTraversalReference);
            Assert.AreEqual(1, snapshot.UnsupportedResourceReferences.Count(reference => reference.Kind == ChartResourceKind.Audio));
            Assert.AreEqual(1, snapshot.UnsupportedResourceReferences.Count(reference => reference.Kind == ChartResourceKind.Movie));
            CollectionAssert.DoesNotContain(snapshot.AudioRelativePaths.ToArray(), Path.Combine("Base", "sound"));
            CollectionAssert.DoesNotContain(snapshot.MovieRelativePaths.ToArray(), Path.Combine("Base", "movie"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 保証：実BMSから取得したリソース原文を単体・集約で保持し、検索キーだけを拡張子なしへ正規化して原文を上書きしない。
    /// </summary>
    /// <remarks>本番の取得と解析を通し、表記・階層・種別の受渡しを両snapshotで観測する。</remarks>
    [TestMethod]
    public void Create_PreservesAcquiredBmsRawResourcePaths()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "ChartResourceSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string chartPath = Path.Combine(tempDirectory, "raw-resource.bms");
        try
        {
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                + "#TITLE Raw Resource\r\n"
                + "#WAVAA .\\sound\\kick.wav\r\n"
                + "#BMPAA visual\\bg.final.png\r\n#00104:AA\r\n"
                + "#BMPAB movie\\op.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:AB\r\n");
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile chart = (file);

            var snapshot = ChartResourceSnapshot.Create(chart);
            var aggregate = ChartResourceSnapshot.CreateAggregate([chart]);

            Assert.AreEqual(1, snapshot.AudioReferenceCount);
            Assert.AreEqual(1, snapshot.VisualReferenceCount);
            Assert.AreEqual(1, snapshot.MovieReferenceCount);
            Assert.AreEqual(@".\sound\kick.wav", snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Audio).RawPath);
            Assert.AreEqual(Path.Combine("sound", "kick"), snapshot.AudioReferences.Single().LookupKey);
            Assert.AreEqual(ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick")), snapshot.AudioReferences.Single().RelativePathHash);
            CollectionAssert.Contains(snapshot.AudioRelativePathHashes.ToArray(), ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick")));
            CollectionAssert.DoesNotContain(snapshot.AudioRelativePathHashes.ToArray(), ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "kick.wav")));
            Assert.AreEqual(@"visual\bg.final.png", snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Image).RawPath);
            Assert.AreEqual(Path.Combine("visual", "bg.final"), snapshot.VisualReferences.Single().LookupKey);
            Assert.AreEqual(@"movie\op.mpg", snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Movie).RawPath);
            Assert.AreEqual(Path.Combine("movie", "op"), snapshot.MovieReferences.Single().LookupKey);
            Assert.AreEqual(snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Audio).RawPath, aggregate.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Audio).RawPath);
            Assert.AreEqual(snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Image).RawPath, aggregate.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Image).RawPath);
            Assert.AreEqual(snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Movie).RawPath, aggregate.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Movie).RawPath);
            Assert.AreEqual(3, snapshot.ResourceReferences.Count);
            CollectionAssert.AreEquivalent(
                snapshot.ResourceReferences.Select(reference => reference.RawPath).ToArray(),
                aggregate.ResourceReferences.Select(reference => reference.RawPath).ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 保証：リソースsnapshotは渡した譜面の共通値を正本とし、元の別値を探索して投影したリソースを上書きしない。
    /// </summary>
    [TestMethod]
    public void Create_ResourcesRemainAuthoritativeWhenStorageOwnerHasDifferentResources()
    {
        ChartFile file = CreateBmsFile("C:\\Pending\\owner.bms", ["owner.wav"], []);
        file = file with
        {
            Resources = [
            ChartResourceReference.Parse("owner.wav", ChartResourceKind.Audio)
        ]
        };
        var chart = new ChartFile(ChartFileKind.Bms, file.Path, file.Md5, file.Sha256, file.Title, file.RawTitle, file.Artist, file.Genre, "Pending", file.Tag, file.Level?.ToString(), null, file.Mode, null, resources: TestChartResources.Create(["projected.wav"]));

        var snapshot = ChartResourceSnapshot.Create(chart);

        CollectionAssert.Contains(snapshot.AudioRelativePaths.ToArray(), "projected");
        CollectionAssert.DoesNotContain(snapshot.AudioRelativePaths.ToArray(), "owner");
        Assert.AreEqual("projected.wav", snapshot.ResourceReferences.Single(reference => reference.Kind == ChartResourceKind.Audio).RawPath);
    }

    /// <summary>
    /// 保証：同じ照合キーを持つ異拡張子は件数を重複計上せず、互換判定に必要な全原文とその順序は保持する。
    /// </summary>
    [TestMethod]
    public void Create_DuplicateNormalizedResourceKeepsUniqueLookupAndAllRawReferences()
    {
        ChartFile file = CreateBmsFile("C:\\Pending\\duplicate.bms", ["sound.wav", "sound.ogg"], []);
        file = file with
        {
            Resources = [
            ChartResourceReference.Parse(@".\sound.wav", ChartResourceKind.Audio),
            ChartResourceReference.Parse("sound.ogg", ChartResourceKind.Audio)
        ]
        };
        var chart = new ChartFile(ChartFileKind.Bms, file.Path, file.Md5, file.Sha256, file.Title, file.RawTitle, file.Artist, file.Genre, "Pending", file.Tag, file.Level?.ToString(), null, file.Mode, null, resources: file.Resources);

        var snapshot = ChartResourceSnapshot.Create(chart);

        Assert.AreEqual(1, snapshot.AudioReferenceCount);
        Assert.AreEqual(@".\sound.wav", snapshot.ResourceReferences.First(reference => reference.Kind == ChartResourceKind.Audio).RawPath);
        CollectionAssert.Contains(snapshot.AudioRelativePaths.ToArray(), "sound");
        Assert.AreEqual(2, snapshot.ResourceReferences.Count);
        CollectionAssert.AreEqual(
            new[] { @".\sound.wav", "sound.ogg" },
            snapshot.ResourceReferences.Select(reference => reference.RawPath).ToArray());
    }

    /// <summary>
    /// 保証：導入推定に捕捉済みの定義リソースを明示した場合はその入力を使い、対象譜面から再捕捉して別内容へ変えない。
    /// </summary>
    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_UsesPrecomputedDefinedResources()
    {
        var targetEntry = PackageChartEntry.FromChart((CreateBmsFile("C:\\Pending\\target.bms", ["target.wav"], [])));
        var precomputedResources = ChartResourceSnapshot.CreateAggregate([
            (CreateBmsFile("C:\\Pending\\precomputed.bms", ["precomputed.wav"], []))
        ]);

        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(
            ChartPackage.FromChartEntries([targetEntry]),
            [targetEntry],
            PackageInstallSurfaceSnapshot.Empty,
            sourceSurfaceCacheHit: false,
            definedResources: precomputedResources);

        Assert.AreSame(precomputedResources, snapshot.DefinedResources);
        CollectionAssert.Contains(snapshot.DefinedResources.AudioRelativePaths.ToArray(), "precomputed");
        CollectionAssert.DoesNotContain(snapshot.DefinedResources.AudioRelativePaths.ToArray(), "target");
    }

    /// <summary>
    /// 保証：未取得リソースは単体・集約で失敗させ、取得済みの空集合だけを正常な零件として扱う。
    /// </summary>
    [TestMethod]
    public void Create_UnacquiredResourcesFailAndSuccessfulEmptyResourcesRemainEmpty()
    {
        ChartFile owner = (ChartTestValues.Empty() with { Path = @"C:\Missing\chart.bms" });
        ChartFile unacquired = (owner);
        Assert.IsNull(unacquired.Resources);
        Assert.ThrowsException<InvalidOperationException>(() => ChartResourceSnapshot.Create(unacquired));
        Assert.ThrowsException<InvalidOperationException>(() => ChartResourceSnapshot.CreateAggregate([unacquired]));
        owner = owner with { Resources = [] };
        ChartFile acquired = (owner);
        Assert.IsNotNull(acquired.Resources);
        Assert.AreEqual(0, ChartResourceSnapshot.Create(acquired).TotalReferenceCount);
        ChartFile omitted = owner with { Resources = null };
        Assert.ThrowsException<InvalidOperationException>(() => ChartResourceSnapshot.Create(omitted));
    }

    /// <summary>
    /// 保証：譜面削除後も取得済みreferenceをコピー・パッケージへ保持し、健全性とLR2互換性を原文・任意画像・親参照に基づいて判定できる。
    /// </summary>
    /// <remarks>実ファイルから原文を取得して削除した後、投影・パッケージ・保守・互換評価の接続を確認する。</remarks>
    [TestMethod]
    public void AcquiredResourcesRemainSharedAcrossOwnerlessCopiesAfterChartDeletion()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(ChartResourceSnapshotTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "chart.bms");
            File.WriteAllText(path, "#WAV01 .\\audio\\foo.v2.flac\n#00101:01\n#WAV02 audio/foo.v2.ogg\n#00101:02\n#BMP01 mystery.xyz\n#00104:01\n#BMP02 .png\n#00104:02\n#WAV03 ..\\base\\hit.wav\n#00101:03\n#BANNER .\\banner.jpeg\n#BACKBMP ..\\back.png\n#STAGEFILE stage.png\n");
            ChartFile owner = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
            ChartFile chart = (owner);
            ChartFile detached = ChartFileProjection.ToImmutableSnapshot(chart);
            File.Delete(path);
            owner = owner with { Resources = [] };
            owner = owner with { Banner = "different.png" };
            var entry = PackageChartEntry.FromChart(detached);
            ChartFile copy = ChartFileProjection.WithWarnings(entry.Chart, []);
            foreach (ChartFile result in new[] { detached, entry.Chart, copy })
            {
                Assert.IsNull(result.Token);
                Assert.AreSame(chart.Resources, result.Resources);
                Assert.AreEqual(8, result.Resources.Count);
                ChartResourceReference audio = result.Resources[0];
                Assert.AreEqual(@".\audio\foo.v2.flac", audio.RawPath);
                Assert.AreEqual(@"audio\foo.v2.wav", audio.NormalizedPath);
                Assert.AreEqual(@"audio\foo.v2", audio.LookupKey);
                Assert.AreEqual(ChartResourceUsage.Normal, audio.Usage);
                Assert.AreEqual(ChartResourcePathNormalizationStatus.Valid, audio.Status);
                Assert.AreEqual("banner.png", result.Resources.Single(reference => reference.Usage == ChartResourceUsage.Banner).NormalizedPath);
                Assert.AreEqual(".\\banner.jpeg", result.Resources.Single(reference => reference.Usage == ChartResourceUsage.Banner).RawPath);
                var index = ChartResourceSnapshot.Create(result);
                Assert.AreEqual(1, index.AudioReferenceCount);
                Assert.AreEqual(2, index.UnsupportedResourceReferenceCount);
                Assert.AreEqual(8, index.ResourceReferences.Count);
                CollectionAssert.AreEqual(chart.Resources.ToArray(), index.ResourceReferences.ToArray());
                CollectionAssert.AreEqual(chart.Resources.ToArray(), ChartResourceSnapshot.CreateAggregate([result]).ResourceReferences.ToArray());
                ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(result);
                Assert.IsNull(health.WavFilesDefined);
                Assert.IsTrue(health.IsInformationChecked);
                Assert.AreEqual(true, health.StagefileDefined);
                Assert.AreEqual(true, health.BannerDefined);
                Assert.AreEqual(true, health.BackbmpDefined);
                Assert.AreEqual(false, health.StagefileExisting);
                Assert.AreEqual(false, health.BannerExisting);
                Assert.AreEqual(false, health.BackbmpExisting);
                Lr2ResourceReferenceEvaluation compatibility = Lr2CompatibilityEvaluator.EvaluateResourceReferences(result.Path, index);
                Assert.IsTrue(compatibility.HasParentTraversal);
                Assert.AreEqual(@"audio\foo.v2.flac".Length, compatibility.MaxRelativeCp932Bytes);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 保証：BMS・bmsonの基本・一覧・一時状態・パッケージ・保存対象へのコピーは元の取得済み原文を保持し、未取得を空集合へ変換しない。
    /// </summary>
    [TestMethod]
    public void ChartCopiesPreserveSourceResourcesAndUnacquiredState()
    {
        ImmutableList<ChartResourceReference> resources =
        [
            ChartResourceReference.Parse(@".\audio\foo.v2.flac", ChartResourceKind.Audio),
            ChartResourceReference.Parse(@"..\base\sound.wav", ChartResourceKind.Audio),
            ChartResourceReference.Parse("mystery.xyz", ChartResourceKind.Unknown),
            ChartResourceReference.Parse(".wav", ChartResourceKind.Audio),
            ChartResourceReference.Parse(@".\banner.jpeg", ChartResourceKind.Image, ChartResourceUsage.Banner),
            ChartResourceReference.Parse("stage.png", ChartResourceKind.Image, ChartResourceUsage.Stagefile),
            ChartResourceReference.Parse(@"..\back.png", ChartResourceKind.Image, ChartResourceUsage.Backbmp),
            new(ChartResourceKind.Unknown, ChartResourceUsage.InputDiagnostic, string.Empty, string.Empty,
                string.Empty, ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported)
        ];
        ChartFile bmsOwner = (ChartTestValues.Empty() with { Path = @"C:\Missing\source.bms", Md5 = new string('a', 32), RawTitle = "before", Resources = resources });
        ChartFile bmsonOwner = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = @"C:\Missing\source.bmson", Md5 = new string('b', 32), RawTitle = "before", Resources = resources };
        ChartFile[] sources = [(bmsOwner), (bmsonOwner)];
        ChartFile[] unacquiredSources =
        [
            bmsOwner with { Resources = null },
            bmsonOwner with { Resources = null }
        ];
        foreach (ChartFile source in sources)
        {
            ChartFile[] copies =
            [
                ChartFileProjection.CaptureBasicSnapshot(source),
                ChartFileProjection.CaptureBasicSnapshotWithTransientState(source, ChartFileTransientState.Empty),
                ChartFileProjection.CaptureListSnapshot(source),
                PackageChartEntry.FromChart(source).Chart,
                ChartStorageTargetSet.FromCharts([source]).Charts.Single()
            ];
            foreach (ChartFile copy in copies)
            {
                Assert.AreEqual("before", copy.RawTitle);
                Assert.AreSame(resources, copy.Resources);
                CollectionAssert.AreEqual(resources.ToArray(), ChartResourceSnapshot.Create(copy).ResourceReferences.ToArray());
                Assert.AreEqual(@"audio\foo.v2", copy.Resources[0].LookupKey);
                Assert.AreEqual(3, ChartResourceSnapshot.Create(copy).UnsupportedResourceReferenceCount);
            }
        }
        foreach (ChartFile source in unacquiredSources)
        {
            foreach (ChartFile copy in new[]
            {
                ChartFileProjection.CaptureBasicSnapshot(source),
                ChartFileProjection.CaptureBasicSnapshotWithTransientState(source, ChartFileTransientState.Empty),
                ChartFileProjection.CaptureListSnapshot(source),
                PackageChartEntry.FromChart(source).Chart,
                ChartStorageTargetSet.FromCharts([source]).Charts.Single()
            })
            {
                Assert.IsNull(copy.Resources);
                Assert.ThrowsException<InvalidOperationException>(() => ChartResourceSnapshot.Create(copy));
            }
        }
    }

    private static ChartFile CreateBmsFile(string path, IEnumerable<string> wavFiles, IEnumerable<string> bgaFiles)
    {
        return ChartTestValues.Empty() with
        {
            Path = path,
            Resources = TestChartResources.Create(wavFiles, bgaFiles)
        };
    }
}
