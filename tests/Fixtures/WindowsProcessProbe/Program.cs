using System.Text;
using System.Text.Json;

namespace BeMusicSeeker.WindowsProcessProbe;

/// <summary>fresh-process契約のpayload移管先です。未接続のprobeを成功にしません。</summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Error.WriteLine("指定されたprobeはまだ接続されていません: " + string.Join(" ", args));
        return Task.FromResult(2);
    }

    /// <summary>probeごとの変更不能な観測結果をUTF-8のJSON一行として親へ返します。</summary>
    internal static void WriteResult<T>(T result) => Console.WriteLine(JsonSerializer.Serialize(result));
}
