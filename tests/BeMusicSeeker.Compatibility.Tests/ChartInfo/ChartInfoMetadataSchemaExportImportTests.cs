using System.Globalization;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using SQLite;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;
using static BeMusicSeeker.TestSupport.Components.ChartInfoMetadataTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 譜面情報のスキーマ、bundle、起動時取込み、カタログ反映の境界を確認します。
/// </summary>
[TestClass]
public sealed class ChartInfoMetadataSchemaExportImportTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実7zへ出力したrootのmetadata DBを起動入口から取り込み、保存行の復元とarchive移動までを接続して配布形式の不整合を検出する。</summary>
    /// <remarks>実7zが必要な配布・起動接続。専用環境で実行し、未導入時は失敗とする。他caseの注入抽出ではarchive形式自体を保証しない。</remarks>
    [TestMethod]
    [TestCategory("Suite.chart-info-archive")]
    [TestCategory("EnvironmentIntegration")]
    public async Task ChartInfoExport_CreatesArchiveWithRootMetadataDbAndStartupImporterCanImport()
    {
        string sevenZipPath = ResolveInstalledSevenZipPath();
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = workspace.RootDirectory;
        string songDbPath = workspace.GetPath("song.db");
        File.WriteAllBytes(songDbPath, []);
        string md5 = new('a', 32);
        string sha = new('1', 64);
        using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
        {
            BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
            BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
            songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
        }

        string outputPath = Path.Combine(tempRootPath, "chart-info-metadata.db");
        string archivePath = Path.Combine(tempRootPath, "chart-info-metadata.7z");
        string exporter = Path.Combine(AppContext.BaseDirectory, "tools", "chart-info-export", "ChartInfoExport.exe");
        Assert.IsTrue(File.Exists(exporter), "公式chart-info-exportのbuild出力がありません。");
        ProcessCompletion result = await ChartInfoToolTestSupport.RunAsync(exporter,
            ["--source", songDbPath, "--out", outputPath, "--archive-out", archivePath, "--sevenzip", sevenZipPath],
            tempRootPath, tempRootPath, workspace.PreserveArtifacts);

        Assert.IsTrue(File.Exists(outputPath));
        Assert.IsTrue(File.Exists(archivePath));
        StringAssert.Contains(result.StandardOutput, "archive=\"" + archivePath + "\"");
        Match archiveBytes = Regex.Match(result.StandardOutput, @"archiveBytes=(\d+)");
        Assert.IsTrue(archiveBytes.Success, result.StandardOutput);
        long reportedBytes = long.Parse(archiveBytes.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.IsTrue(reportedBytes > 0L);
        Assert.AreEqual(new FileInfo(archivePath).Length, reportedBytes);
        using (var exported = new SQLiteConnection(outputPath, SQLiteOpenFlags.ReadOnly))
        {
            Assert.AreEqual(1L, exported.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
        }
        ProcessCompletion listed = await ChartInfoToolTestSupport.RunAsync(sevenZipPath, ["l", archivePath], tempRootPath, tempRootPath, workspace.PreserveArtifacts);
        StringAssert.Contains(listed.StandardOutput + Environment.NewLine + listed.StandardError, "chart-info-metadata.db");

        string appBaseDirectory = Path.Combine(tempRootPath, "app");
        Directory.CreateDirectory(appBaseDirectory);
        string appArchivePath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
        File.Copy(archivePath, appArchivePath);
        string importDbDirectoryPath = Path.Combine(tempRootPath, "import");
        Directory.CreateDirectory(importDbDirectoryPath);
        string importDbPath = Path.Combine(importDbDirectoryPath, "song.db");
        File.Copy(songDbPath, importDbPath);
        using (LR2SongDBExtended importDb = databaseScope.OpenSongWrite(importDbPath))
        {
            importDb.Execute("DELETE FROM chart_info;");
            importDb.Execute("DELETE FROM chart_digest_map;");
        }
        ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(
            appBaseDirectory,
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), importDbPath),
            null,
            createTempDirectory: () => Directory.CreateDirectory(Path.Combine(tempRootPath, "extracted")).FullName);

        using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(importDbPath);
        Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
        Assert.IsFalse(File.Exists(appArchivePath));
        string importedArchivePath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
        Assert.IsTrue(File.Exists(importedArchivePath));
        CollectionAssert.AreEqual(File.ReadAllBytes(archivePath), File.ReadAllBytes(importedArchivePath));
    }
}
