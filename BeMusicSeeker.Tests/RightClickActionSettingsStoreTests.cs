using System;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RightClickActionSettingsStoreTests
{
    [TestMethod]
    public void ParseMissingPropertyReturnsExactBuiltInDefaults()
    {
        RightClickActionSettingsParseResult result = RightClickActionSettingsSerializer.Parse(null);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.IsMissing);
        Assert.IsFalse(result.IsExplicitEmpty);
        Assert.AreEqual(5, result.Settings.WebActions.Count);
        Assert.AreEqual(0, result.Settings.ProgramActions.Count);
        CollectionAssert.AreEqual(
            new[] { "bms-ir", "mocha", "minir", "rianir", "stellaverse-ir" },
            result.Settings.WebActions.Select(action => action.Id).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                RightClickActionSettingsDefaults.BmsIrUrl,
                RightClickActionSettingsDefaults.MochaUrl,
                RightClickActionSettingsDefaults.MinIrUrl,
                RightClickActionSettingsDefaults.RianIrUrl,
                RightClickActionSettingsDefaults.StellaverseIrUrl
            },
            result.Settings.WebActions.Select(action => action.UrlTemplate).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ExternalChartKind.BmsOnly,
                ExternalChartKind.All,
                ExternalChartKind.All,
                ExternalChartKind.All,
                ExternalChartKind.All
            },
            result.Settings.WebActions.Select(action => action.ChartKind).ToArray());
        Assert.IsTrue(result.Settings.WebActions.All(action => action.Enabled));
        Assert.IsTrue(result.Settings.WebActions.All(action => action.Name == null));
    }

    [TestMethod]
    public void StoreLoadsAndSavesThroughTheProvidedSettingsOwnerWithoutFallback()
    {
        Settings settings = new()
        {
            RightClickActionsJson = string.Empty
        };
        var store = new RightClickActionSettingsStore(() => settings);

        RightClickActionSettingsParseResult loaded = store.Load();
        Assert.IsTrue(loaded.Succeeded);
        Assert.IsTrue(loaded.IsExplicitEmpty);

        RightClickActionSettings replacement = RightClickActionSettingsDefaults.Create();
        Assert.IsTrue(store.TrySave(replacement, out RightClickActionSettingsParseError saveError), saveError?.ToString());
        Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, settings.RightClickActionsJson);

        string beforeInvalidSave = settings.RightClickActionsJson;
        RightClickActionSettings invalid = new(
            [new RightClickWebActionDefinition(
                "invalid",
                "Invalid",
                "ftp://example.test/{md5}",
                true,
                ExternalChartKind.All)],
            []);

        Assert.IsFalse(store.TrySave(invalid, out RightClickActionSettingsParseError invalidError));
        Assert.IsNotNull(invalidError);
        Assert.AreEqual(beforeInvalidSave, settings.RightClickActionsJson);
    }

    [TestMethod]
    public void ParseExplicitEmptyDoesNotReseedDefaults()
    {
        RightClickActionSettingsParseResult result = RightClickActionSettingsSerializer.Parse(string.Empty);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.IsMissing);
        Assert.IsTrue(result.IsExplicitEmpty);
        Assert.AreEqual(0, result.Settings.WebActions.Count);
        Assert.AreEqual(0, result.Settings.ProgramActions.Count);
    }

    [TestMethod]
    public void SerializeRoundTripPreservesOrderAndDefinitions()
    {
        string payload = """
        {"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{md5}/{sha256}","enabled":false,"chartKind":"BmsonOnly"}],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"--chart=\"{filePath}\"","enabled":true}]}
        """;

        RightClickActionSettingsParseResult parsed = RightClickActionSettingsSerializer.Parse(payload);
        Assert.IsTrue(parsed.Succeeded, parsed.Error?.ToString());

        string serialized = RightClickActionSettingsSerializer.Serialize(parsed.Settings);
        RightClickActionSettingsParseResult roundTrip = RightClickActionSettingsSerializer.Parse(serialized);

        Assert.IsTrue(roundTrip.Succeeded, roundTrip.Error?.ToString());
        Assert.AreEqual("custom", roundTrip.Settings.WebActions[0].Id);
        Assert.AreEqual("Custom", roundTrip.Settings.WebActions[0].Name);
        Assert.AreEqual(ExternalChartKind.BmsonOnly, roundTrip.Settings.WebActions[0].ChartKind);
        Assert.IsFalse(roundTrip.Settings.WebActions[0].Enabled);
        Assert.AreEqual("viewer", roundTrip.Settings.ProgramActions[0].Id);
        Assert.AreEqual(@"C:\Tools\viewer.exe", roundTrip.Settings.ProgramActions[0].ExecutablePath);
        Assert.AreEqual("--chart=\"{filePath}\"", roundTrip.Settings.ProgramActions[0].ArgumentTemplate);
    }

    [TestMethod]
    public void ParseCorruptUnknownAndDuplicateJsonReturnsTypedFailureWithoutDefaults()
    {
        RightClickActionSettingsParseResult corrupt = RightClickActionSettingsSerializer.Parse("not-json");
        Assert.IsFalse(corrupt.Succeeded);
        Assert.IsNull(corrupt.Settings);
        Assert.AreEqual(RightClickActionSettingsParseErrorKind.InvalidJson, corrupt.Error.Kind);

        RightClickActionSettingsParseResult unknown = RightClickActionSettingsSerializer.Parse(
            """{"webActions":[],"programActions":[],"unexpected":true}""");
        Assert.IsFalse(unknown.Succeeded);
        Assert.AreEqual(RightClickActionSettingsParseErrorKind.UnknownProperty, unknown.Error.Kind);
        Assert.AreEqual("unexpected", unknown.Error.Path);

        RightClickActionSettingsParseResult duplicate = RightClickActionSettingsSerializer.Parse(
            """{"webActions":[],"webActions":[],"programActions":[]}""");
        Assert.IsFalse(duplicate.Succeeded);
        Assert.AreEqual(RightClickActionSettingsParseErrorKind.DuplicateProperty, duplicate.Error.Kind);
    }

    [TestMethod]
    public void ParseRejectsTrailingJsonContentAsTypedFailure()
    {
        RightClickActionSettingsParseResult result = RightClickActionSettingsSerializer.Parse(
            "{\"webActions\":[],\"programActions\":[]} {\"unexpected\":true}");

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Settings);
        Assert.AreEqual(RightClickActionSettingsParseErrorKind.InvalidJson, result.Error.Kind);
    }

    [TestMethod]
    public void ParseDuplicateIdAndInvalidEnumReturnTypedFailure()
    {
        string duplicateId = """
        {"webActions":[{"id":"same","name":"Web","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"All"}],"programActions":[{"id":"same","name":"Program","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"{filePath}","enabled":true}]}
        """;
        RightClickActionSettingsParseResult duplicate = RightClickActionSettingsSerializer.Parse(duplicateId);
        Assert.IsFalse(duplicate.Succeeded);
        Assert.AreEqual(RightClickActionSettingsParseErrorKind.InvalidValue, duplicate.Error.Kind);
        StringAssert.Contains(duplicate.Error.Message, "Duplicate action id");

        string invalidEnum = """
        {"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"all"}],"programActions":[]}
        """;
        RightClickActionSettingsParseResult enumResult = RightClickActionSettingsSerializer.Parse(invalidEnum);
        Assert.IsFalse(enumResult.Succeeded);
        Assert.AreEqual(RightClickActionSettingsParseErrorKind.InvalidValue, enumResult.Error.Kind);

        string numericEnum = """
        {"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"0"}],"programActions":[]}
        """;
        RightClickActionSettingsParseResult numericResult = RightClickActionSettingsSerializer.Parse(numericEnum);
        Assert.IsFalse(numericResult.Succeeded);
        Assert.AreEqual(RightClickActionSettingsParseErrorKind.InvalidValue, numericResult.Error.Kind);
    }

    [TestMethod]
    public void ParseCustomAndBuiltInNameOverrideSemanticsAreDistinct()
    {
        string custom = """
        {"webActions":[{"id":"custom","name":"  Literal name  ","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"All"}],"programActions":[]}
        """;
        RightClickActionSettingsParseResult customResult = RightClickActionSettingsSerializer.Parse(custom);
        Assert.IsTrue(customResult.Succeeded, customResult.Error?.ToString());
        Assert.AreEqual("  Literal name  ", customResult.Settings.WebActions[0].Name);

        string builtIn = """
        {"webActions":[{"id":"bms-ir","name":null,"urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"BmsOnly"}],"programActions":[]}
        """;
        RightClickActionSettingsParseResult builtInResult = RightClickActionSettingsSerializer.Parse(builtIn);
        Assert.IsTrue(builtInResult.Succeeded, builtInResult.Error?.ToString());
        Assert.IsNull(builtInResult.Settings.WebActions[0].Name);
    }

    [TestMethod]
    public void ParseRejectsInvalidUrlsAndProgramDefinitions()
    {
        string[] invalidPayloads =
        [
            """{"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/fixed","enabled":true,"chartKind":"All"}],"programActions":[]}""",
            """{"webActions":[{"id":"custom","name":"Custom","urlTemplate":"ftp://example.test/{md5}","enabled":true,"chartKind":"All"}],"programActions":[]}""",
            """{"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https:example.test/{md5}","enabled":true,"chartKind":"All"}],"programActions":[]}""",
            """{"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{MD5}","enabled":true,"chartKind":"All"}],"programActions":[]}""",
            """{"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{md5","enabled":true,"chartKind":"All"}],"programActions":[]}""",
            """{"webActions":[],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"tools\\viewer.exe","argumentTemplate":"{filePath}","enabled":true}]}""",
            """{"webActions":[],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"--unknown={other}","enabled":true}]}""",
            """{"webActions":[],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"\"{filePath}","enabled":true}]}"""
        ];

        foreach (string payload in invalidPayloads)
        {
            RightClickActionSettingsParseResult result = RightClickActionSettingsSerializer.Parse(payload);
            Assert.IsFalse(result.Succeeded, payload);
            Assert.IsNull(result.Settings, payload);
            Assert.IsNotNull(result.Error, payload);
        }
    }

    [TestMethod]
    public void ResolverExpandsLowercaseHashesAndHonorsChartKindAndOrder()
    {
        string payload = """
        {"webActions":[{"id":"md5","name":"MD5","urlTemplate":"https://example.test/md5/{md5}","enabled":true,"chartKind":"BmsOnly"},{"id":"sha","name":"SHA","urlTemplate":"https://example.test/sha/{sha256}","enabled":true,"chartKind":"BmsonOnly"},{"id":"both","name":"Both","urlTemplate":"https://example.test/{md5}/{sha256}","enabled":true,"chartKind":"All"}],"programActions":[]}
        """;
        RightClickActionSettingsParseResult parsed = RightClickActionSettingsSerializer.Parse(payload);
        Assert.IsTrue(parsed.Succeeded, parsed.Error?.ToString());

        RightClickActionResolution bmsResult = RightClickActionResolver.Resolve(
            parsed.Settings,
            new RightClickActionResolutionInput(new string('A', 32), new string('B', 64), null, ExternalChartKind.BmsOnly));
        CollectionAssert.AreEqual(new[] { "md5", "both" }, bmsResult.WebActions.Select(action => action.Id).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "https://example.test/md5/" + new string('a', 32),
                "https://example.test/" + new string('a', 32) + "/" + new string('b', 64)
            },
            bmsResult.WebActions.Select(action => action.Url).ToArray());

        RightClickActionResolution bmsonResult = RightClickActionResolver.Resolve(
            parsed.Settings,
            new RightClickActionResolutionInput(new string('A', 32), new string('B', 64), null, ExternalChartKind.BmsonOnly));
        CollectionAssert.AreEqual(new[] { "sha", "both" }, bmsonResult.WebActions.Select(action => action.Id).ToArray());

        RightClickActionResolution missingHashResult = RightClickActionResolver.Resolve(
            parsed.Settings,
            new RightClickActionResolutionInput("not-md5", new string('B', 64), null, ExternalChartKind.BmsOnly));
        Assert.AreEqual(0, missingHashResult.WebActions.Count);
    }

    [TestMethod]
    public void ResolverReturnsEnabledProgramsOnlyForAbsoluteLocalPathWithoutCheckingExistence()
    {
        string payload = """
        {"webActions":[],"programActions":[{"id":"first","name":"First","executablePath":"C:\\Tools\\first.exe","argumentTemplate":"--file=\"{filePath}\"","enabled":true},{"id":"disabled","name":"Disabled","executablePath":"C:\\Tools\\disabled.exe","argumentTemplate":"{filePath}","enabled":false}]}
        """;
        RightClickActionSettingsParseResult parsed = RightClickActionSettingsSerializer.Parse(payload);
        Assert.IsTrue(parsed.Succeeded, parsed.Error?.ToString());

        RightClickActionResolution result = RightClickActionResolver.Resolve(
            parsed.Settings,
            new RightClickActionResolutionInput(null, null, @"C:\譜面\chart.bms", ExternalChartKind.BmsOnly));
        Assert.AreEqual(1, result.ProgramActions.Count);
        Assert.AreEqual("first", result.ProgramActions[0].Id);
        Assert.AreEqual(@"C:\Tools\first.exe", result.ProgramActions[0].ExecutablePath);
        CollectionAssert.AreEqual(
            new[] { @"--file=C:\譜面\chart.bms" },
            result.ProgramActions[0].Arguments.ToArray());

        RightClickActionResolution noPathResult = RightClickActionResolver.Resolve(
            parsed.Settings,
            new RightClickActionResolutionInput(null, null, "relative\\chart.bms", ExternalChartKind.BmsOnly));
        Assert.AreEqual(0, noPathResult.ProgramActions.Count);
    }
}
