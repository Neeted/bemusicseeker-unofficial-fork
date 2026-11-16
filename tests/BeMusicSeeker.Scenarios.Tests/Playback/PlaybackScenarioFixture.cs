using System.IO;
using System.Text;
using BeMusicSeeker.Scenarios.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Files;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Scenarios.Tests.Playback;

/// <summary>実曲・起動設定と、再生前に確定する保全期待値を所有します。</summary>
internal sealed class PlaybackScenarioFixture : IAsyncDisposable
{
    private readonly ScenarioStartupFixture startup;
    /// <summary>実App専有の保存領域です。</summary>
    internal string Profile { get; }
    /// <summary>起動時に通常走査する譜面rootです。</summary>
    internal string Root { get; }
    /// <summary>最初に表activationするBMSです。</summary>
    internal string Bms { get; }
    /// <summary>通常Nextで到達する別音源のBMSです。</summary>
    internal string NextBms { get; }
    /// <summary>通常Dropで発見する保留packageの元directoryです。</summary>
    internal string Package { get; }
    /// <summary>相対WAVを参照する入れ子bmsonです。</summary>
    internal string Preview { get; }
    /// <summary>一時試聴で用いる既知の配置先です。</summary>
    internal string Destination { get; }
    /// <summary>実App起動前に確定した通常譜面と音源の保全期待値です。</summary>
    internal IReadOnlyDictionary<string, byte[]> RootBytes { get; }
    /// <summary>開始前の元packageの全ファイルbytesです。</summary>
    internal IReadOnlyDictionary<string, byte[]> SourceBytes { get; }
    /// <summary>開始前の配置先の非所有ファイルbytesです。</summary>
    internal IReadOnlyDictionary<string, byte[]> DestinationBytes { get; }
    /// <summary>fixtureが所有する更新応答URLです。</summary>
    internal string UpdateUrl => startup.UpdateManifestUrl;

    /// <summary>通常配置と入れ子入力、保全対象、内蔵再生設定を実App起動前に作ります。</summary>
    internal PlaybackScenarioFixture(TestWorkspace workspace, bool temporaryPlayback = false)
    {
        Profile = workspace.GetPath("profile");
        Root = workspace.GetPath("library");
        Directory.CreateDirectory(Root);
        Bms = Path.Combine(Root, "a.bms");
        NextBms = Path.Combine(Root, "b.bms");
        File.WriteAllText(Bms, BmsText("A", "a.wav"));
        File.WriteAllText(NextBms, BmsText("B", "b.wav"));
        WriteWave(Path.Combine(Root, "a.wav"), 8192, -4096);
        WriteWave(Path.Combine(Root, "b.wav"), 4096, 12288);
        Package = workspace.GetPath("source/package");
        Preview = Path.Combine(Package, "nested", "preview.bmson");
        Directory.CreateDirectory(Path.GetDirectoryName(Preview) ?? throw new InvalidOperationException());
        Directory.CreateDirectory(Path.Combine(Package, "audio"));
        File.WriteAllText(Path.Combine(Package, "parent.bms"), BmsText("Preview parent", "audio/a.wav"));
        File.WriteAllText(Preview, """
            {"version":"1.0.0","info":{"title":"Preview nested","artist":"Playback fixture","init_bpm":120,"resolution":240,"mode_hint":"beat-7k"},"sound_channels":[{"name":"../audio/a.wav","notes":[{"x":1,"y":0,"l":0,"c":false},{"x":1,"y":960,"l":0,"c":false},{"x":1,"y":3840,"l":0,"c":false}]}]}
            """);
        WriteWave(Path.Combine(Package, "audio", "a.wav"), 8192, -4096);
        File.WriteAllText(Path.Combine(Package, "keep.txt"), "source owned data");
        // 一時試聴の宛先は起動走査で所持譜面を得る既知directoryに限定します。
        Destination = temporaryPlayback ? Root : workspace.GetPath("preview-destination");
        Directory.CreateDirectory(Path.Combine(Destination, "nested"));
        File.WriteAllText(Path.Combine(Destination, "nested", "preview.bmson"),
            """{"version":"1.0.0","info":{"title":"Existing nested","init_bpm":120,"resolution":240,"mode_hint":"beat-7k"},"sound_channels":[]} """);
        File.WriteAllText(Path.Combine(Destination, "keep.txt"), "destination owned data");
        RootBytes = CaptureFiles(Root);
        SourceBytes = CaptureFiles(Package);
        DestinationBytes = CaptureFiles(Destination);
        startup = ScenarioStartupFixture.Create(Path.Combine(Profile, "config", "user.config"), Root, settings =>
        {
            settings.UsePlayeruBMplay = settings.UsePlayerBMIIDXView = settings.UsePlayerLR2body = false;
            settings.AutoInstallAfterAutomaticEstimation = false;
            settings.DeletePendingPackageSourceAfterInstall = false;
            settings.SinglePlayMode = true;
            settings.RepeatPlayMode = settings.FolderSkipPlayMode = false;
            settings.PlayerSampleRate = SampleRate.SAMPLE_RATE_48000Hz;
            settings.uBMplayVolume = 100;
        });
    }

    private static string BmsText(string title, string wave) =>
        $"#PLAYER 1\n#TITLE {title}\n#ARTIST Playback fixture\n#BPM 120\n#PLAYLEVEL 1\n#RANK 2\n#WAV01 {wave}\n#00011:01\n#00111:01\n";

    private static void WriteWave(string path, short left, short right)
    {
        const int rate = 48000;
        const int frames = rate / 2;
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 4);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)2); writer.Write(rate); writer.Write(rate * 4);
        writer.Write((ushort)4); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(frames * 4);
        for (int index = 0; index < frames; index++) { writer.Write(left); writer.Write(right); }
    }

    /// <summary>相対配置と全bytesを独立した保全期待値として捕捉します。</summary>
    internal static IReadOnlyDictionary<string, byte[]> CaptureFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    public ValueTask DisposeAsync() => startup.DisposeAsync();
}
