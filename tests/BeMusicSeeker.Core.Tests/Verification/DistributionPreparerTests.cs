using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>取得とcommandのI/O境界だけを代替し、配布準備の停止・期限・cache公開判断を保証します。</summary>
[TestClass]
public sealed class DistributionPreparerTests
{
    /// <summary>有効cacheは取得せず再利用し、不正cacheは取得・上書き・fallbackせず失敗します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExistingCacheIsEitherReusedOrRejectedWithoutDownload(bool corrupt)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("legacy.zip");
        byte[] good = [1, 2, 3];
        byte[] existing = corrupt ? [1, 2, 4] : good;
        File.WriteAllBytes(path, existing);
        int downloads = 0;
        Task Acquire() => DistributionPreparer.AcquireLegacyAsync(path, new Uri("https://example.invalid/package"), good.Length,
            Convert.ToHexString(SHA256.HashData(good)), (_, _, _) => { downloads++; return Task.CompletedTask; }, Deadline(), default);
        if (corrupt) { await Assert.ThrowsExactlyAsync<InvalidDataException>(Acquire); } else { await Acquire(); }
        Assert.AreEqual(0, downloads);
        CollectionAssert.AreEqual(existing, File.ReadAllBytes(path));
    }

    /// <summary>不在cacheだけを一回取得し、検査前にcanonicalを見せず、検査済tempを同じdirectoryから公開します。</summary>
    [TestMethod]
    public async Task MissingCacheIsPublishedOnlyAfterTheSingleDownloadWasValidated()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("legacy.zip");
        byte[] good = [1, 2, 3];
        int downloads = 0;
        await DistributionPreparer.AcquireLegacyAsync(path, new Uri("https://example.invalid/package"), good.Length,
            Convert.ToHexString(SHA256.HashData(good)), async (_, temporary, token) =>
            {
                downloads++;
                Assert.IsFalse(File.Exists(path));
                Assert.AreEqual(Path.GetDirectoryName(path), Path.GetDirectoryName(temporary));
                await File.WriteAllBytesAsync(temporary, good, token);
                Assert.IsFalse(File.Exists(path));
            }, Deadline(), default);
        Assert.AreEqual(1, downloads);
        CollectionAssert.AreEqual(good, File.ReadAllBytes(path));
        Assert.AreEqual(1, Directory.GetFiles(workspace.RootDirectory).Length);
    }

    /// <summary>取得失敗・同サイズhash違い・期限超過・取消ではcanonicalを作らず、tempを残さず失敗します。</summary>
    [TestMethod]
    [DataRow("fetch")]
    [DataRow("hash")]
    [DataRow("timeout")]
    [DataRow("cancel")]
    public async Task FailedAcquisitionDoesNotPublishTheCanonicalCache(string failure)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("legacy.zip");
        byte[] good = [1, 2, 3];
        using var canceled = new CancellationTokenSource();
        var timeout = ProcessDeadline.FromTimeout(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<Exception>(() => DistributionPreparer.AcquireLegacyAsync(path, new Uri("https://example.invalid/package"), good.Length,
            Convert.ToHexString(SHA256.HashData(good)), async (_, temporary, token) =>
            {
                await File.WriteAllBytesAsync(temporary, failure == "hash" ? [1, 2, 4] : good, token);
                if (failure == "fetch") { throw new IOException("fetch fixture"); }
                if (failure == "cancel") { canceled.Cancel(); token.ThrowIfCancellationRequested(); }
                if (failure == "timeout") { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            }, failure == "timeout" ? timeout : Deadline(), canceled.Token));
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(0, Directory.GetFiles(workspace.RootDirectory).Length);
    }

    /// <summary>一時downloadの回収にも失敗した場合、取得の元失敗と回収失敗を両方残し、成功やcanonical公開へ進みません。</summary>
    [TestMethod]
    public async Task CleanupFailureRetainsTheDownloadFailureAndDoesNotPublish()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("legacy.zip");
        FileStream? held = null;
        var original = new IOException("download failed");
        try
        {
            AggregateException error = await Assert.ThrowsExactlyAsync<AggregateException>(() => DistributionPreparer.AcquireLegacyAsync(path,
                new Uri("https://example.invalid/package"), 3, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })),
                (_, temporary, _) =>
                {
                    held = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    throw original;
                }, Deadline(), default));
            Assert.IsTrue(error.InnerExceptions.Contains(original));
            Assert.AreEqual(2, error.InnerExceptions.Count);
            Assert.IsFalse(File.Exists(path));
        }
        finally { held?.Dispose(); }
    }

    /// <summary>実pwshで特殊文字を含む出力先と版指定を忠実に束縛し、子だけをUTF-8へ初期化して両pipeの日本語を保ち、現行・次版を一度ずつ準備します。</summary>
    /// <remarks>publish/layoutの小scriptで実引数とencodingを観測した後に配布fixtureを生成します。三つのartifact検査からcatalogまで接続しますが、実publish受入の成功は代用しません。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentAndNextEachPublishOnceWithSeparateRootsAndOnlyNextVersionOverride(bool includeMetadata)
    {
        using var fixture = new DistributionFixture();
        fixture.CreateLegacy();
        Encoding parentEncoding = Console.OutputEncoding;
        string runRoot = fixture.PathFor("run ' $value `tick [1]");
        string metadataSource = fixture.PathFor("metadata ' $value.7z");
        string currentVersion = DistributionValidation.ReadVersion(typeof(BeMusicSeeker.App).Assembly.Location);
        string nextVersion = DistributionValidation.IncrementVersion(currentVersion);
        Directory.CreateDirectory(fixture.PathFor("scripts"));
        File.WriteAllText(fixture.PathFor("scripts/publish.ps1"), """
            param([switch]$SkipDocHtml, [string]$ArtifactRoot, [string]$ApplicationVersion, [switch]$IncludeMetadata, [string]$MetadataSource)
            [void][IO.Directory]::CreateDirectory($ArtifactRoot)
            $receipt = [ordered]@{
                artifactRoot = $ArtifactRoot
                skipDocHtml = [bool]$SkipDocHtml
                includeMetadata = [bool]$IncludeMetadata
                metadataSource = $MetadataSource
                hasVersion = $PSBoundParameters.ContainsKey('ApplicationVersion')
                version = $ApplicationVersion
                codePage = [Console]::OutputEncoding.CodePage
            }
            [IO.File]::WriteAllText([IO.Path]::Combine($ArtifactRoot, 'publish-receipt.json'), ($receipt | ConvertTo-Json -Compress))
            [Console]::WriteLine('publish 日本語🙂 stdout')
            [Console]::Error.WriteLine('publish 日本語🙂 stderr')
            """);
        File.WriteAllText(fixture.PathFor("scripts/portable-package-layout.ps1"), """
            function Assert-PortableReleasePackageLayout([string]$assetPath) {
                $receipt = [ordered]@{ packagePath = $assetPath; codePage = [Console]::OutputEncoding.CodePage }
                [IO.File]::WriteAllText([IO.Path]::Combine([IO.Path]::GetDirectoryName($assetPath), 'layout-receipt.json'), ($receipt | ConvertTo-Json -Compress))
                [Console]::WriteLine('layout 日本語🙂 stdout')
                [Console]::Error.WriteLine('layout 日本語🙂 stderr')
            }
            """);
        async Task Stage(string name, Func<ExecuteCommand, ProcessDeadline, CancellationToken, Task> action)
        {
            int commands = 0;
            bool next = name == "publish-next";
            string root = Path.Combine(runRoot, next ? "next" : "v");
            string version = next ? nextVersion : currentVersion;
            await action(async (executable, arguments, deadline) =>
            {
                bool publish = ++commands == 1;
                await using var process = OwnedProcess.Start(new ProcessStartRequest(executable, arguments, fixture.Root,
                    ownershipPolicy: ProcessOwnershipPolicy.Preparation));
                ProcessCompletion result = await process.WaitForExitAsync(deadline);
                ProcessStopResult stopped = await process.StopAsync(null, Deadline());
                Assert.IsNull(stopped.CleanupFailure);
                Assert.AreEqual(0, result.ExitCode, result.StandardError);
                string marker = publish ? "publish" : "layout";
                Assert.AreEqual(marker + " 日本語🙂 stdout", result.StandardOutput.Trim());
                Assert.AreEqual(marker + " 日本語🙂 stderr", result.StandardError.Trim());
                string receiptPath = publish ? Path.Combine(root, "publish-receipt.json") : Path.Combine(root, "dist", "layout-receipt.json");
                using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
                Assert.AreEqual(65001, receipt.RootElement.GetProperty("codePage").GetInt32());
                if (publish)
                {
                    Assert.AreEqual(root, receipt.RootElement.GetProperty("artifactRoot").GetString());
                    Assert.IsFalse(receipt.RootElement.GetProperty("skipDocHtml").GetBoolean());
                    Assert.AreEqual(next, receipt.RootElement.GetProperty("hasVersion").GetBoolean());
                    Assert.AreEqual(next ? nextVersion : "", receipt.RootElement.GetProperty("version").GetString());
                    Assert.AreEqual(includeMetadata && !next, receipt.RootElement.GetProperty("includeMetadata").GetBoolean());
                    Assert.AreEqual(includeMetadata && !next ? metadataSource : "", receipt.RootElement.GetProperty("metadataSource").GetString());
                    // 実束縛値の照合前に出力を作らず、誤引数をfixtureで補修しない。
                    DistributionArtifact created = fixture.Create(Path.GetRelativePath(fixture.Root, root), next ? nextVersion : null);
                    if (includeMetadata && !next) { fixture.CreateMetadataVariant(created); }
                }
                else
                {
                    Assert.AreEqual(Path.Combine(root, "dist", $"bemusicseeker-unofficial-fork-v{version}.zip"),
                        receipt.RootElement.GetProperty("packagePath").GetString());
                }
                return result;
            }, Deadline(), default);
            Assert.AreEqual(name == "legacy-artifact" ? 0 : 2, commands);
        }
        string catalogPath = await DistributionPreparer.PrepareAsync(fixture.Root, runRoot, Stage, includeMetadata, includeMetadata ? metadataSource : null);
        DistributionCatalog catalog = await DistributionCatalog.ReadAsync(catalogPath, fixture.Root, Deadline());
        string currentApp = Path.Combine(runRoot, "v", "app"), nextApp = Path.Combine(runRoot, "next", "app");
        Assert.AreEqual(nextVersion, DistributionValidation.ReadVersion(Path.Combine(nextApp, "BeMusicSeeker.exe")));
        Assert.AreEqual(catalogPath, Path.Combine(runRoot, "catalog.json"));
        Assert.AreEqual(currentApp, catalog.Current.AppPublishRoot);
        Assert.AreEqual(nextApp, catalog.Next.AppPublishRoot);
        Assert.AreEqual(includeMetadata, catalog.CurrentWithMetadata is not null);
        Assert.AreEqual(parentEncoding, Console.OutputEncoding);
    }

    /// <summary>存在する出力や相対出力を拒否し、既存内容を変更せず準備段階も開始しません。</summary>
    [TestMethod]
    public async Task CandidateOutputMustBeNewAndAbsolute()
    {
        using var fixture = new DistributionFixture();
        string existing = fixture.PathFor("existing");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
        int stages = 0;
        Task Stage(string _, Func<ExecuteCommand, ProcessDeadline, CancellationToken, Task> action) { stages++; return Task.CompletedTask; }
        await Assert.ThrowsExactlyAsync<IOException>(() => DistributionPreparer.PrepareAsync(fixture.Root, existing, Stage));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => DistributionPreparer.PrepareAsync(fixture.Root, "relative", Stage));
        Assert.AreEqual(0, stages);
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(existing, "keep.txt")));
    }

    /// <summary>catalog tempの保存と削除が同時に失敗したら二つの例外を保持し、未完成catalogを公開しません。</summary>
    [TestMethod]
    public async Task CatalogWriteAndTemporaryCleanupFailuresAreBothRetained()
    {
        using var fixture = new DistributionFixture();
        fixture.CreateLegacy();
        FileStream? held = null;
        async Task Stage(string name, Func<ExecuteCommand, ProcessDeadline, CancellationToken, Task> action)
        {
            if (name == "publish-next")
            {
                held = new FileStream(fixture.PathFor("candidate/catalog.json.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            await action((_, args, _) =>
            {
                if (args[^1].Contains(" -ArtifactRoot ", StringComparison.Ordinal))
                {
                    fixture.CreatePublishedVariant(name == "publish-next");
                }
                return Task.FromResult(new ProcessCompletion(0, "", ""));
            }, Deadline(), default);
        }
        try
        {
            AggregateException error = await Assert.ThrowsExactlyAsync<AggregateException>(() => DistributionPreparer.PrepareAsync(fixture.Root, fixture.PathFor("candidate"), Stage));
            Assert.AreEqual(2, error.InnerExceptions.Count);
            Assert.IsTrue(error.InnerExceptions.All(failure => failure is IOException));
            Assert.AreNotSame(error.InnerExceptions[0], error.InnerExceptions[1]);
            Assert.IsFalse(File.Exists(fixture.PathFor("candidate/catalog.json")));
        }
        finally { held?.Dispose(); }
    }

    /// <summary>publishの非ゼロ終了または同じ段階のvalidation失敗で、後続publishとcatalog公開を開始しません。</summary>
    [TestMethod]
    [DataRow("publish-v", false)]
    [DataRow("publish-v", true)]
    [DataRow("publish-next", false)]
    [DataRow("publish-next", true)]
    public async Task FailedPublishOrValidationStopsBeforeTheNextVariant(string failingStage, bool validationFailure)
    {
        using var fixture = new DistributionFixture();
        fixture.CreateLegacy();
        var stages = new List<string>();
        int commands = 0;
        int publishes = 0;
        async Task Stage(string name, Func<ExecuteCommand, ProcessDeadline, CancellationToken, Task> action)
        {
            stages.Add(name);
            await action((_, args, _) =>
            {
                commands++;
                bool publish = args[^1].Contains(" -ArtifactRoot ", StringComparison.Ordinal);
                if (publish) { publishes++; }
                if (name != failingStage && publish)
                {
                    fixture.CreatePublishedVariant(name == "publish-next");
                }
                return Task.FromResult(new ProcessCompletion(name == failingStage && !validationFailure ? 17 : 0, "", "publish fixture"));
            }, Deadline(), default);
        }
        await Assert.ThrowsAsync<Exception>(() => DistributionPreparer.PrepareAsync(fixture.Root, fixture.PathFor("candidate"), Stage));
        Assert.AreEqual(failingStage == "publish-v" ? 1 : 3, commands);
        Assert.AreEqual(failingStage == "publish-v" ? 1 : 2, publishes);
        CollectionAssert.AreEqual(failingStage == "publish-v" ? new[] { "legacy-artifact", "publish-v" } : ["legacy-artifact", "publish-v", "publish-next"], stages);
        Assert.IsFalse(File.Exists(fixture.PathFor("candidate/catalog.json")));
    }

    /// <summary>publish成功後に期限内へ到達したlayoutが元期限を使い切って成功を返しても、成果物検査は期限超過となりNextとcatalog公開へ進みません。</summary>
    [TestMethod]
    public async Task LayoutValidationDoesNotResetTheVariantDeadline()
    {
        using var fixture = new DistributionFixture();
        fixture.CreateLegacy();
        await DistributionPreparer.PrepareLegacyAsync(fixture.Root, Deadline(), default);
        DistributionArtifact current = fixture.Create("seed");
        await DistributionValidation.ValidateZipAsync(current.PackagePath, fixture.App("seed"),
            fixture.PathFor("seed/updater"), current.Version, Deadline(), default);
        var stages = new List<string>();
        int commands = 0;
        bool publishSucceeded = false, layoutReached = false, layoutWaitCompleted = false;
        async Task Stage(string name, Func<ExecuteCommand, ProcessDeadline, CancellationToken, Task> action)
        {
            stages.Add(name);
            if (name == "legacy-artifact") { await action((_, _, _) => throw new InvalidOperationException(), Deadline(), default); return; }
            Assert.AreEqual("publish-v", name);
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(1));
            await action(async (_, args, received) =>
            {
                commands++;
                Assert.AreSame(deadline, received);
                if (args[^1].Contains(" -ArtifactRoot ", StringComparison.Ordinal))
                {
                    Assert.AreEqual(1, commands);
                    Directory.Move(fixture.PathFor("seed"), fixture.PathFor("candidate/v"));
                    publishSucceeded = true;
                    return new ProcessCompletion(0, "", "");
                }
                Assert.AreEqual(2, commands);
                Assert.IsTrue(publishSucceeded);
                Assert.IsTrue(args[^1].Contains("Assert-PortableReleasePackageLayout", StringComparison.Ordinal));
                layoutReached = true;
                TimeSpan remaining = deadline.Remaining;
                Assert.IsTrue(remaining > TimeSpan.Zero);
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(10));
                Assert.AreEqual(TimeSpan.Zero, deadline.Remaining);
                layoutWaitCompleted = true;
                return new ProcessCompletion(0, "", "");
            }, deadline, default);
        }
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => DistributionPreparer.PrepareAsync(fixture.Root, fixture.PathFor("candidate"), Stage));
        Assert.IsTrue(publishSucceeded);
        Assert.IsTrue(layoutReached);
        Assert.IsTrue(layoutWaitCompleted);
        Assert.AreEqual(2, commands);
        CollectionAssert.AreEqual(new[] { "legacy-artifact", "publish-v" }, stages);
        Assert.IsFalse(File.Exists(fixture.PathFor("candidate/catalog.json")));
    }

    private static ProcessDeadline Deadline() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
}
