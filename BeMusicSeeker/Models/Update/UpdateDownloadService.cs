using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdateDownloadService
{
    /// <summary>応答ヘッダーの受信までと、本文の各読取りに適用する固定の期限です。通信設定には依存しません。</summary>
    private static readonly TimeSpan NoReceiveTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromSeconds(1);
    private const long ProgressByteInterval = 512 * 1024;
    private const int CopyBufferBytes = 81920;
    private const string UpdateFailureReceiptFileName = "update-failure.txt";
    private const string UpdateFailureReceiptTemporaryFileName = "update-failure.txt.tmp";
    private const string TransactionJournalFileName = "update-transaction.json";
    private const string TransactionLockFileName = "update-transaction.lock";
    private const string UpdaterReadyFileName = "updater-ready.txt";
    private const string UpdaterDecisionFileName = "updater-decision.txt";
    private const string UpdaterExecutableFileName = "BeMusicSeeker.Updater.exe";
    private static readonly string[] UpdaterPayloadFileNames =
    [
        "BeMusicSeeker.Updater.exe"
    ];

    private static readonly string[] LegacyUpdaterPayloadFileNames =
    [
        "BeMusicSeeker.Updater.dll",
        "BeMusicSeeker.Updater.deps.json",
        "BeMusicSeeker.Updater.runtimeconfig.json"
    ];

    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    private readonly IUpdaterProcessGateway updaterProcessGateway;

    private readonly IReadOnlyList<string> restartArguments;

    private readonly HttpMessageHandler httpHandler;

    private readonly TimeProvider timeProvider;

    private readonly Func<string, FileStream> openDownloadFile;

    /// <summary>更新ZIPの取得と更新ツールの起動準備を担当するサービスを作成します。</summary>
    /// <param name="applicationPathSnapshot">起動時に確定したアプリの配置です。</param>
    /// <param name="updaterProcessGateway">更新ツールの起動窓口です。</param>
    /// <param name="restartArguments">
    /// アプリが起動時に捕捉した再起動引数です。動作モード変更の再起動と同じ値を、更新後の再起動へ引き継ぎます。
    /// 引数なしで起動した場合は空の列を渡します。
    /// </param>
    /// <param name="httpHandler">
    /// 取得に使うHTTPハンドラーです。呼出し側が所有し、サービスは破棄しません。省略すると取得ごとに既定のハンドラーを作ります。
    /// </param>
    /// <param name="timeProvider">取得の期限と進捗のまとめに使う時計です。省略すると <see cref="TimeProvider.System"/> です。</param>
    /// <param name="openDownloadFile">
    /// 一時ファイルを新規作成して書込み用に開く処理です。省略すると長いパスに対応した共有なしの書込みで開きます。
    /// </param>
    internal UpdateDownloadService(
        ApplicationPathSnapshot applicationPathSnapshot,
        IUpdaterProcessGateway updaterProcessGateway,
        IReadOnlyList<string> restartArguments,
        HttpMessageHandler httpHandler = null,
        TimeProvider timeProvider = null,
        Func<string, FileStream> openDownloadFile = null)
    {
        this.httpHandler = httpHandler;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.openDownloadFile = openDownloadFile
            ?? (path => LongPathFileSystem.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        this.restartArguments = restartArguments
            ?? throw new ArgumentNullException(nameof(restartArguments));
        this.applicationPathSnapshot = applicationPathSnapshot
            ?? throw new ArgumentNullException(nameof(applicationPathSnapshot));
        this.updaterProcessGateway = updaterProcessGateway
            ?? throw new ArgumentNullException(nameof(updaterProcessGateway));
    }

    /// <summary>
    /// 更新ZIPを一時ファイルへ逐次書き込み、サイズとSHA-256を同じ読取りで検証してから完成名へ移します。
    /// </summary>
    /// <param name="asset">取得する資産です。<see cref="UpdateAssetInfo.SizeBytes"/> と <see cref="UpdateAssetInfo.Sha256"/> が受入条件です。</param>
    /// <param name="cancellationToken">
    /// 呼出し側の取消です。ヘッダー待ち・本文読取り・ファイル書込みへ伝わり、<see cref="OperationCanceledException"/> で終わります。
    /// </param>
    /// <param name="progress">受信済み量の通知先です。通知は量と経過時間でまとめ、返したTaskの完了後には呼びません。</param>
    /// <returns><c>downloads/&lt;fileName&gt;</c> のパスです。</returns>
    /// <remarks>
    /// 転送全体の時間制限は設けません。応答ヘッダーの受信までと、本文の各読取りに固定の期限だけを適用し、
    /// ディスク書込みの時間は含めません。期限切れは <see cref="TimeoutException"/> として取消以外の失敗にします。
    /// 失敗・取消では本文ストリームを閉じてから自分が作った一時ファイルを消し、保留中の実I/Oが終わってから完了します。
    /// 回収の失敗で元の失敗・取消を置き換えません。
    /// </remarks>
    public async Task<string> DownloadAndVerifyAsync(
        UpdateAssetInfo asset,
        CancellationToken cancellationToken = default,
        IProgress<UpdateDownloadProgress> progress = null)
    {
        if (asset == null)
        {
            throw new ArgumentNullException(nameof(asset));
        }

        // 不正な名前は、HTTP要求・ディレクトリ作成・リパースポイント検査のどれよりも前に拒否する。
        string fileName = Path.GetFileName(asset.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName != asset.FileName)
        {
            throw new UpdateManifestValidationException("Invalid update package file name.");
        }

        string workRoot = GetUpdateWorkRoot();
        string downloadsDirectory = Path.Combine(workRoot, "downloads");
        EnsureNoReparsePointAncestors(applicationPathSnapshot.BaseDirectory, "update_work/downloads");
        EnsureNoReparsePointIfPresent(workRoot);
        if (LongPathFileSystem.DirectoryExists(workRoot))
        {
            EnsureNoReparsePointTree(workRoot);
        }
        EnsureNoReparsePointIfPresent(downloadsDirectory);
        LongPathFileSystem.CreateDirectory(downloadsDirectory);

        string packagePath = Path.Combine(downloadsDirectory, fileName);
        string temporaryPath = packagePath + ".download";
        if (LongPathFileSystem.FileExists(temporaryPath))
        {
            LongPathFileSystem.DeleteFile(temporaryPath);
        }

        FileStream destination = null;
        bool temporaryCreated = false;
        bool completed = false;
        try
        {
            using HttpClient httpClient = httpHandler == null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
            using HttpResponseMessage response = await SendWithDeadlineAsync(httpClient, asset.Url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string actualSha256;
            Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                destination = openDownloadFile(temporaryPath);
                temporaryCreated = true;
                actualSha256 = await CopyAndHashAsync(source, destination, asset.SizeBytes, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // 本文を閉じてから一時ファイルを閉じて消す順序を保つ。
                try
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception closeFailure)
                {
                    // 閉じる処理の失敗で、元の失敗・取消を置き換えない。読取りが済んだ後の失敗も診断だけ残す。
                    WarnCleanupFailure(closeFailure, "update package response body close failed");
                }
            }

            await destination.DisposeAsync().ConfigureAwait(false);
            destination = null;
            if (!string.Equals(actualSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Downloaded update package SHA-256 does not match update.json.");
            }

            // 最終通知は完成名への移動の前に行う。通知先が失敗しても完成名を残さず、返したTaskの完了後には通知しない。
            progress?.Report(new UpdateDownloadProgress(asset.SizeBytes, asset.SizeBytes));
            cancellationToken.ThrowIfCancellationRequested();
            LongPathFileSystem.MoveFile(temporaryPath, packagePath, overwrite: true);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                ReclaimTemporaryFile(destination, temporaryPath, temporaryCreated);
            }
        }

        return packagePath;
    }

    /// <summary>応答ヘッダーを受信するまでを固定期限で待ちます。期限切れは取消以外の失敗にします。</summary>
    private async Task<HttpResponseMessage> SendWithDeadlineAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(NoReceiveTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        HttpResponseMessage response = null;
        try
        {
            response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            ThrowIfExpired(deadline, cancellationToken, null);
            return response;
        }
        catch (OperationCanceledException exception) when (IsExpired(deadline, cancellationToken))
        {
            response?.Dispose();
            throw CreateTimeout(exception);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 本文を読み、受信量とSHA-256を同じ読取りで更新しながら一時ファイルへ書きます。
    /// 受信量が期待サイズを超えた時点で失敗にし、EOFでサイズが一致しなければ失敗にします。
    /// </summary>
    private async Task<string> CopyAndHashAsync(
        Stream source,
        FileStream destination,
        long expectedBytes,
        IProgress<UpdateDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferBytes];
        long received = 0;
        long reported = 0;
        long lastReportTimestamp = timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = await ReadWithDeadlineAsync(source, buffer, cancellationToken).ConfigureAwait(false);
            if (count <= 0)
            {
                break;
            }

            received += count;
            if (received > expectedBytes)
            {
                throw new InvalidOperationException("Downloaded update package size does not match update.json.");
            }

            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (progress != null && ShouldReport(received - reported, lastReportTimestamp))
            {
                reported = received;
                lastReportTimestamp = timeProvider.GetTimestamp();
                progress.Report(new UpdateDownloadProgress(received, expectedBytes));
            }
        }

        if (received != expectedBytes)
        {
            throw new InvalidOperationException("Downloaded update package size does not match update.json.");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private bool ShouldReport(long unreportedBytes, long lastReportTimestamp)
    {
        return unreportedBytes >= ProgressByteInterval
            || timeProvider.GetElapsedTime(lastReportTimestamp) >= ProgressTimeInterval;
    }

    /// <summary>本文の1回の読取りを固定期限で待ちます。期限は読取りの開始前に設定し、書込みの時間を含めません。</summary>
    private async Task<int> ReadWithDeadlineAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(NoReceiveTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            int count = await source.ReadAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false);
            // 取消を無視して読取りが完了した場合も、呼出し側の取消を優先し、次に期限切れを失敗にする。
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExpired(deadline, cancellationToken, null);
            return count;
        }
        catch (OperationCanceledException exception) when (IsExpired(deadline, cancellationToken))
        {
            throw CreateTimeout(exception);
        }
    }

    private static bool IsExpired(CancellationTokenSource deadline, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested;

    private static void ThrowIfExpired(CancellationTokenSource deadline, CancellationToken cancellationToken, Exception inner)
    {
        if (IsExpired(deadline, cancellationToken))
        {
            throw CreateTimeout(inner);
        }
    }

    private static TimeoutException CreateTimeout(Exception inner)
        => new("The update package download received no data within the allowed time.", inner);

    /// <summary>
    /// 失敗・取消の後に、自分が作った一時ファイルを閉じて消します。回収の失敗は元の失敗・取消を置き換えないため、警告として記録して伝えません。
    /// 残った一時ファイルは次回の取得開始時に消します。
    /// </summary>
    private static void ReclaimTemporaryFile(FileStream destination, string temporaryPath, bool temporaryCreated)
    {
        try
        {
            destination?.Dispose();
        }
        catch (Exception closeFailure)
        {
            WarnCleanupFailure(closeFailure, "update package temporary file close failed");
        }

        if (!temporaryCreated)
        {
            return;
        }
        try
        {
            if (LongPathFileSystem.FileExists(temporaryPath))
            {
                LongPathFileSystem.DeleteFile(temporaryPath);
            }
        }
        catch (Exception deleteFailure)
        {
            WarnCleanupFailure(deleteFailure, "update package temporary file delete failed: " + temporaryPath);
        }
    }

    /// <summary>後片付けの二次的な失敗を警告として記録します。記録の失敗も、元の失敗・取消を置き換えません。</summary>
    private static void WarnCleanupFailure(Exception failure, string message)
    {
        try
        {
            NLogWrapper.FileLogger?.Warn(failure, message);
        }
        catch (Exception)
        {
        }
    }

    public IPreparedUpdaterLaunch PrepareUpdaterLaunch(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("An update package path is required.", nameof(packagePath));
        }

        string appDirectory = applicationPathSnapshot.BaseDirectory;
        EnsureDownloadedPackagePath(packagePath);
        string currentUpdaterDirectory = Path.Combine(GetUpdateWorkRoot(), "current");
        EnsureNoReparsePointAncestors(appDirectory, "update_work/current");
        EnsureNoReparsePointIfPresent(currentUpdaterDirectory);
        if (LongPathFileSystem.DirectoryExists(currentUpdaterDirectory))
        {
            EnsureNoReparsePointTree(currentUpdaterDirectory);
        }
        LongPathFileSystem.CreateDirectory(currentUpdaterDirectory);
        string readyFilePath = Path.Combine(currentUpdaterDirectory, UpdaterReadyFileName);
        string decisionFilePath = Path.Combine(currentUpdaterDirectory, UpdaterDecisionFileName);
        EnsureNoReparsePointIfPresent(readyFilePath);
        if (LongPathFileSystem.FileExists(readyFilePath))
        {
            LongPathFileSystem.DeleteFile(readyFilePath);
        }
        EnsureNoReparsePointIfPresent(decisionFilePath);
        if (LongPathFileSystem.FileExists(decisionFilePath))
        {
            LongPathFileSystem.DeleteFile(decisionFilePath);
        }
        foreach (string legacyPayloadFileName in LegacyUpdaterPayloadFileNames)
        {
            string legacyPayloadPath = Path.Combine(currentUpdaterDirectory, legacyPayloadFileName);
            EnsureNoReparsePointIfPresent(legacyPayloadPath);
            if (LongPathFileSystem.FileExists(legacyPayloadPath))
            {
                LongPathFileSystem.DeleteFile(legacyPayloadPath);
            }
        }
        string updaterPath = null;
        foreach (string payloadFileName in UpdaterPayloadFileNames)
        {
            string sourcePath = Path.Combine(appDirectory, payloadFileName);
            if (!LongPathFileSystem.FileExists(sourcePath))
            {
                throw new FileNotFoundException("Updater payload file was not found.", sourcePath);
            }

            string destinationPath = Path.Combine(currentUpdaterDirectory, payloadFileName);
            EnsureNoReparsePointIfPresent(destinationPath);
            LongPathFileSystem.CopyFile(sourcePath, destinationPath, overwrite: true);
            if (string.Equals(payloadFileName, "BeMusicSeeker.Updater.exe", StringComparison.OrdinalIgnoreCase))
            {
                updaterPath = destinationPath;
            }
        }

        return updaterProcessGateway.Prepare(
            UpdaterProcessLaunchRequest.Create(
                updaterPath,
                currentUpdaterDirectory,
                appDirectory,
                packagePath,
                Path.Combine(appDirectory, "update_backup"),
                readyFilePath,
                decisionFilePath,
                applicationPathSnapshot.ExecutablePath,
                restartArguments));
    }

    internal string GetUpdateWorkRoot()
    {
        return Path.Combine(applicationPathSnapshot.BaseDirectory, "update_work");
    }

    /// <summary>
    /// 前回の一時物を回収し、未完了の更新と未確認の失敗記録を既存の回復・通知へ渡します。
    /// 更新ツールが使用し続けるcurrentと、解放後も同じパスで再利用する同期ロックは保持します。
    /// </summary>
    internal void CleanupPreviousWorkDirectory()
    {
        string workRoot = GetUpdateWorkRoot();
        EnsureNoReparsePointIfPresent(workRoot);
        if (!LongPathFileSystem.DirectoryExists(workRoot))
        {
            return;
        }

        EnsureNoReparsePointTree(workRoot);
        if (!RecoverIncompleteTransaction(workRoot))
        {
            return;
        }
        UpdateFailureReceiptException failureReceipt = ReadFailureReceipt(workRoot);

        foreach (string child in LongPathFileSystem.EnumerateFileSystemEntries(workRoot))
        {
            string childName = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (LongPathFileSystem.DirectoryExists(child)
                && string.Equals(childName, "current", StringComparison.OrdinalIgnoreCase))
            {
                // The updater process is launched from this directory and may still hold
                // its payload open while the restarted application is initializing.
                // Leave the payload in place; it is overwritten on the next launch.
                continue;
            }
            if (string.Equals(childName, UpdateFailureReceiptFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(childName, UpdateFailureReceiptTemporaryFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(childName, TransactionLockFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            EnsureNoReparsePointTree(child);
            if (LongPathFileSystem.DirectoryExists(child))
            {
                LongPathFileSystem.DeleteDirectory(child, recursive: true);
            }
            else if (LongPathFileSystem.FileExists(child))
            {
                LongPathFileSystem.DeleteFile(child);
            }
        }

        if (failureReceipt != null)
        {
            throw failureReceipt;
        }
    }

    private bool RecoverIncompleteTransaction(string workRoot)
    {
        string journalPath = Path.Combine(workRoot, TransactionJournalFileName);
        EnsureNoReparsePointIfPresent(journalPath);
        if (!LongPathFileSystem.FileExists(journalPath))
        {
            return true;
        }

        string currentUpdaterPath = Path.Combine(workRoot, "current", UpdaterExecutableFileName);
        EnsureNoReparsePointIfPresent(currentUpdaterPath);
        string updaterPath = currentUpdaterPath;
        if (!LongPathFileSystem.FileExists(updaterPath))
        {
            updaterPath = Path.Combine(applicationPathSnapshot.BaseDirectory, UpdaterExecutableFileName);
            EnsureNoReparsePointIfPresent(updaterPath);
        }
        if (!LongPathFileSystem.FileExists(updaterPath))
        {
            throw new FileNotFoundException(
                "An updater executable is required to recover an incomplete update transaction.",
                updaterPath);
        }

        return updaterProcessGateway.RecoverIncompleteTransaction(
            updaterPath,
            applicationPathSnapshot.BaseDirectory);
    }

    private UpdateFailureReceiptException ReadFailureReceipt(string workRoot)
    {
        string receiptPath = Path.Combine(workRoot, UpdateFailureReceiptFileName);
        if (!LongPathFileSystem.FileExists(receiptPath))
        {
            receiptPath = Path.Combine(workRoot, UpdateFailureReceiptTemporaryFileName);
            if (!LongPathFileSystem.FileExists(receiptPath))
            {
                return null;
            }
        }

        EnsureNoReparsePointIfPresent(receiptPath);
        string failureDetails = Encoding.UTF8.GetString(LongPathFileSystem.ReadAllBytes(receiptPath)).Trim();
        return new UpdateFailureReceiptException(
            "The previous update failed before the application could restart. "
            + (string.IsNullOrWhiteSpace(failureDetails) ? "No failure details were recorded." : failureDetails),
            () => AcknowledgeFailureReceipt(workRoot));
    }

    private void AcknowledgeFailureReceipt(string workRoot)
    {
        EnsureNoReparsePointAncestors(applicationPathSnapshot.BaseDirectory, "update_work/update-failure.txt");
        EnsureNoReparsePointIfPresent(workRoot);
        foreach (string fileName in new[] { UpdateFailureReceiptFileName, UpdateFailureReceiptTemporaryFileName })
        {
            string path = Path.Combine(workRoot, fileName);
            EnsureNoReparsePointIfPresent(path);
            if (LongPathFileSystem.FileExists(path))
            {
                LongPathFileSystem.DeleteFile(path);
            }
        }
    }

    private static void EnsureNoReparsePointTree(string path)
    {
        EnsureNoReparsePointEntry(path);
        if (!LongPathFileSystem.DirectoryExists(path))
        {
            return;
        }

        foreach (string child in LongPathFileSystem.EnumerateFileSystemEntries(path))
        {
            EnsureNoReparsePointTree(child);
        }
    }

    private static void EnsureNoReparsePointAncestors(string rootDirectory, string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string[] segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string current = rootDirectory;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            EnsureNoReparsePointIfPresent(current);
        }
    }

    private static void EnsureNoReparsePointIfPresent(string path)
    {
        try
        {
            EnsureNoReparsePointEntry(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void EnsureNoReparsePointEntry(string path)
    {
        FileAttributes attributes = LongPathFileSystem.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Reparse points are not supported in the update work directory: " + path);
        }
    }

    private void EnsureDownloadedPackagePath(string packagePath)
    {
        string downloadsDirectory = Path.Combine(GetUpdateWorkRoot(), "downloads");
        string normalizedPackagePath = LongPathFileSystem.NormalizePathForStorage(packagePath);
        string normalizedDownloadsDirectory = LongPathFileSystem.TrimTrailingDirectorySeparators(
            LongPathFileSystem.NormalizePathForStorage(downloadsDirectory));
        if (string.Equals(normalizedPackagePath, normalizedDownloadsDirectory, StringComparison.OrdinalIgnoreCase)
            || !LongPathFileSystem.IsSameOrDescendantDirectoryPath(normalizedPackagePath, normalizedDownloadsDirectory))
        {
            throw new InvalidOperationException("The update package must be under update_work/downloads.");
        }

        EnsureNoReparsePointAncestors(applicationPathSnapshot.BaseDirectory, "update_work/downloads");
        EnsureNoReparsePointIfPresent(downloadsDirectory);
        if (LongPathFileSystem.DirectoryExists(downloadsDirectory))
        {
            EnsureNoReparsePointTree(downloadsDirectory);
        }
    }

    internal void TryDeleteDownloadedPackage(string packagePath, Action<Exception> warningReporter = null)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }
        try
        {
            EnsureDownloadedPackagePath(packagePath);
            EnsureNoReparsePointIfPresent(packagePath);
            if (LongPathFileSystem.FileExists(packagePath))
            {
                LongPathFileSystem.DeleteFile(packagePath);
            }
        }
        catch (Exception exception)
        {
            warningReporter?.Invoke(exception);
        }
    }
}
