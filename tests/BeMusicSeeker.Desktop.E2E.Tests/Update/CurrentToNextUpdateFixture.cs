using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using BeMusicSeeker.Desktop.E2E.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Components.Update;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Desktop.E2E.Tests.Update;

/// <summary>
/// 実配布のCurrentを専有コピーへ展開し、UIから次版へ更新する受入の起動前入力と、更新後の配置・設定・登録の照合を担います。
/// </summary>
/// <remarks>
/// 入力は起動前に準備します。配信内容の公開は呼出元の責任で、ケースが起動後に世代を切り替えることもできます。profileは配布コピーの外に置き、配信はケース所有のloopbackです。
/// 配布ZIPを基準にした配置の照合は、更新先がNextでもCurrentへの巻き戻しでも使える形にしています。
/// </remarks>
internal sealed class CurrentToNextUpdateFixture : IAsyncDisposable
{
    /// <summary>検索入力に使う語です。一致する譜面のtitleだけが含みます。</summary>
    internal const string SearchText = "needle";
    /// <summary>製品のファイル契約が読まない目印の名前です。</summary>
    internal const string SentinelName = "sc15a-sentinel.txt";
    /// <summary>更新後に除去されるべき、旧管理ファイルの相対パスです。</summary>
    internal const string ObsoleteName = "sc15a-obsolete.txt";
    /// <summary>旧管理ファイルの内容です。巻き戻し後に元のbytesで残っていることの照合に使います。</summary>
    private static readonly byte[] ObsoleteBytes = new UTF8Encoding(false).GetBytes("obsolete managed file");
    private const string ManifestName = "update-managed-files.txt";
    private const string Language = "en-US";

    private CurrentToNextUpdateFixture(DistributionCatalog catalog, string appRoot, string profile, string sessionManifestPath, LoopbackUpdateFeed feed)
    {
        Catalog = catalog;
        AppRoot = appRoot;
        ProfileDirectory = profile;
        SessionManifestPath = sessionManifestPath;
        Feed = feed;
    }

    /// <summary>入力の配布catalogです。</summary>
    internal DistributionCatalog Catalog { get; }
    /// <summary>展開済みCurrentの専有installationです。更新はここへ適用されます。</summary>
    internal string AppRoot { get; }
    /// <summary>installationの外に置くtest sessionのprofileです。</summary>
    internal string ProfileDirectory { get; }
    /// <summary>driverが書くtest session manifestの置き場です。installationの外にあります。</summary>
    internal string SessionManifestPath { get; }
    /// <summary>ケース所有のloopback更新配信です。</summary>
    internal LoopbackUpdateFeed Feed { get; }
    /// <summary>登録した譜面rootです。</summary>
    internal string LibraryRoot { get; private set; } = string.Empty;
    /// <summary>parserに依存せず宣言した譜面の入力です。</summary>
    internal IReadOnlyList<DesktopChartInput> Charts { get; private set; } = [];
    /// <summary>検索語に一致する譜面です。</summary>
    internal IReadOnlyList<DesktopChartInput> Matching
        => Charts.Where(chart => chart.Title.Contains(SearchText, StringComparison.Ordinal)).ToArray();
    /// <summary>起動前にprofileの設定へ書いた値です。</summary>
    internal IReadOnlyDictionary<string, string> Settings { get; private set; } = new Dictionary<string, string>();
    /// <summary>更新前に置いた、保持されるべきファイルの相対パス(installation基準)とbytesです。</summary>
    internal IReadOnlyDictionary<string, byte[]> Sentinels { get; private set; } = new Dictionary<string, byte[]>();
    /// <summary>更新先のpackageのファイル名です。配信path・ダイアログの表示名と一致します。</summary>
    internal string NextPackageFileName { get; private set; } = string.Empty;
    private string nextVersion = string.Empty;
    private byte[] nextPackageBytes = [];
    /// <summary>manifestの更新配信path(配信の起点からの相対)です。</summary>
    internal const string ManifestPath = "update.json";
    /// <summary>配信するmanifestが示す配布ページのURLです。配信は応答せず、到達すれば404として記録します。</summary>
    internal string ReleasePageUrl { get; private set; } = string.Empty;
    /// <summary>test sessionへ渡す更新manifestのURLです。</summary>
    internal string UpdateManifestUrl => new Uri(Feed.BaseAddress, ManifestPath).AbsoluteUri;
    /// <summary>更新先packageの配信path(配信の起点からの相対)です。</summary>
    internal string PackagePath => PackagePathOf(NextPackageFileName);

    /// <summary>指定したファイル名のpackageの配信path(配信の起点からの相対)です。</summary>
    internal static string PackagePathOf(string fileName) => "packages/" + fileName;

    /// <summary>指定したファイル名のpackageの配信URLです。</summary>
    private string PackageUrlOf(string fileName) => new Uri(Feed.BaseAddress, PackagePathOf(fileName)).AbsoluteUri;
    /// <summary>起動する配布の実行ファイルです。</summary>
    internal string ExecutablePath => Path.Combine(AppRoot, "BeMusicSeeker.exe");
    /// <summary>profileの設定ファイルです。</summary>
    internal string UserConfigPath => Path.Combine(ProfileDirectory, "config", "user.config");

    /// <summary>起動前の入力を完成させます。配信内容は公開せず、呼出元が <see cref="PublishNextPackage"/> または <see cref="PublishPackage"/> で公開します。</summary>
    internal static async Task<CurrentToNextUpdateFixture> CreateAsync(TestWorkspace workspace, DistributionCatalog catalog,
        CancellationToken operationToken, ProcessDeadline deadline)
    {
        var current = Version.Parse(catalog.Current.Version);
        var next = Version.Parse(catalog.Next.Version);
        Assert.IsTrue(next > current, "更新先の版は現行より新しい必要があります。");
        IReadOnlyDictionary<string, string> currentHashes = ReadZipHashes(catalog.Current.PackagePath);
        IReadOnlyDictionary<string, string> nextHashes = ReadZipHashes(catalog.Next.PackagePath);
        Assert.AreNotEqual(currentHashes["BeMusicSeeker.exe"], nextHashes["BeMusicSeeker.exe"],
            "CurrentとNextの実行ファイルが同一では、更新の適用を識別できません。");

        string app = await catalog.Current.ExtractInstallationAsync(workspace, "app", deadline);
        var feed = new LoopbackUpdateFeed(operationToken);
        CurrentToNextUpdateFixture? fixture = null;
        try
        {
            fixture = new CurrentToNextUpdateFixture(catalog, app, workspace.GetPath("profile"), workspace.GetPath("session.json"), feed);
            await fixture.PrepareAsync(workspace, next);
            return fixture;
        }
        catch
        {
            await feed.DisposeAsync();
            throw;
        }
    }

    private async Task PrepareAsync(TestWorkspace workspace, Version next)
    {
        // 保持領域・管理外・旧管理ファイルの目印を、起動前の配布コピーへ置く。bytesは目印ごとに固有にする。
        var sentinels = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [@"config\" + SentinelName] = Encoding.UTF8.GetBytes("sc15a config sentinel " + Guid.NewGuid().ToString("N")),
            [@"data\" + SentinelName] = Encoding.UTF8.GetBytes("sc15a data sentinel " + Guid.NewGuid().ToString("N")),
            ["sc15a-unmanaged-root.txt"] = Encoding.UTF8.GetBytes("sc15a unmanaged root " + Guid.NewGuid().ToString("N")),
            [@"user-data\sc15a-unmanaged-nested.txt"] = Encoding.UTF8.GetBytes("sc15a unmanaged nested " + Guid.NewGuid().ToString("N"))
        };
        foreach ((string relative, byte[] bytes) in sentinels)
        {
            string path = Path.Combine(AppRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidDataException(path));
            await File.WriteAllBytesAsync(path, bytes);
        }
        Sentinels = sentinels;
        await File.WriteAllBytesAsync(Path.Combine(AppRoot, ObsoleteName), ObsoleteBytes);
        await File.AppendAllTextAsync(Path.Combine(AppRoot, ManifestName), "\n" + ObsoleteName + "\n", new UTF8Encoding(false));

        LibraryRoot = workspace.GetPath("library");
        Charts = Array.AsReadOnly(new[]
        {
            DesktopPlaylistFixture.CreateChart(Path.Combine(LibraryRoot, "alpha"), "alpha", "SC15A needle Alpha"),
            DesktopPlaylistFixture.CreateChart(Path.Combine(LibraryRoot, "gamma"), "gamma", "SC15A needle Gamma"),
            DesktopPlaylistFixture.CreateChart(Path.Combine(LibraryRoot, "beta"), "beta", "SC15A other Beta")
        });

        // 製品のAssemblyVersionと同じ値にして、初回起動や版更新の経路を通さない。単一ファイル配布のexeからはFileVersionを読む。
        var version = FileVersionInfo.GetVersionInfo(ExecutablePath);
        Settings = new Dictionary<string, string>
        {
            ["AssemblyVersion"] = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}",
            ["Lang"] = Language,
            ["OperationModeLR2DB"] = "False",
            ["BMSRootPath"] = LibraryRoot,
            ["StandaloneBmsRootPaths"] = LibraryRoot,
            ["BMSInstallDir"] = LibraryRoot,
            ["ScanBmsFilesOnStartup"] = "True",
            // 既定のTrueは起動時に導入待ちの表示を選ぶため、登録rootの一覧を見るために通常の表示へ戻す。
            ["StartupSelectInstallPending"] = "False",
            // 外部通信が既定で有効な項目は、解決されない名前または無効値にし、配信へ更新以外の要求を向けない。
            ["EnablePlaylistUrlCompletion"] = "False",
            ["EnableStellaFullPlaylistUrlCompletion"] = "False",
            ["SkipInitPlaylistLoad"] = "True",
            ["TableListURL"] = "https://example.invalid/sc15a-table-list"
        };
        LegacySettingsFixture.Write(UserConfigPath, Settings);

        // 配信はNextの実bytesとそこから計算したサイズ・hashだけを使う。catalogの申告と一致することも確認する。
        // 公開は入力の準備と分け、呼出元が最初に配る内容を選べるようにする。
        nextPackageBytes = await File.ReadAllBytesAsync(Catalog.Next.PackagePath);
        Assert.AreEqual(Catalog.Next.SizeBytes, nextPackageBytes.LongLength);
        Assert.AreEqual(Catalog.Next.Sha256, Convert.ToHexStringLower(SHA256.HashData(nextPackageBytes)), ignoreCase: true);
        NextPackageFileName = Path.GetFileName(Catalog.Next.PackagePath);
        nextVersion = next.ToString(4);
        ReleasePageUrl = new Uri(Feed.BaseAddress, "releases/v" + next).AbsoluteUri;
    }

    /// <summary>Nextの実bytesを、実サイズ・実SHA-256のmanifestとともに新しい世代として配信します。</summary>
    /// <returns>公開した世代番号です。</returns>
    internal int PublishNextPackage() => PublishPackage(NextPackageFileName, nextPackageBytes, "update acceptance next");

    /// <summary>
    /// 指定したファイル名・bytesのpackageを、そのbytesから計算したサイズ・SHA-256のmanifest(版=Next)とともに新しい世代として配信します。
    /// </summary>
    /// <returns>公開した世代番号です。</returns>
    internal int PublishPackage(string fileName, byte[] package, string label)
    {
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(package));
        string manifest = UpdateFeedManifest.Create(nextVersion, ReleasePageUrl, label, fileName, PackageUrlOf(fileName), sha256, package.LongLength);
        return Feed.Publish(new Dictionary<string, byte[]>
        {
            [ManifestPath] = Encoding.UTF8.GetBytes(manifest),
            [PackagePathOf(fileName)] = package
        });
    }

    /// <summary>ZIPの全ファイルentryについて、相対パス(区切りは`/`)とSHA-256(小文字hex)の対応を返します。</summary>
    internal static IReadOnlyDictionary<string, string> ReadZipHashes(string zipPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) { continue; }
            using Stream input = entry.Open();
            hashes.Add(name, Convert.ToHexStringLower(SHA256.HashData(input)));
        }
        return hashes;
    }

    /// <summary>
    /// 配布ZIPを基準に、更新が確定したinstallationの配置を照合します。管理一覧の全パスが存在して同じentryのhashと一致し、
    /// 管理一覧自身のパス集合が配布のものと一致し、更新前に追記した旧管理ファイルが除去されていることを確認します。
    /// </summary>
    /// <param name="zipPath">基準にする配布ZIPです。</param>
    internal void AssertInstallationMatches(string zipPath)
    {
        string[] zipManaged = AssertManagedFilesMatchZip(zipPath, [], "installationの管理一覧が配布の一覧と一致しません。");
        Assert.IsFalse(File.Exists(Path.Combine(AppRoot, ObsoleteName)), "旧管理ファイルが除去されていません。");
        Assert.IsFalse(zipManaged.Contains(ObsoleteName, StringComparer.OrdinalIgnoreCase), "配布の管理一覧に旧管理ファイルがあります。");
    }

    /// <summary>
    /// 更新が確定せず更新前の構成へ戻った後のinstallationを、更新前の配布ZIPを基準に照合します。
    /// 管理一覧のパス集合が「配布の一覧 + 旧管理ファイル」と一致し、配布の各管理ファイルが同じentryのhashと一致し、
    /// 旧管理ファイルが元のbytesで残り、更新先のpackage専用ファイルが存在しないことを確認します。
    /// </summary>
    /// <param name="zipPath">更新前の配布ZIPです。</param>
    /// <param name="packageOnlyFileName">更新先のpackageだけが持つファイルの相対パスです。巻き戻し後は存在してはいけません。</param>
    internal void AssertInstallationRolledBackTo(string zipPath, string packageOnlyFileName)
    {
        string[] zipManaged = AssertManagedFilesMatchZip(zipPath, [ObsoleteName], "巻き戻し後の管理一覧が「更新前の配布の一覧 + 旧管理ファイル」と一致しません。");
        Assert.IsFalse(zipManaged.Contains(packageOnlyFileName, StringComparer.OrdinalIgnoreCase), "更新前の配布の管理一覧にpackage専用ファイルがあります。");
        string obsolete = Path.Combine(AppRoot, ObsoleteName);
        Assert.IsTrue(File.Exists(obsolete), "旧管理ファイルが復元されていません。");
        CollectionAssert.AreEqual(ObsoleteBytes, File.ReadAllBytes(obsolete), "旧管理ファイルのbytesが元と異なります。");
        Assert.IsFalse(File.Exists(Path.Combine(AppRoot, packageOnlyFileName.Replace('/', '\\'))), "更新先のpackage専用ファイルが巻き戻し後に残っています。");
    }

    private string[] AssertManagedFilesMatchZip(string zipPath, IReadOnlyCollection<string> additionalManagedPaths, string listMessage)
    {
        IReadOnlyDictionary<string, string> expected = ReadZipHashes(zipPath);
        string[] managed = ReadManifestPaths(File.ReadAllText(Path.Combine(AppRoot, ManifestName)));
        string[] zipManaged = ReadZipManifestPaths(zipPath);
        string[] expectedPaths = [.. zipManaged, .. additionalManagedPaths];
        CollectionAssert.AreEquivalent(expectedPaths, managed, listMessage);
        foreach (string relative in zipManaged)
        {
            string path = Path.Combine(AppRoot, relative.Replace('/', '\\'));
            Assert.IsTrue(File.Exists(path), "管理ファイルが存在しません: " + relative);
            Assert.AreEqual(expected[relative], HashFile(path), "管理ファイルが配布と一致しません: " + relative);
        }
        return zipManaged;
    }

    /// <summary>installationの`BeMusicSeeker.exe`が、指定した配布ZIPの同じentryと一致するかを返します。</summary>
    internal bool ExecutableMatches(string zipPath)
        => ReadZipHashes(zipPath)["BeMusicSeeker.exe"] == HashFile(ExecutablePath);

    /// <summary>更新前に置いた保持領域・管理外のファイルが、同じbytesで残っていることを確認します。</summary>
    internal void AssertSentinelsPreserved()
    {
        foreach ((string relative, byte[] bytes) in Sentinels)
        {
            string path = Path.Combine(AppRoot, relative);
            Assert.IsTrue(File.Exists(path), "保持されるべきファイルが消えています: " + relative);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "保持されるべきファイルのbytesが変わっています: " + relative);
        }
    }

    /// <summary>
    /// 終了後のprofile設定について、起動前に書いた全キーが同じ値で残っていることを確認します。
    /// `AssemblyVersion`だけは、更新後の製品の実行ファイルのFileVersionと四成分で一致することを要求します。
    /// </summary>
    internal void AssertSettingsPreserved(string expectedAssemblyVersion)
    {
        Dictionary<string, string> saved = ReadSavedSettings();
        foreach ((string key, string expected) in Settings)
        {
            Assert.IsTrue(saved.ContainsKey(key), "終了後の設定に " + key + " がありません。");
            if (key == "AssemblyVersion") { continue; }
            if (key is "BMSRootPath" or "StandaloneBmsRootPaths" or "BMSInstallDir")
            {
                Assert.IsTrue(string.Equals(Path.GetFullPath(expected), Path.GetFullPath(saved[key]), StringComparison.OrdinalIgnoreCase),
                    key + " expected=" + expected + " actual=" + saved[key]);
            }
            else { Assert.AreEqual(expected, saved[key], key); }
        }
        XElement version = XDocument.Load(UserConfigPath).Descendants("setting")
            .Single(element => (string?)element.Attribute("name") == "AssemblyVersion")
            .Element("value")?.Element("SerializableVersion") ?? throw new InvalidDataException("AssemblyVersionがSerializableVersion形式で保存されていません。");
        int Part(string name) => int.Parse(version.Element(name)?.Value ?? throw new InvalidDataException(name), CultureInfo.InvariantCulture);
        Assert.AreEqual(Version.Parse(expectedAssemblyVersion), new Version(Part("Major"), Part("Minor"), Part("Build"), Part("Revision")),
            "AssemblyVersionが更新後の製品の版と一致しません。");
    }

    /// <summary>更新後の製品の実行ファイルから、AssemblyVersion相当の四成分(FileVersion)を読みます。</summary>
    internal string ReadExecutableFileVersion()
    {
        var version = FileVersionInfo.GetVersionInfo(ExecutablePath);
        return $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}";
    }

    private Dictionary<string, string> ReadSavedSettings()
        => XDocument.Load(UserConfigPath).Descendants("setting")
            .ToDictionary(element => (string?)element.Attribute("name") ?? throw new InvalidDataException(),
                element => element.Element("value")?.Value ?? string.Empty);

    /// <summary>profileのsong.dbを読取り専用で開き、song と chart_digest_map の全列・全行を列名つきで読みます。</summary>
    /// <remarks>ファイルコピーではなくDB接続で読み、WALを考慮します。値は型を含む文字列へ写し、型の違いも差として検出します。</remarks>
    internal PersistedTables ReadPersistedTables()
    {
        using var database = new SQLite.SQLiteConnection(Path.Combine(ProfileDirectory, "data", "song.db"),
            SQLite.SQLiteOpenFlags.ReadOnly | SQLite.SQLiteOpenFlags.FullMutex);
        return new(ReadTable(database, "song", "path"), ReadTable(database, "chart_digest_map", "md5"));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadTable(SQLite.SQLiteConnection database, string table, string key)
    {
        var rows = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        SQLitePCL.sqlite3_stmt statement = SQLite.SQLite3.Prepare2(database.Handle, $"SELECT * FROM {table} ORDER BY {key}");
        try
        {
            int count = SQLite.SQLite3.ColumnCount(statement);
            while (SQLite.SQLite3.Step(statement) == SQLite.SQLite3.Result.Row)
            {
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int index = 0; index < count; index++)
                {
                    row[SQLite.SQLite3.ColumnName16(statement, index)] = SQLite.SQLite3.ColumnType(statement, index) switch
                    {
                        SQLite.SQLite3.ColType.Null => "null",
                        SQLite.SQLite3.ColType.Integer => "int:" + SQLite.SQLite3.ColumnInt64(statement, index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        SQLite.SQLite3.ColType.Float => "real:" + SQLite.SQLite3.ColumnDouble(statement, index).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        SQLite.SQLite3.ColType.Text => "text:" + SQLite.SQLite3.ColumnString(statement, index),
                        _ => "blob:" + Convert.ToHexString(SQLite.SQLite3.ColumnByteArray(statement, index))
                    };
                }
                rows.Add(row[key], row);
            }
        }
        finally { SQLite.SQLite3.Finalize(statement); }
        return rows;
    }

    /// <summary>song と chart_digest_map の全行を、キー列(path と md5)から列名つきの値への対応で保持します。</summary>
    internal sealed record PersistedTables(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Song,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ChartDigestMap);

    /// <summary>更新前後の永続値を全行・全列で完全一致比較し、差のあるキー・列名・前後の値を失敗に含めます。</summary>
    internal static void AssertPersistedTablesEqual(PersistedTables before, PersistedTables after)
    {
        var differences = new List<string>();
        Compare("song", before.Song, after.Song);
        Compare("chart_digest_map", before.ChartDigestMap, after.ChartDigestMap);
        Assert.AreEqual(0, differences.Count, "更新前後で永続の譜面DBが一致しません: " + string.Join(" ; ", differences));

        void Compare(string table, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> left, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> right)
        {
            foreach (string key in left.Keys.Union(right.Keys, StringComparer.Ordinal))
            {
                if (!left.TryGetValue(key, out IReadOnlyDictionary<string, string>? from)) { differences.Add($"{table}[{key}] は更新後に増えた行"); continue; }
                if (!right.TryGetValue(key, out IReadOnlyDictionary<string, string>? to)) { differences.Add($"{table}[{key}] は更新後に消えた行"); continue; }
                foreach (string column in from.Keys.Union(to.Keys, StringComparer.Ordinal))
                {
                    from.TryGetValue(column, out string? a);
                    to.TryGetValue(column, out string? b);
                    if (!string.Equals(a, b, StringComparison.Ordinal)) { differences.Add($"{table}[{key}].{column}: {a ?? "<列なし>"} -> {b ?? "<列なし>"}"); }
                }
            }
        }
    }

    /// <summary>installationを指すRunOnce回復登録と永続監視登録が、どちらも0件であることを確認します。</summary>
    internal void AssertNoRecoveryRegistrations()
    {
        Assert.AreEqual(0, UpdaterPackageTestSupport.GetRecoveryRunOnceValues(AppRoot).Count, "RunOnceの回復登録が残っています。");
        Assert.AreEqual(0, UpdaterPackageTestSupport.GetRecoverySupervisorValues(AppRoot).Count, "回復の永続監視登録が残っています。");
    }

    /// <summary>全所有process終端後に、このinstallationを指す回復登録だけを解除します。成功時は何も消しません。</summary>
    internal void DeleteRecoveryRegistrations() => UpdaterPackageTestSupport.DeleteRecoveryRunOnceValues(AppRoot);

    private static string[] ReadManifestPaths(string text)
        => [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Replace('\\', '/'))];

    private static string[] ReadZipManifestPaths(string zipPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = archive.Entries.Single(candidate => string.Equals(candidate.FullName, ManifestName, StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return ReadManifestPaths(reader.ReadToEnd());
    }

    private static string HashFile(string path)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    /// <summary>配信の待受と全接続処理を、呼出元が確定した回収期限で終端します。</summary>
    internal async ValueTask DisposeAsync(ProcessDeadline cleanupDeadline)
    {
        using var budget = new CancellationTokenSource(cleanupDeadline.Remaining);
        await Feed.DisposeAsync(budget.Token);
    }

    /// <summary>既定の10秒で配信を回収します。</summary>
    public ValueTask DisposeAsync() => DisposeAsync(ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
}
