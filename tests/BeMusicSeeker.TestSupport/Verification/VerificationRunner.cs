using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>CLIと接続検証で共用する、実行場所・プロセス開始境界・段階予算です。</summary>
internal sealed record VerificationRun(string Repository, string RunRoot)
{
    /// <summary>実際の所有プロセスの開始境界です。起動失敗の最初の検出で段階共通の回収期限を取得します。</summary>
    internal Func<ProcessStartRequest, Func<ProcessDeadline>, OwnedProcess> StartProcess { get; init; } = OwnedProcess.Start;
    /// <summary>所有対象への正常終了要求です。共有回収期限内で既存の強制停止・回収へ続きます。</summary>
    internal Func<OwnedProcess, CancellationToken, Task>? RequestGracefulExit { get; init; }
    /// <summary>実行条件取得の各commandの期限です。</summary>
    internal TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>restoreとbuildそれぞれの期限です。</summary>
    internal TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromSeconds(300);
    /// <summary>全formatter batchで共有する期限です。</summary>
    internal TimeSpan FormatTimeout { get; init; } = TimeSpan.FromSeconds(180);
    /// <summary>接続検査で短い期限を使う場合の上書きです。通常実行はprofileの共通test期限を使います。</summary>
    internal TimeSpan? TestTimeout { get; init; }
    /// <summary>固定旧ZIP取得と、V・Vnext各publish＋検査にそれぞれ与える期限です。</summary>
    internal TimeSpan DistributionTimeout { get; init; } = TimeSpan.FromSeconds(180);
    /// <summary>段階の致命失敗時に一度だけ確定する回収猶予です。</summary>
    internal TimeSpan CleanupGrace { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>主失敗、ログ保存失敗、所有対象の回収失敗を独立して保持するcommandの結果です。</summary>
internal sealed record VerificationCommandResult(string Name, ProcessCompletion? Completion, Exception? PrimaryFailure,
    Exception? OutputFailure, Exception? CleanupFailure, double? ElapsedMs)
{
    /// <summary>process、ログ、回収がすべて成功した場合だけ真です。</summary>
    internal bool Passed => Completion?.ExitCode == 0 && PrimaryFailure is null && OutputFailure is null && CleanupFailure is null;
    /// <summary>前段の非成功を後段へ持ち越さず停止するための失敗を生成します。</summary>
    internal Exception Failure => new AggregateException($"commandが失敗しました: {Name}",
        new[] { PrimaryFailure, OutputFailure, CleanupFailure }.OfType<Exception>());
}

/// <summary>証跡保存に失敗しても元の実行結果を呼出元へ返す、入口全体の結果です。</summary>
internal sealed record VerificationRunResult(int ExitCode, IReadOnlyList<ModuleOutcome> Modules,
    IReadOnlyList<VerificationCommandResult> Commands, Exception? PrimaryFailure, Exception? SummaryFailure);

/// <summary>準備と固定moduleの起動、標準reportの回収だけを行う入口です。</summary>
internal static class VerificationRunner
{
    /// <summary>CLIを解釈し、親のConsole設定や環境を変更せず実pipelineへ接続します。</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("<dev|verify|full|compatibility|performance> [--suite 対象名] [--filter 標準MTP条件]");
            Console.WriteLine("移行中: 空moduleは失敗です。必須の既存回帰は scripts/verify-refactor.ps1 の単一MTP Testsで継続します。");
            return 0;
        }
        try
        {
            var plan = VerificationPlan.Parse(args, Environment.ProcessorCount);
            string repository = FindRepository();
            string runRoot = Path.Combine(repository, "artifacts", "verification", "mtp-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            using var canceled = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; canceled.Cancel(); };
            Console.CancelKeyPress += cancel;
            try { return (await ExecuteAsync(plan, new VerificationRun(repository, runRoot), canceled.Token)).ExitCode; }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    /// <summary>CLIと同じ準備・整形・build・test・report集約を、専有の実行領域で動かします。</summary>
    internal static async Task<VerificationRunResult> ExecuteAsync(VerificationPlan plan, VerificationRun run,
        CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        var preparation = new Stopwatch();
        var build = new Stopwatch();
        var distribution = new Stopwatch();
        var commands = new ConcurrentBag<VerificationCommandResult>();
        var executions = new ConcurrentBag<ModuleExecution>();
        var cleanupIntervals = new ConcurrentBag<(long Start, long End)>();
        IReadOnlyList<ModuleOutcome> outcomes = [];
        Exception? primary = null;
        Exception? summaryFailure = null;
        string? source = null, sdk = null;
        string? distributionCatalog = null;
        bool? dirty = null;
        long executionStart = 0;
        var summaryDeadline = ProcessDeadline.FromTimeout(run.MetadataTimeout);
        int commandIndex = 0;

        async Task<ProcessCompletion> ExecutePreparation(string executable, IReadOnlyList<string> arguments, RunnerStage stage)
        {
            VerificationCommandResult result = await ExecuteProcessAsync(run, stage,
                new ProcessStartRequest(executable, arguments, run.Repository, ownershipPolicy: ProcessOwnershipPolicy.Preparation),
                run.RunRoot, $"preparation-{++commandIndex:D3}");
            commands.Add(result);
            summaryDeadline = stage.CleanupDeadline;
            if (!result.Passed) { throw result.Failure; }
            return result.Completion ?? throw new InvalidOperationException("commandの終端結果がありません。");
        }
        async Task<ProcessCompletion> Metadata(string executable, IReadOnlyList<string> arguments)
        {
            using var stage = new RunnerStage(run.MetadataTimeout, run.CleanupGrace, cancellationToken);
            return await ExecutePreparation(executable, arguments, stage);
        }

        try
        {
            Directory.CreateDirectory(run.RunRoot);
            Console.WriteLine($"profile={plan.Profile} configuration={plan.Configuration} budget={plan.Budget}");
            Console.WriteLine("選択: " + string.Join(", ", plan.Modules.Select(module => module.Name)));
            Console.WriteLine("対象外: " + string.Join(", ", new[] { "Core", "Windows", "Scenarios", "Desktop.E2E", "Compatibility", "Performance" }.Except(plan.Modules.Select(module => module.Name))));
            Console.WriteLine("結果: " + run.RunRoot);
            if (plan.Profile is "compatibility" or "performance")
            {
                throw new InvalidOperationException("このprofileの配布物・環境preflight・期限は後続移行で接続します。未実施を成功として扱いません。");
            }
            preparation.Start();
            source = (await Metadata("git", ["rev-parse", "HEAD"])).StandardOutput.Trim();
            dirty = (await Metadata("git", ["status", "--porcelain"])).StandardOutput.Length != 0;
            sdk = (await Metadata("dotnet", ["--version"])).StandardOutput.Trim();
            using (var restore = new RunnerStage(run.PreparationTimeout, run.CleanupGrace, cancellationToken))
            {
                await ExecutePreparation("dotnet", ["restore", plan.Format ? "BeMusicSeeker.sln" : plan.SolutionFilter,
                    "--locked-mode", "--disable-build-servers", "-p:Platform=x64"], restore);
            }
            if (plan.Format)
            {
                using var format = new RunnerStage(run.FormatTimeout, run.CleanupGrace, cancellationToken);
                await RepositoryFormatter.FormatAsync(run.Repository,
                    (executable, arguments, _) => ExecutePreparation(executable, arguments, format), format.ExecutionDeadline,
                    allSources: plan.Profile == "full");
                await ExecutePreparation("git", ["diff", "--check"], format);
            }
            preparation.Stop();
            build.Start();
            using (var building = new RunnerStage(run.PreparationTimeout, run.CleanupGrace, cancellationToken))
            {
                await ExecutePreparation("dotnet", ["build", plan.SolutionFilter, "--no-restore", "--disable-build-servers", "-c", plan.Configuration, "-p:Platform=x64"], building);
            }
            build.Stop();

            if (plan.Profile == "full")
            {
                distribution.Start();
                distributionCatalog = await DistributionPreparer.PrepareAsync(run.Repository, run.RunRoot, async (name, action) =>
                {
                    using var stage = new RunnerStage(run.DistributionTimeout, run.CleanupGrace, cancellationToken);
                    Console.WriteLine("配布準備: " + name);
                    try
                    {
                        stage.ThrowIfCannotStart();
                        await action((executable, arguments, _) => ExecutePreparation(executable, arguments, stage),
                            stage.ExecutionDeadline, stage.CancellationToken);
                        stage.ThrowIfCannotStart();
                    }
                    catch
                    {
                        stage.Abort();
                        throw;
                    }
                    finally { summaryDeadline = stage.CleanupDeadline; }
                });
                distribution.Stop();
            }

            using var testing = new RunnerStage(run.TestTimeout ?? plan.TestTimeout, run.CleanupGrace, cancellationToken);
            async Task RunModule(VerificationModule module)
            {
                if (testing.Aborted) { return; }
                string directory = Path.Combine(run.RunRoot, module.Name);
                VerificationCommandResult result;
                try
                {
                    Directory.CreateDirectory(directory);
                    string settings = Path.Combine(directory, "tests.runsettings");
                    module.CreateRunSettings().Save(settings);
                    string executable = Path.Combine(run.Repository, Path.GetDirectoryName(module.ProjectPath) ?? throw new InvalidOperationException(),
                        "bin", "x64", plan.Configuration, "net10.0-windows", $"BeMusicSeeker.{module.Name}.Tests.exe");
                    string[] arguments = ["--results-directory", directory, "--report-trx", "--report-trx-filename", "results.trx",
                        "--minimum-expected-tests", "1", "--settings", settings, "--crashdump", .. module.Filter is null ? [] : new[] { "--filter", module.Filter }];
                    result = await ExecuteProcessAsync(run, testing, new ProcessStartRequest(executable, arguments, run.Repository,
                        new Dictionary<string, string?>
                        {
                            [TestRunDirectory.EnvironmentVariable] = Path.Combine(directory, "cases"),
                            [DistributionCatalog.EnvironmentVariable] = distributionCatalog,
                        }),
                        directory, "process", started => Interlocked.CompareExchange(ref executionStart, started, 0), cleanupIntervals);
                }
                catch (Exception error)
                {
                    testing.Abort();
                    result = new(module.Name, null, error, null, null, null);
                }
                commands.Add(result with { Name = module.Name });
                string? processFailure = result.PrimaryFailure?.ToString();
                if (result.OutputFailure is not null)
                {
                    processFailure = string.Join(Environment.NewLine, new[] { processFailure, "ログ保存失敗: " + result.OutputFailure }.OfType<string>());
                }
                executions.Add(new(module.Name, Path.Combine(directory, "results.trx"), result.Completion?.ExitCode,
                    result.Completion is not null, processFailure, result.CleanupFailure?.ToString(), result.ElapsedMs));
            }
            async Task RunUiLane()
            {
                foreach (VerificationModule module in plan.Modules.Where(module => module.UsesUi))
                {
                    if (testing.Aborted) { break; }
                    await RunModule(module);
                }
            }
            if (plan.Budget == 1)
            {
                foreach (VerificationModule module in plan.Modules)
                {
                    if (testing.Aborted) { break; }
                    await RunModule(module);
                }
            }
            else
            {
                await Task.WhenAll(RunModule(plan.Modules.Single(module => module.Name == "Core")), RunUiLane());
            }
            outcomes = ResultGate.Evaluate(plan.Modules.Select(module => module.Name).ToArray(), executions.ToArray(), plan.RequiredScenarioIds);
            summaryDeadline = testing.CleanupDeadline;
        }
        catch (Exception error) { primary = error; }
        if (cancellationToken.IsCancellationRequested && primary is null) { primary = new OperationCanceledException(cancellationToken); }
        preparation.Stop();
        build.Stop();
        distribution.Stop();
        TimeSpan executionTime = executionStart == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(executionStart);
        double cleanupMs = MeasureIntervalUnion(cleanupIntervals);
        total.Stop();
        // summary保存より先に元の失敗を出す。保存失敗が加わっても診断を失わない。
        if (primary is not null) { Console.Error.WriteLine(primary); }
        foreach (ModuleOutcome outcome in outcomes)
        {
            Console.WriteLine($"{outcome.Name}: {(outcome.Passed ? "Passed" : "Failed")} {outcome.Cases} cases {string.Join(" / ", outcome.Errors)}");
        }
        try
        {
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
                distributionMs = distribution.Elapsed.TotalMilliseconds,
                testTimeoutSeconds = (run.TestTimeout ?? plan.TestTimeout).TotalSeconds,
                executionMs = executionTime.TotalMilliseconds,
                cleanupMs,
                totalMs = total.Elapsed.TotalMilliseconds,
                timingDefinition = "executionMsは最初のtest起動から全report集約・所有child終端まで。cleanupMsは重複を除いた回収区間の内数で加算しない。",
                primaryFailure = primary?.ToString(),
                commands = commands.Select(command => new
                {
                    command.Name,
                    exitCode = command.Completion?.ExitCode,
                    primaryFailure = command.PrimaryFailure?.ToString(),
                    outputFailure = command.OutputFailure?.ToString(),
                    cleanupFailure = command.CleanupFailure?.ToString()
                }),
                modules = outcomes.Select(outcome => new { outcome.Name, outcome.Cases, outcome.Passed, outcome.ElapsedMs, outcome.Errors }),
            };
            await WriteTextAsync(Path.Combine(run.RunRoot, "summary.json"), JsonSerializer.Serialize(summary,
                new JsonSerializerOptions { WriteIndented = true }), summaryDeadline);
        }
        catch (Exception error)
        {
            summaryFailure = error;
            Console.Error.WriteLine("summary保存失敗: " + error);
        }
        Console.WriteLine($"test wall={executionTime.TotalSeconds:F3}s（cleanup内数={cleanupMs / 1000:F3}s）total={total.Elapsed.TotalSeconds:F3}s");
        bool passed = primary is null && summaryFailure is null && outcomes.Count == plan.Modules.Count && outcomes.All(outcome => outcome.Passed);
        return new(passed ? 0 : 1, outcomes, commands.ToArray(), primary, summaryFailure);
    }

    private static async Task<VerificationCommandResult> ExecuteProcessAsync(VerificationRun run, RunnerStage stage,
        ProcessStartRequest request, string directory, string name, Action<long>? onStarting = null,
        ConcurrentBag<(long Start, long End)>? cleanupIntervals = null)
    {
        OwnedProcess? process = null;
        ProcessCompletion? completed = null;
        Exception? primary = null, output = null, cleanup = null;
        long started = 0;
        try
        {
            stage.ThrowIfCannotStart();
            long starting = Stopwatch.GetTimestamp();
            process = run.StartProcess(request, () =>
            {
                stage.Abort();
                return stage.CleanupDeadline;
            });
            started = starting;
            onStarting?.Invoke(started);
            completed = await process.WaitForExitAsync(stage.ExecutionDeadline, stage.CancellationToken);
            primary = process.PrimaryFailure;
            if (completed.ExitCode != 0 && request.OwnershipPolicy == ProcessOwnershipPolicy.Preparation)
            {
                // 準備失敗は後段へ進めない。停止・ログ保存より先に共通の回収猶予を確定する。
                stage.Abort();
            }
        }
        catch (Exception error)
        {
            primary = process?.PrimaryFailure is { } observed && !ReferenceEquals(observed, error) ? Combine(observed, error) : error;
            if (error is OwnedProcessStartException startFailure) { cleanup = startFailure.CleanupFailure; }
            stage.Abort();
        }
        finally
        {
            if (process is not null)
            {
                long cleanupStart = Stopwatch.GetTimestamp();
                try
                {
                    ProcessStopResult stopped = await process.StopAsync(run.RequestGracefulExit is null ? null
                        : token => run.RequestGracefulExit(process, token), stage.CleanupDeadline);
                    cleanup = stopped.CleanupFailure;
                }
                catch (Exception error) { cleanup = Combine(cleanup, error); }
                if (cleanup is not null) { stage.Abort(); }
                // 両pipeの保存は独立して試す。stdoutの書込み失敗でstderrを失わない。
                foreach ((Task<string> content, string suffix) in new[] { (process.StandardOutput, ".stdout.log"), (process.StandardError, ".stderr.log") })
                {
                    if (content.IsCompletedSuccessfully)
                    {
                        try { await WriteTextAsync(Path.Combine(directory, name + suffix), await content, stage.CleanupDeadline); }
                        catch (Exception error)
                        {
                            output = Combine(output, error);
                            stage.Abort();
                        }
                    }
                }
                try { await process.DisposeAsync(); }
                catch (Exception error)
                {
                    cleanup = Combine(cleanup, error);
                    stage.Abort();
                }
                cleanupIntervals?.Add((cleanupStart, Stopwatch.GetTimestamp()));
                if (process.Completion.IsCompletedSuccessfully) { completed = await process.Completion; }
                primary ??= process.PrimaryFailure;
            }
        }
        return new(name, completed, primary, output, cleanup, started == 0 ? null : Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static Exception Combine(Exception? previous, Exception next) => previous is null ? next
        : ReferenceEquals(previous, next) ? previous : new AggregateException(previous, next);

    private static async Task WriteTextAsync(string path, string content, ProcessDeadline deadline)
    {
        if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("ログ保存の期限を超えています。"); }
        using var canceled = new CancellationTokenSource(deadline.Remaining);
        // 元の書込みを直接待ち、取消後もファイル解放前に呼出元へ戻らない。
        // タイマーの取消配送が遅れた場合も、期限後の完了を成功にはしない。
        await File.WriteAllTextAsync(path, content, canceled.Token);
        if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("ログ保存の期限を超えています。"); }
    }

    private static double MeasureIntervalUnion(IEnumerable<(long Start, long End)> intervals)
    {
        long start = 0, end = 0;
        double milliseconds = 0;
        foreach ((long nextStart, long nextEnd) in intervals.OrderBy(interval => interval.Start))
        {
            if (nextStart > end)
            {
                if (end > start) { milliseconds += Stopwatch.GetElapsedTime(start, end).TotalMilliseconds; }
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
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln"))) { return directory.FullName; }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("BeMusicSeeker.slnを含むリポジトリから実行してください。");
    }

    /// <summary>一段階の絶対期限と致命失敗時の共有取消・回収cutoffだけを所有します。</summary>
    private sealed class RunnerStage : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource canceled = new();
        private readonly CancellationTokenRegistration registration;
        private readonly TimeSpan cleanupGrace;
        private ProcessDeadline? cleanupCutoff;

        /// <summary>段階開始時に実行期限を確定し、外部取消を同じ段階の失敗へ接続します。</summary>
        internal RunnerStage(TimeSpan timeout, TimeSpan cleanupGrace, CancellationToken cancellationToken)
        {
            ExecutionDeadline = ProcessDeadline.FromTimeout(timeout);
            this.cleanupGrace = cleanupGrace;
            registration = cancellationToken.Register(Abort);
        }
        /// <summary>全command・moduleで共有する実行期限です。</summary>
        internal ProcessDeadline ExecutionDeadline { get; }
        /// <summary>同段階のactive processへ致命失敗を伝える取消です。</summary>
        internal CancellationToken CancellationToken => canceled.Token;
        /// <summary>致命失敗または外部取消を一度でも検出したかを示します。</summary>
        internal bool Aborted { get { lock (gate) { return cleanupCutoff is not null; } } }
        /// <summary>正常終了では実行期限、致命失敗後は共有cutoffです。正常Core終了では後続UIの回収予算を開始しません。</summary>
        internal ProcessDeadline CleanupDeadline { get { lock (gate) { return cleanupCutoff ?? ExecutionDeadline; } } }

        /// <summary>取消・期限切れの段階で後続processを開始させません。</summary>
        internal void ThrowIfCannotStart()
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (ExecutionDeadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("段階の実行期限を超えています。"); }
        }
        /// <summary>致命失敗の最初の検出で回収cutoffを固定し、active processをまとめて取り消します。</summary>
        internal void Abort()
        {
            lock (gate)
            {
                if (cleanupCutoff is not null) { return; }
                cleanupCutoff = ExecutionDeadline.AddGrace(cleanupGrace).LimitRemaining(cleanupGrace);
            }
            canceled.Cancel();
        }
        /// <summary>全command終端後に取消の接続を解放します。</summary>
        public void Dispose()
        {
            registration.Dispose();
            canceled.Dispose();
        }
    }
}
