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
    public void DefaultsContainSixNamedWebActionsInPersistedOrder()
    {
        RightClickActionSettings settings = RightClickActionSettingsDefaults.Create();

        Assert.AreEqual(6, settings.WebActions.Count);
        Assert.AreEqual(0, settings.ProgramActions.Count);
        CollectionAssert.AreEqual(
            new[] { "bms-ir", "mocha", "minir", "rianir", "stellaverse-ir", "kaleid-ir" },
            settings.WebActions.Select(action => action.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "BMS-IR", "Mocha", "MinIR", "rianIR", "STELLAVERSE IR", "Kaleid IR" },
            settings.WebActions.Select(action => action.Name).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "https://bms-ir.org/new/song?songmd5={md5}&view=both",
                "https://mocha-repository.info/song.php?sha256={sha256}",
                "https://www.gaftalk.com/minir/#/viewer/song/{sha256}/0",
                "https://rianir.link/ranking?sha256={sha256}",
                "https://ir.stellabms.xyz/charts/{md5}",
                "https://kaleidir.com/charts/{sha256}"
            },
            settings.WebActions.Select(action => action.UrlTemplate).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ExternalChartKind.BmsOnly,
                ExternalChartKind.All,
                ExternalChartKind.All,
                ExternalChartKind.All,
                ExternalChartKind.All,
                ExternalChartKind.All
            },
            settings.WebActions.Select(action => action.ChartKind).ToArray());
        Assert.IsTrue(settings.WebActions.All(action => action.Enabled));
        Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, RightClickActionSettingsSerializer.Serialize(settings));
    }

    [TestMethod]
    public void StoreLoadsAndSavesThroughTheProvidedSettingsOwnerWithoutFallback()
    {
        Settings settings = new()
        {
            RightClickActionsJson = "{\"webActions\":[],\"programActions\":[]}"
        };
        var store = new RightClickActionSettingsStore(() => settings);

        RightClickActionSettingsParseResult loaded = store.Load();
        Assert.IsTrue(loaded.Succeeded);
        Assert.AreEqual(0, loaded.Settings.WebActions.Count);
        Assert.AreEqual(0, loaded.Settings.ProgramActions.Count);

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
    public void ParseRejectsMissingAndBlankSerializedValues()
    {
        foreach (string? value in new string?[] { null, string.Empty, "   " })
        {
            RightClickActionSettingsParseResult result = RightClickActionSettingsSerializer.Parse(value);
            Assert.IsFalse(result.Succeeded);
            Assert.IsNull(result.Settings);
            Assert.AreEqual(RightClickActionSettingsParseErrorKind.InvalidValue, result.Error.Kind);
        }
    }

    [TestMethod]
    public void SerializeRoundTripPreservesOrderAndDefinitions()
    {
        string payload = """
        {"webActions":[{"id":"custom","name":"Custom","urlTemplate":"https://example.test/{md5}/{sha256}","enabled":false,"chartKind":"BmsonOnly"}],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"--chart=\"{filePath}\"","enabled":true},{"id":"player","name":"Player","executablePath":"C:\\Tools\\player.exe","argumentTemplate":"{filePath} --mode=preview","enabled":false}]}
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
        Assert.AreEqual("player", roundTrip.Settings.ProgramActions[1].Id);
        Assert.AreEqual("Player", roundTrip.Settings.ProgramActions[1].Name);
        Assert.AreEqual(@"C:\Tools\player.exe", roundTrip.Settings.ProgramActions[1].ExecutablePath);
        Assert.AreEqual("{filePath} --mode=preview", roundTrip.Settings.ProgramActions[1].ArgumentTemplate);
        Assert.IsFalse(roundTrip.Settings.ProgramActions[1].Enabled);
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
    public void ParseRequiresNonblankNameForEveryWebAction()
    {
        string literal = """
        {"webActions":[{"id":"custom","name":"  Literal name  ","urlTemplate":"https://example.test/{md5}","enabled":true,"chartKind":"All"}],"programActions":[]}
        """;
        RightClickActionSettingsParseResult literalResult = RightClickActionSettingsSerializer.Parse(literal);
        Assert.IsTrue(literalResult.Succeeded, literalResult.Error?.ToString());
        Assert.AreEqual("  Literal name  ", literalResult.Settings.WebActions[0].Name);

        foreach (string invalid in new[]
        {
            "{\"webActions\":[{\"id\":\"bms-ir\",\"name\":null,\"urlTemplate\":\"https://example.test/{md5}\",\"enabled\":true,\"chartKind\":\"BmsOnly\"}],\"programActions\":[]}",
            "{\"webActions\":[{\"id\":\"bms-ir\",\"urlTemplate\":\"https://example.test/{md5}\",\"enabled\":true,\"chartKind\":\"BmsOnly\"}],\"programActions\":[]}",
            "{\"webActions\":[{\"id\":\"bms-ir\",\"name\":\"   \",\"urlTemplate\":\"https://example.test/{md5}\",\"enabled\":true,\"chartKind\":\"BmsOnly\"}],\"programActions\":[]}"
        })
        {
            RightClickActionSettingsParseResult result = RightClickActionSettingsSerializer.Parse(invalid);
            Assert.IsFalse(result.Succeeded, invalid);
            Assert.IsNull(result.Settings, invalid);
        }
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
            """{"webActions":[],"programActions":[{"id":"viewer","name":"   ","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"{filePath}","enabled":true}]}""",
            """{"webActions":[],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"   ","argumentTemplate":"{filePath}","enabled":true}]}""",
            """{"webActions":[],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"--unknown={other}","enabled":true}]}""",
            """{"webActions":[],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"{filePath,{filePath} }","enabled":true}]}""",
            """{"webActions":[],"programActions":[{"id":"viewer","name":"Viewer","executablePath":"C:\\Tools\\viewer.exe","argumentTemplate":"   ","enabled":true}]}""",
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

        RightClickActionResolution unknownKindResult = RightClickActionResolver.Resolve(
            parsed.Settings,
            new RightClickActionResolutionInput(new string('A', 32), new string('B', 64), null, null));
        CollectionAssert.AreEqual(new[] { "both" }, unknownKindResult.WebActions.Select(action => action.Id).ToArray());

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
