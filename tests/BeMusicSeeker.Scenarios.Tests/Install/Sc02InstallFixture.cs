using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Scenarios.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Scenarios.Tests.Install;

/// <summary>SC02 の起動前入力と、導入段階ごとの独立した FS・DB 期待値を構築します。</summary>
/// <remarks>実アプリと Workspace の寿命は呼出元が所有します。起動後に入力や DB を補充しません。</remarks>
internal sealed class Sc02InstallFixture : IAsyncDisposable
{
    private readonly ScenarioStartupFixture startup;
    private readonly IReadOnlyDictionary<Sc02InstallPhase, Sc02PhaseExpectation> expectations;

    private Sc02InstallFixture(TestWorkspace workspace)
    {
        ProfileDirectory = workspace.GetPath("profile");
        SettingsPath = workspace.GetPath("profile/config/user.config");
        SongDatabasePath = workspace.GetPath("profile/data/song.db");
        LibraryRoot = workspace.GetPath("library");
        InstalledWorkDirectory = workspace.GetPath("library/SC02Work");
        ChildDirectory = workspace.GetPath("library/SC02Work/Child");
        InitialArchivePath = workspace.GetPath("sources/initial.zip");
        DifferenceSourcePath = workspace.GetPath("sources/difference");
        NextDifferencePath = workspace.GetPath("sources/next.bms");
        UnrelatedPendingSourcePath = workspace.GetPath("sources/unrelated");

        Sc02FileManifest existingChart = Bms("Existing/owned.bms", "#PLAYER 1\r\n#TITLE SC02 Existing\r\n#ARTIST SC02 Existing Artist\r\n#BPM 120\r\n#PLAYLEVEL 1\r\n#RANK 2\r\n#WAV01 keep.wav\r\n#00111:01\r\n");
        ExistingFiles = Array.AsReadOnly(new[] { existingChart, FileValue("Existing/keep.wav", Wave(1)) });
        ExistingChart = Chart(existingChart, "SC02 Existing", 1);

        // 親 preview で一式を保持し、通常音源は Child だけが所有するため資源付き親子の禁止条件へ入りません。
        Sc02FileManifest parent = Bms("parent.bms", "#PLAYER 1\r\n#TITLE SC02 Work\r\n#ARTIST SC02 Artist\r\n#BPM 120\r\n#PLAYLEVEL 2\r\n#RANK 2\r\n#PREVIEW preview.wav\r\n#WAV01 Child/base.wav\r\n#WAV02 Child/shared.wav\r\n#00111:0102\r\n");
        Sc02FileManifest child = Bms("Child/child.bms", "#PLAYER 1\r\n#TITLE SC02 Work\r\n#ARTIST SC02 Artist\r\n#BPM 120\r\n#PLAYLEVEL 3\r\n#RANK 2\r\n#WAV01 base.wav\r\n#WAV02 shared.wav\r\n#00111:0102\r\n");
        InitialArchiveFiles = Array.AsReadOnly(new[]
        {
            parent, child, FileValue("preview.wav", Wave(2)), FileValue("Child/base.wav", Wave(3)), FileValue("Child/shared.wav", Wave(4))
        });
        InitialInstalledCharts = Array.AsReadOnly(new[]
        {
            ExistingChart, Chart(UnderWork(parent), "SC02 Work", 2), Chart(UnderWork(child), "SC02 Work", 2)
        });

        Sc02FileManifest diffA = Bms("diff-a.bms", "#PLAYER 1\r\n#TITLE SC02 Work\r\n#ARTIST SC02 Artist\r\n#BPM 120\r\n#PLAYLEVEL 4\r\n#RANK 2\r\n#WAV01 base.wav\r\n#WAV02 new.wav\r\n#00111:0102\r\n");
        Sc02FileManifest diffB = Bms("diff-b.bms", "#PLAYER 1\r\n#TITLE SC02 Work\r\n#ARTIST SC02 Artist\r\n#BPM 120\r\n#PLAYLEVEL 5\r\n#RANK 2\r\n#WAV01 shared.wav\r\n#WAV02 new.wav\r\n#00111:0102\r\n");
        DifferenceFiles = Array.AsReadOnly(new[] { diffA, diffB, FileValue("new.wav", Wave(5)) });
        DifferenceCharts = Array.AsReadOnly(new[] { Chart(diffA, "SC02 Work", 2), Chart(diffB, "SC02 Work", 2) });
        DifferenceInstalledCharts = Array.AsReadOnly(InitialInstalledCharts.Concat(new[]
        {
            Chart(UnderChild(diffA), "SC02 Work", 2), Chart(UnderChild(diffB), "SC02 Work", 2)
        }).ToArray());
        NextDifference = Bms("next.bms", "#PLAYER 1\r\n#TITLE SC02 Work\r\n#ARTIST SC02 Artist\r\n#BPM 120\r\n#PLAYLEVEL 6\r\n#RANK 2\r\n#WAV01 Child/new.wav\r\n#00111:01\r\n");
        NextDifferenceChart = Chart(NextDifference, "SC02 Work", 1);
        UnrelatedPendingFiles = Array.AsReadOnly(new[]
        {
            Bms("unrelated.bms", "#PLAYER 1\r\n#TITLE SC02 Unrelated Pending\r\n#ARTIST Other Artist\r\n#BPM 120\r\n#PLAYLEVEL 1\r\n#RANK 2\r\n#WAV01 absent-unrelated.wav\r\n#00111:01\r\n"),
            FileValue("keep.txt", Encoding.UTF8.GetBytes("SC02 unrelated user-owned source\n"))
        });

        IReadOnlyList<Sc02FileManifest> initialFiles = Array.AsReadOnly(ExistingFiles.Concat(InitialArchiveFiles.Select(UnderWork)).ToArray());
        IReadOnlyList<Sc02FileManifest> installedFiles = Array.AsReadOnly(initialFiles.Concat(DifferenceFiles.Select(UnderChild)).ToArray());
        string[] unrelatedOnly = [UnrelatedPendingSourcePath];
        expectations = new Dictionary<Sc02InstallPhase, Sc02PhaseExpectation>
        {
            [Sc02InstallPhase.Startup] = new(ExistingFiles, Array.AsReadOnly(new[] { ExistingChart }), Array.AsReadOnly(unrelatedOnly), false),
            [Sc02InstallPhase.InitialInstalled] = new(initialFiles, InitialInstalledCharts, Array.AsReadOnly(unrelatedOnly), false),
            [Sc02InstallPhase.DifferencePending] = new(initialFiles, InitialInstalledCharts, Array.AsReadOnly(new[] { UnrelatedPendingSourcePath, DifferenceSourcePath }), false),
            [Sc02InstallPhase.DifferenceInstalled] = new(installedFiles, DifferenceInstalledCharts, Array.AsReadOnly(unrelatedOnly), true),
            [Sc02InstallPhase.NextDifferencePending] = new(installedFiles, DifferenceInstalledCharts, Array.AsReadOnly(new[] { UnrelatedPendingSourcePath, NextDifferencePath }), true),
            // 再起動では現物が残る利用者所有の2入力について登録を保証し、候補・指定先や管理一時入力の復元は要求しません。
            [Sc02InstallPhase.Restarted] = new(installedFiles, DifferenceInstalledCharts, Array.AsReadOnly(new[] { UnrelatedPendingSourcePath, NextDifferencePath }), true)
        };

        WriteFiles(LibraryRoot, ExistingFiles);
        WriteFiles(DifferenceSourcePath, DifferenceFiles);
        WriteFiles(UnrelatedPendingSourcePath, UnrelatedPendingFiles);
        WriteFiles(Path.GetDirectoryName(NextDifferencePath) ?? throw new InvalidOperationException("入力の親がありません。"), [NextDifference]);
        InitialArchive = FileValue("initial.zip", BuildArchive(InitialArchiveFiles));
        File.WriteAllBytes(InitialArchivePath, InitialArchive.RawBytes.ToArray());
        SeedPendingRegistration(workspace);
        startup = ScenarioStartupFixture.Create(SettingsPath, LibraryRoot, settings =>
        {
            settings.FolderNameFormat = "SC02Work";
            settings.AutoInstallAfterAutomaticEstimation = false;
            settings.KeepInstallablePackagesPending = false;
            settings.DeletePendingPackageSourceAfterInstall = false;
            settings.ShowDiffBMSInstallConfirmMsg = false;
            settings.ShowNewPackageInstallConfirmMsg = false;
        });
    }

    /// <summary>実アプリだけが変更する設定・DB・ログの配置です。</summary>
    internal string ProfileDirectory { get; }
    /// <summary>専有の起動前設定ファイルです。</summary>
    internal string SettingsPath { get; }
    /// <summary>読取り専用 oracle の対象 DB です。</summary>
    internal string SongDatabasePath { get; }
    /// <summary>このケースで登録する唯一の root です。</summary>
    internal string LibraryRoot { get; }
    /// <summary>通常の FolderNameFormat から決まる親作品の配置です。</summary>
    internal string InstalledWorkDirectory { get; }
    /// <summary>手動 Set へ渡す、初回導入で作られた既知の実 Child です。</summary>
    internal string ChildDirectory { get; }
    /// <summary>最初の実 Drop へ渡す、保存し続ける元 ZIP です。</summary>
    internal string InitialArchivePath { get; }
    /// <summary>2譜面と new.wav を含む、手動導入まで保留する入力元です。</summary>
    internal string DifferenceSourcePath { get; }
    /// <summary>親の更新済み祖先資源索引を再走査なしで使う、別 hash の次差分です。</summary>
    internal string NextDifferencePath { get; }
    /// <summary>初期 install 行で復元する、root 外の利用者所有の無関係な保留元です。</summary>
    internal string UnrelatedPendingSourcePath { get; }
    /// <summary>無関係な保留元の初期登録と再起動後に保持する delete_parent の独立期待値です。</summary>
    internal const int UnrelatedPendingDeleteParent = 0;
    /// <summary>末尾 Drop で登録する利用者所有の単独次差分に期待する delete_parent です。</summary>
    internal const int NextDifferenceDeleteParent = 0;
    /// <summary>標準起動の更新確認へ渡す loopback URL です。</summary>
    internal string UpdateManifestUrl => startup.UpdateManifestUrl;
    /// <summary>初回 ZIP を作る前に宣言した作品内の相対 path と bytes・hash です。</summary>
    internal IReadOnlyList<Sc02FileManifest> InitialArchiveFiles { get; }
    /// <summary>開始前に作成した元 ZIP の bytes と hash です。</summary>
    internal Sc02FileManifest InitialArchive { get; }
    /// <summary>無関係な既存作品の root 相対ファイル集合です。</summary>
    internal IReadOnlyList<Sc02FileManifest> ExistingFiles { get; }
    /// <summary>実起動の走査で登録する無関係な既存譜面です。</summary>
    internal Sc02ChartManifest ExistingChart { get; }
    /// <summary>初回 ZIP 導入後の root 相対所持集合です。</summary>
    internal IReadOnlyList<Sc02ChartManifest> InitialInstalledCharts { get; }
    /// <summary>差分入力元に存在する全ファイルの相対 path と元 bytes・hash です。</summary>
    internal IReadOnlyList<Sc02FileManifest> DifferenceFiles { get; }
    /// <summary>保留・全宛先 Clear・明示 Search・一括 Set の対象となる元配置の2譜面です。</summary>
    internal IReadOnlyList<Sc02ChartManifest> DifferenceCharts { get; }
    /// <summary>手動導入後の root 相対所持集合です。</summary>
    internal IReadOnlyList<Sc02ChartManifest> DifferenceInstalledCharts { get; }
    /// <summary>次差分の元 bytes・hash です。導入せず親への自動推定を観測します。</summary>
    internal Sc02FileManifest NextDifference { get; }
    /// <summary>次差分の入力元における期待譜面値です。</summary>
    internal Sc02ChartManifest NextDifferenceChart { get; }
    /// <summary>すべての段階で保全する無関係な保留元の全ファイルです。</summary>
    internal IReadOnlyList<Sc02FileManifest> UnrelatedPendingFiles { get; }

    /// <summary>アプリの操作開始前だけ入力と専有設定・schema・無関係な保留登録を作成します。</summary>
    internal static Sc02InstallFixture Create(TestWorkspace workspace) => new(workspace);

    /// <summary>本番処理の結果を読まず、開始前に宣言した段階ごとの期待値を返します。</summary>
    internal Sc02PhaseExpectation GetExpectedState(Sc02InstallPhase phase) => expectations[phase];

    /// <summary>実アプリ終了後に起動用 HTTP 入力の待受を解放します。</summary>
    public ValueTask DisposeAsync() => startup.DisposeAsync();

    private void SeedPendingRegistration(TestWorkspace workspace)
    {
        var scope = new ApplicationDatabaseScope();
        string assets = Path.Combine(AppContext.BaseDirectory, "fixtures", "application");
        var paths = ApplicationPathSnapshot.FromLayout(Path.Combine(assets, "BeMusicSeeker.exe"),
            assets, ProfileDirectory, Path.Combine(assets, "lang"), workspace.GetPath("temp"), workspace.GetPath("legacy"));
        StandaloneLibraryDatabase.EnsurePortableSongDb(scope, paths);
        using LR2SongDBExtended db = scope.OpenSongWrite(SongDatabasePath);
        db.Execute("INSERT INTO install (path, delete_parent) VALUES (?, ?)", UnrelatedPendingSourcePath, UnrelatedPendingDeleteParent);
    }

    private static Sc02FileManifest Bms(string relativePath, string text) => FileValue(relativePath, Encoding.ASCII.GetBytes(text));

    private static Sc02FileManifest FileValue(string relativePath, byte[] bytes) => new(relativePath, bytes,
        Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private static Sc02ChartManifest Chart(Sc02FileManifest file, string title, int usedAudioCount)
        => new(file.RelativePath, title, file.Md5, file.Sha256, usedAudioCount);

    private static Sc02FileManifest UnderWork(Sc02FileManifest file) => file with { RelativePath = "SC02Work/" + file.RelativePath };

    private static Sc02FileManifest UnderChild(Sc02FileManifest file) => file with { RelativePath = "SC02Work/Child/" + file.RelativePath };

    private static void WriteFiles(string root, IEnumerable<Sc02FileManifest> files)
    {
        foreach (Sc02FileManifest file in files)
        {
            string path = Path.Combine(root, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("入力の親がありません。"));
            File.WriteAllBytes(path, file.RawBytes.ToArray());
        }
    }

    private static byte[] BuildArchive(IReadOnlyList<Sc02FileManifest> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (Sc02FileManifest file in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry("SC02Work/" + file.RelativePath, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 2, 3, 4, 6, TimeSpan.Zero);
                using Stream stream = entry.Open();
                stream.Write(file.RawBytes.Span);
            }
        }
        return output.ToArray();
    }

    private static byte[] Wave(int variant)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        const int samples = 80;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * sizeof(short));
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * sizeof(short));
        for (int index = 0; index < samples; index++) { writer.Write((short)(index % 8 < 4 ? 1024 * variant : -1024 * variant)); }
        writer.Flush();
        return output.ToArray();
    }
}

/// <summary>独立期待値を選ぶ利用者フローの段階です。Clear・Search・Set は DifferencePending の同じ保存結果を保ちます。</summary>
internal enum Sc02InstallPhase
{
    /// <summary>実起動の初期走査完了後です。</summary>
    Startup,
    /// <summary>初回 ZIP の通常導入完了後です。</summary>
    InitialInstalled,
    /// <summary>複数譜面差分の保留登録後で、導入先操作によって FS・所持は変えません。</summary>
    DifferencePending,
    /// <summary>既知 Child への手動導入完了後です。</summary>
    DifferenceInstalled,
    /// <summary>親候補を自動推定する次差分の保留登録後です。</summary>
    NextDifferencePending,
    /// <summary>通常終了後の次回起動で、導入済み結果と利用者所有の2入力の保留登録・元 bytes を確認します。</summary>
    Restarted
}

/// <summary>開始前に固定する一ファイルの元 bytes と hash です。</summary>
/// <param name="RelativePath">対応する入力元または library root からの相対 path です。</param>
/// <param name="RawBytes">期待する元 bytes です。</param>
/// <param name="Md5">元 bytes の MD5 です。</param>
/// <param name="Sha256">元 bytes の SHA-256 です。</param>
internal sealed record Sc02FileManifest(string RelativePath, ReadOnlyMemory<byte> RawBytes, string Md5, string Sha256);

/// <summary>parser から逆生成せず宣言した譜面の保存期待値です。</summary>
/// <param name="RelativePath">対応する入力元または library root からの譜面相対 path です。</param>
/// <param name="Title">期待する title です。</param>
/// <param name="Md5">元 bytes の MD5 です。</param>
/// <param name="Sha256">元 bytes の SHA-256 です。</param>
/// <param name="UsedAudioCount">正常に使用する実 WAV の数です。発見専用 preview は数えません。</param>
internal sealed record Sc02ChartManifest(string RelativePath, string Title, string Md5, string Sha256, int UsedAudioCount);

/// <summary>一段階の保存結果と元入力の消費条件を、実操作と独立して保持します。</summary>
/// <param name="LibraryFiles">library root の全期待ファイルです。</param>
/// <param name="Charts">root 相対の全所持譜面です。</param>
/// <param name="PendingSourcePaths">install 表に残す入力元の絶対 path です。</param>
/// <param name="DifferenceConsumed">差分元の全ファイルが実移動で消費された段階かを示します。</param>
internal sealed record Sc02PhaseExpectation(IReadOnlyList<Sc02FileManifest> LibraryFiles, IReadOnlyList<Sc02ChartManifest> Charts,
    IReadOnlyList<string> PendingSourcePaths, bool DifferenceConsumed);
