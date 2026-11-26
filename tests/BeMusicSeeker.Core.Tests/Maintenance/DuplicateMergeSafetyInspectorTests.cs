using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.DuplicateMergeSafetyTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 統合の安全性確認の検査器を、テスト用の作業領域の実ファイルと実ディレクトリで確認します。所持譜面の情報だけをメモリ上の代替にし、
/// 段階・理由・該当するパス・比較方法を結果から読みます。KEEPに依存しない事実は一グループの検査の入口から、
/// KEEPに依存する事実は <see cref="DuplicateMergeGroupInspection.Evaluate"/> から確認します。
/// 境界サイズと区間長は仕様値で指定し、「読まない経路」は排他ロックしたファイルを置いて、読めば解析不能が出ることで区別します。
/// </summary>
[TestClass]
public sealed class DuplicateMergeSafetyInspectorTests
{
    private TestWorkspace? ownedWorkspace;

    private TestWorkspace Workspace => ownedWorkspace ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    private string Folder(string relative)
    {
        string path = Workspace.GetPath(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static readonly DateTime FixedTime = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static DuplicateMergeFinding Finding(string folder, string relative, string? detail = null) => new(folder, relative, detail);

    /// <summary>
    /// 保証：一つのグループに親子関係・型の衝突・別内容のファイルが同時にあると、段階は統合不可で、理由は契約の列挙順（親子関係、型の衝突、別内容ファイル衝突）に並び、
    /// 各理由が該当するフォルダと相対パスを持つ。大文字小文字だけが違うディレクトリ（Sub）とファイル（sub）も型の衝突になる。
    /// 検出する誤り：最初の理由で打ち切る集約、理由の並びの崩れ、型の衝突の大文字小文字の区別、該当パスの欠落。
    /// </summary>
    [TestMethod]
    public void Stages_AggregateSimultaneousReasonsAndOrderThemByContract()
    {
        string p = Folder("g/p"), nested = Folder("g/p/r"), q = Folder("g/q");
        WriteFile(Path.Combine(p, "sub"), [1, 2, 3]);
        WriteFile(Path.Combine(q, "Sub", "k.bin"), [4]);
        WriteFile(Path.Combine(p, "data.bin"), PatternBytes(100, 1));
        WriteFile(Path.Combine(q, "data.bin"), PatternBytes(100, 2));
        WriteFile(Path.Combine(p, "same.bin"), PatternBytes(50, 3));
        WriteFile(Path.Combine(q, "same.bin"), PatternBytes(50, 3));

        DuplicateMergeVerificationResult result = Inspect([p, nested, q]).Evaluate(p);

        Assert.AreEqual(DuplicateMergeVerificationStage.Unmergeable, result.Stage);
        CollectionAssert.AreEqual(
            new[] { DuplicateMergeVerificationReasonKind.ParentChild, DuplicateMergeVerificationReasonKind.TypeConflict, DuplicateMergeVerificationReasonKind.ContentConflict },
            Kinds(result));
        CollectionAssert.AreEqual(new[] { Finding(p, string.Empty, nested) }, Reason(result, DuplicateMergeVerificationReasonKind.ParentChild)!.Findings.ToArray());
        CollectionAssert.AreEqual(new[] { Finding(p, "sub"), Finding(q, "Sub") }, Reason(result, DuplicateMergeVerificationReasonKind.TypeConflict)!.Findings.ToArray());
        CollectionAssert.AreEqual(new[] { Finding(p, "data.bin"), Finding(q, "data.bin") }, Reason(result, DuplicateMergeVerificationReasonKind.ContentConflict)!.Findings.ToArray());
    }

    /// <summary>
    /// 保証：グループ内のフォルダ同士が同一または親子なら統合不可の理由になり、名前の先頭が同じだけの兄弟（A と A2）は該当しない。末尾の区切りだけが違う指定は同一として扱う。
    /// 検出する誤り：接頭辞だけの一致を親子とみなす、末尾の区切りの違いを別のフォルダとみなす、直接の子だけを親子とみなす。
    /// </summary>
    [TestMethod]
    [DataRow("\\child", true)]
    [DataRow("\\child\\deep", true)]
    [DataRow("2", false)]
    [DataRow("\\", true)]
    public void ParentChild_MatchesSameOrAncestryButNotNamePrefix(string suffix, bool expected)
    {
        string a = Folder("g/a");
        string other = a + suffix;
        Directory.CreateDirectory(other);

        DuplicateMergeVerificationResult result = Inspect([a, other]).Evaluate(a);

        Assert.AreEqual(expected, Reason(result, DuplicateMergeVerificationReasonKind.ParentChild) != null);
        if (!expected) { Assert.AreEqual(DuplicateMergeVerificationStage.Safe, result.Stage, "空のフォルダ二つで、他の理由はありません。"); }
    }

    /// <summary>
    /// 保証：現在の重複結果の別のグループのフォルダと同一または親子（祖先側・子孫側のどちらも）なら統合不可で、相手が一フォルダのグループでも該当する。
    /// 名前の先頭が同じだけの兄弟は該当しない。一フォルダのグループ自身は、この理由を持たない。
    /// 検出する誤り：祖先側だけの判定、相手が一フォルダのグループのときの見落とし、接頭辞だけの一致、一フォルダのグループへの理由の付与。
    /// </summary>
    [TestMethod]
    [DataRow("ancestor", 1, true)]
    [DataRow("ancestor", 2, true)]
    [DataRow("descendant", 1, true)]
    [DataRow("descendant", 2, true)]
    [DataRow("equal", 1, true)]
    [DataRow("prefixSibling", 1, false)]
    public void OtherGroup_ReportsAncestorAndDescendantOfAnyOtherGroup(string relation, int otherSize, bool expected)
    {
        string t1 = Folder("g/t1"), t2 = Folder("g/t2");
        string related = relation switch
        {
            "ancestor" => Path.GetDirectoryName(t1)!,
            "descendant" => t1 + @"\inner",
            "equal" => t1.ToUpperInvariant(),
            _ => t1 + "2"
        };
        string[] otherFolders = otherSize == 1 ? [related] : [related, Workspace.GetPath("g/elsewhere")];

        DuplicateMergeVerificationResult result = Inspect([t1, t2], otherGroups: [otherFolders]).Evaluate(t1);

        DuplicateMergeVerificationReason? reason = Reason(result, DuplicateMergeVerificationReasonKind.OtherGroup);
        Assert.AreEqual(expected, reason != null);
        if (expected) { CollectionAssert.Contains(reason!.Findings.ToArray(), Finding(t1, string.Empty, related)); }
        Assert.AreEqual(expected ? DuplicateMergeVerificationStage.Unmergeable : DuplicateMergeVerificationStage.Safe, result.Stage);

        // 一フォルダのグループ自身は、相手の関係にかかわらずこの理由を持たない。
        DuplicateMergeVerificationResult single = Inspect([t1], otherGroups: [otherFolders]).Evaluate(t1);
        Assert.IsNull(Reason(single, DuplicateMergeVerificationReasonKind.OtherGroup));
        Assert.AreEqual(DuplicateMergeVerificationStage.Safe, single.Stage);
    }

    /// <summary>
    /// 保証：登録範囲のフォルダが二つ以上あれば統合不可の理由になり、KEEPを変えても保つ。一つだけなら理由にならない。
    /// 検出する誤り：登録ルートの理由を確認の結果で上書きして消す、KEEPの変更で消える、一つの登録でも統合不可にする。
    /// </summary>
    [TestMethod]
    public void RegisteredRoot_IsKeptAfterVerificationAndForAnyKeep()
    {
        string a = Folder("g/a"), b = Folder("g/b");

        DuplicateMergeGroupInspection twoRegistered = Inspect([a, b], registeredFolders: [a, b]);
        foreach (string keep in new[] { a, b })
        {
            DuplicateMergeVerificationResult result = twoRegistered.Evaluate(keep);
            Assert.AreEqual(DuplicateMergeVerificationStage.Unmergeable, result.Stage);
            CollectionAssert.AreEqual(new[] { Finding(a, string.Empty), Finding(b, string.Empty) },
                Reason(result, DuplicateMergeVerificationReasonKind.RegisteredRoot)!.Findings.ToArray());
        }
        Assert.IsNull(Reason(Inspect([a, b], registeredFolders: [a]).Evaluate(a), DuplicateMergeVerificationReasonKind.RegisteredRoot));
    }

    /// <summary>
    /// 保証：配下のディレクトリの列挙に失敗すると（共有違反・削除保留のどちらも）、解析不能で統合不可になり、失敗した位置の相対パスを持つ。成功へ読み替えない。
    /// 検出する誤り：列挙の失敗の黙殺（安全への読替え）、失敗位置の欠落、失敗した一つのディレクトリで確認全体が例外になる。
    /// </summary>
    [TestMethod]
    [DataRow("sharing-violation")]
    [DataRow("delete-pending")]
    public void Unreadable_EnumerationFailureOfDescendantDirectory(string shape)
    {
        string a = Folder("g/a"), b = Folder("g/b");
        string blocked = Folder("g/a/blocked");
        using (shape == "sharing-violation" ? DirectoryEnumerationBlocker.HoldSharingViolation(blocked) : DirectoryEnumerationBlocker.HoldDeletePending(blocked))
        {
            DuplicateMergeVerificationResult result = Inspect([a, b]).Evaluate(a);

            Assert.AreEqual(DuplicateMergeVerificationStage.Unmergeable, result.Stage);
            CollectionAssert.AreEqual(new[] { Finding(a, "blocked") }, Reason(result, DuplicateMergeVerificationReasonKind.Unreadable)!.Findings.ToArray());
        }
    }

    /// <summary>
    /// 保証：比較が必要なファイル（相手と同じサイズ）の読取りに失敗すると解析不能で統合不可になり、該当するパスを持つ。別内容や同一内容とは判定しない。
    /// 読めない組の二フォルダは連結しているものとして扱い、その組だけでは「同一内容ファイルなし」を推測しない。
    /// 一方、その組と資源を共有しない第三のフォルダがあれば、解析不能に加えて「同一内容ファイルなし」も理由になり、資源を持つ全フォルダを指す。
    /// グループのフォルダが存在しない場合も解析不能で、フォルダ自身を指す空の相対パスを持つ。
    /// 検出する誤り：読めないファイルの別内容・同一内容への読替え、読取りの失敗の黙殺、読めない組を連結不足とみなす誤り、
    /// 読めない組が一つでもあると他のフォルダの連結不足まで消す誤り、存在しないフォルダの安全扱い。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Unreadable_ContentReadFailureAndMissingFolder(bool isolatedThirdFolder)
    {
        string a = Folder("g/a"), b = Folder("g/b");
        string locked = WriteFile(Path.Combine(a, "x.bin"), PatternBytes(64, 1));
        WriteFile(Path.Combine(b, "x.bin"), PatternBytes(64, 2));
        string[] folders = [a, b];
        if (isolatedThirdFolder)
        {
            // A・B と同じ相対パスの資源を持たない第三のフォルダ。読めない組（A–B）とは連結しない。
            string c = Folder("g/c");
            WriteFile(Path.Combine(c, "c.bin"), PatternBytes(64, 3));
            folders = [a, b, c];
        }
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            DuplicateMergeVerificationResult result = Inspect(folders).Evaluate(a);

            Assert.AreEqual(DuplicateMergeVerificationStage.Unmergeable, result.Stage);
            if (isolatedThirdFolder)
            {
                CollectionAssert.AreEqual(
                    new[] { DuplicateMergeVerificationReasonKind.Unreadable, DuplicateMergeVerificationReasonKind.NoSharedContent }, Kinds(result),
                    "読めない組の外のフォルダの連結不足は、読めない組があっても理由に残します。");
                CollectionAssert.AreEqual(folders.Select(folder => Finding(folder, string.Empty)).ToArray(),
                    Reason(result, DuplicateMergeVerificationReasonKind.NoSharedContent)!.Findings.ToArray());
            }
            else
            {
                CollectionAssert.AreEqual(new[] { DuplicateMergeVerificationReasonKind.Unreadable }, Kinds(result),
                    "読めない組は、別内容（ContentConflict）にも同一内容にも判定せず、その組の連結の不足も推測しません。");
            }
            CollectionAssert.AreEqual(new[] { Finding(a, "x.bin") }, Reason(result, DuplicateMergeVerificationReasonKind.Unreadable)!.Findings.ToArray());
        }

        string missing = Workspace.GetPath("g/missing");
        DuplicateMergeVerificationResult missingResult = Inspect([a, missing]).Evaluate(a);
        CollectionAssert.AreEqual(new[] { Finding(missing, string.Empty) }, Reason(missingResult, DuplicateMergeVerificationReasonKind.Unreadable)!.Findings.ToArray());
    }

    /// <summary>
    /// 保証：KEEP以外のフォルダの直下・配下の所持譜面が、使用中のリソースに親ディレクトリ参照を持てば要注意で、フォルダ・譜面の相対パス・参照の原文を持つ。
    /// KEEP側だけの参照、未使用の定義は該当しない。
    /// 検出する誤り：KEEP側の参照の誤検出、未使用の定義の誤検出、参照の原文の欠落、KEEP変更時の導き直しの漏れ。
    /// </summary>
    [TestMethod]
    public void ParentReference_AppliesOnlyToMovingFolderAndUsedReferences()
    {
        string a = Folder("g/a"), b = Folder("g/b");
        MemoryOwnedChartSource charts = new MemoryOwnedChartSource()
            .Add(OwnedChart(Path.Combine(a, "c.bms"), usedReferences: ["../x.wav"], unusedReferences: ["../unused.wav"]))
            .Add(OwnedChart(Path.Combine(b, "sub", "d.bms"), usedReferences: ["snd/z.wav"], unusedReferences: ["../only-unused.wav"]));
        DuplicateMergeGroupInspection inspection = Inspect([a, b], charts);

        DuplicateMergeVerificationResult keepB = inspection.Evaluate(b);
        Assert.AreEqual(DuplicateMergeVerificationStage.Caution, keepB.Stage);
        CollectionAssert.AreEqual(new[] { Finding(a, "c.bms", "../x.wav") },
            Reason(keepB, DuplicateMergeVerificationReasonKind.ParentReference)!.Findings.ToArray(), "KEEP以外のフォルダの使用中の親参照だけです。");

        DuplicateMergeVerificationResult keepA = inspection.Evaluate(a);
        Assert.IsNull(Reason(keepA, DuplicateMergeVerificationReasonKind.ParentReference),
            "KEEP側だけの参照と、移動元側の未使用の定義だけの参照は該当しません。");
    }

    /// <summary>
    /// 保証：KEEP以外のフォルダの配下の子孫フォルダに所持譜面があれば要注意で、子孫フォルダの相対パスを持つ。直下の所持譜面、KEEP側の子孫、
    /// 所持していない譜面ファイルだけの子孫は該当しない。
    /// 検出する誤り：直下の譜面の誤検出、KEEP側の子孫の誤検出、所持していない.bmsファイルの誤検出。
    /// </summary>
    [TestMethod]
    public void NestedChartFolder_RequiresOwnedChartsBelowMovingFolder()
    {
        string a = Folder("g/a"), b = Folder("g/b");
        WriteFile(Path.Combine(a, "inner", "unowned.bms"), [1]);
        MemoryOwnedChartSource charts = new MemoryOwnedChartSource()
            .Add(OwnedChart(Path.Combine(a, "direct.bms")))
            .Add(OwnedChart(Path.Combine(b, "sub", "owned.bms")));
        DuplicateMergeGroupInspection inspection = Inspect([a, b], charts);

        DuplicateMergeVerificationResult keepA = inspection.Evaluate(a);
        CollectionAssert.AreEqual(new[] { Finding(b, "sub") }, Reason(keepA, DuplicateMergeVerificationReasonKind.NestedChartFolder)!.Findings.ToArray());

        DuplicateMergeVerificationResult keepB = inspection.Evaluate(b);
        Assert.IsNull(Reason(keepB, DuplicateMergeVerificationReasonKind.NestedChartFolder),
            "直下の所持譜面と、所持していない .bms だけの子孫、KEEP側の子孫は該当しません。");
    }

    /// <summary>
    /// 保証：同じ相対位置の同名音源を持つフォルダ間で拡張子の集合が異なると、その名前の全音源が要注意になる。同等性検査がオフ（既定）でも、
    /// そのうちグループ内の拡張子が .wav と .ogg のちょうど二種類の名前を、WAV・OGGの衝突名として確認結果に保つ。
    /// 検出する誤り：同じ集合や保持するフォルダが一つだけの誤検出、真部分集合の見落とし、別ディレクトリ・対象外形式・大文字小文字の扱いの誤り、
    /// 検査オフでの衝突名の欠落、.mp3・.flac を含む名前の衝突名への混入。
    /// </summary>
    [TestMethod]
    [DataRow(@"bgm\x.wav", @"bgm\X.ogg", true, @"bgm\x")]
    [DataRow(@"bgm\x.wav|bgm\x.flac", @"bgm\x.wav", true, "")]
    [DataRow(@"bgm\x.wav|bgm\x.ogg", @"BGM\X.OGG|BGM\X.WAV", false, "")]
    [DataRow(@"bgm\x.wav|bgm\x.ogg", @"bgm\other.wav", false, "")]
    [DataRow(@"bgm\x.wav", @"sfx\x.ogg", false, "")]
    [DataRow(@"bgm\x.bmp", @"bgm\x.png", false, "")]
    [DataRow(@"bgm\x.wav", @"bgm\X.WAV", false, "")]
    [DataRow(@"bgm\x.wav", @"bgm\x.wav|bgm\x.ogg", true, @"bgm\x")]
    [DataRow(@"bgm\x.wav", @"bgm\x.ogg|bgm\x.mp3", true, "")]
    public void AudioExtension_WarnsWhenMergingAddsAFormat(string aFiles, string bFiles, bool expected, string collisionNames)
    {
        string a = Folder("g/a"), b = Folder("g/b");
        DuplicateMergeFinding[] files = aFiles.Split('|').Select(path => Finding(a, path))
            .Concat(bFiles.Split('|').Select(path => Finding(b, path))).ToArray();
        WriteFile(Path.Combine(a, "common.bin"), [1]);
        WriteFile(Path.Combine(b, "common.bin"), [1]);
        foreach (DuplicateMergeFinding file in files) { WriteFile(Path.Combine(file.Folder, file.RelativePath), [2]); }

        DuplicateMergeVerificationResult result = Inspect([a, b]).Evaluate(a);

        Assert.AreEqual(expected ? DuplicateMergeVerificationStage.Caution : DuplicateMergeVerificationStage.Safe, result.Stage);
        if (expected)
        {
            CollectionAssert.AreEquivalent(files, Reason(result, DuplicateMergeVerificationReasonKind.AudioExtension)!.Findings.ToArray());
        }
        string[] expectedNames = collisionNames.Length == 0 ? [] : collisionNames.Split('|');
        CollectionAssert.AreEquivalent(
            expectedNames.Select(name => name.ToUpperInvariant()).ToArray(),
            result.WavOggCollisionNames!.Select(name => name.ToUpperInvariant()).ToArray(),
            "WAV・OGGの衝突名は、拡張子がちょうど .wav と .ogg の名前だけです（大文字小文字は問いません）。");
    }

    /// <summary>
    /// 保証：同等性検査がオフの確認は、WAVとOGGを復号せず（排他ロックして読めなくしても解析不能が出ない）、従来どおり拡張子違いの理由が残り、
    /// 同等と判定した名前の一覧は空、「全衝突が同等」は偽である。WAV・OGGの衝突名は、KEEPによらず保つ。
    /// 検出する誤り：オフでの復号、オフの結果への同等性の情報の混入、理由の欠落、オフやKEEPの変更での衝突名の欠落。
    /// </summary>
    [TestMethod]
    public void AudioEquivalence_WhenDisabled_DoesNotDecodeAndKeepsTheReason()
    {
        string a = Folder("g/a"), b = Folder("g/b");
        WriteFile(Path.Combine(a, "common.bin"), [1]);
        WriteFile(Path.Combine(b, "common.bin"), [1]);
        string wav = WriteFile(Path.Combine(a, "x.wav"), [2]);
        string ogg = WriteFile(Path.Combine(b, "x.ogg"), [3]);
        using var holdWav = new FileStream(wav, FileMode.Open, FileAccess.Read, FileShare.None);
        using var holdOgg = new FileStream(ogg, FileMode.Open, FileAccess.Read, FileShare.None);

        DuplicateMergeVerificationResult result = Inspect([a, b], audioEquivalence: false).Evaluate(a);

        Assert.AreEqual(DuplicateMergeVerificationStage.Caution, result.Stage);
        CollectionAssert.AreEqual(new[] { DuplicateMergeVerificationReasonKind.AudioExtension }, Kinds(result));
        CollectionAssert.AreEqual(new[] { Finding(a, "x.wav"), Finding(b, "x.ogg") }, Reason(result, DuplicateMergeVerificationReasonKind.AudioExtension)!.Findings.ToArray());
        Assert.AreEqual(0, result.EquivalentAudioNames!.Count);
        Assert.IsFalse(result.AllAudioCollisionsEquivalent);
        foreach (string keep in new[] { a, b })
        {
            DuplicateMergeVerificationResult evaluated = keep == a ? result : Inspect([a, b], audioEquivalence: false).Evaluate(b);
            CollectionAssert.AreEqual(new[] { "x" }, evaluated.WavOggCollisionNames!.ToArray(), "KEEP=" + keep);
            Assert.AreEqual(0, evaluated.EquivalentAudioNames!.Count, "KEEP=" + keep);
        }
    }

    /// <summary>
    /// 保証：三フォルダの一部で形式が増える名前だけを、KEEPによらず全パスとともに報告する。同じパスの別内容は独立した理由になる。
    /// 検出する誤り：先頭の二フォルダだけの比較、形式数だけの比較、同じ形式を持つ側の報告漏れ、内容衝突と拡張子違いの混同。
    /// </summary>
    [TestMethod]
    public void AudioExtension_ReportsAllVariantsOfAffectedNamesIndependentlyOfContentAndKeep()
    {
        string a = Folder("g/a"), b = Folder("g/b"), c = Folder("g/c");
        string[] folders = [a, b, c];
        foreach (string folder in folders)
        {
            WriteFile(Path.Combine(folder, "common.bin"), [1]);
            WriteFile(Path.Combine(folder, "kick.wav"), folder == c ? [2] : [1]);
        }
        WriteFile(Path.Combine(a, "kick.ogg"), [1]);
        WriteFile(Path.Combine(b, "kick.ogg"), [1]);
        WriteFile(Path.Combine(c, "kick.mp3"), [1]);
        // この音源は A と B にだけあり、形式は同じだが WAV の内容が異なる。
        WriteFile(Path.Combine(a, "bass.wav"), [1]);
        WriteFile(Path.Combine(b, "bass.wav"), [2]);
        WriteFile(Path.Combine(a, "bass.ogg"), [1]);
        WriteFile(Path.Combine(b, "bass.ogg"), [1]);

        DuplicateMergeGroupInspection inspection = Inspect(folders);
        foreach (string keep in folders)
        {
            DuplicateMergeVerificationResult result = inspection.Evaluate(keep);
            CollectionAssert.AreEqual(new[] { DuplicateMergeVerificationReasonKind.AudioExtension, DuplicateMergeVerificationReasonKind.ContentConflict }, Kinds(result));
            CollectionAssert.AreEqual(new[] { Finding(a, "kick.ogg"), Finding(a, "kick.wav"), Finding(b, "kick.ogg"), Finding(b, "kick.wav"),
                Finding(c, "kick.mp3"), Finding(c, "kick.wav") }, Reason(result, DuplicateMergeVerificationReasonKind.AudioExtension)!.Findings.ToArray());
            CollectionAssert.AreEqual(new[] { Finding(a, "bass.wav"), Finding(a, "kick.wav"), Finding(b, "bass.wav"), Finding(b, "kick.wav"),
                Finding(c, "kick.wav") }, Reason(result, DuplicateMergeVerificationReasonKind.ContentConflict)!.Findings.ToArray());
        }
    }

    /// <summary>
    /// 保証：別内容ファイル衝突の理由は資源だけが対象で、譜面ファイルと保護対象（拡張子の大文字小文字を問わない）は理由にならず、隠し属性のディレクトリの中の資源は対象になる。
    /// 同じ相対パスの別内容の保護対象は、内容を比べたうえで、段階を変えずに付記として相対パスを一覧する。
    /// 検出する誤り：譜面・保護対象を別内容ファイル衝突の理由にする、隠しディレクトリの見落とし、保護対象の付記による段階の変更・付記の欠落、拡張子の大文字小文字の区別。
    /// </summary>
    [TestMethod]
    public void ContentConflict_ExcludesChartsAndProtectedAndIncludesHiddenDirectories()
    {
        string a = Folder("g/a"), b = Folder("g/b");
        WriteFile(Path.Combine(a, "ok.bin"), PatternBytes(32, 1));
        WriteFile(Path.Combine(b, "ok.bin"), PatternBytes(32, 1));
        WriteFile(Path.Combine(a, "Song.BMS"), PatternBytes(40, 1));
        WriteFile(Path.Combine(b, "song.bms"), PatternBytes(40, 2));
        WriteFile(Path.Combine(a, "readme.txt"), PatternBytes(40, 3));
        WriteFile(Path.Combine(b, "readme.txt"), PatternBytes(40, 4));
        WriteFile(Path.Combine(a, "Data.BMX"), PatternBytes(40, 5));
        WriteFile(Path.Combine(b, "data.bmx"), PatternBytes(40, 6));
        WriteFile(Path.Combine(a, "hid", "x.bin"), PatternBytes(40, 7));
        WriteFile(Path.Combine(b, "hid", "x.bin"), PatternBytes(40, 8));
        foreach (string hidden in new[] { Path.Combine(a, "hid"), Path.Combine(b, "hid") })
        {
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        }

        DuplicateMergeVerificationResult result = Inspect([a, b]).Evaluate(a);

        Assert.AreEqual(DuplicateMergeVerificationStage.Caution, result.Stage);
        CollectionAssert.AreEqual(new[] { DuplicateMergeVerificationReasonKind.ContentConflict }, Kinds(result));
        CollectionAssert.AreEqual(new[] { Finding(a, @"hid\x.bin"), Finding(b, @"hid\x.bin") },
            Reason(result, DuplicateMergeVerificationReasonKind.ContentConflict)!.Findings.ToArray(), "隠しディレクトリの中の資源だけです。");
        CollectionAssert.AreEqual(new[] { "Data.BMX", "readme.txt" }, result.ProtectedNameCollisions!.ToArray(), "別内容の保護対象は相対パスで一覧します。");
    }

    /// <summary>
    /// 保証：同じ相対パスの保護対象（.bmx・.pmx・.txt。大文字小文字を無視）は資源と同じ手順で比べ、別内容と判定された組が一つでもある相対パスだけを、段階を変えずに付記する。
    /// 全組が同一内容なら付記しない。サイズが違えば読まずに別内容、簡易チェックではサイズと更新日時が同じなら読まずに同一内容とする。
    /// 読めない組は解析不能の理由になり、付記の判定には使わないが、同じ相対パスの読める組が別内容なら付記する。
    /// 別内容の組が見つかった後も全ての組を比べるため、後のフォルダの読めないファイルも解析不能の理由になる。保護対象の比較は、資源の組の数に数えない。
    /// 検出する誤り：名前の一致だけの付記、同サイズ・同更新日時を読まずに同一とする（簡易チェックなし）、簡易チェックの値が保護対象へ届かない、サイズ違いの読取り、
    /// 一つの別内容で全部の名前を出す、全組が別内容のときだけ付記する、読めない組を別内容として付記する、一組の失敗で付記を止める、
    /// 別内容の組が見つかった後の読取り失敗の見落とし、保護対象の組を数える。
    /// </summary>
    /// <param name="unreadableFolder">解析不能の該当になるフォルダ（"a" または "c"）。空なら解析不能にならない。</param>
    [TestMethod]
    [DataRow("same-content", false, "", "")]
    [DataRow("same-metadata-different-content", false, "r.txt", "")]
    [DataRow("same-metadata-different-content", true, "", "")]
    [DataRow("size-differs-locked", false, "Y.PMX", "")]
    [DataRow("one-of-two-names-differs", false, "diff.bmx", "")]
    [DataRow("three-folders-one-differs", false, "r.txt", "")]
    [DataRow("unreadable-pair", false, "", "a")]
    [DataRow("unreadable-and-different-pairs", false, "r.txt", "a")]
    [DataRow("different-then-unreadable", false, "r.txt", "c")]
    public void ProtectedFiles_AnnotateOnlyPathsWithDifferentContentAndStayOutOfResourceCounts(
        string scenario, bool quick, string expectedNotes, string unreadableFolder)
    {
        string a = Folder("g/a"), b = Folder("g/b"), c = Folder("g/c");
        string[] folders = scenario is "three-folders-one-differs" or "unreadable-and-different-pairs" or "different-then-unreadable" ? [a, b, c] : [a, b];
        // 資源の連結を満たし、資源の組の数が全量の一組（三フォルダでは三組）に決まるよう、同一内容で更新日時の違う資源を置く。
        for (int index = 0; index < folders.Length; index++)
        {
            WriteFile(Path.Combine(folders[index], "shared.bin"), PatternBytes(32, 1), FixedTime.AddHours(index));
        }
        string? locked = null;
        switch (scenario)
        {
            case "same-content":
                WriteFile(Path.Combine(a, "r.txt"), PatternBytes(10, 1), FixedTime);
                WriteFile(Path.Combine(b, "r.txt"), PatternBytes(10, 1), FixedTime.AddHours(1));
                break;
            case "same-metadata-different-content":
                WriteFile(Path.Combine(a, "r.txt"), PatternBytes(10, 1), FixedTime);
                WriteFile(Path.Combine(b, "r.txt"), PatternBytes(10, 2), FixedTime);
                break;
            case "size-differs-locked":
                locked = WriteFile(Path.Combine(a, "Y.PMX"), PatternBytes(10, 1), FixedTime);
                WriteFile(Path.Combine(b, "y.pmx"), PatternBytes(11, 1), FixedTime);
                break;
            case "one-of-two-names-differs":
                WriteFile(Path.Combine(a, "same.txt"), PatternBytes(10, 1));
                WriteFile(Path.Combine(b, "same.txt"), PatternBytes(10, 1));
                WriteFile(Path.Combine(a, "diff.bmx"), PatternBytes(10, 1));
                WriteFile(Path.Combine(b, "diff.bmx"), PatternBytes(10, 2));
                break;
            case "three-folders-one-differs":
                WriteFile(Path.Combine(a, "r.txt"), PatternBytes(10, 1));
                WriteFile(Path.Combine(b, "r.txt"), PatternBytes(10, 1));
                WriteFile(Path.Combine(c, "r.txt"), PatternBytes(10, 2));
                break;
            case "unreadable-pair":
                locked = WriteFile(Path.Combine(a, "r.txt"), PatternBytes(10, 1));
                WriteFile(Path.Combine(b, "r.txt"), PatternBytes(10, 2));
                break;
            case "unreadable-and-different-pairs":
                locked = WriteFile(Path.Combine(a, "r.txt"), PatternBytes(10, 1));
                WriteFile(Path.Combine(b, "r.txt"), PatternBytes(10, 2));
                WriteFile(Path.Combine(c, "r.txt"), PatternBytes(10, 3));
                break;
            default:
                // 先頭の組 (a,b) が別内容で、最後尾の c だけが読めない。サイズを揃え、c を読まないと失敗が分からないようにする。
                WriteFile(Path.Combine(a, "r.txt"), PatternBytes(10, 1));
                WriteFile(Path.Combine(b, "r.txt"), PatternBytes(10, 2));
                locked = WriteFile(Path.Combine(c, "r.txt"), PatternBytes(10, 3));
                break;
        }

        DuplicateMergeVerificationResult result;
        using (locked == null ? null : new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Inspect(folders, sizeAndTimeOnly: quick).Evaluate(a);
        }

        string label = scenario + (quick ? " (quick)" : string.Empty) + ": " + string.Join(",", Kinds(result));
        CollectionAssert.AreEqual(
            expectedNotes.Length == 0 ? [] : new[] { expectedNotes }, (result.ProtectedNameCollisions ?? []).ToArray(), label);
        if (unreadableFolder.Length > 0)
        {
            Assert.AreEqual(DuplicateMergeVerificationStage.Unmergeable, result.Stage, label);
            CollectionAssert.AreEqual(new[] { DuplicateMergeVerificationReasonKind.Unreadable }, Kinds(result), label);
            CollectionAssert.AreEqual(
                new[] { Finding(unreadableFolder == "c" ? c : a, "r.txt") },
                Reason(result, DuplicateMergeVerificationReasonKind.Unreadable)!.Findings.ToArray(), label);
        }
        else
        {
            Assert.AreEqual(DuplicateMergeVerificationStage.Safe, result.Stage, label);
            Assert.IsEmpty(Kinds(result), label);
            int pairs = folders.Length == 3 ? 3 : 1;
            Assert.AreEqual((0, pairs, 0), (result.Comparisons!.SizeAndTime, result.Comparisons.Full, result.Comparisons.Partial),
                label + " 保護対象の比較は、資源の組の数に数えません。");
        }
    }

    /// <summary>
    /// 保証：資源を持つフォルダが「同じ相対パスで同一内容の資源を共有する」関係で一つに連結していれば理由がない（全フォルダに共通する一ファイルは要求しない）。
    /// 連結しない資源を持つフォルダがあれば要注意になり、資源を持たないフォルダは外し、保護対象だけの一致はつながりに数えない。
    /// 検出する誤り：全フォルダ共通のファイルの要求（連鎖の誤検出）、連結の見落とし、資源を持たないフォルダの数え入れ、保護対象の一致のつながりへの算入。
    /// </summary>
    [TestMethod]
    [DataRow("chain", false)]
    [DataRow("one-isolated", true)]
    [DataRow("resourceless-folder-ignored", false)]
    [DataRow("protected-only-match", true)]
    public void SharedContent_RequiresResourceHoldingFoldersToBeConnected(string scenario, bool expectedReason)
    {
        string a = Folder("g/a"), b = Folder("g/b"), c = Folder("g/c");
        switch (scenario)
        {
            case "chain":
                WriteFile(Path.Combine(a, "ab.bin"), PatternBytes(20, 1));
                WriteFile(Path.Combine(b, "ab.bin"), PatternBytes(20, 1));
                WriteFile(Path.Combine(b, "bc.bin"), PatternBytes(20, 2));
                WriteFile(Path.Combine(c, "bc.bin"), PatternBytes(20, 2));
                break;
            case "one-isolated":
                WriteFile(Path.Combine(a, "ab.bin"), PatternBytes(20, 1));
                WriteFile(Path.Combine(b, "ab.bin"), PatternBytes(20, 1));
                WriteFile(Path.Combine(c, "c.bin"), PatternBytes(20, 3));
                break;
            case "resourceless-folder-ignored":
                WriteFile(Path.Combine(a, "ab.bin"), PatternBytes(20, 1));
                WriteFile(Path.Combine(b, "ab.bin"), PatternBytes(20, 1));
                WriteFile(Path.Combine(c, "only.bms"), PatternBytes(20, 4));
                break;
            default:
                WriteFile(Path.Combine(a, "a.bin"), PatternBytes(20, 1));
                WriteFile(Path.Combine(b, "b.bin"), PatternBytes(20, 2));
                WriteFile(Path.Combine(a, "r.txt"), PatternBytes(20, 5));
                WriteFile(Path.Combine(b, "r.txt"), PatternBytes(20, 5));
                break;
        }

        DuplicateMergeVerificationResult result = Inspect([a, b, c]).Evaluate(a);

        Assert.AreEqual(expectedReason, Reason(result, DuplicateMergeVerificationReasonKind.NoSharedContent) != null, scenario + ": " + string.Join(",", Kinds(result)));
        Assert.IsNull(Reason(result, DuplicateMergeVerificationReasonKind.ContentConflict));
    }

    /// <summary>
    /// 保証：一フォルダだけのグループは、フォルダ本体のリンクと解析不能だけを検査する。配下に何があっても（列挙できない配下のディレクトリを含む）安全で、
    /// フォルダ本体が存在しなければ解析不能になる。
    /// 検出する誤り：一フォルダのグループの配下の走査（読めない配下の解析不能、不要な負荷）、存在しない本体の安全扱い。
    /// </summary>
    [TestMethod]
    public void SingleFolderGroup_InspectsOnlyTheFolderBody()
    {
        string a = Folder("g/a");
        string blocked = Folder("g/a/blocked");
        WriteFile(Path.Combine(a, "dir", "x.bin"), [1]);
        MemoryOwnedChartSource charts = new MemoryOwnedChartSource().Add(OwnedChart(Path.Combine(a, "sub", "c.bms"), usedReferences: ["../x.wav"]));
        using (DirectoryEnumerationBlocker.HoldSharingViolation(blocked))
        {
            DuplicateMergeVerificationResult result = Inspect([a], charts).Evaluate(a);

            Assert.AreEqual(DuplicateMergeVerificationStage.Safe, result.Stage);
            Assert.AreEqual(0, result.Reasons.Count);
            Assert.AreEqual(0, charts.RequestedFolders.Count, "一フォルダのグループは、所持譜面も調べません。");
        }

        string missing = Workspace.GetPath("g/missing");
        DuplicateMergeVerificationResult missingResult = Inspect([missing]).Evaluate(missing);
        Assert.AreEqual(DuplicateMergeVerificationStage.Unmergeable, missingResult.Stage);
        CollectionAssert.AreEqual(new[] { Finding(missing, string.Empty) }, Reason(missingResult, DuplicateMergeVerificationReasonKind.Unreadable)!.Findings.ToArray());
    }

    /// <summary>
    /// 保証：KEEPを変えると、KEEPに依存する二つの理由（親ディレクトリ参照、配下の別の譜面フォルダ）だけが確認時の事実から導き直され、
    /// それ以外の理由は確認時の結果のままで、確認後にファイルを変えても再走査しない。
    /// 検出する誤り：KEEPの変更で全理由を再走査する、KEEPに依存する理由の導き直しの漏れ、KEEPに依存しない理由の消失・変化。
    /// </summary>
    [TestMethod]
    public void KeepChange_ReevaluatesOnlyKeepDependentReasonsWithoutRescanning()
    {
        string a = Folder("g/a"), b = Folder("g/b");
        WriteFile(Path.Combine(a, "x.bin"), PatternBytes(64, 1));
        WriteFile(Path.Combine(b, "x.bin"), PatternBytes(64, 2));
        MemoryOwnedChartSource charts = new MemoryOwnedChartSource()
            .Add(OwnedChart(Path.Combine(a, "c.bms"), usedReferences: ["../p.wav"]))
            .Add(OwnedChart(Path.Combine(b, "nested", "d.bms")));
        DuplicateMergeGroupInspection inspection = Inspect([a, b], charts);
        DuplicateMergeVerificationResult keepA = inspection.Evaluate(a);
        Assert.AreEqual(2, charts.RequestedFolders.Count);

        // 確認後に、新しい衝突と読めないファイルを作る。再走査しなければ、結果へ現れない。
        WriteFile(Path.Combine(a, "late.bin"), PatternBytes(64, 3));
        WriteFile(Path.Combine(b, "late.bin"), PatternBytes(64, 4));
        DuplicateMergeVerificationResult keepB = inspection.Evaluate(b);

        CollectionAssert.AreEqual(
            new[] { DuplicateMergeVerificationReasonKind.ContentConflict, DuplicateMergeVerificationReasonKind.NoSharedContent, DuplicateMergeVerificationReasonKind.NestedChartFolder },
            Kinds(keepA));
        CollectionAssert.AreEqual(
            new[] { DuplicateMergeVerificationReasonKind.ParentReference, DuplicateMergeVerificationReasonKind.ContentConflict, DuplicateMergeVerificationReasonKind.NoSharedContent },
            Kinds(keepB), "KEEPに依存する二つの理由だけが入れ替わり、他は確認時のままです。");
        CollectionAssert.AreEqual(
            new[] { Finding(a, "x.bin"), Finding(b, "x.bin") },
            Reason(keepB, DuplicateMergeVerificationReasonKind.ContentConflict)!.Findings.ToArray(), "確認後に作ったファイルは結果に現れません。");
        Assert.AreEqual(2, charts.RequestedFolders.Count, "KEEPの変更で所持譜面を取り直しません。");
    }

    /// <summary>
    /// 保証：読まない経路を使う条件では、読めないファイル（排他ロック）があっても解析不能にならない。相手のいないファイル、サイズの違う同名ファイル（別内容）、
    /// 簡易チェックがオンでサイズと更新日時が完全に一致するファイル（同一内容、比較方法は「サイズと更新日時」）である。
    /// 検出する誤り：相手のいないファイルの読取り、サイズの違う組の読取り、簡易チェックでの読取り、簡易チェックの比較方法の誤記録。
    /// </summary>
    [TestMethod]
    [DataRow("no-partner")]
    [DataRow("different-size")]
    [DataRow("quick-same-metadata")]
    public void ReadAvoidance_DoesNotReadLockedFilesWhenDecidedWithoutContent(string scenario)
    {
        string a = Folder("g/a"), b = Folder("g/b");
        // 更新日時を明示し、簡易チェックでも共通のファイルは更新日時が違うため、内容を読んで比べる（比較方法の件数を決定的にする）。
        WriteFile(Path.Combine(a, "common.bin"), PatternBytes(16, 1), FixedTime);
        WriteFile(Path.Combine(b, "common.bin"), PatternBytes(16, 1), FixedTime.AddSeconds(7));
        string locked = scenario switch
        {
            "no-partner" => WriteFile(Path.Combine(a, "only.bin"), PatternBytes(40, 1)),
            "different-size" => WriteFile(Path.Combine(a, "x.bin"), PatternBytes(40, 1)),
            _ => WriteFile(Path.Combine(a, "x.bin"), PatternBytes(40, 1), FixedTime)
        };
        if (scenario == "different-size") { WriteFile(Path.Combine(b, "x.bin"), PatternBytes(41, 2)); }
        if (scenario == "quick-same-metadata") { WriteFile(Path.Combine(b, "x.bin"), PatternBytes(40, 2), FixedTime); }
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        DuplicateMergeVerificationResult result = Inspect([a, b], sizeAndTimeOnly: scenario == "quick-same-metadata").Evaluate(a);

        Assert.IsNull(Reason(result, DuplicateMergeVerificationReasonKind.Unreadable), scenario + ": " + string.Join(",", Kinds(result)));
        if (scenario == "different-size") { Assert.IsNotNull(Reason(result, DuplicateMergeVerificationReasonKind.ContentConflict)); }
        if (scenario == "quick-same-metadata")
        {
            Assert.AreEqual(DuplicateMergeVerificationStage.Safe, result.Stage);
            Assert.AreEqual((1, 1, 0), (result.Comparisons!.SizeAndTime, result.Comparisons.Full, result.Comparisons.Partial),
                "ロック中のファイルは「サイズと更新日時」、更新日時が違う共通のファイルは全量で比べます。");
        }
    }

    /// <summary>
    /// 保証：簡易チェックの境界。オンでも更新日時が1秒違えば内容を読んで判定し、オフならサイズと更新日時が一致しても内容を読む。読んだかどうかは、排他ロックしたファイルが解析不能になるかで区別する。
    /// 検出する誤り：更新日時の一致を粗い精度で比べる、簡易チェックのオフでの省略、更新日時の違いでの省略。
    /// </summary>
    [TestMethod]
    [DataRow(true, 1, true)]
    [DataRow(true, 0, false)]
    [DataRow(false, 0, true)]
    public void QuickCompare_ReadsUnlessSizeAndTimeMatchExactlyWhenEnabled(bool quick, int deltaSeconds, bool expectedRead)
    {
        string a = Folder("g/a"), b = Folder("g/b");
        string locked = WriteFile(Path.Combine(a, "x.bin"), PatternBytes(48, 1), FixedTime);
        WriteFile(Path.Combine(b, "x.bin"), PatternBytes(48, 1), FixedTime.AddSeconds(deltaSeconds));
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        DuplicateMergeVerificationResult result = Inspect([a, b], sizeAndTimeOnly: quick).Evaluate(a);

        Assert.AreEqual(expectedRead, Reason(result, DuplicateMergeVerificationReasonKind.Unreadable) != null);
    }

    /// <summary>
    /// 保証：全量と部分の境界。サイズが全量読取りの上限ちょうどなら全量、上限+1なら部分（先頭・中央・末尾の区間）で比較し、確認結果が使った方法を数える。
    /// 検出する誤り：境界の不等号の誤り（以上と超過の取り違え）、比較方法の誤記録。
    /// </summary>
    [TestMethod]
    [DataRow("x.WEBM", 192 * 1024, true)]
    [DataRow("x.WEBM", 192 * 1024 + 1, false)]
    [DataRow("x.bin", 32 * 1024 * 1024, true)]
    [DataRow("x.bin", 32 * 1024 * 1024 + 1, false)]
    public void ContentComparison_UsesResourceSpecificLimit(string name, int size, bool expectedFull)
    {
        string a = Folder("g/a"), b = Folder("g/b");
        WriteSizedFile(Path.Combine(a, name), size);
        WriteSizedFile(Path.Combine(b, name), size);

        DuplicateMergeVerificationResult result = Inspect([a, b]).Evaluate(a);

        Assert.AreEqual(DuplicateMergeVerificationStage.Safe, result.Stage);
        Assert.AreEqual(expectedFull ? (0, 1, 0) : (0, 0, 1), (result.Comparisons!.SizeAndTime, result.Comparisons.Full, result.Comparisons.Partial));
    }

    /// <summary>
    /// 保証：512 KiBの動画では、先頭・中央・末尾の各64 KiBにある差を検出し、そのすぐ外側の差は部分一致となります。
    /// 区間の長さや中央の位置のずれを、境界の前後1バイトで検出します。
    /// </summary>
    [TestMethod]
    [DataRow(0, true)]
    [DataRow(64 * 1024 - 1, true)]
    [DataRow(64 * 1024, false)]
    [DataRow(224 * 1024 - 1, false)]
    [DataRow(224 * 1024, true)]
    [DataRow(288 * 1024 - 1, true)]
    [DataRow(288 * 1024, false)]
    [DataRow(448 * 1024 - 1, false)]
    [DataRow(448 * 1024, true)]
    [DataRow(512 * 1024 - 1, true)]
    public void PartialRead_DetectsDifferencesOnlyInReadSegments(int index, bool expectedConflict)
    {
        const int size = 512 * 1024;
        string a = Folder("g/a"), b = Folder("g/b");
        byte[] first = PatternBytes(size, 21);
        byte[] second = [.. first];
        second[index] ^= 0xFF;
        WriteFile(Path.Combine(a, "big.mp4"), first);
        WriteFile(Path.Combine(b, "big.mp4"), second);

        DuplicateMergeVerificationResult result = Inspect([a, b]).Evaluate(a);

        Assert.AreEqual(expectedConflict, Reason(result, DuplicateMergeVerificationReasonKind.ContentConflict) != null);
        if (!expectedConflict)
        {
            Assert.AreEqual(DuplicateMergeVerificationStage.Safe, result.Stage);
            Assert.AreEqual(1, result.Comparisons!.Partial, "未読領域だけの差は同一内容で、比較方法は「部分」です。");
        }
    }

    /// <summary>32 MiB付近で部分比較の範囲外だけを変更し、その他の境界と音源・静止画に上限を誤適用する回帰を検出します。</summary>
    [TestMethod]
    [DataRow("x.bin", 32 * 1024 * 1024, true)]
    [DataRow("x.bin", 32 * 1024 * 1024 + 1, false)]
    [DataRow("x.FLAC", 32 * 1024 * 1024 + 1, true)]
    [DataRow("x.JPEG", 32 * 1024 * 1024 + 1, true)]
    public void ContentComparison_DetectsUnsampledDifferenceWhenReadingInFull(string name, int size, bool expectedConflict)
    {
        string a = Folder("g/a"), b = Folder("g/b");
        WriteSizedFile(Path.Combine(a, name), size);
        WriteSizedFile(Path.Combine(b, name), size, changeQuarter: true);

        DuplicateMergeVerificationResult result = Inspect([a, b]).Evaluate(a);

        Assert.AreEqual(expectedConflict, Reason(result, DuplicateMergeVerificationReasonKind.ContentConflict) != null);
        if (!expectedConflict) { Assert.AreEqual(1, result.Comparisons!.Partial); }
    }

    private static void WriteSizedFile(string path, int size, bool changeQuarter = false)
    {
        using FileStream stream = File.Create(path);
        stream.SetLength(size);
        if (changeQuarter)
        {
            stream.Position = size / 4;
            stream.WriteByte(1);
        }
    }
}
