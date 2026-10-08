using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>基準版で受理された境界の外部結果と、文字列保持要求の同値性を固定します。</summary>
[TestClass]
public sealed class ChartInfoParserBoundaryTests
{
    [TestMethod]
    [DataRow("future_predecessor")]
    [DataRow("negative_events")]
    [DataRow("zero_coordinates")]
    [DataRow("infinite_last")]
    [DataRow("infinite_request")]
    [DataRow("long_start_overwritten")]
    [DataRow("long_reverse")]
    [DataRow("covered_endpoint_overwritten")]
    [DataRow("diagnostics_order")]
    [DataRow("random_excluded_command")]
    [DataRow("base62_mine")]
    [DataRow("long_distribution")]
    [DataRow("bmson_output")]
    [DataRow("infinite_unrequested")]
    [DataRow("predecessor_minimum")]
    [DataRow("lnobj_previous_mine")]
    [DataRow("covered_endpoint_removed")]
    [DataRow("lnobj_paired_blocks_older_normal")]
    [DataRow("pending_removal_uses_current_owner_cells")]
    public void ParseBytes_BoundaryInput_PreservesBaselineOutputAndDiagnostics(string name)
    {
        BoundaryCase fixture = GetCase(name);
        byte[] bytes = Encoding.UTF8.GetBytes(fixture.Input);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            if (fixture.Failure != null)
            {
                InvalidDataException exception = Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.ParseBytesDetailed(bytes, fixture.Extension));
                Assert.AreEqual(fixture.Failure, exception.Message);
                Assert.IsNotNull(exception.InnerException);
                Assert.AreEqual("BeMusicSeeker.Models.BmsLibraryInternal.ChartInfoParser+BmsRecoverableParseException", exception.InnerException.GetType().FullName);
                Assert.AreEqual(fixture.Failure, exception.InnerException.Message);
                Assert.IsNull(exception.InnerException.InnerException);
                return;
            }
            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(bytes, fixture.Extension);
            Assert.AreEqual(fixture.ChartString, result.ChartString, name);
            CollectionAssert.AreEqual(fixture.Diagnostics, ObserveDiagnostics(result), name);
            Assert.AreEqual(fixture.Distribution, result.Row.distribution, name);
            Assert.AreEqual(fixture.SpeedChange, result.Row.speedchange, name);
            Assert.AreEqual(fixture.Notes, result.Row.notes, name);
            Assert.AreEqual(fixture.NormalKeys, result.Row.n, name);
            Assert.AreEqual(fixture.LongKeys, result.Row.ln, name);
            Assert.AreEqual(fixture.NormalScratch, result.Row.s, name);
            Assert.AreEqual(fixture.LongScratch, result.Row.ls, name);
            Assert.AreEqual(fixture.Length, result.Row.length, name);
            Assert.AreEqual(fixture.Feature, result.Row.feature, name);
            Assert.AreEqual(fixture.MainBpmBits, DoubleBits(result.Row.mainbpm), name);
            Assert.AreEqual(fixture.MinBpmBits, DoubleBits(result.Row.minbpm), name);
            Assert.AreEqual(fixture.MaxBpmBits, DoubleBits(result.Row.maxbpm), name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    [DataRow("long_distribution")]
    [DataRow("bmson_output")]
    public void ParseBytes_ChartStringRetentionModes_PreserveAllRowBitsDiagnosticsAndBaselineString(string name)
    {
        BoundaryCase fixture = GetCase(name);
        byte[] bytes = Encoding.UTF8.GetBytes(fixture.Input);
        ChartInfoParser.ChartInfoParseResult detailed = ChartInfoParser.ParseBytesDetailed(bytes, fixture.Extension);
        ChartInfoParser.ChartInfoParseResult production = ChartInfoParser.ParseBytesDetailed(bytes, fixture.Extension, retainChartString: false);
        Assert.IsNull(production.ChartString);
        Assert.AreEqual(fixture.ChartString, detailed.ChartString);
        AssertAllRowBitsEqual(detailed.Row, production.Row);
        AssertAllRowBitsEqual(detailed.Row, ChartInfoParser.ParseBytes(bytes, fixture.Extension));
        CollectionAssert.AreEqual(ObserveDiagnostics(detailed), ObserveDiagnostics(production));
        string chartString = detailed.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。");
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(chartString))), detailed.Row.charthash);
    }

    private static void AssertAllRowBitsEqual(ChartDetails expected, ChartDetails actual)
    {
        foreach (PropertyInfo property in typeof(ChartDetails).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == nameof(ChartDetails.updated_at))
            {
                continue;
            }
            object? expectedValue = property.GetValue(expected);
            object? actualValue = property.GetValue(actual);
            if (expectedValue is double expectedDouble && actualValue is double actualDouble)
            {
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(expectedDouble), BitConverter.DoubleToInt64Bits(actualDouble), property.Name);
            }
            else
            {
                Assert.AreEqual(expectedValue, actualValue, property.Name);
            }
        }
    }

    private static string? DoubleBits(double? value) => value.HasValue ? BitConverter.DoubleToInt64Bits(value.Value).ToString("x16", CultureInfo.InvariantCulture) : null;

    private static string[] ObserveDiagnostics(ChartInfoParser.ChartInfoParseResult result) => result.Diagnostics.Select(diagnostic => diagnostic.Severity + "|" + diagnostic.Code + "|" + diagnostic.Message).ToArray();

    private sealed record BoundaryCase(string Input, string Extension, string? ChartString, string? Distribution, string? SpeedChange,
        int Notes, int NormalKeys, int LongKeys, int NormalScratch, int LongScratch, int Length, int Feature, string? MainBpmBits, string? MinBpmBits, string? MaxBpmBits,
        string[] Diagnostics, string? Failure);

    // 19826e8の保存旧DLLから取得。内部索引の実装に依存する期待値は置きません。
    private static BoundaryCase GetCase(string name)
    {
        return name switch
        {
            "future_predecessor" => new BoundaryCase("#BPM 131.4889812233735\r\n#00102:0.1\r\n#00202:0.3\r\n#00103:0000007F\r\n#00111:01010000\r\n#00211:00010001\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(131.4889812233735)L[0,0,0,0,0,0]\n1825:L[1,0,0,0,0,0]\n1870:[1,0,0,0,0,0]\n1962:B(127.0)[0,0,0,0,0,0]\n2009:L[0,0,0,0,0,0]\n2151:[1,0,0,0,0,0]\n2434:[1,0,0,0,0,0]\n", "#00000000000000000000000002000000000000020000000000000000", "131.4889812233735,0.0,127.0,1962.0,127.0,2434.0", 4, 4, 0, 0, 0, 2434, 0, "40606fa5bbf357e9", "405fc00000000000", "40606fa5bbf357e9", ["Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です", "Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です"], null),
            "negative_events" => new BoundaryCase("#BPM 120\r\n#BPM01 180\r\n#STOP01 48\r\n#SCROLL01 0.5\r\n#00102:-1\r\n#00108:0100\r\n#00109:0001\r\n#001SC:0100\r\n#00111:0101\r\n#00211:01\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[1,0,0,0,0,0]\n1000:S(500)[1,0,0,0,0,0]\n2000:B(180.0)L[1,0,0,0,0,0]\n", "#00000000000100000000000001000000000000010000000000000000", "120.0,0.0,0.0,1000.0,90.0,2000.0", 3, 3, 0, 0, 0, 2000, 192, "405e000000000000", "405e000000000000", "4066800000000000", ["Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です"], null),
            "zero_coordinates" => new BoundaryCase("#BPM 120\r\n#00102:-0\r\n#00202:0\r\n#00103:407F\r\n#00111:0101\r\n#00212:01\r\n#00311:01\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n2000:B(127.0)L[1,1,0,0,0,0]\n", "#00000000000000000000000000000000000000020000000000000000", "120.0,0.0,127.0,2000.0", 2, 2, 0, 0, 0, 2000, 0, "405fc00000000000", "405e000000000000", "405fc00000000000", ["Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です"], null),
            "infinite_last" => new BoundaryCase("#BPM 120\r\n#00102:Infinity\r\n#00111:01\r\n", ".bms", null, null, null, 0, 0, 0, 0, 0, 0, 0, null, null, null, [], "BMS timeline time is out of range."),
            "infinite_request" => new BoundaryCase("#BPM 120\r\n#00102:Infinity\r\n#00111:0001\r\n", ".bms", null, null, null, 0, 0, 0, 0, 0, 0, 0, null, null, null, [], "BMS timeline time is out of range."),
            "long_start_overwritten" => new BoundaryCase("#BPM 120\r\n#00151:01000000\r\n#00111:02000300\r\n#00151:00000001\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n2000:L[1,0,0,0,0,0]\n", "#0000000000000000000000000000000000000001000000000000000000000000000000", "120.0,0.0,120.0,3500.0", 1, 1, 0, 0, 0, 3500, 1, "405e000000000000", "405e000000000000", "405e000000000000", [], null),
            "long_reverse" => new BoundaryCase("#BPM 120\r\n#00102:-1\r\n#00111:00000100\r\n#00151:01000001\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n1000:[1,0,0,0,0,0]\n2000:L[0,0,0,0,0,0]\n", "#000000000000000000000000010000000000000000", "120.0,0.0,120.0,2000.0", 1, 1, 0, 0, 0, 1000, 0, "405e000000000000", "405e000000000000", "405e000000000000", ["Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です"], null),
            "covered_endpoint_overwritten" => new BoundaryCase("#BPM 120\r\n#00151:01000100\r\n#00111:02000000\r\n#001D1:00010000\r\n#00151:00010001\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n2000:L[1,0,0,0,0,0]\n", "#0000000000000000000000000000000000000001000000000000000000000000000000", "120.0,0.0,120.0,3500.0", 1, 1, 0, 0, 0, 3000, 1, "405e000000000000", "405e000000000000", "405e000000000000", [], null),
            "diagnostics_order" => new BoundaryCase("#BPM 120\r\n#00111:??0100??\r\n#00103:??4000??\r\n#00199:01????\r\n#00211: 1001\r\n#00311\r\n#004??:01\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n2000:L[0,0,0,0,0,0]\n2500:B(64.0)[1,0,0,0,0,0]\n5312:L[0,0,0,0,0,0]\n9062:L[0,0,0,0,0,0]\n12812:L[0,0,0,0,0,0]\n", "#00000000000000000000000000000000000000010000000000000000", "120.0,0.0,64.0,2500.0,64.0,12812.0", 1, 1, 0, 0, 0, 2500, 0, "4050000000000000", "4050000000000000", "405e000000000000", ["Warning|BMS_CHANNEL_INVALID|チャンネルに不正な値が定義されています", "Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です", "Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です", "Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です", "Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です", "Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です", "Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です", "Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です", "Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です"], null),
            "random_excluded_command" => new BoundaryCase("#BPM 120\r\n#RANDOM 2\r\n#IF 2\r\n#BPM01 broken\r\n#00111:????\r\n#ENDIF\r\n#IF 1\r\n#00111:01\r\n#ENDIF\r\n#ENDRANDOM\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n2000:L[1,0,0,0,0,0]\n", "#00000000000000000000000000000000000000010000000000000000", "120.0,0.0,120.0,2000.0", 1, 1, 0, 0, 0, 2000, 4, "405e000000000000", "405e000000000000", "405e000000000000", [], null),
            "base62_mine" => new BoundaryCase("#BPM 120\r\n#BASE 62\r\n#001D1:az\r\n#00103:az\r\n#00111:0001\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n2000:B(195.0)L[m2293.0,0,0,0,0,0]\n2615:[1,0,0,0,0,0]\n", "#00000000000000000000000000000000000000010100000000000000", "120.0,0.0,195.0,2000.0,195.0,2615.0", 1, 1, 0, 0, 0, 2615, 2, "4068600000000000", "405e000000000000", "4068600000000000", [], null),
            "long_distribution" => new BoundaryCase("#BPM 120\r\n#LNMODE 3\r\n#00151:0101\r\n#00251:01\r\n#02051:01\r\n#02111:01\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\nLNMODE:3\n0:B(120.0)L[0,0,0,0,0,0]\n2000:L[72,0,0,0,0,0]\n4000:L[72,0,0,0,0,0]\n6000:L[0,0,0,0,0,0]\n8000:L[0,0,0,0,0,0]\n10000:L[0,0,0,0,0,0]\n12000:L[0,0,0,0,0,0]\n14000:L[0,0,0,0,0,0]\n16000:L[0,0,0,0,0,0]\n18000:L[0,0,0,0,0,0]\n20000:L[0,0,0,0,0,0]\n22000:L[0,0,0,0,0,0]\n24000:L[0,0,0,0,0,0]\n26000:L[0,0,0,0,0,0]\n28000:L[0,0,0,0,0,0]\n30000:L[0,0,0,0,0,0]\n32000:L[0,0,0,0,0,0]\n34000:L[0,0,0,0,0,0]\n36000:L[0,0,0,0,0,0]\n38000:L[0,0,0,0,0,0]\n40000:L[,0,0,0,0,0]\n42000:L[1,0,0,0,0,0]\n", "#0000000000000000000000000000000000010000000000000100000000000001000000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000000010000000000000100000000000001000000000001000000000000000000000000000000010000000000000000", "120.0,0.0,120.0,42000.0", 5, 1, 4, 0, 0, 42000, 32, "405e000000000000", "405e000000000000", "405e000000000000", [], null),
            "bmson_output" => new BoundaryCase("{\"version\":\"1.0.0\",\"info\":{\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"total\":100},\"resolution\":240,\"sound_channels\":[{\"name\":\"a.wav\",\"notes\":[{\"x\":1,\"y\":240,\"l\":480,\"c\":false}]}],\"bpm_events\":[{\"y\":480,\"bpm\":131.4889812233735}]}", ".bmson", "JUDGERANK:100\nTOTAL:260.0\n0:B(120.0)[0,0,0,0,0,0,0,0]\n500:[108,0,0,0,0,0,0,0]\n1000:B(131.4889812233735)[0,0,0,0,0,0,0,0]\n", "#000000010000000000000001000000000000000000", "120.0,0.0,131.4889812233735,1000.0,131.4889812233735,1456.0", 1, 0, 1, 0, 0, 1456, 1, "405e000000000000", "405e000000000000", "40606fa5bbf357e9", [], null),
            "infinite_unrequested" => new BoundaryCase("#BPM 120\r\n#00102:Infinity\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n2000:L[0,0,0,0,0,0]\n", "#0000000000000000000000000000", "120.0,0.0,120.0,2000.0", 0, 0, 0, 0, 0, 0, 0, "0000000000000000", "405e000000000000", "405e000000000000", [], null),
            "predecessor_minimum" => new BoundaryCase("#BPM 120\r\n#BPM01 180\r\n#BPM02 90\r\n#00102:-4\r\n#00108:00000102\r\n#00111:01\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n-3333:B(90.0)[0,0,0,0,0,0]\n-2000:B(180.0)[0,0,0,0,0,0]\n0:B(120.0)L[0,0,0,0,0,0]\n2000:L[1,0,0,0,0,0]\n", "#00000000000000000000000000000000000000010000000000000000", "120.0,0.0,90.0,-3333.0,180.0,-2000.0,120.0,0.0,120.0,2000.0", 1, 1, 0, 0, 0, 2000, 0, "405e000000000000", "4056800000000000", "4066800000000000", ["Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です"], null),
            "lnobj_previous_mine" => new BoundaryCase("#BPM 120\r\n#LNOBJ ZZ\r\n#00111:01000000\r\n#001D1:00010000\r\n#00111:000000ZZ\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n2000:L[1,0,0,0,0,0]\n2500:[m1.0,0,0,0,0,0]\n", "#00000000000000000000000000000000000000010100000000000000", "120.0,0.0,120.0,3500.0", 1, 1, 0, 0, 0, 2500, 2, "405e000000000000", "405e000000000000", "405e000000000000", [], null),
            "covered_endpoint_removed" => new BoundaryCase("#BPM 120\r\n#00151:01000100\r\n#00202:-1\r\n#00251:01010000\r\n#002D1:00000100\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n2000:L[0,0,0,0,0,0]\n4000:L[0,0,0,0,0,0]\n", "#0000000000000000000000000000", "120.0,0.0,120.0,4000.0", 0, 0, 0, 0, 0, 0, 0, "0000000000000000", "405e000000000000", "405e000000000000", ["Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です"], null),
            "lnobj_paired_blocks_older_normal" => new BoundaryCase("#BPM 120\r\n#LNOBJ ZZ\r\n#00011:0001\r\n#00111:0100ZZ00\r\n#00111:000000ZZ\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,0,0,0,0,0]\n1000:[1,0,0,0,0,0]\n2000:L[108,0,0,0,0,0]\n", "#0000000000000000000000000100000000010000000000000001000000000000000000", "120.0,0.0,120.0,3500.0", 2, 1, 1, 0, 0, 3000, 1, "405e000000000000", "405e000000000000", "405e000000000000", [], null),
            "pending_removal_uses_current_owner_cells" => new BoundaryCase("#BPM 120\r\n#00052:0101\r\n#00102:-2\r\n#00151:01\r\n#00152:0100000000000000\r\n#00111:02\r\n#00112:02\r\n#00152:0000000100000000\r\n", ".bms", "JUDGERANK:75\nTOTAL:260.0\n0:B(120.0)L[0,108,0,0,0,0]\n2000:L[0,0,0,0,0,0]\n", "#000000010000000000000001000000000000000000", "120.0,0.0,120.0,2000.0", 1, 0, 1, 0, 0, 1000, 1, "405e000000000000", "405e000000000000", "405e000000000000", ["Warning|BMS_CHANNEL_DATA_INVALID|チャンネル定義中の不正な値です"], null),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
    }
}
