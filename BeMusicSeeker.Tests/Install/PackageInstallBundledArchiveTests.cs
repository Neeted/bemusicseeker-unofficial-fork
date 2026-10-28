using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>四つの独立譜面と十の付属資源を人工ZIPから実展開・通常推定・後段導入へ通し、未参照previewを含む全相対path/bytesとDB・所持集合を確認します。所持側は各譜面と同じメタデータの別hashと合成演奏資源です。</summary>
    [TestMethod]
    public async Task AutomaticContinuation_SupplementOnlyArchiveKeepsAllFourChartsAndTenAssets()
    {
        (byte[] archive, IReadOnlyDictionary<string, byte[]> expected) = CreateSupplementOnlyArchive();
        await VerifySupplementOnlyArchiveAsync(archive, expected);
    }

    /// <summary>同じ人工ZIPの四譜面・十資源を、推定と無関係な所持先への通常手動編集・導入で確認します。元ZIP・既存所持・無関係保留を保ち、全内容の配置と自動適用条件を分離します。</summary>
    [TestMethod]
    public async Task ManualInstall_SupplementOnlyArchiveKeepsAllFourChartsAndTenAssets()
    {
        (byte[] archive, IReadOnlyDictionary<string, byte[]> expected) = CreateSupplementOnlyArchive();
        await VerifySupplementOnlyArchiveManuallyAsync(archive, expected);
    }

    /// <summary>自動・手動で共用する、四つの不足音源参照と十の識別可能な補助資源を持つ人工ZIPと独立期待値を作ります。</summary>
    private static (byte[] Archive, IReadOnlyDictionary<string, byte[]> Expected) CreateSupplementOnlyArchive()
    {
        var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < 4; index++)
        {
            string chart = CreateValidBmsText("Song").Replace("sound.wav", "note" + index + ".wav")
                + "#GENRE variant" + index + "\n#PREVIEW trailer" + index + ".ogg\n#STAGEFILE stage.png\n#BACKBMP back.png\n#BANNER banner.png\n";
            expected.Add("chart" + index + ".bms", Encoding.ASCII.GetBytes(chart));
            expected.Add("trailer" + index + ".ogg", [(byte)(index + 2), 9]);
        }
        foreach (string name in new[] { "preview.ogg", "stage.png", "back.png", "banner.png", "cover.png", "unused.png" })
        { expected.Add(name, Encoding.ASCII.GetBytes("asset:" + name)); }
        using var content = new MemoryStream();
        using (var zip = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in expected)
            {
                using Stream stream = zip.CreateEntry("Package/" + name).Open();
                stream.Write(bytes);
            }
        }
        return (content.ToArray(), expected);
    }

    /// <summary>独立したZIP一覧を期待値として、通常の保留登録・導入先編集owner・実storeの手動導入を通します。所持先は異なるmetadataと独立音源だけで作り、自動推定の成立前提を使いません。</summary>
    internal static async Task VerifySupplementOnlyArchiveManuallyAsync(byte[] archiveBytes, IReadOnlyDictionary<string, byte[]> expected)
    {
        string[] sourceCharts = expected.Keys.Where(ChartFileKindResolver.IsSupportedChartFilePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.AreEqual(4, sourceCharts.Length);
        Assert.AreEqual(10, expected.Count - sourceCharts.Length);
        string ownedBody = CreateValidBmsText("Independent owned destination").Replace("#ARTIST Artist", "#ARTIST Manual owner")
            .Replace("sound.wav", "owned-only.wav");
        await using var fixture = new AutomaticContinuationFixture(false, false,
            destinationSounds: ["owned-only.wav"], installedChartBodies: [ownedBody]);
        string archive = Path.Combine(fixture.Root, "input.zip");
        File.WriteAllBytes(archive, archiveBytes);
        string unrelated = fixture.WriteInput("Unrelated", "other.bms", "unrelated");
        Dictionary<string, byte[]> unrelatedBytes = CaptureArchiveTree(unrelated);
        await fixture.InitializeAsync();
        Assert.IsFalse(fixture.Options.AutoInstallAfterAutomaticEstimation);
        Assert.IsTrue(fixture.Owner.Enqueue([unrelated]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        ChartPackage unrelatedPackage = fixture.Library.ChartPackagesPending.Single();
        PackageChartEntry unrelatedEntry = unrelatedPackage.ChartEntries.Single();
        // ChartFileの可変参照を期待値にせず、操作前の値と警告文字列をここで固定します。
        (string PackagePath, string Path, string Md5, ChartFileStatus Status, int? WAVHealth, string[] Warnings) unrelatedBefore = (PackagePath: unrelatedPackage.path, unrelatedEntry.Chart.Path, unrelatedEntry.Chart.Md5,
            unrelatedEntry.Chart.Status, unrelatedEntry.Chart.WAVHealth,
            Warnings: unrelatedEntry.Chart.Warnings.Select(warning => warning.Kind + "|" + warning.Message).ToArray());
        PackageChartInstallDestinationState unrelatedDestination = unrelatedEntry.CaptureInstallDestinationState();
        var ownedBefore = fixture.Library.BmsCharts.Select(chart => new
        {
            chart.Path,
            chart.Md5,
            chart.Title,
            chart.Artist,
            chart.RawTitle,
            chart.RawArtist,
            chart.Status,
            chart.WAVHealth
        }).ToArray();
        Dictionary<string, byte[]> destinationBefore = CaptureArchiveTree(fixture.Destination);
        Dictionary<string, string> songsBefore;
        string[] installBefore;
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            songsBefore = db.Table<LR2SongDB.song>().ToDictionary(row => row.path, row => JsonSerializer.Serialize(row), StringComparer.OrdinalIgnoreCase);
            installBefore = db.Table<LR2SongDBExtended.install>().Select(row => row.path + "|" + row.delete_parent).ToArray();
        }

        Assert.IsTrue(fixture.Owner.Enqueue([archive]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count, "自動後段は無効で、手動操作前は全四譜面を保留します。");
        AssertArchiveTree(destinationBefore, fixture.Destination);
        Assert.AreEqual(2, fixture.Library.ChartPackagesPending.Count);
        ChartPackage package = fixture.Library.ChartPackagesPending.Single(item => !ReferenceEquals(item, unrelatedPackage));
        Assert.IsTrue(Directory.Exists(package.path));
        Assert.AreEqual(4, package.ChartEntries.Count);
        CollectionAssert.AreEquivalent(sourceCharts, package.ChartEntries.Select(entry => Path.GetRelativePath(package.path, entry.Chart.Path)).ToArray());
        Assert.IsTrue(package.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
        Assert.IsTrue(package.ChartEntries.All(entry => ownedBefore.All(owned => owned.Md5 != entry.Chart.Md5)));
        Assert.IsTrue(package.ChartEntries.All(entry => ownedBefore.All(owned => owned.RawTitle != entry.Chart.RawTitle && owned.RawArtist != entry.Chart.RawArtist)),
            "所持先に元譜面のmetadataや演奏音源を合成して、自動推定の成功条件へ寄せません。");
        AssertArchiveTree(expected, package.path);
        using (LR2SongDBExtended beforeManual = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            CollectionAssert.AreEquivalent(new[] { unrelatedBefore.PackagePath, package.path },
                beforeManual.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
        }
        var manualOwner = new PendingPackageWorkflowOwner(() => fixture.Library, fixture.Library.OperationAdmission,
            new ChartMutationActivityOwner(), new NoOpChartMutationPlaybackPort(), new FileDbReportRecordingDialogs(),
            () => new InstallDestinationWorkflowSettingsSnapshot(false, false, false), new ArchiveVerificationShellGateway());
        PackageChartEntry target = package.ChartEntries.First();
        Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(new ChartOperationTarget(target.Chart, null,
            ChartOperationSourceScope.PendingPackage, isOwned: false, isPending: true, isPlaylistMissing: false,
            capabilities: ChartOperationCapabilities.UpdateInstallDestination, packageEntry: target), out PendingInstallDestinationEditRequest request));
        await manualOwner.SetPendingAsync(request, fixture.Destination);
        Assert.IsTrue(package.ChartEntries.All(entry => entry.Chart.InstallDestination == fixture.Destination));
        PendingPackageMutationResult result = await manualOwner.ManualInstallPackagesAsync([package]);
        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
        Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
        AssertArchiveTree(destinationBefore.Concat(expected).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase), fixture.Destination);
        CollectionAssert.AreEquivalent(sourceCharts.Select(relative => Path.Combine(fixture.Destination, relative)).ToArray(),
            fixture.Library.ChartPackagesInstalled.Single().ChartEntries.Select(entry => entry.Chart.Path).ToArray());
        CollectionAssert.AreEquivalent(ownedBefore.Select(chart => chart.Path).Concat(sourceCharts.Select(relative => Path.Combine(fixture.Destination, relative))).ToArray(),
            fixture.Library.BmsCharts.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(ownedBefore.Length + 4, fixture.Library.BmsCharts.Count);
        foreach (var owned in ownedBefore)
        {
            ChartFile current = fixture.Library.BmsCharts.Single(chart => chart.Path == owned.Path);
            Assert.AreEqual(owned.Md5, current.Md5);
            Assert.AreEqual(owned.Title, current.Title);
            Assert.AreEqual(owned.Artist, current.Artist);
            Assert.AreEqual(owned.Status, current.Status);
            Assert.AreEqual(owned.WAVHealth, current.WAVHealth);
        }
        Assert.AreSame(unrelatedPackage, fixture.Library.ChartPackagesPending.Single());
        Assert.AreEqual(unrelatedBefore.PackagePath, unrelatedPackage.path);
        Assert.AreEqual(unrelatedBefore.Path, unrelatedEntry.Chart.Path);
        Assert.AreEqual(unrelatedBefore.Md5, unrelatedEntry.Chart.Md5);
        Assert.AreEqual(unrelatedBefore.Status, unrelatedEntry.Chart.Status);
        Assert.AreEqual(unrelatedBefore.WAVHealth, unrelatedEntry.Chart.WAVHealth);
        CollectionAssert.AreEqual(unrelatedBefore.Warnings,
            unrelatedEntry.Chart.Warnings.Select(warning => warning.Kind + "|" + warning.Message).ToArray());
        PackageChartInstallDestinationState remainingDestination = unrelatedEntry.CaptureInstallDestinationState();
        Assert.AreEqual(unrelatedDestination.Destination, remainingDestination.Destination);
        Assert.AreEqual(unrelatedDestination.Title, remainingDestination.Title);
        Assert.AreEqual(unrelatedDestination.Artist, remainingDestination.Artist);
        CollectionAssert.AreEqual(unrelatedDestination.Suggestions.ToArray(), remainingDestination.Suggestions.ToArray());
        AssertArchiveTree(unrelatedBytes, unrelated);
        using LR2SongDBExtended after = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        CollectionAssert.AreEquivalent(installBefore, after.Table<LR2SongDBExtended.install>().Select(row => row.path + "|" + row.delete_parent).ToArray());
        Assert.AreEqual(songsBefore.Count + 4, after.Table<LR2SongDB.song>().Count());
        foreach ((string path, string row) in songsBefore) { Assert.AreEqual(row, JsonSerializer.Serialize(after.Find<LR2SongDB.song>(path))); }
        foreach (string relative in sourceCharts) { Assert.IsNotNull(after.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, relative))); }
        CollectionAssert.AreEqual(archiveBytes, File.ReadAllBytes(archive));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>入力や既存宛先から独立した相対path/bytes一覧を、操作前に一回捕捉します。</summary>
    private static Dictionary<string, byte[]> CaptureArchiveTree(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    /// <summary>余分な展開結果も検出するため、全file集合の一致と各bytesを照合します。</summary>
    private static void AssertArchiveTree(IReadOnlyDictionary<string, byte[]> expected, string root)
    {
        Dictionary<string, byte[]> actual = CaptureArchiveTree(root);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach ((string path, byte[] bytes) in expected) { CollectionAssert.AreEqual(bytes, actual[path], path); }
    }

    /// <summary>この手動導入の保証に外部プロセス起動を混入させません。</summary>
    private sealed class ArchiveVerificationShellGateway : IExternalShellGateway
    {
        public void Open(ExternalShellRequest request) => throw new NotSupportedException();
        public ExplorerOpenResult OpenFileAndSelect(string filePath) => throw new NotSupportedException();
        public ExplorerOpenResult OpenDirectory(string directoryPath) => throw new NotSupportedException();
        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason) => throw new NotSupportedException();
    }

    /// <summary>私有コピーのZIPを本番入口へ通す局所検証の共通本体です。外部の固定path・利用者DBへ依存しません。</summary>
    internal static async Task VerifySupplementOnlyArchiveAsync(byte[] archiveBytes, IReadOnlyDictionary<string, byte[]> expected)
    {
        string[] sourceCharts = expected.Keys.Where(ChartFileKindResolver.IsSupportedChartFilePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.AreEqual(4, sourceCharts.Length);
        Assert.AreEqual(10, expected.Count - sourceCharts.Length);
        ChartFile[] charts = sourceCharts.Select(path => BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(
            Path.Combine(Path.GetTempPath(), "supplement-fixture", path), expected[path], DateTime.UtcNow,
            Convert.ToHexString(MD5.HashData(expected[path])), Convert.ToHexString(SHA256.HashData(expected[path]))))).ToArray();
        string[] sounds = charts.SelectMany((chart, index) => chart.Resources.Where(reference => reference.IsUsed && reference.Kind == ChartResourceKind.Audio
                && reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid)
            .Select(reference => Path.Combine(Path.GetDirectoryName(sourceCharts[index]) ?? string.Empty, reference.LookupKey + ".wav"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // 同数の異なるremixペアでも、sourceと所持側のパス順が同じ実在ペアを代表するよう準備します。
        string[] ownedBodies = sourceCharts.Select((path, index) => Encoding.GetEncoding("shift_jis").GetString(expected[path]) + "\n* synthetic-owned-" + index + "\n").ToArray();
        await using var fixture = new AutomaticContinuationFixture(true, false, destinationSounds: sounds, installedChartBodies: ownedBodies);
        string archive = Path.Combine(fixture.Root, "input.zip");
        File.WriteAllBytes(archive, archiveBytes);
        await fixture.InitializeAsync();
        Assert.IsTrue(charts.All(chart => fixture.Library.BmsCharts.All(owned => owned.Md5 != chart.Md5)), "同hash所持による譜面除外で四譜面の観測を落としません。");
        Assert.IsTrue(charts.All(chart => fixture.Library.BmsCharts.Any(owned => owned.RawTitle == chart.RawTitle && owned.Artist == chart.Artist)),
            "合成した所持側に、元譜面と一致するメタデータを準備します。");
        ChartFile firstOwned = fixture.Library.BmsCharts.OrderBy(chart => chart.Path, StringComparer.OrdinalIgnoreCase).First();
        Assert.AreEqual(charts[0].RawTitle, firstOwned.RawTitle, "同数ペアの代表順も元のパス順に揃えます。");
        Assert.AreEqual(charts[0].RawArtist, firstOwned.RawArtist);
        Assert.IsTrue(fixture.Owner.Enqueue([archive]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.IsNotNull(fixture.Result?.EstimatedInstallReceipt, string.Join(Environment.NewLine,
            fixture.Library.ChartPackagesPending.SelectMany(package => package.ChartEntries).Select(entry =>
                Path.GetFileName(entry.Chart.Path) + " title=" + entry.Chart.RawTitle + " artist=" + entry.Chart.Artist
                + " audio=" + entry.Chart.ResourceHealthMaintenanceSnapshot.WavFilesExisting + "/" + entry.Chart.ResourceHealthMaintenanceSnapshot.WavFilesDefined
                + " dst=" + entry.Chart.InstallDestination + " candidates=" + string.Join(",", entry.Chart.InstallDestinationSuggestions)
                + " warnings=" + string.Join(";", entry.Chart.Warnings.Select(warning => warning.Kind + ":" + warning.Message)))));
        Assert.IsTrue(fixture.Result.EstimatedInstallReceipt.DurableCommit);
        Assert.AreEqual(1, fixture.Result.EstimatedInstallReceipt.ApplyCounts.InstalledTargetApplyCount);
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
        CollectionAssert.AreEquivalent(sourceCharts.Select(path => Path.Combine(fixture.Destination, path)).ToArray(),
            fixture.Library.ChartPackagesInstalled.Single().ChartEntries.Select(entry => entry.Chart.Path).ToArray());
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        foreach ((string relative, byte[] bytes) in expected)
        { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(fixture.Destination, relative)), relative); }
        foreach (string relative in sourceCharts)
        {
            string path = Path.Combine(fixture.Destination, relative);
            Assert.IsNotNull(db.Find<LR2SongDB.song>(path));
            Assert.IsTrue(fixture.Library.BmsCharts.Any(chart => chart.Path == path));
        }
        Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
        CollectionAssert.AreEqual(archiveBytes, File.ReadAllBytes(archive));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }
}
