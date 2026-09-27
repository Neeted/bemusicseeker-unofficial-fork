#include <ogg/ogg.h>
#include <vorbis/vorbisfile.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <new>
#include <string>

#if defined(_WIN32)
#define BMS_EXPORT extern "C" __declspec(dllexport)
#define BMS_CALL __cdecl
#else
#define BMS_EXPORT extern "C" __attribute__((visibility("default")))
#define BMS_CALL
#endif

namespace
{
    constexpr std::int32_t abi_version = 1;
    constexpr std::int32_t status_ok = 0;
    constexpr std::int32_t status_invalid_argument = 1;
    constexpr std::int32_t status_unsupported = 3;
    constexpr std::int32_t status_vorbis = 4;
    constexpr std::int32_t status_format_change = 5;
    constexpr std::int32_t status_out_of_memory = 7;

    struct memory_input
    {
        const unsigned char* bytes{};
        std::size_t length{};
        std::size_t position{};
    };

    struct decoder
    {
        OggVorbis_File file{};
        memory_input input{};
        std::int32_t sample_rate{};
        std::int32_t channels{};
        std::int32_t link_count{};
        std::int64_t total_frames{};
        bool opened{};
    };

    std::size_t memory_read(void* destination, std::size_t element_size, std::size_t element_count, void* datasource)
    {
        auto* input = static_cast<memory_input*>(datasource);
        if (input == nullptr || destination == nullptr || element_size == 0 || element_count == 0)
        {
            return 0;
        }

        const std::size_t remaining = input->length - input->position;
        if (element_count > std::numeric_limits<std::size_t>::max() / element_size)
        {
            return 0;
        }

        const std::size_t requested = element_size * element_count;
        const std::size_t count = std::min(remaining, requested);
        const std::size_t complete_elements = count / element_size;
        const std::size_t complete_bytes = complete_elements * element_size;
        if (complete_bytes != 0)
        {
            std::memcpy(destination, input->bytes + input->position, complete_bytes);
            input->position += complete_bytes;
        }
        return complete_elements;
    }

    int memory_seek(void* datasource, ogg_int64_t offset, int whence)
    {
        auto* input = static_cast<memory_input*>(datasource);
        if (input == nullptr)
        {
            return -1;
        }

        std::int64_t base{};
        switch (whence)
        {
        case SEEK_SET:
            base = 0;
            break;
        case SEEK_CUR:
            base = static_cast<std::int64_t>(input->position);
            break;
        case SEEK_END:
            base = static_cast<std::int64_t>(input->length);
            break;
        default:
            return -1;
        }

        if (offset < -base || offset > std::numeric_limits<std::int64_t>::max() - base)
        {
            return -1;
        }
        const std::int64_t next = base + offset;
        if (next < 0 || static_cast<std::uint64_t>(next) > input->length)
        {
            return -1;
        }

        input->position = static_cast<std::size_t>(next);
        return 0;
    }

    long memory_tell(void* datasource)
    {
        auto* input = static_cast<memory_input*>(datasource);
        if (input == nullptr || input->position > static_cast<std::size_t>(LONG_MAX))
        {
            return -1;
        }
        return static_cast<long>(input->position);
    }

    int vorbis_layout_supported(std::int32_t channels)
    {
        // Vorbis mapping family 0がスピーカー順を定義するのは8チャンネルまでです。
        return channels >= 1 && channels <= 8;
    }

    std::int32_t get_link_format(OggVorbis_File* file, int link, std::int32_t* sample_rate, std::int32_t* channels)
    {
        vorbis_info* info = ov_info(file, link);
        if (info == nullptr || info->rate <= 0 || info->channels <= 0
            || info->rate > std::numeric_limits<std::int32_t>::max()
            || info->channels > std::numeric_limits<std::int32_t>::max())
        {
            return status_vorbis;
        }

        if (!vorbis_layout_supported(static_cast<std::int32_t>(info->channels)))
        {
            return status_unsupported;
        }

        *sample_rate = static_cast<std::int32_t>(info->rate);
        *channels = static_cast<std::int32_t>(info->channels);
        return status_ok;
    }

    void release_decoder(decoder* value)
    {
        if (value == nullptr)
        {
            return;
        }
        if (value->opened)
        {
            ov_clear(&value->file);
        }
        delete value;
    }
}

BMS_EXPORT std::int32_t BMS_CALL bms_vorbis_get_abi_version()
{
    return abi_version;
}

BMS_EXPORT const char* BMS_CALL bms_vorbis_get_build_info()
{
    static const std::string value = []
    {
        char compiler[64]{};
        std::snprintf(compiler, sizeof(compiler), "MSVC-%d.%d", _MSC_VER, _MSC_FULL_VER);
        return std::string("abi=1;libogg=1.3.6;libvorbis=1.3.7;compiler=") + compiler
            + ";arch=x64;config=Release;crt=static;fp=precise";
    }();
    return value.c_str();
}

BMS_EXPORT std::int32_t BMS_CALL bms_vorbis_open_memory(
    const unsigned char* bytes,
    std::uint64_t length,
    void** result_handle,
    std::int32_t* native_error)
{
    if (result_handle == nullptr || native_error == nullptr || bytes == nullptr || length == 0
        || length > static_cast<std::uint64_t>(LONG_MAX))
    {
        if (result_handle != nullptr)
        {
            *result_handle = nullptr;
        }
        if (native_error != nullptr)
        {
            *native_error = 0;
        }
        return status_invalid_argument;
    }

    *result_handle = nullptr;
    *native_error = 0;
    const auto input_length = static_cast<std::size_t>(length);
    auto* value = new (std::nothrow) decoder{};
    if (value == nullptr)
    {
        return status_out_of_memory;
    }
    value->input.bytes = bytes;
    value->input.length = input_length;

    ov_callbacks callbacks{};
    callbacks.read_func = memory_read;
    callbacks.seek_func = memory_seek;
    callbacks.close_func = nullptr;
    callbacks.tell_func = memory_tell;
    const int open_status = ov_open_callbacks(&value->input, &value->file, nullptr, 0, callbacks);
    if (open_status != 0)
    {
        *native_error = open_status;
        release_decoder(value);
        return status_vorbis;
    }
    value->opened = true;

    value->link_count = ov_streams(&value->file);
    if (value->link_count <= 0)
    {
        release_decoder(value);
        return status_vorbis;
    }

    // ov_open_callbacks may finish its seekable link scan at the final link.
    // Rewind explicitly so sequential decoding starts at the first logical stream.
    const int seek_status = ov_pcm_seek(&value->file, 0);
    if (seek_status != 0)
    {
        *native_error = seek_status;
        release_decoder(value);
        return status_vorbis;
    }

    std::int32_t sample_rate = 0;
    std::int32_t channels = 0;
    const std::int32_t format_status = get_link_format(&value->file, 0, &sample_rate, &channels);
    if (format_status != status_ok)
    {
        release_decoder(value);
        return format_status;
    }
    value->sample_rate = sample_rate;
    value->channels = channels;
    const ogg_int64_t frame_hint = ov_pcm_total(&value->file, -1);
    value->total_frames = frame_hint > 0 ? frame_hint : 0;

    *result_handle = value;
    return status_ok;
}

BMS_EXPORT std::int32_t BMS_CALL bms_vorbis_get_info(
    void* handle,
    std::int32_t* sample_rate,
    std::int32_t* channels,
    std::int64_t* frame_count,
    std::int32_t* link_count)
{
    auto* value = static_cast<decoder*>(handle);
    if (value == nullptr || sample_rate == nullptr || channels == nullptr
        || frame_count == nullptr || link_count == nullptr)
    {
        return status_invalid_argument;
    }
    *sample_rate = value->sample_rate;
    *channels = value->channels;
    *frame_count = value->total_frames;
    *link_count = value->link_count;
    return status_ok;
}

BMS_EXPORT std::int32_t BMS_CALL bms_vorbis_read_frames(
    void* handle,
    float* interleaved,
    std::int32_t capacity_frames,
    std::int64_t* read_frames,
    std::int32_t* native_error)
{
    auto* value = static_cast<decoder*>(handle);
    if (value == nullptr || interleaved == nullptr || capacity_frames <= 0
        || read_frames == nullptr || native_error == nullptr)
    {
        return status_invalid_argument;
    }

    *read_frames = 0;
    *native_error = 0;
    float** planar = nullptr;
    int link = 0;
    long frame_count{};
    do
    {
        frame_count = ov_read_float(&value->file, &planar, capacity_frames, &link);
    } while (frame_count == OV_HOLE);
    if (frame_count < 0)
    {
        *native_error = static_cast<std::int32_t>(frame_count);
        return status_vorbis;
    }
    if (frame_count == 0)
    {
        return status_ok;
    }
    if (planar == nullptr || link < 0 || link >= value->link_count
        || frame_count > capacity_frames || frame_count > std::numeric_limits<std::int32_t>::max())
    {
        return status_vorbis;
    }

    std::int32_t sample_rate = 0;
    std::int32_t channels = 0;
    const std::int32_t format_status = get_link_format(&value->file, link, &sample_rate, &channels);
    if (format_status != status_ok)
    {
        return format_status;
    }
    if (sample_rate != value->sample_rate || channels != value->channels)
    {
        return status_format_change;
    }

    for (long frame = 0; frame < frame_count; ++frame)
    {
        for (std::int32_t channel = 0; channel < value->channels; ++channel)
        {
            interleaved[frame * value->channels + channel] = planar[channel][frame];
        }
    }
    *read_frames = frame_count;
    return status_ok;
}

BMS_EXPORT void BMS_CALL bms_vorbis_close(void* handle)
{
    release_decoder(static_cast<decoder*>(handle));
}
