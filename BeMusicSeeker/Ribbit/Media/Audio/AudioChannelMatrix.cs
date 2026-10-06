using System;

namespace Ribbit.Media.Audio;

/// <summary>BASS 標準 speaker 配置間の固定 routing 行列を作成します。</summary>
internal static class AudioChannelMatrix
{
    private const float CenterCoefficient = 0.70710677f;

    /// <summary>入力・出力の配置に基づく mixer 出力行 × source 入力列の行列を作成します。</summary>
    internal static float[,] Create(AudioChannelLayout sourceLayout, AudioChannelLayout outputLayout)
    {
        ArgumentNullException.ThrowIfNull(sourceLayout);
        ArgumentNullException.ThrowIfNull(outputLayout);
        EnsureSupported(sourceLayout);
        EnsureSupported(outputLayout);

        if (outputLayout.ChannelCount == 1)
        {
            float[,] stereo = Create(sourceLayout, AudioChannelLayout.CreateBassOutput(2));
            float[,] mono = new float[1, sourceLayout.ChannelCount];
            for (int source = 0; source < sourceLayout.ChannelCount; source++)
            {
                mono[0, source] = (stereo[0, source] + stereo[1, source]) * 0.5f;
            }
            return mono;
        }

        float[,] matrix = new float[outputLayout.ChannelCount, sourceLayout.ChannelCount];
        bool duplicateMonoToFront = sourceLayout.ChannelCount == 1
            && sourceLayout[0] == AudioSpeakerPosition.FrontCenter;
        for (int source = 0; source < sourceLayout.ChannelCount; source++)
        {
            AudioSpeakerPosition speaker = sourceLayout[source];
            int matchingOutput = IndexOf(outputLayout, speaker);
            if (matchingOutput >= 0)
            {
                matrix[matchingOutput, source] = 1f;
                continue;
            }

            switch (speaker)
            {
                case AudioSpeakerPosition.FrontLeft:
                    Add(matrix, source, outputLayout, AudioSpeakerPosition.FrontLeft, 1f);
                    break;
                case AudioSpeakerPosition.FrontRight:
                    Add(matrix, source, outputLayout, AudioSpeakerPosition.FrontRight, 1f);
                    break;
                case AudioSpeakerPosition.FrontCenter:
                    float centerCoefficient = duplicateMonoToFront ? 1f : CenterCoefficient;
                    Add(matrix, source, outputLayout, AudioSpeakerPosition.FrontLeft, centerCoefficient);
                    Add(matrix, source, outputLayout, AudioSpeakerPosition.FrontRight, centerCoefficient);
                    break;
                case AudioSpeakerPosition.LowFrequency:
                    // LFE は同じ speaker を持つ出力がない限り破棄し、front へ混ぜません。
                    break;
                case AudioSpeakerPosition.BackLeft:
                case AudioSpeakerPosition.SideLeft:
                    Add(matrix, source, outputLayout, AudioSpeakerPosition.FrontLeft, CenterCoefficient);
                    break;
                case AudioSpeakerPosition.BackRight:
                case AudioSpeakerPosition.SideRight:
                    Add(matrix, source, outputLayout, AudioSpeakerPosition.FrontRight, CenterCoefficient);
                    break;
                case AudioSpeakerPosition.BackCenter:
                    Add(matrix, source, outputLayout, AudioSpeakerPosition.FrontLeft, 0.5f);
                    Add(matrix, source, outputLayout, AudioSpeakerPosition.FrontRight, 0.5f);
                    break;
                default:
                    throw UnsupportedSpeaker(speaker);
            }
        }

        return matrix;
    }

    private static void Add(
        float[,] matrix,
        int sourceChannel,
        AudioChannelLayout outputLayout,
        AudioSpeakerPosition outputSpeaker,
        float coefficient)
    {
        int outputChannel = IndexOf(outputLayout, outputSpeaker);
        if (outputChannel < 0)
        {
            throw new ArgumentException("The output layout has no front speaker for the required downmix.", nameof(outputLayout));
        }
        matrix[outputChannel, sourceChannel] += coefficient;
    }

    private static int IndexOf(AudioChannelLayout layout, AudioSpeakerPosition speaker)
    {
        for (int channel = 0; channel < layout.ChannelCount; channel++)
        {
            if (layout[channel] == speaker)
            {
                return channel;
            }
        }
        return -1;
    }

    private static void EnsureSupported(AudioChannelLayout layout)
    {
        if (layout.ChannelCount > 8)
        {
            throw new ArgumentException("Audio routing supports at most eight channels.", nameof(layout));
        }
        for (int channel = 0; channel < layout.ChannelCount; channel++)
        {
            AudioSpeakerPosition speaker = layout[channel];
            if (speaker is not (AudioSpeakerPosition.FrontLeft
                or AudioSpeakerPosition.FrontRight
                or AudioSpeakerPosition.FrontCenter
                or AudioSpeakerPosition.LowFrequency
                or AudioSpeakerPosition.BackLeft
                or AudioSpeakerPosition.BackRight
                or AudioSpeakerPosition.BackCenter
                or AudioSpeakerPosition.SideLeft
                or AudioSpeakerPosition.SideRight))
            {
                throw UnsupportedSpeaker(speaker);
            }
        }
    }

    private static ArgumentException UnsupportedSpeaker(AudioSpeakerPosition speaker)
        => new($"Audio routing does not define a matrix for speaker position {speaker}.");
}
