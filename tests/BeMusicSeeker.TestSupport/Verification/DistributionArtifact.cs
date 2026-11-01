using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>検査を完了した配布ZIPと、生成した場合のpublish出力を指す変更不能な値です。</summary>
public sealed class DistributionArtifact
{
    /// <summary>管理対象集合を複製し、ケース間で共有する配布入力を表します。</summary>
    [JsonConstructor]
    public DistributionArtifact(string version, string? appPublishRoot, string? updaterPublishRoot,
        string packagePath, long sizeBytes, string sha256, IReadOnlyList<string> managedFiles)
    {
        Version = version;
        AppPublishRoot = appPublishRoot;
        UpdaterPublishRoot = updaterPublishRoot;
        PackagePath = packagePath;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        ManagedFiles = new ReadOnlyCollection<string>(managedFiles.ToArray());
    }

    /// <summary>配布実行ファイルから得た実効版です。旧版だけは固定配布の版です。</summary>
    public string Version { get; }
    /// <summary>現行・次版のアプリpublish出力です。固定旧版にはありません。</summary>
    public string? AppPublishRoot { get; }
    /// <summary>現行・次版のupdater publish出力です。固定旧版にはありません。</summary>
    public string? UpdaterPublishRoot { get; }
    /// <summary>検査済みZIPの絶対パスです。</summary>
    public string PackagePath { get; }
    /// <summary>ZIPのバイト数です。</summary>
    public long SizeBytes { get; }
    /// <summary>ZIP全体のSHA-256です。</summary>
    public string Sha256 { get; }
    /// <summary>ZIPのmanifest自身を除く管理対象集合です。旧版は空です。</summary>
    public IReadOnlyList<string> ManagedFiles { get; }

    /// <summary>共有ZIPを変更せず、ケースが所有する新規installationへ展開します。</summary>
    public async Task<string> ExtractInstallationAsync(TestWorkspace workspace, string relativePath,
        ProcessDeadline deadline, CancellationToken cancellationToken = default)
    {
        await DistributionValidation.AssertIdentityAsync(PackagePath, SizeBytes, Sha256, deadline, cancellationToken);
        string root = workspace.GetPath(relativePath);
        if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("installationは新しいケース専有パスが必要です。"); }
        DistributionValidation.CheckDeadline(deadline, cancellationToken);
        Directory.CreateDirectory(root);
        using ZipArchive archive = ZipFile.OpenRead(PackagePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = DistributionValidation.NormalizePath(entry.FullName.TrimEnd('/', '\\'));
            string destination = Path.GetFullPath(Path.Combine(root, name));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("ZIPの展開先がinstallation外です。");
            }
            DistributionValidation.CheckDeadline(deadline, cancellationToken);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidDataException("展開先の親がありません。"));
            await using Stream input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            using CancellationTokenSource budget = DistributionValidation.CreateBudget(deadline, cancellationToken);
            await input.CopyToAsync(output, budget.Token);
        }
        DistributionValidation.CheckDeadline(deadline, cancellationToken);
        return root;
    }
}

/// <summary>一回のFull前段で揃えた三つの配布入力です。未完成のcatalogは公開しません。</summary>
public sealed record DistributionCatalog(DistributionArtifact Legacy, DistributionArtifact Current, DistributionArtifact Next)
{
    /// <summary>runnerと直接module実行で共用するcatalogの環境変数名です。</summary>
    public const string EnvironmentVariable = "BMS_TEST_DISTRIBUTION_CATALOG";

    /// <summary>指定済みcatalogと、呼出元が信頼するrepositoryの旧版descriptorを照合します。未指定・不正入力では自動publish・取得せず失敗します。</summary>
    public static Task<DistributionCatalog> ReadFromEnvironmentAsync(string repository, ProcessDeadline deadline, CancellationToken cancellationToken = default)
    {
        string path = Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? throw new InvalidOperationException($"配布catalogを{EnvironmentVariable}で指定してください。");
        return ReadAsync(path, repository, deadline, cancellationToken);
    }

    /// <summary>catalogの旧版を信頼するrepositoryの追跡descriptorと照合し、全ZIPの同一性・管理対象を検査してから利用側へ渡します。旧ZIPのコピー先はcacheと同じでなくても構いません。</summary>
    public static async Task<DistributionCatalog> ReadAsync(string path, string repository, ProcessDeadline deadline, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path)) { throw new InvalidDataException("catalogには絶対パスが必要です。"); }
        using CancellationTokenSource budget = DistributionValidation.CreateBudget(deadline, cancellationToken);
        await using FileStream input = File.OpenRead(path);
        DistributionCatalog catalog = await JsonSerializer.DeserializeAsync<DistributionCatalog>(input, cancellationToken: budget.Token)
            ?? throw new InvalidDataException("catalogが空です。");
        if (catalog.Legacy is null || catalog.Current is null || catalog.Next is null) { throw new InvalidDataException("catalogに全配布物が必要です。"); }
        _ = DistributionValidation.ParseVersion(catalog.Legacy.Version);
        if (catalog.Legacy.AppPublishRoot is not null || catalog.Legacy.UpdaterPublishRoot is not null || catalog.Legacy.ManagedFiles.Count != 0)
        {
            throw new InvalidDataException("固定旧版のcatalogが不正です。");
        }
        (string Version, string CachePath, Uri DownloadUri, long SizeBytes, string Sha256) legacyDescriptor = await DistributionPreparer.ReadLegacyDescriptorAsync(repository, deadline, cancellationToken);
        if (catalog.Legacy.Version != legacyDescriptor.Version || catalog.Legacy.SizeBytes != legacyDescriptor.SizeBytes
            || !string.Equals(catalog.Legacy.Sha256, legacyDescriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("catalogの固定旧版identityが追跡descriptorと一致しません。");
        }
        foreach (DistributionArtifact artifact in new[] { catalog.Legacy, catalog.Current, catalog.Next })
        {
            if (!Path.IsPathFullyQualified(artifact.PackagePath)) { throw new InvalidDataException("ZIPには絶対パスが必要です。"); }
            await DistributionValidation.AssertIdentityAsync(artifact.PackagePath, artifact.SizeBytes, artifact.Sha256, deadline, cancellationToken);
        }
        foreach (DistributionArtifact artifact in new[] { catalog.Current, catalog.Next })
        {
            if (artifact.AppPublishRoot is not { } app || !Path.IsPathFullyQualified(app)
                || artifact.UpdaterPublishRoot is not { } updater || !Path.IsPathFullyQualified(updater)
                || !Directory.Exists(app) || !Directory.Exists(updater)) { throw new InvalidDataException("publish出力が不正です。"); }
            IReadOnlyList<string> managed = await DistributionValidation.ValidateZipAsync(artifact.PackagePath, app, updater, artifact.Version, deadline, cancellationToken);
            if (!new HashSet<string>(managed, StringComparer.OrdinalIgnoreCase).SetEquals(artifact.ManagedFiles))
            {
                throw new InvalidDataException("catalogとZIPの管理対象が一致しません。");
            }
        }
        if (catalog.Next.Version != DistributionValidation.IncrementVersion(catalog.Current.Version)
            || string.Equals(catalog.Current.AppPublishRoot, catalog.Next.AppPublishRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(catalog.Current.UpdaterPublishRoot, catalog.Next.UpdaterPublishRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(catalog.Current.PackagePath, catalog.Next.PackagePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("現行版と次版が独立していません。");
        }
        DistributionValidation.CheckDeadline(deadline, cancellationToken);
        return catalog;
    }
}

/// <summary>配布準備とconsumerで共用する、ZIP・実バイナリ・期限の検査です。</summary>
internal static class DistributionValidation
{
    private const string ManifestName = "update-managed-files.txt";
    // 採用済み音声部品と既存Everything資産だけを固定する。一般依存のhash台帳へ広げない。
    private static readonly IReadOnlyDictionary<string, string> NativePins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["libs/x64/bass.dll"] = "FEBB2CF1882D554C3A958280777DA0B69F07DE6E262DF271DE11C56E4A54AFD4",
        ["libs/x64/bassmix.dll"] = "3A1777CD14C0FC6E2D6F879CE9B66CB60CD1C6607F344D70D2434164526063AB",
        ["libs/x64/bassenc.dll"] = "9D8EE8D750DEF93E927E62E35D02A4CC8457C509CFA561C47AED3381691F51F8",
        ["libs/x64/basswasapi.dll"] = "6F0869C11431E01F759FBE1CD6080299C833C519EB8AB1FEAE12106907B1FBD1",
        ["libs/x64/bass_fx.dll"] = "A6E1847EEF52D882B4137AF514D834C2E220DACEB417C821D1E502FB7A34C84A",
        ["libs/x64/bassasio.dll"] = "73BF79C8ECCD63DEA8EB3E3E9B5FFE6F9406DEB9BBCCCC7557CA54F5013B4B96",
        ["libs/x64/bms_vorbis.dll"] = "403287F5E79580C4987F5018AF7FFE5B180F63819B10D09EA744AF5465D360C9",
        ["native/Everything3_x64.dll"] = "BE25B01C73BBF359B50DDF30255133225F93B4BC40A8D208173319373BCDAA5C",
        ["native/EverythingBridge_x64.dll"] = "886298F310E3FE3F73E635431E380277E21A740844D5D3E093AB5AFC867F2E3B",
    };

    /// <summary>Win32 ProductVersionに埋め込まれたInformationalVersionを実効版として読みます。</summary>
    internal static string ReadVersion(string executable)
    {
        string value = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? throw new InvalidDataException("実行ファイルに版がありません。");
        _ = ParseVersion(value);
        return value;
    }

    /// <summary>四成分版の末尾だけをcheckedで進めます。</summary>
    internal static string IncrementVersion(string value)
    {
        Version version = ParseVersion(value);
        return new Version(version.Major, version.Minor, version.Build, checked(version.Revision + 1)).ToString(4);
    }

    /// <summary>四成分の実効版を検査します。AssemblyVersionの定義は読み替えません。</summary>
    internal static Version ParseVersion(string value) => Version.TryParse(value, out Version? version) && version.Revision >= 0
        && version.ToString(4) == value ? version : throw new InvalidDataException("実効版は四成分の数値版が必要です。");

    /// <summary>入力の相対file pathを一形式へ揃え、installation外への展開を拒否します。</summary>
    internal static string NormalizePath(string value)
    {
        string path = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':')
            || path.Split('/').Any(part => part is "" or "." or "..")) { throw new InvalidDataException("ZIP/manifestの相対パスが不正です。"); }
        return path;
    }

    /// <summary>取消と絶対期限を各I/Oの境界で確認します。</summary>
    internal static void CheckDeadline(ProcessDeadline deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("配布準備の絶対期限を超過しました。"); }
    }

    /// <summary>既存の絶対期限の残時間だけを非同期I/Oへ渡します。</summary>
    internal static CancellationTokenSource CreateBudget(ProcessDeadline deadline, CancellationToken cancellationToken)
    {
        CheckDeadline(deadline, cancellationToken);
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(deadline.Remaining);
        return budget;
    }

    /// <summary>ファイルを共有読取りでhashし、全読取りも同じ絶対期限へ収めます。</summary>
    internal static async Task<string> HashAsync(string path, ProcessDeadline deadline, CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CreateBudget(deadline, cancellationToken);
        await using FileStream input = File.OpenRead(path);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, budget.Token));
        CheckDeadline(deadline, cancellationToken);
        return hash;
    }

    /// <summary>固定されたサイズとhashの両方を検査します。</summary>
    internal static async Task AssertIdentityAsync(string path, long size, string hash, ProcessDeadline deadline, CancellationToken cancellationToken)
    {
        CheckDeadline(deadline, cancellationToken);
        if (size <= 0 || new FileInfo(path).Length != size) { throw new InvalidDataException("配布ZIPのsizeが一致しません。"); }
        if (!string.Equals(await HashAsync(path, deadline, cancellationToken), hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("配布ZIPのSHA-256が一致しません。");
        }
    }

    /// <summary>publish実体とZIPの版・管理対象・native pin・test依存を検査します。</summary>
    internal static async Task<IReadOnlyList<string>> ValidateZipAsync(string package, string appRoot, string updaterRoot, string version,
        ProcessDeadline deadline, CancellationToken cancellationToken)
    {
        CheckDeadline(deadline, cancellationToken);
        if (ReadVersion(Path.Combine(appRoot, "BeMusicSeeker.exe")) != version) { throw new InvalidDataException("実行ファイルの実効版が一致しません。"); }
        using ZipArchive archive = ZipFile.OpenRead(package);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = NormalizePath(entry.FullName.TrimEnd('/', '\\'));
            string top = name.Split('/')[0];
            if (new[] { "config", "data", "log", "logs", "update_work", "update_backup", "imported_metadata" }.Contains(top, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("配布ZIPに実行時データ領域が含まれています: " + name);
            }
            AssertNoTestDependency(name);
            if (!entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\') && !entries.TryAdd(name, entry))
            {
                throw new InvalidDataException("ZIP内のfile pathが重複しています。");
            }
        }
        if (!entries.TryGetValue(ManifestName, out ZipArchiveEntry? manifest)) { throw new InvalidDataException("ZIPに管理対象manifestがありません。"); }
        using CancellationTokenSource budget = CreateBudget(deadline, cancellationToken);
        using var reader = new StreamReader(manifest.Open());
        string text = await reader.ReadToEndAsync(budget.Token);
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = NormalizePath(line);
            if (!managed.Add(name)) { throw new InvalidDataException("管理対象manifestに重複があります。"); }
        }
        if (!managed.SetEquals(entries.Keys.Where(name => !name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException("ZIPと管理対象manifestのfile集合が一致しません。");
        }
        foreach ((string name, string root) in new[] { ("BeMusicSeeker.exe", appRoot), ("BeMusicSeeker.Updater.exe", updaterRoot) })
        {
            if (!entries.TryGetValue(name, out ZipArchiveEntry? executable) || !File.Exists(Path.Combine(root, name)))
            {
                throw new InvalidDataException("ZIPまたはpublishに実行ファイルがありません: " + name);
            }
            await using Stream input = executable.Open();
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, budget.Token));
            if (hash != await HashAsync(Path.Combine(root, name), deadline, cancellationToken))
            {
                throw new InvalidDataException("ZIPとpublishの実行ファイル実体が一致しません: " + name);
            }
        }
        foreach ((string name, string pin) in NativePins)
        {
            if (!entries.TryGetValue(name, out ZipArchiveEntry? native)) { throw new InvalidDataException("ZIPにnative部品がありません: " + name); }
            await using Stream input = native.Open();
            using var bytes = new MemoryStream();
            await input.CopyToAsync(bytes, budget.Token);
            bytes.Position = 0;
            using var pe = new PEReader(bytes, PEStreamOptions.LeaveOpen);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) { throw new InvalidDataException("native部品がAMD64ではありません: " + name); }
            bytes.Position = 0;
            if (Convert.ToHexString(await SHA256.HashDataAsync(bytes, budget.Token)) != pin
                || await HashAsync(Path.Combine(appRoot, name), deadline, cancellationToken) != pin)
            {
                throw new InvalidDataException("採用済みnative部品のSHA-256が一致しません: " + name);
            }
        }
        foreach (string file in Directory.EnumerateFiles(appRoot, "*", SearchOption.AllDirectories)) { AssertNoTestDependency(file); }
        CheckDeadline(deadline, cancellationToken);
        return Array.AsReadOnly(managed.ToArray());
    }

    /// <summary>配布物へtest runner/support依存が混入することを検出します。</summary>
    internal static void AssertNoTestDependency(string path)
    {
        string name = Path.GetFileName(path.Replace('\\', '/'));
        if (name.Contains("TestSupport", StringComparison.OrdinalIgnoreCase) || name.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ProcessFixture", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WindowsProcessProbe", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("BeMusicSeeker.Verify", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.Testing.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.TestPlatform.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.VisualStudio.TestPlatform.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.VisualStudio.TestTools", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("配布物にtest依存が含まれています: " + path);
        }
    }
}
