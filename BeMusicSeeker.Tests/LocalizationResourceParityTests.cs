using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

/// <summary>表示リソース全体のキー、文字列値、書式引数の対応を検証します。</summary>
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

    /// <summary>全言語の辞書が同じキーと書式引数を持ち、非空の文字列だけを含むことを確認します。</summary>
    [TestMethod]
    public void LanguageJsonFiles_HaveSameKeysAsGeneratedResources()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, string> resx = ReadResxStringValues(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        Dictionary<string, HashSet<int>> expectedArguments = resx.ToDictionary(
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
            HashSet<string> keys = language.Properties()
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

                HashSet<int> actualArguments = ReadFormatArgumentIndices(sourceName, property.Name, value);
                Assert.IsTrue(
                    expectedArguments[property.Name].SetEquals(actualArguments),
                    sourceName + " " + property.Name + " の書式引数番号は Resources.resx と一致する必要があります。");
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
        JToken token = JToken.Parse(File.ReadAllText(path), new JsonLoadSettings
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
            CompositeFormat format = CompositeFormat.Parse(value);
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
