using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class ReleaseScriptVersionSourceTests
{
    private const string Version = "9.9.9.9";
    private const string Tag = "v9.9.9.9";

    /// <summary>MSTestが各ケースへ渡す取消と診断の文脈です。</summary>
    public required TestContext TestContext { get; set; }

    /// <summary>
    /// mainより複数commit先でも通常版の正本propsからdraftと資産manifestを生成し、生成物以外の変更のcommitやremote mainの先行更新を検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトへ渡す。gh/文書生成は局所fixtureに差し替える。</remarks>
    [TestMethod]
    public async Task CreateDraftUsesCanonicalVersionAndCommitsOnlyGeneratedFilesWithMultipleAheadCommits()
    {
        await RunCaseAsync(true, false, async fixture =>
        {
            await fixture.AddCommitAsync("source-a.txt", "first ahead commit");
            await fixture.AddCommitAsync("source-b.txt", "second ahead commit");
            string aheadHeadBefore = await fixture.HeadCommitAsync();
            string remoteMainBefore = await fixture.RemoteMainCommitAsync();

            ProcessResult result = await fixture.RunReleaseAsync("-CreateDraft");

            AssertSuccess(result);
            Assert.AreEqual(remoteMainBefore, await fixture.RemoteMainCommitAsync(), result.CombinedOutput);
            Assert.AreEqual(await fixture.HeadCommitAsync(), await fixture.LocalTagCommitAsync(Tag));

            string[] generatedDiff = await fixture.DiffNamesAsync(aheadHeadBefore);
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
        });
    }

    /// <summary>
    /// 同梱版のみの指定では、full合格済みの同梱版だけをRelease資産とmanifestへ載せ、旧版が要求する先頭のkind=appを同梱版へ付ける。
    /// 通常版を公開・配信する誤り、旧版が拒否するmanifestを生成する誤り、同じ指定のない公開で配信集合の不一致を見逃す誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    public async Task MetadataBundleOnlyPublishesOnlyBundleAsLegacyAppAsset()
    {
        await RunCaseAsync(true, false, async fixture =>
        {
            string metadataName = Path.GetFileName(fixture.MetadataPackagePath);
            string normalName = Path.GetFileName(fixture.NormalPackagePath);
            fixture.WriteRemoteAssetsJson(JsonSerializer.Serialize(new[]
            {
                new { name = metadataName, size = new FileInfo(fixture.MetadataPackagePath).Length }
            }));

            ProcessResult created = await fixture.RunReleaseAsync("-CreateDraft", "-MetadataBundleOnly");

            AssertSuccess(created);
            using (var manifest = JsonDocument.Parse(File.ReadAllText(fixture.UpdateManifestPath)))
            {
                // v3.1.0.0以前のアプリが必須とする項目を、現行アプリが要求しなくなっても出力し続けることを確かめます。
                JsonElement root = manifest.RootElement;
                Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.AreEqual(Tag, root.GetProperty("releaseTag").GetString());
                Assert.AreEqual(1, root.GetProperty("packageFormatVersion").GetInt32());
                Assert.AreEqual("1", root.GetProperty("minimumUpdaterVersion").GetString());
                JsonElement assets = root.GetProperty("assets");
                Assert.AreEqual(1, assets.GetArrayLength());
                AssertManifestAsset(assets[0], fixture.MetadataPackagePath, "app", true);
                Assert.IsFalse(string.IsNullOrWhiteSpace(assets[0].GetProperty("label").GetString()));
            }
            string createCommand = fixture.GhLogLines().Single(line => line.StartsWith("release create ", StringComparison.Ordinal));
            StringAssert.Contains(createCommand, metadataName);
            Assert.IsFalse(createCommand.Contains(normalName, StringComparison.Ordinal), createCommand);

            ProcessResult withoutOption = await fixture.RunReleaseAsync("-PublishDraft");

            AssertFailure(withoutOption);
            Assert.IsFalse(fixture.GhLogLines().Any(line => line.Contains("--draft=false", StringComparison.Ordinal)));

            ProcessResult published = await fixture.RunReleaseAsync("-PublishDraft", "-MetadataBundleOnly");

            AssertSuccess(published);
            Assert.AreEqual(await fixture.HeadCommitAsync(), await fixture.RemoteMainCommitAsync());
        });
    }

    /// <summary>
    /// 同梱版のみの指定で候補に同梱版がない入力はdraft生成を拒否し、通常版の公開へ黙って切り替える誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトへ渡す。gh/文書生成は局所fixtureに差し替える。</remarks>
    [TestMethod]
    public async Task MetadataBundleOnlyRequiresMetadataCandidate()
    {
        await RunCaseAsync(false, false, async fixture =>
        {
            ProcessResult result = await fixture.RunReleaseAsync("-CreateDraft", "-MetadataBundleOnly");

            AssertFailure(result);
            Assert.IsNull(await fixture.LocalTagCommitAsync(Tag));
            Assert.AreEqual(fixture.InitialHead, await fixture.HeadCommitAsync());
            Assert.AreEqual(0, fixture.GhLogLines().Count(line => line.StartsWith("release ", StringComparison.Ordinal)), result.CombinedOutput);
        });
    }

    /// <summary>
    /// 互換versionファイルと任意metadataプロパティがない通常packageだけの入力でdraftを作成し、不要な入力を必須にする誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    public async Task CreateDraftDoesNotRequireCompatibilityVersionFile()
    {
        await RunCaseAsync(false, false, async fixture =>
        {
            JsonObject catalog = JsonNode.Parse(File.ReadAllText(fixture.CatalogPath))?.AsObject()
                ?? throw new InvalidDataException("catalog がありません。");
            catalog.Remove("CurrentWithMetadata");
            File.WriteAllText(fixture.CatalogPath, catalog.ToJsonString());

            ProcessResult result = await fixture.RunReleaseAsync("-CreateDraft");

            AssertSuccess(result);
            Assert.IsTrue(File.Exists(fixture.UpdateManifestPath), result.CombinedOutput);
            Assert.AreEqual(await fixture.HeadCommitAsync(), await fixture.LocalTagCommitAsync(Tag));
        });
    }

    /// <summary>
    /// 互換versionファイルが残る入力でもその内容を保持し、新版の正本値で旧互換データを書き換える誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    public async Task CreateDraftLeavesCompatibilityVersionFileUnchanged()
    {
        await RunCaseAsync(false, true, async fixture =>
        {
            string compatibilityPath = Path.Combine(fixture.Root, "version.txt");
            string before = File.ReadAllText(compatibilityPath);

            ProcessResult result = await fixture.RunReleaseAsync("-CreateDraft");

            AssertSuccess(result);
            Assert.AreEqual(before, File.ReadAllText(compatibilityPath));
        });
    }

    /// <summary>
    /// 既存manifestが同じreleaseなら公開時刻を保持し、別releaseなら更新することで、再実行による時刻の改変と別版の時刻流用を検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CreateDraftPublishedAtIsReusedOnlyForTheSameRelease(bool sameRelease)
    {
        await RunCaseAsync(false, false, async fixture =>
        {
            const string previousPublishedAt = "2000-01-02T03:04:05Z";
            await fixture.SeedExistingManifestAsync(
                sameRelease ? Version : "1.0.0.0",
                sameRelease ? Tag : "v1.0.0.0",
                previousPublishedAt);

            ProcessResult result = await fixture.RunReleaseAsync("-CreateDraft");

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
        });
    }

    /// <summary>
    /// 通常app・metadata packageがともにない入力はdraft生成を拒否し、tagや生成commitを残さない。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトへ渡す。gh/文書生成は局所fixtureに差し替える。</remarks>
    [TestMethod]
    public async Task CreateDraftRequiresNormalPackage()
    {
        await RunCaseAsync(false, false, async fixture =>
        {
            File.Delete(fixture.NormalPackagePath);

            ProcessResult result = await fixture.RunReleaseAsync("-CreateDraft");

            AssertFailure(result);
            Assert.IsNull(await fixture.LocalTagCommitAsync(Tag));
            Assert.AreEqual(fixture.InitialHead, await fixture.HeadCommitAsync());
        });
    }

    /// <summary>
    /// Full未実行・不合格・別候補・公開集合や実体の不一致を各公開入口で拒否し、生成物・タグ・外部操作を残す誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("missing", "-CreateDraft")]
    [DataRow("profile", "-CreatePrereleaseDraft")]
    [DataRow("failed", "-PublishDraft")]
    [DataRow("catalog", "-CreateDraft")]
    [DataRow("set", "-CreateDraft")]
    [DataRow("hash", "-CreateDraft")]
    [DataRow("size", "-CreateDraft")]
    [DataRow("version", "-CreateDraft")]
    [DataRow("path", "-CreateDraft")]
    [DataRow("zip", "-CreateDraft")]
    public async Task ReleaseRequiresMatchingSuccessfulFullBeforeSideEffects(string mismatch, string mode)
    {
        await RunCaseAsync(true, false, async fixture =>
        {
            byte[] manifestBefore = File.ReadAllBytes(fixture.UpdateManifestPath);
            string remoteBefore = await fixture.RemoteMainCommitAsync();
            fixture.InvalidateFullEvidence(mismatch);

            ProcessResult result = await fixture.RunReleaseAsync(mode);

            AssertFailure(result);
            Assert.AreEqual(fixture.InitialHead, await fixture.HeadCommitAsync());
            Assert.AreEqual(remoteBefore, await fixture.RemoteMainCommitAsync());
            Assert.IsNull(await fixture.LocalTagCommitAsync(Tag));
            Assert.IsNull(await fixture.LocalTagCommitAsync(Tag + "-preview.1"));
            CollectionAssert.AreEqual(manifestBefore, File.ReadAllBytes(fixture.UpdateManifestPath));
            Assert.AreEqual("source", File.ReadAllText(Path.Combine(fixture.Root, "docs", "index.html")));
            Assert.AreEqual(0, fixture.GhLogLines().Count, result.CombinedOutput);
        });
    }

    /// <summary>
    /// draft公開成功時の公開状態・remote mainのHEAD一致と編集引数を確認し、本文や題名を再指定する誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトへ渡す。gh/文書生成は局所fixtureに差し替える。</remarks>
    [TestMethod]
    public async Task PublishDraftPublishesReleaseUpdatesRemoteMainAndUsesOnlyDraftEditArgument()
    {
        await RunCaseAsync(true, true, async fixture =>
        {
            AssertSuccess(await fixture.RunReleaseAsync("-CreateDraft"));
            string releaseHead = await fixture.HeadCommitAsync();
            string remoteMainBefore = await fixture.RemoteMainCommitAsync();
            int publishLogStart = fixture.GhLogLines().Count;

            ProcessResult result = await fixture.RunReleaseAsync("-PublishDraft");

            AssertSuccess(result);
            Assert.AreNotEqual(remoteMainBefore, await fixture.RemoteMainCommitAsync());
            Assert.AreEqual(releaseHead, await fixture.RemoteMainCommitAsync());
            Assert.AreEqual("false", File.ReadAllText(fixture.GhStatePath).Trim());
            string[] publishCommands = fixture.GhLogLines().Skip(publishLogStart).ToArray();
            string[] releaseEdits = publishCommands
                .Where(line => line.StartsWith("release edit ", StringComparison.Ordinal))
                .ToArray();
            CollectionAssert.AreEqual(new[] { $"release edit {Tag} --draft=false" }, releaseEdits);
            Assert.IsFalse(releaseEdits[0].Contains("--notes-file", StringComparison.Ordinal));
            Assert.IsFalse(releaseEdits[0].Contains("--notes", StringComparison.Ordinal));
            Assert.IsFalse(releaseEdits[0].Contains("--title", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// tag・HEAD・ZIPの内容・manifest・remote資産が一致しない各入力で公開とmain更新を止め、未確認資産を正式配信へ進める誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    [DataRow("head")]
    [DataRow("remote-tag")]
    [DataRow("zip")]
    [DataRow("asset-size")]
    [DataRow("asset-set")]
    public async Task PublishDraftRejectsTagHeadZipManifestOrRemoteAssetMismatchWithoutPushingMain(string mismatch)
    {
        await RunCaseAsync(true, true, async fixture =>
        {
            AssertSuccess(await fixture.RunReleaseAsync("-CreateDraft"));
            string remoteMainBefore = await fixture.RemoteMainCommitAsync();

            switch (mismatch)
            {
                case "head":
                    await fixture.AddCommitAsync("after-release.txt", "head moved");
                    break;
                case "remote-tag":
                    await fixture.SetRemoteTagCommitAsync(fixture.InitialHead);
                    break;
                case "zip":
                    long packageLength = new FileInfo(fixture.NormalPackagePath).Length;
                    string[] packageEntries = fixture.ZipEntryNames(fixture.NormalPackagePath);
                    string packageHash = Convert.ToHexString(
                        SHA256.HashData(File.ReadAllBytes(fixture.NormalPackagePath)));
                    fixture.RewriteNormalPackageContent();
                    // 合格した新候補でも追跡中のmanifestと異なれば公開できないことを分けて確認します。
                    fixture.WriteFullEvidence();
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

            ProcessResult result = await fixture.RunReleaseAsync("-PublishDraft");

            Assert.AreNotEqual(0, result.ExitCode, result.CombinedOutput);
            Assert.AreEqual(remoteMainBefore, await fixture.RemoteMainCommitAsync(), result.CombinedOutput);
            Assert.IsFalse(fixture.GhLogLines().Any(line => line.Contains("--draft=false", StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// remote mainが独立に進んだ入力では資産公開前に拒否し、非祖先の履歴へ正式releaseを押し込む誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    public async Task PublishDraftRejectsNonAncestorRemoteMainBeforePublishingAssets()
    {
        await RunCaseAsync(false, false, async fixture =>
        {
            AssertSuccess(await fixture.RunReleaseAsync("-CreateDraft"));
            string remoteMainBefore = await fixture.RemoteMainCommitAsync();
            await fixture.AdvanceRemoteMainOnIndependentCloneAsync();
            string advancedRemoteMain = await fixture.RemoteMainCommitAsync();

            ProcessResult result = await fixture.RunReleaseAsync("-PublishDraft");

            AssertFailure(result);
            Assert.AreNotEqual(remoteMainBefore, await fixture.RemoteMainCommitAsync());
            Assert.AreEqual(advancedRemoteMain, await fixture.RemoteMainCommitAsync());
            Assert.IsFalse(fixture.GhLogLines().Any(line => line.Contains("--draft=false", StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// 資産公開が失敗したときremote mainを保持し、配信に失敗した版だけを正式履歴へ進める誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    public async Task PublishDraftDoesNotPushMainWhenReleasePublicationFails()
    {
        await RunCaseAsync(false, false, async fixture =>
        {
            AssertSuccess(await fixture.RunReleaseAsync("-CreateDraft"));
            string remoteMainBefore = await fixture.RemoteMainCommitAsync();
            fixture.FailReleasePublication = true;

            ProcessResult result = await fixture.RunReleaseAsync("-PublishDraft");

            Assert.AreNotEqual(0, result.ExitCode, result.CombinedOutput);
            Assert.AreEqual(remoteMainBefore, await fixture.RemoteMainCommitAsync());
        });
    }

    /// <summary>
    /// 既に公開済みの同一releaseを再実行してmainを収束させ、公開操作を重複させたり完了済み状態を拒否したりする誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    public async Task PublishDraftAllowsRerunAfterReleaseIsAlreadyPublished()
    {
        await RunCaseAsync(false, true, async fixture =>
        {
            AssertSuccess(await fixture.RunReleaseAsync("-CreateDraft"));
            AssertSuccess(await fixture.RunReleaseAsync("-PublishDraft"));
            int publishCallsBefore = fixture.GhLogLines().Count(line => line.Contains("--draft=false", StringComparison.Ordinal));

            ProcessResult result = await fixture.RunReleaseAsync("-PublishDraft");

            AssertSuccess(result);
            Assert.AreEqual(publishCallsBefore, fixture.GhLogLines().Count(line => line.Contains("--draft=false", StringComparison.Ordinal)));
            Assert.AreEqual(await fixture.HeadCommitAsync(), await fixture.RemoteMainCommitAsync());
        });
    }

    /// <summary>
    /// dev HEADからpreview draftを作成してtagを接続し、正式manifest・互換version・mainの内容をpreview版で変更する誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    public async Task PreviewDraftTagsDevHeadAndLeavesTrackedReleaseFilesAndMainUnchanged()
    {
        await RunCaseAsync(true, true, async fixture =>
        {
            await fixture.CheckoutDevelopmentBranchAsync();
            string headBefore = await fixture.HeadCommitAsync();
            string mainBefore = await fixture.RemoteMainCommitAsync();
            byte[] manifestBefore = File.ReadAllBytes(fixture.UpdateManifestPath);
            byte[] compatibilityBefore = File.ReadAllBytes(Path.Combine(fixture.Root, "version.txt"));

            ProcessResult result = await fixture.RunReleaseAsync("-CreatePrereleaseDraft", "-PreviewSuffix", "preview.7");

            AssertSuccess(result);
            string previewTag = Tag + "-preview.7";
            Assert.AreEqual(headBefore, await fixture.LocalTagCommitAsync(previewTag));
            Assert.AreEqual(headBefore, await fixture.RemoteTagCommitAsync(previewTag));
            Assert.AreEqual(headBefore, await fixture.HeadCommitAsync());
            Assert.AreEqual(mainBefore, await fixture.RemoteMainCommitAsync());
            CollectionAssert.AreEqual(manifestBefore, File.ReadAllBytes(fixture.UpdateManifestPath));
            CollectionAssert.AreEqual(compatibilityBefore, File.ReadAllBytes(Path.Combine(fixture.Root, "version.txt")));
            Assert.IsTrue(fixture.GhLogLines().Any(line => line.Contains("--prerelease", StringComparison.Ordinal) &&
                                                            line.Contains("--draft", StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// 同名preview tagが別HEADを指す場合は既存tagを保持して拒否し、過去のpreviewの識別先を上書きする誤りを検出する。
    /// </summary>
    /// <remarks>一時領域の実git履歴とremoteを実スクリプトで操作する。gh/文書生成だけを局所fixtureへ差し替え、外部公開せずtag・commit・配信順の保証を保つ。</remarks>
    [TestMethod]
    public async Task PreviewDraftRejectsExistingPreviewTagAtAnotherHead()
    {
        await RunCaseAsync(false, true, async fixture =>
        {
            await fixture.CheckoutDevelopmentBranchAsync();
            string previewTag = Tag + "-preview.1";
            await fixture.CreateLocalTagAsync(previewTag, fixture.InitialHead);
            await fixture.AddCommitAsync("dev-change.txt", "new dev head");

            ProcessResult result = await fixture.RunReleaseAsync("-CreatePrereleaseDraft");

            Assert.AreNotEqual(0, result.ExitCode, result.CombinedOutput);
            Assert.AreEqual(fixture.InitialHead, await fixture.LocalTagCommitAsync(previewTag));
            Assert.IsFalse(fixture.GhLogLines().Any(line => line.Contains("release create", StringComparison.Ordinal)));
        });
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

    // 初期のgit状態（コミット済みの作業リポジトリと、mainをpush済みのbareのリモート）はケースで変わる内容が
    // version.txtの有無だけなので、その種類ごとにクラスで一度だけ作り、各ケースは専有領域へ複製して使います。
    // これによりケースごとのgit約10回の起動を、複製と1回のremote URL付け替えに減らします。
    // テンプレートは複製元として読むだけで、ケース同士で共有する可変状態にはしません。
    // 作成は特定ケースの取消へ結び付けず、テンプレート作成全体に独立した120秒の期限を持ちます。
    // 作成が失敗した場合は元の例外を保った失敗Taskを各ケースが待つため、失敗はskipや成功へ読み替えられません。
    private static readonly Lazy<Task<ReleaseFixture>> TemplateWithoutCompatibilityFile =
        new(() => Task.Run(() => CreateTemplateAsync(includeCompatibilityFile: false)));

    private static readonly Lazy<Task<ReleaseFixture>> TemplateWithCompatibilityFile =
        new(() => Task.Run(() => CreateTemplateAsync(includeCompatibilityFile: true)));

    private static Task<ReleaseFixture> GetTemplate(bool includeCompatibilityFile) =>
        (includeCompatibilityFile ? TemplateWithCompatibilityFile : TemplateWithoutCompatibilityFile).Value;

    private static async Task<ReleaseFixture> CreateTemplateAsync(bool includeCompatibilityFile)
    {
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(120));
        var failureCleanup = new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(5)));
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var template = new ReleaseFixture(workspace, deadline, failureCleanup, CancellationToken.None);
        Exception? primary = null;
        Exception? cleanup = null;
        try
        {
            await template.InitializeTemplateAsync(includeCompatibilityFile);
            return template;
        }
        catch (Exception failure)
        {
            primary = failure;
            _ = failureCleanup.Value;
        }
        try { template.CleanupWorkspace(failureCleanup.Value); }
        catch (Exception failure) { cleanup = failure; }
        ThrowFailures(primary, cleanup);
        throw new InvalidOperationException("到達しない経路です。");
    }

    /// <summary>
    /// テンプレート領域を回収します。ケースは並列に複製元として読むため、全ケースの終端後を保証できるクラス終端で一度だけ回収します。
    /// </summary>
    /// <remarks>作成に失敗したテンプレートは作成側が回収または証跡保持を済ませているため、ここでは対象にしません。</remarks>
    [ClassCleanup]
    public static async Task CleanupTemplatesAsync()
    {
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
        var failures = new List<Exception>();
        foreach (Lazy<Task<ReleaseFixture>> lazy in new[] { TemplateWithoutCompatibilityFile, TemplateWithCompatibilityFile })
        {
            if (!lazy.IsValueCreated) { continue; }
            ReleaseFixture template;
            try { template = await lazy.Value; }
            catch { continue; }
            try { template.CleanupWorkspace(deadline); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count == 1) { ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1) { throw new AggregateException(failures); }
    }

    private async Task RunCaseAsync(bool includeMetadata, bool includeCompatibilityFile, Func<ReleaseFixture, Task> action)
    {
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(360));
        var failureCleanup = new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(5)));
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var fixture = new ReleaseFixture(workspace, deadline, failureCleanup, TestContext.CancellationToken);
        Exception? primary = null;
        Exception? cleanup = null;
        try
        {
            ReleaseFixture template = await GetTemplate(includeCompatibilityFile).WaitAsync(TestContext.CancellationToken);
            await fixture.InitializeAsync(includeMetadata, template);
            await action(fixture);
        }
        catch (Exception failure)
        {
            primary = failure;
            _ = failureCleanup.Value;
        }
        try
        {
            fixture.CleanupWorkspace(primary is null ? deadline : failureCleanup.Value);
        }
        catch (Exception failure) { cleanup = failure; }
        ThrowFailures(primary, cleanup);
    }

    private static void ThrowFailures(Exception? primary, Exception? cleanup)
    {
        if (primary is not null)
        {
            if (cleanup is not null)
            {
                // 元の例外とstackを維持し、プロセス回収と媒体回収の失敗も別途残します。
                const string key = "ReleaseFixture.CleanupFailure";
                primary.Data[key] = primary.Data[key] is Exception previous
                    ? new AggregateException(previous, cleanup)
                    : cleanup;
            }
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup is not null) { ExceptionDispatchInfo.Capture(cleanup).Throw(); }
    }

    private sealed class ReleaseFixture
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
            "libs/x64/bms_vorbis.dll",
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

        private readonly TestWorkspace workspace;
        private readonly ProcessDeadline deadline;
        private readonly Lazy<ProcessDeadline> failureCleanup;
        private readonly CancellationToken cancellationToken;
        private Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase);
        private string gitPath = string.Empty;
        private string pwshPath = string.Empty;
        private bool preserveArtifacts;

        public ReleaseFixture(TestWorkspace workspace, ProcessDeadline deadline,
            Lazy<ProcessDeadline> failureCleanup, CancellationToken cancellationToken)
        {
            this.workspace = workspace;
            this.deadline = deadline;
            this.failureCleanup = failureCleanup;
            this.cancellationToken = cancellationToken;
            Root = workspace.GetPath("repo");
            DistRoot = workspace.GetPath("candidate");
            NormalPackagePath = Path.Combine(DistRoot, $"bemusicseeker-unofficial-fork-v{Version}.zip");
            MetadataPackagePath = Path.Combine(DistRoot, $"bemusicseeker-unofficial-fork-v{Version}-with-metadata.zip");
            ScriptPath = Path.Combine(Root, "scripts", "release.ps1");
            UpdateManifestPath = Path.Combine(Root, "update.json");
            RemotePath = workspace.GetPath("remote.git");
            FakeBinRoot = workspace.GetPath("fakebin");
            GhStatePath = workspace.GetPath("gh-state.txt");
            GhAssetsPath = workspace.GetPath("gh-assets.json");
            GhLogPath = workspace.GetPath("gh.log");
            InitialHead = string.Empty;
        }

        public string Root { get; }

        public string DistRoot { get; }

        public string CatalogPath => Path.Combine(DistRoot, "catalog.json");

        public string SummaryPath => workspace.GetPath("full-summary.json");

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

        /// <summary>テンプレートを作る側の初期化です。最初のコミットに入る内容とgit状態だけを作ります。</summary>
        public async Task InitializeTemplateAsync(bool includeCompatibilityFile)
        {
            gitPath = ResolveExecutable("git.exe");
            pwshPath = ResolveExecutable("pwsh.exe");
            CreateTrackedFiles(includeCompatibilityFile);
            environment = CreateEnvironment();
            await InitializeGitAsync();
        }

        /// <summary>
        /// ケース側の初期化です。テンプレートの作業リポジトリとリモートを専有領域へ複製し、
        /// 最初のコミットに入らないケース固有の内容を作ります。
        /// </summary>
        public async Task InitializeAsync(bool includeMetadata, ReleaseFixture template)
        {
            gitPath = ResolveExecutable("git.exe");
            pwshPath = ResolveExecutable("pwsh.exe");
            // 複製先が空の状態で複製するため、後続のケース固有の作成と衝突しません。
            CopyDirectory(template.Root, Root);
            CopyDirectory(template.RemotePath, RemotePath);
            RequireTime(deadline);
            cancellationToken.ThrowIfCancellationRequested();
            CreateCaseFiles(includeMetadata);
            environment = CreateEnvironment();
            await RunGitAsync("config", "remote.origin.url", RemotePath);
            InitialHead = template.InitialHead;
        }

        public async Task AddCommitAsync(string relativePath, string content)
        {
            File.WriteAllText(Path.Combine(Root, relativePath), content, Encoding.UTF8);
            await RunGitAsync("add", "--", relativePath);
            await RunGitAsync("commit", "-m", "source change");
        }

        public async Task SeedExistingManifestAsync(string version, string releaseTag, string publishedAt)
        {
            string manifest = JsonSerializer.Serialize(new { version, releaseTag, publishedAt });
            File.WriteAllText(UpdateManifestPath, manifest + Environment.NewLine, Encoding.UTF8);
            await RunGitAsync("add", "--", "update.json");
            await RunGitAsync("commit", "-m", "seed existing update manifest");
        }

        public async Task CheckoutDevelopmentBranchAsync()
        {
            await RunGitAsync("checkout", "-b", "dev");
            await RunGitAsync("push", "-u", "origin", "dev");
        }

        public async Task CreateLocalTagAsync(string tag, string commit)
        {
            await RunGitAsync("tag", tag, commit);
        }

        public async Task<string> HeadCommitAsync()
        {
            return await RunGitAsync("rev-parse", "HEAD");
        }

        public async Task<string?> LocalTagCommitAsync(string tag)
        {
            ProcessResult result = await RunProcessAsync(
                gitPath,
                new[] { "rev-parse", "--verify", $"{tag}^{{commit}}" },
                Root,
                null);
            return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
        }

        public async Task<string?> RemoteTagCommitAsync(string tag)
        {
            // bareのリモートを直接読み、ls-remoteの子プロセス連鎖を避けます。refが指すオブジェクトIDを観測します。
            ProcessResult result = await RunProcessAsync(
                gitPath,
                new[] { "--git-dir", RemotePath, "rev-parse", "--verify", "--quiet", $"refs/tags/{tag}" },
                Root,
                null);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }
            return result.StandardOutput.Trim();
        }

        public async Task<string> RemoteMainCommitAsync()
        {
            return await RunGitAsync("--git-dir", RemotePath, "rev-parse", "--verify", "refs/heads/main");
        }

        public async Task<string[]> DiffNamesAsync(string baseCommit)
        {
            return (await RunGitAsync("diff", "--name-only", $"{baseCommit}..HEAD"))
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Replace('\\', '/'))
                .ToArray();
        }

        public async Task SetRemoteTagCommitAsync(string commit)
        {
            await RunGitAtAsync(RemotePath, "update-ref", $"refs/tags/{Tag}", commit);
        }

        public void WriteRemoteAssetsJson(string json)
        {
            File.WriteAllText(GhAssetsPath, json, Encoding.ASCII);
        }

        /// <summary>指定した合格根拠だけを壊し、公開副作用前の拒否を観測します。</summary>
        public void InvalidateFullEvidence(string mismatch)
        {
            if (mismatch == "missing") { File.Delete(SummaryPath); return; }
            if (mismatch == "zip") { RewriteNormalPackageContent(); return; }
            JsonNode summary = JsonNode.Parse(File.ReadAllText(SummaryPath))
                ?? throw new InvalidDataException("summary がありません。");
            JsonNode artifact = summary["releaseArtifacts"]?[0]
                ?? throw new InvalidDataException("公開 ZIP がありません。");
            switch (mismatch)
            {
                case "profile": summary["profile"] = "verify"; break;
                case "failed": summary["passed"] = false; break;
                case "catalog": summary["distributionCatalog"] = workspace.GetPath("other-catalog.json"); break;
                case "set": summary["releaseArtifacts"] = new JsonArray(artifact.DeepClone()); break;
                case "hash": artifact["sha256"] = new string('0', 64); break;
                case "size": artifact["sizeBytes"] = new FileInfo(NormalPackagePath).Length + 1; break;
                case "version": artifact["version"] = "1.0.0.0"; break;
                case "path": artifact["packagePath"] = MetadataPackagePath; break;
                default: throw new ArgumentException("不明な不一致条件です。", nameof(mismatch));
            }
            File.WriteAllText(SummaryPath, summary.ToJsonString());
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

        public Task<ProcessResult> RunReleaseAsync(params string[] arguments)
        {
            var additions = new Dictionary<string, string?>
            {
                ["GH_FAIL_EDIT"] = FailReleasePublication ? "1" : null
            };
            return RunProcessAsync(pwshPath, new[] { "-NoProfile", "-NonInteractive", "-File", ScriptPath,
                "-DistributionCatalog", CatalogPath, "-FullSummary", SummaryPath }.Concat(arguments), Root, additions);
        }

        public async Task AdvanceRemoteMainOnIndependentCloneAsync()
        {
            string cloneRoot = workspace.GetPath("mutator");
            await RunGitAtAsync(workspace.RootDirectory, "clone", "--branch", "main", RemotePath, cloneRoot);
            File.WriteAllText(Path.Combine(cloneRoot, "remote-only.txt"), "remote main advanced", Encoding.UTF8);
            await RunGitAtAsync(cloneRoot, "config", "user.email", "release-tests@example.invalid");
            await RunGitAtAsync(cloneRoot, "config", "user.name", "Release Integration Tests");
            await RunGitAtAsync(cloneRoot, "add", "--", "remote-only.txt");
            await RunGitAtAsync(cloneRoot, "commit", "-m", "remote main change");
            await RunGitAtAsync(cloneRoot, "push", "origin", "main");
            await RunGitAsync("fetch", "origin", "main");
        }

        public void CleanupWorkspace(ProcessDeadline cleanupDeadline)
        {
            if (preserveArtifacts) { return; }
            try
            {
                RequireTime(cleanupDeadline);
                // Gitが作った専有objectだけを削除可能へ戻し、全プロセス終端後に一度回収します。
                foreach (string objects in new[] { Path.Combine(Root, ".git", "objects"),
                    Path.Combine(RemotePath, "objects"), workspace.GetPath("mutator/.git/objects") })
                {
                    if (!Directory.Exists(objects)) { continue; }
                    foreach (string file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
                    {
                        RequireTime(cleanupDeadline);
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    }
                }
                RequireTime(cleanupDeadline);
                workspace.Dispose();
                RequireTime(cleanupDeadline);
            }
            catch
            {
                PreserveArtifacts();
                throw;
            }
        }

        private void PreserveArtifacts()
        {
            preserveArtifacts = true;
            workspace.PreserveArtifacts();
        }

        private static void RequireTime(ProcessDeadline deadline)
        {
            if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("Releaseケースの共有期限を超えました。"); }
        }

        /// <summary>最初のコミットへ入る内容だけを作ります。テンプレート作成時に一度だけ呼びます。</summary>
        private void CreateTrackedFiles(bool includeCompatibilityFile)
        {
            Directory.CreateDirectory(Path.Combine(Root, "scripts"));
            Directory.CreateDirectory(Path.Combine(Root, "BeMusicSeeker", "Properties"));
            Directory.CreateDirectory(Path.Combine(Root, "release notes"));
            Directory.CreateDirectory(Path.Combine(Root, "docs"));

            string repositoryRoot = FindRepositoryRoot();
            File.Copy(Path.Combine(repositoryRoot, "scripts", "release.ps1"), ScriptPath);
            File.Copy(Path.Combine(repositoryRoot, "scripts", "application-version.ps1"), Path.Combine(Root, "scripts", "application-version.ps1"));
            File.Copy(
                Path.Combine(repositoryRoot, "scripts", "portable-package-layout.ps1"),
                Path.Combine(Root, "scripts", "portable-package-layout.ps1"));
            File.WriteAllText(
                Path.Combine(Root, "ApplicationVersion.props"),
                "<Project><PropertyGroup><BeMusicSeekerAppVersion>9.9.9.9</BeMusicSeekerAppVersion></PropertyGroup></Project>",
                Encoding.UTF8);
            string sourceNote = Directory.GetFiles(Path.Combine(repositoryRoot, "release notes"), "v2.1.6.0*").Single();
            File.Copy(sourceNote, Path.Combine(Root, "release notes", $"v{Version} リリースノート.md"));
            File.WriteAllText(Path.Combine(Root, "docs", "index.html"), "source", Encoding.UTF8);
            File.WriteAllText(Path.Combine(Root, "docs", "index.ja.html"), "source-ja", Encoding.UTF8);
            File.WriteAllText(Path.Combine(Root, "docs", ".nojekyll"), string.Empty, Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(Root, ".gitignore"),
                "dist/\n",
                Encoding.ASCII);

            if (includeCompatibilityFile)
            {
                File.WriteAllText(Path.Combine(Root, "version.txt"), "2.1.6.0\n", Encoding.UTF8);
            }

            File.WriteAllText(UpdateManifestPath, "{\"unchanged\":true}\n", Encoding.UTF8);
        }

        /// <summary>Root外または.gitignoreで除外され、最初のコミットに入らないケース固有の内容を作ります。</summary>
        private void CreateCaseFiles(bool includeMetadata)
        {
            Directory.CreateDirectory(DistRoot);
            Directory.CreateDirectory(FakeBinRoot);
            CreateZip(NormalPackagePath, includeMetadata: false);
            if (includeMetadata)
            {
                CreateZip(MetadataPackagePath, includeMetadata: true);
            }

            WriteFullEvidence();

            File.Delete(GhStatePath);
            File.WriteAllText(GhAssetsPath, BuildAssetJson(includeMetadata), Encoding.ASCII);
            File.WriteAllText(GhLogPath, string.Empty, Encoding.UTF8);
            File.WriteAllText(Path.Combine(FakeBinRoot, "gh.cmd"), FakeGhScript, Encoding.ASCII);
            File.WriteAllText(Path.Combine(FakeBinRoot, "uv.cmd"), FakeUvScript, Encoding.ASCII);
        }

        /// <summary>現在のfixture ZIPに合格記録を接続し、公開時の入力照合を通す前提を用意します。</summary>
        public void WriteFullEvidence()
        {
            object Artifact(string path) => new
            {
                version = Version,
                packagePath = path,
                sizeBytes = new FileInfo(path).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            };
            object normal = Artifact(NormalPackagePath);
            object? metadata = File.Exists(MetadataPackagePath) ? Artifact(MetadataPackagePath) : null;
            File.WriteAllText(CatalogPath, JsonSerializer.Serialize(new
            {
                Current = normal,
                CurrentWithMetadata = metadata
            }));
            File.WriteAllText(SummaryPath, JsonSerializer.Serialize(new
            {
                profile = "full",
                passed = true,
                distributionCatalog = CatalogPath,
                releaseArtifacts = metadata is null ? new[] { normal } : new[] { normal, metadata }
            }));
        }

        private async Task InitializeGitAsync()
        {
            await RunGitAsync("init");
            await RunGitAsync("checkout", "-b", "main");
            await RunGitAsync("config", "user.email", "release-tests@example.invalid");
            await RunGitAsync("config", "user.name", "Release Integration Tests");
            await RunGitAsync("add", ".");
            await RunGitAsync("commit", "-m", "fixture base");
            InitialHead = await HeadCommitAsync();
            await RunGitAsync("init", "--bare", RemotePath);
            await RunGitAsync("remote", "add", "origin", RemotePath);
            await RunGitAsync("push", "-u", "origin", "main");
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
            }
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

        private Task<string> RunGitAsync(params string[] arguments) => RunGitAtAsync(Root, arguments);

        private async Task<string> RunGitAtAsync(string workingDirectory, params string[] arguments)
        {
            ProcessResult result = await RunProcessAsync(gitPath, arguments, workingDirectory);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git failed: exit=0x{unchecked((uint)result.ExitCode):X8} ({result.ExitCode}), " +
                    $"cwd={workingDirectory}, args=[{string.Join(' ', arguments)}], " +
                    $"stdout=[{result.StandardOutput}], stderr=[{result.StandardError}]");
            }
            return result.StandardOutput.Trim();
        }

        private async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> arguments,
            string workingDirectory, IReadOnlyDictionary<string, string?>? additions = null)
        {
            RequireTime(deadline);
            cancellationToken.ThrowIfCancellationRequested();
            // 待機の上限はケース共通の期限だけで、一回のcommandに独自の入れ子上限を設けない。
            var childEnvironment = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
            if (additions is not null)
            {
                foreach ((string key, string? value) in additions) { childEnvironment[key] = value; }
            }
            OwnedProcess? process = null;
            ProcessCompletion? completion = null;
            Exception? primary = null;
            Exception? cleanup = null;
            try
            {
                process = OwnedProcess.Start(new ProcessStartRequest(fileName, arguments.ToArray(),
                    workingDirectory, childEnvironment, ProcessOwnershipPolicy.Application), () => failureCleanup.Value);
                completion = await process.WaitForExitAsync(deadline, cancellationToken);
            }
            catch (Exception failure)
            {
                primary = failure;
                _ = failureCleanup.Value;
                if (failure is OwnedProcessStartException { CleanupFailure: not null }) { PreserveArtifacts(); }
            }
            if (process is not null)
            {
                try
                {
                    ProcessStopResult stopped = await process.StopAsync(null, primary is null ? deadline : failureCleanup.Value);
                    cleanup = stopped.CleanupFailure;
                    if (cleanup is not null) { _ = failureCleanup.Value; }
                    if (cleanup is not null || !process.Completion.IsCompletedSuccessfully) { PreserveArtifacts(); }
                }
                catch (Exception failure)
                {
                    cleanup = failure;
                    _ = failureCleanup.Value;
                    PreserveArtifacts();
                }
                try
                {
                    // StopAsyncの確定した期限を再利用するため、Disposeの既定10秒は追加されません。
                    await process.DisposeAsync();
                }
                catch (Exception failure)
                {
                    _ = failureCleanup.Value;
                    if (!ReferenceEquals(cleanup, failure))
                    {
                        cleanup = cleanup is null ? failure : new AggregateException(cleanup, failure);
                    }
                    PreserveArtifacts();
                }
            }
            ThrowFailures(primary, cleanup);
            ProcessCompletion result = completion ?? throw new InvalidOperationException("プロセス結果を回収できません。");
            return new ProcessResult(result.ExitCode, result.StandardOutput, result.StandardError);
        }

        private Dictionary<string, string?> CreateEnvironment()
        {
            var child = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in Environment.GetEnvironmentVariables().Keys)
            {
                if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("GH_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("UV_", StringComparison.OrdinalIgnoreCase))
                {
                    child[key] = null;
                }
            }
            string hooks = workspace.GetPath("empty-hooks");
            Directory.CreateDirectory(hooks);
            string configuration = workspace.GetPath("gitconfig");
            string escapedHooks = hooks.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal);
            File.WriteAllText(configuration,
                "[user]\n name = Release Integration Tests\n email = release-tests@example.invalid\n" +
                "[commit]\n gpgsign = false\n[tag]\n gpgsign = false\n" +
                $"[core]\n hooksPath = \"{escapedHooks}\"\n autocrlf = false\n" +
                $"[init]\n templateDir = \"{escapedHooks}\"\n", new UTF8Encoding(false));
            child["GIT_CONFIG_NOSYSTEM"] = "1";
            child["GIT_CONFIG_GLOBAL"] = configuration;
            child["GIT_TERMINAL_PROMPT"] = "0";
            child["GIT_ALLOW_PROTOCOL"] = "file";
            child["GH_STATE"] = GhStatePath;
            child["GH_ASSETS"] = GhAssetsPath;
            child["GH_LOG"] = GhLogPath;
            // 実gh/uvや別Gitの発見を避け、子だけを専有代替と解決済み実行体へ限定します。
            // Git Bash等からMSYSTEMを継承した子でGit for WindowsのmingwXX\bin\git.exeを直接起動すると、gitがPATHを補わず
            // ローカルpushが起動するshを解決できないため、0xC0000005で異常終了します。MSYSTEMが未設定ならmingw側のgitも
            // cmd\git.exeも自前でPATHを補うため、PATH先頭がmingw側でMSYSTEMを継承した環境でだけ起きます。
            // 同じ導入先のusr\binを末尾へ足し、PATHの並びに依らず同じ結果にします。usr\binにはfind.exeなど
            // System32と同名のUnix版があるため、System32より前に置いて置き換えません。shはSystem32にないので末尾でも解決できます。
            string gitDirectory = Path.GetDirectoryName(gitPath)
                ?? throw new InvalidOperationException($"gitの格納ディレクトリを解決できません: {gitPath}");
            string pwshDirectory = Path.GetDirectoryName(pwshPath)
                ?? throw new InvalidOperationException($"pwshの格納ディレクトリを解決できません: {pwshPath}");
            string gitShellDirectory = Path.GetFullPath(Path.Combine(gitDirectory, "..", "..", "usr", "bin"));
            var searchPath = new List<string> { FakeBinRoot, gitDirectory, pwshDirectory, Environment.SystemDirectory };
            if (Directory.Exists(gitShellDirectory)) { searchPath.Add(gitShellDirectory); }
            child["PATH"] = string.Join(Path.PathSeparator, searchPath);
            child["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            return child;
        }

        private static string ResolveExecutable(string name)
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                string candidateDirectory = directory.Trim('"');
                if (!Path.IsPathFullyQualified(candidateDirectory)) { continue; }
                string candidate = Path.Combine(candidateDirectory, name);
                if (File.Exists(candidate)) { return Path.GetFullPath(candidate); }
            }
            throw new FileNotFoundException($"実行に必要な実ファイルをPATHから解決できません: {name}");
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
