using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using Ribbit.Media.Audio;
using static BeMusicSeeker.TestSupport.Components.DuplicateMergeAudioTestFiles;
using static BeMusicSeeker.TestSupport.Components.DuplicateMergeSafetyTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 重複音源の確認の検査器（<see cref="AudioDuplicateFolderInspector"/>）を、実ファイル・実の復号器・実の同梱ネイティブで入口から確認します。
/// 同等性の判定そのものは <see cref="DuplicateMergeAudioEquivalenceInspectionTests"/> が担当するため、ここでは同等・非同等・復号不能・読取りの入出力の失敗の代表だけを確かめます。
/// 各ケースは音声ランタイムが未初期化の状態から始め、検査が自分で用意できることも確かめます。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AudioDuplicateFolderInspectionTests
{
    private const string MonoOgg = "nvorbis-1test.ogg";

    private TestWorkspace? ownedWorkspace;

    private TestWorkspace Workspace => ownedWorkspace ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    [TestInitialize]
    public void InitializeCase()
    {
        BassAudioRuntime.Shutdown();
        ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
    }

    [TestCleanup]
    public void CleanupCase()
    {
        ownedWorkspace?.Dispose();
        BassAudioRuntime.Shutdown();
    }

    private string Folder(string relative)
    {
        string path = Workspace.GetPath(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string AudioPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "TestData", "audio", fileName);

    private static void CopyOgg(string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(AudioPath(MonoOgg), destination);
    }

    private static void WriteEquivalentWave(string path)
        => WriteWave(path, 44100, 1, ReadFloat32(File.ReadAllBytes(AudioPath("nvorbis-1test-reference.f32"))));

    private static void WriteDifferentWave(string path, int seed) => WriteWave(path, 44100, 1, Noise(17318, seed));

    /// <summary>復号できない（空の）WAVとOGGの組です。同等性は判定できず同等でないが、読取りの失敗ではありません。</summary>
    private static void WriteEmptyPair(string folder, string name)
    {
        WriteFile(Path.Combine(folder, name + ".wav"), []);
        WriteFile(Path.Combine(folder, name + ".ogg"), []);
    }

    private static AudioDuplicateFolderInspection Inspect(string folder, Func<string, bool>? isOtherTargetFolder = null)
        => new AudioDuplicateFolderInspector().Inspect(new AudioDuplicateInspectionRequest(folder, isOtherTargetFolder));

    private static SortedDictionary<string, byte[]> Tree(string root)
        => new(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 保証：衝突名は、所有範囲を一回だけ列挙して実フォルダから確定する。隠し・システム属性のファイルとディレクトリを含み、配下の別の対象フォルダ以下は
    /// 入らずに除く（判定は配下のディレクトリごとに一回だけで、除いたディレクトリの中は列挙しない）。名前の規則は探索と同じで、大文字の拡張子・複数ピリオドの名前・
    /// mp3/flacが並ぶ名前は衝突名、拡張子なしの名前が空・空白だけ・<c>..</c>のファイル（整理の要求の名前として扱えない。確定した衝突名で <c>AudioDuplicateOrganizeFolder</c> を構築できる）・片方だけの名前・別のディレクトリの組・WAVという名前のディレクトリは衝突名でない。
    /// 衝突名は相対ディレクトリと名前を <c>\</c> でつないだ大文字小文字を無視する昇順で、復号できない組は同等でなくても整理不可にならない。
    /// 検出する誤り：隠し・システム属性の取りこぼし、配下の対象フォルダの混入、名前の規則の誤り、二重の列挙、復号不能の整理不可化。
    /// </summary>
    [TestMethod]
    public void Inspect_DeterminesCollisionNamesFromTheOwnedRangeByTheCollisionRule()
    {
        string folder = Folder("own");
        WriteEmptyPair(folder, "Foo");
        WriteFile(Path.Combine(folder, "UP.WAV"), []);
        WriteFile(Path.Combine(folder, "up.OGG"), []);
        WriteEmptyPair(folder, "foo.v2");
        WriteEmptyPair(folder, "m");
        WriteFile(Path.Combine(folder, "m.mp3"), [1]);
        WriteFile(Path.Combine(folder, "m.flac"), [1]);
        WriteFile(Path.Combine(folder, ".wav"), []);
        WriteFile(Path.Combine(folder, ".ogg"), []);
        WriteFile(Path.Combine(folder, "  .wav"), []);
        WriteFile(Path.Combine(folder, "  .ogg"), []);
        WriteFile(Path.Combine(folder, "...wav"), []);
        WriteFile(Path.Combine(folder, "...ogg"), []);
        WriteFile(Path.Combine(folder, "only.wav"), []);
        WriteFile(Path.Combine(folder, "solo.ogg"), []);
        WriteFile(Path.Combine(folder, "x.wav", "z.wav"), []);
        WriteFile(Path.Combine(folder, "x.ogg"), []);
        WriteFile(Path.Combine(folder, "sub", "s.wav"), []);
        WriteFile(Path.Combine(folder, "other", "s.ogg"), []);
        WriteEmptyPair(Path.Combine(folder, "sub"), "t");
        string hiddenFile = WriteFile(Path.Combine(folder, "h.wav"), []);
        string hiddenOgg = WriteFile(Path.Combine(folder, "h.ogg"), []);
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        File.SetAttributes(hiddenOgg, FileAttributes.System);
        string hiddenDirectory = Folder("own/hid");
        WriteEmptyPair(hiddenDirectory, "d");
        File.SetAttributes(hiddenDirectory, FileAttributes.Hidden | FileAttributes.System);
        string inner = Folder("own/inner");
        WriteEmptyPair(inner, "in");
        WriteEmptyPair(Path.Combine(inner, "deep"), "dd");

        var visited = new List<string>();
        Func<string, bool> predicate = AudioDuplicateInspectionRequest.CreateTargetFolderPredicate([folder, inner]);
        AudioDuplicateFolderInspection result = Inspect(folder, directory =>
        {
            visited.Add(Path.GetRelativePath(folder, directory));
            return predicate(directory);
        });

        Assert.AreEqual(AudioDuplicateInspectionStage.Caution, result.Stage);
        Assert.AreEqual(0, result.Reasons.Count);
        CollectionAssert.AreEqual(
            new[] { "Foo", "foo.v2", "h", @"hid\d", "m", @"sub\t", "UP" }.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            result.CollisionNames.ToArray());
        Assert.AreEqual(0, result.EquivalentCollisionNames.Count, "復号できない組は同等でなく、整理不可の理由も増やしません。");
        _ = new AudioDuplicateOrganizeFolder(folder, ".wav", result.CollisionNames);
        CollectionAssert.AreEquivalent(
            new[] { "x.wav", "sub", "other", "hid", "inner" }, visited.ToArray(),
            "配下のディレクトリごとに判定は一回で、除いた配下の中（deep）は列挙しません。");
    }

    /// <summary>
    /// 保証：存在しないフォルダと、ディレクトリでないパスは、整理不可（解析不能）で、フォルダ自身の位置を該当に持ち、衝突名の一覧も同等の一覧も空になる。
    /// 配下の列挙に失敗した位置も、整理不可（解析不能）でその相対パスを該当に持つ。失敗を衝突名0件の安全へ読み替える誤りを検出する。
    /// </summary>
    [TestMethod]
    public void Inspect_FailedEnumeration_IsUnorganizableAsUnreadableWithTheLocation()
    {
        string missing = Workspace.GetPath("missing");
        AssertUnreadable(Inspect(missing), missing, string.Empty);

        string file = WriteFile(Workspace.GetPath("notadirectory"), [1]);
        AssertUnreadable(Inspect(file), file, string.Empty);

        string folder = Folder("own");
        WriteEmptyPair(folder, "a");
        string denied = Folder("own/denied");
        // 列挙の失敗は、ディレクトリを共有違反の状態で保持して再現する（DuplicateMergeSafetyInspectorTests と同じ方法）。
        using (DirectoryEnumerationBlocker.HoldSharingViolation(denied))
        {
            AssertUnreadable(Inspect(folder), folder, "denied");
        }
    }

    private static void AssertUnreadable(AudioDuplicateFolderInspection result, string folder, string relative, bool diagnosticsKept = false)
    {
        if (diagnosticsKept) { Assert.IsNotNull(result.PairDiagnostics, "判定まで進んだ組の診断は保持します。"); }
        else { Assert.IsNull(result.PairDiagnostics, "判定へ進まなかった整理不可は診断を持ちません。"); }
        Assert.AreEqual(AudioDuplicateInspectionStage.Unorganizable, result.Stage);
        DuplicateMergeVerificationReason reason = result.Reasons.Single();
        Assert.AreEqual(DuplicateMergeVerificationReasonKind.Unreadable, reason.Kind);
        CollectionAssert.AreEqual(new[] { new DuplicateMergeFinding(folder, relative) }, reason.Findings.ToArray());
        Assert.AreEqual(0, result.CollisionNames.Count);
        Assert.AreEqual(0, result.EquivalentCollisionNames.Count);
    }

    /// <summary>
    /// 保証：段階は、同等でない名前が一つでもあれば要注意、すべての名前が同等なら安全、衝突名が0件でも安全になる。同等な名前と同等でない名前が混在するときは、
    /// 同等の一覧が同等と判定した名前だけを持ち、同等でない名前は衝突名に残る。ファイルは変えない。
    /// 診断は、判定した全衝突名の組（衝突名の昇順）を持ち、衝突名が0件でも空の一覧を持つ。同等でない組は、同じレート・同じフレーム数の雑音なので、理由は波形と音量の四条件のいずれかで、比較は測定済み。
    /// 検出する誤り：混在での安全判定、衝突名0件の要注意、同等でない名前の同等化、検査によるファイルの変更、診断の欠落。
    /// </summary>
    [TestMethod]
    public void Inspect_Stages_FollowTheEquivalenceOfEveryCollisionName()
    {
        string none = Folder("none");
        WriteFile(Path.Combine(none, "only.wav"), []);
        AudioDuplicateFolderInspection empty = Inspect(none);
        Assert.AreEqual(AudioDuplicateInspectionStage.Safe, empty.Stage);
        Assert.AreEqual(0, empty.CollisionNames.Count);
        Assert.AreEqual(0, empty.PairDiagnostics!.Count, "衝突名がなくても、判定した結果として空の診断を持ちます。");

        string safe = Folder("safe");
        WriteEquivalentWave(Path.Combine(safe, "x.wav"));
        CopyOgg(Path.Combine(safe, "x.ogg"));
        WriteEquivalentWave(Path.Combine(safe, "bgm", "Y.WAV"));
        CopyOgg(Path.Combine(safe, "bgm", "Y.OGG"));
        AudioDuplicateFolderInspection safeResult = Inspect(safe);
        Assert.AreEqual(AudioDuplicateInspectionStage.Safe, safeResult.Stage);
        CollectionAssert.AreEqual(new[] { @"bgm\Y", "x" }, safeResult.CollisionNames.ToArray());
        CollectionAssert.AreEqual(safeResult.CollisionNames.ToArray(), safeResult.EquivalentCollisionNames.ToArray());
        CollectionAssert.AreEqual(new[] { @"bgm\Y", "x" }, safeResult.PairDiagnostics!.Select(pair => pair.Name).ToArray());
        Assert.IsTrue(safeResult.PairDiagnostics!.All(pair => pair.Measurement.Result == DuplicateMergeAudioPairResult.Equivalent));
        Assert.AreEqual(Path.Combine(safe, "x.wav"), safeResult.PairDiagnostics![1].WavPath);
        Assert.AreEqual(Path.Combine(safe, "x.ogg"), safeResult.PairDiagnostics![1].OggPath);
        Assert.AreEqual(Path.Combine(safe, "bgm", "Y.WAV"), safeResult.PairDiagnostics![0].WavPath);

        string mixed = Folder("mixed");
        WriteEquivalentWave(Path.Combine(mixed, "good.wav"));
        CopyOgg(Path.Combine(mixed, "good.ogg"));
        WriteDifferentWave(Path.Combine(mixed, "bad.wav"), seed: 1);
        CopyOgg(Path.Combine(mixed, "bad.ogg"));
        SortedDictionary<string, byte[]> before = Tree(Workspace.RootDirectory);
        AudioDuplicateFolderInspection mixedResult = Inspect(mixed);
        Assert.AreEqual(AudioDuplicateInspectionStage.Caution, mixedResult.Stage);
        CollectionAssert.AreEqual(new[] { "bad", "good" }, mixedResult.CollisionNames.ToArray());
        CollectionAssert.AreEqual(new[] { "good" }, mixedResult.EquivalentCollisionNames.ToArray());
        CollectionAssert.AreEqual(new[] { "bad" }, mixedResult.NonEquivalentCollisionNames.ToArray());
        CollectionAssert.AreEqual(new[] { "bad", "good" }, mixedResult.PairDiagnostics!.Select(pair => pair.Name).ToArray());
        DuplicateMergeAudioPairMeasurement bad = mixedResult.PairDiagnostics![0].Measurement;
        Assert.AreEqual(DuplicateMergeAudioPairResult.NonEquivalent, bad.Result);
        Assert.IsTrue(
            bad.Reason is DuplicateMergeAudioPairReason.LowBandWaveform or DuplicateMergeAudioPairReason.Correlation
                or DuplicateMergeAudioPairReason.Volume or DuplicateMergeAudioPairReason.SegmentVolume,
            "同じレート・同じフレーム数の雑音の組の理由は、波形と音量の四条件のいずれかです: " + bad.Reason);
        Assert.IsNotNull(bad.Comparison);
        Assert.AreEqual(DuplicateMergeAudioPairResult.Equivalent, mixedResult.PairDiagnostics![1].Measurement.Result);
        SortedDictionary<string, byte[]> after = Tree(Workspace.RootDirectory);
        CollectionAssert.AreEqual(before.Keys.ToArray(), after.Keys.ToArray(), "ファイルの集合が変わりました。");
        foreach (KeyValuePair<string, byte[]> item in before)
        {
            CollectionAssert.AreEqual(item.Value, after[item.Key], item.Key + " のバイト列が変わりました。");
        }
    }

    /// <summary>
    /// 保証：復号できない音源（空のOGG）を含む名前は同等でなく、解析不能の理由を増やさない（要注意）。
    /// 読取りの入出力の失敗（排他ロックされたWAVまたはOGG）は整理不可（解析不能）で、そのファイルの相対パスを該当に持ち、読めた名前の結果も出さない（一覧は空）。
    /// 診断は、復号できない組の理由（OGG復号不可）を持ち、読取りの入出力の失敗で整理不可になったフォルダも、判定まで進んだ全組の診断（失敗した組は読取り失敗）を保持する。
    /// 衝突名と同等の一覧は空のままで、リンクや列挙の失敗は診断を持たない。
    /// 検出する誤り：復号不能の解析不能化、入出力の失敗の握りつぶしと失敗した名前の同等化、整理不可での診断の欠落。
    /// </summary>
    [TestMethod]
    public void Inspect_UndecodableIsNotEquivalent_AndReadFailureIsUnreadable()
    {
        string undecodable = Folder("undecodable");
        WriteEquivalentWave(Path.Combine(undecodable, "u.wav"));
        WriteFile(Path.Combine(undecodable, "u.ogg"), []);
        AudioDuplicateFolderInspection result = Inspect(undecodable);
        Assert.AreEqual(AudioDuplicateInspectionStage.Caution, result.Stage);
        Assert.AreEqual(0, result.Reasons.Count);
        CollectionAssert.AreEqual(new[] { "u" }, result.CollisionNames.ToArray());
        Assert.AreEqual(0, result.EquivalentCollisionNames.Count);
        DuplicateMergeAudioPairMeasurement undecodablePair = result.PairDiagnostics!.Single().Measurement;
        Assert.AreEqual(DuplicateMergeAudioPairReason.OggUndecodable, undecodablePair.Reason);
        Assert.AreEqual(new DuplicateMergeOggInfo(null, null, null, 0), undecodablePair.Ogg);

        foreach (string lockedName in new[] { "x.wav", "x.ogg" })
        {
            string folder = Folder("locked-" + lockedName);
            string wav = Path.Combine(folder, "x.wav"), ogg = Path.Combine(folder, "x.ogg");
            WriteEquivalentWave(wav);
            CopyOgg(ogg);
            WriteEquivalentWave(Path.Combine(folder, "y.wav"));
            CopyOgg(Path.Combine(folder, "y.ogg"));
            AudioDuplicateFolderInspection locked;
            using (new FileStream(lockedName == "x.wav" ? wav : ogg, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                locked = Inspect(folder);
            }
            AssertUnreadable(locked, folder, lockedName, diagnosticsKept: true);
            CollectionAssert.AreEqual(new[] { "x", "y" }, locked.PairDiagnostics!.Select(pair => pair.Name).ToArray());
            Assert.AreEqual(DuplicateMergeAudioPairResult.ReadFailure, locked.PairDiagnostics![0].Measurement.Result, "読めなかったファイルを含む組です。");
            Assert.AreEqual(DuplicateMergeAudioPairReason.None, locked.PairDiagnostics![0].Measurement.Reason);
            Assert.AreEqual(DuplicateMergeAudioPairResult.Equivalent, locked.PairDiagnostics![1].Measurement.Result, "読めた名前の組も判定しています。");
        }
    }
}
