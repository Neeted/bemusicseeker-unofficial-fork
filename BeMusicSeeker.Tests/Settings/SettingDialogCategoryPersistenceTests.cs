using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
    /// <summary>通常・ROOT・追加先の実compiled選択入口から保存し、picker取消と移行確認取消では実二媒体を変更しません。移行本体と未了だけの修復はOutputConfirmationが分担します。</summary>
    [DataTestMethod]
    [DataRow("normal", "accept")]
    [DataRow("root", "accept")]
    [DataRow("additional", "accept")]
    [DataRow("normal", "picker-cancel")]
    [DataRow("root", "picker-cancel")]
    [DataRow("additional", "picker-cancel")]
    [DataRow("normal", "confirmation-cancel")]
    [DataRow("root", "confirmation-cancel")]
    [DataRow("additional", "confirmation-cancel")]
    [DataRow("normal", "invalid-after-busy")]
    [DataRow("root", "invalid-after-busy")]
    [DataRow("additional", "invalid-after-busy")]
    public void CompiledOutputSelection_CommitsOnlySelectedPlacementAndKeepsCancelledMedia(string section, string outcome)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            string target = Path.Combine(root, "SelectedOutput");
            Settings values = CreateValidCustomFolderSettings(root, [target], PortableSettingsPersistenceTests.OpenSettings(path));
            values.Save();
            string oldNormal = values.LR2CustomFolderOutputBaseDir;
            string oldRoot = values.LR2CustomFolderOutputBaseDirRootType;
            byte[] previousSettings = File.ReadAllBytes(path);
            byte[] previousConfig = File.ReadAllBytes(values.LR2ConfigXmlPath);
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(outcome == "confirmation-cancel" ? MessageBoxResult.Cancel : MessageBoxResult.OK)
            };
            dialogs.FolderResults.Enqueue(outcome == "picker-cancel"
                ? new UiFolderPickerResult(UiDialogStatus.CancelledByUser)
                : new UiFolderPickerResult(UiDialogStatus.Accepted, [target]));
            var failures = new List<Exception>();
            MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: true, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog };
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationPlaylist");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                Button select;
                if (section == "additional")
                {
                    ListBox list = FindDescendants<ListBox>(page).Single(item => item.Name == "additionalOutputList");
                    select = FindDescendants<Button>((DependencyObject)list.Parent).Single(button => Equals(button.Content, Resources.Add));
                }
                else
                {
                    string property = section == "normal" ? nameof(SettingsDialogViewModel.LR2CustomFolderOutputDir) : nameof(SettingsDialogViewModel.LR2CustomFolderAsRootOutputDir);
                    BeMusicSeeker.Views.Settings.SettingsPathPicker picker = FindDescendants<BeMusicSeeker.Views.Settings.SettingsPathPicker>(page)
                        .Single(item => item.GetBindingExpression(BeMusicSeeker.Views.Settings.SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == property);
                    select = FindDescendants<Button>(picker).Single();
                }
                if (outcome == "invalid-after-busy")
                {
                    ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
                    Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable? lease));
                    using (lease)
                    {
                        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "output-rejected-candidate-terminal");
                    }
                    Assert.AreEqual(1, dialogs.MessageCount);
                    CollectionAssert.AreEqual(previousSettings, File.ReadAllBytes(path));
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [root]));
                }
                select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "output-selection-terminal");
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.AreEqual(outcome == "invalid-after-busy" ? 2 : 0, dialogs.MessageCount, dialogs.LastMessageText);
                Assert.AreEqual(outcome == "invalid-after-busy" ? 2 : 1, dialogs.FolderRequests.Count);
                Assert.IsTrue(dialogs.FolderRequests.All(request => ReferenceEquals(window, request.Owner)));
                Assert.AreEqual(outcome == "picker-cancel" ? 0 : 1, dialogs.ConfirmationCount);
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(path);
                if (outcome == "accept")
                {
                    Assert.AreEqual(section == "normal" ? target : oldNormal, fresh.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(section == "root" ? target : oldRoot, fresh.LR2CustomFolderOutputBaseDirRootType);
                    CollectionAssert.AreEqual(section == "additional" ? new[] { target } : Array.Empty<string>(),
                        CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(fresh.LR2CustomFolderAdditionalOutputBaseDirs).ToArray());
                }
                else
                {
                    CollectionAssert.AreEqual(previousSettings, File.ReadAllBytes(path));
                    CollectionAssert.AreEqual(previousConfig, File.ReadAllBytes(values.LR2ConfigXmlPath));
                }
                Assert.IsTrue(window.IsVisible);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-picker-owner-terminal");
                owner.SettingDialog.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>初回LR2の両空値・両旧先欠落から、どちらの実pickerでも順に二媒体へ保存できます。未変更の要修復先へアクセスせず、別区分候補を混ぜず、空値の開始必須検査を維持します。</summary>
    [DataTestMethod]
    [DataRow("normal", false)]
    [DataRow("root", false)]
    [DataRow("normal", true)]
    [DataRow("root", true)]
    public void CompiledInitialLr2OutputPickers_SaveEachRequiredPlacementBeforeAllowingInitialization(string firstSection, bool missingPreviousDirectories)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            string normal = Path.Combine(root, "SelectedNormal");
            string rootOutput = Path.Combine(root, "SelectedRoot");
            Settings values = CreateValidCustomFolderSettings(root, [normal, rootOutput], PortableSettingsPersistenceTests.OpenSettings(path));
            string previousNormal = missingPreviousDirectories ? Path.Combine(root, "UnavailableNormal", "PreviousNormal") : string.Empty;
            string previousRoot = missingPreviousDirectories ? Path.Combine(root, "UnavailableRoot", "PreviousRoot") : string.Empty;
            values.LR2CustomFolderOutputBaseDir = previousNormal;
            values.LR2CustomFolderOutputBaseDirRootType = previousRoot;
            if (missingPreviousDirectories)
            {
                Directory.CreateDirectory(previousNormal);
                Directory.CreateDirectory(previousRoot);
                var config = new BeMusicSeeker.Models.LR2.LR2Config(values.LR2ConfigXmlPath);
                config.AddBMSSearchDirectories([previousNormal]);
                config.Save();
                Directory.Delete(previousNormal);
                Directory.Delete(previousRoot);
                Directory.Delete(Path.Combine(root, "UnavailableNormal"));
                Directory.Delete(Path.Combine(root, "UnavailableRoot"));
                // 登録後に旧先を失い、別区分の保存から作り直すと失敗する実FS条件にします。
                File.WriteAllText(Path.Combine(root, "UnavailableNormal"), "unavailable previous parent");
                File.WriteAllText(Path.Combine(root, "UnavailableRoot"), "unavailable previous parent");
            }
            values.Save();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            var failures = new List<Exception>();
            MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: true, dialogs: dialogs, reportSettingsApplyFailure: failures.Add);
            SettingsWindow? window = null;
            try
            {
                if (!missingPreviousDirectories) { Assert.IsFalse(owner.SettingDialog.CheckValidationForInitialization(out _)); }
                window = new SettingsWindow(dialogs) { DataContext = owner.SettingDialog };
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationPlaylist");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                string otherSection = firstSection == "normal" ? "root" : "normal";
                SetCustomFolderOutput(owner.SettingDialog, otherSection, Path.Combine(root, "UnconfirmedOutput"));
                int completed = 0;
                foreach (string section in new[] { firstSection, otherSection })
                {
                    string property = section == "normal" ? nameof(SettingsDialogViewModel.LR2CustomFolderOutputDir) : nameof(SettingsDialogViewModel.LR2CustomFolderAsRootOutputDir);
                    BeMusicSeeker.Views.Settings.SettingsPathPicker picker = FindDescendants<BeMusicSeeker.Views.Settings.SettingsPathPicker>(page)
                        .Single(item => item.GetBindingExpression(BeMusicSeeker.Views.Settings.SettingsPathPicker.PathProperty)?.ParentBinding.Path?.Path == property);
                    dialogs.FolderResults.Enqueue(new UiFolderPickerResult(UiDialogStatus.Accepted, [section == "normal" ? normal : rootOutput]));
                    FindDescendants<Button>(picker).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.WaitForOperationCompletionAsync(), "initial-output-picker-terminal");
                    completed++;
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(0, dialogs.MessageCount, dialogs.LastMessageText);
                    Assert.AreEqual(completed, dialogs.ConfirmationCount);
                    Settings fresh = PortableSettingsPersistenceTests.OpenSettings(path);
                    string expectedNormal = completed == 2 || section == "normal" ? normal : previousNormal;
                    string expectedRoot = completed == 2 || section == "root" ? rootOutput : previousRoot;
                    Assert.AreEqual(expectedNormal, fresh.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(expectedRoot, fresh.LR2CustomFolderOutputBaseDirRootType);
                    CustomFolderOutputSettingsSnapshot reader = MainWindowViewModelTestFactory.GetComposition(owner).CustomFolderOutputSettingsProvider();
                    Assert.AreEqual(expectedNormal, reader.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(expectedRoot, reader.LR2CustomFolderOutputBaseDirRootType);
                    string[] registered = new BeMusicSeeker.Models.LR2.LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking().ToArray();
                    CollectionAssert.Contains(registered, normal);
                    if (missingPreviousDirectories)
                    {
                        Assert.IsFalse(Directory.Exists(previousNormal));
                        Assert.IsFalse(Directory.Exists(previousRoot));
                        if (expectedNormal == previousNormal) { CollectionAssert.Contains(registered, previousNormal); }
                        else { CollectionAssert.DoesNotContain(registered, previousNormal); }
                    }
                    if (!missingPreviousDirectories || completed == 2)
                    { Assert.AreEqual(completed == 2, owner.SettingDialog.CheckValidationForInitialization(out string error), error); }
                }
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "initial-output-owner-terminal");
                owner.SettingDialog.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>実画像controlの有効選択を自動保存し現在のPlaybackPanel画像へ届けます。実保存失敗は確定値と現画像を保持し、同値・別項目保存では再試行せず明示再試行だけで完了します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CompiledImageSelection_AutomaticallySavesAndUpdatesCurrentArtworkOrKeepsFailure(bool failSave)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string path = Path.Combine(root, "user.config");
            string imagePath = Path.Combine(root, "external.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
                1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 33, 44, 55, 255 }, 4)));
            using (FileStream imageFile = File.Create(imagePath)) { encoder.Save(imageFile); }
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(path));
            values.UseExternalPanelImage = false;
            values.StagefilePath = imagePath;
            values.Save();
            var failures = new List<Exception>();
            var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                reportSettingsApplyFailure: failures.Add, audioDeviceCatalog: new TestAudioDeviceCatalog(),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
            var view = new PlaybackPanelView { DataContext = owner.PlaybackPanel };
            var playbackWindow = new Window { Content = view, Width = 640, Height = 360, ShowInTaskbar = false };
            var artwork = (Image)view.FindName("gridBMSPlayerImage");
            SettingsWindow? window = null;
            try
            {
                scope.ShowAndWaitForContentRendered(playbackWindow);
                window = OpenSettingsWindow(scope, owner.SettingDialog);
                var toggle = (CheckBox)GetCategoryControl(window, "Appearance", nameof(SettingsDialogViewModel.UseExternalPanelImage)).Control;
                System.Windows.Media.ImageSource previousImage = artwork.Source;
                if (failSave)
                {
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    { toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); }
                    Assert.AreEqual(1, failures.Count);
                    Assert.IsFalse(values.UseExternalPanelImage);
                    Assert.AreSame(previousImage, artwork.Source);
                    toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                    owner.SettingDialog.ShowNewPackageInstallConfirmMsg = !values.ShowNewPackageInstallConfirmMsg;
                    Assert.IsFalse(PortableSettingsPersistenceTests.OpenSettings(path).UseExternalPanelImage);
                    Assert.AreEqual(1, failures.Count);
                    Assert.IsTrue(owner.SettingDialog.CanRetryImageSettings);
                    FindDescendants<Button>(window).Single(button => Equals(button.Tag, "retry:image"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else { toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true); }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsInstanceOfType<System.Windows.Media.Imaging.BitmapSource>(artwork.Source);
                var bitmap = (System.Windows.Media.Imaging.BitmapSource)artwork.Source;
                Assert.AreEqual(1, bitmap.PixelWidth);
                byte[] pixels = new byte[4];
                bitmap.CopyPixels(pixels, 4, 0);
                Assert.AreEqual((byte)33, pixels[0]);
                Assert.IsTrue(PortableSettingsPersistenceTests.OpenSettings(path).UseExternalPanelImage);
                Assert.IsFalse(owner.SettingDialog.CanRetryImageSettings);
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(failSave ? 1 : 0, failures.Count);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                view.DisposePlayerHost();
                playbackWindow.Content = null;
                playbackWindow.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "image-owner-terminal");
                owner.SettingDialog.Dispose();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    /// <summary>通常TextBoxのEnterとSliderのドラッグ完了だけを確定時機とし、途中値・不正な別候補を保存せず同じWindowを維持します。</summary>
    [TestMethod]
    public void CompiledOrdinaryInputs_EnterAndSliderCompletionPersistOnlyTheirOwnItem()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Settings values = CreateValidStandaloneSettings(root, PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")));
            values.Save();
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                MainWindowViewModel owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false);
                SetActiveLibraryProfile(owner, true);
                SettingsWindow? window = null;
                try
                {
                    window = OpenSettingsWindow(scope, owner.SettingDialog);
                    owner.SettingDialog.UseBeatorajaScoreDb = true;
                    var text = (TextBox)GetCategoryControl(window, "Install", "FolderNameFormat").Control;
                    text.SetCurrentValue(TextBox.TextProperty, "%ARTIST% - %TITLE%");
                    Assert.AreNotEqual("%ARTIST% - %TITLE%", PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")).FolderNameFormat);
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    text.RaiseEvent(key);
                    Assert.IsTrue(key.Handled);
                    Assert.AreEqual("%ARTIST% - %TITLE%", PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config")).FolderNameFormat);
                    var slider = (Slider)GetCategoryControl(window, "Appearance", "CustomTableRowHeight").Control;
                    double before = values.CustomTableRowHeight;
                    slider.SetCurrentValue(RangeBase.ValueProperty, 27d);
                    slider.SetCurrentValue(RangeBase.ValueProperty, 31d);
                    Assert.AreEqual(before, values.CustomTableRowHeight);
                    slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent, Source = slider });
                    Settings fresh = PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "user.config"));
                    Assert.AreEqual(31d, fresh.CustomTableRowHeight);
                    Assert.IsFalse(fresh.UseBeatorajaScoreDb);
                    ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationAbout");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    var about = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                    System.Windows.Documents.Hyperlink inline = FindDescendants<TextBlock>(about)
                        .SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single();
                    inline.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                    Assert.IsTrue(window.IsVisible, "共通slider終端handlerは非VisualのHyperlink入力を妨げません。");
                }
                finally { window?.CloseForOwnerShutdown(); owner.SettingDialog.Dispose(); }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>各カテゴリの実compiled editorを変更して実保存し、専用providerの新reader、稼働側の設定窓口、新dialogの値を接続します。新しい取消編集は保存済み値へ戻し、別設定と別Web actionを保持します。</summary>
    /// <remarks>各副作用の条件表は既存設定・player・導入・playlist系列へ分担し、この実Window系列はBindingと保存の正常接続だけを補います。文化・テーマ・共有通知・Window一覧を準備から終端と復元まで所有します。</remarks>
    [DataTestMethod]
    [DataRow("General", "Language")]
    [DataRow("Install", "FolderNameFormat")]
    [DataRow("Playlist", "OverwritePlaylistUrlsWithCompletion")]
    [DataRow("Appearance", "CustomTableRowHeight")]
    [DataRow("Playback", "IsSaveLR2bodyWindowPosition")]
    [DataRow("Audio", "PlayerResamplingQuality")]
    [DataRow("Recording", "EncoderAmplifier")]
    [DataRow("Backup", "LR2BackupNum")]
    [DataRow("Install", "KeepInstallablePackagesPending")]
    [DataRow("Install", "RequireCompleteUsedResourcesForAutoInstall")]
    [DataRow("Install", "AutoApplyAmbiguousInstallDestination")]
    [DataRow("Install", "AutoInstallAfterAutomaticEstimation")]
    [DataRow("Install", "InstallDestinationAutoApplyMinAudioHealth")]
    [DataRow("RightClick", "RightClickActionSettingsEditor.SelectedWebAction.Name")]
    public void CompiledCategoryEditor_SaveReachesFreshProviderLiveSettingsAndNewDialog(string category, string bindingPath)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string root = CreateTemporaryRoot();
            string config = Path.Combine(root, "config", "user.config");
            Directory.CreateDirectory(Path.GetDirectoryName(config) ?? throw new AssertFailedException());
            string culture = Resources.Culture?.Name ?? "ja-JP";
            string theme = Settings.Default.AppearanceTheme;
            MainWindowViewModel? owner = null;
            MainWindowViewModel? freshOwner = null;
            SettingsWindow? window = null;
            SettingsWindow? reopened = null;
            Task? save = null;
            Exception? primaryFailure = null;
            try
            {
                Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(config));
                values.Lang = "ja-JP";
                values.LangDisplayName = TestApplicationContext.CreateCultureCatalog().Cultures.Single(pair => pair.Value == "ja-JP").Key;
                values.EnablePlaylistUrlCompletion = true;
                values.PlaylistMd5UrlMappingTsvUri = null;
                values.OverwritePlaylistUrlsWithCompletion = false;
                values.CustomTableRowHeight = 22;
                values.IsSaveLR2bodyWindowPosition = false;
                values.UsePlayerLR2body = true;
                values.PlayerResamplingQuality = 2;
                values.EncoderAmplifier = 1f;
                values.IsLR2BackupEnabled = true;
                values.LR2BackupPath = Path.Combine(root, "backup");
                Directory.CreateDirectory(values.LR2BackupPath);
                values.LR2BackupNum = 2;
                values.KeepInstallablePackagesPending = false;
                values.RequireCompleteUsedResourcesForAutoInstall = false;
                values.AutoApplyAmbiguousInstallDestination = false;
                values.AutoInstallAfterAutomaticEstimation = false;
                // 範囲外の保存値は画面初期化で丸めず、選び直して97%へ修正します。
                values.InstallDestinationAutoApplyMinAudioHealth = bindingPath == "InstallDestinationAutoApplyMinAudioHealth" ? 89 : 100;
                values.FolderNameFormat = "%TITLE%";
                values.RightClickActionsJson = "{\"webActions\":[{\"id\":\"edited\",\"name\":\"Before\",\"urlTemplate\":\"https://example.com/{md5}\",\"enabled\":true,\"chartKind\":\"All\"},{\"id\":\"kept\",\"name\":\"Other\",\"urlTemplate\":\"https://example.org/{md5}\",\"enabled\":true,\"chartKind\":\"BmsOnly\"}],\"programActions\":[]}";
                values.Save();
                string untouched = values.EncodeFileNameFormat;
                var failures = new List<Exception>();
                owner = CreateViewModel(new SettingsEditSession(values), firstStartup: false, reportSettingsApplyFailure: failures.Add);
                SetActiveLibraryProfile(owner, true);
                var presentation = new WindowClosingPresentationPort();
                owner.SettingDialog.AttachPresentationPort(presentation);
                window = OpenSettingsWindow(scope, owner.SettingDialog);
                presentation.CurrentWindow = window;
                object before = ReadCategoryEditor(window, category, bindingPath);
                if (bindingPath == "InstallDestinationAutoApplyMinAudioHealth")
                {
                    Assert.IsNull(before);
                    Assert.AreEqual(89, owner.SettingDialog.InstallDestinationAutoApplyMinAudioHealth);
                    Assert.IsFalse(string.IsNullOrEmpty(owner.SettingDialog.InstallDestinationAutoApplyMinAudioHealthWarning));
                }
                object expected = bindingPath switch
                {
                    "InstallDestinationAutoApplyMinAudioHealth" => 97,
                    "Language" => TestApplicationContext.CreateCultureCatalog().Cultures.Single(pair => pair.Value == "en-US").Key,
                    "FolderNameFormat" => "%ARTIST% - %TITLE%",
                    "OverwritePlaylistUrlsWithCompletion" or "IsSaveLR2bodyWindowPosition"
                        or "KeepInstallablePackagesPending" or "RequireCompleteUsedResourcesForAutoInstall"
                        or "AutoApplyAmbiguousInstallDestination" or "AutoInstallAfterAutomaticEstimation" => true,
                    "CustomTableRowHeight" => 31d,
                    "PlayerResamplingQuality" => 4,
                    "EncoderAmplifier" => (double)1.3f,
                    "LR2BackupNum" => 5,
                    "RightClickActionSettingsEditor.SelectedWebAction.Name" => "Saved custom action",
                    _ => throw new AssertFailedException(bindingPath)
                };
                Assert.AreNotEqual(before, expected);
                EditCategoryControl(window, category, bindingPath, expected);
                if (category == "RightClick")
                {
                    Button confirm = FindDescendants<Button>(window).Single(button => Equals(button.Tag, "confirm:web"));
                    Task click = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(() => confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, confirm)));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(click, "category record confirmation");
                }
                Assert.IsTrue(window.IsVisible, "通常確定・record確定のいずれも同じWindowを保ちます。");
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                window.CloseForOwnerShutdown();
                AssertCategoryRuntime(owner, bindingPath);
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(config);
                AssertCategoryPersisted(fresh, bindingPath);
                Assert.AreEqual(untouched, fresh.EncodeFileNameFormat);
                freshOwner = CreateViewModel(new SettingsEditSession(fresh), firstStartup: false);
                SetActiveLibraryProfile(freshOwner, true);
                var freshPresentation = new WindowClosingPresentationPort();
                freshOwner.SettingDialog.AttachPresentationPort(freshPresentation);
                reopened = OpenSettingsWindow(scope, freshOwner.SettingDialog);
                freshPresentation.CurrentWindow = reopened;
                Assert.AreEqual(expected, ReadCategoryEditor(reopened, category, bindingPath));
                if (category == "RightClick") { EditCategoryControl(reopened, category, bindingPath, before); }
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                reopened.Closed += (_, _) => cancelled.TrySetResult();
                freshOwner.SettingDialog.CloseCommand.Execute();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(cancelled.Task, "category Cancel close");
                AssertCategoryRuntime(freshOwner, bindingPath);
                AssertCategoryPersisted(PortableSettingsPersistenceTests.OpenSettings(config), bindingPath);
                reopened = OpenSettingsWindow(scope, freshOwner.SettingDialog);
                freshPresentation.CurrentWindow = reopened;
                Assert.AreEqual(expected, ReadCategoryEditor(reopened, category, bindingPath));
            }
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                var cleanupFailures = new List<Exception>();
                void Cleanup(Action action)
                {
                    try { action(); }
                    catch (Exception failure) { cleanupFailures.Add(failure); }
                }
                if (save != null) { Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(save, "category save cleanup")); }
                if (window?.IsVisible == true) { Cleanup(window.CloseForOwnerShutdown); }
                if (reopened?.IsVisible == true) { Cleanup(reopened.CloseForOwnerShutdown); }
                foreach (MainWindowViewModel current in new[] { freshOwner, owner }.OfType<MainWindowViewModel>())
                {
                    Cleanup(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(current.ShellShutdownWorkflow.RequestWindowCloseAsync(), "category owner shutdown"));
                    Cleanup(current.SettingDialog.Dispose);
                }
                Cleanup(() => ResourceService.Current.ChangeCulture(culture));
                Cleanup(() => AppThemeService.ApplyTheme(theme));
                Cleanup(() => Directory.Delete(root, recursive: true));
                if (cleanupFailures.Count > 0)
                {
                    var cleanupFailure = new AggregateException("category fixture cleanup", cleanupFailures);
                    if (primaryFailure == null) { throw cleanupFailure; }
                    Console.WriteLine(cleanupFailure);
                }
            }
        });
    }

    /// <summary>カテゴリを実navigationで材料化し、該当Bindingを持つ編集可能なcontrolを選びます。</summary>
    private static (FrameworkElement Control, DependencyProperty Property) GetCategoryControl(SettingsWindow window, string category, string path)
    {
        ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigation" + category);
        TestUiDispatcherHost.ProcessQueuedPresentation();
        var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
        foreach (FrameworkElement control in FindDescendants<FrameworkElement>(page))
        {
            DependencyProperty? property = control switch
            {
                TextBox => TextBox.TextProperty,
                ComboBox => path == "Language" ? Selector.SelectedItemProperty : Selector.SelectedValueProperty,
                CheckBox => ToggleButton.IsCheckedProperty,
                Slider => RangeBase.ValueProperty,
                _ => null
            };
            if (property != null && control.GetBindingExpression(property)?.ParentBinding.Path?.Path == path)
            {
                Assert.IsTrue(control.IsEnabled, category + " editor must be enabled");
                return (control, property);
            }
        }
        throw new AssertFailedException("compiled editor Binding not found: " + category + "/" + path);
    }

    /// <summary>実Bindingのeditor値を読み、ViewModelへの直接代入で端末確認を代用しません。</summary>
    private static object ReadCategoryEditor(SettingsWindow window, string category, string path)
    {
        (FrameworkElement control, DependencyProperty property) = GetCategoryControl(window, category, path);
        return control.GetValue(property);
    }

    /// <summary>実controlを編集し、boolは標準の即時Binding伝播、それ以外は明示UpdateSourceで保存入力へ届けます。</summary>
    private static void EditCategoryControl(SettingsWindow window, string category, string path, object value)
    {
        (FrameworkElement control, DependencyProperty property) = GetCategoryControl(window, category, path);
        control.SetCurrentValue(property, value);
        if (control is TextBox text)
        {
            text.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, text));
        }
        else if (control is Slider slider)
        {
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent, Source = slider });
        }
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    /// <summary>本番compositionが実稼働側へ渡す窓口と同じactionの読取りから、保存後の指定値を確認します。</summary>
    private static void AssertCategoryRuntime(MainWindowViewModel owner, string bindingPath)
    {
        ApplicationComposition composition = MainWindowViewModelTestFactory.GetComposition(owner);
        object ReadField(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
            ?? throw new AssertFailedException("runtime field unavailable: " + name);
        switch (bindingPath)
        {
            case "Language": Assert.AreEqual("en-US", Resources.Culture?.Name); break;
            case "FolderNameFormat": Assert.AreEqual("%ARTIST% - %TITLE%", composition.BmsLibraryOptionsProvider().FolderNameFormat); break;
            case "OverwritePlaylistUrlsWithCompletion": Assert.IsTrue(composition.PlaylistUrlCompletionOptionsProvider().OverwritePlaylistUrlsWithCompletion); break;
            case "CustomTableRowHeight": Assert.AreEqual(31d, composition.MainWindowViewSettingsStore.CustomTableRowHeight); break;
            case "IsSaveLR2bodyWindowPosition": Assert.IsTrue(((IPlayerSettingsGateway)ReadField(composition, "playerSettingsGateway")).CaptureSnapshot().IsSaveLR2bodyWindowPosition); break;
            case "PlayerResamplingQuality": Assert.AreEqual(4, ((IPlayerSettingsGateway)ReadField(composition, "playerSettingsGateway")).CaptureSnapshot().SampleRateConversionQuality); break;
            case "EncoderAmplifier": Assert.AreEqual(1.3f, ((IAudioSettingsGateway)ReadField(composition, "audioSettingsGateway")).CaptureEncodingSettings().EncoderAmplifier); break;
            case "LR2BackupNum": Assert.AreEqual(5, composition.StartupSettingsProvider().LR2BackupNum); break;
            case "InstallDestinationAutoApplyMinAudioHealth":
                Assert.AreEqual(97, composition.BmsLibraryOptionsProvider().InstallDestinationAutoApplyMinAudioHealth); break;
            case "KeepInstallablePackagesPending":
            case "RequireCompleteUsedResourcesForAutoInstall":
            case "AutoApplyAmbiguousInstallDestination":
            case "AutoInstallAfterAutomaticEstimation":
                Assert.IsTrue(bindingPath switch
                {
                    "RequireCompleteUsedResourcesForAutoInstall" => composition.BmsLibraryOptionsProvider().RequireCompleteUsedResourcesForAutoInstall,
                    "KeepInstallablePackagesPending" => composition.BmsLibraryOptionsProvider().KeepInstallablePackagesPending,
                    "AutoApplyAmbiguousInstallDestination" => composition.BmsLibraryOptionsProvider().AutoApplyAmbiguousInstallDestination,
                    "AutoInstallAfterAutomaticEstimation" => composition.BmsLibraryOptionsProvider().AutoInstallAfterAutomaticEstimation,
                    _ => throw new AssertFailedException(bindingPath)
                }); break;
            case "RightClickActionSettingsEditor.SelectedWebAction.Name": AssertCustomWebActions(((RightClickActionSettingsStore)ReadField(owner.SelectedChartExternalActions, "rightClickActionSettingsStore")).Load()); break;
        }
    }

    /// <summary>同じSettings instanceの再確認と分け、新providerの実永続値をBinding先ごとに確認します。</summary>
    private static void AssertCategoryPersisted(Settings settings, string bindingPath)
    {
        switch (bindingPath)
        {
            case "Language": Assert.AreEqual("en-US", settings.Lang); break;
            case "FolderNameFormat": Assert.AreEqual("%ARTIST% - %TITLE%", settings.FolderNameFormat); break;
            case "OverwritePlaylistUrlsWithCompletion": Assert.IsTrue(settings.OverwritePlaylistUrlsWithCompletion); break;
            case "CustomTableRowHeight": Assert.AreEqual(31d, settings.CustomTableRowHeight); break;
            case "IsSaveLR2bodyWindowPosition": Assert.IsTrue(settings.IsSaveLR2bodyWindowPosition); break;
            case "PlayerResamplingQuality": Assert.AreEqual(4, settings.PlayerResamplingQuality); break;
            case "EncoderAmplifier": Assert.AreEqual(1.3f, settings.EncoderAmplifier); break;
            case "LR2BackupNum": Assert.AreEqual(5, settings.LR2BackupNum); break;
            case "InstallDestinationAutoApplyMinAudioHealth":
                Assert.AreEqual(97, settings.InstallDestinationAutoApplyMinAudioHealth); break;
            case "KeepInstallablePackagesPending":
            case "RequireCompleteUsedResourcesForAutoInstall":
            case "AutoApplyAmbiguousInstallDestination":
            case "AutoInstallAfterAutomaticEstimation":
                Assert.IsTrue(bindingPath switch
                {
                    "RequireCompleteUsedResourcesForAutoInstall" => settings.RequireCompleteUsedResourcesForAutoInstall,
                    "KeepInstallablePackagesPending" => settings.KeepInstallablePackagesPending,
                    "AutoApplyAmbiguousInstallDestination" => settings.AutoApplyAmbiguousInstallDestination,
                    "AutoInstallAfterAutomaticEstimation" => settings.AutoInstallAfterAutomaticEstimation,
                    _ => throw new AssertFailedException(bindingPath)
                }); break;
            case "RightClickActionSettingsEditor.SelectedWebAction.Name": AssertCustomWebActions(new RightClickActionSettingsStore(() => settings).Load()); break;
        }
    }

    /// <summary>編集対象IDと別actionの指定値を保持し、名前だけが別actionへ誤適用された結果を拒否します。</summary>
    private static void AssertCustomWebActions(RightClickActionSettingsParseResult parsed)
    {
        Assert.IsNotNull(parsed.Settings, parsed.Error?.ToString());
        Assert.AreEqual(2, parsed.Settings.WebActions.Count);
        RightClickWebActionDefinition edited = parsed.Settings.WebActions.Single(action => action.Id == "edited");
        Assert.AreEqual("Saved custom action", edited.Name);
        Assert.AreEqual("https://example.com/{md5}", edited.UrlTemplate);
        Assert.IsTrue(edited.Enabled);
        Assert.AreEqual(ExternalChartKind.All, edited.ChartKind);
        RightClickWebActionDefinition kept = parsed.Settings.WebActions.Single(action => action.Id == "kept");
        Assert.AreEqual("Other", kept.Name);
        Assert.AreEqual("https://example.org/{md5}", kept.UrlTemplate);
        Assert.IsTrue(kept.Enabled);
        Assert.AreEqual(ExternalChartKind.BmsOnly, kept.ChartKind);
    }
}
