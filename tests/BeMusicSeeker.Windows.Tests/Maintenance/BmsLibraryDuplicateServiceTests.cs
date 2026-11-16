using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

/// <summary>実AppComposition・VM投影の通知完了まで、重複解析の受付を保持することを確認します。</summary>
/// <remarks>
/// 重複統合の実DB・ファイルシステム、ロールバック、結果記録は Core の同名クラスが headless scheduler で確認します。
/// このモジュールには、実AppComposition と ViewModel への投影を必要とする確認だけを置きます。
/// </remarks>
[TestClass]
public sealed class BmsLibraryDuplicateServiceTests
{
    /// <summary>
    /// 実解析の確定通知を保留した間は共通Lを保持し、利用者の明示的な実移動要求を未開始のまま受付使用中（Busy、失敗なし）として拒み、受付使用中の警告（本文・題名・Exclamation・OK）を一度だけ表示します。
    /// 汎用の失敗（Msg_error_unexpected）への置換を出さず、通知完了後は解放され、元の譜面とDBを保ちます。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void DuplicateAdmission_RealAnalysisRejectsLibraryMoveUntilRequiredNotificationsComplete()
    {
        var dialogs = new FileDbReportRecordingDialogs();
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
            Assert.IsTrue(move.Result.Busy, "解析中の明示的な移動要求は受付使用中として拒みます。");
            Assert.IsNull(move.Result.Failure, "受付使用中を汎用の失敗にしません。");
            BeMusicSeeker.Views.Dialogs.UiMessageRequest warning = dialogs.Messages.Single();
            Assert.AreEqual(Resources.Warn_LibraryOperationBusy, warning.MessageBoxText);
            Assert.AreEqual(Resources.Warning, warning.Caption);
            Assert.AreEqual(System.Windows.MessageBoxImage.Exclamation, warning.Icon);
            Assert.AreEqual(System.Windows.MessageBoxButton.OK, warning.Button);
            Assert.IsFalse(dialogs.Messages.Any(message => message.MessageBoxText == Resources.Msg_error_unexpected));
            Assert.IsFalse(move.Result.HasDurableCommit);
            Assert.IsTrue(File.Exists(chart.Path));
            Assert.IsFalse(Directory.Exists(destination));
            fixture.CompleteDuplicate();
            Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
            Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable next));
            next.Dispose();
            Assert.IsTrue(File.Exists(chart.Path));
            Assert.IsTrue(fixture.Library.BmsCharts.Any(current => current.Path == chart.Path));
            Assert.AreEqual(1, dialogs.Messages.Count, "解析の完了と解放で追加の案内を出しません。");
        }, dialogs: dialogs);
    }
}
