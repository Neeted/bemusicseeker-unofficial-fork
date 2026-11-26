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
/// 実際の符号化（libvorbisのVBR品質4、約128kbps相当）で作ったOGGと元音の固定データ（<c>equivalence-*</c>）で、通常の変換を同等と判定し、音量・音色・遅れ・末尾の破裂音の違いを同等と判定しないことも確かめます。
/// 固定データは、元音をインターリーブしたfloat32リトルエンディアンの生データ（<c>*-source.f32</c>）と、同じ配列を soundfile 0.14.0／libsndfile 1.2.2 の
/// <c>format="OGG", subtype="VORBIS", compression_level=0.6</c>（VBR品質4相当）で符号化したOGGです。元音の生成手順は次のとおりです（fs=44100、numpy・scipy）。
/// hihat：モノラル13230フレーム。<c>numpy.random.default_rng(11).standard_normal(13230)</c> を <c>butter(4, 7000, "highpass", fs=44100)</c> の <c>sosfilt</c> に通し、
/// 0・4410・8820フレームから始まる <c>exp(−(n−開始)/(0.015·fs))</c> の和を掛け、ピークを0.5へ正規化。
/// snare：ステレオ13230フレーム。<c>body(n) = 0.35·exp(−n/(0.05·fs))·sin(2π·190·n/fs)</c>、<c>decay(n) = 0.25·exp(−n/(0.04·fs))</c>。
/// 左 = <c>body + default_rng(21).uniform(−1, 1)·decay</c>、右 = <c>10^(−13/20)·(body + default_rng(22).uniform(−1, 1)·decay)</c>（右は左より13dB小さい）。
/// pluck：48000Hz・モノラル14400フレームの元音は、<c>Σ_{h=1..8} (0.3/h)·exp(−n/(0.08·fs))·sin(2π·330h·n/fs) + default_rng(31).uniform(−1, 1)·0.1·exp(−n/(0.01·fs))</c>（fs=48000）を
/// ピーク0.5へ正規化したもので、OGGは <c>scipy.signal.resample_poly(x, 147, 160)</c> で13230フレームにして44100Hzで符号化したものです（<c>147·Nw − 160·No = 0</c>）。
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

    private static string[] CollisionNames(DuplicateMergeVerificationResult result)
        => [.. result.WavOggCollisionNames!.Select(name => name.ToLowerInvariant()).Order()];

    private static DuplicateMergeFinding[] AudioFindings(DuplicateMergeVerificationResult result)
        => Reason(result, DuplicateMergeVerificationReasonKind.AudioExtension)?.Findings.ToArray() ?? [];

    /// <summary>
    /// 保証：WAVから変換されたOGGを持つ名前（拡張子が大文字の行、別内容のファイル衝突が同居する行を含む）は、拡張子違いの理由から外れ、
    /// 同等と判定した名前の一覧と「全衝突が同等」が、KEEPをどちらにしても同じ値で得られる。他の理由（別内容ファイル衝突）はそのまま残る。
    /// 理由から外れても、WAV・OGGの衝突名には残る。
    /// 検出する誤り：同等な名前の理由残り、KEEPによる結果の変化、大文字の拡張子の見落とし、他の理由の巻き添え、起動直後で音声ランタイムが未初期化のときの失敗、
    /// 理由から外れた名前の衝突名からの欠落。
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
            CollectionAssert.AreEqual(new[] { @"bgm\y", "x" }, CollisionNames(result), "同等で理由から外れた名前も衝突名に残ります。");
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
    /// WAV・OGGの衝突名は、同等な名前と同等でない名前（y）の両方で、mp3を含む名前（z）を含まず、同等な名前はその部分集合になる。
    /// 検出する誤り：並列処理での結果の取りこぼし・取り違え、mp3を含む名前の検査、同等でない名前の巻き添え除外、衝突名への mp3 を含む名前の混入・同等でない名前の欠落。
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
        CollectionAssert.AreEqual(equivalentNames.Append("y").Order().ToArray(), CollisionNames(result), "衝突名は同等でない y を含み、mp3を含む z を含みません。");
        CollectionAssert.IsSubsetOf(Names(result), CollisionNames(result));
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

    private static float[] Source(string fileName) => ReadFloat32(File.ReadAllBytes(AudioPath(fileName)));

    private static float[] Scale(float[] samples, double gain) => [.. samples.Select(sample => (float)(sample * gain))];

    /// <summary>スネアの胴の音（190Hzの減衰する正弦波）です。</summary>
    private static double SnareBody(int frame, double hertz) => 0.35 * Math.Exp(-frame / (0.05 * 44100)) * Math.Sin(2 * Math.PI * hertz * frame / 44100);

    /// <summary>
    /// 保証：実変換の固定データの形式が想定どおりである（固定データが変わったときに、実変換のケースの失敗の原因を特定するため）。
    /// 検出する誤り：固定データの取り違え・欠落、復号器が返すサンプルレート・チャンネル数・フレーム数の変化。
    /// </summary>
    [TestMethod]
    public void FixedConversionData_HasTheExpectedFormat()
    {
        DuplicateMergeAudioReader.EnsureRuntime();

        foreach ((string file, int rate, int channels, int frames) in new[]
        {
            ("equivalence-hihat.ogg", 44100, 1, 13230),
            ("equivalence-snare.ogg", 44100, 2, 13230),
            ("equivalence-pluck-44100.ogg", 44100, 1, 13230)
        })
        {
            DuplicateMergeAudioSamples decoded = DuplicateMergeAudioReader.ReadOgg(AudioPath(file));
            Assert.IsNotNull(decoded, file);
            Assert.AreEqual(rate, decoded.SampleRate, file);
            Assert.AreEqual(channels, decoded.Channels, file);
            Assert.AreEqual(frames, decoded.FrameCount, file);
        }

        Assert.AreEqual(13230, Source("equivalence-hihat-source.f32").Length);
        Assert.AreEqual(13230 * 2, Source("equivalence-snare-source.f32").Length);
        Assert.AreEqual(14400, Source("equivalence-pluck-source.f32").Length);
    }

    /// <summary>
    /// 保証：実際の品質4の変換で作ったOGG（ハイハット・スネア、48000HzのWAVから変換した撥弦音、末尾へ0を100フレーム足したWAVのハイハット）は、同名のWAVと同等と判定される。
    /// 全名前が同等の一覧にあり、拡張子違いの理由がなく、「全衝突が同等」が真になる。
    /// 検出する誤り：実際の符号化雑音がある通常の変換（高域が中心の音、弱い側のチャンネル、雑音成分の多い打音）の拒否、実ファイルでレート違い・長さ違いの経路がつながらない誤り。
    /// </summary>
    [TestMethod]
    public void Inspect_RealVorbisConversions_AreEquivalent()
    {
        (string a, string b) = Pair();
        float[] hihat = Source("equivalence-hihat-source.f32");
        WriteWave(Path.Combine(a, "hh.wav"), 44100, 1, hihat);
        CopyOgg("equivalence-hihat.ogg", Path.Combine(b, "hh.ogg"));
        WriteWave(Path.Combine(a, "sn.wav"), 44100, 2, Source("equivalence-snare-source.f32"));
        CopyOgg("equivalence-snare.ogg", Path.Combine(b, "sn.ogg"));
        WriteWave(Path.Combine(a, "r48.wav"), 48000, 1, Source("equivalence-pluck-source.f32"));
        CopyOgg("equivalence-pluck-44100.ogg", Path.Combine(b, "r48.ogg"));
        WriteWave(Path.Combine(a, "pad.wav"), 44100, 1, [.. hihat, .. new float[100]]);
        CopyOgg("equivalence-hihat.ogg", Path.Combine(b, "pad.ogg"));

        DuplicateMergeVerificationResult result = Inspect([a, b], audioEquivalence: true).Evaluate(a);

        Assert.AreEqual(0, AudioFindings(result).Length, "拡張子違いの理由は残りません。");
        CollectionAssert.AreEqual(new[] { "hh", "pad", "r48", "sn" }, Names(result));
        Assert.IsTrue(result.AllAudioCollisionsEquivalent);
        Assert.IsNull(Reason(result, DuplicateMergeVerificationReasonKind.Unreadable));
    }

    /// <summary>
    /// 保証：実際の符号化雑音があっても、意図的な違い（スネアの胴の音の190Hzから230Hzへの置換、−3dB・−6dBの音量変更、末尾の端数区間の高域の破裂音、220フレームの遅れ）は、
    /// 同等と判定されない。全名前が拡張子違いに残り、同等の一覧は空で、解析不能は出ない。
    /// 検出する誤り：符号化雑音を理由にした誤った同等（削除範囲の拡大）。
    /// </summary>
    [TestMethod]
    public void Inspect_RealVorbisConversionsWithIntentionalDifferences_AreNotEquivalent()
    {
        (string a, string b) = Pair();
        float[] hihat = Source("equivalence-hihat-source.f32");
        float[] snare = Source("equivalence-snare-source.f32");

        // adj：スネアの胴の音を230Hzへ置き換える（右は13dB小さい）。
        double rightGain = Math.Pow(10, -13.0 / 20);
        float[] adjusted = [.. snare];
        for (int frame = 0; frame < adjusted.Length / 2; frame++)
        {
            adjusted[frame * 2] = (float)(snare[frame * 2] - SnareBody(frame, 190) + SnareBody(frame, 230));
            adjusted[frame * 2 + 1] = (float)(snare[frame * 2 + 1] - rightGain * SnareBody(frame, 190) + rightGain * SnareBody(frame, 230));
        }

        // tail：末尾の端数区間（942フレーム）に、ピーク区間の10分の1以上のエネルギーの高域の破裂音（交番）を加える。
        double peak = Enumerable.Range(0, 7).Max(segment => hihat.Skip(segment * 2048).Take(2048).Sum(sample => (double)sample * sample));
        double burst = Math.Sqrt(0.3 * peak / 942);
        float[] tailed = [.. hihat];
        for (int frame = 6 * 2048; frame < tailed.Length; frame++) { tailed[frame] += (float)(burst * (frame % 2 == 0 ? 1 : -1)); }

        float[] shifted = [.. new float[220], .. hihat.Take(hihat.Length - 220)];

        foreach ((string name, int channels, float[] wav, string ogg) in new[]
        {
            ("adj", 2, adjusted, "equivalence-snare.ogg"),
            ("v3", 2, Scale(snare, Math.Pow(10, -3.0 / 20)), "equivalence-snare.ogg"),
            ("v6", 2, Scale(snare, Math.Pow(10, -6.0 / 20)), "equivalence-snare.ogg"),
            ("tail", 1, tailed, "equivalence-hihat.ogg"),
            ("shift", 1, shifted, "equivalence-hihat.ogg")
        })
        {
            WriteWave(Path.Combine(a, name + ".wav"), 44100, channels, wav);
            CopyOgg(ogg, Path.Combine(b, name + ".ogg"));
        }

        DuplicateMergeVerificationResult result = Inspect([a, b], audioEquivalence: true).Evaluate(a);

        CollectionAssert.AreEqual(Array.Empty<string>(), Names(result), "同等と判定された名前があります。");
        Assert.AreEqual(10, AudioFindings(result).Length, "全名前の拡張子違いが残ります。");
        Assert.IsFalse(result.AllAudioCollisionsEquivalent);
        Assert.IsNull(Reason(result, DuplicateMergeVerificationReasonKind.Unreadable));
    }
}
