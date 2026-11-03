using System.Net.Http;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>親runnerが所有する240秒段階の中で、準備と検査を同じ絶対期限へ接続します。</summary>
internal delegate Task RunDistributionStage(string name, Func<ExecuteCommand, ProcessDeadline, CancellationToken, Task> action);

/// <summary>公開候補生成で固定旧ZIP、現行版、次版を一度ずつ準備します。</summary>
internal static class DistributionPreparer
{
    // UTF-8固定のpipe readerへ合わせるため、子の起動時に出力encodingを確定する。
    private const string PowerShellUtf8 = "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); ";

    /// <summary>存在しない専有絶対出力へ配布物を生成し、全検査の完成後だけ直下のcatalogをatomicに公開します。metadata-sourceは同梱指定時だけ受け付けます。</summary>
    internal static async Task<string> PrepareAsync(string repository, string outputRoot, RunDistributionStage runStage,
        bool includeMetadata = false, string? metadataSource = null)
    {
        if (!Path.IsPathFullyQualified(outputRoot)) { throw new ArgumentException("候補出力には絶対パスが必要です。", nameof(outputRoot)); }
        if (metadataSource is not null && !includeMetadata) { throw new ArgumentException("metadata-sourceにはinclude-metadataが必要です。"); }
        string root = Path.GetFullPath(outputRoot);
        if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("候補出力は存在しない専有パスが必要です。"); }
        DistributionArtifact? legacy = null, current = null, metadata = null;
        Directory.CreateDirectory(root);
        await runStage("legacy-artifact", async (_, deadline, token) =>
        {
            legacy = await PrepareLegacyAsync(repository, deadline, token);
        });
        await runStage("publish-v", async (execute, deadline, token) =>
        {
            current = await PublishAsync(repository, Path.Combine(root, "v"), null, execute, deadline, token, includeMetadata, metadataSource);
            if (includeMetadata)
            {
                string package = Path.Combine(root, "v", "dist", $"bemusicseeker-unofficial-fork-v{current.Version}-with-metadata.zip");
                string app = current.AppPublishRoot ?? throw new InvalidDataException("現行publish出力がありません。");
                string updater = current.UpdaterPublishRoot ?? throw new InvalidDataException("updater publish出力がありません。");
                IReadOnlyList<string> managed = await DistributionValidation.ValidateZipAsync(package, app, updater, current.Version, deadline, token);
                metadata = new(current.Version, app, updater, package, new FileInfo(package).Length,
                    await DistributionValidation.HashAsync(package, deadline, token), managed);
                await DistributionValidation.ValidateMetadataVariantAsync(current, metadata, deadline, token);
            }
        });
        DistributionArtifact completedCurrent = current ?? throw new InvalidOperationException("現行配布の準備が完了していません。");
        string nextVersion = DistributionValidation.IncrementVersion(completedCurrent.Version);
        await runStage("publish-next", async (execute, deadline, token) =>
        {
            DistributionArtifact next = await PublishAsync(repository, Path.Combine(root, "next"), nextVersion, execute, deadline, token);
            var catalog = new DistributionCatalog(legacy ?? throw new InvalidOperationException("固定旧版の準備が完了していません。"),
                completedCurrent, next)
            { CurrentWithMetadata = metadata };
            string temporary = Path.Combine(root, "catalog.json.tmp");
            Exception? primary = null;
            try
            {
                using CancellationTokenSource budget = DistributionValidation.CreateBudget(deadline, token);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                {
                    await JsonSerializer.SerializeAsync(output, catalog, cancellationToken: budget.Token);
                    await output.FlushAsync(budget.Token);
                }
                DistributionValidation.CheckDeadline(deadline, token);
                File.Move(temporary, Path.Combine(root, "catalog.json"));
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                try { if (File.Exists(temporary)) { File.Delete(temporary); } }
                catch (Exception cleanup) when (primary is not null) { throw new AggregateException("catalogの保存と一時fileの回収に失敗しました。", primary, cleanup); }
            }
        });
        return Path.Combine(root, "catalog.json");
    }

    private static async Task<DistributionArtifact> PublishAsync(string repository, string root, string? version,
        ExecuteCommand execute, ProcessDeadline deadline, CancellationToken token, bool includeMetadata = false, string? metadataSource = null)
    {
        DistributionValidation.CheckDeadline(deadline, token);
        string publish = PowerShellUtf8 + "& " + Quote(Path.Combine(repository, "scripts", "publish.ps1"))
            + " -ArtifactRoot " + Quote(root)
            + (includeMetadata ? " -IncludeMetadata" : "")
            + (metadataSource is null ? "" : " -MetadataSource " + Quote(metadataSource))
            + (version is null ? "" : " -ApplicationVersion " + Quote(version));
        string[] arguments = ["-NoProfile", "-NonInteractive", "-Command", publish];
        RequireSuccess(await execute("pwsh", arguments, deadline));
        DistributionValidation.CheckDeadline(deadline, token);
        string app = Path.Combine(root, "app"), updater = Path.Combine(root, "updater");
        string actualVersion = DistributionValidation.ReadVersion(Path.Combine(app, "BeMusicSeeker.exe"));
        if (version is not null && actualVersion != version) { throw new InvalidDataException("次版publishへ版指定が反映されていません。"); }
        string package = Path.Combine(root, "dist", $"bemusicseeker-unofficial-fork-v{actualVersion}.zip");
        if (!File.Exists(Path.Combine(updater, "BeMusicSeeker.Updater.exe"))) { throw new InvalidDataException("updater publishがありません。"); }
        await ValidatePackageLayoutAsync(repository, package, execute, deadline, token);
        foreach (string file in Directory.EnumerateFiles(updater, "*", SearchOption.AllDirectories)) { DistributionValidation.AssertNoTestDependency(file); }
        IReadOnlyList<string> managed = await DistributionValidation.ValidateZipAsync(package, app, updater, actualVersion, deadline, token);
        string hash = await DistributionValidation.HashAsync(package, deadline, token);
        DistributionValidation.CheckDeadline(deadline, token);
        return new(actualVersion, app, updater, package, new FileInfo(package).Length, hash, managed);
    }

    /// <summary>生成時とFullの候補検査で配布layoutの正本を実行し、同じ段階期限内で成功を要求します。ZIPの再生成は行いません。</summary>
    internal static async Task ValidatePackageLayoutAsync(string repository, string package, ExecuteCommand execute,
        ProcessDeadline deadline, CancellationToken token)
    {
        DistributionValidation.CheckDeadline(deadline, token);
        // 必須・禁止file一覧はPowerShellの正本へ委ね、consumer側へ複製しない。
        string layout = ". " + Quote(Path.Combine(repository, "scripts", "portable-package-layout.ps1"))
            + "; Assert-PortableReleasePackageLayout " + Quote(package);
        RequireSuccess(await execute("pwsh", ["-NoProfile", "-NonInteractive", "-Command", PowerShellUtf8 + "$ErrorActionPreference='Stop'; " + layout], deadline));
        DistributionValidation.CheckDeadline(deadline, token);
    }

    /// <summary>追跡metadataだけをidentityの正本として、既存cacheの検査または一回だけの取得へ進みます。</summary>
    internal static async Task<DistributionArtifact> PrepareLegacyAsync(string repository, ProcessDeadline deadline, CancellationToken token)
    {
        (string Version, string CachePath, Uri DownloadUri, long SizeBytes, string Sha256) descriptor = await ReadLegacyDescriptorAsync(repository, deadline, token);
        await AcquireLegacyAsync(descriptor.CachePath, descriptor.DownloadUri, descriptor.SizeBytes, descriptor.Sha256, DownloadAsync, deadline, token);
        return new(descriptor.Version, null, null, descriptor.CachePath, descriptor.SizeBytes, descriptor.Sha256, []);
    }

    /// <summary>呼出元が信頼するrepositoryの追跡descriptorを読みます。cacheの存在確認・取得は行わず、producerとconsumerが同じidentityを使います。</summary>
    internal static async Task<(string Version, string CachePath, Uri DownloadUri, long SizeBytes, string Sha256)> ReadLegacyDescriptorAsync(
        string repository, ProcessDeadline deadline, CancellationToken token)
    {
        using CancellationTokenSource budget = DistributionValidation.CreateBudget(deadline, token);
        string metadataPath = Path.Combine(repository, "devdocs", "acceptance", "v216-first-hop", "artifact.json");
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, budget.Token));
        JsonElement value = metadata.RootElement;
        if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("manifestType").GetString() != "BeMusicSeeker.V216Artifact"
            || value.GetProperty("packageFormatVersion").GetInt32() != 1
            || !value.GetProperty("sealed").GetBoolean()) { throw new InvalidDataException("固定旧版metadataのidentityが不正です。"); }
        string version = value.GetProperty("version").GetString() ?? throw new InvalidDataException("固定旧版の版がありません。");
        _ = DistributionValidation.ParseVersion(version);
        string fileName = value.GetProperty("fileName").GetString() ?? throw new InvalidDataException("固定旧版のfile名がありません。");
        long size = value.GetProperty("sizeBytes").GetInt64();
        string hash = value.GetProperty("sha256").GetString() ?? throw new InvalidDataException("固定旧版hashがありません。");
        if (size <= 0 || hash.Length != 64 || !hash.All(Uri.IsHexDigit)
            || !Uri.TryCreate(value.GetProperty("downloadUrl").GetString(), UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
        {
            throw new InvalidDataException("固定旧版metadataの値が不正です。");
        }
        string relative = value.GetProperty("artifactPath").GetString() ?? throw new InvalidDataException("固定旧版cache pathがありません。");
        string path = Path.GetFullPath(Path.Combine(repository, relative));
        if (Path.GetFileName(path) != fileName || !path.StartsWith(Path.GetFullPath(repository).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("固定旧版cacheのパスが不正です。"); }
        DistributionValidation.CheckDeadline(deadline, token);
        return (version, path, uri, size, hash);
    }

    /// <summary>不正cacheは変更せず失敗し、不在の場合だけ同一directoryの検査済みtempをatomic公開します。</summary>
    internal static async Task AcquireLegacyAsync(string path, Uri uri, long size, string hash,
        Func<Uri, string, CancellationToken, Task> download, ProcessDeadline deadline, CancellationToken token)
    {
        if (Directory.Exists(path)) { throw new InvalidDataException("固定旧版cacheがfileではありません。"); }
        if (File.Exists(path))
        {
            await DistributionValidation.AssertIdentityAsync(path, size, hash, deadline, token);
            return;
        }
        DistributionValidation.CheckDeadline(deadline, token);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidDataException("cacheの親がありません。"));
        string temporary = path + ".download-" + Guid.NewGuid().ToString("N");
        Exception? primary = null;
        try
        {
            using CancellationTokenSource budget = DistributionValidation.CreateBudget(deadline, token);
            await download(uri, temporary, budget.Token);
            await DistributionValidation.AssertIdentityAsync(temporary, size, hash, deadline, token);
            DistributionValidation.CheckDeadline(deadline, token);
            File.Move(temporary, path);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { if (File.Exists(temporary)) { File.Delete(temporary); } }
            catch (Exception cleanup) when (primary is not null) { throw new AggregateException("旧配布ZIPの取得と一時fileの回収に失敗しました。", primary, cleanup); }
        }
    }

    private static async Task DownloadAsync(Uri uri, string path, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using Stream input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token);
        await output.FlushAsync(token);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void RequireSuccess(ProcessCompletion completion)
    {
        if (completion.ExitCode != 0) { throw new InvalidOperationException($"配布commandが失敗しました: exit={completion.ExitCode}\n{completion.StandardError}"); }
    }
}
