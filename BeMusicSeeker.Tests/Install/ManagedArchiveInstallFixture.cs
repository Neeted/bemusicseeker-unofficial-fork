using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;

namespace BeMusicSeeker.Tests;

/// <summary>管理ZIP回帰だけが所有する小DB・固定候補・実ファイルを準備し、実日時復元の引数から展開先を捕捉します。</summary>
internal sealed class ManagedArchiveInstallFixture : IDisposable
{
    private readonly List<string> managedRoots = [];
    private readonly Queue<Func<Task>> background = new();

    internal ManagedArchiveInstallFixture(bool knownCandidate = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ManagedArchiveTests_" + Guid.NewGuid().ToString("N"));
        InstallRoot = Path.Combine(Root, "Library");
        Directory.CreateDirectory(InstallRoot);
        SongDb = Path.Combine(Root, "song.db");
        File.WriteAllBytes(SongDb, []);
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(SongDb, db =>
        {
            db.CreateTable<LR2SongDB.song>();
            db.CreateTable<LR2SongDB.folder>();
            db.CreateTable<LR2SongDBExtended.maintenance>();
            db.CreateTable<LR2SongDBExtended.bmson_song>();
        });
        CandidateDirectory = Path.Combine(InstallRoot, "Song");
        string[] charts = [];
        var resources = new Dictionary<string, IEnumerable<string>>();
        if (knownCandidate)
        {
            Directory.CreateDirectory(CandidateDirectory);
            string chart = Path.Combine(CandidateDirectory, "existing.bms");
            File.WriteAllText(chart, ChartText("Song", 1), Encoding.ASCII);
            File.WriteAllBytes(Path.Combine(CandidateDirectory, "sound.wav"), [1]);
            charts = [chart];
            resources[CandidateDirectory] = ["sound.wav"];
        }
        Options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = true,
            BMSInstallDir = InstallRoot,
            FolderNameFormat = "%TITLE%",
            KeepInstallablePackagesPending = false,
            PendingInstallEstimateMaxParallelPackages = 1
        };
        Files = new ArchiveInputFileMutationService(path =>
        {
            string? extractedRoot = Path.GetDirectoryName(path);
            if (extractedRoot != null && TempDirectoryPublisher.IsManagedPath(extractedRoot))
            {
                ExtractedRoots.Add(extractedRoot);
            }
        });
        Dialogs = new FileDbReportRecordingDialogs();
        Library = new TestBmsLibrary(SongDb, null, null, Files, Dialogs,
            new TestUiScheduler(() => null), () => Options,
            CapturedChartFileScanner.FromFixture(charts, resources, knownCandidate ? [CandidateDirectory] : []))
        {
            BmsCharts = [],
            BmsonCharts = [],
            SearchTargets = [InstallRoot],
            ChartPackagesPending = new ObservableCollection<ChartPackage>(),
            ChartPackagesInstalled = new ObservableCollection<ChartPackage>(),
            StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
        };
    }

    internal string Root { get; }
    internal string InstallRoot { get; }
    internal string SongDb { get; }
    internal string CandidateDirectory { get; }
    internal BmsLibraryOptionsSnapshot Options { get; }
    internal TestBmsLibrary Library { get; }
    internal FileDbReportRecordingDialogs Dialogs { get; }
    internal ArchiveInputFileMutationService Files { get; }
    internal HashSet<string> ExtractedRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>実初期化と受理済み背景更新を終え、固定候補の実索引から推定できる状態にします。</summary>
    internal async Task InitializeAsync()
    {
        Library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
        while (background.TryDequeue(out Func<Task>? work)) { await work(); }
    }

    /// <summary>全形態で同じ内容を作り、管理ZIPだけを現在の管理領域へ配置します。</summary>
    internal string CreateInput(string kind, string name = "Song", bool resources = true, bool chart = true)
    {
        string source = Path.Combine(Root, name + "-source");
        Directory.CreateDirectory(source);
        if (chart) { File.WriteAllText(Path.Combine(source, "chart.bms"), ChartText(name, 12), Encoding.ASCII); }
        else { File.WriteAllText(Path.Combine(source, "readme.txt"), "no chart", Encoding.ASCII); }
        if (resources) { File.WriteAllBytes(Path.Combine(source, "sound.wav"), [1]); }
        if (kind == "folder") { return source; }
        string archiveRoot = Root;
        if (kind == "managed")
        {
            archiveRoot = TempDirectoryPublisher.Get();
            managedRoots.Add(archiveRoot);
        }
        string archive = Path.Combine(archiveRoot, name + ".zip");
        ZipFile.CreateFromDirectory(source, archive);
        return archive;
    }

    private static string ChartText(string title, int level) =>
        "#PLAYER 1\r\n#TITLE " + title + "\r\n#ARTIST Test\r\n#BPM 120\r\n#PLAYLEVEL " + level
        + "\r\n#WAV01 sound.wav\r\n#00111:01\r\n";

    public void Dispose()
    {
        Library.RequestShutdown("managed-archive-fixture");
        foreach (string path in ExtractedRoots) { TempDirectoryPublisher.TryDeleteManagedPath(path); }
        foreach (string path in managedRoots) { TempDirectoryPublisher.TryDeleteManagedPath(path); }
        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>実ファイル操作を委譲し、アーカイブの必須日時復元だけを観測・失敗注入する局所境界です。</summary>
internal sealed class ArchiveInputFileMutationService(Action<string> observeTimestamp) : IFileMutationService
{
    private readonly ResilientFileMutationService inner = new();
    internal Action<string>? BeforeTimestamp { get; set; }
    public void EnsureDirectory(string path, FileMutationOptions? options = null) => inner.EnsureDirectory(path, options);
    public void MoveFile(string source, string destination, bool overwrite, FileMutationOptions? options = null) => inner.MoveFile(source, destination, overwrite, options);
    public void MoveDirectory(string source, string destination, bool overwrite, FileMutationOptions? options = null) => inner.MoveDirectory(source, destination, overwrite, options);
    public void CopyFile(string source, string destination, bool overwrite, FileMutationOptions? options = null) => inner.CopyFile(source, destination, overwrite, options);
    public void CopyDirectory(string source, string destination, bool overwrite, FileMutationOptions? options = null) => inner.CopyDirectory(source, destination, overwrite, options);
    public void DeleteFileDirect(string path, FileMutationOptions? options = null) => inner.DeleteFileDirect(path, options);
    public void DeleteFileShell(string path, UIOption ui, RecycleOption recycle, FileMutationOptions? options = null) => inner.DeleteFileShell(path, ui, recycle, options);
    public void DeleteDirectoryDirect(string path, bool recursive, FileMutationOptions? options = null) => inner.DeleteDirectoryDirect(path, recursive, options);
    public void DeleteDirectoryShell(string path, UIOption ui, RecycleOption recycle, FileMutationOptions? options = null) => inner.DeleteDirectoryShell(path, ui, recycle, options);
    public void SetTimestamps(string path, bool isDirectory, DateTime? creation, DateTime? modified, FileMutationOptions? options = null)
    {
        if (!isDirectory && modified.HasValue)
        {
            observeTimestamp(path);
            BeforeTimestamp?.Invoke(path);
        }
        inner.SetTimestamps(path, isDirectory, creation, modified, options);
    }
}
