using System.IO;
using System.Runtime.ExceptionServices;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateDownloadServiceTests
{
    /// <summary>実配布updaterをcurrentへ準備し、実exe/CWDとprotocol 1応答・正常終端を確認します。</summary>
    [TestMethod]
    [TestCategory("ReleaseAcceptance")]
    public async Task PreparedUpdaterPayloadStartsFromCurrentDirectory()
    {
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(180));
        // 共通10秒上限の内側で、元の公開受入の回収5秒も一度だけ確定します。
        var cleanupDeadline = new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)).LimitRemaining(TimeSpan.FromSeconds(5)));
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string packagePath = Path.Combine(root, "update_work", "downloads", "app.zip");
        var processes = new List<OwnedProcess>();
        var cleanupFailures = new List<Exception>();
        Exception? primaryFailure = null;
        try
        {
            DistributionCatalog catalog = await DistributionCatalog.ReadFromEnvironmentAsync(UpdateDeploymentTestSupport.FindRepositoryRoot(), deadline);
            Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
            File.WriteAllText(applicationPath, string.Empty);
            File.WriteAllText(packagePath, "package");
            string sourcePath = Path.Combine(catalog.Current.UpdaterPublishRoot ?? throw new InvalidDataException("Current updater rootがありません。"), "BeMusicSeeker.Updater.exe");
            File.Copy(sourcePath, Path.Combine(root, "BeMusicSeeker.Updater.exe"));

            var gateway = new RecordingUpdaterProcessGateway();
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                gateway,
                []);
            service.PrepareUpdaterLaunch(packagePath);
            string expectedWorkingDirectory = Path.Combine(root, "update_work", "current");
            string expectedExecutablePath = Path.Combine(expectedWorkingDirectory, "BeMusicSeeker.Updater.exe");
            Assert.AreEqual(expectedExecutablePath, gateway.Request.ExecutablePath);
            Assert.AreEqual(expectedWorkingDirectory, gateway.Request.WorkingDirectory);
            DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
            var process = OwnedProcess.Start(new ProcessStartRequest(expectedExecutablePath, ["--version"], expectedWorkingDirectory), () => cleanupDeadline.Value);
            processes.Add(process);
            ProcessCompletion completion = await process.WaitForExitAsync(deadline.LimitRemaining(TimeSpan.FromSeconds(30)));
            Assert.AreEqual(0, completion.ExitCode, completion.StandardError);
            StringAssert.Contains(completion.StandardOutput, "1");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (exception is OwnedProcessStartException { CleanupFailure: not null } failed) { cleanupFailures.Add(failed.CleanupFailure); }
        }
        finally
        {
            foreach (OwnedProcess owned in processes.AsEnumerable().Reverse())
            {
                try
                {
                    ProcessStopResult stopped = await owned.StopAsync(null, cleanupDeadline.Value);
                    if (stopped.CleanupFailure is not null) { cleanupFailures.Add(stopped.CleanupFailure); }
                    await owned.DisposeAsync();
                }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            if (primaryFailure is not null || cleanupFailures.Count > 0) { workspace.PreserveArtifacts(); }
            try { workspace.Dispose(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count > 0) { primaryFailure.Data["UpdaterCleanupSecondaryDiagnostics"] = new AggregateException(cleanupFailures); }
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (cleanupFailures.Count > 0) { throw new AggregateException(cleanupFailures); }
    }
}
