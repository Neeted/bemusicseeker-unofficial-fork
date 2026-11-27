using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models;

/// <summary>外部表の展開済み本文を、種別に応じたバイト上限内で読み取ってから復号します。</summary>
internal static class ExternalPlaylistContentReader
{
    /// <summary>
    /// 初回入力はBOM・空白後の先頭文字で判定し、固定JSONはJSON上限を使います。
    /// 種別が確定するまでは両上限の大きい方まで読み、確定した時点で、それまでの累積量（BOMと空白を含む）を該当する上限と比べます。
    /// 上限超過は解析前に失敗します。
    /// </summary>
    /// <param name="source">展開後の本文ストリーム。</param>
    /// <param name="detectKind">先頭文字でHTMLとJSONを判定するか。falseではJSONとして読みます。</param>
    /// <param name="htmlLimitBytes">HTMLとして確定した本文の上限（バイト）。</param>
    /// <param name="jsonLimitBytes">JSONとして確定した本文の上限（バイト）。</param>
    /// <param name="cancellationToken">読取りを取り消すトークン。</param>
    internal static async Task<(string Text, bool IsJson)> ReadAsync(
        Stream source, bool detectKind, int htmlLimitBytes, int jsonLimitBytes, CancellationToken cancellationToken)
    {
        int LimitFor(bool? kind) => kind switch { true => jsonLimitBytes, false => htmlLimitBytes, null => Math.Max(htmlLimitBytes, jsonLimitBytes) };
        using var content = new MemoryStream();
        byte[] buffer = new byte[81920];
        bool? isJson = detectKind ? null : true;
        int bomBytes = 0;
        while (true)
        {
            int limit = LimitFor(isJson);
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
            limit = LimitFor(isJson);
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
