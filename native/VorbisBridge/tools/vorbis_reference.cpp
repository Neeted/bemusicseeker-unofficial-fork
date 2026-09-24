#include <vorbis/vorbisfile.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <limits>

int main(int argc, char** argv)
{
    if (argc != 3)
    {
        std::fprintf(stderr, "usage: vorbis_reference <input.ogg> <output.interleaved-f32>\n");
        return 2;
    }

    FILE* input = nullptr;
    if (fopen_s(&input, argv[1], "rb") != 0 || input == nullptr)
    {
        std::fprintf(stderr, "cannot open input: %s\n", argv[1]);
        return 3;
    }

    OggVorbis_File file{};
    const int open_status = ov_open(input, &file, nullptr, 0);
    if (open_status != 0)
    {
        std::fclose(input);
        std::fprintf(stderr, "ov_open failed: %d\n", open_status);
        return 4;
    }

    FILE* output = nullptr;
    if (fopen_s(&output, argv[2], "wb") != 0 || output == nullptr)
    {
        ov_clear(&file);
        std::fprintf(stderr, "cannot open output: %s\n", argv[2]);
        return 5;
    }

    const int links = ov_streams(&file);
    vorbis_info* first = links > 0 ? ov_info(&file, 0) : nullptr;
    if (first == nullptr || first->channels <= 0 || first->rate <= 0)
    {
        std::fclose(output);
        ov_clear(&file);
        return 6;
    }
    const long sample_rate = first->rate;
    const int channels = first->channels;

    std::uint64_t frames_written = 0;
    for (;;)
    {
        float** planar = nullptr;
        int link = 0;
        const long frames = ov_read_float(&file, &planar, 4096, &link);
        if (frames == 0)
        {
            break;
        }
        if (frames < 0 || planar == nullptr || link < 0 || link >= links)
        {
            std::fprintf(stderr, "ov_read_float failed: %ld\n", frames);
            std::fclose(output);
            ov_clear(&file);
            return 7;
        }

        vorbis_info* current = ov_info(&file, link);
        if (current == nullptr || current->rate != sample_rate || current->channels != channels)
        {
            std::fprintf(stderr, "reference utility requires a same-format Vorbis chain\n");
            std::fclose(output);
            ov_clear(&file);
            return 8;
        }

        for (long frame = 0; frame < frames; ++frame)
        {
            for (int channel = 0; channel < channels; ++channel)
            {
                const float sample = planar[channel][frame];
                if (std::fwrite(&sample, sizeof(sample), 1, output) != 1)
                {
                    std::fclose(output);
                    ov_clear(&file);
                    return 9;
                }
            }
        }
        frames_written += static_cast<std::uint64_t>(frames);
    }

    std::fclose(output);
    ov_clear(&file);
    std::fprintf(stderr, "libvorbis=1.3.7 rate=%ld channels=%d frames=%llu\n",
        sample_rate, channels, static_cast<unsigned long long>(frames_written));
    return 0;
}
