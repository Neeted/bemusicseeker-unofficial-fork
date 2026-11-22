using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using BeMusicSeeker.Views.Settings;
using static BeMusicSeeker.Tests.SettingsWindowTestFixture;
using static BeMusicSeeker.TestSupport.Components.SettingsMediaTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>設定画面の不備の案内欄・ページの印・欄の理由・行の操作と、LR2バックアップの有効化と出力先の組の保存を実Windowで検証します。</summary>
/// <remarks>一覧の生成条件はVM境界の <see cref="SettingsDeficiencyListTests"/> が分担し、ここではcompiled XAMLの接続と画面の操作だけを確かめます。共有 Dispatcher と表示を使うため非並列にします。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class SettingsWindowDeficiencyGuidanceTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>
    /// 初回・通常の設定画面で、一覧が空でなければ見出しと一覧順の行を出し、不備のあるページだけに支援技術が読める印を付け、
    /// 欄の直下に理由を一度だけ出し、初回の開始ボタンを無効にすることと、最小寸法でも下部操作がウィンドウ内に収まることを確認します。
    /// 確定値を直すと案内欄・印・理由が消えることも確認します（K6）。
    /// </summary>
    /// <remarks>案内欄と一覧の不一致、印の付け漏れ・付け過ぎ、オートメーションから読めない印、導入先の既存検証表示との二重表示、案内欄による下部操作の押し出しを検出します。</remarks>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SettingsWindow_GuidanceRowsMarksAndFieldReasonsFollowDeficiencyListInInitialAndNormalSettings(bool initial)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            Settings values = CreateValidCustomFolderSettings(Path.Combine(root, "lr2"), [], TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")));
            string validInstall = values.BMSInstallDir;
            values.BMSInstallDir = Path.Combine(root, "missing-install");
            values.UseExternalPanelImage = true;
            values.StagefilePath = Path.Combine(root, "missing.png");
            values.Save();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            var failures = new List<Exception>();
            var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: !initial, reportApplyFailure: failures.Add, dialogs: dialogs);
            SettingsWindow? window = null;
            try
            {
                SettingsDialogViewModel dialog = harness.Dialog;
                window = new SettingsWindow(dialogs) { DataContext = dialog, Width = 820, Height = 600 };
                scope.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(initial, dialog.IsInitialEnvironment);
                var guidance = (FrameworkElement)window.FindName("settingsDeficiencyGuidance");
                Assert.AreEqual(Visibility.Visible, guidance.Visibility);
                Assert.IsTrue(FindDescendants<TextBlock>(guidance).Any(text => text.Text == Resources.Settings_deficiency_guidance_title && text.IsVisible));
                Assert.AreEqual(Resources.Settings_deficiency_guidance_title, AutomationProperties.GetName(guidance));
                string[] rows = GuidanceRows(guidance).Select(button => AutomationProperties.GetName(button)).ToArray();
                CollectionAssert.AreEqual(new[]
                {
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, Resources.SettingValidation_SectionMessageFormat, Resources.Appearance, Resources.Error_InvalidStagefilePath),
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, Resources.SettingValidation_SectionMessageFormat, Resources.Install, Resources.Error_InvalidBmsInstallDirNotRegistered)
                }, rows);
                AssertMarks(window, "navigationAppearance", "navigationInstall");

                // 縦タブの固定領域（見出し・案内・操作）は TabControl の peer に隠れず、ウィンドウから UI Automation の木で到達できる。
                string[] reachable =
                [
                    "SettingsCategoryNavigation", "SettingsCategoryGeneral", "SettingsCategoryAppearance", "SettingsCategoryPlayback", "SettingsCategoryAudio",
                    "SettingsCategoryRecording", "SettingsCategoryPlaylist", "SettingsCategoryInstall", "SettingsCategoryBackup", "SettingsCategoryAdvanced",
                    "SettingsCategoryRightClick", "SettingsCategoryAbout", "SettingsSelectedPageHeader", "SettingsSelectedPageContent",
                    "SettingsDeficiencyGuidance", "SettingsClose"
                ];
                TabNavigationAssert.AssertReachableByAutomation(window, initial ? [.. reachable, "SettingsStartEnvironment"] : reachable);

                // 欄の直下の理由は一度だけで、導入先の既存検証表示と二重にしません。
                SelectPage(window, "navigationInstall");
                SettingsField install = FindTarget<SettingsField>(window, SettingsDeficiencyField.InstallDirectory);
                Assert.AreEqual(Resources.Error_InvalidBmsInstallDirNotRegistered, install.ValidationMessage);
                SelectPage(window, "navigationAppearance");
                SettingsPathPicker image = FindDescendants<SettingsPathPicker>(CurrentPage(window)).Single(picker =>
                    picker.GetBindingExpression(SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == nameof(SettingsDialogViewModel.StagefilePath));
                Assert.AreEqual(Resources.Error_InvalidStagefilePath, image.ValidationMessage);

                var start = (Button)window.FindName("buttonOK");
                var close = (Button)window.FindName("buttonCancel");
                Assert.AreEqual(initial ? Visibility.Visible : Visibility.Collapsed, start.Visibility);
                Assert.IsFalse(start.IsEnabled);
                Assert.AreEqual(dialog.CanStartEnvironment, start.IsEnabled);
                // 下部の案内文は、初回だけ表示し、不備が残っている間は設定を促します。
                var startText = (TextBlock)window.FindName("settingsStartGuidanceText");
                Assert.AreEqual(initial, startText.IsVisible);
                if (initial) { Assert.AreEqual(Resources.Settings_start_requires_settings, startText.Text); }
                // 最小寸法でも案内欄が下部操作をウィンドウ外へ押し出しません。
                var content = (FrameworkElement)window.Content;
                Rect closeBounds = close.TransformToAncestor(content).TransformBounds(new Rect(0, 0, close.ActualWidth, close.ActualHeight));
                Assert.IsTrue(closeBounds.Bottom <= content.ActualHeight + 0.5, closeBounds + " / " + content.ActualHeight);
                Rect guidanceBounds = guidance.TransformToAncestor(content).TransformBounds(new Rect(0, 0, guidance.ActualWidth, guidance.ActualHeight));
                Assert.IsTrue(guidanceBounds.Bottom <= closeBounds.Top, guidanceBounds + " / " + closeBounds);

                // 確定値を直すと、確定操作の終端で作り直した一覧に案内欄・印・理由・開始ボタンが追従します。
                dialog.UseExternalPanelImage = false;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(validInstall), "deficiency-guidance-install");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, dialog.Deficiencies.Count, SettingsDeficiency.FormatDiagnostic(dialog.Deficiencies));
                Assert.AreEqual(Visibility.Collapsed, guidance.Visibility);
                AssertMarks(window);
                Assert.AreEqual(string.Empty, image.ValidationMessage ?? string.Empty);
                Assert.AreEqual(initial, start.IsEnabled);
                // 不備が解消すると、案内文は開始の案内へ切り替わります。
                if (initial) { Assert.AreEqual(Resources.Settings_start_ready, startText.Text); }
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                harness.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>
    /// 確定済みのLR2設定XMLを解決できない場合と解決できる場合で、一般ページのBMSディレクトリ欄の無効化と案内、プレイリストページの出力先の参照・追加・削除・改名の有効・無効と案内、
    /// 案内の操作によるLR2環境の欄への移動、インストールページのボタンによるBMSディレクトリ欄への移動（解決できる場合だけ欄の中へフォーカス）を確認します。
    /// </summary>
    /// <remarks>
    /// 解決できない状態の編集を許す誤り、案内の取り違え、解決できる場合に案内を出す誤り、移動先の取り違え、無効な欄へフォーカスを移そうとする誤りを検出します。
    /// BMSディレクトリ欄の表示範囲の判定は、ページの選択が先頭へスクロールすることを踏まえ、欄より上の詳細を展開した先頭表示で範囲外であることを確認してから行います。
    /// LR2環境の欄は一般ページの先頭付近にあり、ページの選択による先頭表示で常に表示範囲に入るため、フォーカスの移動だけを確認します。
    /// </remarks>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SettingsWindow_Lr2ConfigResolutionGuidesBmsAndOutputEditingAndRoutesToTargetFields(bool resolved)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            Settings values = CreateValidCustomFolderSettings(Path.Combine(root, "lr2"), [], TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")));
            if (!resolved) { File.WriteAllText(values.LR2ConfigXmlPath, "<config><broken>"); }
            values.Save();
            var dialogs = new RecordingRootDialogService();
            var harness = SettingsDialogHarness.Create(values, dialogs: dialogs);
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow(dialogs) { DataContext = harness.Dialog, Width = 820, Height = 600 };
                scope.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                // 一般ページ: BMSディレクトリ欄は解決できない間だけ無効で、案内を出します。
                SelectPage(window, "navigationGeneral");
                SettingsField bms = FindTarget<SettingsField>(window, SettingsDeficiencyField.BmsDirectories);
                Assert.AreEqual(resolved, bms.IsEnabled);
                if (resolved) { Assert.IsFalse((bms.ValidationMessage ?? string.Empty).Contains(Resources.Settings_bms_editor_requires_lr2_root, StringComparison.Ordinal)); }
                else { StringAssert.Contains(bms.ValidationMessage, Resources.Settings_bms_editor_requires_lr2_root); }

                // プレイリストページ: 出力先の編集操作の有効・無効と、一般ページへ案内する操作。
                SelectPage(window, "navigationPlaylist");
                FrameworkElement playlist = CurrentPage(window);
                foreach (SettingsPathPicker picker in FindDescendants<SettingsPathPicker>(playlist).Where(item => item.Name is "customFolderOutputPathPicker" or "customFolderRootOutputPathPicker"))
                {
                    Assert.AreEqual(resolved, picker.IsEnabled);
                    Assert.AreEqual(resolved, FindDescendants<Button>(picker).Single().IsEnabled);
                }
                SettingsListEditor additional = FindTarget<SettingsListEditor>(window, SettingsDeficiencyField.AdditionalOutput);
                Assert.IsTrue(FindDescendants<Button>(additional).Any());
                Assert.IsTrue(FindDescendants<Button>(additional).All(button => button.IsEnabled == resolved));
                Button? prerequisite = FindDescendants<Button>(playlist).SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "SettingsOutputRequiresLr2Root");
                Assert.AreEqual(!resolved, prerequisite is { IsVisible: true });

                // インストールページのボタンは一般ページへ移り、BMSディレクトリ欄を表示範囲へ入れます。
                // ページの選択は先頭へスクロールするため、移動直前と同じ先頭表示で欄が範囲外になるよう、欄より上の詳細を展開しておきます。
                SelectPage(window, "navigationGeneral");
                bms = FindTarget<SettingsField>(window, SettingsDeficiencyField.BmsDirectories);
                FrameworkElement generalPage = CurrentPage(window);
                double Top(FrameworkElement element) => element.TransformToAncestor(generalPage).Transform(new Point()).Y;
                Expander[] detailsAbove = FindDescendants<Expander>(generalPage).Where(item => item.IsVisible && Top(item) < Top(bms)).ToArray();
                Assert.IsTrue(detailsAbove.Length > 0, "BMSディレクトリ欄より上に詳細の折り畳み欄があります。");
                foreach (Expander details in detailsAbove) { details.IsExpanded = true; }
                ScrollViewer viewer = FindAncestor<ScrollViewer>(bms);
                viewer.ScrollToVerticalOffset(0);
                window.UpdateLayout();
                Assert.IsFalse(IsInViewport(bms, viewer), "BMSディレクトリ欄が先頭表示で見えていると、表示範囲へ入れる処理を検出できません。");
                SelectPage(window, "navigationInstall");
                Button edit = FindDescendants<Button>(CurrentPage(window)).Single(button => AutomationProperties.GetAutomationId(button) == "SettingsEditBmsDirectories");
                edit.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreSame(window.FindName("navigationGeneral"), ((TabControl)window.FindName("settingsNavigation")).SelectedItem);
                bms = FindTarget<SettingsField>(window, SettingsDeficiencyField.BmsDirectories);
                viewer = FindAncestor<ScrollViewer>(bms);
                Assert.IsTrue(IsInViewport(bms, viewer), "BMSディレクトリ欄を表示範囲へ入れます。");
                var focused = FocusManager.GetFocusedElement(FocusManager.GetFocusScope(bms)) as DependencyObject;
                Assert.AreEqual(resolved, focused != null && IsDescendantOf(focused, bms), "有効な欄だけフォーカスを受けます: " + focused);

                // 解決できない間は、案内の操作でLR2環境の欄へ移ります。
                if (!resolved)
                {
                    SelectPage(window, "navigationPlaylist");
                    prerequisite = FindDescendants<Button>(CurrentPage(window)).Single(button => AutomationProperties.GetAutomationId(button) == "SettingsOutputRequiresLr2Root");
                    prerequisite.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreSame(window.FindName("navigationGeneral"), ((TabControl)window.FindName("settingsNavigation")).SelectedItem);
                    FrameworkElement environment = FindTarget<FrameworkElement>(window, SettingsDeficiencyField.Lr2Environment);
                    var environmentFocus = FocusManager.GetFocusedElement(FocusManager.GetFocusScope(environment)) as DependencyObject;
                    Assert.IsNotNull(environmentFocus);
                    Assert.IsTrue(IsDescendantOf(environmentFocus, environment), "LR2環境の欄の最初のフォーカス可能な要素へフォーカスします: " + environmentFocus);
                }
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                harness.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>
    /// 案内の行を操作すると対象ページを選び、表示範囲外にあった対象の欄を表示範囲へ入れ、欄の最初のフォーカス可能な要素へ論理フォーカスを移すことを確認します（K6）。
    /// </summary>
    /// <remarks>
    /// ページ選択だけで欄へ移らない誤り、対象の欄の取り違え、フォーカスを移さない誤りを検出します。行は標準の <see cref="Button"/> のため、Enter・Spaceはクリックと同じ経路です（型を確認します）。
    /// </remarks>
    [TestMethod]
    [DataRow(SettingsDeficiencyField.BeatorajaRoot, "navigationGeneral")]
    [DataRow(SettingsDeficiencyField.Lr2BackupOutput, "navigationBackup")]
    public void SettingsWindow_GuidanceRowActivationSelectsPageScrollsTargetIntoViewAndFocusesIt(SettingsDeficiencyField field, string targetNavigation)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            Settings values = CreateValidCustomFolderSettings(Path.Combine(root, "lr2"), [], TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config")));
            if (field == SettingsDeficiencyField.BeatorajaRoot)
            {
                values.BeatorajaRootPath = Path.Combine(root, "missing-beatoraja");
                values.EnableBeatorajaBmtOutput = true;
            }
            else
            {
                values.IsLR2BackupEnabled = true;
                values.LR2BackupPath = Path.Combine(root, "missing-backup");
            }
            values.Save();
            var dialogs = new RecordingRootDialogService();
            var harness = SettingsDialogHarness.Create(values, dialogs: dialogs);
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow(dialogs) { DataContext = harness.Dialog, Width = 820, Height = 600 };
                scope.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                CollectionAssert.AreEqual(new[] { field }, harness.Dialog.Deficiencies.Select(item => item.Field).ToArray());

                // 前提: 一般ページの対象の欄は先頭表示では表示範囲外です。
                SelectPage(window, targetNavigation);
                FrameworkElement target = FindTarget<FrameworkElement>(window, field);
                ScrollViewer viewer = FindAncestor<ScrollViewer>(target);
                viewer.ScrollToVerticalOffset(0);
                window.UpdateLayout();
                if (field == SettingsDeficiencyField.BeatorajaRoot)
                {
                    Assert.IsFalse(IsInViewport(target, viewer), "対象の欄が先頭表示で見えていると、表示範囲へ入れる処理を検出できません。");
                }

                SelectPage(window, "navigationAbout");
                Button row = GuidanceRows((FrameworkElement)window.FindName("settingsDeficiencyGuidance")).Single();
                Assert.IsInstanceOfType<Button>(row);
                row.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreSame(window.FindName(targetNavigation), ((TabControl)window.FindName("settingsNavigation")).SelectedItem);
                Assert.AreSame(((TabItem)window.FindName(targetNavigation)).Content, SettingsTabs.SelectedPage(window), "カテゴリの選択と本文のページが一致する。");
                target = FindTarget<FrameworkElement>(window, field);
                viewer = FindAncestor<ScrollViewer>(target);
                Assert.IsTrue(IsInViewport(target, viewer), "対象の欄を表示範囲へ入れます。");
                var focused = FocusManager.GetFocusedElement(FocusManager.GetFocusScope(target)) as DependencyObject;
                Assert.IsNotNull(focused);
                Assert.IsTrue(IsDescendantOf(focused, target), "対象の欄の最初のフォーカス可能な要素へフォーカスします: " + focused);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                harness.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>
    /// LR2連携で有効な出力先がない間にチェックするとフォルダ選択を1回開き、承認では有効化と出力先を組で保存して次回起動からの案内を1回出し、
    /// 取消・閉鎖では保存せずチェックを外した表示へ戻し、選択の失敗では既存の設定操作の失敗経路で1件だけ通知して保存しないことを確認します。
    /// 有効な出力先が保存済みなら選択を開かずに組で保存します（K7）。
    /// </summary>
    /// <remarks>出力先なしの有効化の保存、取消後のチェック表示の残留、案内の重複・欠落、失敗の握り潰しや二重通知を検出します。</remarks>
    [TestMethod]
    [DataRow("accepted")]
    [DataRow("cancelled")]
    [DataRow("closed")]
    [DataRow("failed")]
    [DataRow("saved-output")]
    public void CompiledLr2BackupCheck_RequiresAcceptedOutputBeforeSavingPair(string outcome)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            string path = Path.Combine(root, "user.config");
            Settings values = CreateValidCustomFolderSettings(Path.Combine(root, "lr2"), [], TestSettingsFactory.OpenSettings(path));
            string output = Directory.CreateDirectory(Path.Combine(root, "backup")).FullName;
            values.IsLR2BackupEnabled = false;
            values.LR2BackupPath = outcome == "saved-output" ? output : string.Empty;
            values.Save();
            byte[] before = File.ReadAllBytes(path);
            var dialogs = new RecordingRootDialogService();
            var failures = new List<Exception>();
            var harness = SettingsDialogHarness.Create(values, reportApplyFailure: failures.Add, dialogs: dialogs);
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow(dialogs) { DataContext = harness.Dialog };
                scope.ShowAndWaitForContentRendered(window);
                SelectPage(window, "navigationBackup");
                var check = (CheckBox)(CurrentPage(window).FindName("lr2BackupEnabledCheckBox") ?? throw new AssertFailedException("checkbox"));
                switch (outcome)
                {
                    case "accepted": dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [output])); break;
                    case "cancelled": dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.CancelledByUser)); break;
                    case "closed": dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.ClosedByUser)); break;
                    case "failed": dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Failed, error: new IOException("picker failed"))); break;
                }
                Toggle(check, harness.Dialog);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Settings saved = TestSettingsFactory.OpenSettings(path);
                Assert.AreEqual(outcome == "saved-output" ? 0 : 1, dialogs.FolderRequests.Count);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                if (outcome is "accepted" or "saved-output")
                {
                    Assert.IsTrue(saved.IsLR2BackupEnabled);
                    Assert.AreEqual(output, saved.LR2BackupPath);
                    Assert.IsTrue(check.IsChecked == true);
                    Assert.AreEqual(1, dialogs.MessageCount);
                    Assert.AreEqual(Resources.Msg_LR2ConfigBackupEnabledNextStartup, dialogs.LastMessageText);
                }
                else
                {
                    CollectionAssert.AreEqual(before, File.ReadAllBytes(path), "選択で組が成立しない限り保存しません。");
                    Assert.IsFalse(check.IsChecked == true, "保存しなかった有効化はチェックを外した表示へ戻します。");
                    Assert.IsFalse(harness.Dialog.IsLR2BackupEnabled);
                    Assert.AreEqual(outcome == "failed" ? 1 : 0, dialogs.MessageCount);
                    if (outcome == "failed") { Assert.AreEqual(Resources.Msg_error_unexpected, dialogs.LastMessageText); }
                    Assert.AreEqual(string.Empty, harness.Dialog.FieldReasons[nameof(SettingsDeficiencyField.Lr2BackupOutput)]);
                }
                Assert.AreEqual(0, harness.Dialog.Deficiencies.Count);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                harness.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>
    /// 画面読み上げなどのUIオートメーションの切替（Click イベントを出さない経路）でも、有効な出力先が保存済みなら組で保存し、
    /// 出力先がなければフォルダ選択を1回開き、取消では保存せずチェックを外した表示へ戻すことを確認します（K7）。
    /// </summary>
    /// <remarks>保存の起点を Click だけに置き、自動化の切替で保存も選択もせず表示だけがチェック済みのまま確定値と食い違う誤りを検出します。</remarks>
    [TestMethod]
    [DataRow("saved-output")]
    [DataRow("cancelled")]
    public void CompiledLr2BackupAutomationToggle_SavesPairOrRequiresAcceptedOutput(string outcome)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            string path = Path.Combine(root, "user.config");
            Settings values = CreateValidCustomFolderSettings(Path.Combine(root, "lr2"), [], TestSettingsFactory.OpenSettings(path));
            string output = Directory.CreateDirectory(Path.Combine(root, "backup")).FullName;
            values.IsLR2BackupEnabled = false;
            values.LR2BackupPath = outcome == "saved-output" ? output : string.Empty;
            values.Save();
            byte[] before = File.ReadAllBytes(path);
            var dialogs = new RecordingRootDialogService();
            var failures = new List<Exception>();
            var harness = SettingsDialogHarness.Create(values, reportApplyFailure: failures.Add, dialogs: dialogs);
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow(dialogs) { DataContext = harness.Dialog };
                scope.ShowAndWaitForContentRendered(window);
                SelectPage(window, "navigationBackup");
                var check = (CheckBox)(CurrentPage(window).FindName("lr2BackupEnabledCheckBox") ?? throw new AssertFailedException("checkbox"));
                if (outcome == "cancelled") { dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.CancelledByUser)); }
                Toggle(check, harness.Dialog, automation: true);
                Settings saved = TestSettingsFactory.OpenSettings(path);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                if (outcome == "saved-output")
                {
                    Assert.AreEqual(0, dialogs.FolderRequests.Count);
                    Assert.IsTrue(saved.IsLR2BackupEnabled);
                    Assert.AreEqual(output, saved.LR2BackupPath);
                    Assert.IsTrue(check.IsChecked == true);
                    Assert.AreEqual(1, dialogs.MessageCount);
                    Assert.AreEqual(Resources.Msg_LR2ConfigBackupEnabledNextStartup, dialogs.LastMessageText);

                    // 自動化で外すと無効化だけを保存し、出力先は残します。
                    Toggle(check, harness.Dialog, automation: true);
                    saved = TestSettingsFactory.OpenSettings(path);
                    Assert.IsFalse(saved.IsLR2BackupEnabled);
                    Assert.AreEqual(output, saved.LR2BackupPath);
                    Assert.IsFalse(check.IsChecked == true);
                    Assert.AreEqual(1, dialogs.MessageCount);
                }
                else
                {
                    Assert.AreEqual(1, dialogs.FolderRequests.Count);
                    CollectionAssert.AreEqual(before, File.ReadAllBytes(path), "選択で組が成立しない限り保存しません。");
                    Assert.IsFalse(check.IsChecked == true, "保存しなかった有効化はチェックを外した表示へ戻します。");
                    Assert.IsFalse(harness.Dialog.IsLR2BackupEnabled);
                    Assert.AreEqual(0, dialogs.MessageCount);
                }
                Assert.AreEqual(0, harness.Dialog.Deficiencies.Count);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                harness.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>
    /// 保存済みの「有効・出力先なし」の組が一覧・印・欄の理由に出て、未チェックでも使える参照ボタンで出力先を選ぶと組で保存して案内を1回出すこと、
    /// チェックを外すと無効化だけを保存して出力先を残すこと、未チェックで出力先を選ぶと出力先だけを保存して案内しないことを確認します（K7・K9）。
    /// </summary>
    /// <remarks>未チェック時の参照ボタンの無効化、外したときの出力先の消去、組が新たに成立しない保存での案内、保存済み不整合の見落としを検出します。</remarks>
    [TestMethod]
    public void CompiledLr2Backup_OutputBeforeCheckAndUncheckKeepPathAndRepairSavedUnmetPair()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot(CaseWorkspace);
            string path = Path.Combine(root, "user.config");
            Settings values = CreateValidCustomFolderSettings(Path.Combine(root, "lr2"), [], TestSettingsFactory.OpenSettings(path));
            string first = Directory.CreateDirectory(Path.Combine(root, "backup-first")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(root, "backup-second")).FullName;
            values.IsLR2BackupEnabled = true;
            values.LR2BackupPath = Path.Combine(root, "missing-backup");
            values.Save();
            var dialogs = new RecordingRootDialogService();
            var failures = new List<Exception>();
            var harness = SettingsDialogHarness.Create(values, reportApplyFailure: failures.Add, dialogs: dialogs);
            SettingsWindow? window = null;
            try
            {
                SettingsDialogViewModel dialog = harness.Dialog;
                window = new SettingsWindow(dialogs) { DataContext = dialog };
                scope.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                CollectionAssert.AreEqual(new[] { (SettingsCategory.Backup, SettingsDeficiencyField.Lr2BackupOutput) },
                    dialog.Deficiencies.Select(item => (item.Category, item.Field)).ToArray());
                AssertMarks(window, "navigationBackup");
                SelectPage(window, "navigationBackup");
                FrameworkElement page = CurrentPage(window);
                var check = (CheckBox)(page.FindName("lr2BackupEnabledCheckBox") ?? throw new AssertFailedException("checkbox"));
                var picker = (SettingsPathPicker)(page.FindName("lr2BackupPathPicker") ?? throw new AssertFailedException("picker"));
                Assert.IsTrue(check.IsChecked == true);
                Assert.AreEqual(Resources.Error_LR2BackupPathNotSet, picker.ValidationMessage);
                Button browse = FindDescendants<Button>(picker).Single();

                void Browse(string selected)
                {
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [selected]));
                    Task completion = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => browse.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(completion, "lr2-backup-browse");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }

                // 保存済みの有効化に出力先を選ぶと、組で保存して案内を1回出します。
                Browse(first);
                Settings saved = TestSettingsFactory.OpenSettings(path);
                Assert.IsTrue(saved.IsLR2BackupEnabled);
                Assert.AreEqual(first, saved.LR2BackupPath);
                Assert.AreEqual(1, dialogs.MessageCount);
                Assert.AreEqual(Resources.Msg_LR2ConfigBackupEnabledNextStartup, dialogs.LastMessageText);
                Assert.AreEqual(0, dialog.Deficiencies.Count);
                Assert.AreEqual(string.Empty, picker.ValidationMessage ?? string.Empty);
                AssertMarks(window);

                // 外すと無効化だけを保存し、出力先は残します。
                Toggle(check, dialog);
                saved = TestSettingsFactory.OpenSettings(path);
                Assert.IsFalse(saved.IsLR2BackupEnabled);
                Assert.AreEqual(first, saved.LR2BackupPath);
                Assert.IsFalse(check.IsChecked == true);

                // 未チェックでも参照ボタンは使え、出力先だけを保存して案内しません。
                Assert.IsTrue(browse.IsEnabled && picker.IsEnabled);
                Browse(second);
                saved = TestSettingsFactory.OpenSettings(path);
                Assert.IsFalse(saved.IsLR2BackupEnabled);
                Assert.AreEqual(second, saved.LR2BackupPath);
                Assert.AreEqual(1, dialogs.MessageCount);

                // 有効な出力先があればチェックだけで組を保存し、新たに成立したため案内を出します。
                Toggle(check, dialog);
                saved = TestSettingsFactory.OpenSettings(path);
                Assert.IsTrue(saved.IsLR2BackupEnabled);
                Assert.AreEqual(second, saved.LR2BackupPath);
                Assert.AreEqual(2, dialogs.MessageCount);
                Assert.AreEqual(2, dialogs.FolderRequests.Count);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                harness.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>
    /// チェックを操作し、compiled の async void ハンドラーと設定操作の終端まで待ちます。
    /// 既定は実クリックの <c>OnClick</c> と同じ順（表示の切替の後に Click イベント）、<paramref name="automation"/> では
    /// UIオートメーションの <see cref="IToggleProvider.Toggle"/>（Click イベントを出さない経路）で切り替えます。
    /// </summary>
    private static void Toggle(CheckBox check, SettingsDialogViewModel dialog, bool automation = false)
    {
        Assert.IsTrue(check.IsEnabled);
        Task completion = MainWindowEventCompletion.CaptureAsyncEventCompletion(() =>
        {
            if (automation)
            {
                ((IToggleProvider)new CheckBoxAutomationPeer(check)).Toggle();
                return;
            }
            check.SetCurrentValue(ToggleButton.IsCheckedProperty, check.IsChecked != true);
            check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        });
        TestUiDispatcherHost.AwaitTaskOnDispatcher(completion, "lr2-backup-toggle");
        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SettingsOperationCompletionTask, "lr2-backup-toggle-terminal");
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static IEnumerable<Button> GuidanceRows(FrameworkElement guidance)
        => FindDescendants<Button>(guidance).Where(button => AutomationProperties.GetAutomationId(button) == "SettingsDeficiencyGuidanceItem")
            .OrderBy(button => button.TransformToAncestor(guidance).Transform(default).Y);

    /// <summary>指定したページの項目だけに印が見え、オートメーションの状態として読めることを確認します。</summary>
    private static void AssertMarks(SettingsWindow window, params string[] marked)
    {
        TestUiDispatcherHost.ProcessQueuedPresentation();
        foreach (TabItem item in ((TabControl)window.FindName("settingsNavigation")).Items.OfType<TabItem>())
        {
            bool expected = marked.Contains(item.Name);
            string status = UIElementAutomationPeer.CreatePeerForElement(item).GetItemStatus();
            Assert.AreEqual(expected ? Resources.Settings_deficiency_page_mark : string.Empty, status ?? string.Empty, item.Name);
            Assert.AreEqual(expected, SettingsNavigationMark.GetIsMarked(item), item.Name);
            // 縦タブの見た目: 選択項目だけに左端の帯と選択背景、フォーカス枠は共通、角丸なし、フォーカス条件のトリガーなし。
            bool selected = ReferenceEquals(item, ((TabControl)window.FindName("settingsNavigation")).SelectedItem);
            var pill = (Border)item.Template.FindName("NavigationPill", item);
            var band = (Border)item.Template.FindName("SelectionIndicator", item);
            Assert.AreEqual(new CornerRadius(0), pill.CornerRadius, item.Name);
            Assert.AreEqual(selected, !IsTransparent(band.Background), item.Name + ": 左端の帯は選択項目だけ。");
            // 選択項目だけが選択背景を持つ。選択されていない項目の背景は、実マウスが重なっている間だけ重ね合わせの背景になる。
            if (selected) { AssertBrushColor(item, "App.ControlSelectedBrush", pill.Background); }
            else { Assert.IsTrue(IsTransparent(pill.Background) || item.IsMouseOver, item.Name + ": 選択背景は選択項目だけ。"); }
            FocusVisualAssert.AssertKeyboardFocusRing(item);
            FocusVisualAssert.AssertNoFocusConditionedTriggers(item);
            FrameworkElement mark = FindDescendants<FrameworkElement>(item).Single(element => element.Name == "DeficiencyMark");
            Assert.AreEqual(expected ? Visibility.Visible : Visibility.Collapsed, mark.Visibility, item.Name);
        }
    }

    private static void AssertBrushColor(FrameworkElement resourceOwner, string resourceKey, Brush? actual)
    {
        var expected = (SolidColorBrush)resourceOwner.FindResource(resourceKey);
        Assert.AreEqual(expected.Color, ((SolidColorBrush?)actual)?.Color, resourceKey);
    }

    private static bool IsTransparent(Brush? brush)
        => brush == null || (brush is SolidColorBrush { Color.A: 0 }) || brush.Opacity == 0d;

    private static void SelectPage(SettingsWindow window, string navigationName)
    {
        ((TabControl)window.FindName("settingsNavigation")).SelectedItem = window.FindName(navigationName);
        TestUiDispatcherHost.ProcessQueuedPresentation();
        window.UpdateLayout();
    }

    private static FrameworkElement CurrentPage(SettingsWindow window)
        => (FrameworkElement)SettingsTabs.SelectedPage(window);

    private static T FindTarget<T>(SettingsWindow window, SettingsDeficiencyField field) where T : FrameworkElement
        => FindDescendants<T>(CurrentPage(window)).Single(element => SettingsDeficiencyTarget.GetField(element) == field.ToString());

    private static T FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T found) { return found; }
        }
        throw new AssertFailedException(typeof(T).Name + " がありません。");
    }

    private static bool IsInViewport(FrameworkElement target, ScrollViewer viewer)
    {
        Rect bounds = target.TransformToAncestor(viewer).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        return bounds.Top >= -0.5 && bounds.Top < viewer.ViewportHeight;
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) { return true; }
        }
        return false;
    }
}
