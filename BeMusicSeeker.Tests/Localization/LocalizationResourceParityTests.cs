using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

/// <summary>表示リソースの整合と実JSON読取りの互換・失敗境界を検証します。</summary>
[TestClass]
public sealed class LocalizationResourceParityTests
{
    private const string LanguageNameKey = "_language_name";

    /// <summary>基準辞書の全キーに公開アクセサーがあり、非空の有効な書式であることを確認します。</summary>
    [TestMethod]
    public void ResourcesGeneratedAccessors_MatchResxStringKeys()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, string> values = ReadResxStringValues(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        AssertSetEquals(
            values.Keys.ToHashSet(StringComparer.Ordinal),
            ReadGeneratedResourceStringKeys(),
            "Resources.cs の公開文字列アクセサーは Resources.resx のキーと一致する必要があります。");

        foreach ((string key, string value) in values)
        {
            _ = ReadFormatArgumentIndices("Resources.resx", key, value);
        }
    }

    /// <summary>全言語のキー・書式引数・非空値に加え、日本語文言と基準辞書の一致を確認します。</summary>
    [TestMethod]
    public void LanguageJsonFiles_HaveSameKeysAsGeneratedResources()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, string> resx = ReadResxStringValues(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        var expectedArguments = resx.ToDictionary(
            pair => pair.Key,
            pair => ReadFormatArgumentIndices("Resources.resx", pair.Key, pair.Value),
            StringComparer.Ordinal);
        HashSet<string> resourceKeys = ReadGeneratedResourceStringKeys();
        string[] languagePaths = Directory.GetFiles(Path.Combine(root, "lang"), "*.json");
        Assert.IsTrue(languagePaths.Length > 0, "同梱する言語辞書がありません。");

        foreach (string languagePath in languagePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string sourceName = Path.GetFileName(languagePath);
            JObject language = ReadLanguageJsonObject(languagePath);
            var keys = language.Properties()
                .Select(property => property.Name)
                .Where(key => key != LanguageNameKey)
                .ToHashSet(StringComparer.Ordinal);
            AssertSetEquals(resourceKeys, keys, sourceName + " のキーは Resources.cs と一致する必要があります。");
            AssertSetEquals(resx.Keys.ToHashSet(StringComparer.Ordinal), keys, sourceName + " のキーは Resources.resx と一致する必要があります。");

            foreach (JProperty property in language.Properties())
            {
                Assert.AreEqual(JTokenType.String, property.Value.Type, sourceName + " " + property.Name + " は文字列である必要があります。");
                string value = property.Value.Value<string>()!;
                Assert.IsFalse(string.IsNullOrWhiteSpace(value), sourceName + " " + property.Name + " は空にできません。");
                if (property.Name == LanguageNameKey)
                {
                    continue;
                }

                if (string.Equals(sourceName, "ja-JP.json", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.AreEqual(
                        resx[property.Name],
                        value,
                        sourceName + " " + property.Name + " の日本語文言は Resources.resx と一致する必要があります。");
                }

                HashSet<int> actualArguments = ReadFormatArgumentIndices(sourceName, property.Name, value);
                Assert.IsTrue(
                    expectedArguments[property.Name].SetEquals(actualArguments),
                    sourceName + " " + property.Name + " の書式引数番号は Resources.resx と一致する必要があります。");
            }
        }
    }

    /// <summary>UTF-8のBOM有無と任意キー・大小文字を維持し、正常な辞書を空のfallbackへ変換する誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LanguageFile_Utf8AndBomPreserveAcceptedDictionary(bool includeBom)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker.LanguageRead." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "ja-JP.json");
            File.WriteAllText(path, "{\"_language_name\":\"日本語の編集版\",\"任意キー\":\"追加した値\",\"Key\":\"大文字\",\"key\":\"小文字\"}", new UTF8Encoding(includeBom));

            IReadOnlyDictionary<string, string> values = JsonLanguageCatalog.LoadLanguageMapFromFile(path);

            Assert.AreEqual(4, values.Count);
            Assert.AreEqual("日本語の編集版", values["_language_name"]);
            Assert.AreEqual("追加した値", values["任意キー"]);
            Assert.AreEqual("大文字", values["Key"]);
            Assert.AreEqual("小文字", values["key"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>欠落・壊れたJSON・重複キー・null辞書は既知の入力失敗としてfallbackへ渡し、失敗前の断片を公開しません。</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("{\"Key\":")]
    [DataRow("{\"Key\":\"first\",\"Key\":\"second\"}")]
    [DataRow("null")]
    public void LanguageFile_MissingOrInvalidInputReturnsEmptyFallback(string? json)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker.LanguageFailure." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "fr-FR.json");
            if (json != null)
            {
                File.WriteAllText(path, json, Encoding.UTF8);
            }

            IReadOnlyDictionary<string, string> values = JsonLanguageCatalog.LoadLanguageMapFromFile(path);

            Assert.AreEqual(0, values.Count);
            Assert.IsFalse(values.ContainsKey("Key"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>実ディレクトリの正常辞書は破損辞書・無効カルチャ・言語名なしと独立して候補へ入り、欠落ディレクトリでも日本語を保ちます。</summary>
    [TestMethod]
    public void LanguageDiscovery_MixedFilesPreserveValidCultureAndJapaneseFallback()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker.LanguageDiscovery." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "en-US.json"), "{\"_language_name\":\"Custom English\",\"CustomKey\":\"accepted\"}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "fr-FR.json"), "{\"_language_name\":", Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "invalid!.json"), "{\"_language_name\":\"Invalid culture\"}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "ko-KR.json"), "{\"CustomKey\":\"no language name\"}", Encoding.UTF8);

            Dictionary<string, string> languages = JsonLanguageCatalog.DiscoverLanguages(directory);

            Assert.AreEqual(2, languages.Count);
            Assert.AreEqual("ja-JP", languages["Default(日本語)"]);
            Assert.AreEqual("en-US", languages["Custom English"]);
            Dictionary<string, string> missing = JsonLanguageCatalog.DiscoverLanguages(Path.Combine(directory, "missing"));
            Assert.AreEqual(1, missing.Count);
            Assert.AreEqual("ja-JP", missing["Default(日本語)"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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

    private static Dictionary<string, string> ReadResxStringValues(string path)
    {
        return XDocument.Load(path).Root!.Elements("data")
            .Where(element => element.Attribute("type") == null && element.Attribute("mimetype") == null)
            .ToDictionary(
                element => (string)element.Attribute("name")!,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static JObject ReadLanguageJsonObject(string path)
    {
        var token = JToken.Parse(File.ReadAllText(path), new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
        });
        Assert.IsInstanceOfType<JObject>(token, Path.GetFileName(path) + " は JSON オブジェクトである必要があります。");
        return (JObject)token;
    }

    private static HashSet<int> ReadFormatArgumentIndices(string sourceName, string key, string value)
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(value), sourceName + " " + key + " は空にできません。");
        var usedArguments = new HashSet<int>();
        try
        {
            var format = CompositeFormat.Parse(value);
            object[] arguments = Enumerable.Range(0, format.MinimumArgumentCount)
                .Select(index => (object)new FormatArgument(index, usedArguments))
                .ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, format, arguments);
        }
        catch (FormatException exception)
        {
            Assert.Fail(sourceName + " " + key + " の複合書式が不正です: " + exception.Message);
        }
        return usedArguments;
    }

    // .NET の書式処理で実際に参照された番号だけを集め、エスケープされた波括弧や訳文の語順を制限しません。
    private sealed class FormatArgument(int index, HashSet<int> usedArguments) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            usedArguments.Add(index);
            return string.Empty;
        }
    }

    private static void AssertSetEquals(HashSet<string> expected, HashSet<string> actual, string message)
    {
        string[] missing = [.. expected.Except(actual, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal)];
        string[] extra = [.. actual.Except(expected, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal)];
        Assert.IsTrue(
            missing.Length == 0 && extra.Length == 0,
            message + Environment.NewLine + "不足: " + string.Join(", ", missing)
                + Environment.NewLine + "余分: " + string.Join(", ", extra));
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
        throw new DirectoryNotFoundException("リポジトリのルートが見つかりません。");
    }
}
