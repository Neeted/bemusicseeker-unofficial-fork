using System;
using System.Diagnostics;
using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterProcessGatewayTests
{
    [TestMethod]
    public void WindowsGatewayMapsTypedRequestToUpdaterProcessStartInfo()
    {
        ProcessStartInfo captured = null!;
        using Process fakeProcess = new();
        var gateway = new WindowsUpdaterProcessGateway(startInfo =>
        {
            captured = startInfo;
            return fakeProcess;
        });
        string applicationDirectory = @"C:\Be Music Seeker";
        string packagePath = @"C:\downloads\app package.zip";
        string backupDirectory = @"C:\Be Music Seeker\update_backup";
        string restartExecutablePath = @"C:\Be Music Seeker\BeMusicSeeker.exe";
        UpdaterProcessLaunchRequest request = UpdaterProcessLaunchRequest.Create(
            @"C:\Be Music Seeker\update_work\BeMusicSeeker.Updater.exe",
            @"C:\Be Music Seeker\update_work",
            applicationDirectory,
            packagePath,
            backupDirectory,
            restartExecutablePath);

        UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();

        Assert.IsNotNull(receipt);
        Assert.AreEqual(request.ExecutablePath, captured.FileName);
        Assert.AreEqual(request.WorkingDirectory, captured.WorkingDirectory);
        Assert.IsFalse(captured.UseShellExecute);
        int processId = Process.GetCurrentProcess().Id;
        Assert.AreEqual(
            "\"--app-dir\" \"" + applicationDirectory + "\" "
                + "\"--package\" \"" + packagePath + "\" "
                + "\"--backup-dir\" \"" + backupDirectory + "\" "
                + "\"--pid\" \"" + processId + "\" "
                + "\"--restart-exe\" \"" + restartExecutablePath + "\"",
            captured.Arguments);
    }
}
