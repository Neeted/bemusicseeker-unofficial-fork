using System.Diagnostics;
using System.Text.Json;

namespace BeMusicSeeker.ProcessFixture;

/// <summary>runnerのcommand/I/O境界へ渡された操作だけを実行する非テストfixtureです。</summary>
internal sealed record RunnerCommand
{
    /// <summary>標準出力へそのまま渡す内容です。</summary>
    public string StandardOutput { get; init; } = "stdout";
    /// <summary>標準エラーへそのまま渡す内容です。</summary>
    public string StandardError { get; init; } = "stderr";
    /// <summary>rootが返す終了コードです。</summary>
    public int ExitCode { get; init; }
    /// <summary>rootの起動完了をPIDで通知するファイルです。</summary>
    public string? Ready { get; init; }
    /// <summary>rootの終了を許可するgateです。</summary>
    public string? Release { get; init; }
    /// <summary>commandが変更する専有入力です。</summary>
    public string? WritePath { get; init; }
    /// <summary>前段commandの変更結果です。</summary>
    public string? WriteContent { get; init; }
    /// <summary>前段commandから受け取る入力です。</summary>
    public string? RequirePath { get; init; }
    /// <summary>受け取るべき入力内容です。</summary>
    public string? RequireContent { get; init; }
    /// <summary>report境界へ与える合成TRX入力です。実testの成功を表しません。</summary>
    public string? ReportSource { get; init; }
    /// <summary>runnerが要求したreport出力先です。</summary>
    public string? ReportDestination { get; init; }
    /// <summary>所有childの起動完了をPIDで通知するファイルです。</summary>
    public string? ChildReady { get; init; }
    /// <summary>所有childの終了を許可するgateです。</summary>
    public string? ChildRelease { get; init; }
    /// <summary>childを生存させたままrootだけを先に終端させます。</summary>
    public bool ExitBeforeChild { get; init; }

    /// <summary>ファイルの受渡しと明示gateでcommandを制御し、test発見やpipeline判断は実装しません。</summary>
    internal static async Task<int> ExecuteAsync(string path)
    {
        RunnerCommand command = JsonSerializer.Deserialize<RunnerCommand>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("fixture commandがありません。");
        await Task.WhenAll(Console.Out.WriteAsync(command.StandardOutput), Console.Error.WriteAsync(command.StandardError));
        if (command.RequirePath is not null && await File.ReadAllTextAsync(command.RequirePath) != command.RequireContent)
        {
            Console.Error.Write("前段からの入力が一致しません。");
            return 19;
        }
        if (command.WritePath is not null) { await File.WriteAllTextAsync(command.WritePath, command.WriteContent); }
        if (command.ReportSource is not null && command.ReportDestination is not null)
        {
            File.Copy(command.ReportSource, command.ReportDestination);
        }
        Process? child = null;
        try
        {
            if (command.ChildReady is not null && command.ChildRelease is not null)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException()) { UseShellExecute = false };
                foreach (string argument in new[] { "hang", command.ChildReady, command.ChildRelease }) { start.ArgumentList.Add(argument); }
                child = Process.Start(start) ?? throw new InvalidOperationException();
                await WaitForFileAsync(command.ChildReady);
            }
            if (command.Ready is not null)
            {
                await File.WriteAllTextAsync(command.Ready + ".tmp", Environment.ProcessId.ToString());
                File.Move(command.Ready + ".tmp", command.Ready);
            }
            if (command.ExitBeforeChild) { return command.ExitCode; }
            if (command.Release is not null) { await WaitForFileAsync(command.Release); }
            if (child is not null) { await child.WaitForExitAsync(); }
            return command.ExitCode;
        }
        finally { child?.Dispose(); }
    }

    private static async Task WaitForFileAsync(string path)
    {
        while (!File.Exists(path)) { await Task.Delay(10); }
    }
}
