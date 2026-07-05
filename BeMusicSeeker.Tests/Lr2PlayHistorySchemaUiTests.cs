using System;
using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2PlayHistorySchemaUiTests
{
    [TestMethod]
    public void Lr2ScoreDbPathResolver_ResolvesExistingPlayerScoreDbOnly()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2ScoreDbPathResolver_" + Guid.NewGuid().ToString("N"));
        try
        {
            string scoreDirectoryPath = Path.Combine(directoryPath, "LR2files", "Database", "Score");
            Directory.CreateDirectory(scoreDirectoryPath);
            string scoreDbPath = Path.Combine(scoreDirectoryPath, "player1.db");
            File.WriteAllText(scoreDbPath, string.Empty);

            Assert.AreEqual(scoreDbPath, Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(directoryPath, "player1"));
            Assert.AreEqual(Path.Combine(scoreDirectoryPath, "missing.db"), Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(directoryPath, "missing"));
            Assert.AreEqual(scoreDbPath, Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, "player1"));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, "missing"));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, (string)null!));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, () => throw new InvalidOperationException("config")));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Lr2PlayHistorySchemaStatusPresentation_MapsActionsAndLabels()
    {
        Lr2PlayHistorySchemaStatusPresentation notInstalled = CreatePresentation(Lr2PlayHistorySchemaStatus.NotInstalled);
        Assert.IsTrue(notInstalled.CanInstall);
        Assert.IsFalse(notInstalled.CanRepair);
        Assert.AreEqual(Resources.Lr2_play_history_schema_status_not_installed, notInstalled.StatusText);

        Lr2PlayHistorySchemaStatusPresentation repairable = CreatePresentation(Lr2PlayHistorySchemaStatus.Repairable);
        Assert.IsFalse(repairable.CanInstall);
        Assert.IsTrue(repairable.CanRepair);
        Assert.AreEqual(Resources.Lr2_play_history_schema_status_repairable, repairable.StatusText);

        foreach (Lr2PlayHistorySchemaStatus status in new[]
        {
            Lr2PlayHistorySchemaStatus.Installed,
            Lr2PlayHistorySchemaStatus.ManualRepairRequired,
            Lr2PlayHistorySchemaStatus.Unreadable,
            Lr2PlayHistorySchemaStatus.SkippedProfile
        })
        {
            Lr2PlayHistorySchemaStatusPresentation presentation = CreatePresentation(status);
            Assert.IsFalse(presentation.CanInstall, status.ToString());
            Assert.IsFalse(presentation.CanRepair, status.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(presentation.StatusText), status.ToString());
        }
    }

    [TestMethod]
    public void SettingDialog_Lr2PlayHistorySchemaUiUsesExplicitInstallBoundary()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string uninstallDialogXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Lr2PlayHistorySchemaUninstallDialog.xaml"));
        string viewModel = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        string resourceCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.cs"));
        string playHistorySchemaUi = ExtractBetween(
            xaml,
            "<Grid Margin=\"20,2,10,4\" IsEnabled=\"{Binding IsChecked, ElementName=radioButtonUseLR2}\">",
            "<Grid Margin=\"10,0,0,4\" IsEnabled=\"{Binding settingDialog.IsBmsSearchRootEditorEnabled}\">");

        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Lr2_play_history_schema_label, Mode=OneWay"));
        Assert.AreEqual(0, CountOccurrences(xaml, "Click=\"refreshLr2PlayHistorySchemaButtonClicked\""));
        Assert.AreEqual(0, CountOccurrences(xaml, "Click=\"installLr2PlayHistorySchemaButtonClicked\""));
        Assert.AreEqual(0, CountOccurrences(xaml, "Click=\"repairLr2PlayHistorySchemaButtonClicked\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Click=\"installOrRepairLr2PlayHistorySchemaButtonClicked\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Text=\"{Binding settingDialog.Lr2PlayHistorySchemaStatusText, Mode=OneWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "ToolTip=\"{Binding settingDialog.Lr2PlayHistorySchemaDetailText, Mode=OneWay}\""));
        StringAssert.Contains(xaml, "Content=\"{Binding settingDialog.Lr2PlayHistorySchemaInstallOrRepairButtonText, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding settingDialog.CanInstallOrRepairLr2PlayHistorySchema, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "DataTrigger Binding=\"{Binding settingDialog.CanInstallOrRepairLr2PlayHistorySchema, Mode=OneWay}\" Value=\"True\"");
        StringAssert.Contains(xaml, "Value=\"{DynamicResource App.WarningTextBrush}\"");
        StringAssert.Contains(xaml, "Click=\"uninstallLr2PlayHistorySchemaButtonClicked\"");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding settingDialog.CanUninstallLr2PlayHistorySchema, Mode=OneWay}\"");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_title, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_desc, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_score_db, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_mode, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_triggers_only, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_tables_and_triggers, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_warning, Mode=OneWay");
        Assert.IsTrue(
            xaml.IndexOf("Lr2_play_history_schema_label", StringComparison.Ordinal)
            < xaml.IndexOf("Lr2_song_db_sync_data_resync", StringComparison.Ordinal));
        StringAssert.Contains(playHistorySchemaUi, "<RowDefinition Height=\"20\" />");
        StringAssert.Contains(playHistorySchemaUi, "Grid.Row=\"0\" Grid.Column=\"1\" Width=\"270\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" TextAlignment=\"Center\"");
        StringAssert.Contains(playHistorySchemaUi, "<Run Text=\": \" />");
        StringAssert.Contains(playHistorySchemaUi, "Grid.Row=\"1\" Grid.Column=\"0\" Width=\"190\" Height=\"24\" HorizontalAlignment=\"Center\"");
        StringAssert.Contains(playHistorySchemaUi, "Grid.Row=\"1\" Grid.Column=\"1\" Width=\"130\" Height=\"24\" HorizontalAlignment=\"Center\"");
        Assert.IsTrue(
            playHistorySchemaUi.IndexOf("Grid.Row=\"1\" Grid.Column=\"0\"", StringComparison.Ordinal)
            < playHistorySchemaUi.IndexOf("Grid.Row=\"1\" Grid.Column=\"1\"", StringComparison.Ordinal));

        string installHandler = ExtractBetween(
            codeBehind,
            "private async Task InstallOrRepairLr2PlayHistorySchemaAsync",
            "private async void detailTabItemRestoreButtonClicked");
        string normalizedInstallHandler = installHandler.Replace("\r\n", "\n");
        StringAssert.Contains(installHandler, "Msg_confirm_lr2_play_history_schema_install_or_repair");
        StringAssert.Contains(installHandler, "Task.Run(settingDialogViewModel.InstallOrRepairLr2PlayHistorySchemaCore)");
        StringAssert.Contains(installHandler, "Task.Run(() =>");
        StringAssert.Contains(installHandler, "settingDialogViewModel.CheckLr2PlayHistorySchemaCore(expectedScoreDbPath, expectedOperationMode)");
        StringAssert.Contains(normalizedInstallHandler, "!= MessageBoxResult.OK)\n        {\n            return;\n        }\n\n        settingDialogRootGrid.IsEnabled = false;");
        Assert.IsTrue(
            installHandler.IndexOf("Msg_confirm_lr2_play_history_schema_install_or_repair", StringComparison.Ordinal)
            < installHandler.IndexOf("Task.Run(settingDialogViewModel.InstallOrRepairLr2PlayHistorySchemaCore)", StringComparison.Ordinal));
        StringAssert.Contains(installHandler, "viewModel.ReloadScoresOnly();");
        Assert.IsFalse(installHandler.Contains("SaveSettings("));
        Assert.IsFalse(installHandler.Contains("Settings.Default.Save"));
        Assert.IsFalse(installHandler.Contains("lr2config.Save"));
        StringAssert.Contains(codeBehind, "private async void uninstallLr2PlayHistorySchemaButtonClicked");
        StringAssert.Contains(codeBehind, "private void SettingDialogIsVisibleChanged");
        Assert.IsFalse(codeBehind.Contains("await RefreshLr2PlayHistorySchemaStatusAsync(settingDialogViewModel, force: false);"));
        StringAssert.Contains(codeBehind, "settingDialogViewModel.RefreshLr2PlayHistorySchemaStatusPresentation();");
        StringAssert.Contains(codeBehind, "new Lr2PlayHistorySchemaUninstallDialog(settingDialogViewModel.Lr2PlayHistoryScoreDbPath)");

        StringAssert.Contains(viewModel, "Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(Settings.Default.LR2RootPath, lr2config.GetPlayerId)");
        StringAssert.Contains(viewModel, "Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(Settings.Default.LR2RootPath, () => lr2config?.GetPlayerId())");
        StringAssert.Contains(viewModel, "new Lr2PlayHistorySchemaService().Check(scoreDbPath, isLr2LinkedProfile)");
        StringAssert.Contains(viewModel, "new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, OperationModeLR2DB)");
        StringAssert.Contains(viewModel, "new Lr2PlayHistorySchemaService().Uninstall(scoreDbPath, OperationModeLR2DB, uninstallMode)");
        StringAssert.Contains(viewModel, "lr2PlayHistorySchemaCheckResult == null || CanInstallLr2PlayHistorySchema || CanRepairLr2PlayHistorySchema");
        StringAssert.Contains(viewModel, "play_history_schema_");

        foreach (string key in RequiredResourceKeys)
        {
            StringAssert.Contains(resources, "name=\"" + key + "\"");
            StringAssert.Contains(resourceCode, "public static string " + key);
            foreach (string languagePath in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
            {
                StringAssert.Contains(File.ReadAllText(languagePath), "\"" + key + "\"");
            }
        }
        Assert.IsFalse(resources.Contains("name=\"Lr2_play_history_schema_refresh\""));
        Assert.IsFalse(resourceCode.Contains("public static string Lr2_play_history_schema_refresh"));
        foreach (string languagePath in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
        {
            Assert.IsFalse(File.ReadAllText(languagePath).Contains("\"Lr2_play_history_schema_refresh\""));
        }
    }

    private static Lr2PlayHistorySchemaStatusPresentation CreatePresentation(Lr2PlayHistorySchemaStatus status)
    {
        return Lr2PlayHistorySchemaStatusPresentation.Create(new Lr2PlayHistorySchemaCheckResult
        {
            Status = status,
            Message = status.ToString()
        });
    }

    private static string FindRepositoryRoot()
    {
        string? directoryPath = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            string currentDirectoryPath = directoryPath!;
            if (File.Exists(Path.Combine(currentDirectoryPath, "BeMusicSeeker.sln")))
            {
                return currentDirectoryPath;
            }
            DirectoryInfo? parent = Directory.GetParent(currentDirectoryPath);
            directoryPath = parent?.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string ExtractBetween(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, "start marker not found: " + start);
        int endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(endIndex > startIndex, "end marker not found: " + end);
        return text.Substring(startIndex, endIndex - startIndex);
    }

    private static readonly string[] RequiredResourceKeys =
    [
        "Lr2_play_history_schema_label",
        "Lr2_play_history_schema_install",
        "Lr2_play_history_schema_repair",
        "Lr2_play_history_schema_install_or_repair",
        "Lr2_play_history_schema_uninstall",
        "Lr2_play_history_schema_uninstall_title",
        "Lr2_play_history_schema_uninstall_desc",
        "Lr2_play_history_schema_uninstall_score_db",
        "Lr2_play_history_schema_uninstall_mode",
        "Lr2_play_history_schema_uninstall_triggers_only",
        "Lr2_play_history_schema_uninstall_tables_and_triggers",
        "Lr2_play_history_schema_uninstall_warning",
        "Lr2_play_history_schema_status_unknown",
        "Lr2_play_history_schema_status_installed",
        "Lr2_play_history_schema_status_not_installed",
        "Lr2_play_history_schema_status_repairable",
        "Lr2_play_history_schema_status_manual_repair_required",
        "Lr2_play_history_schema_status_unreadable",
        "Lr2_play_history_schema_status_skipped_profile",
        "Lr2_play_history_schema_message_skipped_profile",
        "Lr2_play_history_schema_message_score_db_path_not_configured",
        "Lr2_play_history_schema_message_score_db_file_not_found",
        "Lr2_play_history_schema_message_install_or_repair_failed_format",
        "Lr2_play_history_schema_message_uninstall_failed_format",
        "Lr2_play_history_schema_message_base_schema_incompatible",
        "Lr2_play_history_schema_message_object_name_collision",
        "Lr2_play_history_schema_message_table_columns_incompatible",
        "Lr2_play_history_schema_message_partial_schema_manual_action_required",
        "Lr2_play_history_schema_message_not_installed",
        "Lr2_play_history_schema_message_installed",
        "Lr2_play_history_schema_message_install_or_repair_available",
        "Msg_confirm_lr2_play_history_schema_install_or_repair",
        "Msg_success_lr2_play_history_schema_install_or_repair",
        "Msg_lr2_play_history_schema_uninstall_not_installed",
        "Msg_success_lr2_play_history_schema_uninstall"
    ];
}
