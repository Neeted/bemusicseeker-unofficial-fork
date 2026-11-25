using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.DuplicateMergeAudioTestFiles;

namespace BeMusicSeeker.Tests;

/// <summary>同等性検査のWAV読取りを、無圧縮PCMの各形式・チャンクの並び・長さの申告の食い違い・非対応入力で確認します。</summary>
[TestClass]
public sealed class DuplicateMergeWaveReaderTests
{
    private static DuplicateMergeAudioSamples? Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return DuplicateMergeWaveReader.Read(stream);
    }

    /// <summary>
    /// 保証：整数8・16・24・32ビットと32ビット浮動小数点（拡張形式を含む）のステレオが、レート・チャンネル数・フレーム数・交互の並びを保ち、
    /// 値がフルスケール±1へ正規化される（許容差は各形式の分解能 2^-(n-1)）。8ビットは符号なしで中心128。
    /// 検出する誤り：符号・中心値・ビット幅の取り違え、チャンネルの並びの崩れ、拡張形式の見落とし、正規化の係数の誤り。
    /// </summary>
    [TestMethod]
    [DataRow(WaveSampleFormat.Pcm8, false)]
    [DataRow(WaveSampleFormat.Pcm16, false)]
    [DataRow(WaveSampleFormat.Pcm24, false)]
    [DataRow(WaveSampleFormat.Pcm32, false)]
    [DataRow(WaveSampleFormat.Float32, false)]
    [DataRow(WaveSampleFormat.Pcm16, true)]
    [DataRow(WaveSampleFormat.Pcm24, true)]
    [DataRow(WaveSampleFormat.Float32, true)]
    public void Read_UncompressedPcmFormats_NormalizeToFullScale(WaveSampleFormat format, bool extensible)
    {
        // 左は正、右は負の値で、符号と並びを同時に確かめる。値は各形式の分解能の整数倍に取る。
        float[] expected = [0.5f, -0.5f, 0.25f, -0.75f, -1f, 0.125f, 0f, -0.0625f];

        DuplicateMergeAudioSamples? actual = Read(Wave(44100, 2, format, expected, extensible));

        Assert.IsNotNull(actual);
        Assert.AreEqual(44100, actual.SampleRate);
        Assert.AreEqual(2, actual.Channels);
        Assert.AreEqual(4L, actual.FrameCount);
        double tolerance = format == WaveSampleFormat.Float32 ? 0 : 1 / Scale(format);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index], actual.Samples[index], tolerance, $"sample {index}");
        }
    }

    /// <summary>
    /// 保証：<c>fmt </c> と <c>data</c> の間にある奇数長のチャンク（パディング付き）を読み飛ばして <c>data</c> に到達し、<c>data</c> の後ろのチャンクは無視する。
    /// 検出する誤り：パディングの無視による以後のチャンク位置のずれ、後続チャンクのサンプルへの混入。
    /// </summary>
    [TestMethod]
    public void Read_SkipsOtherAndPaddedChunksAndIgnoresTrailingChunks()
    {
        float[] samples = [0.5f, -0.25f, 0.125f, -0.5f];
        byte[] oddList = [1, 2, 3, 4, 5];
        byte[] trailing = [9, 9, 9, 9, 9, 9];

        DuplicateMergeAudioSamples? actual = Read(Wave(8000, 1, WaveSampleFormat.Pcm16, samples, chunkBetween: oddList, chunkAfter: trailing));

        Assert.IsNotNull(actual);
        Assert.AreEqual(8000, actual.SampleRate);
        Assert.AreEqual(1, actual.Channels);
        CollectionAssert.AreEqual(samples, actual.Samples);
    }

    /// <summary>
    /// 保証：<c>data</c> の申告長（400バイトや0xFFFFFFFF）がファイルの残りを超える場合、実在する10バイトをフレーム単位に切り捨てた2フレームだけを読む。
    /// 検出する誤り：申告長を信じた過読み・例外、端数バイトの取込み、読めた範囲を捨てて復号不能にする誤り。
    /// </summary>
    [TestMethod]
    [DataRow(400u)]
    [DataRow(0xFFFFFFFFu)]
    public void Read_DataLongerThanFile_TruncatesToWholeFrames(uint declared)
    {
        // 16ビットのステレオは1フレーム4バイト。5フレーム分の20バイトのうち、10バイトだけが実在する。
        byte[] full = Wave(44100, 2, WaveSampleFormat.Pcm16, [0.5f, -0.5f, 0.25f, -0.25f, 0.125f, -0.125f, 0f, 0f, 0.5f, 0.5f], declaredDataLength: declared);
        byte[] truncated = full[..(full.Length - 10)];

        DuplicateMergeAudioSamples? actual = Read(truncated);

        Assert.IsNotNull(actual);
        Assert.AreEqual(2L, actual.FrameCount);
        CollectionAssert.AreEqual(new[] { 0.5f, -0.5f, 0.25f, -0.25f }, actual.Samples);
    }

    /// <summary>
    /// 保証：非対応（ADPCM、MP3、12ビット、64ビット浮動小数点、チャンネル0）・破損（RIFFでない、<c>data</c> なし、ヘッダーの途中で切れている、<c>fmt </c> が短い）の入力は、
    /// 例外ではなく「復号できない」（null）を返し、入出力の失敗とは区別される。
    /// 検出する誤り：非対応形式を無理に解釈した誤ったPCM、破損入力での例外（入出力の失敗との混同）。
    /// </summary>
    [TestMethod]
    public void Read_UnsupportedOrBrokenInput_IsNotDecodableAndNotAnIoFailure()
    {
        byte[] valid = Wave(44100, 1, WaveSampleFormat.Pcm16, [0.5f, 0.25f]);
        int formatOffset = 20, bitsOffset = 34, channelsOffset = 22;
        byte[] WithFormat(ushort tag, ushort? bits = null, ushort? channels = null)
        {
            byte[] bytes = [.. valid];
            BitConverter.GetBytes(tag).CopyTo(bytes, formatOffset);
            if (bits.HasValue) { BitConverter.GetBytes(bits.Value).CopyTo(bytes, bitsOffset); }
            if (channels.HasValue) { BitConverter.GetBytes(channels.Value).CopyTo(bytes, channelsOffset); }
            return bytes;
        }

        Assert.IsNotNull(Read(valid), "前提：変更前は読めます。");
        (string Name, byte[] Bytes)[] cases =
        [
            ("ADPCM", WithFormat(2)),
            ("MP3", WithFormat(0x55)),
            ("12ビット整数", WithFormat(1, bits: 12)),
            ("64ビット浮動小数点", WithFormat(3, bits: 64)),
            ("32ビット以外の浮動小数点", WithFormat(3, bits: 16)),
            ("チャンネル0", WithFormat(1, channels: 0)),
            ("RIFFでない", [.. "RIFX"u8, .. valid[4..]]),
            ("WAVEでない", [.. valid[..8], .. "AVI "u8, .. valid[12..]]),
            ("空", []),
            ("RIFFヘッダーの途中", valid[..10]),
            ("fmtの途中", valid[..30]),
            ("fmtの直後で終わる（dataなし）", valid[..36]),
            ("dataのヘッダーの途中", valid[..40]),
            ("dataの前に別のチャンクだけ", Wave(44100, 1, WaveSampleFormat.Pcm16, [], chunkBetween: [1, 2])[..^8]),
        ];

        foreach ((string name, byte[] bytes) in cases)
        {
            DuplicateMergeAudioSamples? actual = null;
            try { actual = Read(bytes); }
            catch (Exception failure) { Assert.Fail($"{name}: 例外ではなく復号不能（null）を返します: {failure.GetType().Name}"); }
            Assert.IsNull(actual, name);
        }
    }

    /// <summary>
    /// 保証：ファイルの読取りの入出力の失敗は、復号できないこと（null）に置き換えず例外のまま伝える。存在しないファイルは入出力の失敗である。
    /// 検出する誤り：入出力の失敗を握りつぶして「復号できない」にし、解析不能と取り違える。
    /// </summary>
    [TestMethod]
    public void ReadFile_MissingFile_ThrowsAnIoFailureInsteadOfReportingUndecodable()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        Assert.ThrowsExactly<FileNotFoundException>(() => DuplicateMergeWaveReader.ReadFile(Path.Combine(workspace.RootDirectory, "none.wav")));
    }
}
