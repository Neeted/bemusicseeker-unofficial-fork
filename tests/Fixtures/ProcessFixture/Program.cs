using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Ui;
using BeMusicSeeker.TestSupport.Verification;
using Microsoft.Win32.SafeHandles;

namespace BeMusicSeeker.ProcessFixture;

/// <summary>プロセス所有の外部契約だけを再現する、非テスト実行体です。</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        switch (args[0])
        {
            case "--test-session":
                return await ObserveScenarioTransportAsync(args[1]);
            case "exit":
                return 0;
            case "runner-command":
                return await RunnerCommand.ExecuteAsync(args[1]);
            case "distribution-catalog":
                // 親環境を変更せず、直接moduleと同じcatalog入口の成功・拒否を観測します。
                try
                {
                    DistributionCatalog catalog = await DistributionCatalog.ReadFromEnvironmentAsync(args[1], ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30)));
                    Console.Write(catalog.Legacy.Version);
                    return 0;
                }
                catch (Exception error)
                {
                    Console.Error.Write(error);
                    return 3;
                }
            case "assignment-failure":
                return await ObserveAssignmentFailureAsync(args[1], args[2], args.Length > 3 && bool.Parse(args[3]));
            case "lease":
                using (TestUiSessionLease lease = await TestUiSessionLease.AcquireFileAsync(args[3], ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10))))
                {
                    File.WriteAllText(args[1], "ready");
                    await WaitForFileAsync(args[2]);
                }
                return 0;
            case "lease-path":
                Console.Write(TestUiSessionLease.SessionPath);
                return 0;
            case "output":
                string text = string.Concat(Enumerable.Repeat("日本語🙂🎵\\\"引数\n", 20000));
                await Task.WhenAll(Console.Out.WriteAsync(text), Console.Error.WriteAsync(text));
                return 0;
            case "environment":
                Console.WriteLine(Environment.GetEnvironmentVariable("DOTNET_CLI_FORCE_UTF8_ENCODING"));
                Console.WriteLine(Environment.GetEnvironmentVariable("DOTNET_CLI_USE_MSBUILD_SERVER"));
                Console.WriteLine(Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE"));
                Console.WriteLine(Environment.GetEnvironmentVariable("UseSharedCompilation"));
                foreach (string argument in args.Skip(1))
                {
                    Console.WriteLine(argument);
                }

                return 0;
            case "hang":
                File.WriteAllText(args[1] + ".tmp", Environment.ProcessId.ToString());
                File.Move(args[1] + ".tmp", args[1]);
                await WaitForFileAsync(args[2]);
                return 0;
            case "child":
                var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException()) { UseShellExecute = false };
                foreach (string argument in new[] { "hang", args[1], args[2] })
                {
                    info.ArgumentList.Add(argument);
                }

                using (Process child = Process.Start(info) ?? throw new InvalidOperationException())
                {
                    await WaitForFileAsync(args[1]);
                    if (args[3] == "exit")
                    {
                        return 7;
                    }

                    await child.WaitForExitAsync();
                }
                return 0;
            case "owner":
                await using (var child = OwnedProcess.Start(new ProcessStartRequest(
                    Environment.ProcessPath ?? throw new InvalidOperationException(), ["hang", args[1], args[2]], AppContext.BaseDirectory)))
                {
                    await child.Completion;
                }
                return 0;
            default:
                throw new ArgumentException("未知の fixture 操作です。");
        }
    }

    // 既存process fixtureで通信だけを検査します。製品起動や保存の成功oracleには使いません。
    private static async Task<int> ObserveScenarioTransportAsync(string manifestPath)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        JsonElement root = manifest.RootElement;
        string profile = root.GetProperty("profileDirectory").GetString() ?? throw new InvalidDataException();
        string mode = root.GetProperty("caseId").GetString() ?? throw new InvalidDataException();
        using var pipe = new NamedPipeClientStream(".", root.GetProperty("controlPipeName").GetString() ?? throw new InvalidDataException(), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync();
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { message = "connected", ProcessId = Environment.ProcessId, RequestId = 0 }));
        if (mode is "unanswered" or "answered")
        {
            string command = await reader.ReadLineAsync() ?? throw new EndOfStreamException();
            if (mode == "answered")
            {
                using var request = JsonDocument.Parse(command);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    message = "table-state",
                    RequestId = request.RootElement.GetProperty("RequestId").GetInt64(),
                    State = new { OperationSection = "probe", SourceScope = "probe", SourceType = "probe", RowsGeneration = 1, Count = 0, RealizedRowCount = 0 }
                }));
            }
            File.WriteAllText(Path.Combine(profile, "request-received"), "ready");
        }
        await WaitForFileAsync(Path.Combine(profile, "release"));
        if (mode == "malformed") { await writer.WriteLineAsync("{invalid-json"); }
        return mode == "nonzero" ? 7 : 0;
    }

    private static async Task<int> ObserveAssignmentFailureAsync(string readyPath, string releasePath, bool failCleanupCallback)
    {
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));
        int failureCleanupBegins = 0;
        // 未破棄のゼロ値なら marshaller ではなく、実際の Job 割当が無効ハンドルで失敗する。
        using var job = new WindowsProcessJob(new SafeFileHandle(IntPtr.Zero, ownsHandle: true));
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var outputReader = new StreamReader(output, new UTF8Encoding(false), false);
        using var errorReader = new StreamReader(error, new UTF8Encoding(false), false);
        Task<string> standardOutput = outputReader.ReadToEndAsync();
        Task<string> standardError = errorReader.ReadToEndAsync();
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException())
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (string argument in new[] { "hang", readyPath, releasePath })
        {
            info.ArgumentList.Add(argument);
        }

        OwnedProcessStartException startFailure;
        try
        {
            using Process unexpected = job.Start(info, output, error, () =>
            {
                failureCleanupBegins++;
                if (failCleanupCallback)
                {
                    throw new IOException("回収期限 callback の故障注入");
                }
                return deadline;
            });
            throw new InvalidOperationException("無効な Job への割当が成功しました。");
        }
        catch (OwnedProcessStartException failure)
        {
            startFailure = failure;
        }
        finally
        {
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
        }

        // 回収の自己申告ではなく、生成された target が継承した両 writer の実 EOF を待つ。
        await Task.WhenAll(standardOutput, standardError).WaitAsync(deadline.Remaining);
        Console.Write(JsonSerializer.Serialize(new
        {
            NativeErrorCode = startFailure.InnerException is Win32Exception nativeFailure ? (int?)nativeFailure.NativeErrorCode : null,
            FailureCleanupBegins = failureCleanupBegins,
            CleanupFailure = startFailure.CleanupFailure?.ToString(),
            StandardOutput = await standardOutput,
            StandardError = await standardError,
        }));
        return 0;
    }

    private static async Task WaitForFileAsync(string path)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(10);
        }
    }
}
