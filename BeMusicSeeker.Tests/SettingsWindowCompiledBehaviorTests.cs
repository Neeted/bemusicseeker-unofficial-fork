using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using BeMusicSeeker.Views.Settings;
using BeMusicSeeker.Views.Settings.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Verifies settings-page behavior through the compiled WPF tree without showing a modal window.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SettingsWindowCompiledBehaviorTests
{
    [TestInitialize]
    public void MaterializeCanonicalApplicationResources()
    {
        TestUiDispatcherHost.Invoke(EnsureCanonicalApplicationResources);
    }

    [TestMethod]
    public void EverySettingsNavigationPageMaterializesWithoutCreatingPendingChanges()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(new Settings
            {
                OperationModeLR2DB = false,
                BMSRootPath = Path.GetTempPath(),
                StandaloneBmsRootPaths = Path.GetTempPath(),
                BMSInstallDir = Path.GetTempPath(),
                ScanBmsFilesOnStartup = false,
                SkipInitPlaylistLoad = true,
                UseBeatorajaScoreDb = false,
                EnableBeatorajaBmtOutput = false,
                UsePlayeruBMplay = false,
                UsePlayerLR2body = false,
                UsePlayerBMIIDXView = false
            });
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                Assert.IsFalse(owner.SettingDialog.HasPendingSettingChanges());

                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                Type[] pageTypes =
                {
                    typeof(GeneralSettingsPage),
                    typeof(AppearanceSettingsPage),
                    typeof(PlaybackSettingsPage),
                    typeof(AudioSettingsPage),
                    typeof(RecordingSettingsPage),
                    typeof(PlaylistSettingsPage),
                    typeof(InstallSettingsPage),
                    typeof(BackupSettingsPage),
                    typeof(AdvancedSettingsPage),
                    typeof(RightClickSettingsPage),
                    typeof(AboutSettingsPage)
                };
                Assert.AreEqual(pageTypes.Length, navigation.Items.Count);
                for (int index = 0; index < pageTypes.Length; index++)
                {
                    navigation.SelectedIndex = index;
                    Materialize(window);
                    Assert.IsNotNull(content.Content);
                    Assert.AreSame(owner.SettingDialog, ((FrameworkElement)content.Content).DataContext);
                    Assert.AreEqual(pageTypes[index], content.Content.GetType(),
                        $"Unexpected page materialized for navigation index {index}: {content.Content.GetType().Name}.");
                    Assert.IsFalse(owner.SettingDialog.HasPendingSettingChanges(),
                        $"Materializing page index {index} changed the settings draft.");
                }
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void PlaybackPageCompiledTreeMaterializesCurrentPlayerControls()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(new Settings
            {
                OperationModeLR2DB = false,
                BMSRootPath = Path.GetTempPath(),
                StandaloneBmsRootPaths = Path.GetTempPath(),
                BMSInstallDir = Path.GetTempPath(),
                ScanBmsFilesOnStartup = false,
                SkipInitPlaylistLoad = true,
                UsePlayeruBMplay = false,
                UsePlayerLR2body = false,
                UsePlayerBMIIDXView = false
            });
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationPlayback");
                Materialize(window);

                var page = (PlaybackSettingsPage)content.Content;
                var internalPlayer = (RadioButton)page.FindName("radioButtonInternalPlayer");
                var ubmplayPlayer = (RadioButton)page.FindName("radioButtonPlayuBMplay");
                var bmiidxPlayer = (RadioButton)page.FindName("radioButtonPlayBMIIDXView");
                var lr2Player = (RadioButton)page.FindName("radioButtonPlayLR2body");

                Assert.AreSame(owner.SettingDialog, page.DataContext);
                Assert.AreSame(owner.SettingDialog, internalPlayer.DataContext);
                Assert.AreSame(owner.SettingDialog, ubmplayPlayer.DataContext);
                Assert.AreSame(owner.SettingDialog, bmiidxPlayer.DataContext);
                Assert.AreSame(owner.SettingDialog, lr2Player.DataContext);
                Assert.AreEqual(nameof(SettingsDialogViewModel.UseInternalPlayer), GetBindingPath(internalPlayer, ToggleButton.IsCheckedProperty));
                Assert.AreEqual(nameof(SettingsDialogViewModel.UsePlayeruBMplay), GetBindingPath(ubmplayPlayer, ToggleButton.IsCheckedProperty));
                Assert.AreEqual(nameof(SettingsDialogViewModel.UsePlayerBMIIDXView), GetBindingPath(bmiidxPlayer, ToggleButton.IsCheckedProperty));
                Assert.AreEqual(nameof(SettingsDialogViewModel.UsePlayerLR2body), GetBindingPath(lr2Player, ToggleButton.IsCheckedProperty));
                Assert.IsFalse(owner.SettingDialog.HasPendingSettingChanges());
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void RightClickPageBindsTheChildEditorWithoutMutatingSettingsOnOpen()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(new Settings
            {
                OperationModeLR2DB = false,
                BMSRootPath = Path.GetTempPath(),
                StandaloneBmsRootPaths = Path.GetTempPath(),
                BMSInstallDir = Path.GetTempPath(),
                ScanBmsFilesOnStartup = false,
                SkipInitPlaylistLoad = true,
                RightClickActionsJson = "{\"webActions\":[],\"programActions\":[]}"
            });
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationRightClick");
                Materialize(window);

                Assert.IsInstanceOfType<RightClickSettingsPage>(content.Content);
                Assert.AreSame(owner.SettingDialog, ((FrameworkElement)content.Content).DataContext);
                var page = (RightClickSettingsPage)content.Content;
                var webActions = (ListBox)page.FindName("webActionsListBox");
                var programActions = (ListBox)page.FindName("programActionsListBox");
                Assert.AreEqual(0, webActions.Items.Count);
                Assert.AreEqual(0, programActions.Items.Count);
                Assert.IsFalse(owner.SettingDialog.HasPendingSettingChanges());
                Assert.IsNotNull(owner.SettingDialog.RightClickActionSettingsEditor);
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void RightClickPageMarksNewWebActionRequiredFieldsAsErrors()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(new Settings
            {
                OperationModeLR2DB = false,
                BMSRootPath = Path.GetTempPath(),
                StandaloneBmsRootPaths = Path.GetTempPath(),
                BMSInstallDir = Path.GetTempPath(),
                ScanBmsFilesOnStartup = false,
                SkipInitPlaylistLoad = true,
                RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson
            });
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationRightClick");
                Materialize(window);

                owner.SettingDialog.RightClickActionSettingsEditor.AddWebAction();
                Materialize(window);

                var page = (RightClickSettingsPage)content.Content;
                TextBox nameEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "RightClickWebName");
                TextBox urlEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                    AutomationProperties.GetAutomationId(textBox) == "RightClickWebUrl");

                Assert.AreEqual(string.Empty, nameEditor.Text);
                Assert.AreEqual("Error", AutomationProperties.GetItemStatus(nameEditor));
                Assert.AreEqual(Resources.RightClick_name_required, AutomationProperties.GetHelpText(nameEditor));
                Assert.AreEqual(string.Empty, urlEditor.Text);
                Assert.AreEqual("Error", AutomationProperties.GetItemStatus(urlEditor));
                Assert.AreEqual(Resources.RightClick_url_required, AutomationProperties.GetHelpText(urlEditor));
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void RightClickPageShowsProgramValidationForAllFieldsAndRefreshesItAfterLanguageChange()
    {
        string previousCulture = Resources.Culture?.Name ?? "ja-JP";
        try
        {
            ResourceService.Current.ChangeCulture("en-US");
            TestUiDispatcherHost.RunWindowTest(_ =>
            {
                MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(new Settings
                {
                    OperationModeLR2DB = false,
                    BMSRootPath = Path.GetTempPath(),
                    StandaloneBmsRootPaths = Path.GetTempPath(),
                    BMSInstallDir = Path.GetTempPath(),
                    ScanBmsFilesOnStartup = false,
                    SkipInitPlaylistLoad = true,
                    RightClickActionsJson = "{\"webActions\":[],\"programActions\":[]}"
                });
                var window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel
                };
                try
                {
                    Materialize(window);
                    var navigation = (ListBox)window.FindName("settingsNavigation");
                    var content = (ContentControl)window.FindName("settingsPageContent");
                    navigation.SelectedItem = window.FindName("navigationRightClick");
                    Materialize(window);

                    var page = (RightClickSettingsPage)content.Content;
                    Button programAddButton = FindLogicalDescendants<Button>(page).Single(button =>
                        AutomationProperties.GetAutomationId(button) == "RightClickProgramAdd");
                    programAddButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, programAddButton));
                    Materialize(window);
                    page = (RightClickSettingsPage)content.Content;
                    TextBox nameEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramName");
                    TextBox executableEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramExecutable");
                    TextBox argumentsEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramArguments");

                    Assert.AreEqual(Resources.RightClick_name_required, AutomationProperties.GetHelpText(nameEditor));
                    Assert.AreEqual(Resources.RightClick_executable_required, AutomationProperties.GetHelpText(executableEditor));
                    Assert.AreEqual(string.Empty, AutomationProperties.GetHelpText(argumentsEditor));
                    Assert.AreEqual("Error", AutomationProperties.GetItemStatus(nameEditor));
                    Assert.AreEqual("Error", AutomationProperties.GetItemStatus(executableEditor));
                    Assert.IsTrue(executableEditor.IsReadOnly);

                    nameEditor.Text = "Viewer";
                    owner.SettingDialog.RightClickActionSettingsEditor.SelectedProgramAction
                        .SetExecutablePathFromPicker(@"C:\Tools\viewer.exe");
                    argumentsEditor.Text = "--fixed";
                    Materialize(window);
                    Assert.AreEqual(string.Empty, AutomationProperties.GetHelpText(nameEditor));
                    Assert.AreEqual(string.Empty, AutomationProperties.GetHelpText(executableEditor));
                    string expectedMissingPlaceholderMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.RightClick_arguments_file_path_required,
                        "{filePath}");
                    Assert.AreEqual(expectedMissingPlaceholderMessage, AutomationProperties.GetHelpText(argumentsEditor));
                    string englishArgumentMessage = AutomationProperties.GetHelpText(argumentsEditor);

                    ResourceService.Current.ChangeCulture("ja-JP");
                    Materialize(window);
                    Assert.AreEqual(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.RightClick_arguments_file_path_required,
                            "{filePath}"),
                        AutomationProperties.GetHelpText(argumentsEditor));
                    Assert.AreNotEqual(englishArgumentMessage, AutomationProperties.GetHelpText(argumentsEditor));

                    navigation.SelectedItem = window.FindName("navigationGeneral");
                    Materialize(window);
                    navigation.SelectedItem = window.FindName("navigationRightClick");
                    Materialize(window);
                    page = (RightClickSettingsPage)content.Content;
                    nameEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramName");
                    executableEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramExecutable");
                    argumentsEditor = FindLogicalDescendants<TextBox>(page).Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "RightClickProgramArguments");

                    Assert.AreEqual("Viewer", nameEditor.Text);
                    Assert.AreEqual(@"C:\Tools\viewer.exe", executableEditor.Text);
                    Assert.AreEqual("--fixed", argumentsEditor.Text);
                    Assert.AreEqual(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.RightClick_arguments_file_path_required,
                            "{filePath}"),
                        AutomationProperties.GetHelpText(argumentsEditor));
                    Assert.AreEqual("Error", AutomationProperties.GetItemStatus(argumentsEditor));

                    argumentsEditor.Text = "{filePath}";
                    Materialize(window);
                    Assert.AreEqual("{filePath}", argumentsEditor.Text);
                    Assert.AreEqual(string.Empty, AutomationProperties.GetHelpText(argumentsEditor));
                    Assert.AreEqual(string.Empty, AutomationProperties.GetItemStatus(argumentsEditor));
                }
                finally
                {
                    window.CloseForOwnerShutdown();
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousCulture);
        }
    }

    [TestMethod]
    public void BeatorajaScoreDbControlsUseLocalizedResourcesAndValidation()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            string previousCulture = Resources.Culture?.Name ?? "ja-JP";
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                owner = MainWindowViewModelTestFactory.Create(new Settings
                {
                    OperationModeLR2DB = false,
                    BMSRootPath = Path.GetTempPath(),
                    StandaloneBmsRootPaths = Path.GetTempPath(),
                    BMSInstallDir = Path.GetTempPath(),
                    ScanBmsFilesOnStartup = false,
                    SkipInitPlaylistLoad = true,
                    UseBeatorajaScoreDb = false,
                    EnableBeatorajaBmtOutput = false,
                    RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson
                });
                window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel
                };
                Materialize(window);

                var generalPage = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                var scoreDb = (CheckBox)generalPage.FindName("useBeatorajaScoreDbCheckBox");
                var hashMode = (ComboBox)generalPage.FindName("beatorajaHashOutputModeComboBox");
                var hashModeField = (SettingsField)generalPage.FindName("beatorajaHashOutputModeField");

                scoreDb.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                hashMode.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
                hashModeField.GetBindingExpression(HeaderedContentControl.HeaderProperty)?.UpdateTarget();

                Assert.AreEqual(Resources.Use_beatoraja_scoreDB, scoreDb.Content);
                Assert.AreEqual(Resources.Beatoraja_bmt_hash_output_mode, hashModeField.Header);
                Assert.AreEqual(nameof(SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption.DisplayName), hashMode.DisplayMemberPath);
                Assert.AreEqual(nameof(SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption.Key), hashMode.SelectedValuePath);
                CollectionAssert.AreEquivalent(
                    owner.SettingDialog.BeatorajaBmtHashOutputModeOptions.ToArray(),
                    hashMode.Items.Cast<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption>().ToArray());
                SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption[] englishOptions =
                    hashMode.Items.Cast<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption>().ToArray();
                AssertHashOptionDisplayNames(englishOptions);
                var displayNameNotifications = new HashSet<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption>();
                foreach (SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption option in englishOptions)
                {
                    option.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption.DisplayName))
                        {
                            displayNameNotifications.Add(option);
                        }
                    };
                }

                scoreDb.IsChecked = true;
                Materialize(window);
                Assert.IsFalse(owner.SettingDialog.CheckValidation(out string englishValidationError));
                StringAssert.Contains(englishValidationError, Resources.Error_InvalidBeatorajaRootPath);

                ResourceService.Current.ChangeCulture("ja-JP");
                Materialize(window);
                scoreDb.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                hashModeField.GetBindingExpression(HeaderedContentControl.HeaderProperty)?.UpdateTarget();
                CollectionAssert.AreEquivalent(englishOptions, displayNameNotifications.ToArray());
                Assert.AreEqual(Resources.Use_beatoraja_scoreDB, scoreDb.Content);
                Assert.AreEqual(Resources.Beatoraja_bmt_hash_output_mode, hashModeField.Header);
                SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption[] japaneseOptions =
                    hashMode.Items.Cast<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption>().ToArray();
                Assert.AreEqual(englishOptions.Length, japaneseOptions.Length);
                for (int index = 0; index < englishOptions.Length; index++)
                {
                    Assert.AreSame(englishOptions[index], japaneseOptions[index]);
                }
                AssertHashOptionDisplayNames(japaneseOptions);

                Materialize(window);
                Assert.IsFalse(owner.SettingDialog.CheckValidation(out string japaneseValidationError));
                StringAssert.Contains(japaneseValidationError, Resources.Error_InvalidBeatorajaRootPath);
            }
            finally
            {
                window?.CloseForOwnerShutdown();
                owner?.SettingDialog.Dispose();
                ResourceService.Current.ChangeCulture(previousCulture);
            }
        });
    }

    [TestMethod]
    public void GeneralAndPlaylistPagesExposeDistinctCompiledBindings()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create(new Settings
            {
                OperationModeLR2DB = false,
                BMSRootPath = Path.GetTempPath(),
                StandaloneBmsRootPaths = Path.GetTempPath(),
                BMSInstallDir = Path.GetTempPath(),
                ScanBmsFilesOnStartup = false,
                SkipInitPlaylistLoad = true
            });
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                Assert.IsInstanceOfType<GeneralSettingsPage>(content.Content);
                Assert.AreSame(owner.SettingDialog, ((FrameworkElement)content.Content).DataContext);
                var language = (ComboBox)((FrameworkElement)content.Content).FindName("languageComboBox");
                Assert.AreEqual(nameof(SettingsDialogViewModel.Languages),
                    GetBindingPath(language, ItemsControl.ItemsSourceProperty));

                navigation.SelectedItem = window.FindName("navigationPlaylist");
                Materialize(window);
                Assert.IsInstanceOfType<PlaylistSettingsPage>(content.Content);
                Assert.AreSame(owner.SettingDialog, ((FrameworkElement)content.Content).DataContext);
                var tableListUrl = (TextBox)((FrameworkElement)content.Content).FindName("tableListUrlTextBox");
                var urlCompletion = (CheckBox)((FrameworkElement)content.Content).FindName("enablePlaylistUrlCompletionCheckBox");
                Binding? tableListUrlBinding = BindingOperations.GetBinding(tableListUrl, TextBox.TextProperty);
                Binding? urlCompletionBinding = BindingOperations.GetBinding(urlCompletion, ToggleButton.IsCheckedProperty);
                Assert.AreEqual(nameof(SettingsDialogViewModel.TableListURL), tableListUrlBinding?.Path?.Path);
                Assert.AreEqual(UpdateSourceTrigger.LostFocus, tableListUrlBinding?.UpdateSourceTrigger);
                Assert.AreEqual(nameof(SettingsDialogViewModel.EnablePlaylistUrlCompletion), urlCompletionBinding?.Path?.Path);
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void GeneralLr2MaintenanceActionsHaveDistinctSemanticSectionOwners()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var page = (GeneralSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                Button resyncButton = FindLogicalDescendants<Button>(page).Single(button =>
                    GetBindingPath(button, ContentControl.ContentProperty) == "Resources.Lr2_song_db_sync_data_resync");
                SettingsStatusBanner schemaBanner = FindLogicalDescendants<SettingsStatusBanner>(page).Single(banner =>
                    GetBindingPath(banner, ContentControl.ContentProperty) == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaStatusText));
                Button schemaButton = FindLogicalDescendants<Button>(page).Single(button =>
                    GetBindingPath(button, ContentControl.ContentProperty) == nameof(SettingsDialogViewModel.Lr2PlayHistorySchemaInstallOrRepairButtonText));

                SettingsSection? resyncSection = FindNearestSettingsSection(resyncButton);
                SettingsSection? schemaSection = FindNearestSettingsSection(schemaBanner);
                Assert.IsNotNull(resyncSection);
                Assert.IsNotNull(schemaSection);
                Assert.AreNotSame(resyncSection, schemaSection);
                Assert.IsFalse(string.IsNullOrWhiteSpace(resyncSection!.Header?.ToString()));
                Assert.IsFalse(string.IsNullOrWhiteSpace(schemaSection!.Header?.ToString()));
                Assert.AreNotEqual(resyncSection!.Header?.ToString(), schemaSection!.Header?.ToString());
                Assert.AreSame(schemaSection, FindNearestSettingsSection(schemaButton));
                Assert.AreEqual("Resources.Lr2_song_db_sync_data_resync",
                    GetBindingPath(resyncSection!, HeaderedContentControl.HeaderProperty));
                Assert.AreEqual("Resources.Lr2_play_history_schema_label",
                    GetBindingPath(schemaSection!, HeaderedContentControl.HeaderProperty));
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void AudioMeasurementControlsShareDedicatedLocalizedSemanticSection()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            string previousCulture = Resources.Culture?.Name ?? "ja-JP";
            MainWindowViewModel? owner = null;
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                owner = MainWindowViewModelTestFactory.Create();
                var window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel
                };
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationAudio");
                Materialize(window);
                var page = (AudioSettingsPage)content.Content;
                SettingsField latencyField = FindLogicalDescendants<SettingsField>(page).Single(field =>
                    GetBindingPath(field, HeaderedContentControl.HeaderProperty) == "Resources.Device_setting_latency");
                Button testButton = FindLogicalDescendants<Button>(page).Single(button =>
                    GetBindingPath(button, ContentControl.ContentProperty) == "Resources.Device_setting_test");
                SettingsSection? measurementSection = FindNearestSettingsSection(testButton);
                SettingsSection? latencySection = FindNearestSettingsSection(latencyField);
                SettingsSection advancedSection = FindLogicalDescendants<SettingsSection>(page).Single(section =>
                    GetBindingPath(section, HeaderedContentControl.HeaderProperty) == "Resources.Settings_audio_advanced");
                SettingsSection volumeSection = FindLogicalDescendants<SettingsSection>(page).Single(section =>
                    GetBindingPath(section, HeaderedContentControl.HeaderProperty) == "Resources.Device_setting_volume");

                Assert.AreSame(measurementSection, latencySection);
                Assert.AreNotSame(advancedSection, measurementSection);
                Assert.AreNotSame(volumeSection, measurementSection);
                Assert.IsFalse(string.IsNullOrWhiteSpace(measurementSection!.Header?.ToString()));
                Assert.AreEqual("レイテンシ", latencyField.Header);
                Assert.AreEqual("Resources.Device_setting_test",
                    GetBindingPath(measurementSection!, HeaderedContentControl.HeaderProperty));
            }
            finally
            {
                owner?.SettingDialog.Dispose();
                ResourceService.Current.ChangeCulture(previousCulture);
            }
        });
    }

    [TestMethod]
    public void PlayHistoryPresetEditorUsesItsOwnLocalizedSectionOutsideMd5Mapping()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            var window = new SettingsWindow
            {
                DataContext = owner.SettingDialog,
                PlaybackPanel = owner.PlaybackPanel
            };
            try
            {
                Materialize(window);
                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");
                navigation.SelectedItem = window.FindName("navigationPlaylist");
                Materialize(window);
                var page = (PlaylistSettingsPage)content.Content;
                var presetList = (ListBox)page.FindName("playHistoryPresetList");
                SettingsListEditor presetEditor = FindLogicalDescendants<SettingsListEditor>(page).Single(editor =>
                    FindLogicalDescendants<ListBox>(editor).Any(list => ReferenceEquals(list, presetList)));
                SettingsSection? presetSection = FindNearestSettingsSection(presetEditor);
                SettingsSection md5Section = FindLogicalDescendants<SettingsSection>(page).Single(section =>
                    GetBindingPath(section, HeaderedContentControl.HeaderProperty) == "Resources.Playlist_md5_url_mapping_tsv_uri");

                Assert.IsNotNull(presetSection);
                Assert.AreNotSame(md5Section, presetSection);
                Assert.IsFalse(string.IsNullOrWhiteSpace(presetSection!.Header?.ToString()));
                Assert.AreEqual("Resources.Play_history_folder_display_preset",
                    GetBindingPath(presetSection!, HeaderedContentControl.HeaderProperty));
                Assert.IsTrue(string.IsNullOrWhiteSpace(presetEditor.Header?.ToString()),
                    "The preset section owns the heading; the nested list editor must not duplicate it.");
                Assert.IsTrue(string.IsNullOrWhiteSpace(presetEditor.Description?.ToString()),
                    "The preset section owns the description; the nested list editor must not duplicate it.");
            }
            finally
            {
                owner.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void StandaloneBmsRootsUseLocalizedListAddAndRemoveBindings()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            string previousCulture = Resources.Culture?.Name ?? "ja-JP";
            string scope = Path.Combine(Path.GetTempPath(), "bemusicseeker-settings-roots-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scope);
            string firstRoot = Path.Combine(scope, "first");
            string secondRoot = Path.Combine(scope, "second");
            string addedRoot = Path.Combine(scope, "added");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);
            Directory.CreateDirectory(addedRoot);
            MainWindowViewModel? owner = null;
            try
            {
                ResourceService.Current.ChangeCulture("en-US");
                owner = MainWindowViewModelTestFactory.Create(new Settings
                {
                    OperationModeLR2DB = false,
                    BMSRootPath = firstRoot,
                    StandaloneBmsRootPaths = string.Join(Environment.NewLine, firstRoot, secondRoot),
                    BMSInstallDir = firstRoot,
                    ScanBmsFilesOnStartup = false,
                    SkipInitPlaylistLoad = true
                });
                var window = new SettingsWindow
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel
                };
                Materialize(window);
                var page = (FrameworkElement)((ContentControl)window.FindName("settingsPageContent")).Content;
                var roots = (ListBox)page.FindName("bmsSearchRootPathListBox");
                Assert.AreEqual(2, roots.Items.Count);
                Assert.AreEqual(nameof(SettingsDialogViewModel.AvailableBMSDirectories),
                    roots.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path?.Path);
                var add = (Button)page.FindName("addBmsSearchRootButton");
                var remove = (Button)page.FindName("removeBmsSearchRootButton");
                add.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                remove.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                Assert.AreEqual(Resources.Add_BMSDirectory, add.Content);
                Assert.AreEqual(Resources.Remove_BMSDirectory, remove.Content);

                owner.SettingDialog.AddBmsSearchRootPathFromPicker(nameof(SettingsDialogViewModel.BMSRootPath), addedRoot);
                Materialize(window);
                Assert.AreEqual(3, roots.Items.Count);
                Assert.IsTrue(roots.Items.Cast<string>().Any(path => string.Equals(path, addedRoot, StringComparison.OrdinalIgnoreCase)));

                roots.SelectedItem = secondRoot;
                Materialize(window);
                roots.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateSource();
                remove.GetBindingExpression(ButtonBase.CommandParameterProperty)?.UpdateTarget();
                Assert.IsTrue(remove.Command.CanExecute(remove.CommandParameter));
                remove.Command.Execute(remove.CommandParameter);
                Materialize(window);
                Assert.IsFalse(roots.Items.Cast<string>().Any(path => string.Equals(path, secondRoot, StringComparison.OrdinalIgnoreCase)));

                ResourceService.Current.ChangeCulture("ja-JP");
                Materialize(window);
                add.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                remove.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                Assert.AreEqual(Resources.Add_BMSDirectory, add.Content);
                Assert.AreEqual(Resources.Remove_BMSDirectory, remove.Content);
            }
            finally
            {
                owner?.SettingDialog.Dispose();
                ResourceService.Current.ChangeCulture(previousCulture);
                Directory.Delete(scope, recursive: true);
            }
        });
    }

    [TestMethod]
    public void BackupAdvancedAndAboutPagesExposeCompiledActionContracts()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? owner = null;
            SettingsWindow? window = null;
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No)
            };
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser));

            try
            {
                owner = MainWindowViewModelTestFactory.Create();
                window = new SettingsWindow(dialogs)
                {
                    DataContext = owner.SettingDialog,
                    PlaybackPanel = owner.PlaybackPanel,
                    PlaylistWorkspace = owner.PlaylistWorkspace
                };
                Materialize(window);

                var navigation = (ListBox)window.FindName("settingsNavigation");
                var content = (ContentControl)window.FindName("settingsPageContent");

                navigation.SelectedIndex = 7;
                Materialize(window);
                var backupPage = (BackupSettingsPage)content.Content;
                var backupButtons = FindLogicalDescendants<Button>(backupPage).ToList();
                Assert.AreEqual(2, backupButtons.Count);
                Button backupButton = backupButtons[0];
                Button restoreButton = backupButtons[1];
                backupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, backupButton));
                restoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, restoreButton));
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(1, dialogs.SaveFilePickerRequests.Count);
                UiSaveFilePickerRequest saveRequest = dialogs.SaveFilePickerRequests.Single();
                Assert.AreSame(window, saveRequest.Owner);
                Assert.AreEqual("BeMusicSeeker_backup.sql", saveRequest.FileName);
                Assert.AreEqual(".sql", saveRequest.DefaultExtension);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Sql_file_exts, saveRequest.Filter);
                Assert.IsTrue(saveRequest.AddExtension);
                Assert.AreSame(window, dialogs.LastConfirmationRequest.Owner);

                var dangerButtons = new List<Button>();
                var advancedDangerButtons = new List<Button>();
                int advancedNavigationIndex = navigation.Items.IndexOf(window.FindName("navigationAdvanced"));
                for (int index = 0; index < navigation.Items.Count; index++)
                {
                    navigation.SelectedIndex = index;
                    Materialize(window);
                    var page = (FrameworkElement)content.Content;
                    var pageDangerButtonStyle = (Style)page.FindResource("SettingsDangerButtonStyle");
                    var pageButtons = FindLogicalDescendants<Button>(page).ToList();
                    var pageDangerButtons = pageButtons
                        .Where(button => ReferenceEquals(button.Style, pageDangerButtonStyle))
                        .ToList();
                    dangerButtons.AddRange(pageDangerButtons);
                    if (index == advancedNavigationIndex)
                    {
                        advancedDangerButtons.AddRange(pageDangerButtons);
                    }
                }

                Assert.AreEqual(2, dangerButtons.Count);
                Assert.AreEqual(2, advancedDangerButtons.Count);

                navigation.SelectedItem = window.FindName("navigationAbout");
                Materialize(window);
                var aboutPage = (AboutSettingsPage)content.Content;
                Button projectButton = FindLogicalDescendants<Button>(aboutPage)
                    .Single(button => Equals(
                        button.Tag,
                        "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases"));
                Assert.AreEqual(
                    "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases",
                    projectButton.Tag);
                Assert.IsFalse(owner.SettingDialog.HasPendingSettingChanges());
            }
            finally
            {
                if (window?.IsVisible == true)
                {
                    window.CloseForOwnerShutdown();
                }
                owner?.SettingDialog.Dispose();
            }
        });
    }

    private static void Materialize(SettingsWindow window)
    {
        window.Measure(new Size(820, 760));
        window.Arrange(new Rect(0, 0, 820, 760));
        window.UpdateLayout();
        TestUiDispatcherHost.Drain();
    }

    private static string? GetBindingPath(DependencyObject target, DependencyProperty property)
    {
        return BindingOperations.GetBinding(target, property)?.Path?.Path
            ?? BindingOperations.GetBindingExpression(target, property)?.ParentBinding.Path?.Path;
    }

    private static SettingsSection? FindNearestSettingsSection(DependencyObject element)
    {
        for (DependencyObject current = element; current != null;)
        {
            if (current is SettingsSection section)
            {
                return section;
            }

            DependencyObject? parent = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : null;
            current = parent ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void EnsureCanonicalApplicationResources()
    {
        Application application = Application.Current;
        Assert.IsNotNull(application);
        AddCanonicalResourceIfMissing(
            application,
            "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalControls.xaml");
        AddCanonicalResourceIfMissing(
            application,
            "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalDialogStyles.xaml");
    }

    private static void AddCanonicalResourceIfMissing(Application application, string source)
    {
        if (application.Resources.MergedDictionaries.Any(dictionary =>
            string.Equals(dictionary.Source?.OriginalString, source, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(source, UriKind.RelativeOrAbsolute)
        });
    }

    private static void AssertHashOptionDisplayNames(
        IReadOnlyList<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption> options)
    {
        CollectionAssert.AreEqual(
            new[]
            {
                Resources.Beatoraja_bmt_hash_output_original,
                Resources.Beatoraja_bmt_hash_output_fill_missing,
                Resources.Beatoraja_bmt_hash_output_prefer_sha256_only
            },
            options.Select(option => option.DisplayName).ToArray());
    }

    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        foreach (object? child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject)
            {
                continue;
            }

            foreach (T descendant in FindLogicalDescendants<T>(dependencyObject))
            {
                yield return descendant;
            }
        }
    }

}
