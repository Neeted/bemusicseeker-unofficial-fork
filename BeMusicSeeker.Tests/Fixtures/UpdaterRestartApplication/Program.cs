using System;
using System.IO;
using System.Text.Json;

namespace BeMusicSeeker.Tests.Fixtures.UpdaterRestartApplication;

/// <summary>更新テストの再起動先を、consoleを作らない本番相当のGUIプロセスとして実行します。</summary>
internal static class Program
{
    /// <summary>隣接する版ごとの入力からmarkerを生成するか、準備通知後に標準入力のreleaseを待ちます。入力不備は終了1で報告します。</summary>
    private static int Main()
    {
        try
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("実行パスを取得できません。");
            Configuration configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Path.ChangeExtension(executable, ".json")))
                ?? throw new InvalidDataException("fixture入力がありません。");
            if (configuration.WaitForRelease)
            {
                Console.WriteLine("BMS_TEST_FIXTURE_READY");
                Console.Out.Flush();
                if (Console.ReadLine() != "release") throw new InvalidDataException("release入力がありません。");
            }
            else if (configuration.MarkerFile is string marker)
            {
                string directory = Path.GetDirectoryName(executable) ?? throw new InvalidDataException("実行ディレクトリがありません。");
                File.WriteAllText(Path.Combine(directory, marker), configuration.MarkerContents ?? throw new InvalidDataException("marker内容がありません。"));
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    /// <summary>引数を追加せず、packageやbackupとともに移動する版固有のfixture入力です。</summary>
    private sealed record Configuration(bool WaitForRelease, string? MarkerFile, string? MarkerContents);
}
