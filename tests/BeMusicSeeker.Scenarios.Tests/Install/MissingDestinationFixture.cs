using System.IO;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Scenarios.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Scenarios;

namespace BeMusicSeeker.Scenarios.Tests.Install;

/// <summary>消失先の再選択に必要な利用者所有入力と保存済み利用者列を、起動前だけに用意します。</summary>
internal sealed class MissingDestinationFixture : IAsyncDisposable
{
    private readonly ScenarioStartupFixture startup;

    /// <summary>既存作品・無関係作品・差分の独立bytesと保存期待値を固定します。</summary>
    internal MissingDestinationFixture(TestWorkspace workspace)
    {
        Profile = workspace.GetPath("profile");
        Database = workspace.GetPath("profile/data/song.db");
        Library = workspace.GetPath("library");
        Original = workspace.GetPath("library/Original");
        Renamed = workspace.GetPath("library/Renamed");
        Source = workspace.GetPath("user-input/difference");
        Existing = Chart("owned.bms", "SC05 Existing", "owned.wav");
        Sentinel = Chart("sentinel.bms", "SC05 Sentinel", "sentinel.wav");
        Difference = Chart("difference.bms", "SC05 Difference", "difference.wav");
        ExistingFiles = [Existing, FileValue("owned.wav", Wave(1))];
        SentinelFiles = [Sentinel, FileValue("sentinel.wav", Wave(2))];
        DifferenceFiles = [Difference, FileValue("difference.wav", Wave(3))];
        Write(Original, ExistingFiles);
        Write(Path.Combine(Library, "Sentinel"), SentinelFiles);
        Write(Source, DifferenceFiles);
        var scope = new ApplicationDatabaseScope();
        string executable = ScenarioApplicationPaths.ExecutablePath;
        var paths = ApplicationPathSnapshot.FromLayout(executable, Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("実行体の親がありません。"), Profile,
            ScenarioApplicationPaths.LanguageDirectory, workspace.GetPath("temp"), workspace.GetPath("legacy"));
        StandaloneLibraryDatabase.EnsurePortableSongDb(scope, paths);
        using (LR2SongDBExtended db = scope.OpenSongWrite(Database))
        {
            foreach ((string directory, MissingDestinationFile chart) in new[] { (Original, Existing), (Path.Combine(Library, "Sentinel"), Sentinel) })
            {
                string path = Path.Combine(directory, chart.Name);
                if (!Lr2SongFolderParentNormalizer.TryComputeExpectedHashes(path, out string folder, out string parent))
                { throw new InvalidOperationException("保存済み入力の親を構成できません。"); }
                db.Execute("INSERT INTO song(path,hash,title,artist,folder,parent,tag,favorite,adddate,date,txt,mode,level,judge) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    path, chart.Md5, chart.Title, "SC05 Artist", folder, parent, "利用者タグ", 3, 123456789,
                    checked((int)new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds()), 0, 7, 1, 2);
                db.Execute("INSERT INTO chart_digest_map(md5,sha256) VALUES(?,?)", chart.Md5, chart.Sha256);
                db.Execute("INSERT INTO maintenance(path,hash,encoding,is_encoding_fixed,wav_files_defined,wav_files_existing,bga_files_defined,bga_files_existing,movie_files_defined,movie_files_existing,is_stagefile_defined,is_banner_defined,is_backbmp_defined) VALUES(?,?,'utf-8',0,1,1,0,0,0,0,0,0,0)", path, chart.Md5);
            }
        }
        startup = ScenarioStartupFixture.Create(workspace.GetPath("profile/config/user.config"), Library, settings =>
        {
            settings.AutoInstallAfterAutomaticEstimation = false;
            settings.KeepInstallablePackagesPending = true;
            settings.DeletePendingPackageSourceAfterInstall = false;
            settings.ShowDiffBMSInstallConfirmMsg = false;
            settings.ShowNewPackageInstallConfirmMsg = false;
        });
    }

    /// <summary>実アプリ専有の保存profileです。</summary>
    internal string Profile { get; }
    /// <summary>独立readonly照合のDBです。</summary>
    internal string Database { get; }
    /// <summary>唯一の登録rootです。</summary>
    internal string Library { get; }
    /// <summary>手動指定後に消失させる既存作品です。</summary>
    internal string Original { get; }
    /// <summary>外部改名先かつ再選択する既存作品です。</summary>
    internal string Renamed { get; }
    /// <summary>登録root・管理tempの外にある利用者入力です。</summary>
    internal string Source { get; }
    /// <summary>実Dropする差分譜面の絶対pathです。</summary>
    internal string InputChart => Path.Combine(Source, Difference.Name);
    /// <summary>本番startupが取得する実loopback URLです。</summary>
    internal string UpdateManifestUrl => startup.UpdateManifestUrl;
    /// <summary>既存所持譜面の固定期待値です。</summary>
    internal MissingDestinationFile Existing { get; }
    /// <summary>対象外所持譜面の固定期待値です。</summary>
    internal MissingDestinationFile Sentinel { get; }
    /// <summary>差分譜面の固定期待値です。</summary>
    internal MissingDestinationFile Difference { get; }
    /// <summary>既存作品の全ファイルです。</summary>
    internal IReadOnlyList<MissingDestinationFile> ExistingFiles { get; }
    /// <summary>対象外作品の全ファイルです。</summary>
    internal IReadOnlyList<MissingDestinationFile> SentinelFiles { get; }
    /// <summary>差分入力の全ファイルです。</summary>
    internal IReadOnlyList<MissingDestinationFile> DifferenceFiles { get; }
    /// <summary>実アプリ回収後にHTTP接続を回収します。</summary>
    public ValueTask DisposeAsync() => startup.DisposeAsync();

    private static MissingDestinationFile Chart(string name, string title, string resource)
        => FileValue(name, Encoding.UTF8.GetBytes($"#PLAYER 1\r\n#TITLE {title}\r\n#ARTIST SC05 Artist\r\n#BPM 120\r\n#PLAYLEVEL 1\r\n#RANK 2\r\n#WAV01 {resource}\r\n#00111:01\r\n"), title);

    private static MissingDestinationFile FileValue(string name, byte[] bytes, string? title = null)
        => new(name, bytes, Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA256.HashData(bytes)), title);

    private static void Write(string directory, IEnumerable<MissingDestinationFile> files)
    {
        Directory.CreateDirectory(directory);
        foreach (MissingDestinationFile file in files) { File.WriteAllBytes(Path.Combine(directory, file.Name), file.Bytes.ToArray()); }
    }

    private static byte[] Wave(short sample)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(38);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(2); writer.Write(sample);
        return stream.ToArray();
    }
}

/// <summary>製品parserに依存しない、入力bytes・hash・titleの宣言です。</summary>
internal sealed record MissingDestinationFile(string Name, ReadOnlyMemory<byte> Bytes, string Md5, string Sha256, string? Title);
