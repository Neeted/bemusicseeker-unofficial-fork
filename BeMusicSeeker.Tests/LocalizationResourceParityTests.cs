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
