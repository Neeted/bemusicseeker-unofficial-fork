using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// 保守の再検査で読込みに失敗する譜面を、実ライブラリと実SQLiteの上に用意します。
/// 登録した後で一部のファイルを削除し、要求の順は名前の辞書順と異なる順にします。
/// ライブラリの窓口は件数だけを記録し、読取りや保守がモデルの窓口へ表示しないことを確かめられます。
/// </summary>
internal sealed class MaintenanceReadFailureFixture
{
    /// <summary>読込みに失敗する譜面の件数です。標本の上限（5件）を超える件数にします。</summary>
    internal const int FailureCount = 7;

    private MaintenanceReadFailureFixture(
        ApplicationDatabaseScope databaseScope, string databasePath, TestBmsLibrary library,
        FileDbReportRecordingDialogs libraryDialogs, ChartFile readable, IReadOnlyList<ChartFile> missing)
    {
        DatabaseScope = databaseScope;
        DatabasePath = databasePath;
        Library = library;
        LibraryDialogs = libraryDialogs;
        Readable = readable;
        Missing = missing;
    }

    internal ApplicationDatabaseScope DatabaseScope { get; }

    internal string DatabasePath { get; }

    internal TestBmsLibrary Library { get; }

    /// <summary>ライブラリへ注入した窓口です。モデルが同期の表示または投函で通知した場合だけ <see cref="FileDbReportRecordingDialogs.ModelMessages"/> が増えます。</summary>
    internal FileDbReportRecordingDialogs LibraryDialogs { get; }

    internal ChartFile Readable { get; }

    /// <summary>登録後にファイルを削除した譜面です。要求の順です。</summary>
    internal IReadOnlyList<ChartFile> Missing { get; }

    /// <summary>読込み失敗が起きる要求の順のパスです。</summary>
    internal IEnumerable<string> MissingPaths => Missing.Select(PathOf);

    /// <summary>このフィクスチャが作る譜面は必ず実パスを持つため、欠落は準備の誤りとして失敗にします。</summary>
    internal static string PathOf(ChartFile chart) => chart.Path ?? throw new InvalidOperationException("フィクスチャの譜面にはパスがある。");

    /// <summary>
    /// 保守の保存の失敗が、SQLiteの例外とトリガーが拒否した原因（文言）を、型と原因の連鎖として保っていることを確認します。
    /// </summary>
    internal static void AssertCommitFailure(Exception? failure)
    {
        Assert.IsNotNull(failure);
        bool found = false;
        for (Exception? current = failure; current != null; current = current.InnerException)
        {
            found |= current.GetType().Name.Contains("SQLite", StringComparison.Ordinal)
                && current.Message.Contains("forced maintenance commit failure", StringComparison.Ordinal);
        }
        Assert.IsTrue(found, "保存の失敗の型と原因の連鎖が保たれる: " + failure);
    }

    /// <summary>
    /// 読めない譜面を要求の順に <paramref name="failureCount"/> 件と、読める譜面1件の再検査の要求を作ります。
    /// 読める譜面は読めない譜面の途中へ置き、パスの辞書順とは逆になる要求の順で並べます。
    /// </summary>
    internal ChartResourceHealthRequest CreateRequest(int failureCount = FailureCount)
    {
        IEnumerable<ChartFile> charts = [.. Missing.Take(Math.Min(3, failureCount)), Readable, .. Missing.Skip(3).Take(Math.Max(0, failureCount - 3))];
        ChartOperationTarget[] targets = [.. charts.Select(chart => new ChartOperationTarget(
            chart, null, ChartOperationSourceScope.Library, isOwned: true, isPending: false, isPlaylistMissing: false,
            ChartOperationCapabilities.RunResourceHealthCheck))];
        Assert.IsTrue(ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest? request));
        Assert.IsNotNull(request);
        return request;
    }

    /// <summary>
    /// 読める譜面の保守行の保存を、SQLiteのトリガーで失敗させます。読取りは確定より前に全件終わるため、
    /// 読込み失敗を集めた後に確定だけが失敗する状況を決定的に作れます。
    /// </summary>
    internal void FailMaintenanceCommit()
    {
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(DatabaseScope, DatabasePath, db =>
        {
            db.Execute("CREATE TRIGGER reject_maintenance_insert BEFORE INSERT ON maintenance BEGIN SELECT RAISE(ABORT, 'forced maintenance commit failure'); END;");
            db.Execute("CREATE TRIGGER reject_maintenance_update BEFORE UPDATE ON maintenance BEGIN SELECT RAISE(ABORT, 'forced maintenance commit failure'); END;");
        });
    }

    /// <summary>
    /// 読込み失敗の標本の一行（パスと原因）を、要求の順の先頭から返します。原因はファイルを削除した状態の実際の読取りから得ます。
    /// </summary>
    internal IReadOnlyList<string> ExpectedSampleLines(int count = MaintenanceReadFailureCollector.MaximumSampleCount) =>
    [.. Missing.Take(count).Select(chart =>
    {
        Exception? failure = null;
        try { ChartFileContentReader.ReadBuffer(chart.Path); }
        catch (Exception exception) { failure = exception; }
        Assert.IsNotNull(failure, "削除済みの譜面の読取りは失敗する。");
        return chart.Path + " (" + failure.Message + ")";
    })];

    /// <summary>
    /// 読込み失敗の通知の本文を、リソースから組み立てた期待値として返します。表示の言語は現在の既定です。
    /// 一件は従来の個別の通知の本文で、複数件は件数の行と標本の行です。
    /// </summary>
    internal string ExpectedBody(int count = FailureCount)
    {
        if (count == 1)
        {
            Exception? failure = null;
            try { ChartFileContentReader.ReadBuffer(Missing[0].Path); }
            catch (Exception exception) { failure = exception; }
            Assert.IsNotNull(failure);
            return string.Format(BeMusicSeeker.Properties.Resources.Error_BmsLoadFailedSkip, Missing[0].Path, failure.Message);
        }
        List<string> lines = [string.Format(BeMusicSeeker.Properties.Resources.Notice_GroupedCount, count)];
        lines.AddRange(ExpectedSampleLines(Math.Min(count, MaintenanceReadFailureCollector.MaximumSampleCount)).Select(line => "- " + line));
        if (count > MaintenanceReadFailureCollector.MaximumSampleCount)
        {
            lines.Add("- ...");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>実ライブラリと実SQLiteの上に、読める譜面1件と、登録後に削除した譜面を用意して本体を実行します。</summary>
    internal static async Task RunAsync(Func<MaintenanceReadFailureFixture, Task> body)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var databaseScope = new ApplicationDatabaseScope();
        string root = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_MaintenanceReadFailure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "song.db");
        TestBmsLibrary? library = null;
        try
        {
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(databaseScope, databasePath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            });
            ChartFile Parse(string name)
            {
                string path = Path.Combine(root, name);
                File.WriteAllText(path, "#PLAYER 1\r\n#TITLE " + name + "\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
                return BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
            }
            // 辞書順は「要求の順」の逆になるよう、先頭ほど辞書順で後ろへ置きます。
            List<ChartFile> missing = [.. Enumerable.Range(0, FailureCount).Select(index => Parse((char)('z' - index) + "-missing-" + index + ".bms"))];
            ChartFile readable = Parse("m-readable.bms");
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), databasePath)
                .UpsertSongs([.. missing, readable]);
            var libraryDialogs = new FileDbReportRecordingDialogs();
            library = new TestBmsLibrary(databaseScope, databasePath, null, null, null, libraryDialogs)
            {
                BmsCharts = [.. missing, readable]
            };
            foreach (ChartFile chart in missing)
            {
                File.Delete(PathOf(chart));
            }
            // 要求には、ライブラリが所有する現在の譜面を使います。
            TestBmsLibrary current = library;
            ChartFile Owned(ChartFile chart) => current.BmsCharts.Single(owned => owned.Path == chart.Path);
            await body(new MaintenanceReadFailureFixture(
                databaseScope, databasePath, library, libraryDialogs, Owned(readable), [.. missing.Select(Owned)]))
                .ConfigureAwait(false);
        }
        finally
        {
            library?.RequestShutdown("maintenance-read-failure-fixture");
            Directory.Delete(root, recursive: true);
        }
    }
}
