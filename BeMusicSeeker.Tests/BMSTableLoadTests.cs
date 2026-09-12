using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BMSTableLoadTests
{
    [TestMethod]
    public void LoadHeaderJson_StoresPassedHeaderUri()
    {
        var table = new BMSTable();
        var pageUri = new Uri("https://example.com/table.html");
        var headerUri = new Uri("https://example.com/header.json");

        table.LoadHeaderJSON("{\"name\":\"Test\",\"symbol\":\"T\",\"data_url\":\"score.json\",\"level_order\":[1]}", pageUri, headerUri);

        Assert.AreEqual(pageUri, table.Page_url);
        Assert.AreEqual(headerUri, table.Header_url);
        Assert.AreEqual("score.json", table.data_url);
    }

    [TestMethod]
    public void LoadHeaderJson_PreservesInnerException()
    {
        var table = new BMSTable();

        PlaylistHeaderParseException exception = Assert.ThrowsException<PlaylistHeaderParseException>(() => table.LoadHeaderJSON("{ invalid json }"));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void LoadDataJson_PreservesInnerException()
    {
        var table = new BMSTable();

        PlaylistDataParseException exception = Assert.ThrowsException<PlaylistDataParseException>(() => table.LoadDataJSON("{ invalid json }"));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void LoadDataJson_WrongTopLevelShapePreservesParseContract()
    {
        var table = new BMSTable();

        PlaylistDataParseException exception = Assert.ThrowsException<PlaylistDataParseException>(
            () => table.LoadDataJSON("{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}"));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void LoadDataJson_PreservesCaseSensitiveFieldsAndOptionalNulls()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"Typed\",\"symbol\":\"st\",\"data_url\":\"data.json\"}");
        table.LoadDataJSON("[{\"MD5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Target\",\"level\":2.5,\"comment\":null,\"org_md5s\":[\"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\"],\"unknown\":true}]");

        BMSTableEntry entry = table.entries.Single();
        Assert.IsNull(entry.md5);
        Assert.AreEqual("Target", entry.title);
        Assert.AreEqual(2.5d, entry.level);
        Assert.AreEqual("st2.5", entry.folder);
        CollectionAssert.AreEqual(new[] { "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }, entry.Org_md5);
    }

    [TestMethod]
    public void LoadDataJson_ExplicitNullOrgLevelKeepsNullLevelAndUsesCompatibleFolder()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"Typed\",\"symbol\":\"st\",\"data_url\":\"data.json\"}");
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"org_level\":null,\"level\":\"2\"}]");

        BMSTableEntry entry = table.entries.Single();
        Assert.IsNull(entry.level);
        Assert.AreEqual("st2", entry.folder);
    }

    [TestMethod]
    public void LoadDataJson_InvalidOrgLevelShapePreservesParseContract()
    {
        var table = new BMSTable();

        PlaylistDataParseException exception = Assert.ThrowsException<PlaylistDataParseException>(
            () => table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"org_level\":{}}]"));

        Assert.IsNotNull(exception.InnerException);
    }

    [DataTestMethod]
    [DataRow("\"1\"")]
    [DataRow("true")]
    [DataRow("[]")]
    [DataRow("{}")]
    public void LoadDataJson_NonNumericOrgLevelPreservesParseContract(string orgLevelJson)
    {
        var table = new BMSTable();

        PlaylistDataParseException exception = Assert.ThrowsException<PlaylistDataParseException>(
            () => table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"org_level\":" + orgLevelJson + "}]"));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void LoadHeaderJson_ExplicitNullEntryTypePreservesParseContract()
    {
        var table = new BMSTable();

        PlaylistHeaderParseException exception = Assert.ThrowsException<PlaylistHeaderParseException>(
            () => table.LoadHeaderJSON("{\"name\":\"Typed\",\"symbol\":\"st\",\"entry_type\":null,\"data_url\":\"data.json\"}"));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void LoadJson_PreservesIsoDateStringsAsStringsAtJsonBoundary()
    {
        const string timestamp = "2026-01-02T03:04:05.000Z";
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"Typed\",\"symbol\":\"st\",\"tag\":\"" + timestamp + "\",\"data_url\":\"data.json\"}");
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"" + timestamp + "\",\"comment\":\"" + timestamp + "\"}]");

        Assert.AreEqual(timestamp, table.tag);
        BMSTableEntry entry = table.entries.Single();
        Assert.AreEqual(timestamp, entry.title);
        Assert.AreEqual(timestamp, entry.comment);
    }

    [TestMethod]
    public void PlaylistJson_WireFormatMatchesIndependentIndentedGolden()
    {
        var entry = new BMSTableEntry(JObject.Parse("{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"org_level\":1.5,\"title\":\"Title \\\"quoted\\\"\",\"artist\":\"Artist\",\"folder\":\"st1\",\"level\":\"1\",\"lr2_bmsid\":\"123\",\"url\":\"https://example.com/u\",\"url_diff\":\"https://example.com/d\",\"name_diff\":\"Diff\",\"org_md5s\":[\"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\"],\"comment\":\"Comment\"}"));
        entry.adddate = new DateTime(2024, 1, 2);

        string actual = entry.ToJson().Replace(entry.adddate.ToShortDateString(), "<DATE>", StringComparison.Ordinal);
        string expected = "{" + Environment.NewLine
            + "  \"md5\": \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," + Environment.NewLine
            + "  \"sha256\": \"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"," + Environment.NewLine
            + "  \"org_level\": 1.5," + Environment.NewLine
            + "  \"title\": \"Title \\\"quoted\\\"\"," + Environment.NewLine
            + "  \"artist\": \"Artist\"," + Environment.NewLine
            + "  \"folder\": \"st1\"," + Environment.NewLine
            + "  \"level\": \"st1\"," + Environment.NewLine
            + "  \"lr2_bmsid\": \"123\"," + Environment.NewLine
            + "  \"url\": \"https://example.com/u\"," + Environment.NewLine
            + "  \"url_diff\": \"https://example.com/d\"," + Environment.NewLine
            + "  \"name_diff\": \"Diff\"," + Environment.NewLine
            + "  \"org_md5s\": [" + Environment.NewLine
            + "    \"cccccccccccccccccccccccccccccccc\"" + Environment.NewLine
            + "  ]," + Environment.NewLine
            + "  \"org_md5\": \"cccccccccccccccccccccccccccccccc\"," + Environment.NewLine
            + "  \"comment\": \"Comment\"," + Environment.NewLine
            + "  \"adddate\": \"<DATE>\"" + Environment.NewLine
            + "}";

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void PlaylistPersistedJson_UsesIndependentIndentedArrayGolden()
    {
        var table = new BMSTable
        {
            Folder_order = ["st2", "st1"]
        };
        var entry = new BMSTableEntry
        {
            Org_md5 = ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"]
        };

        string expected = "[" + Environment.NewLine
            + "  \"st2\"," + Environment.NewLine
            + "  \"st1\"" + Environment.NewLine
            + "]";
        string expectedOrgMd5 = "[" + Environment.NewLine
            + "  \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," + Environment.NewLine
            + "  \"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"" + Environment.NewLine
            + "]";

        Assert.AreEqual(expected, table.folder_order);
        Assert.AreEqual(expectedOrgMd5, entry.org_md5);
    }

    [TestMethod]
    public void PlaylistHeaderJson_MatchesIndependentFullGoldenAfterVariableNormalization()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"Table\",\"symbol\":\"st\",\"tag\":\"tag\",\"compat_prefix\":\"st\",\"folder_sort_key\":\"\",\"folder_sort_ascending\":true,\"data_url\":\"data.json\",\"folder_order\":[\"st1\"],\"level_order\":[1],\"course\":[{\"name\":\"Course\",\"constraint\":[\"grade_mirror\"],\"md5\":[\"11111111111111111111111111111111\"]}]}");
        table.last_update = new DateTime(2024, 1, 2);

        JObject normalized = JObject.Parse(table.HeaderToJson());
        normalized["last_update"] = "<LAST_UPDATE>";
        normalized["editor_version"] = "<EDITOR_VERSION>";
        normalized["output_date"] = "<OUTPUT_DATE>";

        string expected = "{" + Environment.NewLine
            + "  \"name\": \"Table\"," + Environment.NewLine
            + "  \"symbol\": \"st\"," + Environment.NewLine
            + "  \"level_order\": []," + Environment.NewLine
            + "  \"folder_order\": [" + Environment.NewLine
            + "    \"st1\"" + Environment.NewLine
            + "  ]," + Environment.NewLine
            + "  \"folder_sort_key\": \"\"," + Environment.NewLine
            + "  \"folder_sort_ascending\": true," + Environment.NewLine
            + "  \"entry_type\": \"\"," + Environment.NewLine
            + "  \"data_url\": \"data.json\"," + Environment.NewLine
            + "  \"tag\": \"tag\"," + Environment.NewLine
            + "  \"course\": [" + Environment.NewLine
            + "    {" + Environment.NewLine
            + "      \"name\": \"Course\"," + Environment.NewLine
            + "      \"constraint\": [" + Environment.NewLine
            + "        \"grade_mirror\"" + Environment.NewLine
            + "      ]," + Environment.NewLine
            + "      \"md5\": [" + Environment.NewLine
            + "        \"11111111111111111111111111111111\"" + Environment.NewLine
            + "      ]" + Environment.NewLine
            + "    }" + Environment.NewLine
            + "  ]," + Environment.NewLine
            + "  \"compat_prefix\": \"st\"," + Environment.NewLine
            + "  \"last_update\": \"<LAST_UPDATE>\"," + Environment.NewLine
            + "  \"editor_name\": \"BeMusicSeeker\"," + Environment.NewLine
            + "  \"editor_version\": \"<EDITOR_VERSION>\"," + Environment.NewLine
            + "  \"output_date\": \"<OUTPUT_DATE>\"" + Environment.NewLine
            + "}";

        Assert.AreEqual(expected, normalized.ToString(Formatting.Indented));
    }

    [TestMethod]
    public void PlaylistDataJson_MatchesIndependentFullGoldenAfterDateNormalization()
    {
        var entry = new BMSTableEntry(JObject.Parse("{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"folder\":\"st1\",\"org_level\":null}"));
        entry.adddate = new DateTime(2024, 1, 2);
        var table = new BMSTable
        {
            Folder_order = ["st1"],
            entries = [entry]
        };

        string actual = table.DataToJson().Replace(entry.adddate.ToShortDateString(), "<DATE>", StringComparison.Ordinal);
        string expected = "[" + Environment.NewLine
            + "  {" + Environment.NewLine
            + "    \"md5\": \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," + Environment.NewLine
            + "    \"sha256\": null," + Environment.NewLine
            + "    \"org_level\": null," + Environment.NewLine
            + "    \"title\": \"Song\"," + Environment.NewLine
            + "    \"artist\": \"\"," + Environment.NewLine
            + "    \"folder\": \"st1\"," + Environment.NewLine
            + "    \"level\": \"st1\"," + Environment.NewLine
            + "    \"lr2_bmsid\": null," + Environment.NewLine
            + "    \"url\": \"\"," + Environment.NewLine
            + "    \"url_diff\": \"\"," + Environment.NewLine
            + "    \"name_diff\": null," + Environment.NewLine
            + "    \"org_md5s\": []," + Environment.NewLine
            + "    \"org_md5\": \"\"," + Environment.NewLine
            + "    \"comment\": \"\"," + Environment.NewLine
            + "    \"adddate\": \"<DATE>\"" + Environment.NewLine
            + "  }" + Environment.NewLine
            + "]";

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void LoadHeaderJson_RejectsTrailingJsonDocument()
    {
        var table = new BMSTable();

        PlaylistHeaderParseException exception = Assert.ThrowsException<PlaylistHeaderParseException>(
            () => table.LoadHeaderJSON("{\"name\":\"Typed\",\"symbol\":\"st\"}{\"extra\":true}"));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void LoadDataJson_RejectsTrailingJsonDocument()
    {
        var table = new BMSTable();

        PlaylistDataParseException exception = Assert.ThrowsException<PlaylistDataParseException>(
            () => table.LoadDataJSON("[]{}"));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void NormalizeOutputDirectoryName_ResolvesRelativeSegmentsInsideManagedBase()
    {
        Assert.AreEqual(Path.Combine("Parent", "Child"), BMSTable.NormalizeOutputDirectoryName("Parent/./Child"));
        Assert.AreEqual("Child", BMSTable.NormalizeOutputDirectoryName("Parent/../Child"));
        Assert.AreEqual("Sibling", BMSTable.NormalizeOutputDirectoryName("../Sibling"));
        Assert.IsNull(BMSTable.NormalizeOutputDirectoryName("../.."));
    }

    [TestMethod]
    public void LoadHeaderJson_InvalidDataUrlThrowsInvalidOperationException()
    {
        var table = new BMSTable();

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() => table.LoadHeaderJSON("{\"name\":\"Test\",\"symbol\":\"T\",\"data_url\":\"http://[broken\",\"level_order\":[1]}"));

        StringAssert.Contains(exception.Message, "Failed to resolve playlist data_url");
    }

    [TestMethod]
    public void StoredDataUrlSetter_InvalidValueDoesNotThrowAndKeepsNull()
    {
        var table = new BMSTable();
        PropertyInfo propertyInfo = typeof(BMSTable).GetProperty("data_url", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        propertyInfo.SetValue(table, "http://[broken");

        Assert.IsNull(table.Data_url);
    }

    [TestMethod]
    public void LoadHeaderJson_StoresTagAndCoursesForRoundTrip()
    {
        var table = new BMSTable();

        table.LoadHeaderJSON("{\"name\":\"Stella\",\"symbol\":\"sl\",\"tag\":\"st\",\"data_url\":\"data.json\",\"level_order\":[0],\"course\":[[{\"name\":\"段位\",\"constraint\":[\"grade_mirror\"],\"md5\":[\"11111111111111111111111111111111\"]}]]}");

        Assert.AreEqual("st", table.tag);
        Assert.AreEqual(1, table.Courses.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(table.header_sha256));
        StringAssert.Contains(table.HeaderToJson(), "\"tag\": \"st\"");
        StringAssert.Contains(table.HeaderToJson(), "\"course\"");
        StringAssert.Contains(table.HeaderToJson(), "\"grade_mirror\"");
    }

    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromNumericLevelOrderSymbol()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st1" }, table.Folder_order);
        Assert.AreEqual("st1", table.entries[0].folder);
        CollectionAssert.AreEqual(new[] { "st1" }, table.folder_list);
    }

    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromNumericLevelOrderJapaneseSymbol()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Insane\",\"symbol\":\"★\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("★", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "★1" }, table.Folder_order);
        Assert.AreEqual("★1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromSymbolicLevelOrder()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"level_order\":[\"st1\"]}", "st1");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "stst1" }, table.Folder_order);
        Assert.AreEqual("stst1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromTagBeforeSymbol()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"sl\",\"tag\":\"st\",\"data_url\":\"score.json\",\"level_order\":[\"X\"]}", "X");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "stX" }, table.Folder_order);
        Assert.AreEqual("stX", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_NonCp932TagUsesLegacyPrefixWithoutTryingSymbol()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Unicode\",\"symbol\":\"st\",\"tag\":\"😀\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("LEVEL ", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "LEVEL 1" }, table.Folder_order);
        Assert.AreEqual("LEVEL 1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_NonCp932SymbolUsesLegacyPrefix()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Unicode\",\"symbol\":\"😀\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("LEVEL ", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "LEVEL 1" }, table.Folder_order);
        Assert.AreEqual("LEVEL 1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_MissingTagAndSymbolUsesLegacyPrefix()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"NoPrefix\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("LEVEL ", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "LEVEL 1" }, table.Folder_order);
        Assert.AreEqual("LEVEL 1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromSignedLevelOrder()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"level_order\":[\"-5\",0,1]}", "-5");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st-5", "st0", "st1" }, table.Folder_order);
        Assert.AreEqual("st-5", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_ExplicitEmptyCompatPrefixPreventsInference()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"compat_prefix\":\"\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual(string.Empty, table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "1" }, table.Folder_order);
        Assert.AreEqual("1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_ExplicitEmptyCompatPrefixKeepsRawFolderOrder()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"FolderOrder\",\"symbol\":\"st\",\"compat_prefix\":\"\",\"data_url\":\"score.json\",\"folder_order\":[\"2\",\"1\"]}");
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song A\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song B\",\"level\":\"2\"}]");

        Assert.AreEqual(string.Empty, table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "2", "1" }, table.Folder_order);
        CollectionAssert.AreEqual(new[] { "2", "1" }, table.folder_list);
    }

    [TestMethod]
    public void LoadHeaderJson_ExplicitLevelCompatPrefixKeepsLegacyFolderNames()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Legacy\",\"symbol\":\"st\",\"compat_prefix\":\"LEVEL \",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("LEVEL ", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "LEVEL 1" }, table.Folder_order);
        Assert.AreEqual("LEVEL 1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_ExplicitNonCp932CompatPrefixIsPreserved()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Explicit\",\"symbol\":\"st\",\"compat_prefix\":\"😀\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("😀", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "😀1" }, table.Folder_order);
        Assert.AreEqual("😀1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_UsesDefaultCompatPrefixWhenOnlyFolderOrderExists()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"FolderOrder\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"folder_order\":[\"st1\"]}", "1");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st1" }, table.Folder_order);
        Assert.AreEqual("st1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadHeaderJson_MaterializesCompatibleFolderOrderWithDefaultCompatPrefix()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"FolderOrder\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"folder_order\":[\"2\",\"1\"]}");
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song A\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song B\",\"level\":\"2\"}]");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st2", "st1" }, table.Folder_order);
        CollectionAssert.AreEqual(new[] { "st2", "st1" }, table.folder_list);
    }

    [TestMethod]
    public void LoadHeaderJson_KeepsMaterializedFolderOrderWithDefaultCompatPrefix()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"FolderOrder\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"folder_order\":[\"st2\",\"st1\"]}");
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song A\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song B\",\"level\":\"2\"}]");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st2", "st1" }, table.Folder_order);
        CollectionAssert.AreEqual(new[] { "st2", "st1" }, table.folder_list);
    }

    [TestMethod]
    public void LoadHeaderJson_NullCompatPrefixUsesDefaultPrefix()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"compat_prefix\":null,\"folder_sort_key\":\"\",\"folder_sort_ascending\":true,\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st1" }, table.Folder_order);
        Assert.AreEqual("st1", table.entries[0].folder);
    }

    [TestMethod]
    public void LoadDataJson_InferCompatPrefixFromHeaderTagOrSymbolWhenHeaderOrderIsMissing()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\"}", "0");

        Assert.AreEqual("st", table.compat_prefix);
        Assert.AreEqual("st0", table.entries[0].folder);
        CollectionAssert.AreEqual(new[] { "st0" }, table.folder_list);
    }

    [TestMethod]
    public void LoadDataJson_InferCompatPrefixForSymbolicDataLevelWhenHeaderOrderIsMissing()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\"}", "st0");

        Assert.AreEqual("st", table.compat_prefix);
        Assert.AreEqual("stst0", table.entries[0].folder);
        CollectionAssert.AreEqual(new[] { "stst0" }, table.folder_list);
    }

    [TestMethod]
    public void LoadHeaderJson_ExplicitCompatPrefixClearsPendingDataInference()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"First\",\"symbol\":\"st\",\"data_url\":\"score.json\"}");
        table.LoadHeaderJSON("{\"name\":\"Second\",\"symbol\":\"st\",\"compat_prefix\":\"LEVEL \",\"folder_sort_key\":\"\",\"folder_sort_ascending\":true,\"data_url\":\"score.json\"}");
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"level\":\"1\"}]");

        Assert.AreEqual("LEVEL ", table.compat_prefix);
        Assert.AreEqual("LEVEL 1", table.entries[0].folder);
    }

    [TestMethod]
    public void RewriteCompatibleFolderPrefix_UpdatesEntriesAndFolderOrderWithoutTouchingLastUpdate()
    {
        var lastUpdate = new DateTime(2026, 1, 2, 3, 4, 5);
        var table = new BMSTable
        {
            compat_prefix = "LEVEL ",
            last_update = lastUpdate,
            entries =
            [
                CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "LEVEL 1"),
                CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "LEVEL 2")
            ],
            Folder_order = ["LEVEL 2", "LEVEL 1"]
        };
        int revisionBefore = table.PlaylistEntriesRevision;
        var changedProperties = new List<string>();
        table.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        bool changed = table.RewriteCompatibleFolderPrefix("LEVEL ", "★");

        Assert.IsTrue(changed);
        Assert.AreEqual("★1", table.entries[0].folder);
        Assert.AreEqual("★2", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "★2", "★1" }, table.Folder_order);
        CollectionAssert.AreEqual(new[] { "★2", "★1" }, table.folder_list);
        CollectionAssert.AreEqual(new[] { "★2", "★1" }, table.FolderNodes.Where(node => !node.IsSpecial).Select(node => node.FolderName).ToList());
        Assert.AreEqual(lastUpdate, table.last_update);
        Assert.AreEqual(revisionBefore + 1, table.PlaylistEntriesRevision);
        CollectionAssert.Contains(changedProperties, "folder_list");
        CollectionAssert.Contains(changedProperties, "FolderNodes");
        CollectionAssert.Contains(changedProperties, "PlaylistEntriesRevision");
    }

    [TestMethod]
    public void RewriteCompatibleFolderPrefix_PrefixesLocalFoldersWhenOldPrefixIsEmpty()
    {
        var table = new BMSTable
        {
            compat_prefix = string.Empty,
            entries =
            [
                CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Alpha"),
                CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Beta")
            ],
            Folder_order = ["Alpha", "Beta"]
        };

        bool changed = table.RewriteCompatibleFolderPrefix(string.Empty, "★");

        Assert.IsTrue(changed);
        Assert.AreEqual("★Alpha", table.entries[0].folder);
        Assert.AreEqual("★Beta", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "★Alpha", "★Beta" }, table.Folder_order);
    }

    [TestMethod]
    public void PlaylistStructureMutations_AdvanceLastUpdateMonotonically()
    {
        var table = new BMSTable
        {
            entries =
            [
                CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Alpha")
            ],
            Folder_order = ["Alpha"]
        };
        var future = DateTime.Now.AddDays(1);

        table.last_update = future;
        table.CreateNewFolder("Beta");
        Assert.IsTrue(table.last_update > future);

        future = DateTime.Now.AddDays(2);
        table.last_update = future;
        table.RenameFolder("Alpha", "Gamma");
        Assert.IsTrue(table.last_update > future);

        future = DateTime.Now.AddDays(3);
        table.last_update = future;
        table.AddBMSTableEntriesToFolder([CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Delta")], "Gamma");
        Assert.IsTrue(table.last_update > future);

        future = DateTime.Now.AddDays(4);
        table.last_update = future;
        table.RemoveBMSTableEntries([table.entries.First(entry => entry.md5 == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]);
        Assert.IsTrue(table.last_update > future);
    }

    [TestMethod]
    public void RewriteCompatibleFolderPrefix_PrefixesExternalFoldersWhenOldPrefixIsEmpty()
    {
        var table = new BMSTable
        {
            is_external_sync = true,
            compat_prefix = string.Empty,
            entries =
            [
                CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1"),
                CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "2")
            ],
            Folder_order = ["2", "1"]
        };

        bool changed = table.RewriteCompatibleFolderPrefix(string.Empty, "★");

        Assert.IsTrue(changed);
        Assert.AreEqual("★1", table.entries[0].folder);
        Assert.AreEqual("★2", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "★2", "★1" }, table.Folder_order);
    }

    [TestMethod]
    public void RewriteCompatibleFolderPrefix_RewritesExistingTargetFolderInsteadOfTreatingItAsCollision()
    {
        var table = new BMSTable
        {
            compat_prefix = "LEVEL ",
            entries =
            [
                CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "LEVEL 1"),
                CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "★1")
            ],
            Folder_order = ["LEVEL 1", "★1"]
        };

        Assert.IsTrue(table.CanRewriteCompatibleFolderPrefix("LEVEL ", "★"));
        Assert.IsTrue(table.RewriteCompatibleFolderPrefix("LEVEL ", "★"));

        Assert.AreEqual("★1", table.entries[0].folder);
        Assert.AreEqual("★★1", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "★1", "★★1" }, table.Folder_order);
    }

    [TestMethod]
    public void RewriteCompatibleFolderPrefix_FinalDuplicateDoesNotMutateTable()
    {
        var table = new BMSTable
        {
            compat_prefix = "★",
            entries =
            [
                CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1"),
                CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "★1")
            ],
            Folder_order = ["1", "★1"]
        };

        Assert.IsFalse(table.CanRewriteCompatibleFolderPrefix("★", "☆"));
        Assert.ThrowsException<InvalidOperationException>(() => table.RewriteCompatibleFolderPrefix("★", "☆"));

        Assert.AreEqual("1", table.entries[0].folder);
        Assert.AreEqual("★1", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "1", "★1" }, table.Folder_order);
    }

    private static BMSTableEntry CreateEntry(string md5, string folder)
    {
        return new BMSTableEntry(JObject.Parse("{\"md5\":\"" + md5 + "\",\"title\":\"" + md5 + "\",\"level\":\"" + folder + "\"}"));
    }

    private static BMSTable LoadTableWithSingleEntry(string headerJson, string level)
    {
        var table = new BMSTable();
        table.LoadHeaderJSON(headerJson);
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"level\":\"" + level + "\"}]");
        return table;
    }
}
