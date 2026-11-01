using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>準備と固定moduleの起動、標準reportの回収だけを行う入口です。</summary>
internal static class VerificationRunner
{
    /// <summary>指定profileを実行し、不完全な結果や回収失敗を非ゼロで返します。</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("dev: Core/Windows/Daily Scenarios、verify: 自動整形後dev、full: 全Scenarios/Desktop（Release）、compatibility/performance: --suite 対象名");
            Console.WriteLine("移行中: 空moduleは失敗です。必須の既存回帰は scripts/verify-refactor.ps1 の単一MTP Testsで継続します。");
            return 0;
        }
        var total = Stopwatch.StartNew();
        try
        {
            string? suite = null;
            if (args.Length == 3 && args[1] == "--suite")
            {
                suite = args[2];
            }
            else if (args.Length != 1)
            {
                throw new ArgumentException("利用方法: <profile> [--suite 対象名]");

            }

            var plan = VerificationPlan.Create(args[0], Environment.ProcessorCount, suite);
            string repository = FindRepository();
            string runRoot = Path.Combine(repository, "artifacts", "verification", "mtp-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(runRoot);
            Console.WriteLine($"profile={plan.Profile} configuration={plan.Configuration} budget={plan.Budget}");
            Console.WriteLine("選択: " + string.Join(", ", plan.Modules.Select(module => module.Name)));
            Console.WriteLine("対象外: " + string.Join(", ", new[] { "Core", "Windows", "Scenarios", "Desktop.E2E", "Compatibility", "Performance" }.Except(plan.Modules.Select(module => module.Name))));
            Console.WriteLine("結果: " + runRoot);
            if (plan.Profile is "full" or "compatibility" or "performance")
            {
                throw new InvalidOperationException("このprofileの配布物・環境preflight・期限は後続移行で接続します。未実施を成功として扱いません。");
            }

            using var canceled = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; canceled.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                int commandIndex = 0;
                async Task<ProcessCompletion> Execute(string executable, IReadOnlyList<string> arguments, ProcessDeadline deadline)
                {
                    string logName = $"preparation-{++commandIndex:D3}";
                    OwnedProcess? process = null;
                    Exception? primary = null;
                    try
                    {
                        process = OwnedProcess.Start(new ProcessStartRequest(executable, arguments, repository, ownershipPolicy: ProcessOwnershipPolicy.Preparation));
                        ProcessCompletion result = await process.WaitForExitAsync(deadline, canceled.Token);
                        if (result.ExitCode != 0)
                        {
                            throw new InvalidOperationException($"{executable} が終了コード {result.ExitCode} で失敗しました。{result.StandardError}");
                        }

                        return result;
                    }
                    catch (Exception error) { primary = error; throw; }
                    finally
                    {
                        if (process is not null)
                        {
                            await process.StopAsync(null, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
                            Exception? cleanup = process.CleanupFailure;
                            try { await WriteOutputAsync(process, runRoot, logName); }
                            catch (Exception error) { cleanup = cleanup is null ? error : new AggregateException(cleanup, error); }
                            try { await process.DisposeAsync(); }
                            catch (Exception error) { cleanup ??= error; }
                            if (cleanup is not null)
                            {
                                throw primary is null ? cleanup : new AggregateException(primary, cleanup);
                            }
                        }
                    }
                }

                var preparation = Stopwatch.StartNew();
                string source = (await Execute("git", ["rev-parse", "HEAD"], ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30)))).StandardOutput.Trim();
                bool dirty = (await Execute("git", ["status", "--porcelain"], ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30)))).StandardOutput.Length != 0;
                string sdk = (await Execute("dotnet", ["--version"], ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30)))).StandardOutput.Trim();
                string restoreTarget = plan.Format ? "BeMusicSeeker.sln" : plan.SolutionFilter;
                await Execute("dotnet", ["restore", restoreTarget, "--locked-mode", "--disable-build-servers", "-p:Platform=x64"], ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(300)));
                if (plan.Format)
                {
                    var formattingDeadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(180));
                    await RepositoryFormatter.FormatAsync(repository, Execute, formattingDeadline);
                    await Execute("git", ["diff", "--check"], formattingDeadline);
                }
                preparation.Stop();
                var build = Stopwatch.StartNew();
                await Execute("dotnet", ["build", plan.SolutionFilter, "--no-restore", "--disable-build-servers", "-c", plan.Configuration, "-p:Platform=x64"], ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(300)));
                build.Stop();

                var executions = new ConcurrentBag<ModuleExecution>();
                var cleanupIntervals = new ConcurrentBag<(long Start, long End)>();
                long executionStart = 0;
                var testDeadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(360));
                async Task<bool> RunModule(VerificationModule module)
                {
                    string directory = Path.Combine(runRoot, module.Name);
                    OwnedProcess? process = null;
                    Exception? primary = null;
                    Exception? cleanup = null;
                    long moduleStart = 0;
                    try
                    {
                        canceled.Token.ThrowIfCancellationRequested();
                        if (testDeadline.Remaining == TimeSpan.Zero)
                        {
                            throw new TimeoutException("test段階の期限を超えています。");
                        }

                        Directory.CreateDirectory(directory);
                        string settings = Path.Combine(directory, "tests.runsettings");
                        module.CreateRunSettings().Save(settings);
                        string exe = Path.Combine(repository, Path.GetDirectoryName(module.ProjectPath) ?? throw new InvalidOperationException(), "bin", "x64", plan.Configuration, "net10.0-windows", $"BeMusicSeeker.{module.Name}.Tests.exe");
                        string[] options = ["--results-directory", directory, "--report-trx", "--report-trx-filename", "results.trx", "--minimum-expected-tests", "1", "--settings", settings, "--crashdump", .. module.Filter is null ? [] : new[] { "--filter", module.Filter }];
                        long processStart = Stopwatch.GetTimestamp();
                        process = OwnedProcess.Start(new ProcessStartRequest(exe, options, repository,
                            new Dictionary<string, string?> { [TestRunDirectory.EnvironmentVariable] = Path.Combine(directory, "cases") }));
                        moduleStart = processStart;
                        Interlocked.CompareExchange(ref executionStart, moduleStart, 0);
                        await process.WaitForExitAsync(testDeadline, canceled.Token);
                    }
                    catch (Exception error) { primary = error; }
                    finally
                    {
                        if (process is not null)
                        {
                            long cleanupStart = Stopwatch.GetTimestamp();
                            try
                            {
                                await process.StopAsync(null, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
                                cleanup = process.CleanupFailure;
                                await WriteOutputAsync(process, directory, "process");
                            }
                            catch (Exception error) { cleanup = cleanup is null ? error : new AggregateException(cleanup, error); }
                            finally
                            {
                                try { await process.DisposeAsync(); }
                                catch (Exception error) { cleanup ??= error; }
                                cleanupIntervals.Add((cleanupStart, Stopwatch.GetTimestamp()));
                            }
                        }
                    }
                    ProcessCompletion? completed = process?.Completion.IsCompletedSuccessfully == true ? await process.Completion : null;
                    executions.Add(new(module.Name, Path.Combine(directory, "results.trx"), completed?.ExitCode,
                        completed is not null, (primary ?? process?.PrimaryFailure)?.ToString(), cleanup?.ToString(),
                        moduleStart == 0 ? null : Stopwatch.GetElapsedTime(moduleStart).TotalMilliseconds));
                    return completed is not null && cleanup is null;
                }
                async Task RunUiLane()
                {
                    foreach (VerificationModule module in plan.Modules.Where(module => module.UsesUi))
                    {
                        if (!await RunModule(module))
                        {
                            break;
                        }
                    }
                }
                if (plan.Budget == 1)
                {
                    foreach (VerificationModule module in plan.Modules)
                    {
                        if (!await RunModule(module))
                        {
                            break;
                        }
                    }
                }
                else
                {
                    await Task.WhenAll(RunModule(plan.Modules.Single(module => module.Name == "Core")), RunUiLane());
                }
                IReadOnlyList<ModuleOutcome> outcomes = ResultGate.Evaluate(plan.Modules.Select(module => module.Name).ToArray(), executions.ToArray());
                TimeSpan executionTime = executionStart == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(executionStart);
                double cleanupMs = MeasureIntervalUnion(cleanupIntervals);
                total.Stop();
                var summary = new
                {

                    profile = plan.Profile,
                    configuration = plan.Configuration,

                    source,
                    dirty,
                    sdk,
                    os = Environment.OSVersion.VersionString,
                    logicalProcessors = Environment.ProcessorCount,
                    workers = plan.Modules.Select(module => new { module.Name, module.Workers }),
                    preparationMs = preparation.Elapsed.TotalMilliseconds,
                    buildMs = build.Elapsed.TotalMilliseconds,
                    executionMs = executionTime.TotalMilliseconds,
                    cleanupMs,
                    totalMs = total.Elapsed.TotalMilliseconds,
                    timingDefinition = "executionMsは最初のtest起動から全report集約・所有child終端まで。cleanupMsは重複を除いた回収区間の内数で加算しない。",
                    modules = outcomes.Select(outcome => new { outcome.Name, outcome.Cases, outcome.Passed, outcome.ElapsedMs, outcome.Errors }),
                };
                await File.WriteAllTextAsync(Path.Combine(runRoot, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                foreach (ModuleOutcome outcome in outcomes)
                {
                    Console.WriteLine($"{outcome.Name}: {(outcome.Passed ? "Passed" : "Failed")} {outcome.Cases} cases {string.Join(" / ", outcome.Errors)}");
                }

                Console.WriteLine($"test wall={executionTime.TotalSeconds:F3}s（cleanup内数={cleanupMs / 1000:F3}s）total={total.Elapsed.TotalSeconds:F3}s");
                return outcomes.All(outcome => outcome.Passed) ? 0 : 1;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task WriteOutputAsync(OwnedProcess process, string directory, string name)
    {
        if (process.StandardOutput.IsCompletedSuccessfully)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".stdout.log"), await process.StandardOutput);
        }

        if (process.StandardError.IsCompletedSuccessfully)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".stderr.log"), await process.StandardError);
        }
    }

    private static double MeasureIntervalUnion(IEnumerable<(long Start, long End)> intervals)
    {
        long start = 0, end = 0;
        double milliseconds = 0;
        foreach ((long nextStart, long nextEnd) in intervals.OrderBy(interval => interval.Start))
        {
            if (nextStart > end)
            {
                if (end > start)
                {
                    milliseconds += Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
                }

                start = nextStart;
            }
            end = Math.Max(end, nextEnd);
        }
        return milliseconds + (end > start ? Stopwatch.GetElapsedTime(start, end).TotalMilliseconds : 0);
    }

    private static string FindRepository()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("BeMusicSeeker.slnを含むリポジトリから実行してください。");
    }
}
