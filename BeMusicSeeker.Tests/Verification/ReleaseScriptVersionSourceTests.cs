using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class ReleaseScriptVersionSourceTests
{
    private const string Version = "9.9.9.9";
    private const string Tag = "v9.9.9.9";
    private const int ProcessCleanupTimeoutMilliseconds = 5_000;
    private const uint ToolhelpSnapshotProcess = 0x00000002;
    private const int ErrorNoMoreFiles = 18;
    private static readonly nint InvalidHandleValue = new(-1);

    private sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks);

    private sealed record ProcessSnapshotEntry(int ProcessId, int ParentProcessId);

    private sealed record ProcessCleanupResult(bool Succeeded, bool FoundResidual, string Diagnostic);

    [TestMethod]
    public void CreateDraftUsesAssemblyVersionAndCommitsOnlyGeneratedFilesWithMultipleAheadCommits()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: true, includeCompatibilityFile: false);
        fixture.AddCommit("source-a.txt", "first ahead commit");
        fixture.AddCommit("source-b.txt", "second ahead commit");
        string aheadHeadBefore = fixture.HeadCommit();
        string remoteMainBefore = fixture.RemoteMainCommit();

        ProcessResult result = fixture.RunRelease("-CreateDraft");

        AssertSuccess(result);
        Assert.AreEqual(remoteMainBefore, fixture.RemoteMainCommit(), result.CombinedOutput);
        Assert.AreEqual(fixture.HeadCommit(), fixture.LocalTagCommit(Tag));

        string[] generatedDiff = fixture.DiffNames(aheadHeadBefore);
        CollectionAssert.AreEquivalent(
            new[] { "docs/index.html", "docs/index.ja.html", "update.json" },
            generatedDiff,
            result.CombinedOutput);
        Assert.AreEqual(0, Directory.GetFiles(fixture.DistRoot, "update-v*.json").Length);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "version.txt")));

        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.UpdateManifestPath));
        Assert.AreEqual(Version, manifest.RootElement.GetProperty("version").GetString());
        Assert.AreEqual(Tag, manifest.RootElement.GetProperty("releaseTag").GetString());
        JsonElement assets = manifest.RootElement.GetProperty("assets");
        Assert.AreEqual(2, assets.GetArrayLength());

        AssertManifestAsset(assets[0], fixture.NormalPackagePath, "app", false);
        AssertManifestAsset(assets[1], fixture.MetadataPackagePath, "app-with-metadata", true);
    }

    [TestMethod]
    public void CreateDraftDoesNotRequireCompatibilityVersionFile()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: false, includeCompatibilityFile: false);

        ProcessResult result = fixture.RunRelease("-CreateDraft");

        AssertSuccess(result);
        Assert.IsTrue(File.Exists(fixture.UpdateManifestPath), result.CombinedOutput);
        Assert.AreEqual(fixture.HeadCommit(), fixture.LocalTagCommit(Tag));
    }

    [TestMethod]
    public void CreateDraftLeavesCompatibilityVersionFileUnchanged()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: false, includeCompatibilityFile: true);
        string compatibilityPath = Path.Combine(fixture.Root, "version.txt");
        string before = File.ReadAllText(compatibilityPath);

        ProcessResult result = fixture.RunRelease("-CreateDraft");

        AssertSuccess(result);
        Assert.AreEqual(before, File.ReadAllText(compatibilityPath));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CreateDraftPublishedAtIsReusedOnlyForTheSameRelease(bool sameRelease)
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: false, includeCompatibilityFile: false);
        const string previousPublishedAt = "2000-01-02T03:04:05Z";
        fixture.SeedExistingManifest(
            sameRelease ? Version : "1.0.0.0",
            sameRelease ? Tag : "v1.0.0.0",
            previousPublishedAt);

        ProcessResult result = fixture.RunRelease("-CreateDraft");

        AssertSuccess(result);
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.UpdateManifestPath));
        string? publishedAt = manifest.RootElement.GetProperty("publishedAt").GetString();
        if (sameRelease)
        {
            Assert.AreEqual(previousPublishedAt, publishedAt);
        }
        else
        {
            Assert.AreNotEqual(previousPublishedAt, publishedAt);
        }
    }

    [TestMethod]
    public void CreateDraftRequiresNormalPackage()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: false, includeCompatibilityFile: false);
        File.Delete(fixture.NormalPackagePath);

        ProcessResult result = fixture.RunRelease("-CreateDraft");

        AssertFailure(result);
        Assert.IsNull(fixture.LocalTagCommit(Tag));
        Assert.AreEqual(fixture.InitialHead, fixture.HeadCommit());
    }

    [TestMethod]
    public void PublishDraftPublishesAssetsBeforePushingMainAndThenVerifiesRemoteHead()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: true, includeCompatibilityFile: true);
        AssertSuccess(fixture.RunRelease("-CreateDraft"));
        string releaseHead = fixture.HeadCommit();
        string remoteMainBefore = fixture.RemoteMainCommit();
        int publishLogStart = fixture.GhLogLines().Count;

        ProcessResult result = fixture.RunRelease("-PublishDraft");

        AssertSuccess(result);
        Assert.AreNotEqual(remoteMainBefore, fixture.RemoteMainCommit());
        Assert.AreEqual(releaseHead, fixture.RemoteMainCommit());
        Assert.AreEqual("false", File.ReadAllText(fixture.GhStatePath).Trim());
        string[] publishCommands = fixture.GhLogLines().Skip(publishLogStart).ToArray();
        string[] releaseEdits = publishCommands
            .Where(line => line.StartsWith("release edit ", StringComparison.Ordinal))
            .ToArray();
        CollectionAssert.AreEqual(new[] { $"release edit {Tag} --draft=false" }, releaseEdits);
        Assert.IsFalse(releaseEdits[0].Contains("--notes-file", StringComparison.Ordinal));
        Assert.IsFalse(releaseEdits[0].Contains("--notes", StringComparison.Ordinal));
        Assert.IsFalse(releaseEdits[0].Contains("--title", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow("head")]
    [DataRow("remote-tag")]
    [DataRow("zip")]
    [DataRow("asset-size")]
    [DataRow("asset-set")]
    public void PublishDraftRejectsTagHeadZipManifestOrRemoteAssetMismatchWithoutPushingMain(string mismatch)
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: true, includeCompatibilityFile: true);
        AssertSuccess(fixture.RunRelease("-CreateDraft"));
        string remoteMainBefore = fixture.RemoteMainCommit();

        switch (mismatch)
        {
            case "head":
                fixture.AddCommit("after-release.txt", "head moved");
                break;
            case "remote-tag":
                fixture.SetRemoteTagCommit(fixture.InitialHead);
                break;
            case "zip":
                long packageLength = new FileInfo(fixture.NormalPackagePath).Length;
                string[] packageEntries = fixture.ZipEntryNames(fixture.NormalPackagePath);
                string packageHash = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(fixture.NormalPackagePath)));
                fixture.RewriteNormalPackageContent();
                Assert.AreEqual(packageLength, new FileInfo(fixture.NormalPackagePath).Length);
                CollectionAssert.AreEqual(packageEntries, fixture.ZipEntryNames(fixture.NormalPackagePath));
                Assert.AreNotEqual(
                    packageHash,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.NormalPackagePath))));
                break;
            case "asset-size":
                fixture.WriteRemoteAssetsJson(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        name = Path.GetFileName(fixture.NormalPackagePath),
                        size = new FileInfo(fixture.NormalPackagePath).Length + 1
                    },
                    new
                    {
                        name = Path.GetFileName(fixture.MetadataPackagePath),
                        size = new FileInfo(fixture.MetadataPackagePath).Length
                    }
                }));
                break;
            case "asset-set":
                fixture.WriteRemoteAssetsJson("[]");
                break;
            default:
                Assert.Fail("Unknown mismatch: " + mismatch);
                break;
        }

        ProcessResult result = fixture.RunRelease("-PublishDraft");

        Assert.AreNotEqual(0, result.ExitCode, result.CombinedOutput);
        Assert.AreEqual(remoteMainBefore, fixture.RemoteMainCommit(), result.CombinedOutput);
        Assert.IsFalse(fixture.GhLogLines().Any(line => line.Contains("--draft=false", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PublishDraftRejectsNonAncestorRemoteMainBeforePublishingAssets()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: false, includeCompatibilityFile: false);
        AssertSuccess(fixture.RunRelease("-CreateDraft"));
        string remoteMainBefore = fixture.RemoteMainCommit();
        fixture.AdvanceRemoteMainOnIndependentClone();

        ProcessResult result = fixture.RunRelease("-PublishDraft");

        AssertFailure(result);
        Assert.AreNotEqual(remoteMainBefore, fixture.RemoteMainCommit());
        Assert.IsFalse(fixture.GhLogLines().Any(line => line.Contains("--draft=false", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PublishDraftDoesNotPushMainWhenReleasePublicationFails()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: false, includeCompatibilityFile: false);
        AssertSuccess(fixture.RunRelease("-CreateDraft"));
        string remoteMainBefore = fixture.RemoteMainCommit();
        fixture.FailReleasePublication = true;

        ProcessResult result = fixture.RunRelease("-PublishDraft");

        Assert.AreNotEqual(0, result.ExitCode, result.CombinedOutput);
        Assert.AreEqual(remoteMainBefore, fixture.RemoteMainCommit());
    }

    [TestMethod]
    public void PublishDraftAllowsRerunAfterReleaseIsAlreadyPublished()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: false, includeCompatibilityFile: true);
        AssertSuccess(fixture.RunRelease("-CreateDraft"));
        AssertSuccess(fixture.RunRelease("-PublishDraft"));
        int publishCallsBefore = fixture.GhLogLines().Count(line => line.Contains("--draft=false", StringComparison.Ordinal));

        ProcessResult result = fixture.RunRelease("-PublishDraft");

        AssertSuccess(result);
        Assert.AreEqual(publishCallsBefore, fixture.GhLogLines().Count(line => line.Contains("--draft=false", StringComparison.Ordinal)));
        Assert.AreEqual(fixture.HeadCommit(), fixture.RemoteMainCommit());
    }

    [TestMethod]
    public void PreviewDraftTagsDevHeadAndLeavesTrackedReleaseFilesAndMainUnchanged()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: true, includeCompatibilityFile: true);
        fixture.CheckoutDevelopmentBranch();
        string headBefore = fixture.HeadCommit();
        string mainBefore = fixture.RemoteMainCommit();
        byte[] manifestBefore = File.ReadAllBytes(fixture.UpdateManifestPath);
        byte[] compatibilityBefore = File.ReadAllBytes(Path.Combine(fixture.Root, "version.txt"));

        ProcessResult result = fixture.RunRelease("-CreatePrereleaseDraft", "-PreviewSuffix", "preview.7");

        AssertSuccess(result);
        string previewTag = Tag + "-preview.7";
        Assert.AreEqual(headBefore, fixture.LocalTagCommit(previewTag));
        Assert.AreEqual(headBefore, fixture.RemoteTagCommit(previewTag));
        Assert.AreEqual(headBefore, fixture.HeadCommit());
        Assert.AreEqual(mainBefore, fixture.RemoteMainCommit());
        CollectionAssert.AreEqual(manifestBefore, File.ReadAllBytes(fixture.UpdateManifestPath));
        CollectionAssert.AreEqual(compatibilityBefore, File.ReadAllBytes(Path.Combine(fixture.Root, "version.txt")));
        Assert.IsTrue(fixture.GhLogLines().Any(line => line.Contains("--prerelease", StringComparison.Ordinal) &&
                                                        line.Contains("--draft", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PreviewDraftRejectsExistingPreviewTagAtAnotherHead()
    {
        using var fixture = ReleaseFixture.Create(includeMetadata: false, includeCompatibilityFile: true);
        fixture.CheckoutDevelopmentBranch();
        string previewTag = Tag + "-preview.1";
        fixture.CreateLocalTag(previewTag, fixture.InitialHead);
        fixture.AddCommit("dev-change.txt", "new dev head");

        ProcessResult result = fixture.RunRelease("-CreatePrereleaseDraft");

        Assert.AreNotEqual(0, result.ExitCode, result.CombinedOutput);
        Assert.AreEqual(fixture.InitialHead, fixture.LocalTagCommit(previewTag));
        Assert.IsFalse(fixture.GhLogLines().Any(line => line.Contains("release create", StringComparison.Ordinal)));
    }

    private static void AssertManifestAsset(JsonElement asset, string packagePath, string kind, bool includesMetadata)
    {
        FileInfo package = new(packagePath);
        Assert.AreEqual(kind, asset.GetProperty("kind").GetString());
        Assert.AreEqual(package.Name, asset.GetProperty("fileName").GetString());
        Assert.AreEqual(package.Length, asset.GetProperty("sizeBytes").GetInt64());
        Assert.AreEqual(includesMetadata, asset.GetProperty("includesChartInfoMetadata").GetBoolean());
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))).ToLowerInvariant();
        Assert.AreEqual(hash, asset.GetProperty("sha256").GetString());
    }

    private static void AssertSuccess(ProcessResult result)
    {
        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
    }

    private static void AssertFailure(ProcessResult result)
    {
        Assert.AreNotEqual(0, result.ExitCode, "PowerShell unexpectedly succeeded: " + result.CombinedOutput);
    }

    private sealed class ReleaseFixture : IDisposable
    {
        private static readonly string[] RequiredPackageEntries =
        [
            "BeMusicSeeker.exe",
            "BeMusicSeeker.dll.config",
            "test.mp3",
            "libs/x64/7z.dll",
            "native/Everything3_x64.dll",
            "native/EverythingBridge_x64.dll",
            "libs/x64/bass.dll",
            "libs/x64/bassasio.dll",
            "libs/x64/bassenc.dll",
            "libs/x64/bassmix.dll",
            "libs/x64/basswasapi.dll",
            "libs/x64/bass_fx.dll",
            "lang/en-US.json",
            "lang/fr-FR.json",
            "lang/ja-JP.json",
            "lang/ko-KR.json",
            "lang/zh-CN.json",
            "lang/zh-TW.json",
            "D3DCompiler_47_cor3.dll",
            "e_sqlite3.dll",
            "PenImc_cor3.dll",
            "PresentationNative_cor3.dll",
            "vcruntime140_cor3.dll",
            "wpfgfx_cor3.dll",
            "BeMusicSeeker.Updater.exe",
            "update-managed-files.txt"
        ];

        private ReleaseFixture(string root, bool includeMetadata, bool includeCompatibilityFile)
        {
            Root = root;
            DistRoot = Path.Combine(root, "dist");
            NormalPackagePath = Path.Combine(DistRoot, $"bemusicseeker-unofficial-fork-v{Version}.zip");
            MetadataPackagePath = Path.Combine(DistRoot, $"bemusicseeker-unofficial-fork-v{Version}-with-metadata.zip");
            ScriptPath = Path.Combine(root, "scripts", "release.ps1");
            UpdateManifestPath = Path.Combine(root, "update.json");
            RemotePath = Path.Combine(root, "remote.git");
            FakeBinRoot = Path.Combine(root, "fakebin");
            GhStatePath = Path.Combine(root, "gh-state.txt");
            GhAssetsPath = Path.Combine(root, "gh-assets.json");
            GhLogPath = Path.Combine(root, "gh.log");
            InitialHead = string.Empty;
            CreateFiles(includeMetadata, includeCompatibilityFile);
            InitializeGit();
        }

        public string Root { get; }

        public string DistRoot { get; }

        public string NormalPackagePath { get; }

        public string MetadataPackagePath { get; }

        public string ScriptPath { get; }

        public string UpdateManifestPath { get; }

        public string RemotePath { get; }

        public string FakeBinRoot { get; }

        public string GhStatePath { get; }

        public string GhAssetsPath { get; }

        public string GhLogPath { get; }

        public string InitialHead { get; private set; }

        public bool FailReleasePublication { get; set; }

        public static ReleaseFixture Create(bool includeMetadata, bool includeCompatibilityFile)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "BeMusicSeeker-ReleaseScriptVersionSourceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new ReleaseFixture(root, includeMetadata, includeCompatibilityFile);
        }

        public void AddCommit(string relativePath, string content)
        {
            File.WriteAllText(Path.Combine(Root, relativePath), content, Encoding.UTF8);
            RunGit("add", "--", relativePath);
            RunGit("commit", "-m", "source change");
        }

        public void SeedExistingManifest(string version, string releaseTag, string publishedAt)
        {
            string manifest = JsonSerializer.Serialize(new { version, releaseTag, publishedAt });
            File.WriteAllText(UpdateManifestPath, manifest + Environment.NewLine, Encoding.UTF8);
            RunGit("add", "--", "update.json");
            RunGit("commit", "-m", "seed existing update manifest");
        }

        public void CheckoutDevelopmentBranch()
        {
            RunGit("checkout", "-b", "dev");
            RunGit("push", "-u", "origin", "dev");
        }

        public void CreateLocalTag(string tag, string commit)
        {
            RunGit("tag", tag, commit);
        }

        public string HeadCommit()
        {
            return RunGit("rev-parse", "HEAD");
        }

        public string? LocalTagCommit(string tag)
        {
            ProcessResult result = RunProcess(
                "git",
                new[] { "rev-parse", "--verify", $"{tag}^{{commit}}" },
                Root,
                null);
            return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
        }

        public string? RemoteTagCommit(string tag)
        {
            ProcessResult result = RunProcess(
                "git",
                new[] { "ls-remote", "--tags", "origin", $"refs/tags/{tag}" },
                Root,
                null);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }
            return result.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        public string RemoteMainCommit()
        {
            string output = RunGit("ls-remote", "--heads", "origin", "refs/heads/main");
            return output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        public string[] DiffNames(string baseCommit)
        {
            return RunGit("diff", "--name-only", $"{baseCommit}..HEAD")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Replace('\\', '/'))
                .ToArray();
        }

        public void SetRemoteTagCommit(string commit)
        {
            RunGitAt(RemotePath, "update-ref", $"refs/tags/{Tag}", commit);
        }

        public void WriteRemoteAssetsJson(string json)
        {
            File.WriteAllText(GhAssetsPath, json, Encoding.ASCII);
        }

        public void RewriteNormalPackageContent()
        {
            using ZipArchive archive = ZipFile.Open(NormalPackagePath, ZipArchiveMode.Update);
            ZipArchiveEntry entry = archive.GetEntry("test.mp3")
                ?? throw new InvalidOperationException("Fixture ZIP did not contain test.mp3.");
            using Stream stream = entry.Open();
            stream.Position = 0;
            stream.WriteByte((byte)'y');
        }

        public string[] ZipEntryNames(string packagePath)
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            return archive.Entries.Select(entry => entry.FullName).ToArray();
        }

        public IReadOnlyList<string> GhLogLines()
        {
            return File.Exists(GhLogPath)
                ? File.ReadAllLines(GhLogPath)
                : Array.Empty<string>();
        }

        public ProcessResult RunRelease(params string[] arguments)
        {
            var environment = new Dictionary<string, string?>
            {
                ["GH_STATE"] = GhStatePath,
                ["GH_ASSETS"] = GhAssetsPath,
                ["GH_LOG"] = GhLogPath,
                ["GH_FAKE_BIN"] = FakeBinRoot
            };
            if (FailReleasePublication)
            {
                environment["GH_FAIL_EDIT"] = "1";
            }
            return RunProcess("pwsh", new[] { "-NoProfile", "-NonInteractive", "-File", ScriptPath }.Concat(arguments), Root, environment);
        }

        public void AdvanceRemoteMainOnIndependentClone()
        {
            string cloneRoot = Path.Combine(
                Path.GetDirectoryName(Root)!,
                "release-remote-mutator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cloneRoot);
            try
            {
                RunGitAt(Path.GetDirectoryName(cloneRoot)!, "clone", "--branch", "main", RemotePath, cloneRoot);
                File.WriteAllText(Path.Combine(cloneRoot, "remote-only.txt"), "remote main advanced", Encoding.UTF8);
                RunGitAt(cloneRoot, "config", "user.email", "release-tests@example.invalid");
                RunGitAt(cloneRoot, "config", "user.name", "Release Integration Tests");
                RunGitAt(cloneRoot, "add", "--", "remote-only.txt");
                RunGitAt(cloneRoot, "commit", "-m", "remote main change");
                RunGitAt(cloneRoot, "push", "origin", "main");
                RunGit("fetch", "origin", "main");
            }
            finally
            {
                if (Directory.Exists(cloneRoot))
                {
                    DeleteDirectory(cloneRoot);
                }
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                DeleteDirectory(Root);
            }
        }

        private static void DeleteDirectory(string path)
        {
            DirectoryInfo directory = new(path);
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                entry.Attributes = FileAttributes.Normal;
            }

            directory.Attributes = FileAttributes.Normal;
            directory.Delete(recursive: true);
        }

        private void CreateFiles(bool includeMetadata, bool includeCompatibilityFile)
        {
            Directory.CreateDirectory(Path.Combine(Root, "scripts"));
            Directory.CreateDirectory(Path.Combine(Root, "Properties"));
            Directory.CreateDirectory(Path.Combine(Root, "release notes"));
            Directory.CreateDirectory(Path.Combine(Root, "docs"));
            Directory.CreateDirectory(DistRoot);
            Directory.CreateDirectory(FakeBinRoot);

            string repositoryRoot = FindRepositoryRoot();
            File.Copy(Path.Combine(repositoryRoot, "scripts", "release.ps1"), ScriptPath);
            File.Copy(
                Path.Combine(repositoryRoot, "scripts", "portable-package-layout.ps1"),
                Path.Combine(Root, "scripts", "portable-package-layout.ps1"));
            File.WriteAllText(
                Path.Combine(Root, "Properties", "AssemblyInfo.cs"),
                "[assembly: AssemblyInformationalVersion(\"9.9.9.9\")]",
                Encoding.UTF8);
            string sourceNote = Directory.GetFiles(Path.Combine(repositoryRoot, "release notes"), "v2.1.6.0*").Single();
            File.Copy(sourceNote, Path.Combine(Root, "release notes", $"v{Version} リリースノート.md"));
            File.WriteAllText(Path.Combine(Root, "docs", "index.html"), "source", Encoding.UTF8);
            File.WriteAllText(Path.Combine(Root, "docs", "index.ja.html"), "source-ja", Encoding.UTF8);
            File.WriteAllText(Path.Combine(Root, "docs", ".nojekyll"), string.Empty, Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(Root, ".gitignore"),
                "dist/\nfakebin/\nremote.git/\ngh-state.txt\ngh-assets.json\ngh.log\n",
                Encoding.ASCII);

            if (includeCompatibilityFile)
            {
                File.WriteAllText(Path.Combine(Root, "version.txt"), "2.1.6.0\n", Encoding.UTF8);
            }

            File.WriteAllText(UpdateManifestPath, "{\"unchanged\":true}\n", Encoding.UTF8);
            CreateZip(NormalPackagePath, includeMetadata: false);
            if (includeMetadata)
            {
                CreateZip(MetadataPackagePath, includeMetadata: true);
            }

            File.Delete(GhStatePath);
            File.WriteAllText(GhAssetsPath, BuildAssetJson(includeMetadata), Encoding.ASCII);
            File.WriteAllText(GhLogPath, string.Empty, Encoding.UTF8);
            File.WriteAllText(Path.Combine(FakeBinRoot, "gh.cmd"), FakeGhScript, Encoding.ASCII);
            File.WriteAllText(Path.Combine(FakeBinRoot, "uv.cmd"), FakeUvScript, Encoding.ASCII);
        }

        private void InitializeGit()
        {
            RunGit("init");
            RunGit("checkout", "-b", "main");
            RunGit("config", "user.email", "release-tests@example.invalid");
            RunGit("config", "user.name", "Release Integration Tests");
            RunGit("add", ".");
            RunGit("commit", "-m", "fixture base");
            InitialHead = HeadCommit();
            RunGit("init", "--bare", RemotePath);
            RunGit("remote", "add", "origin", RemotePath);
            RunGit("push", "-u", "origin", "main");
        }

        private void CreateZip(string path, bool includeMetadata)
        {
            using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (string entryName in RequiredPackageEntries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(entryName);
                using Stream stream = entry.Open();
                stream.WriteByte((byte)'x');
            }
            if (includeMetadata)
            {
                ZipArchiveEntry entry = archive.CreateEntry("chart-info-metadata.7z");
                using Stream stream = entry.Open();
                stream.WriteByte((byte)'m');
            }
        }

        private string BuildAssetJson(bool includeMetadata)
        {
            var assets = new List<object>
            {
                new { name = Path.GetFileName(NormalPackagePath), size = new FileInfo(NormalPackagePath).Length }
            };
            if (includeMetadata)
            {
                assets.Add(new { name = Path.GetFileName(MetadataPackagePath), size = new FileInfo(MetadataPackagePath).Length });
            }
            return JsonSerializer.Serialize(assets);
        }

        private string RunGit(params string[] arguments)
        {
            return RunGitAt(Root, arguments);
        }

        private static string RunGitAt(string workingDirectory, params string[] arguments)
        {
            ProcessResult result = RunProcess("git", arguments, workingDirectory, null);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("git failed: " + result.CombinedOutput);
            }
            return result.StandardOutput.Trim();
        }

        private static ProcessResult RunProcess(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string?>? environment)
        {
            const int processTimeoutMilliseconds = 60_000;
            const int streamTimeoutMilliseconds = 5_000;
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            if (environment is not null)
            {
                foreach ((string key, string? value) in environment)
                {
                    if (value is null)
                    {
                        startInfo.Environment.Remove(key);
                    }
                    else
                    {
                        startInfo.Environment[key] = value;
                    }
                }
            }

            if (fileName.Equals("pwsh", StringComparison.OrdinalIgnoreCase) && environment is not null && environment.ContainsKey("GH_STATE"))
            {
                string fakeBin = environment.TryGetValue("GH_FAKE_BIN", out string? configuredFakeBin) && configuredFakeBin is not null
                    ? configuredFakeBin
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(fakeBin))
                {
                    throw new InvalidOperationException("GH_FAKE_BIN was not provided for release process.");
                }
                startInfo.Environment["PATH"] = fakeBin + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException($"Process did not start: {fileName}");
            }
            ProcessIdentity processIdentity = CaptureProcessIdentity(process, fileName);
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(processTimeoutMilliseconds))
            {
                ProcessCleanupResult cleanup = StopProcessTree(process, processIdentity);
                throw new InvalidOperationException(
                    $"Process timed out: {fileName}; cleanup={cleanup.Diagnostic}; " +
                    $"stdout={GetTaskValue(outputTask)}; stderr={GetTaskValue(errorTask)}");
            }
            try
            {
                if (!Task.WaitAll(new Task[] { outputTask, errorTask }, streamTimeoutMilliseconds))
                {
                    ProcessCleanupResult cleanup = StopProcessTree(process, processIdentity);
                    throw new InvalidOperationException(
                        $"Process output did not close: {fileName}; cleanup={cleanup.Diagnostic}");
                }
            }
            catch (Exception exception) when (exception is not InvalidOperationException)
            {
                ProcessCleanupResult cleanup = StopProcessTree(process, processIdentity);
                throw new InvalidOperationException(
                    $"Process output failed: {fileName}; cleanup={cleanup.Diagnostic}", exception);
            }

            string standardOutput = outputTask.GetAwaiter().GetResult();
            string standardError = errorTask.GetAwaiter().GetResult();
            int exitCode = process.ExitCode;
            ProcessCleanupResult completedCleanup = StopProcessTree(process, processIdentity);
            if (!completedCleanup.Succeeded)
            {
                string cleanupMessage = $"Process cleanup failed: {completedCleanup.Diagnostic}";
                if (exitCode == 0)
                {
                    throw new InvalidOperationException(
                        $"Process completed but owned process cleanup failed: {fileName}; " +
                        $"{cleanupMessage}; stdout={standardOutput}; stderr={standardError}");
                }
                standardError = AppendDiagnostic(standardError, cleanupMessage);
            }
            else if (completedCleanup.FoundResidual && exitCode == 0)
            {
                throw new InvalidOperationException(
                    $"Process completed with an owned process residual: {fileName}; " +
                    $"{completedCleanup.Diagnostic}; stdout={standardOutput}; stderr={standardError}");
            }

            return new ProcessResult(
                exitCode,
                standardOutput,
                standardError);
        }

        private static ProcessIdentity CaptureProcessIdentity(Process process, string fileName)
        {
            try
            {
                return new ProcessIdentity(
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Process identity could not be captured: {fileName}; {exception.Message}", exception);
            }
        }

        private static ProcessCleanupResult StopProcessTree(Process process, ProcessIdentity processIdentity)
        {
            var diagnostics = new List<string>();
            var knownDescendants = new Dictionary<int, ProcessIdentity>();
            bool rootExited = TryGetHasExited(process, diagnostics);
            long rootExitTimeUtcTicks = GetRootExitTimeUtcTicks(process, diagnostics);
            bool foundResidual = !rootExited;

            MergeOwnedDescendants(
                processIdentity,
                rootExitTimeUtcTicks,
                knownDescendants,
                diagnostics,
                ref foundResidual);

            if (!rootExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(
                        $"root PID {processIdentity.ProcessId} tree termination failed: " +
                        $"{exception.GetType().Name}: {exception.Message}");
                    TryTaskkill(processIdentity.ProcessId, diagnostics);
                }
            }

            foreach (ProcessIdentity descendant in knownDescendants.Values)
            {
                StopOwnedProcess(descendant, diagnostics);
            }

            WaitForOwnedProcessTreeToExit(
                process,
                processIdentity,
                rootExitTimeUtcTicks,
                knownDescendants,
                diagnostics,
                ref foundResidual);

            string diagnostic = diagnostics.Count == 0
                ? "completed"
                : string.Join("; ", diagnostics);
            return new ProcessCleanupResult(diagnostics.Count == 0, foundResidual, diagnostic);
        }

        private static bool TryGetHasExited(Process process, ICollection<string> diagnostics)
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"root process exit state could not be observed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private static long GetRootExitTimeUtcTicks(Process process, ICollection<string> diagnostics)
        {
            try
            {
                return process.HasExited
                    ? process.ExitTime.ToUniversalTime().Ticks
                    : long.MaxValue;
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"root process exit time could not be observed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return long.MaxValue;
            }
        }

        private static void MergeOwnedDescendants(
            ProcessIdentity root,
            long rootExitTimeUtcTicks,
            IDictionary<int, ProcessIdentity> knownDescendants,
            ICollection<string> diagnostics,
            ref bool foundResidual)
        {
            IReadOnlyList<ProcessSnapshotEntry> processTable;
            try
            {
                processTable = CaptureProcessTable();
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"owned process lineage could not be observed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return;
            }

            var childrenByParent = processTable
                .GroupBy(entry => entry.ParentProcessId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var reachable = new HashSet<int> { root.ProcessId };
            var pending = new Queue<int>();
            pending.Enqueue(root.ProcessId);
            while (pending.Count > 0)
            {
                int parentId = pending.Dequeue();
                if (!childrenByParent.TryGetValue(parentId, out List<ProcessSnapshotEntry>? children))
                {
                    continue;
                }
                foreach (ProcessSnapshotEntry child in children)
                {
                    if (reachable.Contains(child.ProcessId))
                    {
                        continue;
                    }
                    if (!TryGetProcessStartTimeUtcTicks(
                            child.ProcessId,
                            out long startTimeUtcTicks,
                            out string? error))
                    {
                        if (error is not null)
                        {
                            diagnostics.Add(
                                $"owned descendant PID {child.ProcessId} identity could not be observed: {error}");
                        }
                        continue;
                    }

                    bool directChild = child.ParentProcessId == root.ProcessId;
                    if (startTimeUtcTicks < root.StartTimeUtcTicks ||
                        (directChild && startTimeUtcTicks > rootExitTimeUtcTicks))
                    {
                        continue;
                    }

                    if (reachable.Add(child.ProcessId))
                    {
                        knownDescendants[child.ProcessId] = new ProcessIdentity(
                            child.ProcessId,
                            startTimeUtcTicks);
                        pending.Enqueue(child.ProcessId);
                    }
                }
            }
        }

        private static bool TryGetProcessStartTimeUtcTicks(
            int processId,
            out long startTimeUtcTicks,
            out string? error)
        {
            startTimeUtcTicks = 0;
            error = null;
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return false;
                }
                startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
                return false;
            }
        }

        private static void StopOwnedProcess(ProcessIdentity identity, ICollection<string> diagnostics)
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                if (process.HasExited)
                {
                    return;
                }
                if (!TryGetProcessStartTimeUtcTicks(
                        identity.ProcessId,
                        out long currentStartTimeUtcTicks,
                        out string? identityError))
                {
                    if (identityError is not null)
                    {
                        diagnostics.Add(
                            $"owned descendant PID {identity.ProcessId} could not be verified before termination: " +
                            identityError);
                    }
                    return;
                }
                if (currentStartTimeUtcTicks != identity.StartTimeUtcTicks)
                {
                    diagnostics.Add(
                        $"owned descendant PID {identity.ProcessId} was reused; " +
                        "the replacement process was not terminated");
                    return;
                }
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // The descendant exited between the snapshot and the handle open.
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"owned descendant PID {identity.ProcessId} termination failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        private static void WaitForOwnedProcessTreeToExit(
            Process rootProcess,
            ProcessIdentity root,
            long rootExitTimeUtcTicks,
            IDictionary<int, ProcessIdentity> knownDescendants,
            ICollection<string> diagnostics,
            ref bool foundResidual)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ProcessCleanupTimeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                bool rootExited = TryGetHasExited(rootProcess, diagnostics);
                MergeOwnedDescendants(
                    root,
                    rootExitTimeUtcTicks,
                    knownDescendants,
                    diagnostics,
                    ref foundResidual);
                bool descendantsAlive = false;
                foreach (ProcessIdentity descendant in knownDescendants.Values)
                {
                    if (IsProcessIdentityAlive(descendant))
                    {
                        descendantsAlive = true;
                        break;
                    }
                }
                if (rootExited && !descendantsAlive)
                {
                    return;
                }
                Thread.Sleep(50);
            }

            if (!TryGetHasExited(rootProcess, diagnostics))
            {
                diagnostics.Add(
                    $"root PID {root.ProcessId} remained active after " +
                    $"{ProcessCleanupTimeoutMilliseconds / 1000}s cleanup");
                TryTaskkill(root.ProcessId, diagnostics);
                if (!TryGetHasExited(rootProcess, diagnostics))
                {
                    diagnostics.Add($"root PID {root.ProcessId} remained active after taskkill fallback");
                }
            }
            MergeOwnedDescendants(
                root,
                rootExitTimeUtcTicks,
                knownDescendants,
                diagnostics,
                ref foundResidual);
            foreach (ProcessIdentity descendant in knownDescendants.Values)
            {
                if (IsProcessIdentityAlive(descendant))
                {
                    StopOwnedProcess(descendant, diagnostics);
                }
            }
            foreach (ProcessIdentity descendant in knownDescendants.Values)
            {
                if (IsProcessIdentityAlive(descendant))
                {
                    foundResidual = true;
                    diagnostics.Add(
                        $"owned descendant PID {descendant.ProcessId} remained active after " +
                        $"{ProcessCleanupTimeoutMilliseconds / 1000}s cleanup");
                }
            }
        }

        private static bool IsProcessIdentityAlive(ProcessIdentity identity)
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                return !process.HasExited &&
                    process.StartTime.ToUniversalTime().Ticks == identity.StartTimeUtcTicks;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void TryTaskkill(int processId, ICollection<string> diagnostics)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("/PID");
                startInfo.ArgumentList.Add(processId.ToString());
                startInfo.ArgumentList.Add("/T");
                startInfo.ArgumentList.Add("/F");
                using var taskkill = Process.Start(startInfo);
                if (taskkill is null)
                {
                    diagnostics.Add("taskkill.exe did not start");
                    return;
                }
                if (!taskkill.WaitForExit(ProcessCleanupTimeoutMilliseconds))
                {
                    diagnostics.Add(
                        $"taskkill.exe for PID {processId} did not finish within " +
                        $"{ProcessCleanupTimeoutMilliseconds / 1000}s");
                    return;
                }
                if (taskkill.ExitCode != 0 && IsProcessIdAlive(processId))
                {
                    diagnostics.Add(
                        $"taskkill.exe for PID {processId} failed with exit code {taskkill.ExitCode}");
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"taskkill.exe for PID {processId} failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        private static bool IsProcessIdAlive(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static string AppendDiagnostic(string text, string diagnostic)
        {
            return string.IsNullOrWhiteSpace(text)
                ? diagnostic
                : text + Environment.NewLine + diagnostic;
        }

        private static IReadOnlyList<ProcessSnapshotEntry> CaptureProcessTable()
        {
            nint snapshot = CreateToolhelp32Snapshot(ToolhelpSnapshotProcess, 0);
            if (snapshot == InvalidHandleValue)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to capture the process table.");
            }

            Exception? failure = null;
            var entries = new List<ProcessSnapshotEntry>();
            try
            {
                var nativeEntry = new ProcessEntry32
                {
                    Size = (uint)Marshal.SizeOf<ProcessEntry32>()
                };
                if (!Process32FirstW(snapshot, ref nativeEntry))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoMoreFiles)
                    {
                        throw new Win32Exception(error, "Unable to read the process snapshot.");
                    }
                }
                else
                {
                    while (true)
                    {
                        entries.Add(new ProcessSnapshotEntry(
                            unchecked((int)nativeEntry.ProcessId),
                            unchecked((int)nativeEntry.ParentProcessId)));
                        nativeEntry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                        if (Process32NextW(snapshot, ref nativeEntry))
                        {
                            continue;
                        }
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorNoMoreFiles)
                        {
                            throw new Win32Exception(error, "Unable to finish reading the process snapshot.");
                        }
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (!CloseHandle(snapshot) && failure is null)
                {
                    failure = new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to close the process snapshot.");
                }
            }

            if (failure is not null)
            {
                throw failure;
            }
            return entries;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public nint DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint handle);

        private static string GetTaskValue(Task<string> task)
        {
            return task.Status == TaskStatus.RanToCompletion ? task.Result : "<unavailable>";
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Repository root was not found.");
        }

        private const string FakeUvScript = "@echo off\r\necho generated>docs\\index.html\r\necho generated-ja>docs\\index.ja.html\r\nexit /b 0\r\n";

        private const string FakeGhScript = "@echo off\r\nsetlocal EnableDelayedExpansion\r\n" +
            "if not \"%GH_LOG%\"==\"\" echo %*>>\"%GH_LOG%\"\r\n" +
            "if /I \"%~1\"==\"auth\" goto success\r\n" +
            "if /I not \"%~1\"==\"release\" goto success\r\n" +
            "if /I \"%~2\"==\"view\" goto view\r\n" +
            "if /I \"%~2\"==\"create\" goto create\r\n" +
            "if /I \"%~2\"==\"edit\" goto edit\r\n" +
            "if /I \"%~2\"==\"upload\" goto upload\r\n" +
            "goto success\r\n" +
            ":view\r\n" +
            "if not defined GH_STATE goto no_release\r\n" +
            "if not exist \"%GH_STATE%\" goto no_release\r\n" +
            "set /p state=<\"%GH_STATE%\"\r\n" +
            "set /p assets=<\"%GH_ASSETS%\"\r\n" +
            "echo {\"isDraft\":!state!,\"assets\":!assets!}\r\n" +
            "goto success\r\n" +
            ":create\r\n" +
            "echo true>\"%GH_STATE%\"\r\n" +
            "goto success\r\n" +
            ":edit\r\n" +
            "if defined GH_FAIL_EDIT goto fail_edit\r\n" +
            "if /I \"%~4\"==\"--draft\" if /I \"%~5\"==\"false\" goto published\r\n" +
            "echo true>\"%GH_STATE%\"\r\n" +
            "goto success\r\n" +
            ":published\r\n" +
            "echo false>\"%GH_STATE%\"\r\n" +
            "goto success\r\n" +
            ":upload\r\n" +
            "if defined GH_FAIL_UPLOAD goto fail_upload\r\n" +
            "goto success\r\n" +
            ":fail_edit\r\n" +
            "exit /b 7\r\n" +
            ":fail_upload\r\n" +
            "exit /b 8\r\n" +
            ":no_release\r\n" +
            "exit /b 1\r\n" +
            ":success\r\n" +
            "exit /b 0\r\n";
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + Environment.NewLine + StandardError;
    }
}
