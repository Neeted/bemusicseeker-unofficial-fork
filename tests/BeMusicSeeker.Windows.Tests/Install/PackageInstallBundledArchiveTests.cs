using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PackageInstallComponentTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>
    /// Stella型の合成ZIP（同梱PREVIEW・BACKBMP・1譜面だけのSTAGEFILE・フォルダのpreview.oggを持ち、TITLE・ARTISTがリミックスごとに異なる4譜面）を自動導入なしで取り込み、通常の手動導入へ通します。
    /// 同梱資源があるので規則1で4譜面の一式1件として保留し、推定は4譜面の要求音源の和集合で行うため、全キー音を持つ元曲フォルダだけが全項目の候補になって導入先に自動設定され（自動導入は行わない）、譜面1のキー音だけを持つ同じメタデータの囮は候補に入りません。
    /// その後、TITLE・ARTISTが異なり、どの譜面のキー音も持たない独立した所持フォルダ（Independent）を導入先に明示して手動導入します。明示した導入先には自動推定のStrongも音源充足も要求しません。
    /// 全項目の導入先がIndependentになること、ZIP全ファイルがIndependentの既存ファイルに加えて相対パス・バイト列ごと配置されること、元曲フォルダと囮の不変、song行4件の追加と既存song行の不変（readme導入によるIndependentのtxt有無の追従を除く）、保留行の解除、無関係な保留の不変を確認します。
    /// 検出する誤り: メタデータが揃わないことを理由に同梱物付きの差分集を単体へ割る、未参照の付属物（readme、unused.png、preview.ogg）を落とす、譜面ごとの推定で囮を候補へ混ぜる、明示した導入先へStrongや音源充足を要求して手動導入を拒む、推定で設定された導入先へ導入する、手動導入で無関係な保留を変える。
    /// </summary>
    [TestMethod]
    public async Task ManualInstall_RemixDiffArchiveWithBundledSupplementsInstallsWholeUnitToSoleCandidate()
    {
        (byte[] archiveBytes, IReadOnlyDictionary<string, byte[]> expected, string[][] chartSounds) = CreateRemixDiffArchive();
        string[] sourceCharts = expected.Keys.Where(ChartFileKindResolver.IsSupportedChartFilePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] allSounds = [.. chartSounds.SelectMany(sounds => sounds)];
        var databaseScope = new ApplicationDatabaseScope();
        await using var fixture = new AutomaticContinuationFixture(databaseScope, false, false,
            destinationSounds: allSounds, installedChartBodies: [CreateRemixOwnedBody(allSounds)],
            ownedSongs: [("Decoy", CreateRemixOwnedBody(chartSounds[0]), chartSounds[0]),
                ("Independent", "#PLAYER 1\n#TITLE Independent owned destination\n#ARTIST Manual owner\n#BPM 120\n#WAV01 owned-only.wav\n#00101:01\n", ["owned-only.wav"])]);
        string decoy = Path.Combine(fixture.LibraryRoot, "Decoy");
        string independent = Path.Combine(fixture.LibraryRoot, "Independent");
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
        // ChartFileの可変参照を期待値にせず、操作前の値と警告の種類・事実をここで固定します。
        (string PackagePath, string Path, string Md5, ChartFileStatus Status, int? WAVHealth, ChartWarning[] Warnings) unrelatedBefore = (PackagePath: unrelatedPackage.path, unrelatedEntry.Chart.Path, unrelatedEntry.Chart.Md5,
            unrelatedEntry.Chart.Status, unrelatedEntry.Chart.WAVHealth, Warnings: unrelatedEntry.Chart.Warnings.ToArray());
        PackageChartInstallDestinationState unrelatedDestination = unrelatedEntry.CaptureInstallDestinationState();
        var ownedBefore = fixture.Library.BmsCharts.Select(chart => new { chart.Path, chart.Md5, chart.Title, chart.Artist, chart.Status, chart.WAVHealth }).ToArray();
        Dictionary<string, byte[]> destinationBefore = CaptureArchiveTree(fixture.Destination);
        Dictionary<string, byte[]> decoyBefore = CaptureArchiveTree(decoy);
        Dictionary<string, byte[]> independentBefore = CaptureArchiveTree(independent);
        Dictionary<string, string> songsBefore;
        string[] installBefore;
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Library.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.Database).OpenSongDbReadOnly())
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
        Assert.IsTrue(Directory.Exists(package.path), "同梱資源により一式として保留します。");
        CollectionAssert.AreEquivalent(sourceCharts, package.ChartEntries.Select(entry => Path.GetRelativePath(package.path, entry.Chart.Path)).ToArray());
        foreach (PackageChartEntry entry in package.ChartEntries)
        {
            // 自動設定した導入先は候補一覧から除かれるため、導入先と残りの候補の両方で囮の不在を観測します。
            Assert.AreEqual(fixture.Destination, entry.Chart.InstallDestination, Path.GetFileName(entry.Chart.Path) + ": 唯一の候補である元曲フォルダを導入先にします。");
            Assert.AreEqual(0, entry.Chart.InstallDestinationSuggestions.Count,
                Path.GetFileName(entry.Chart.Path) + ": 和集合で推定し、譜面1のキー音だけを持つ囮を候補へ入れません。実際: " + string.Join(",", entry.Chart.InstallDestinationSuggestions));
        }
        AssertArchiveTree(expected, package.path);
        using (LR2SongDBExtended beforeManual = new BmsLibraryDbGateway(fixture.Library.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.Database).OpenSongDbReadOnly())
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
        // 推定が自動設定した元曲フォルダではなく、Strongも音源充足も成り立たない独立した所持フォルダを明示します。
        await manualOwner.SetPendingAsync(request, independent);
        Assert.IsTrue(package.ChartEntries.All(entry => entry.Chart.InstallDestination == independent));
        PendingPackageMutationResult result = await manualOwner.ManualInstallPackagesAsync([package]);
        Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
        Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
        AssertArchiveTree(independentBefore.Concat(expected).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase), independent);
        AssertArchiveTree(destinationBefore, fixture.Destination);
        AssertArchiveTree(decoyBefore, decoy);
        CollectionAssert.AreEquivalent(sourceCharts.Select(relative => Path.Combine(independent, relative)).ToArray(),
            fixture.Library.ChartPackagesInstalled.Single().ChartEntries.Select(entry => entry.Chart.Path).ToArray());
        CollectionAssert.AreEquivalent(ownedBefore.Select(chart => chart.Path).Concat(sourceCharts.Select(relative => Path.Combine(independent, relative))).ToArray(),
            fixture.Library.BmsCharts.Select(chart => chart.Path).ToArray());
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
        CollectionAssert.AreEqual(unrelatedBefore.Warnings, unrelatedEntry.Chart.Warnings.ToArray());
        PackageChartInstallDestinationState remainingDestination = unrelatedEntry.CaptureInstallDestinationState();
        Assert.AreEqual(unrelatedDestination.Destination, remainingDestination.Destination);
        Assert.AreEqual(unrelatedDestination.Title, remainingDestination.Title);
        Assert.AreEqual(unrelatedDestination.Artist, remainingDestination.Artist);
        CollectionAssert.AreEqual(unrelatedDestination.Suggestions.ToArray(), remainingDestination.Suggestions.ToArray());
        AssertArchiveTree(unrelatedBytes, unrelated);
        using LR2SongDBExtended after = new BmsLibraryDbGateway(fixture.Library.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.Database).OpenSongDbReadOnly();
        CollectionAssert.AreEquivalent(installBefore, after.Table<LR2SongDBExtended.install>().Select(row => row.path + "|" + row.delete_parent).ToArray());
        Assert.AreEqual(songsBefore.Count + 4, after.Table<LR2SongDB.song>().Count());
        foreach ((string path, string row) in songsBefore)
        {
            LR2SongDB.song current = after.Find<LR2SongDB.song>(path);
            if (string.Equals(Path.GetDirectoryName(path), independent, StringComparison.OrdinalIgnoreCase))
            {
                // 同梱のreadme.txtを導入したIndependentでは、LR2のtxt有無だけが実ファイルに追従して変わります。
                Assert.AreEqual(1, current.txt, path);
                current.txt = 0;
            }
            Assert.AreEqual(row, JsonSerializer.Serialize(current), path);
        }
        foreach (string relative in sourceCharts) { Assert.IsNotNull(after.Find<LR2SongDB.song>(Path.Combine(independent, relative))); }
        CollectionAssert.AreEqual(archiveBytes, File.ReadAllBytes(archive));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>
    /// 同じStella型の合成ZIPを自動導入ありで取り込み、全キー音と元曲のTITLE・ARTISTを持つ合成所持先へ、自動後段が4譜面と全同梱物（未参照の readme・unused.png・preview.ogg を含む）を一式で導入することを確認します。
    /// 検出する誤り: 同梱物付きの差分集を単体へ割って付属物を落とす、一部の譜面だけを導入する、導入後に保留行や元ZIPを残す・変える。
    /// </summary>
    [TestMethod]
    public async Task AutomaticContinuation_RemixDiffArchiveWithBundledSupplementsInstallsWholeUnit()
    {
        (byte[] archiveBytes, IReadOnlyDictionary<string, byte[]> expected, string[][] chartSounds) = CreateRemixDiffArchive();
        string[] sourceCharts = expected.Keys.Where(ChartFileKindResolver.IsSupportedChartFilePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] allSounds = [.. chartSounds.SelectMany(sounds => sounds)];
        var databaseScope = new ApplicationDatabaseScope();
        await using var fixture = new AutomaticContinuationFixture(databaseScope, true, false, destinationSounds: allSounds, installedChartBodies: [CreateRemixOwnedBody(allSounds)]);
        string archive = Path.Combine(fixture.Root, "input.zip");
        File.WriteAllBytes(archive, archiveBytes);
        await fixture.InitializeAsync();
        Dictionary<string, byte[]> destinationBefore = CaptureArchiveTree(fixture.Destination);
        Assert.IsTrue(fixture.Owner.Enqueue([archive]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.IsNotNull(fixture.Result?.EstimatedInstallReceipt, string.Join(Environment.NewLine,
            fixture.Library.ChartPackagesPending.SelectMany(package => package.ChartEntries).Select(entry =>
                Path.GetFileName(entry.Chart.Path) + " dst=" + entry.Chart.InstallDestination + " candidates=" + string.Join(",", entry.Chart.InstallDestinationSuggestions)
                + " warnings=" + string.Join(";", entry.Chart.Warnings.Select(warning => warning.Kind + ":" + ChartWarningProjectionFormatter.GetDetailText(warning))))));
        Assert.IsTrue(fixture.Result.EstimatedInstallReceipt.DurableCommit);
        Assert.AreEqual(1, fixture.Result.EstimatedInstallReceipt.ApplyCounts.InstalledTargetApplyCount);
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        CollectionAssert.AreEquivalent(sourceCharts.Select(path => Path.Combine(fixture.Destination, path)).ToArray(),
            fixture.Library.ChartPackagesInstalled.Single().ChartEntries.Select(entry => entry.Chart.Path).ToArray());
        AssertArchiveTree(destinationBefore.Concat(expected).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase), fixture.Destination);
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Library.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.Database).OpenSongDbReadOnly();
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

    /// <summary>
    /// Stella 7475型の合成ZIPと独立した期待値を作ります。4譜面はTITLE・ARTISTがリミックスごとに異なり（規則4ではまとまらない）、キー音は同梱せず、各譜面のPREVIEW・BACKBMP、譜面1だけのSTAGEFILE、フォルダのpreview.ogg、readme、unused.pngを同梱します。
    /// 戻り値の譜面ごとのキー音は、所持側の合成に使います。
    /// </summary>
    private static (byte[] Archive, IReadOnlyDictionary<string, byte[]> Expected, string[][] ChartSounds) CreateRemixDiffArchive()
    {
        (string Title, string Artist)[] songs = [("千年女王の幻燈", "微熱"), ("千年女王の幻燈 (Alpha remix)", "微熱 vs. Alpha"), ("千年女王の幻燈 (Beta remix)", "Beta"), ("千年女王の幻燈 -Gamma mix-", "Gamma")];
        var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string[][] chartSounds = new string[songs.Length][];
        var shiftJis = Encoding.GetEncoding("shift_jis");
        for (int index = 0; index < songs.Length; index++)
        {
            int number = index + 1;
            chartSounds[index] = ["key" + number + "a.wav", "key" + number + "b.wav"];
            string chart = "#PLAYER 1\n#TITLE " + songs[index].Title + "\n#ARTIST " + songs[index].Artist + "\n#BPM 120\n"
                + "#WAV01 " + chartSounds[index][0] + "\n#WAV02 " + chartSounds[index][1] + "\n#00111:0102\n"
                + "#PREVIEW preview_" + number + ".ogg\n#BACKBMP _backbmp_" + number + ".png\n" + (index == 0 ? "#STAGEFILE title_Another.png\n" : string.Empty);
            expected.Add("chart" + number + ".bms", shiftJis.GetBytes(chart));
            expected.Add("preview_" + number + ".ogg", [(byte)(number + 10), 9]);
            expected.Add("_backbmp_" + number + ".png", [(byte)(number + 20), 8]);
        }
        foreach (string name in new[] { "title_Another.png", "preview.ogg", "readme.txt", "unused.png" })
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
        return (content.ToArray(), expected, chartSounds);
    }

    /// <summary>元曲（譜面1と同じTITLE・ARTIST）の所持譜面本文を、指定したキー音をすべて使用する形で作ります。</summary>
    private static string CreateRemixOwnedBody(IEnumerable<string> sounds)
    {
        var body = new StringBuilder("#PLAYER 1\n#TITLE 千年女王の幻燈\n#ARTIST 微熱\n#BPM 120\n");
        var keys = new StringBuilder();
        int id = 1;
        foreach (string sound in sounds)
        {
            string key = id.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
            body.Append("#WAV").Append(key).Append(' ').Append(sound).Append('\n');
            keys.Append(key);
            id++;
        }
        // BGMチャンネルへ全IDを並べ、全キー音を使用参照にします。
        return body.Append("#00101:").Append(keys).Append('\n').ToString();
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

}
