using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>小さい実ZIPと採用済みPEを使い、配布入力の検査境界を保証します。実publish受入はFullが担当します。</summary>
[TestClass]
public sealed class DistributionArtifactTests
{
    /// <summary>manifestの行順・改行によらず全file集合を受理し、実binaryの版だけを正本にします。</summary>
    [TestMethod]
    public async Task ValidZipUsesBinaryVersionAndManifestSet()
    {
        using var fixture = new DistributionFixture();
        DistributionArtifact artifact = fixture.Create("v");
        IReadOnlyList<string> managed = await DistributionValidation.ValidateZipAsync(artifact.PackagePath, fixture.App("v"), fixture.PathFor("v/updater"), artifact.Version, Deadline(), default);
        CollectionAssert.AreEquivalent(artifact.ManagedFiles.ToArray(), managed.ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DistributionValidation.ValidateZipAsync(
            artifact.PackagePath, fixture.App("v"), fixture.PathFor("v/updater"), DistributionValidation.IncrementVersion(artifact.Version), Deadline(), default));
    }

    /// <summary>ZIPの内容だけを一条件ずつ変え、管理対象欠落・余分なfile・保護領域・native pin・AMD64・test依存を区別して拒否します。</summary>
    [TestMethod]
    [DataRow("manifest")]
    [DataRow("extra")]
    [DataRow("config")]
    [DataRow("data")]
    [DataRow("log")]
    [DataRow("logs")]
    [DataRow("update_work")]
    [DataRow("update_backup")]
    [DataRow("imported_metadata")]
    [DataRow("native-hash")]
    [DataRow("native-machine")]
    [DataRow("test-dependency")]
    [DataRow("updater-content")]
    public async Task InvalidZipIsRejectedAtTheDistributionBoundary(string mutation)
    {
        using var fixture = new DistributionFixture();
        DistributionArtifact artifact = fixture.Create("v");
        using (ZipArchive archive = ZipFile.Open(artifact.PackagePath, ZipArchiveMode.Update))
        {
            if (mutation == "manifest")
            {
                archive.GetEntry("update-managed-files.txt")?.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("update-managed-files.txt").Open());
                writer.Write("BeMusicSeeker.exe");
            }
            else if (mutation.StartsWith("native-", StringComparison.Ordinal))
            {
                const string name = "libs/x64/bass.dll";
                ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException();
                byte[] bytes;
                using (Stream input = entry.Open()) { using var copy = new MemoryStream(); input.CopyTo(copy); bytes = copy.ToArray(); }
                entry.Delete();
                if (mutation == "native-hash") { bytes[^1] ^= 1; }
                else { int pe = BitConverter.ToInt32(bytes, 0x3c); bytes[pe + 4] = 0x4c; bytes[pe + 5] = 0x01; }
                using Stream output = archive.CreateEntry(name).Open();
                output.Write(bytes);
            }
            else if (mutation == "updater-content")
            {
                archive.GetEntry("BeMusicSeeker.Updater.exe")?.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("BeMusicSeeker.Updater.exe").Open());
                writer.Write("changed updater");
            }
            else
            {
                string name = mutation == "extra" ? "extra.txt" : mutation == "test-dependency" ? "Microsoft.Testing.Platform.dll" : mutation + "/sentinel.txt";
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) { writer.Write("mutation"); }
                if (mutation != "extra")
                {
                    // 禁止対象の拒否をmanifest不一致で代用できないよう、追加fileも管理対象へ含める。
                    archive.GetEntry("update-managed-files.txt")?.Delete();
                    using var writer = new StreamWriter(archive.CreateEntry("update-managed-files.txt").Open());
                    writer.Write(string.Join("\n", artifact.ManagedFiles.Append(name)));
                }
            }
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DistributionValidation.ValidateZipAsync(
            artifact.PackagePath, fixture.App("v"), fixture.PathFor("v/updater"), artifact.Version, Deadline(), default));
    }

    /// <summary>既存Everything二資産の欠落・hash変更・誤アーキテクチャを、manifest自体は整合したZIPで一条件ずつ拒否します。</summary>
    [TestMethod]
    [DataRow("Everything3_x64.dll", "missing")]
    [DataRow("Everything3_x64.dll", "hash")]
    [DataRow("Everything3_x64.dll", "machine")]
    [DataRow("EverythingBridge_x64.dll", "missing")]
    [DataRow("EverythingBridge_x64.dll", "hash")]
    [DataRow("EverythingBridge_x64.dll", "machine")]
    public async Task ExistingEverythingAssetsAreRequiredWithTheirPinnedHashAndAmd64(string fileName, string mutation)
    {
        using var fixture = new DistributionFixture();
        DistributionArtifact artifact = fixture.Create("v");
        string name = "native/" + fileName;
        using (ZipArchive archive = ZipFile.Open(artifact.PackagePath, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException();
            byte[] bytes;
            using (Stream input = entry.Open()) { using var copy = new MemoryStream(); input.CopyTo(copy); bytes = copy.ToArray(); }
            entry.Delete();
            if (mutation == "missing")
            {
                archive.GetEntry("update-managed-files.txt")?.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("update-managed-files.txt").Open());
                writer.Write(string.Join("\n", artifact.ManagedFiles.Where(path => path != name)));
            }
            else
            {
                if (mutation == "hash") { bytes[^1] ^= 1; }
                else { int pe = BitConverter.ToInt32(bytes, 0x3c); bytes[pe + 4] = 0x4c; bytes[pe + 5] = 0x01; }
                using Stream output = archive.CreateEntry(name).Open();
                output.Write(bytes);
            }
        }
        InvalidDataException error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DistributionValidation.ValidateZipAsync(
            artifact.PackagePath, fixture.App("v"), fixture.PathFor("v/updater"), artifact.Version, Deadline(), default));
        StringAssert.Contains(error.Message, name);
    }

    /// <summary>ZIPは正常なままpublish updaterだけが欠落・置換された場合も、consumerがcatalogを受理しないことを保証します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CatalogRejectsMissingOrReplacedPublishedUpdater(bool replace)
    {
        using var fixture = new DistributionFixture();
        fixture.CreateLegacy();
        DistributionArtifact legacy = await DistributionPreparer.PrepareLegacyAsync(fixture.Root, Deadline(), default);
        DistributionArtifact current = fixture.Create("v");
        DistributionArtifact next = fixture.Create("next", DistributionValidation.IncrementVersion(current.Version));
        string catalogPath = fixture.PathFor("catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new DistributionCatalog(legacy, current, next)));
        string updater = fixture.PathFor("v/updater/BeMusicSeeker.Updater.exe");
        if (replace) { File.WriteAllText(updater, "replaced updater"); } else { File.Delete(updater); }
        InvalidDataException error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DistributionCatalog.ReadAsync(catalogPath, fixture.Root, Deadline()));
        StringAssert.Contains(error.Message, "BeMusicSeeker.Updater.exe");
    }

    /// <summary>準備済catalogの旧版だけを自己整合した別ZIP、同サイズ改変、または別Versionへ置換しても、正本descriptorとの相違を拒否します。</summary>
    [TestMethod]
    [DataRow("current-package")]
    [DataRow("same-size")]
    [DataRow("version")]
    public async Task CatalogRequiresTheTrustedLegacyIdentityBeyondItsOwnSizeAndHash(string mutation)
    {
        using var fixture = new DistributionFixture();
        (string Path, DistributionCatalog Catalog) prepared = await fixture.PrepareCatalogAsync();
        DistributionArtifact legacy = prepared.Catalog.Legacy;
        DistributionArtifact replacement;
        if (mutation == "current-package")
        {
            DistributionArtifact current = prepared.Catalog.Current;
            replacement = new(legacy.Version, null, null, current.PackagePath, current.SizeBytes, current.Sha256, []);
        }
        else if (mutation == "same-size")
        {
            byte[] bytes = File.ReadAllBytes(legacy.PackagePath);
            bytes[^1] ^= 1;
            string path = fixture.PathFor("other-legacy.zip");
            File.WriteAllBytes(path, bytes);
            replacement = new(legacy.Version, null, null, path, legacy.SizeBytes, Convert.ToHexString(SHA256.HashData(bytes)), []);
        }
        else
        {
            replacement = new(DistributionValidation.IncrementVersion(legacy.Version), null, null,
                legacy.PackagePath, legacy.SizeBytes, legacy.Sha256, []);
        }
        File.WriteAllText(prepared.Path, JsonSerializer.Serialize(prepared.Catalog with { Legacy = replacement }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DistributionCatalog.ReadAsync(prepared.Path, fixture.Root, Deadline()));
    }

    /// <summary>catalog近傍の一致する別descriptorへ切り替えず、明示repositoryの正本欠落・不正・identity相違をそのまま失敗にします。</summary>
    [TestMethod]
    [DataRow("different")]
    [DataRow("missing")]
    [DataRow("malformed")]
    public async Task CatalogDoesNotFallBackFromTheExplicitTrustedRepository(string trustedDescriptor)
    {
        using var fixture = new DistributionFixture();
        (string Path, DistributionCatalog Catalog) prepared = await fixture.PrepareCatalogAsync();
        string nearbyRoot = fixture.PathFor("catalog-neighbor");
        Directory.CreateDirectory(nearbyRoot);
        string package = Path.Combine(nearbyRoot, "legacy.zip");
        File.Copy(prepared.Catalog.Current.PackagePath, package);
        var replacement = new DistributionArtifact(prepared.Catalog.Legacy.Version, null, null, package,
            prepared.Catalog.Current.SizeBytes, prepared.Catalog.Current.Sha256, []);
        string catalogPath = Path.Combine(nearbyRoot, "catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(prepared.Catalog with { Legacy = replacement }));
        string metadataRelative = "devdocs/acceptance/v216-first-hop/artifact.json";
        string trustedPath = fixture.PathFor(metadataRelative);
        JsonNode nearby = JsonNode.Parse(File.ReadAllText(trustedPath)) ?? throw new InvalidDataException();
        nearby["sizeBytes"] = replacement.SizeBytes;
        nearby["sha256"] = replacement.Sha256;
        string nearbyPath = Path.Combine(nearbyRoot, metadataRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(nearbyPath) ?? throw new InvalidDataException());
        File.WriteAllText(nearbyPath, nearby.ToJsonString());
        if (trustedDescriptor == "missing") { File.Delete(trustedPath); }
        else if (trustedDescriptor == "malformed") { File.WriteAllText(trustedPath, "{"); }
        await Assert.ThrowsAsync<Exception>(() => DistributionCatalog.ReadAsync(catalogPath, fixture.Root, Deadline()));
    }

    /// <summary>旧ZIPのコピー先を受理し、元cacheが不在でもconsumerが再取得を始めず正本identityだけを照合します。</summary>
    [TestMethod]
    public async Task CatalogAcceptsTheCorrectLegacyCopyWithoutFetchingTheMissingCache()
    {
        using var fixture = new DistributionFixture();
        (string Path, DistributionCatalog Catalog) prepared = await fixture.PrepareCatalogAsync();
        DistributionArtifact legacy = prepared.Catalog.Legacy;
        string copy = fixture.PathFor("copied-legacy.zip");
        File.Move(legacy.PackagePath, copy);
        var relocated = new DistributionArtifact(legacy.Version, null, null, copy, legacy.SizeBytes, legacy.Sha256, []);
        File.WriteAllText(prepared.Path, JsonSerializer.Serialize(prepared.Catalog with { Legacy = relocated }));
        DistributionCatalog result = await DistributionCatalog.ReadAsync(prepared.Path, fixture.Root, Deadline());
        Assert.AreEqual(copy, result.Legacy.PackagePath);
        Assert.IsFalse(File.Exists(legacy.PackagePath));
    }

    /// <summary>親環境を変えず子へcatalog環境変数とtrusted repositoryを渡し、直接module用wrapperが正例を受理し自己整合した別旧ZIPを拒否します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChildEnvironmentConnectsTheCatalogReaderToTheTrustedDescriptor(bool replaceLegacy)
    {
        using var fixture = new DistributionFixture();
        (string Path, DistributionCatalog Catalog) prepared = await fixture.PrepareCatalogAsync();
        if (replaceLegacy)
        {
            DistributionArtifact current = prepared.Catalog.Current;
            var replacement = new DistributionArtifact(prepared.Catalog.Legacy.Version, null, null,
                current.PackagePath, current.SizeBytes, current.Sha256, []);
            File.WriteAllText(prepared.Path, JsonSerializer.Serialize(prepared.Catalog with { Legacy = replacement }));
        }
        string executable = Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe");
        await using var process = OwnedProcess.Start(new ProcessStartRequest(executable, ["distribution-catalog", fixture.Root], fixture.Root,
            new Dictionary<string, string?> { [DistributionCatalog.EnvironmentVariable] = prepared.Path }));
        ProcessCompletion result = await process.WaitForExitAsync(Deadline());
        ProcessStopResult stopped = await process.StopAsync(null, Deadline());
        Assert.IsNull(stopped.CleanupFailure);
        Assert.AreEqual(replaceLegacy ? 3 : 0, result.ExitCode);
        if (replaceLegacy) { Assert.IsFalse(string.IsNullOrWhiteSpace(result.StandardError)); }
        else { Assert.AreEqual(prepared.Catalog.Legacy.Version, result.StandardOutput.Trim()); }
    }

    /// <summary>サイズ違いと同サイズ改変を独立に拒否し、hashだけまたはサイズだけの同一性判定を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IdentityRequiresBothSizeAndHash(bool changeSize)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("package.zip");
        byte[] original = [1, 2, 3];
        File.WriteAllBytes(path, changeSize ? [1, 2, 3, 4] : [1, 2, 4]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DistributionValidation.AssertIdentityAsync(path,
            original.Length, Convert.ToHexString(SHA256.HashData(original)), Deadline(), default));
    }

    /// <summary>各ケースがZIPから別installationを得て、片方の変更が共有ZIP・別ケースへ伝わらないことを保証します。</summary>
    [TestMethod]
    public async Task CaseInstallationsDoNotMutateSharedArtifacts()
    {
        using var fixture = new DistributionFixture();
        DistributionArtifact artifact = fixture.Create("v");
        using var first = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        using var second = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string firstRoot = await artifact.ExtractInstallationAsync(first, "installation", Deadline());
        string secondRoot = await artifact.ExtractInstallationAsync(second, "installation", Deadline());
        File.WriteAllText(Path.Combine(firstRoot, "payload.txt"), "changed");
        Assert.AreEqual("payload", File.ReadAllText(Path.Combine(secondRoot, "payload.txt")));
        Assert.AreEqual(artifact.Sha256, await DistributionValidation.HashAsync(artifact.PackagePath, Deadline(), default));
    }

    /// <summary>不完全JSON・catalog欠落を拒否し、入力不足から自動publishを始めないconsumer境界を保証します。</summary>
    [TestMethod]
    public async Task MissingAndMalformedCatalogFail()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => DistributionCatalog.ReadAsync(workspace.GetPath("missing.json"), workspace.RootDirectory, Deadline()));
        string path = workspace.GetPath("catalog.json");
        File.WriteAllText(path, "{}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => DistributionCatalog.ReadAsync(path, workspace.RootDirectory, Deadline()));
        File.WriteAllText(path, "{");
        await Assert.ThrowsExactlyAsync<JsonException>(() => DistributionCatalog.ReadAsync(path, workspace.RootDirectory, Deadline()));
    }

    private static ProcessDeadline Deadline() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
}

/// <summary>版resource付き実PEと採用済nativeを専有領域へ複製する配布入力fixtureです。publishの成功を代用しません。</summary>
internal sealed class DistributionFixture : IDisposable
{
    private readonly TestWorkspace workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
    internal string Root => workspace.RootDirectory;
    internal string PathFor(string path) => workspace.GetPath(path);
    internal string App(string variant) => PathFor(variant + "/app");

    /// <summary>mockはcommand内の動的値を再解釈せず、既知の段階に対応する専有publish出力を作ります。引数の接続は実pwshの正例で検査します。</summary>
    internal DistributionArtifact CreatePublishedVariant(bool next) => Create(next ? "run/distribution/next" : "run/distribution/v",
        next ? DistributionValidation.IncrementVersion(DistributionValidation.ReadVersion(System.IO.Path.Combine(App("run/distribution/v"), "BeMusicSeeker.exe"))) : null);

    /// <summary>command出力だけを専有fixtureへ向け、全producer段階と正しいconsumer読取りを完了してからreader改変試験へ渡します。</summary>
    internal async Task<(string Path, DistributionCatalog Catalog)> PrepareCatalogAsync()
    {
        CreateLegacy();
        string path = await DistributionPreparer.PrepareAsync(Root, PathFor("run"), async (stage, action) =>
        {
            await action((_, arguments, _) =>
            {
                if (arguments[^1].Contains(" -SkipDocHtml -ArtifactRoot ", StringComparison.Ordinal))
                {
                    CreatePublishedVariant(stage == "publish-next");
                }
                return Task.FromResult(new ProcessCompletion(0, "", ""));
            }, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30)), default);
        });
        DistributionCatalog catalog = await DistributionCatalog.ReadAsync(path, Root, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30)));
        return (path, catalog);
    }

    /// <summary>正本と同じ入力形式の小さい旧配布ZIPとdescriptorをケース内で所有します。</summary>
    internal void CreateLegacy()
    {
        string path = PathFor("legacy.zip");
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("legacy.txt").Open());
            writer.Write("legacy");
        }
        string metadata = PathFor("devdocs/acceptance/v216-first-hop/artifact.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(metadata) ?? throw new InvalidDataException());
        File.WriteAllText(metadata, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            manifestType = "BeMusicSeeker.V216Artifact",
            packageFormatVersion = 1,
            version = "2.1.6.0",
            fileName = "legacy.zip",
            artifactPath = "legacy.zip",
            downloadUrl = "https://example.invalid/legacy.zip",
            sizeBytes = new FileInfo(path).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            @sealed = true,
        }));
    }

    /// <summary>生成版だけをWin32 version resourceへ同長で埋め、実FileVersionInfo読取りを通します。</summary>
    internal DistributionArtifact Create(string variant, string? version = null)
    {
        string app = App(variant);
        Directory.CreateDirectory(app);
        string original = typeof(BeMusicSeeker.App).Assembly.Location;
        string actual = FileVersionInfo.GetVersionInfo(original).ProductVersion ?? throw new InvalidDataException("fixture元の版がありません。");
        version ??= actual;
        byte[] binary = File.ReadAllBytes(original);
        if (version != actual)
        {
            byte[] before = Encoding.Unicode.GetBytes(actual + "\0"), after = Encoding.Unicode.GetBytes(version + "\0");
            if (before.Length != after.Length) { throw new InvalidOperationException("fixtureは同長の版resourceだけを変更します。"); }
            int offset = binary.AsSpan().IndexOf(before);
            if (offset < 0) { throw new InvalidDataException("fixture元のversion resourceがありません。"); }
            after.CopyTo(binary, offset);
        }
        File.WriteAllBytes(System.IO.Path.Combine(app, "BeMusicSeeker.exe"), binary);
        string repo = FindRepository();
        string native = System.IO.Path.Combine(app, "libs", "x64");
        Directory.CreateDirectory(native);
        foreach (string file in Directory.EnumerateFiles(System.IO.Path.Combine(repo, "vendor", "native", "x64"), "*.dll"))
        {
            File.Copy(file, System.IO.Path.Combine(native, System.IO.Path.GetFileName(file)));
        }
        string everything = System.IO.Path.Combine(app, "native");
        Directory.CreateDirectory(everything);
        foreach (string name in new[] { "Everything3_x64.dll", "EverythingBridge_x64.dll" })
        {
            File.Copy(System.IO.Path.Combine(repo, "native", name), System.IO.Path.Combine(everything, name));
        }
        File.WriteAllText(System.IO.Path.Combine(app, "payload.txt"), "payload");
        string updater = PathFor(variant + "/updater");
        Directory.CreateDirectory(updater);
        File.WriteAllText(System.IO.Path.Combine(updater, "BeMusicSeeker.Updater.exe"), "updater fixture");
        string dist = PathFor(variant + "/dist");
        Directory.CreateDirectory(dist);
        string zip = System.IO.Path.Combine(dist, $"bemusicseeker-unofficial-fork-v{version}.zip");
        string[] files = Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories)
            .Select(file => System.IO.Path.GetRelativePath(app, file).Replace('\\', '/')).Append("BeMusicSeeker.Updater.exe").ToArray();
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (string name in files)
            {
                archive.CreateEntryFromFile(name == "BeMusicSeeker.Updater.exe" ? System.IO.Path.Combine(updater, name) : System.IO.Path.Combine(app, name), name);
            }
            using var writer = new StreamWriter(archive.CreateEntry("update-managed-files.txt").Open());
            writer.Write(string.Join("\r\n", files.Reverse()));
        }
        return new(version, app, updater, zip, new FileInfo(zip).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))), files);
    }

    private static string FindRepository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "BeMusicSeeker.sln"))) { return directory.FullName; }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("fixture用repositoryがありません。");
    }

    public void Dispose() => workspace.Dispose();
}
