using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Ui;
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
            case "assignment-failure":
                return await ObserveAssignmentFailureAsync(args[1], args[2]);
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

    private static async Task<int> ObserveAssignmentFailureAsync(string readyPath, string releasePath)
    {
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));
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
            using Process unexpected = job.Start(info, output, error);
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
