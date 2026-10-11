using System;
using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryDirectoryWarningFormatterTests
{
    /// <summary>起動前のBMS root欠損は対象パス・欠損原因・BMS用の対処を示し、LR2出力用の対処へ誤分類しません。</summary>
    [TestMethod]
    public void Format_EarlyBmsFailureIncludesRolePathCauseAndBmsGuidance()
    {
        const string missingPath = @"C:\library\missing";
        var failure = new LibraryDirectoryPreflightException(
            LibraryDirectoryPreflightUse.BmsRoot,
            missingPath,
            LibraryDirectoryPreflightFailureCause.NotFound,
            "not_found");

        string message = LibraryDirectoryWarningFormatter.Format(
            failure,
            LibraryDirectoryWarningPhase.Early);

        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightEarlyWarningFormat[..Resources.LibraryDirectoryPreflightEarlyWarningFormat.IndexOf("{0}", StringComparison.Ordinal)]);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightBmsRootRole);
        StringAssert.Contains(message, missingPath);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightCauseNotFound);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightBmsGuidance);
        Assert.IsFalse(message.Contains(Resources.LibraryDirectoryPreflightLr2Guidance, StringComparison.Ordinal));
    }

    /// <summary>起動後の追加出力先書込み失敗は対象パスと追加出力の役割、後片付け失敗とplaylist用の対処を同じ通知へ保持します。</summary>
    [TestMethod]
    public void Format_LateAdditionalOutputFailureUsesPlaylistGuidanceAndCleanupCause()
    {
        const string outputPath = @"C:\playlist-output";
        var cleanupFailure = new IOException("cleanup");
        var failure = new LibraryDirectoryPreflightException(
            LibraryDirectoryPreflightUse.Lr2OutputBase,
            outputPath,
            LibraryDirectoryPreflightFailureCause.Write,
            "write_failed;cleanup_failed",
            LibraryDirectoryPreflightOutputBaseKind.Additional,
            probePath: Path.Combine(outputPath, ".bemusicseeker-probe.tmp"),
            innerException: new IOException("write"),
            cleanupException: cleanupFailure);

        string message = LibraryDirectoryWarningFormatter.Format(
            failure,
            LibraryDirectoryWarningPhase.Late);

        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightLateWarningFormat[..Resources.LibraryDirectoryPreflightLateWarningFormat.IndexOf("{0}", StringComparison.Ordinal)]);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightLr2AdditionalOutputRole);
        StringAssert.Contains(message, outputPath);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightCauseWrite);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightCleanupFailure);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightLr2Guidance);
    }
}
