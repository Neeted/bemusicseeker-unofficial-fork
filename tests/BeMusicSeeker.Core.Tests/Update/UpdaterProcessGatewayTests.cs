using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Components.Update;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterProcessGatewayTests
{
    /// <summary>
    /// 製品が使うGatewayは、受付10秒・停止10秒・回復2分の既定の期限で構築される。
    /// 期限切れの経路を短い注入値で検査するWindowsのケースと分担し、製品の構築や既定値が変わる誤りを実時間を待たずに検出する。
    /// </summary>
    [TestMethod]
    public void ProductGatewayUsesDefaultDeadlines()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), WindowsUpdaterProcessGateway.DefaultReadyTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(10), WindowsUpdaterProcessGateway.DefaultTerminationTimeout);
        Assert.AreEqual(TimeSpan.FromMinutes(2), WindowsUpdaterProcessGateway.DefaultRecoveryTimeout);
        var product = (WindowsUpdaterProcessGateway)UpdaterProcessGatewayPolicy.Current;
        Assert.AreEqual(WindowsUpdaterProcessGateway.DefaultReadyTimeout, product.ReadyTimeout);
        Assert.AreEqual(WindowsUpdaterProcessGateway.DefaultTerminationTimeout, product.TerminationTimeout);
        Assert.AreEqual(WindowsUpdaterProcessGateway.DefaultRecoveryTimeout, product.RecoveryTimeout);
    }

    /// <summary>
    /// 異文化でも空白付きパスと固定整数のPIDを引数ごとの値で渡し、再起動引数を1つのJSONトークンで渡す。
    /// 作業ディレクトリ・非表示起動と引数の分割を保ち、引用の形ではなく渡した値の欠落・順序違いを検出する。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("ja-JP")]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    [DataRow("ar-SA")]
    public void WindowsGatewayMapsTypedRequestToUpdaterProcessStartInfo(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            ProcessStartInfo? captured = null;
            using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string root = workspace.RootDirectory;
            string readyFilePath = Path.Combine(root, "updater-ready.txt");
            string decisionFilePath = Path.Combine(root, "updater-decision.txt");
            Directory.CreateDirectory(root);
            using Process fakeProcess = new();
            var gateway = new WindowsUpdaterProcessGateway(startInfo =>
            {
                captured = startInfo;
                File.WriteAllText(readyFilePath, "1");
                return fakeProcess;
            });
            string applicationDirectory = @"C:\Be Music Seeker";
            string packagePath = @"C:\downloads\app package.zip";
            string backupDirectory = @"C:\Be Music Seeker\update_backup";
            string restartExecutablePath = @"C:\Be Music Seeker\BeMusicSeeker.exe";
            var request = UpdaterProcessLaunchRequest.Create(
                @"C:\Be Music Seeker\update_work\BeMusicSeeker.Updater.exe",
                @"C:\Be Music Seeker\update_work",
                applicationDirectory,
                packagePath,
                backupDirectory,
                readyFilePath,
                decisionFilePath,
                restartExecutablePath,
                UpdaterPackageTestSupport.HardRestartArguments);

            {
                UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();

                Assert.IsNotNull(receipt);
                Assert.IsNotNull(captured);
                ProcessStartInfo capturedStartInfo = captured;
                Assert.AreEqual(request.ExecutablePath, capturedStartInfo.FileName);
                Assert.AreEqual(request.WorkingDirectory, capturedStartInfo.WorkingDirectory);
                Assert.IsFalse(capturedStartInfo.UseShellExecute);
                Assert.IsTrue(capturedStartInfo.CreateNoWindow);
                Assert.AreEqual(string.Empty, capturedStartInfo.Arguments);
                int processId = Process.GetCurrentProcess().Id;
                string[] arguments = capturedStartInfo.ArgumentList.ToArray();
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "--app-dir", applicationDirectory,
                        "--package", packagePath,
                        "--backup-dir", backupDirectory,
                        "--ready-file", readyFilePath,
                        "--decision-file", decisionFilePath,
                        "--pid", processId.ToString(CultureInfo.InvariantCulture),
                        "--restart-exe", restartExecutablePath
                    },
                    RemoveRestartArgumentsOption(arguments, out string? restartArgumentsJson));
                // オプションの位置は問わず、トークンが1つであることとJSONの値の一致だけを判定する。
                // 空でない列では省略できないため、オプションの存在も判定する。
                Assert.IsNotNull(restartArgumentsJson);
                CollectionAssert.AreEqual(
                    UpdaterPackageTestSupport.HardRestartArguments,
                    JsonSerializer.Deserialize<string[]>(restartArgumentsJson));
            }

        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// 再起動引数が空でも更新ツールへ空配列として渡し、引数なしの再起動を欠落や別の値と取り違える誤りを検出する。
    /// </summary>
    [TestMethod]
    public void WindowsGatewayPassesEmptyRestartArgumentsAsEmptyList()
    {
        ProcessStartInfo? captured = null;
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        Directory.CreateDirectory(root);
        string readyFilePath = Path.Combine(root, "updater-ready.txt");
        using Process fakeProcess = new();
        var gateway = new WindowsUpdaterProcessGateway(startInfo =>
        {
            captured = startInfo;
            File.WriteAllText(readyFilePath, "1");
            return fakeProcess;
        });
        var request = UpdaterProcessLaunchRequest.Create(
            @"C:\Be Music Seeker\update_work\BeMusicSeeker.Updater.exe",
            @"C:\Be Music Seeker\update_work",
            @"C:\Be Music Seeker",
            @"C:\downloads\app.zip",
            @"C:\Be Music Seeker\update_backup",
            readyFilePath,
            Path.Combine(root, "updater-decision.txt"),
            @"C:\Be Music Seeker\BeMusicSeeker.exe",
            []);

        Assert.IsNotNull(gateway.Prepare(request).Start());

        Assert.IsNotNull(captured);
        RemoveRestartArgumentsOption(captured.ArgumentList.ToArray(), out string? restartArgumentsJson);
        // 空列は`[]`でも省略でもよい。渡す場合は空配列であること。
        if (restartArgumentsJson is not null)
        {
            CollectionAssert.AreEqual(Array.Empty<string>(), JsonSerializer.Deserialize<string[]>(restartArgumentsJson));
        }
    }

    /// <summary>`--restart-arguments-json`が高々1つであることを確認し、その値を取り出して残りの引数を返します。</summary>
    private static string[] RemoveRestartArgumentsOption(string[] arguments, out string? value)
    {
        int index = Array.IndexOf(arguments, "--restart-arguments-json");
        Assert.AreEqual(Array.LastIndexOf(arguments, "--restart-arguments-json"), index, "オプションは1つだけです。");
        if (index < 0)
        {
            value = null;
            return arguments;
        }
        Assert.IsTrue(index + 1 < arguments.Length, "オプションの値が1トークンありません。");
        value = arguments[index + 1];
        return [.. arguments.Take(index), .. arguments.Skip(index + 2)];
    }

    /// <summary>
    /// 復旧起動を空白入りのディレクトリでも独立した引数として渡し、引用文字列の組立てで値が崩れる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void WindowsGatewayRecoveryPassesRecoverCommandTokens()
    {
        ProcessStartInfo? captured = null;
        var gateway = new WindowsUpdaterProcessGateway(startInfo =>
        {
            captured = startInfo;
            throw new InvalidOperationException("sentinel");
        });
        string applicationDirectory = @"C:\Be Music Seeker";

        Assert.ThrowsExactly<InvalidOperationException>(
            () => gateway.RecoverIncompleteTransaction(@"C:\Be Music Seeker\update_work\BeMusicSeeker.Updater.exe", applicationDirectory));

        Assert.IsNotNull(captured);
        Assert.IsFalse(captured.UseShellExecute);
        Assert.AreEqual(string.Empty, captured.Arguments);
        CollectionAssert.AreEqual(new[] { "--recover", "--app-dir", applicationDirectory }, captured.ArgumentList.ToArray());
    }

    /// <summary>
    /// プロセス起動の元失敗を受付handshake失敗へ包んで伝え、正常receiptへ読み替えたり元原因を失う誤りを検出する。
    /// </summary>
    [TestMethod]
    public void WindowsGatewayNormalizesProcessStartFailure()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        Directory.CreateDirectory(root);
        string readyFilePath = Path.Combine(root, "updater-ready.txt");
        string decisionFilePath = Path.Combine(root, "updater-decision.txt");
        var gateway = new WindowsUpdaterProcessGateway(_ => throw new InvalidOperationException("start failed"));
        var request = UpdaterProcessLaunchRequest.Create(
            @"C:\Be Music Seeker\update_work\BeMusicSeeker.Updater.exe",
            @"C:\Be Music Seeker\update_work",
            @"C:\Be Music Seeker",
            @"C:\downloads\app.zip",
            @"C:\Be Music Seeker\update_backup",
            readyFilePath,
            decisionFilePath,
            @"C:\Be Music Seeker\BeMusicSeeker.exe",
            []);

        {
            UpdaterLaunchFailureException exception = Assert.ThrowsExactly<UpdaterLaunchFailureException>(
                () => gateway.Prepare(request).Start());
            StringAssert.Contains(exception.Message, "ready handshake");
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
        }

    }

    /// <summary>
    /// 取消callbackの最初の失敗後も再試行を受理し、二度目の取消まで受付状態を消費してしまう誤りを検出する。
    /// </summary>
    [TestMethod]
    public void UpdaterLaunchReceiptRetriesAbortAfterAnAbortActionFailure()
    {
        int abortAttempts = 0;
        var receipt = new UpdaterLaunchReceipt(() =>
        {
            if (Interlocked.Increment(ref abortAttempts) == 1)
            {
                throw new IOException("cancel publication failed");
            }
        });

        Assert.ThrowsExactly<IOException>(receipt.Abort);
        receipt.Abort();

        Assert.AreEqual(2, abortAttempts);
    }

    /// <summary>
    /// 続行callbackが保持される間に別workerの取消が完了でき、外部callback中も決定guardを握って競合を止める誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task UpdaterLaunchReceipt_DoesNotHoldDecisionGuardAcrossProceedCallback()
    {
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        var receipt = new UpdaterLaunchReceipt(
            abort: () => { },
            proceed: () =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait();
            });
        Task proceedTask = ConcurrencyProbe.StartDedicated(receipt.Proceed);
        Task? competingAbort = null;
        Exception? primaryFailure = null;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(callbackEntered.Task, proceedTask, "updater-receipt.proceed-callback");
            Assert.IsFalse(proceedTask.IsCompleted, "Proceed must still be held inside its callback.");
            competingAbort = ConcurrencyProbe.StartDedicated(() =>
            {
                abortAttempted.TrySetResult();
                receipt.Abort();
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(abortAttempted.Task, competingAbort, "updater-receipt.abort-attempt");
            Assert.IsFalse(proceedTask.IsCompleted, "Proceed must remain held before the competing decision.");
            // callback保持中の排他不成立だけを停止検出の期限で観測する。保持側(proceedTask)は判定まで保持される。
            await ConcurrencyProbe.AssertCompletesWhileHeldAsync(
                competingAbort,
                "A competing decision must complete while the external callback remains held.",
                proceedTask);
            await competingAbort;
            Assert.IsFalse(proceedTask.IsCompleted, "A completed abort must not end or fault the held proceed worker.");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            releaseCallback.Set();
            try
            {
                await Task.WhenAll(competingAbort is null ? new[] { proceedTask } : new[] { proceedTask, competingAbort });
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString();
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }
}
