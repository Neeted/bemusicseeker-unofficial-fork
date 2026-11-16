using System.Text;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>保存済みpathからLR2の参照キーを求めます。製品のCRC・末尾補正を期待値に使いません。</summary>
internal static class Lr2StoredPathReference
{
    /// <summary>DBから読んだpathをそのままCP932化し、NUL一つだけを追加してOpenLR2の符号付きchar計算を行います。</summary>
    internal static string Compute(string storedPath)
    {
        uint crc = 0xffffffff;
        foreach (byte value in Encoding.GetEncoding(932).GetBytes(storedPath + "\0"))
        {
            crc ^= unchecked((uint)(sbyte)value);
            for (int bit = 0; bit != 8; ++bit)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
            }
        }
        return (~crc).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }
}
