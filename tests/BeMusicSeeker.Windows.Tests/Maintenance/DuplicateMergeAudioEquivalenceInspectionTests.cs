using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using Ribbit.Media.Audio;
using static BeMusicSeeker.TestSupport.Components.DuplicateMergeAudioTestFiles;
using static BeMusicSeeker.TestSupport.Components.DuplicateMergeSafetyTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 統合の安全性確認の同等性検査を、実ファイル・実の復号器（<see cref="VorbisDecoder"/>）・実の同梱ネイティブで検査器の入口から確認します。
/// 所持譜面の情報だけを既存の代替にします。各ケースは音声ランタイムが未初期化の状態から始め、検査が自分で用意できること（起動直後で再生していない状態）も確かめます。
/// OGGは固定のmono（nvorbis-1test）とstereo（nvorbis-3test）を使い、同等なWAVは参照PCMから、同等でないWAVは決定的な雑音から作ります。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DuplicateMergeAudioEquivalenceInspectionTests
{
    private const string MonoOgg = "nvorbis-1test.ogg";
    private const string StereoOgg = "nvorbis-3test.ogg";

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

    private static DuplicateMergeFinding Finding(string folder, string relative) => new(folder, relative);

    private static void CopyOgg(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(AudioPath(source), destination);
    }

    /// <summary>参照PCMから作る、OGGと同等なWAV（32ビット浮動小数点）を書きます。</summary>
    private static void WriteEquivalentWave(string path, bool stereo = false)
        => WriteWave(path, 44100, stereo ? 2 : 1,
            ReadFloat32(File.ReadAllBytes(AudioPath(stereo ? "nvorbis-3test-reference.f32" : "nvorbis-1test-reference.f32"))));

    /// <summary>OGGと同形式・同フレーム数で、波形が無相関なWAVを書きます。</summary>
    private static void WriteDifferentWave(string path, int seed, bool stereo = false)
        => WriteWave(path, 44100, stereo ? 2 : 1, Noise(stereo ? 288094 * 2 : 17318, seed));

    /// <summary>すべてのケースで二つのフォルダが共有する一ファイルを置き、同一内容ファイルなしの理由が混ざらないようにします。</summary>
    private (string A, string B) Pair()
    {
        string a = Folder("g/a"), b = Folder("g/b");
        WriteFile(Path.Combine(a, "common.bin"), [1]);
        WriteFile(Path.Combine(b, "common.bin"), [1]);
        return (a, b);
    }

    private static string[] Names(DuplicateMergeVerificationResult result)
        => [.. result.EquivalentAudioNames!.Select(name => name.ToLowerInvariant()).Order()];

    private static DuplicateMergeFinding[] AudioFindings(DuplicateMergeVerificationResult result)
        => Reason(result, DuplicateMergeVerificationReasonKind.AudioExtension)?.Findings.ToArray() ?? [];

    /// <summary>
    /// 保証：WAVから変換されたOGGを持つ名前（拡張子が大文字の行、別内容のファイル衝突が同居する行を含む）は、拡張子違いの理由から外れ、
    /// 同等と判定した名前の一覧と「全衝突が同等」が、KEEPをどちらにしても同じ値で得られる。他の理由（別内容ファイル衝突）はそのまま残る。
    /// 検出する誤り：同等な名前の理由残り、KEEPによる結果の変化、大文字の拡張子の見落とし、他の理由の巻き添え、起動直後で音声ランタイムが未初期化のときの失敗。
    /// </summary>
    [TestMethod]
    public void Inspect_ConvertedPairs_ExcludeTheNameAndMarkAllCollisionsEquivalentForAnyKeep()
    {
        (string a, string b) = Pair();
        WriteEquivalentWave(Path.Combine(a, "x.wav"));
        CopyOgg(MonoOgg, Path.Combine(b, "x.ogg"));
        WriteEquivalentWave(Path.Combine(a, "bgm", "Y.WAV"), stereo: true);
        CopyOgg(StereoOgg, Path.Combine(b, "bgm", "Y.OGG"));
        WriteFile(Path.Combine(a, "other.bin"), PatternBytes(64, 1));
        WriteFile(Path.Combine(b, "other.bin"), PatternBytes(64, 2));

        DuplicateMergeGroupInspection inspection = Inspect([a, b], audioEquivalence: true);

        foreach (string keep in new[] { a, b })
        {
            DuplicateMergeVerificationResult result = inspection.Evaluate(keep);
            Assert.IsNull(Reason(result, DuplicateMergeVerificationReasonKind.AudioExtension), "同等な名前は拡張子違いに該当しません。");
            CollectionAssert.AreEqual(new[] { DuplicateMergeVerificationReasonKind.ContentConflict }, Kinds(result), "他の理由は残ります。");
            CollectionAssert.AreEqual(new[] { @"bgm\y", "x" }, Names(result));
            Assert.IsTrue(result.AllAudioCollisionsEquivalent);
        }
    }

    /// <summary>
    /// 保証：名前の同等性は、グループ内の全フォルダの全WAV×全OGGの組（同じフォルダ内の組を含む）がすべて同等のときだけ成立する。
    /// 別フォルダ間の組だけが同等でない場合、同じフォルダ内の組だけが同等でない場合、三つ目のフォルダのOGGだけが異なる場合は、いずれも拡張子違いの理由が残る。
    /// 検出する誤り：同じフォルダ内の組の省略、フォルダ間の組の省略、先頭の組だけの判定。
    /// </summary>
    [TestMethod]
    public void Inspect_EveryWavOggPairIncludingSameFolder_MustBeEquivalent()
    {
        // 別フォルダ間の組だけが同等でない: A={x.wav(同等), x.ogg}, B={x.wav(異なる)}。
        string a = Folder("c1/a"), b = Folder("c1/b");
        WriteFile(Path.Combine(a, "common.bin"), [1]);
        WriteFile(Path.Combine(b, "common.bin"), [1]);
        WriteEquivalentWave(Path.Combine(a, "x.wav"));
        CopyOgg(MonoOgg, Path.Combine(a, "x.ogg"));
        WriteDifferentWave(Path.Combine(b, "x.wav"), seed: 1);
        AssertNameRemains(Inspect([a, b], audioEquivalence: true).Evaluate(a), "別フォルダ間の組");

        // 同じフォルダ内の組だけが同等でない: A={x.wav(異なる), x.ogg}, B={x.wav(同等)}。
        a = Folder("c2/a");
        b = Folder("c2/b");
        WriteFile(Path.Combine(a, "common.bin"), [1]);
        WriteFile(Path.Combine(b, "common.bin"), [1]);
        WriteDifferentWave(Path.Combine(a, "x.wav"), seed: 2);
        CopyOgg(MonoOgg, Path.Combine(a, "x.ogg"));
        WriteEquivalentWave(Path.Combine(b, "x.wav"));
        AssertNameRemains(Inspect([a, b], audioEquivalence: true).Evaluate(a), "同じフォルダ内の組");

        // 三つ目のフォルダのOGGだけが異なる: A=x.wav, B=x.ogg(同等), C=x.ogg(形式が異なる)。
        a = Folder("c3/a");
        b = Folder("c3/b");
        string c = Folder("c3/c");
        foreach (string folder in new[] { a, b, c }) { WriteFile(Path.Combine(folder, "common.bin"), [1]); }
        WriteEquivalentWave(Path.Combine(a, "x.wav"));
        CopyOgg(MonoOgg, Path.Combine(b, "x.ogg"));
        CopyOgg(StereoOgg, Path.Combine(c, "x.ogg"));
        AssertNameRemains(Inspect([a, b, c], audioEquivalence: true).Evaluate(a), "三つ目のフォルダ");
    }

    private static void AssertNameRemains(DuplicateMergeVerificationResult result, string label)
    {
        Assert.IsTrue(AudioFindings(result).Length > 0, label + ": 拡張子違いの理由が残ります。");
        Assert.AreEqual(0, result.EquivalentAudioNames!.Count, label);
        Assert.IsFalse(result.AllAudioCollisionsEquivalent, label);
        Assert.IsNull(Reason(result, DuplicateMergeVerificationReasonKind.Unreadable), label);
    }

    /// <summary>
    /// 保証：同等な名前だけが拡張子違いから外れ、同等でない名前と、WAV・OGG以外の形式（mp3）を含む名前は従来どおり全パスが該当し、後者の音源は読まない
    /// （排他ロックしても解析不能が出ない）。名前を多数（同等10件）にしても並列処理で取りこぼさず、一覧は同等な名前だけで、「全衝突が同等」は偽になる。
    /// 検出する誤り：並列処理での結果の取りこぼし・取り違え、mp3を含む名前の検査、同等でない名前の巻き添え除外。
    /// </summary>
    [TestMethod]
    public void Inspect_MixedNames_ExcludeOnlyEquivalentNamesAndSkipNamesWithOtherFormats()
    {
        (string a, string b) = Pair();
        string[] equivalentNames = [.. Enumerable.Range(0, 10).Select(index => "n" + index)];
        foreach (string name in equivalentNames)
        {
            WriteEquivalentWave(Path.Combine(a, name + ".wav"));
            CopyOgg(MonoOgg, Path.Combine(b, name + ".ogg"));
        }
        WriteDifferentWave(Path.Combine(a, "y.wav"), seed: 3);
        CopyOgg(MonoOgg, Path.Combine(b, "y.ogg"));
        string lockedWav = WriteFile(Path.Combine(a, "z.wav"), [1, 2, 3]);
        WriteFile(Path.Combine(a, "z.mp3"), [4, 5, 6]);
        string lockedOgg = WriteFile(Path.Combine(b, "z.ogg"), [7, 8, 9]);

        DuplicateMergeVerificationResult result;
        using (new FileStream(lockedWav, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(lockedOgg, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Inspect([a, b], audioEquivalence: true).Evaluate(a);
        }

        CollectionAssert.AreEquivalent(
            new[]
            {
                Finding(a, "y.wav"), Finding(b, "y.ogg"),
                Finding(a, "z.wav"), Finding(a, "z.mp3"), Finding(b, "z.ogg")
            },
            AudioFindings(result));
        CollectionAssert.AreEqual(equivalentNames.Order().ToArray(), Names(result));
        Assert.IsFalse(result.AllAudioCollisionsEquivalent);
        Assert.IsNull(Reason(result, DuplicateMergeVerificationReasonKind.Unreadable), "mp3を含む名前の音源は読みません。");
    }

    /// <summary>
    /// 保証：検査なしでは拡張子違いに該当しない名前（全フォルダで拡張子の集合が同じ名前、一つのフォルダだけが持つ名前）は、同等でも数えず、
    /// 一覧は空で「全衝突が同等」は偽（該当する名前が一つもないため）。
    /// 検出する誤り：該当しない名前の一覧への混入、該当ゼロでの「全衝突が同等」の真。
    /// </summary>
    [TestMethod]
    public void Inspect_NamesThatDoNotCollideWithoutTheCheck_AreNotCounted()
    {
        (string a, string b) = Pair();
        foreach (string folder in new[] { a, b })
        {
            WriteEquivalentWave(Path.Combine(folder, "s.wav"));
            CopyOgg(MonoOgg, Path.Combine(folder, "s.ogg"));
        }
        WriteEquivalentWave(Path.Combine(a, "only.wav"));
        CopyOgg(MonoOgg, Path.Combine(a, "only.ogg"));

        DuplicateMergeVerificationResult result = Inspect([a, b], audioEquivalence: true).Evaluate(a);

        Assert.IsNull(Reason(result, DuplicateMergeVerificationReasonKind.AudioExtension));
        Assert.AreEqual(0, result.EquivalentAudioNames!.Count);
        Assert.IsFalse(result.AllAudioCollisionsEquivalent);
    }

    /// <summary>
    /// 保証：復号できない音源（空のOGG、先頭ページを壊したOGG、ADPCMのWAV、ヘッダー途中で切れたWAV）を含む名前は、同等でなく、拡張子違いの理由が残り、
    /// 理由を増やさない（解析不能が出ず、ネイティブの結果も致命的な失敗にならない）。
    /// 検出する誤り：復号不能の解析不能化、復号不能での例外、復号不能な音源を含む名前の同等化。
    /// </summary>
    [TestMethod]
    public void Inspect_UndecodableAudio_IsNotEquivalentWithoutAddingAReason()
    {
        (string a, string b) = Pair();
        // u1: 空のOGG。 u2: 壊れたOGG。 u3: ADPCMのWAV。 u4: ヘッダー途中で切れたWAV。
        WriteEquivalentWave(Path.Combine(a, "u1.wav"));
        WriteFile(Path.Combine(b, "u1.ogg"), []);
        WriteEquivalentWave(Path.Combine(a, "u2.wav"));
        byte[] broken = File.ReadAllBytes(AudioPath(MonoOgg));
        Array.Clear(broken, 0, 64);
        WriteFile(Path.Combine(b, "u2.ogg"), broken);
        byte[] adpcm = Wave(44100, 1, WaveSampleFormat.Pcm16, [0.5f, 0.25f]);
        adpcm[20] = 2;
        WriteFile(Path.Combine(a, "u3.wav"), adpcm);
        CopyOgg(MonoOgg, Path.Combine(b, "u3.ogg"));
        WriteFile(Path.Combine(a, "u4.wav"), Wave(44100, 1, WaveSampleFormat.Pcm16, [0.5f, 0.25f])[..30]);
        CopyOgg(MonoOgg, Path.Combine(b, "u4.ogg"));

        DuplicateMergeVerificationResult result = Inspect([a, b], audioEquivalence: true).Evaluate(a);

        CollectionAssert.AreEquivalent(
            new[]
            {
                Finding(a, "u1.wav"), Finding(b, "u1.ogg"), Finding(a, "u2.wav"), Finding(b, "u2.ogg"),
                Finding(a, "u3.wav"), Finding(b, "u3.ogg"), Finding(a, "u4.wav"), Finding(b, "u4.ogg")
            },
            AudioFindings(result));
        Assert.IsNull(Reason(result, DuplicateMergeVerificationReasonKind.Unreadable));
        Assert.AreEqual(0, result.EquivalentAudioNames!.Count);
        Assert.IsFalse(result.AllAudioCollisionsEquivalent);
    }

    /// <summary>
    /// 保証：WAVまたはOGGを開けない（排他ロック）入出力の失敗は、そのファイルの相対パスで解析不能になり、その名前は同等と判定されず拡張子違いの理由が残る。
    /// 検出する誤り：入出力の失敗の握りつぶし・復号不能との取り違え、失敗した名前の同等化、読めたファイル側の誤った解析不能。
    /// </summary>
    [TestMethod]
    [DataRow("x.wav")]
    [DataRow("x.ogg")]
    public void Inspect_ReadFailure_IsUnreadableAndNotEquivalent(string lockedName)
    {
        (string a, string b) = Pair();
        string wav = Path.Combine(a, "x.wav"), ogg = Path.Combine(b, "x.ogg");
        WriteEquivalentWave(wav);
        CopyOgg(MonoOgg, ogg);
        string locked = lockedName == "x.wav" ? wav : ogg;
        string lockedFolder = lockedName == "x.wav" ? a : b;

        DuplicateMergeVerificationResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Inspect([a, b], audioEquivalence: true).Evaluate(a);
        }

        Assert.AreEqual(DuplicateMergeVerificationStage.Unmergeable, result.Stage);
        CollectionAssert.AreEqual(new[] { Finding(lockedFolder, lockedName) }, Reason(result, DuplicateMergeVerificationReasonKind.Unreadable)!.Findings.ToArray());
        CollectionAssert.AreEquivalent(new[] { Finding(a, "x.wav"), Finding(b, "x.ogg") }, AudioFindings(result));
        Assert.AreEqual(0, result.EquivalentAudioNames!.Count);
        Assert.IsFalse(result.AllAudioCollisionsEquivalent);
    }
}
