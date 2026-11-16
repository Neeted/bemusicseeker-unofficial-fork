using System.Text;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>製品の復号・ハッシュ計算を期待値生成に使わない、導入引継ぎ専用の固定入力です。</summary>
internal static class InstallEncodingTestInput
{
    /// <summary>固定bytesに対する独立したMD5参照値です。</summary>
    internal const string Md5 = "c5798b8f3af98af0d76c20f4bb0e0160";
    /// <summary>固定bytesに対する独立したSHA256参照値です。</summary>
    internal const string Sha256 = "519e4cf6baffa78b545104386e442332181fd400fa6e92b557090335cad4c12a";
    /// <summary>既存の文字コード引継ぎ保証で使う同一bytesを返します。</summary>
    internal static byte[] Bytes => [.. Encoding.ASCII.GetBytes("#TITLE "), 0xa4, 0xa4, 0xa4, 0xe5,
        .. Encoding.ASCII.GetBytes("\r\n#ARTIST artist\r\n#GENRE genre\r\n#BPM 120\r\n#WAV01 tone.wav\r\n#00111:01\r\n")];

    /// <summary>同じ固定bytesをケース所有領域へ書き、実解析の入力にします。</summary>
    internal static string Write(string directory, string name = "chart.bms")
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Bytes);
        return path;
    }

    /// <summary>固定bytesの文字コード・hash・titleが保持されることを照合します。</summary>
    internal static void AssertFixed(ChartFile chart, string path)
    {
        Assert.AreEqual(path, chart.Path);
        Assert.AreEqual(Md5, chart.Md5);
        Assert.AreEqual("中文", chart.RawTitle);
        Assert.AreEqual("big5", chart.EncodingName);
        Assert.IsNotNull(chart.ResourceHealthMaintenanceSnapshot);
        Assert.AreEqual(path, chart.ResourceHealthMaintenanceSnapshot.Path);
        Assert.AreEqual(Md5, chart.ResourceHealthMaintenanceSnapshot.Hash);
        Assert.IsTrue(chart.ResourceHealthMaintenanceSnapshot.EncodingFixed);
    }
}
