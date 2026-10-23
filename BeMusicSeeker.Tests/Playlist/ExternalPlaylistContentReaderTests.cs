using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>展開後の実受信量と、分割された先頭判定を小さな上限で検証します。実取得・保存は外部表試験に分担します。</summary>
[TestClass]
public sealed class ExternalPlaylistContentReaderTests
{
    /// <summary>BOM、空白、先頭文字を別readに分けても種別を維持し、BOMと空白を含むexactを許可し+1を拒否します。</summary>
    [DataTestMethod]
    [DataRow("\ufeff \n{}", true)]
    [DataRow("\ufeff \n[]", true)]
    [DataRow("\ufeff <html>", false)]
    [DataRow("            {}", true)]
    public async Task SplitPrefix_CountsAllBytesAndEnforcesExactBound(string text, bool isJson)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        using var exact = new ChunkStream(bytes, 1);
        (string Text, bool IsJson) result = await ExternalPlaylistContentReader.ReadAsync(exact, true, CancellationToken.None,
            htmlLimitBytes: isJson ? 4 : bytes.Length, jsonLimitBytes: bytes.Length);
        Assert.AreEqual(isJson, result.IsJson);
        Assert.AreEqual(text.TrimStart('\ufeff'), result.Text);
        using var excess = new ChunkStream([.. bytes, (byte)' '], 1);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => ExternalPlaylistContentReader.ReadAsync(
            excess, true, CancellationToken.None, isJson ? 4 : bytes.Length, bytes.Length));
        Assert.AreEqual(bytes.Length + 1, excess.BytesRead);
    }

    /// <summary>種別不明な空白はJSON上限で止まり、長い空白後のHTML判定では累積量を戻さず次のreadを始めません。</summary>
    [DataTestMethod]
    [DataRow("                     ", 13)]
    [DataRow("      <meta>", 7)]
    public async Task UnknownOrHtmlPrefix_StopsAtApplicableCumulativeBound(string text, int readBytes)
    {
        using var source = new ChunkStream(Encoding.UTF8.GetBytes(text), 1);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => ExternalPlaylistContentReader.ReadAsync(
            source, true, CancellationToken.None, htmlLimitBytes: 4, jsonLimitBytes: 12));
        Assert.AreEqual(readBytes, source.BytesRead);
    }

    /// <summary>固定JSONはHTMLに見える内容でもJSON上限を使い、残り+1だけを要求してそれ以上読みません。</summary>
    [TestMethod]
    public async Task FixedJson_ReadsAtMostRemainingPlusOne()
    {
        using var source = new ChunkStream(Encoding.UTF8.GetBytes("<html> extra"), 100);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => ExternalPlaylistContentReader.ReadAsync(
            source, false, CancellationToken.None, htmlLimitBytes: 2, jsonLimitBytes: 8));
        Assert.AreEqual(9, source.BytesRead);
    }

    private sealed class ChunkStream(byte[] content, int chunkSize) : MemoryStream(content, writable: false)
    {
        internal int BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Read(buffer.Span[..Math.Min(chunkSize, buffer.Length)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }
}
