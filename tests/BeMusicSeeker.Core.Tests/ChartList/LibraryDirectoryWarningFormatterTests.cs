using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class LibraryDirectoryWarningFormatterTests
{
    /// <summary>設定受理時の予約領域だけは既知の具体理由を表示し、壊れた追加JSONは汎用理由を保つ。どちらも元例外と診断理由を失わない。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Format_InvalidConfigurationShowsOnlyKnownReservedDirectoryDetail(bool reserved)
    {
        const string root = @"C:\LR2-warning-test";
        string builtin = Path.Combine(root, "LR2files", "CustomFolder");
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2RootPath = root,
            LR2CustomFolderOutputBaseDir = reserved ? builtin : Path.Combine(root, "Safe"),
            LR2CustomFolderAdditionalOutputBaseDirsSerialized = reserved ? "[]" : "{broken-json-private-diagnostic"
        };
        var service = new LibraryDirectoryPreflightService();
        LibraryDirectoryPreflightException failure = Assert.ThrowsExactly<LibraryDirectoryPreflightException>(() => LibraryDirectoryPreflightService.ValidateSettingsAcceptance([], service.CreateRequest([], [], options), options));
        Exception? original = failure.InnerException;
        string reason = failure.Reason;
        Assert.IsNotNull(original);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        Assert.AreEqual(LibraryDirectoryPreflightFailureCause.InvalidConfiguration, failure.Cause);
        string message = LibraryDirectoryWarningFormatter.Format(failure, LibraryDirectoryWarningPhase.Early);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightCauseInvalidConfiguration);
        if (reserved)
        {
            string expected = string.Format(Resources.Validation_Lr2BuiltinCustomFolderReservedFormat,
                Resources.Label_CustomFolderOutputBase, builtin, builtin);
            StringAssert.Contains(message, expected);
        }
        else { Assert.IsFalse(message.Contains(original.Message, StringComparison.Ordinal)); }
        Assert.AreSame(original, failure.InnerException);
        Assert.AreEqual(reason, failure.Reason);
    }

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

    /// <summary>
    /// 保証：起動後の追加出力基点の読取り拒否は、Late書式・追加出力の役割・対象パス・拒否原因・LR2用の対処を一つの通知に含め、
    /// BMS用の対処へ誤分類しない。
    /// </summary>
    [TestMethod]
    public void Format_LateAdditionalOutputFailureUsesLr2RoleCauseAndGuidance()
    {
        const string outputPath = @"C:\playlist-output";
        var failure = new LibraryDirectoryPreflightException(
            LibraryDirectoryPreflightUse.Lr2OutputBase,
            outputPath,
            LibraryDirectoryPreflightFailureCause.AccessDenied,
            "read_failed",
            LibraryDirectoryPreflightOutputBaseKind.Additional,
            innerException: new UnauthorizedAccessException("read"));

        string message = LibraryDirectoryWarningFormatter.Format(
            failure,
            LibraryDirectoryWarningPhase.Late);

        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightLateWarningFormat[..Resources.LibraryDirectoryPreflightLateWarningFormat.IndexOf("{0}", StringComparison.Ordinal)]);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightLr2AdditionalOutputRole);
        StringAssert.Contains(message, outputPath);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightCauseAccessDenied);
        StringAssert.Contains(message, Resources.LibraryDirectoryPreflightLr2Guidance);
        Assert.IsFalse(message.Contains(Resources.LibraryDirectoryPreflightBmsGuidance, StringComparison.Ordinal));
    }

}
