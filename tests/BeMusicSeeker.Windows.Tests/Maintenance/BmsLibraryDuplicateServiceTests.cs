using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

/// <summary>実AppComposition・VM投影の通知完了まで、重複解析の受付を保持することを確認します。</summary>
[TestClass]
public sealed class BmsLibraryDuplicateServiceTests
{
    /// <summary>実解析の確定通知を保留した間は共通Lを保持し、実移動要求を未開始で拒みます。通知完了後は解放され、元の譜面とDBを保ちます。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void DuplicateAdmission_RealAnalysisRejectsLibraryMoveUntilRequiredNotificationsComplete()
    {
        RegularChartNavigationTestFixture.RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            fixture.StartDuplicate(); fixture.AwaitGate(); fixture.AwaitGroupsWhileGated();
            Assert.IsTrue(fixture.Library.OperationAdmission.IsActive);
            ChartFile chart = fixture.Library.BmsCharts.Single(current => current.Path == fixture.Duplicates[0].Path);
            string destination = Path.Combine(Path.GetDirectoryName(chart.Directory) ?? throw new AssertFailedException(), "rejected-move");
            var target = new ChartOperationTarget(chart, null, ChartOperationSourceScope.Library, true, false, false, ChartOperationCapabilities.MoveInLibrary);
            Task<SelectedChartMutationResult> move = vm.SelectedChartMutations.MoveAsync(new SelectedChartMoveRequest([target], destination));
            fixture.AwaitOwned(move, "move rejected during duplicate analysis");
            Assert.IsNotNull(move.Result.Failure);
            Assert.IsFalse(move.Result.HasDurableCommit);
            Assert.IsTrue(File.Exists(chart.Path));
            Assert.IsFalse(Directory.Exists(destination));
            fixture.CompleteDuplicate();
            Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
            Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable next));
            next.Dispose();
            Assert.IsTrue(File.Exists(chart.Path));
            Assert.IsTrue(fixture.Library.BmsCharts.Any(current => current.Path == chart.Path));
        });
    }
}
