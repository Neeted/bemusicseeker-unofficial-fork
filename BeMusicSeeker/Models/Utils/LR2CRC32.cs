namespace BeMusicSeeker.Models.Utils;

public static class LR2CRC32
{
    public static uint Compute(byte[] data)
    {
        uint num = uint.MaxValue;
        for (int i = 0; i < data.Length; i++)
        {
            uint num2 = (uint)(sbyte)data[i];
            num ^= num2;
            for (int j = 0; j < 8; j++)
            {
                num = (num >> 1) ^ ((0 - (num & 1)) & 0xEDB88320u);
            }
        }
        return ~num;
    }
}
