using System;
using System.IO;

namespace Ribbit.Media;

internal static class WavFile
{
    private static readonly byte[] RIFF_HEADER = [82, 73, 70, 70];

    private static readonly byte[] FORMAT_WAVE = [87, 65, 86, 69];

    private static readonly byte[] FORMAT_TAG = [102, 109, 116, 32];

    private static readonly byte[] AUDIO_FORMAT = [1, 0];

    private static readonly byte[] SUBCHUNK_ID = [100, 97, 116, 97];

    private const int BYTES_PER_SAMPLE = 2;

    public static void WriteHeader(Stream targetStream, int byteStreamSize, int channelCount, int sampleRate)
    {
        int source = sampleRate * channelCount * 2;
        int source2 = channelCount * 2;
        targetStream.Write(RIFF_HEADER, 0, RIFF_HEADER.Length);
        targetStream.Write(PackageInt(byteStreamSize + 36, 4), 0, 4);
        targetStream.Write(FORMAT_WAVE, 0, FORMAT_WAVE.Length);
        targetStream.Write(FORMAT_TAG, 0, FORMAT_TAG.Length);
        targetStream.Write(PackageInt(16, 4), 0, 4);
        targetStream.Write(AUDIO_FORMAT, 0, AUDIO_FORMAT.Length);
        targetStream.Write(PackageInt(channelCount), 0, 2);
        targetStream.Write(PackageInt(sampleRate, 4), 0, 4);
        targetStream.Write(PackageInt(source, 4), 0, 4);
        targetStream.Write(PackageInt(source2), 0, 2);
        targetStream.Write(PackageInt(16), 0, 2);
        targetStream.Write(SUBCHUNK_ID, 0, SUBCHUNK_ID.Length);
        targetStream.Write(PackageInt(byteStreamSize, 4), 0, 4);
    }

    private static byte[] PackageInt(int source, int length = 2)
    {
        if (length != 2 && length != 4)
        {
            throw new ArgumentException("length must be either 2 or 4", "length");
        }
        byte[] array = new byte[length];
        array[0] = (byte)(source & 0xFF);
        array[1] = (byte)((source >> 8) & 0xFF);
        if (length == 4)
        {
            array[2] = (byte)((source >> 16) & 0xFF);
            array[3] = (byte)((source >> 24) & 0xFF);
        }
        return array;
    }
}
