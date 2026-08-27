using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LocalizationResourceParityTests
{
    private static readonly HashSet<string> JsonOnlyKeys = new(StringComparer.Ordinal)
    {
        "_language_name"
    };

    [TestMethod]
    public void ResourcesGeneratedAccessors_MatchResxStringKeys()
    {
        string root = FindRepositoryRoot();
        HashSet<string> resxKeys = ReadResxStringKeys(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        HashSet<string> generatedKeys = ReadGeneratedResourceStringKeys();

        AssertSetEquals(
            resxKeys,
            generatedKeys,
            "Resources.cs public string accessors must match Resources.resx string keys.");
    }

    [TestMethod]
    public void LanguageJsonFiles_HaveSameKeysAsGeneratedResources()
    {
        string root = FindRepositoryRoot();
        string langDirectory = Path.Combine(root, "lang");
        HashSet<string> resourceKeys = ReadGeneratedResourceStringKeys();

        foreach (string languagePath in Directory.GetFiles(langDirectory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var jsonKeys = ReadLanguageJsonKeys(languagePath)
                .Where(key => !JsonOnlyKeys.Contains(key))
                .ToHashSet(StringComparer.Ordinal);

            AssertSetEquals(
                resourceKeys,
                jsonKeys,
                Path.GetFileName(languagePath) + " keys must match Resources.cs public string accessors.");
        }
    }

    [TestMethod]
    public void InitialSetupLanguageDialogStrings_ArePresentInAllLanguages()
    {
        string root = FindRepositoryRoot();
        string langDirectory = Path.Combine(root, "lang");
        string[] requiredKeys =
        [
            nameof(Resources.InitialSetupLanguageDialogTitle),
            nameof(Resources.InitialSetupLanguageDialogContinue)
        ];

        foreach (string languagePath in Directory.GetFiles(langDirectory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            JObject language = ReadLanguageJsonObject(languagePath);
            foreach (string key in requiredKeys)
            {
                JToken value = language[key] ?? throw new AssertFailedException(Path.GetFileName(languagePath) + " must contain " + key + ".");
                Assert.AreEqual(JTokenType.String, value.Type, Path.GetFileName(languagePath) + " " + key + " must be a string.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(value.Value<string>()), Path.GetFileName(languagePath) + " " + key + " must not be empty.");
            }
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.InitialSetupLanguageDialogTitle));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.InitialSetupLanguageDialogContinue));
    }

    [TestMethod]
    public void SettingsWindowNavigationAndActions_ArePresentInAllLanguages()
    {
        string root = FindRepositoryRoot();
        string langDirectory = Path.Combine(root, "lang");
        string[] requiredKeys =
        [
            nameof(Resources.Advanced_settings),
            nameof(Resources.Settings_window_title),
            nameof(Resources.General),
            nameof(Resources.Appearance),
            nameof(Resources.Playback),
            nameof(Resources.Audio),
            nameof(Resources.Recording),
            nameof(Resources.Playlist),
            nameof(Resources.Install),
            nameof(Resources.Backup),
            nameof(Resources.About_this_app),
            nameof(Resources.Cancel),
            nameof(Resources.Save_and_close)
            , nameof(Resources.Operation_Mode_Standalone)
            , nameof(Resources.Operation_Mode_LR2)
            , nameof(Resources.LR2_integration)
            , nameof(Resources.Settings_appearance_theme_description)
            , nameof(Resources.Settings_appearance_theme_light_description)
            , nameof(Resources.Settings_appearance_theme_dark_description)
            , nameof(Resources.Settings_appearance_table_description)
            , nameof(Resources.Settings_player_executable_path)
            , nameof(Resources.Settings_player_lr2_description)
            , nameof(Resources.Settings_movie_playback_description)
            , nameof(Resources.Settings_audio_output_description)
            , nameof(Resources.Settings_audio_advanced)
            , nameof(Resources.Settings_recording_format_description)
            , nameof(Resources.Settings_token_artist)
            , nameof(Resources.Settings_token_title)
            , nameof(Resources.Settings_token_genre)
            , nameof(Resources.Settings_token_number)
            , nameof(Resources.Settings_token_file)
            , nameof(Resources.Settings_token_hash)
            , nameof(Resources.Settings_library_composition_description)
            , nameof(Resources.Settings_standalone_description)
            , nameof(Resources.Settings_lr2_linked_description)
            , nameof(Resources.Settings_bms_directories_description)
            , nameof(Resources.Settings_list_drag_drop_hint)
            , nameof(Resources.Settings_lr2_paths_description)
            , nameof(Resources.Settings_path_detected)
            , nameof(Resources.Settings_path_missing)
            , nameof(Resources.Lr2_song_db_sync_data_resync)
            , nameof(Resources.Lr2_play_history_schema_label)
            , nameof(Resources.Device_setting_latency)
            , nameof(Resources.Device_setting_test)
            , nameof(Resources.Play_history_folder_display_preset)
            , nameof(Resources.Settings_edit_custom_lr2_paths)
            , nameof(Resources.Settings_lr2_advanced_title)
            , nameof(Resources.Settings_lr2_advanced_description)
            , nameof(Resources.Settings_lr2_advanced_persistence_note)
            , nameof(Resources.Settings_done)
            , nameof(Resources.RightClick_actions)
            , nameof(Resources.RightClick_web_actions)
            , nameof(Resources.RightClick_web_actions_description)
            , nameof(Resources.RightClick_program_actions)
            , nameof(Resources.RightClick_open_with_program)
            , nameof(Resources.RightClick_program_actions_description)
            , nameof(Resources.RightClick_add)
            , nameof(Resources.RightClick_delete)
            , nameof(Resources.RightClick_move_up)
            , nameof(Resources.RightClick_move_down)
            , nameof(Resources.RightClick_restore_defaults)
            , nameof(Resources.RightClick_name)
            , nameof(Resources.RightClick_url)
            , nameof(Resources.RightClick_chart_kind)
            , nameof(Resources.RightClick_enabled)
            , nameof(Resources.RightClick_chart_kind_bms)
            , nameof(Resources.RightClick_chart_kind_bmson)
            , nameof(Resources.RightClick_chart_kind_both)
            , nameof(Resources.RightClick_executable)
            , nameof(Resources.RightClick_arguments)
            , nameof(Resources.RightClick_executable_picker_title)
            , nameof(Resources.RightClick_executable_filter)
            , nameof(Resources.RightClick_settings_invalid)
            , nameof(Resources.RightClick_settings_invalid_format)
            , nameof(Resources.RightClick_settings_validation_error_format)
            , nameof(Resources.RightClick_builtin_bms_ir)
            , nameof(Resources.RightClick_builtin_mocha)
            , nameof(Resources.RightClick_builtin_minir)
            , nameof(Resources.RightClick_builtin_rianir)
            , nameof(Resources.RightClick_builtin_stellaverse)
            , nameof(Resources.RightClick_external_settings_invalid)
            , nameof(Resources.RightClick_external_web_launch_failed)
            , nameof(Resources.RightClick_external_program_executable_missing)
            , nameof(Resources.RightClick_external_program_chart_missing)
            , nameof(Resources.RightClick_external_program_launch_failed)
        ];

        foreach (string languagePath in Directory.GetFiles(langDirectory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            JObject language = ReadLanguageJsonObject(languagePath);
            foreach (string key in requiredKeys)
            {
                JToken value = language[key] ?? throw new AssertFailedException(Path.GetFileName(languagePath) + " must contain " + key + ".");
                Assert.AreEqual(JTokenType.String, value.Type, Path.GetFileName(languagePath) + " " + key + " must be a string.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(value.Value<string>()), Path.GetFileName(languagePath) + " " + key + " must not be empty.");
            }
        }

        foreach (string key in requiredKeys)
        {
            PropertyInfo property = typeof(Resources).GetProperty(key, BindingFlags.Public | BindingFlags.Static)
                ?? throw new AssertFailedException("Resources must expose " + key + ".");
            Assert.IsFalse(string.IsNullOrWhiteSpace(property.GetValue(null) as string), key + " must not be empty in the default resources.");
        }
    }

    [TestMethod]
    public void SettingsPresentationCopy_UsesCurrentPlayerAndAudioTerminology()
    {
        string root = FindRepositoryRoot();
        var expected = new Dictionary<string, (string InternalPlayer, string Lr2Player)>(StringComparer.Ordinal)
        {
            ["en-US.json"] = ("* Configure in Audio settings.", "Play charts with the LR2 executable."),
            ["fr-FR.json"] = ("* Configurez ce réglage dans les paramètres audio.", "Lisez les charts avec l’exécutable LR2."),
            ["ja-JP.json"] = ("※オーディオ設定で設定してください。", "LR2 の実行ファイルで譜面を再生します。"),
            ["ko-KR.json"] = ("* 오디오 설정에서 구성하세요.", "LR2 실행 파일로 차트를 재생합니다."),
            ["zh-CN.json"] = ("* 请在音频设置中进行配置。", "使用 LR2 可执行文件播放谱面。"),
            ["zh-TW.json"] = ("* 請在音訊設定中進行設定。", "使用 LR2 執行檔播放譜面。")
        };

        foreach ((string fileName, (string internalPlayer, string lr2Player)) in expected)
        {
            JObject language = ReadLanguageJsonObject(Path.Combine(root, "lang", fileName));
            Assert.AreEqual(internalPlayer, language[nameof(Resources.Player_Internal_desc)]?.Value<string>(), fileName);
            Assert.AreEqual(lr2Player, language[nameof(Resources.Settings_player_lr2_description)]?.Value<string>(), fileName);
            Assert.IsNull(language["Settings_appearance_preview"], fileName);
            Assert.IsNull(language["About_update_status_format"], fileName);
        }

        JObject japanese = ReadLanguageJsonObject(Path.Combine(root, "lang", "ja-JP.json"));
        Assert.AreEqual("スタンドアローン", japanese[nameof(Resources.Operation_Mode_Standalone)]?.Value<string>());
        Assert.AreEqual("スタンドアローン", Resources.Operation_Mode_Standalone);
        Assert.AreEqual("LR2 の実行ファイルで譜面を再生します。", Resources.Settings_player_lr2_description);
        Assert.AreEqual("※オーディオ設定で設定してください。", Resources.Player_Internal_desc);
        Assert.IsNull(typeof(Resources).GetProperty("Settings_appearance_preview", BindingFlags.Public | BindingFlags.Static));
        Assert.IsNull(typeof(Resources).GetProperty("About_update_status_format", BindingFlags.Public | BindingFlags.Static));
    }

    [TestMethod]
    public void AudioDeviceTestResultStrings_ArePresentInAllLanguages()
    {
        string root = FindRepositoryRoot();
        string langDirectory = Path.Combine(root, "lang");
        var formatArgumentCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [nameof(Resources.AudioDeviceTestSuccessFormat)] = 7,
            [nameof(Resources.AudioDeviceTestFallbackFormat)] = 5,
            [nameof(Resources.AudioDeviceTestStreamFailureFormat)] = 3,
            [nameof(Resources.AudioDeviceTestInitializationErrorFormat)] = 7,
            [nameof(Resources.AudioDeviceTestPlayerCreationFailureReasonFormat)] = 3,
            [nameof(Resources.AudioDeviceTestPlaybackStartFailureReasonFormat)] = 3
        };
        string[] plainKeys =
        [
            nameof(Resources.AudioDeviceTestStreamProgressFailureReason),
            nameof(Resources.AudioDeviceTestFallbackReason),
            nameof(Resources.AudioDeviceTestTestSoundUnavailableReason),
            nameof(Resources.AudioDeviceTestPlayerCreationFailureReason),
            nameof(Resources.AudioDeviceTestInvalidDurationReason),
            nameof(Resources.AudioDeviceTestPlaybackPositionFailureReason),
            nameof(Resources.AudioDeviceTestPlaybackStoppedEarlyReason),
            nameof(Resources.AudioDeviceTestRateFailureReason),
            nameof(Resources.AudioDeviceTestObservationTimeoutReason),
            nameof(Resources.AudioDeviceTestUnexpectedFailureReason)
        ];

        foreach (string languagePath in Directory.GetFiles(langDirectory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            JObject language = ReadLanguageJsonObject(languagePath);
            foreach ((string key, int argumentCount) in formatArgumentCounts)
            {
                string value = language[key]?.Value<string>();
                Assert.IsFalse(string.IsNullOrWhiteSpace(value), Path.GetFileName(languagePath) + " " + key + " must not be empty.");
                for (int index = 0; index < argumentCount; index++)
                {
                    StringAssert.Contains(
                        value,
                        "{" + index.ToString(CultureInfo.InvariantCulture),
                        Path.GetFileName(languagePath) + " " + key + " must preserve placeholder " + index + ".");
                }
                object[] arguments = Enumerable.Range(0, argumentCount)
                    .Select(index => index == argumentCount - 1 ? (object)1.5 : index.ToString(CultureInfo.InvariantCulture))
                    .ToArray();
                try
                {
                    _ = string.Format(CultureInfo.InvariantCulture, value, arguments);
                }
                catch (FormatException exception)
                {
                    Assert.Fail(Path.GetFileName(languagePath) + " " + key + " has invalid placeholders: " + exception.Message);
                }
            }
            foreach (string key in plainKeys)
            {
                string value = language[key]?.Value<string>();
                Assert.IsFalse(string.IsNullOrWhiteSpace(value), Path.GetFileName(languagePath) + " " + key + " must not be empty.");
            }
        }
    }

    private static HashSet<string> ReadGeneratedResourceStringKeys()
    {
        return typeof(Resources)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadResxStringKeys(string path)
    {
        var document = XDocument.Load(path);
        return document
            .Root
            .Elements("data")
            .Where(element => element.Attribute("type") == null)
            .Where(element => element.Attribute("mimetype") == null)
            .Where(element => element.Element("value") != null)
            .Select(element => (string)element.Attribute("name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadLanguageJsonKeys(string path)
    {
        JObject obj = ReadLanguageJsonObject(path);

        return obj.Properties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static JObject ReadLanguageJsonObject(string path)
    {
        string json = File.ReadAllText(path);
        var settings = new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
        };

        var token = JToken.Parse(json, settings);
        if (token is not JObject obj)
        {
            Assert.Fail(Path.GetFileName(path) + " must be a JSON object.");
            throw new AssertFailedException(Path.GetFileName(path) + " must be a JSON object.");
        }

        return obj;
    }

    private static void AssertSetEquals(HashSet<string> expected, HashSet<string> actual, string message)
    {
        string[] missing = [.. expected.Except(actual, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal)];
        string[] extra = [.. actual.Except(expected, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal)];

        if (missing.Length == 0 && extra.Length == 0)
        {
            return;
        }

        Assert.Fail(
            message
            + Environment.NewLine
            + "Missing: "
            + FormatKeys(missing)
            + Environment.NewLine
            + "Extra: "
            + FormatKeys(extra));
    }

    private static string FormatKeys(string[] keys)
    {
        if (keys.Length == 0)
        {
            return "(none)";
        }

        const int maxKeys = 40;
        IEnumerable<string> visibleKeys = keys.Take(maxKeys);
        string suffix = keys.Length > maxKeys ? " ... +" + (keys.Length - maxKeys).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty;
        return string.Join(", ", visibleKeys) + suffix;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
