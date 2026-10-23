using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models;

/// <summary>外部表の展開済み本文を、種別に応じたバイト上限内で読み取ってから復号します。</summary>
internal static class ExternalPlaylistContentReader
{
    /// <summary>初回HTMLに適用する展開済み本文の上限です。</summary>
    internal const int HtmlLimitBytes = 10 * 1024 * 1024;
    /// <summary>種別不明の初回入力と各JSONに適用する展開済み本文の上限です。</summary>
    internal const int JsonLimitBytes = 100 * 1024 * 1024;

    /// <summary>初回入力はBOM・空白後の先頭文字で判定し、固定JSONはJSON上限を使います。上限超過は解析前に失敗します。</summary>
    internal static async Task<(string Text, bool IsJson)> ReadAsync(
        Stream source, bool detectKind, CancellationToken cancellationToken,
        int htmlLimitBytes = HtmlLimitBytes, int jsonLimitBytes = JsonLimitBytes)
    {
        using var content = new MemoryStream();
        byte[] buffer = new byte[81920];
        bool? isJson = detectKind ? null : true;
        int bomBytes = 0;
        while (true)
        {
            int limit = isJson == false ? htmlLimitBytes : jsonLimitBytes;
            int count = await source.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, (long)limit + 1 - content.Length)), cancellationToken).ConfigureAwait(false);
            if (count == 0) { break; }
            if (isJson == null)
            {
                foreach (byte value in buffer.AsSpan(0, count))
                {
                    if (bomBytes != 0)
                    {
                        if ((bomBytes == 1 && value == 0xbb) || (bomBytes == 2 && value == 0xbf))
                        {
                            bomBytes = (bomBytes + 1) % 3;
                            continue;
                        }
                        isJson = false;
                        break;
                    }
                    if (value is 0x20 or 0x09 or 0x0d or 0x0a) { continue; }
                    if (value == 0xef) { bomBytes = 1; continue; }
                    isJson = value is (byte)'{' or (byte)'[';
                    break;
                }
            }
            limit = isJson == false ? htmlLimitBytes : jsonLimitBytes;
            if (content.Length + count > limit)
            {
                throw new InvalidDataException(string.Format(BeMusicSeeker.Properties.Resources.Error_ExternalPlaylistBodySizeLimitExceeded, limit));
            }
            content.Write(buffer, 0, count);
        }
        if (isJson != true && content.Length > htmlLimitBytes)
        {
            throw new InvalidDataException(string.Format(BeMusicSeeker.Properties.Resources.Error_ExternalPlaylistBodySizeLimitExceeded, htmlLimitBytes));
        }
        byte[] bytes = content.GetBuffer();
        int start = content.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return (Encoding.UTF8.GetString(bytes, start, (int)content.Length - start), isJson == true);
    }
}
