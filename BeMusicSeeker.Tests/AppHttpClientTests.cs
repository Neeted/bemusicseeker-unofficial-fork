using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class AppHttpClientTests
{
    [TestMethod]
    [TestCategory("Http")]
    public void GetString_FileUri_StripsUtf8Bom()
    {
        string filePath = CreateTempFileWithBytes(CreateUtf8BomBytes("header-json"));
        try
        {
            string actual = AppHttpClient.Shared.GetString(new Uri(filePath), Encoding.UTF8);

            Assert.AreEqual("header-json", actual);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    [TestCategory("Http")]
    public async Task GetStringAsync_FileUri_StripsUtf8Bom()
    {
        string filePath = CreateTempFileWithBytes(CreateUtf8BomBytes("async-json"));
        try
        {
            string actual = await AppHttpClient.Shared.GetStringAsync(new Uri(filePath), Encoding.UTF8);

            Assert.AreEqual("async-json", actual);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    [TestCategory("Http")]
    public void GetString_FileUri_PreservesPlainUtf8()
    {
        string filePath = CreateTempFileWithBytes(Encoding.UTF8.GetBytes("plain-json"));
        try
        {
            string actual = AppHttpClient.Shared.GetString(new Uri(filePath), Encoding.UTF8);

            Assert.AreEqual("plain-json", actual);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    [TestCategory("Http")]
    public void PostString_StripsUtf8BomFromResponse()
    {
        using var server = new SingleRequestHttpServer(CreateUtf8BomBytes("{\"ok\":true}"));

        string response = AppHttpClient.Shared.PostString(server.Address, "body", "application/json;charset=UTF-8", Encoding.UTF8);

        Assert.AreEqual("{\"ok\":true}", response);
        Assert.AreEqual("POST", server.RequestMethod);
    }

    [TestMethod]
    [TestCategory("Http")]
    public void PostFile_StripsUtf8BomFromResponse()
    {
        string uploadFilePath = CreateTempFileWithBytes(Encoding.UTF8.GetBytes("upload"));
        try
        {
            using var server = new SingleRequestHttpServer(CreateUtf8BomBytes("{\"uploaded\":true}"));

            string response = AppHttpClient.Shared.PostFile(server.Address, uploadFilePath, responseEncoding: Encoding.UTF8);

            Assert.AreEqual("{\"uploaded\":true}", response);
            Assert.AreEqual("POST", server.RequestMethod);
        }
        finally
        {
            File.Delete(uploadFilePath);
        }
    }

    private static byte[] CreateUtf8BomBytes(string text)
    {
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }

    private static string CreateTempFileWithBytes(byte[] bytes)
    {
        string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllBytes(filePath, bytes);
        return filePath;
    }

    private sealed class SingleRequestHttpServer : IDisposable
    {
        private readonly TcpListener listener;

        private readonly Task serverTask;

        private readonly byte[] responseBody;

        public Uri Address { get; }

        public string RequestMethod { get; private set; } = string.Empty;

        public SingleRequestHttpServer(byte[] responseBody)
        {
            this.responseBody = responseBody ?? [];
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Address = new Uri("http://127.0.0.1:" + port + "/");
            serverTask = Task.Run((Action)ServeSingleRequest);
        }

        public void Dispose()
        {
            listener.Stop();
            serverTask.GetAwaiter().GetResult();
        }

        private void ServeSingleRequest()
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                using NetworkStream stream = client.GetStream();
                ReadRequest(stream);
                byte[] responseBytes = BuildHttpResponse(responseBody);
                stream.Write(responseBytes, 0, responseBytes.Length);
                stream.Flush();
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ReadRequest(NetworkStream stream)
        {
            byte[] buffer = new byte[4096];
            using var received = new MemoryStream();
            int headerEndIndex = -1;
            while (headerEndIndex < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                received.Write(buffer, 0, read);
                headerEndIndex = FindHeaderTerminator(received.GetBuffer(), (int)received.Length);
            }
            byte[] receivedBytes = received.ToArray();
            if (headerEndIndex < 0)
            {
                return;
            }
            string headerText = Encoding.ASCII.GetString(receivedBytes, 0, headerEndIndex);
            string[] headerLines = headerText.Split(["\r\n"], StringSplitOptions.None);
            if (headerLines.Length > 0)
            {
                string[] requestLineParts = headerLines[0].Split(' ');
                if (requestLineParts.Length > 0)
                {
                    RequestMethod = requestLineParts[0];
                }
            }
            int contentLength = 0;
            foreach (string line in headerLines.Skip(1))
            {
                const string prefix = "Content-Length:";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring(prefix.Length).Trim(), out contentLength);
                    break;
                }
            }
            int bodyBytesRead = receivedBytes.Length - (headerEndIndex + 4);
            while (bodyBytesRead < contentLength)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, contentLength - bodyBytesRead));
                if (read <= 0)
                {
                    break;
                }
                bodyBytesRead += read;
            }
        }

        private static int FindHeaderTerminator(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static byte[] BuildHttpResponse(byte[] body)
        {
            string header = "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            return [.. Encoding.ASCII.GetBytes(header), .. body];
        }
    }
}
