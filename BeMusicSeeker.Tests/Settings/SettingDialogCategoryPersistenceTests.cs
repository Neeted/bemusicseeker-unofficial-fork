using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
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
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                Assert.IsTrue(owner.SettingDialog.CheckValidation(out string validation), validation);
                save = owner.SettingDialog.ApplySettingsAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(save, "category real save");
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "category saved Window close");
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.IsNull(window.DataContext);
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
                EditCategoryControl(reopened, category, bindingPath, bindingPath == "InstallDestinationAutoApplyMinAudioHealth" ? 90 : before);
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                reopened.Closed += (_, _) => cancelled.TrySetResult();
                freshOwner.SettingDialog.CancelCommand.Execute();
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
        if (control is not CheckBox)
        {
            (control.GetBindingExpression(property) ?? throw new AssertFailedException()).UpdateSource();
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
