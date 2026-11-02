using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryPendingLegacyMutationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("Suite.DirectorySymbolicLink")]
public sealed class BmsLibraryPendingLegacyMutationTests
{
    /// <summary>実symlinkの選択譜面を拒否し、外部のリンク先を残してファイルや親ディレクトリの削除へ進まない。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void DeletePendingCharts_ReparseSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "target");
            Directory.CreateDirectory(packageDirectoryPath);
            string targetPath = Path.Combine(tempDirectoryPath, "external.bms");
            File.WriteAllText(targetPath, "#PLAYER 1\r\n");
            string linkPath = Path.Combine(packageDirectoryPath, "linked.bms");
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is PlatformNotSupportedException)
            {
                Assert.Fail("実ファイルsymlinkを作成できない環境のため、専用環境の準備が不足しています: " + exception.Message);
                return;
            }
            ChartFile chartFile = ChartTestValues.Empty() with
            {
                Path = linkPath
            };
            chartFile = chartFile with { Md5 = new string('a', 32) };
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chartFile]);
            package.path = chartFile.Path;
            var fileMutationService = new RecordingDeleteFileMutationService();
            var service = new BmsLibraryPackageInstallService();

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [(chartFile)],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.Removed);
            Assert.AreEqual(1, result.Failed);
            Assert.IsInstanceOfType(result.Failures[0].Exception, typeof(IOException));
            Assert.AreEqual(0, fileMutationService.DeleteFileShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.IsTrue(File.Exists(targetPath));
        });
    }

    /// <summary>拡張子変更で実symlinkを拒否し、移動・重複削除を行わず外部のリンク先を保持する。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void InvalidExtensionPlan_ReparseSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string targetPath = Path.Combine(tempDirectoryPath, "external.bms");
            File.WriteAllText(targetPath, "#PLAYER 1\r\n");
            string sourcePath = Path.Combine(tempDirectoryPath, "linked.bms");
            try
            {
                File.CreateSymbolicLink(sourcePath, targetPath);
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is PlatformNotSupportedException)
            {
                Assert.Fail("実ファイルsymlinkを作成できない環境のため、専用環境の準備が不足しています: " + exception.Message);
                return;
            }
            ChartFile sourceFile = ChartTestValues.Empty() with
            {
                Path = sourcePath
            };
            sourceFile = sourceFile with { Md5 = new string('a', 32) };
            var service = new BmsLibraryLibraryFileOperationsService();
            var snapshot = LibraryFileOperationTargetSnapshot.FromChart(
                (sourceFile),
                captureSourceFileExistence: true);
            LegacyInvalidExtensionRenamePlan plan = service.BuildInvalidExtensionRenamePlan(
                [snapshot],
                ".bmx",
                rejectUnsafeSource: true);
            var fileMutationService = new RecordingDeleteFileMutationService();

            LegacyInvalidExtensionRenameExecutionResult result = service.ExecuteInvalidExtensionRenamePlan(
                plan,
                fileMutationService,
                null);

            Assert.AreEqual(1, result.SkippedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.RenamedCount);
            Assert.AreEqual(0, result.DuplicateDeletedCount);
            Assert.AreEqual(0, fileMutationService.DeleteFileDirectPaths.Count);
            Assert.AreEqual(0, fileMutationService.MoveFilePaths.Count);
            Assert.IsInstanceOfType(result.Items[0].Outcome.FailureException, typeof(IOException));
            Assert.IsTrue(File.Exists(targetPath));
        });
    }

}
