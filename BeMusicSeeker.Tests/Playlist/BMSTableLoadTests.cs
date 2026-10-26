using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

/// <summary>外部 JSON・保存 JSON と互換フォルダ名の受理、変換、変更失敗時の保全を検証します。</summary>
/// <remarks>モデルの判断と保存形式を直接確認し、実 HTTP・DB・表示への接続は外部取得／永続化／workspace のケースへ分担します。</remarks>
[TestClass]
public sealed class BMSTableLoadTests
{
    /// <summary>同じ旧形式・ISO入力を異文化で全体解析し、Gregorian日・壁時計tickとUnspecifiedを保持します。offset変換は別ケースへ分担します。</summary>
    [DataTestMethod]
    [DataRow("ja-JP")]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    [DataRow("de-DE")]
    [DataRow("th-TH")]
    public void LoadDataJson_AddDatePreservesYearAndCompatibleDateTime(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            (string Text, DateTime Expected)[] cases =
            [
                ("2024/1/2", new DateTime(2024, 1, 2)),
                ("2024/12/31 13:24:35", new DateTime(2024, 12, 31, 13, 24, 35)),
                ("2024/01/02 1:2:3", new DateTime(2024, 1, 2, 1, 2, 3)),
                ("2024-01-02", new DateTime(2024, 1, 2)),
                ("2024-12-31T13:24:35", new DateTime(2024, 12, 31, 13, 24, 35)),
                ("2024-12-31T13:24:35.1", new DateTime(2024, 12, 31, 13, 24, 35).AddTicks(1000000)),
                ("2024-12-31T13:24:35.1234567", new DateTime(2024, 12, 31, 13, 24, 35).AddTicks(1234567))
            ];
            foreach ((string text, DateTime expected) in cases)
            {
                var table = new BMSTable();
                table.LoadHeaderJSON(new JObject { ["name"] = "Date", ["last_update"] = text }.ToString());
                table.LoadDataJSON(new JArray(new JObject { ["title"] = "Date song", ["adddate"] = text }).ToString());
                Assert.AreEqual(expected.Ticks, table.last_update.Ticks, text);
                Assert.AreEqual(expected, table.entries.Single().adddate, text);
                Assert.AreEqual(DateTimeKind.Unspecified, table.last_update.Kind, text);
                Assert.AreEqual(DateTimeKind.Unspecified, table.entries.Single().adddate.Kind, text);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>採用する非空日時の装飾・部分一致・年末尾・桁超過をheader/data分類で拒否し、先行する正常行も一表へ採用しません。</summary>
    [DataTestMethod]
    [DataRow("2024/12/3100")]
    [DataRow("added [2024/12/31]")]
    [DataRow("12/31/2024")]
    [DataRow("31/12/2024")]
    [DataRow("24/12/31")]
    [DataRow("2024/99/99")]
    [DataRow("2024/1/2 1:2")]
    [DataRow("2024-01-02T01:02")]
    [DataRow("2024-01-02T01:02:03.12345678")]
    [DataRow("not-a-date")]
    public void LoadJson_InvalidAdoptedDateRejectsWholeTable(string text)
    {
        var table = new BMSTable { entries = [new BMSTableEntry { title = "Keep" }] };
        IReadOnlyList<BMSTableEntry> old = table.entries;
        PlaylistHeaderParseException headerFailure = Assert.ThrowsException<PlaylistHeaderParseException>(() =>
            table.LoadHeaderJSON(new JObject { ["last_update"] = text }.ToString()));
        Assert.IsInstanceOfType<PlaylistJsonDateException>(headerFailure.InnerException);
        StringAssert.Contains(headerFailure.Message, "last_update");
        PlaylistDataParseException dataFailure = Assert.ThrowsException<PlaylistDataParseException>(() =>
            table.LoadDataJSON(new JArray(new JObject { ["title"] = "Valid first", ["adddate"] = "2024-01-02" },
                new JObject { ["title"] = "Invalid next", ["adddate"] = text }).ToString()));
        Assert.IsInstanceOfType<PlaylistJsonDateException>(dataFailure.InnerException);
        StringAssert.Contains(dataFailure.Message, "adddate");
        Assert.AreSame(old, table.entries);
    }

    /// <summary>省略・null・空白は既定日時を維持し、output_dateと実採用条件を満たさない行の日時は検査しません。</summary>
    [DataTestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(true, "")]
    [DataRow(true, "   ")]
    public void LoadDataJson_EmptyOptionalDateKeepsEntryAndDefaultDate(bool includeDate, string? text)
    {
        var row = new JObject { ["md5"] = new string('a', 32), ["title"] = "Date song" };
        var header = new JObject { ["name"] = "Date", ["output_date"] = "invalid ignored date" };
        if (includeDate) { row["adddate"] = text; header["last_update"] = text; }
        DateTime before = DateTime.Now;
        DateTime preservedUpdate = new DateTime(2020, 1, 2, 3, 4, 5).AddTicks(1);
        var table = new BMSTable { last_update = preservedUpdate };
        table.LoadHeaderJSON(header.ToString());
        table.LoadDataJSON(new JArray(row,
            new JObject { ["md5"] = "invalid hash", ["title"] = "  ", ["adddate"] = "invalid ignored date" },
            new JObject { ["artist"] = "ignored without identity fields", ["adddate"] = "invalid ignored date" }).ToString());
        DateTime after = DateTime.Now;
        BMSTableEntry entry = table.entries.Single();
        Assert.AreEqual(new string('a', 32), entry.md5);
        Assert.AreEqual("Date song", entry.title);
        Assert.IsTrue(entry.adddate >= before && entry.adddate <= after);
        Assert.AreEqual(preservedUpdate, table.last_update);
    }

    /// <summary>旧形式の月日桁数とISOのZ/offsetによらず、明示offsetだけをLocalDateTimeへ変換します。</summary>
    [DataTestMethod]
    [DataRow("2024/1/21 1:2:3+09:00", 9)]
    [DataRow("2024/01/21 01:02:03+00:00", 0)]
    [DataRow("2024-01-21T01:02:03Z", 0)]
    [DataRow("2024-01-21T01:02:03.0000001-04:00", -4)]
    public void LoadDataJson_ExplicitOffsetPreservesOriginalOffsetInterpretation(string text, int offsetHours)
    {
        var table = new BMSTable();
        table.LoadHeaderJSON(new JObject { ["last_update"] = text }.ToString());
        table.LoadDataJSON(new JArray(new JObject { ["title"] = "Date song", ["adddate"] = text }).ToString());
        DateTime expected = new DateTimeOffset(2024, 1, 21, 1, 2, 3, TimeSpan.FromHours(offsetHours)).AddTicks(text.Contains(".0000001", StringComparison.Ordinal) ? 1 : 0).LocalDateTime;
        Assert.AreEqual(expected, table.entries.Single().adddate);
        Assert.AreEqual(expected, table.last_update);
        Assert.AreEqual(DateTimeKind.Local, table.entries.Single().adddate.Kind);
    }

    /// <summary>異文化へ実出力JSONを渡し、時刻と1tickを含む日時を丸めず保持します。完全wireは独立goldenへ分担します。</summary>
    [DataTestMethod]
    [DataRow("en-US", "fr-FR")]
    [DataRow("fr-FR", "th-TH")]
    [DataRow("th-TH", "ja-JP")]
    public void DataToJson_CrossCultureRoundTripPreservesAddDate(string outputCulture, string inputCulture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(outputCulture);
            DateTime expected = new DateTime(2024, 1, 2, 13, 24, 35).AddTicks(1);
            var table = new BMSTable { last_update = expected, entries = [new BMSTableEntry { title = "Date song", adddate = expected }] };
            string header = table.HeaderToJson();
            string data = table.DataToJson();
            StringAssert.Contains(header, "2024-01-02T13:24:35.0000001");
            StringAssert.Contains(data, "2024-01-02T13:24:35.0000001");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(inputCulture);
            var restored = new BMSTable();
            restored.LoadHeaderJSON(header);
            restored.LoadDataJSON(data);
            Assert.AreEqual(expected, restored.entries.Single().adddate);
            Assert.AreEqual(expected, restored.last_update);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>外部levelはorg_level優先/nullと原文folderを保ち、JSON numberの直接値と既存の一数値抽出だけを固定Cultureへ揃えます。</summary>
    [DataTestMethod]
    [DataRow("ja-JP")]
    [DataRow("fr-FR")]
    [DataRow("de-DE")]
    public void LoadDataJson_ExternalLevelKeepsItsOwnGrammarAndOriginalFolder(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var table = new BMSTable { compat_prefix = "P" };
            table.LoadDataJSON("[{\"title\":\"number\",\"level\":1.5},{\"title\":\"exponent\",\"level\":1e2},{\"title\":\"decorated\",\"level\":\"★-1.5\"},{\"title\":\"multiple\",\"level\":\"1 and 2\"},{\"title\":\"none\",\"level\":\"???\"},{\"title\":\"priority\",\"org_level\":2.5,\"level\":\"★99\",\"folder\":\"Original\"},{\"title\":\"null priority\",\"org_level\":null,\"level\":\"★99\"}]");
            (string, double?, string)[] expected = new[] { ("number", (double?)1.5, "P1.5"), ("exponent", (double?)100, "P100"),
                ("decorated", (double?)-1.5, "P★-1.5"), ("multiple", (double?)null, "P1 and 2"),
                ("none", (double?)null, "P???"), ("priority", (double?)2.5, "Original"), ("null priority", (double?)null, "P★99") };
            foreach ((string title, double? level, string folder) in expected)
            {
                BMSTableEntry entry = table.entries.Single(row => row.title == title);
                Assert.AreEqual(level, entry.level, title);
                Assert.AreEqual(folder, entry.folder, title);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>公開集合を標準の可変interfaceへキャストして構成変更しても、確定行の参照・順・所属と派生状態は変化しません。行値の編集は引き続き同じ対象へ反映します。</summary>
    [TestMethod]
    public void Entries_PublicCollectionProtectsCompositionAndPreservesEntryReferences()
    {
        BMSTableEntry first = new() { md5 = new string('a', 32), folder = "A", title = "first" };
        BMSTableEntry second = new() { md5 = new string('b', 32), folder = "B", title = "second" };
        var table = new BMSTable { playlist_id = 71, entries = [first, second] };
        var orderInput = new List<string> { "B", "A" };
        table.Folder_order = orderInput;
        orderInput[0] = "caller change";
        IReadOnlyList<BMSTableEntry> published = table.entries;
        IReadOnlyList<PlaylistFolderNode> nodes = table.FolderNodes;
        IReadOnlyList<string> order = table.Folder_order, names = table.folder_list;
        (PlaylistFolderNodeSpecialKind SpecialKind, string FolderName)[] nodeValues = nodes.Select(node => (node.SpecialKind, node.FolderName)).ToArray();
        BMSTableEntry[] expected = [first, second];
        string[] folders = table.folder_list.ToArray();
        int revision = table.PlaylistEntriesRevision;
        void Check(Action mutate)
        {
            try { mutate(); } catch (NotSupportedException) { }
            CollectionAssert.AreEqual(expected, table.entries.ToArray());
            Assert.AreSame(first, table.entries[0]);
            Assert.AreSame(second, table.entries[1]);
            Assert.AreEqual("A", first.folder);
            Assert.AreEqual("B", second.folder);
            Assert.IsTrue(table.entries.All(entry => ReferenceEquals(table, entry.parent) && entry.playlist_id == 71));
            CollectionAssert.AreEqual(folders, table.folder_list.ToArray());
            CollectionAssert.AreEqual(new[] { "B", "A" }, table.Folder_order.ToArray());
            CollectionAssert.AreEqual(nodeValues, table.FolderNodes.Select(node => (node.SpecialKind, node.FolderName)).ToArray());
            Assert.AreEqual(revision, table.PlaylistEntriesRevision);
        }
        if (published is ICollection<BMSTableEntry> collection)
        {
            Check(() => collection.Add(new BMSTableEntry { md5 = new string('c', 32), folder = "C" }));
            Check(() => collection.Remove(first));
            Check(collection.Clear);
        }
        if (published is IList<BMSTableEntry> list)
        {
            Check(() => list.Insert(0, second));
            Check(() => list.RemoveAt(0));
            Check(() => list[0] = second);
            Check(() => { list[0] = second; list[1] = first; });
        }
        if (published is IList untyped)
        {
            Check(() => untyped.Add(second));
            Check(() => untyped.Remove(first));
            Check(untyped.Clear);
            Check(() => untyped.Insert(0, second));
            Check(() => untyped[0] = second);
        }
        if (published is BMSTableEntry[] array)
        {
            Check(() => array[0] = second);
            Check(() => Array.Reverse(array));
            Check(() => Array.Clear(array));
        }
        if (published is List<BMSTableEntry> concrete)
        {
            Check(concrete.Reverse);
            Check(() => concrete.Sort((left, right) => string.CompareOrdinal(right.title, left.title)));
        }
        // 公開された構成・順序は標準interfaceからも変えられず、getterは同じ確定入力を返します。
        if (nodes is IList<PlaylistFolderNode> mutableNodes) { Check(() => mutableNodes[0] = PlaylistFolderNode.CreateFolder("Changed")); Check(mutableNodes.Clear); }
        if (names is IList<string> mutableNames) { Check(() => mutableNames[0] = "Changed"); Check(mutableNames.Clear); }
        if (order is IList<string> mutableOrder) { Check(() => mutableOrder[0] = "Changed"); Check(mutableOrder.Clear); }
        if (nodes is IList untypedNodes) { Check(() => untypedNodes[0] = PlaylistFolderNode.CreateFolder("Changed")); }
        if (names is IList untypedNames) { Check(() => untypedNames[0] = "Changed"); }
        if (order is IList untypedOrder) { Check(() => untypedOrder[0] = "Changed"); }
        Assert.AreSame(nodes, table.FolderNodes); Assert.AreSame(names, table.folder_list); Assert.AreSame(order, table.Folder_order);
        first.memo = "edited";
        Assert.AreEqual("edited", table.entries[0].memo);
    }

    /// <summary>空・非空の完成入力は一度だけ自前集合へ取り込み、呼出元の追加・削除・置換・並替えを反映せず、行自体の参照は維持します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Entries_InputListMutationDoesNotAlterCommittedComposition(bool nonempty)
    {
        BMSTableEntry first = new() { md5 = new string('a', 32), folder = "A" };
        BMSTableEntry second = new() { md5 = new string('b', 32), folder = "B" };
        List<BMSTableEntry> input = nonempty ? [first, second] : [];
        var table = new BMSTable { entries = input };
        BMSTableEntry[] expected = nonempty ? [first, second] : [];
        string[] folders = table.folder_list.ToArray();
        int revision = table.PlaylistEntriesRevision;
        void Check()
        {
            CollectionAssert.AreEqual(expected, table.entries.ToArray());
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.AreSame(expected[index], table.entries[index]);
                Assert.AreSame(table, table.entries[index].parent);
            }
            CollectionAssert.AreEqual(folders, table.folder_list.ToArray());
            Assert.AreEqual(revision, table.PlaylistEntriesRevision);
        }
        input.Add(new BMSTableEntry { md5 = new string('c', 32), folder = "C" }); Check();
        input[0] = new BMSTableEntry { md5 = new string('d', 32), folder = "D" }; Check();
        input.Reverse(); Check();
        input.RemoveAt(0); Check();
        input.Clear(); Check();
        if (nonempty)
        {
            first.memo = "edited";
            Assert.AreEqual("edited", table.entries[0].memo);
        }
    }

    /// <summary>別々の page／header URI と相対 data_url を読み込み、解析後も取得元と相対表記を区別して保つことを確認します。</summary>
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

    /// <summary>壊れた header JSON を専用解析例外へ包み、元の解析例外を保持することを確認して、診断原因の消失を検出します。</summary>
    [TestMethod]
    public void LoadHeaderJson_PreservesInnerException()
    {
        var table = new BMSTable();

        PlaylistHeaderParseException exception = Assert.ThrowsException<PlaylistHeaderParseException>(() => table.LoadHeaderJSON("{ invalid json }"));

        Assert.IsNotNull(exception.InnerException);
    }

    /// <summary>壊れた data JSON を専用解析例外へ包み、元の解析例外を保持することを確認します。</summary>
    [TestMethod]
    public void LoadDataJson_PreservesInnerException()
    {
        var table = new BMSTable();

        PlaylistDataParseException exception = Assert.ThrowsException<PlaylistDataParseException>(() => table.LoadDataJSON("{ invalid json }"));

        Assert.IsNotNull(exception.InnerException);
    }

    /// <summary>配列でない data の object 入力を専用解析例外と内部原因で拒否し、単一 entry として受理する誤りを検出します。</summary>
    [TestMethod]
    public void LoadDataJson_WrongTopLevelShapePreservesParseContract()
    {
        var table = new BMSTable();

        PlaylistDataParseException exception = Assert.ThrowsException<PlaylistDataParseException>(
            () => table.LoadDataJSON("{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}"));

        Assert.IsNotNull(exception.InnerException);
    }

    /// <summary>大文字 MD5 を別名として受理せず、数値 level・null comment・親 MD5 の小文字化・未知項目の無視を同じ JSON 読込みで確認します。</summary>
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

    /// <summary>明示 null org_level は level 値へ戻さず、互換フォルダには文字列 level を使うことを確認して、数値と所属名の混同を検出します。</summary>
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

    /// <summary>文字列・boolean・array・object の org_level は数値へ暗黙変換せず、専用解析例外と元原因を保って拒否することを確認します。</summary>
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

    /// <summary>明示 null entry_type は欠落と同一視せず専用解析例外へ原因を保持し、不正な項目形式の受理を検出します。</summary>
    [TestMethod]
    public void LoadHeaderJson_ExplicitNullEntryTypePreservesParseContract()
    {
        var table = new BMSTable();

        PlaylistHeaderParseException exception = Assert.ThrowsException<PlaylistHeaderParseException>(
            () => table.LoadHeaderJSON("{\"name\":\"Typed\",\"symbol\":\"st\",\"entry_type\":null,\"data_url\":\"data.json\"}"));

        Assert.IsNotNull(exception.InnerException);
    }

    /// <summary>ISO 日時に見える tag・title・comment を文字列として保ち、JSON 境界で日時へ解釈して表記を変える誤りを検出します。</summary>
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

    /// <summary>引用符・両 hash・親 hash・小数 level を含む entry の公開 JSON を独立した保存形式期待と照合し、固定日時と全項目・型・escape・整形の変更を検出します。</summary>
    [TestMethod]
    public void PlaylistJson_WireFormatMatchesIndependentIndentedGolden()
    {
        var entry = new BMSTableEntry(JObject.Parse("{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"org_level\":1.5,\"title\":\"Title \\\"quoted\\\"\",\"artist\":\"Artist\",\"folder\":\"st1\",\"level\":\"1\",\"lr2_bmsid\":\"123\",\"url\":\"https://example.com/u\",\"url_diff\":\"https://example.com/d\",\"name_diff\":\"Diff\",\"org_md5s\":[\"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\"],\"comment\":\"Comment\"}"));
        entry.adddate = new DateTime(2024, 1, 2);

        string actual = entry.ToJson();
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
            + "  \"adddate\": \"2024-01-02T00:00:00.0000000\"" + Environment.NewLine
            + "}";

        Assert.AreEqual(expected, actual);
    }

    /// <summary>利用者指定の folder 順と親 MD5 集合を DB 用 JSON 配列の独立期待へ照合し、順序や保存形式の変更を検出します。</summary>
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

    /// <summary>course・互換 prefix・folder 順を含む header 出力を時刻・version のみ正規化した独立 golden と照合し、公開 JSON の項目と型の互換変更を検出します。</summary>
    [TestMethod]
    public void PlaylistHeaderJson_MatchesIndependentFullGoldenAfterVariableNormalization()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"Table\",\"symbol\":\"st\",\"tag\":\"tag\",\"compat_prefix\":\"st\",\"folder_sort_key\":\"\",\"folder_sort_ascending\":true,\"data_url\":\"data.json\",\"folder_order\":[\"st1\"],\"level_order\":[1],\"course\":[{\"name\":\"Course\",\"constraint\":[\"grade_mirror\"],\"md5\":[\"11111111111111111111111111111111\"]}]}");
        table.last_update = new DateTime(2024, 1, 2);

        using var jsonReader = new JsonTextReader(new StringReader(table.HeaderToJson())) { DateParseHandling = DateParseHandling.None };
        var normalized = JObject.Load(jsonReader);
        string? outputDate = normalized["output_date"]?.Value<string>();
        Assert.IsNotNull(outputDate);
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(outputDate, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z"));
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
            + "  \"last_update\": \"2024-01-02T00:00:00.0000000\"," + Environment.NewLine
            + "  \"editor_name\": \"BeMusicSeeker\"," + Environment.NewLine
            + "  \"editor_version\": \"<EDITOR_VERSION>\"," + Environment.NewLine
            + "  \"output_date\": \"<OUTPUT_DATE>\"" + Environment.NewLine
            + "}";

        Assert.AreEqual(expected, normalized.ToString(Formatting.Indented));
    }

    /// <summary>明示null levelと固定tick日時を持つentryのdata配列を独立goldenと照合し、null・空文字・全項目の形式変更を検出します。</summary>
    [TestMethod]
    public void PlaylistDataJson_MatchesIndependentFullGolden()
    {
        var entry = new BMSTableEntry(JObject.Parse("{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"folder\":\"st1\",\"org_level\":null}"));
        entry.adddate = new DateTime(2024, 1, 2);
        var table = new BMSTable
        {
            Folder_order = ["st1"],
            entries = [entry]
        };

        string actual = table.DataToJson();
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
            + "    \"adddate\": \"2024-01-02T00:00:00.0000000\"" + Environment.NewLine
            + "  }" + Environment.NewLine
            + "]";

        Assert.AreEqual(expected, actual);
    }

    /// <summary>header object の後に別 JSON が続く入力を専用解析例外で拒否し、末尾の未解析文書を捨てて成功する誤りを検出します。</summary>
    [TestMethod]
    public void LoadHeaderJson_RejectsTrailingJsonDocument()
    {
        var table = new BMSTable();

        PlaylistHeaderParseException exception = Assert.ThrowsException<PlaylistHeaderParseException>(
            () => table.LoadHeaderJSON("{\"name\":\"Typed\",\"symbol\":\"st\"}{\"extra\":true}"));

        Assert.IsNotNull(exception.InnerException);
    }

    /// <summary>data 配列の後に別 JSON が続く入力を専用解析例外で拒否し、先頭配列だけを採用する誤りを検出します。</summary>
    [TestMethod]
    public void LoadDataJson_RejectsTrailingJsonDocument()
    {
        var table = new BMSTable();

        PlaylistDataParseException exception = Assert.ThrowsException<PlaylistDataParseException>(
            () => table.LoadDataJSON("[]{}"));

        Assert.IsNotNull(exception.InnerException);
    }

    /// <summary>現在・親相対 segment を互換の出力名へ正規化し、名前が残らない親移動は null とすることを確認して、相対名の解釈変更を検出します。</summary>
    [TestMethod]
    public void NormalizeOutputDirectoryName_ResolvesRelativeSegmentsInsideManagedBase()
    {
        Assert.AreEqual(Path.Combine("Parent", "Child"), BMSTable.NormalizeOutputDirectoryName("Parent/./Child"));
        Assert.AreEqual("Child", BMSTable.NormalizeOutputDirectoryName("Parent/../Child"));
        Assert.AreEqual("Sibling", BMSTable.NormalizeOutputDirectoryName("../Sibling"));
        Assert.IsNull(BMSTable.NormalizeOutputDirectoryName("../.."));
    }

    /// <summary>header の不正な data URL は URL 解決失敗として返し、通常の JSON 構文失敗と混同しないことを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_InvalidDataUrlThrowsInvalidOperationException()
    {
        var table = new BMSTable();

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() => table.LoadHeaderJSON("{\"name\":\"Test\",\"symbol\":\"T\",\"data_url\":\"http://[broken\",\"level_order\":[1]}"));

        StringAssert.Contains(exception.Message, "Failed to resolve playlist data_url");
    }

    /// <summary>保存済み data URL の不正文字列を setter 経由で読み込む場合は例外を投げず URL を未設定にし、外部 header の厳格な失敗とは区別する互換性を確認します。</summary>
    [TestMethod]
    public void StoredDataUrlSetter_InvalidValueDoesNotThrowAndKeepsNull()
    {
        var table = new BMSTable();
        table.data_url = "http://[broken";

        Assert.IsNull(table.Data_url);
    }

    /// <summary>tag と入れ子 course を含む header 読込みで hash を生成し、再出力にも course 制約と tag を保つことを確認して、読み書き間の脱落を検出します。</summary>
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

    /// <summary>数値 level_order へ symbol を接頭辞として付け、行の所属と公開 folder 順を一致させる互換推定を確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromNumericLevelOrderSymbol()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st1" }, table.Folder_order.ToArray());
        Assert.AreEqual("st1", table.entries[0].folder);
        CollectionAssert.AreEqual(new[] { "st1" }, table.folder_list.ToArray());
    }

    /// <summary>CP932 の日本語記号 symbol をそのまま接頭辞に採用し、数値 level の行と順を同じフォルダ名へ変換することを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromNumericLevelOrderJapaneseSymbol()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Insane\",\"symbol\":\"★\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("★", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "★1" }, table.Folder_order.ToArray());
        Assert.AreEqual("★1", table.entries[0].folder);
    }

    /// <summary>既に symbol を含む level 文字列にも互換 prefix を付ける旧変換を保ち、二重 prefix を推測で除去する変更を検出します。</summary>
    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromSymbolicLevelOrder()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"level_order\":[\"st1\"]}", "st1");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "stst1" }, table.Folder_order.ToArray());
        Assert.AreEqual("stst1", table.entries[0].folder);
    }

    /// <summary>tag と symbol が異なる場合は tag を優先して行と folder 順を作り、接頭辞の優先順位変更を検出します。</summary>
    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromTagBeforeSymbol()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"sl\",\"tag\":\"st\",\"data_url\":\"score.json\",\"level_order\":[\"X\"]}", "X");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "stX" }, table.Folder_order.ToArray());
        Assert.AreEqual("stX", table.entries[0].folder);
    }

    /// <summary>CP932 外 tag がある場合は symbol へ再推定せず旧 LEVEL prefix を使い、別名フォルダへ変える誤りを検出します。</summary>
    [TestMethod]
    public void LoadHeaderJson_NonCp932TagUsesLegacyPrefixWithoutTryingSymbol()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Unicode\",\"symbol\":\"st\",\"tag\":\"😀\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("LEVEL ", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "LEVEL 1" }, table.Folder_order.ToArray());
        Assert.AreEqual("LEVEL 1", table.entries[0].folder);
    }

    /// <summary>CP932 外 symbol は旧 LEVEL prefix へ変換し、LR2 で表せない記号の暗黙置換を避ける互換条件を確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_NonCp932SymbolUsesLegacyPrefix()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Unicode\",\"symbol\":\"😀\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("LEVEL ", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "LEVEL 1" }, table.Folder_order.ToArray());
        Assert.AreEqual("LEVEL 1", table.entries[0].folder);
    }

    /// <summary>tag と symbol の両方がない header は旧 LEVEL prefix を使い、既存の互換フォルダ名を保つことを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_MissingTagAndSymbolUsesLegacyPrefix()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"NoPrefix\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("LEVEL ", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "LEVEL 1" }, table.Folder_order.ToArray());
        Assert.AreEqual("LEVEL 1", table.entries[0].folder);
    }

    /// <summary>負数・ゼロ・正数の level_order を順ごと prefix 付き名へ変換し、符号やゼロを欠落扱いする誤りを検出します。</summary>
    [TestMethod]
    public void LoadHeaderJson_InferCompatPrefixFromSignedLevelOrder()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"level_order\":[\"-5\",0,1]}", "-5");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st-5", "st0", "st1" }, table.Folder_order.ToArray());
        Assert.AreEqual("st-5", table.entries[0].folder);
    }

    /// <summary>明示空 prefix は symbol による推定を止め、数値 level の生のフォルダ名を保つことを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_ExplicitEmptyCompatPrefixPreventsInference()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"compat_prefix\":\"\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual(string.Empty, table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "1" }, table.Folder_order.ToArray());
        Assert.AreEqual("1", table.entries[0].folder);
    }

    /// <summary>明示空 prefix の folder_order は利用者入力の 2→1 を保ち、data 読込み後も並びや名前を推定で変更しないことを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_ExplicitEmptyCompatPrefixKeepsRawFolderOrder()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"FolderOrder\",\"symbol\":\"st\",\"compat_prefix\":\"\",\"data_url\":\"score.json\",\"folder_order\":[\"2\",\"1\"]}");
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song A\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song B\",\"level\":\"2\"}]");

        Assert.AreEqual(string.Empty, table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "2", "1" }, table.Folder_order.ToArray());
        CollectionAssert.AreEqual(new[] { "2", "1" }, table.folder_list.ToArray());
    }

    /// <summary>明示された旧 LEVEL prefix は symbol より優先し、旧フォルダ名と順を読み戻せることを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_ExplicitLevelCompatPrefixKeepsLegacyFolderNames()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Legacy\",\"symbol\":\"st\",\"compat_prefix\":\"LEVEL \",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("LEVEL ", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "LEVEL 1" }, table.Folder_order.ToArray());
        Assert.AreEqual("LEVEL 1", table.entries[0].folder);
    }

    /// <summary>明示された CP932 外 prefix は推定規則で補正せず、その値を行と folder 順へ反映する既存の受理条件を確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_ExplicitNonCp932CompatPrefixIsPreserved()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Explicit\",\"symbol\":\"st\",\"compat_prefix\":\"😀\",\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("😀", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "😀1" }, table.Folder_order.ToArray());
        Assert.AreEqual("😀1", table.entries[0].folder);
    }

    /// <summary>level_order のない header は symbol で data level を材料化し、既に同名の folder_order と一致させることを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_UsesDefaultCompatPrefixWhenOnlyFolderOrderExists()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"FolderOrder\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"folder_order\":[\"st1\"]}", "1");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st1" }, table.Folder_order.ToArray());
        Assert.AreEqual("st1", table.entries[0].folder);
    }

    /// <summary>data 読込み後に raw folder_order へ不足する prefix を付け、入力の 2→1 順と行の所属を一致させることを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_MaterializesCompatibleFolderOrderWithDefaultCompatPrefix()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"FolderOrder\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"folder_order\":[\"2\",\"1\"]}");
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song A\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song B\",\"level\":\"2\"}]");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st2", "st1" }, table.Folder_order.ToArray());
        CollectionAssert.AreEqual(new[] { "st2", "st1" }, table.folder_list.ToArray());
    }

    /// <summary>既に prefix を持つ folder_order は data 読込み後も重ねて prefix を付けず、同じ所属順を保つことを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_KeepsMaterializedFolderOrderWithDefaultCompatPrefix()
    {
        var table = new BMSTable();
        table.LoadHeaderJSON("{\"name\":\"FolderOrder\",\"symbol\":\"st\",\"data_url\":\"score.json\",\"folder_order\":[\"st2\",\"st1\"]}");
        table.LoadDataJSON("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song A\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Song B\",\"level\":\"2\"}]");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st2", "st1" }, table.Folder_order.ToArray());
        CollectionAssert.AreEqual(new[] { "st2", "st1" }, table.folder_list.ToArray());
    }

    /// <summary>明示 null prefix は未設定として symbol 推定を使い、明示空 prefix との受理差を保つことを確認します。</summary>
    [TestMethod]
    public void LoadHeaderJson_NullCompatPrefixUsesDefaultPrefix()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"compat_prefix\":null,\"folder_sort_key\":\"\",\"folder_sort_ascending\":true,\"data_url\":\"score.json\",\"level_order\":[1]}", "1");

        Assert.AreEqual("st", table.compat_prefix);
        CollectionAssert.AreEqual(new[] { "st1" }, table.Folder_order.ToArray());
        Assert.AreEqual("st1", table.entries[0].folder);
    }

    /// <summary>header 順のない表でも数値 data level に symbol prefix を付け、行と公開フォルダ集合へ同じ名前を渡すことを確認します。</summary>
    [TestMethod]
    public void LoadDataJson_InferCompatPrefixFromHeaderTagOrSymbolWhenHeaderOrderIsMissing()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\"}", "0");

        Assert.AreEqual("st", table.compat_prefix);
        Assert.AreEqual("st0", table.entries[0].folder);
        CollectionAssert.AreEqual(new[] { "st0" }, table.folder_list.ToArray());
    }

    /// <summary>header 順のない文字列 data level も互換 prefix を付け、推測による二重 prefix 除去で保存名を変えないことを確認します。</summary>
    [TestMethod]
    public void LoadDataJson_InferCompatPrefixForSymbolicDataLevelWhenHeaderOrderIsMissing()
    {
        BMSTable table = LoadTableWithSingleEntry("{\"name\":\"Stella\",\"symbol\":\"st\",\"data_url\":\"score.json\"}", "st0");

        Assert.AreEqual("st", table.compat_prefix);
        Assert.AreEqual("stst0", table.entries[0].folder);
        CollectionAssert.AreEqual(new[] { "stst0" }, table.folder_list.ToArray());
    }

    /// <summary>未設定 prefix の header を明示 prefix の header で再読込みすると、後の data は新 prefix を使い、前回の推定状態を残さないことを確認します。</summary>
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

    /// <summary>prefix 変更は行・順・公開 node を一緒に更新して revision と必要通知を進め、表の更新日時は保つことを確認します。</summary>
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
        IReadOnlyList<PlaylistFolderNode> oldNodes = table.FolderNodes;
        IReadOnlyList<string> oldNames = table.folder_list, oldOrder = table.Folder_order;
        var changedProperties = new List<string>();
        table.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        bool changed = ApplyPrefixCandidate(table, "★");

        Assert.IsTrue(changed);
        CollectionAssert.AreEqual(new[] { "LEVEL 2", "LEVEL 1" }, oldNames.ToArray());
        CollectionAssert.AreEqual(new[] { "LEVEL 2", "LEVEL 1" }, oldOrder.ToArray());
        CollectionAssert.AreEqual(new[] { "LEVEL 2", "LEVEL 1" }, oldNodes.Where(node => !node.IsSpecial).Select(node => node.FolderName).ToArray());
        Assert.AreEqual("★1", table.entries[0].folder);
        Assert.AreEqual("★2", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "★2", "★1" }, table.Folder_order.ToArray());
        CollectionAssert.AreEqual(new[] { "★2", "★1" }, table.folder_list.ToArray());
        CollectionAssert.AreEqual(new[] { "★2", "★1" }, table.FolderNodes.Where(node => !node.IsSpecial).Select(node => node.FolderName).ToList());
        Assert.AreEqual(lastUpdate, table.last_update);
        Assert.AreEqual(revisionBefore + 1, table.PlaylistEntriesRevision);
        CollectionAssert.Contains(changedProperties, "folder_list");
        CollectionAssert.Contains(changedProperties, "FolderNodes");
        CollectionAssert.Contains(changedProperties, "PlaylistEntriesRevision");
    }

    /// <summary>空 prefix のローカル分類名にも新 prefix を付け、行と入力順を同時に更新することを確認します。</summary>
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

        bool changed = ApplyPrefixCandidate(table, "★");

        Assert.IsTrue(changed);
        Assert.AreEqual("★Alpha", table.entries[0].folder);
        Assert.AreEqual("★Beta", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "★Alpha", "★Beta" }, table.Folder_order.ToArray());
    }

    /// <summary>現在時刻より未来の更新日時でも folder 作成・改名・行追加・削除が単調に日時を進め、時計で既存日時を巻き戻す誤りを検出します。</summary>
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
        DateTime future = DateTime.Now.AddDays(1);

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

    /// <summary>外部同期表の空 prefix も新 prefix へ変換し、逆順の folder_order を保って行と一致させることを確認します。</summary>
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

        bool changed = ApplyPrefixCandidate(table, "★");

        Assert.IsTrue(changed);
        Assert.AreEqual("★1", table.entries[0].folder);
        Assert.AreEqual("★2", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "★2", "★1" }, table.Folder_order.ToArray());
    }

    /// <summary>変更先 prefix を既に含む別フォルダも変換対象とし、最終名が異なる場合は衝突として拒否しないことを確認します。</summary>
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
        Assert.IsTrue(ApplyPrefixCandidate(table, "★"));

        Assert.AreEqual("★1", table.entries[0].folder);
        Assert.AreEqual("★★1", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "★1", "★★1" }, table.Folder_order.ToArray());
    }

    /// <summary>prefix 変換で最終名が重なる場合は可否判断と実変更の両方で拒み、行と folder 順を一切変えないことを確認します。</summary>
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
        Assert.ThrowsException<InvalidOperationException>(() => ApplyPrefixCandidate(table, "☆"));

        Assert.AreEqual("1", table.entries[0].folder);
        Assert.AreEqual("★1", table.entries[1].folder);
        CollectionAssert.AreEqual(new[] { "1", "★1" }, table.Folder_order.ToArray());
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
    // 変換条件の網羅だけをモデル候補/適用へ通します。DB・配送・次要求終端は実property/bulk系列が担います。
    private static bool ApplyPrefixCandidate(BMSTable table, string prefix)
    {
        var edit = new PlaylistHeaderEdit { Fields = PlaylistHeaderFields.CompatPrefix, CompatPrefix = prefix };
        BMSTable.EntryMutation candidate = table.PrepareHeaderMutation(edit);
        PlaylistEntryCommitReceipt receipt = table.ApplyEntryMutation(candidate);
        table.PublishHeaderCommitProperties(edit, receipt);
        return candidate.HasChanges;
    }
}
