using System;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalProgramLaunchGatewayTests
{
    /// <summary>実行先を基準作業先で起動し、空・空白入りpath・日本語の各引数をArgumentListで分割せず渡します。</summary>
    [TestMethod]
    public void Launch_UsesAbsoluteExecutableWorkingDirectoryAndExactArgumentTokens()
    {
        ProcessStartInfo? captured = null;
        WindowsExternalProgramLaunchGateway gateway = new(
            fileExists: _ => true,
            starter: startInfo =>
            {
                captured = startInfo;
                return new Process();
            });

        ExternalProgramLaunchResult result = gateway.Launch(new ExternalProgramLaunchRequest(
            "player",
            @"C:\Tools\Player\player.exe",
            @"C:\Songs\folder name\chart.bms",
            ["--chart", @"C:\Songs\folder name\chart.bms", "", "日本語"]));

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(captured);
        ProcessStartInfo capturedStartInfo = captured!;
        Assert.AreEqual(@"C:\Tools\Player\player.exe", capturedStartInfo.FileName);
        Assert.IsFalse(capturedStartInfo.UseShellExecute);
        Assert.AreEqual(@"C:\Tools\Player", capturedStartInfo.WorkingDirectory);
        CollectionAssert.AreEqual(
            new[] { "--chart", @"C:\Songs\folder name\chart.bms", "", "日本語" },
            capturedStartInfo.ArgumentList.ToArray());
    }

    /// <summary>実行ファイルと譜面の欠落を別原因で返し、どちらもprocess starterを呼びません。</summary>
    [TestMethod]
    public void Launch_MissingExecutableOrChartDoesNotInvokeStarter()
    {
        int startCalls = 0;
        WindowsExternalProgramLaunchGateway gateway = new(
            fileExists: path => path.EndsWith("chart.bms", StringComparison.OrdinalIgnoreCase),
            starter: _ =>
            {
                startCalls++;
                return new Process();
            });

        ExternalProgramLaunchResult missingExecutable = gateway.Launch(new ExternalProgramLaunchRequest(
            "player",
            @"C:\Tools\Player\player.exe",
            @"C:\Songs\chart.bms",
            []));
        ExternalProgramLaunchResult missingChart = new WindowsExternalProgramLaunchGateway(
            fileExists: path => path.EndsWith("player.exe", StringComparison.OrdinalIgnoreCase),
            starter: _ =>
            {
                startCalls++;
                return new Process();
            }).Launch(new ExternalProgramLaunchRequest(
                "player",
                @"C:\Tools\Player\player.exe",
                @"C:\Songs\chart.bms",
                []));

        Assert.AreEqual(ExternalProgramLaunchFailureKind.MissingExecutable, missingExecutable.FailureKind);
        Assert.AreEqual(ExternalProgramLaunchFailureKind.MissingChart, missingChart.FailureKind);
        Assert.AreEqual(0, startCalls);
    }

    /// <summary>相対実行pathは拒否し、起動例外は元の型を持つStartFailedとして返して正常終了へ隠しません。</summary>
    [TestMethod]
    public void Launch_RejectsRelativeExecutableAndReturnsStarterFailure()
    {
        WindowsExternalProgramLaunchGateway invalidPathGateway = new(
            fileExists: _ => true,
            starter: _ => new Process());
        ExternalProgramLaunchResult invalidPath = invalidPathGateway.Launch(new ExternalProgramLaunchRequest(
            "player",
            "player.exe",
            @"C:\Songs\chart.bms",
            []));

        WindowsExternalProgramLaunchGateway throwingGateway = new(
            fileExists: _ => true,
            starter: _ => throw new InvalidOperationException("start failed"));
        ExternalProgramLaunchResult thrown = throwingGateway.Launch(new ExternalProgramLaunchRequest(
            "player",
            @"C:\Tools\player.exe",
            @"C:\Songs\chart.bms",
            []));

        Assert.AreEqual(ExternalProgramLaunchFailureKind.InvalidExecutablePath, invalidPath.FailureKind);
        Assert.AreEqual(ExternalProgramLaunchFailureKind.StartFailed, thrown.FailureKind);
        Assert.IsInstanceOfType(thrown.Exception, typeof(InvalidOperationException));
    }

    /// <summary>starterがprocessを返さない場合はNullProcessで失敗し、存在しないprocessを成功扱いや待機対象にしません。</summary>
    [TestMethod]
    public void Launch_NullProcessIsAnExplicitFailureAndDoesNotWait()
    {
        WindowsExternalProgramLaunchGateway gateway = new(
            fileExists: _ => true,
            starter: _ => null);

        ExternalProgramLaunchResult result = gateway.Launch(new ExternalProgramLaunchRequest(
            "player",
            @"C:\Tools\player.exe",
            @"C:\Songs\chart.bms",
            []));

        Assert.AreEqual(ExternalProgramLaunchFailureKind.NullProcess, result.FailureKind);
        Assert.IsFalse(result.Succeeded);
    }
}
