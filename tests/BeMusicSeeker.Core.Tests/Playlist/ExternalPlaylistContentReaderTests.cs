using System.Text;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Tests;


/// <summary>展開後の実受信量と、分割された先頭判定を小さな上限で検証します。実取得・保存は外部表試験に分担します。</summary>
[TestClass]
public sealed class ExternalPlaylistContentReaderTests
{
    /// <summary>BOM、空白、先頭文字を別readに分けても種別を維持し、BOMと空白を含むexactを許可し+1を拒否します。</summary>
    [TestMethod]
    [DataRow("\ufeff \n{}", true)]
    [DataRow("\ufeff \n[]", true)]
    [DataRow("\ufeff <html>", false)]
    [DataRow("            {}", true)]
    public async Task SplitPrefix_CountsAllBytesAndEnforcesExactBound(string text, bool isJson)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        using var exact = new ChunkStream(bytes, 1);
        (string Text, bool IsJson) result = await ExternalPlaylistContentReader.ReadAsync(exact, true,
            htmlLimitBytes: isJson ? 4 : bytes.Length, jsonLimitBytes: bytes.Length, CancellationToken.None);
        Assert.AreEqual(isJson, result.IsJson);
        Assert.AreEqual(text.TrimStart('\ufeff'), result.Text);
        using var excess = new ChunkStream([.. bytes, (byte)' '], 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ExternalPlaylistContentReader.ReadAsync(
            excess, true, isJson ? 4 : bytes.Length, bytes.Length, CancellationToken.None));
        Assert.AreEqual(bytes.Length + 1, excess.BytesRead);
    }

    /// <summary>種別未判定の間はHTMLとJSONの上限の大きい方（この行ではJSON）まで読み、長い空白後のHTML判定では累積量を戻さず次のreadを始めません。</summary>
    [TestMethod]
    [DataRow("                     ", 13)]
    [DataRow("      <meta>", 7)]
    public async Task UnknownOrHtmlPrefix_StopsAtApplicableCumulativeBound(string text, int readBytes)
    {
        using var source = new ChunkStream(Encoding.UTF8.GetBytes(text), 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ExternalPlaylistContentReader.ReadAsync(
            source, true, htmlLimitBytes: 4, jsonLimitBytes: 12, CancellationToken.None));
        Assert.AreEqual(readBytes, source.BytesRead);
    }

    /// <summary>HTML上限がJSON上限より大きい場合も、種別未判定の間は大きい方（HTML=12）まで読み、確定後の累積量を該当する上限（JSON=4）と比べます。</summary>
    /// <remarks>
    /// 空白だけは大きい方を超える13バイト目で停止し、空白6バイトの後の <c>{</c> は累積量がJSON上限を超えるため拒否し、
    /// 空白後の <c>&lt;</c> はHTML上限ちょうどの12バイトまで受理します。未判定の間もJSON上限で止める誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow("                  ", false, 13)]
    [DataRow("      {", false, 7)]
    [DataRow("  <         ", true, 12)]
    [DataRow("  <          ", false, 13)]
    public async Task UnknownKind_WithLargerHtmlLimit_UsesLargerBoundUntilDetermined(string text, bool accepted, int readBytes)
    {
        using var source = new ChunkStream(Encoding.UTF8.GetBytes(text), 1);
        if (accepted)
        {
            (string Text, bool IsJson) result = await ExternalPlaylistContentReader.ReadAsync(
                source, true, htmlLimitBytes: 12, jsonLimitBytes: 4, CancellationToken.None);
            Assert.IsFalse(result.IsJson);
            Assert.AreEqual(text, result.Text);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ExternalPlaylistContentReader.ReadAsync(
                source, true, htmlLimitBytes: 12, jsonLimitBytes: 4, CancellationToken.None));
        }
        Assert.AreEqual(readBytes, source.BytesRead);
    }

    /// <summary>固定JSONはHTMLに見える内容でもJSON上限を使い、残り+1だけを要求してそれ以上読みません。</summary>
    [TestMethod]
    public async Task FixedJson_ReadsAtMostRemainingPlusOne()
    {
        using var source = new ChunkStream(Encoding.UTF8.GetBytes("<html> extra"), 100);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ExternalPlaylistContentReader.ReadAsync(
            source, false, htmlLimitBytes: 2, jsonLimitBytes: 8, CancellationToken.None));
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
