using System.Security.Cryptography;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 実リンクを含むフォルダ統合の拒否を確認する専用suiteの共通準備です。本番の保存窓口と実SQLiteを持つライブラリ、
/// リンクをたどらない木の写し、拒否後のライブラリ・DB・登録の不変の確認を提供します。
/// </summary>
internal sealed class FolderMergeLinkFixture : IDisposable
{
    private readonly ApplicationDatabaseScope databaseScope;
    private readonly string songDbPath;
    private readonly Settings settings;
    private readonly string[] chartPaths;
    private readonly string[] registrations;

    private FolderMergeLinkFixture(ApplicationDatabaseScope databaseScope, string songDbPath, Settings settings, TestBmsLibrary library, ChartFile[] charts)
    {
        this.databaseScope = databaseScope;
        this.songDbPath = songDbPath;
        this.settings = settings;
        Library = library;
        chartPaths = [.. charts.Select(chart => chart.Path)];
        registrations = [.. new BmsRootRegistrationStore(settings, () => library.FixtureLr2Owner).CaptureUserRoots()];
    }

    /// <summary>この準備が所有する実ライブラリです。</summary>
    internal TestBmsLibrary Library { get; }

    /// <summary>譜面を登録して実DBへ保存したライブラリを作ります。登録ルートの指定がなければ、DBの置き場の親だけを登録します。</summary>
    internal static FolderMergeLinkFixture Create(ApplicationDatabaseScope databaseScope, string songDbPath, ChartFile[] charts, string[]? registeredRoots = null)
    {
        string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
        Settings settings = TestBmsFactory.CreateRootSettings(registeredRoots ?? [root]);
        var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
            new TestUiScheduler(() => null), settings: settings)
        { BmsCharts = charts };
        new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs(charts);
        return new FolderMergeLinkFixture(databaseScope, songDbPath, settings, library, charts);
    }

    /// <summary>モデル入口で統合を要求します。既定の <c>reportAtTerminal: false</c> のため、失敗の汎用案内は案内の収集先へ加わります。</summary>
    internal DuplicateMergeMaintenanceReceipt Merge(string source, string destination, LibraryOperationNotices notices)
        => Library.MergeChartDirectory(source, destination, 1, notices);

    /// <summary>拒否後に、所持譜面・保存したDBの行・登録ルートが操作前と同じであることを確認します。</summary>
    internal void AssertCatalogUnchanged()
    {
        CollectionAssert.AreEquivalent(chartPaths, Library.BmsCharts.Select(chart => chart.Path).ToArray());
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
        {
            CollectionAssert.AreEquivalent(chartPaths, db.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        }
        CollectionAssert.AreEqual(registrations, new BmsRootRegistrationStore(settings, () => Library.FixtureLr2Owner).CaptureUserRoots().ToArray());
    }

    /// <summary>
    /// 統合が未適用で、案内がちょうど一件の指定した本文であり、変更セッションの事実を持たないことを確認します。
    /// 汎用の統合失敗の案内が重なっていれば、件数で検出します。
    /// </summary>
    /// <param name="receipt">統合の確定結果です。</param>
    /// <param name="notices">操作の案内の収集先です。</param>
    /// <param name="expectedText">期待する案内の本文です。</param>
    /// <param name="exact">真なら本文の完全一致、偽なら含むことを求めます。偽は、既存の拒否の本文が補足を含む場合に使います。</param>
    internal static void AssertRefusedWithSingleNotice(DuplicateMergeMaintenanceReceipt receipt, LibraryOperationNotices notices, string expectedText, bool exact = true)
    {
        Assert.IsFalse(receipt.MergeApplied);
        Assert.IsNull(receipt.SessionReceipt.PrimaryFailure);
        Assert.AreEqual(0, receipt.SessionReceipt.ConfirmedChangeCount);
        Assert.AreEqual(0, receipt.SessionReceipt.ApplyCounts.CatalogApplyCount);
        IReadOnlyList<BMSLibrary.OperationDialogMessage> messages = notices.Snapshot();
        Assert.AreEqual(1, messages.Count, string.Join(" | ", messages.Select(message => message.MessageBoxText)));
        if (exact)
        {
            Assert.AreEqual(expectedText, messages[0].MessageBoxText);
        }
        else
        {
            StringAssert.Contains(messages[0].MessageBoxText, expectedText);
        }
    }

    /// <summary>
    /// 指定した各位置の木を、リンクの先へ入らずに写します。リンクは自身の存在とリンク先の綴りだけを記録し、
    /// 隠し・システム属性の項目も含め、ファイルは内容のハッシュを記録します。存在しない位置も記録します。
    /// </summary>
    internal static string[] Snapshot(params (string Label, string Path)[] roots)
    {
        List<string> lines = [];
        foreach ((string label, string path) in roots)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
            {
                lines.Add("M|" + label);
                continue;
            }
            Walk((attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path), label, lines);
        }
        lines.Sort(StringComparer.Ordinal);
        return [.. lines];
    }

    /// <summary>拒否の前後の写しが一致することを、差のある行を示して確認します。</summary>
    internal static void AssertSnapshotUnchanged(string[] before, string[] after)
    {
        string[] removed = [.. before.Except(after)];
        string[] added = [.. after.Except(before)];
        Assert.AreEqual(0, removed.Length + added.Length, "消えた: " + string.Join(", ", removed) + " / 増えた: " + string.Join(", ", added));
        CollectionAssert.AreEqual(before, after);
    }

    /// <summary>ライブラリと設定を終了します。</summary>
    public void Dispose() => Library.RequestShutdown("folder-merge-link-refusal");

    private static void Walk(FileSystemInfo entry, string label, List<string> lines)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            lines.Add("L|" + label + "|" + entry.LinkTarget);
            return;
        }
        if (entry is DirectoryInfo directory)
        {
            lines.Add("D|" + label);
            foreach (FileSystemInfo child in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                Walk(child, label + "/" + child.Name, lines);
            }
            return;
        }
        lines.Add("F|" + label + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry.FullName))));
    }
}
