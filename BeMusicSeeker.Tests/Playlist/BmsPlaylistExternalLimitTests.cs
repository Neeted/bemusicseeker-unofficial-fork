using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsPlaylistExternalLoadTests
{
    /// <summary>ローカル初回JSONはHTML上限を超える空白を含む100MiBちょうどまで許可し、+1は解析前に拒否します。相対data_urlも維持します。</summary>
    [TestMethod]
    public async Task ExternalJson_RealLimitIncludesLeadingWhitespaceAndRejectsOneExtraByte()
    {
        string root = Path.Combine(Path.GetTempPath(), "ExternalJsonLimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string headerPath = Path.Combine(root, "header.html");
            string dataPath = Path.Combine(root, "data.json");
            File.WriteAllText(dataPath, "[]");
            byte[] header = Encoding.UTF8.GetBytes("{\"name\":\"Bounded\",\"symbol\":\"B\",\"data_url\":\"data.json\"}");
            using (FileStream file = File.Create(headerPath))
            {
                byte[] whitespace = new byte[81920]; Array.Fill(whitespace, (byte)' ');
                int remaining = ExternalPlaylistContentReader.JsonLimitBytes - header.Length;
                while (remaining > 0) { int count = Math.Min(remaining, whitespace.Length); file.Write(whitespace, 0, count); remaining -= count; }
                file.Write(header);
            }
            var playlist = new TestBmsPlaylist(CreateTempSongDbPath(root), settings: testSettings);
            BMSTable result = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath));
            Assert.AreEqual("Bounded", result.name);
            Assert.AreEqual(0, result.entries.Count);
            using (var file = new FileStream(headerPath, FileMode.Append)) { file.WriteByte((byte)' '); }
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerPath)));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>実HTTPのgzip展開後HTMLを10MiBで制限し、小さなContent-Lengthでもmeta取得・解析へ進ませません。exactと+1は共通readerで分担します。</summary>
    [TestMethod]
    public async Task ExternalHtml_GzipIsLimitedAfterDecompression()
    {
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes("<html>"));
                byte[] spaces = new byte[81920]; Array.Fill(spaces, (byte)' ');
                for (int count = 0; count < ExternalPlaylistContentReader.HtmlLimitBytes; count += spaces.Length) { gzip.Write(spaces); }
            }
            compressed = output.ToArray();
        }
        string root = Path.Combine(Path.GetTempPath(), "ExternalGzipLimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var server = new SingleRequestHttpServer(compressed, additionalResponseHeaders: "Content-Encoding: gzip\r\n");
            var playlist = new TestBmsPlaylist(CreateTempSongDbPath(root), settings: testSettings);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => playlist.ExternalSyncOwner.LoadExternalTableAsync(server.Address));
        }
        finally { Directory.Delete(root, true); }
    }
}
