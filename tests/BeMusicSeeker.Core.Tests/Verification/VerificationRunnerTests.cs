using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>実runnerのpipelineと所有processを動かし、commandとreportのI/Oだけをfixtureへ向けます。</summary>
[TestClass]
public sealed class VerificationRunnerTests
{
    /// <summary>Fullの実pipelineで配布準備を一度だけ完了し、同じcatalogをReleaseの4moduleへ渡し、短IDと標準reportを集約します。</summary>
    /// <remarks>commandのI/Oだけをfixtureへ向けます。実publishと公開候補のFull成功を代用する検査ではありません。</remarks>
    [TestMethod]
    public async Task FullPreparesSharedArtifactsBeforeRunningEachSelectedModuleOnce()
    {
        using var distribution = new DistributionFixture();
        distribution.CreateLegacy();
        using var fixture = new RunnerFixture(distribution)
        {
            FormatInput = true,
            ScenarioIds = ["SC-01", "SC-02", "SC-03", "SC-04", "SC-05", "SC-06", "SC-08", "SC-09", "SC-10", "SC-11", "SC-12", "SC-13", "SC-14"],
        };
        VerificationRunResult result = await VerificationRunner.ExecuteAsync(VerificationPlan.Create("full", 1),
            fixture.Run() with { TestTimeout = null });
        Assert.AreEqual(0, result.ExitCode, result.PrimaryFailure?.ToString());
        CollectionAssert.AreEqual(new[] { "style", "whitespace", "build", "publish-v", "publish-next", "Core", "Windows", "Scenarios", "Desktop.E2E" }, fixture.Requests
            .Where(item => item.Name is "style" or "whitespace" or "build" or "publish-v" or "publish-next" or "Core" or "Windows" or "Scenarios" or "Desktop.E2E")
            .Select(item => item.Name).ToArray());
        ProcessStartRequest style = fixture.Requests.Single(item => item.Name == "style").Request;
        ProcessStartRequest whitespace = fixture.Requests.Single(item => item.Name == "whitespace").Request;
        Assert.AreEqual(fixture.Path("BeMusicSeeker.sln"), style.Arguments[2]);
        Assert.IsTrue(style.Arguments.Contains("--severity") && style.Arguments.Contains("error") && style.Arguments.Contains("--no-restore"));
        Assert.AreEqual(System.IO.Path.GetDirectoryName(fixture.Path("BeMusicSeeker.sln")), whitespace.Arguments[2]);
        Assert.IsTrue(whitespace.Arguments.Contains("--folder"));
        Assert.IsFalse(whitespace.Arguments.Contains("--no-restore"));
        Assert.IsFalse(style.Arguments.Contains("--include") || whitespace.Arguments.Contains("--include"));
        Assert.AreEqual(4, result.Modules.Count);
        string catalogPath = fixture.Path("run/distribution/catalog.json");
        Assert.IsTrue(File.Exists(catalogPath));
        foreach ((string _, ProcessStartRequest request) in fixture.Requests.Where(item => item.Name is "Core" or "Windows" or "Scenarios" or "Desktop.E2E"))
        {
            Assert.AreEqual(catalogPath, request.Environment[DistributionCatalog.EnvironmentVariable]);
            StringAssert.Contains(request.FileName, "Release");
        }
        using var summary = JsonDocument.Parse(File.ReadAllText(fixture.Path("run/summary.json")));
        Assert.AreEqual(1380d, summary.RootElement.GetProperty("testTimeoutSeconds").GetDouble());
        Assert.IsTrue(summary.RootElement.GetProperty("distributionMs").GetDouble() > 0);
        Assert.IsTrue(summary.RootElement.GetProperty("executionMs").GetDouble() > 0);
    }

    /// <summary>Fullの配布前段が失敗すると、残るvariantとtestを起動せず、元の非成功とログを同じrunner結果へ残します。</summary>
    [TestMethod]
    [DataRow("publish-v")]
    [DataRow("publish-next")]
    public async Task FailedFullPublicationDoesNotStartAnyTestModule(string failure)
    {
        using var distribution = new DistributionFixture();
        distribution.CreateLegacy();
        using var fixture = new RunnerFixture(distribution)
        {
            Configure = (name, command) => name == failure ? command with { ExitCode = 17 } : command,
        };
        VerificationRunResult result = await fixture.ExecuteAsync("full");
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsNotNull(result.PrimaryFailure);
        Assert.IsNull(result.SummaryFailure);
        Assert.AreEqual(17, result.Commands.Single(command => command.Completion?.ExitCode == 17).Completion?.ExitCode);
        Assert.AreEqual(failure == "publish-v" ? 1 : 2, fixture.Requests.Count(item => item.Name is "publish-v" or "publish-next"));
        Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Core" or "Windows" or "Scenarios" or "Desktop.E2E"));
        Assert.IsFalse(File.Exists(fixture.Path("run/distribution/catalog.json")));
    }

    /// <summary>全style・whitespaceの変更を後続buildが受け取り、同一pipeline内で準備からreport判定まで接続することを保証します。</summary>
    [TestMethod]
    public async Task FormatterChangesReachTheBuildInTheActualPipeline()
    {
        using var fixture = new RunnerFixture { FormatInput = true };
        VerificationRunResult result = await fixture.ExecuteAsync("verify");
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("formatted", File.ReadAllText(fixture.Path("changed.cs")));
        CollectionAssert.AreEqual(new[] { "style", "whitespace", "build" }, fixture.Requests
            .Where(item => item.Name is "style" or "whitespace" or "build").Select(item => item.Name).ToArray());
        Assert.IsTrue(result.Commands.All(command => command.Passed));
        Assert.AreEqual(3, result.Modules.Count);
    }

    /// <summary>restore・各整形・buildの非ゼロ終了で実pipelineが止まり、後段の起動で前段失敗を隠さないことを保証します。</summary>
    [TestMethod]
    [DataRow("restore", "style", "verify")]
    [DataRow("style", "whitespace", "verify")]
    [DataRow("whitespace", "build", "verify")]
    [DataRow("build", "Core", "verify")]
    [DataRow("style", "whitespace", "full")]
    public async Task FailedPreparationDoesNotStartTheNextStage(string fail, string next, string profile)
    {
        using var fixture = new RunnerFixture
        {
            FormatInput = true,
            Configure = (name, command) => name == fail ? command with { ExitCode = 17 } : command,
        };
        VerificationRunResult result = await fixture.ExecuteAsync(profile);
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsNotNull(result.PrimaryFailure);
        Assert.AreEqual(17, result.Commands.Single(command => command.Completion?.ExitCode == 17).Completion?.ExitCode);
        Assert.IsFalse(fixture.Requests.Any(item => item.Name == next));
        Assert.AreEqual(fail, fixture.Requests.Last().Name);
        Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Windows" or "Scenarios"));
    }

    /// <summary>準備commandが実行期限近くで非ゼロ終了しても、期限を跨ぐ停止処理の後に共有回収猶予で両ログとsummaryを保存し、元の失敗を保つことを保証します。</summary>
    [TestMethod]
    public async Task FailedPreparationUsesSharedCleanupGraceForLogsAndSummary()
    {
        using var fixture = new RunnerFixture();
        var readyToExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionBudget = TimeSpan.FromSeconds(3);
        ProcessDeadline? observedExecutionDeadline = null;
        long failureObserved = 0;
        fixture.Configure = (name, command) => name == "restore" ? command with
        {
            Ready = fixture.Path("restore-ready"),
            Release = fixture.Path("restore-release"),
            ExitCode = 17,
        } : command;
        fixture.BeforeStart = (name, _) =>
        {
            // 段階開始後の境界で作るため、この期限が切れれば実行期限も確実に過ぎている。
            if (name == "restore") { observedExecutionDeadline = ProcessDeadline.FromTimeout(executionBudget); }
        };
        fixture.AfterStart = (name, process) =>
        {
            if (name != "restore") { return; }
            // 既存の正常終了要求をgateで制御し、runnerのfinallyを実行期限後まで進ませない。
            _ = process.StopAsync(async token =>
            {
                await WaitForFileAsync(fixture.Path("restore-ready"));
                readyToExit.TrySetResult();
                await allowExit.Task.WaitAsync(token);
                File.WriteAllText(fixture.Path("restore-release"), "release");
                await process.Completion;
                failureObserved = Stopwatch.GetTimestamp();
                terminated.TrySetResult();
                await allowCleanup.Task.WaitAsync(token);
            }, Deadline());
        };
        VerificationRun run = fixture.Run() with { PreparationTimeout = executionBudget, CleanupGrace = TimeSpan.FromSeconds(5) };
        Task<VerificationRunResult> execution = VerificationRunner.ExecuteAsync(VerificationPlan.Parse(["dev"], 1), run);
        try
        {
            await readyToExit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ProcessDeadline boundary = observedExecutionDeadline ?? throw new InvalidOperationException("準備段階の起動を観測していません。");
            TimeSpan untilFailure = boundary.Remaining - TimeSpan.FromMilliseconds(500);
            if (untilFailure > TimeSpan.Zero) { await Task.Delay(untilFailure); }
            allowExit.TrySetResult();
            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 時間経過自体を条件にする検証であり、順序はterminatedとallowCleanupのgateで確定する。
            await Task.Delay(boundary.Remaining + TimeSpan.FromMilliseconds(10));
            Assert.AreEqual(TimeSpan.Zero, boundary.Remaining);
            allowCleanup.TrySetResult();
            VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            VerificationCommandResult restore = result.Commands.Single(command => command.Completion?.ExitCode == 17);
            Assert.AreEqual(1, result.ExitCode);
            Assert.IsNotNull(restore.PrimaryFailure);
            StringAssert.Contains(restore.PrimaryFailure.ToString(), "17");
            Assert.IsNotNull(result.PrimaryFailure);
            Assert.IsNull(restore.OutputFailure);
            Assert.IsNull(restore.CleanupFailure);
            Assert.IsNull(result.SummaryFailure);
            Assert.AreEqual("stdout", File.ReadAllText(fixture.Path("run/" + restore.Name + ".stdout.log")));
            Assert.AreEqual("stderr", File.ReadAllText(fixture.Path("run/" + restore.Name + ".stderr.log")));
            using var summary = JsonDocument.Parse(File.ReadAllText(fixture.Path("run/summary.json")));
            StringAssert.Contains(summary.RootElement.GetProperty("primaryFailure").GetString() ?? "", "17");
            Assert.IsTrue(Stopwatch.GetElapsedTime(failureObserved) < run.CleanupGrace);
            Assert.IsFalse(fixture.Requests.Any(item => item.Name is "build" or "Core" or "Windows" or "Scenarios"));
        }
        finally
        {
            allowExit.TrySetResult();
            allowCleanup.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>所有対象が全終端した非ゼロmoduleの失敗を保ちながら、他のmoduleの結果を回収することを保証します。</summary>
    [TestMethod]
    public async Task CompletedNonzeroModuleDoesNotPreventOtherModules()
    {
        using var fixture = new RunnerFixture { Configure = (name, command) => name == "Core" ? command with { ExitCode = 7 } : command };
        VerificationRunResult result = await fixture.ExecuteAsync();
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(3, result.Modules.Count);
        Assert.IsTrue(result.Modules.Where(module => module.Name != "Core").All(module => module.Passed));
        VerificationCommandResult core = result.Commands.Single(command => command.Name == "Core");
        Assert.AreEqual(7, core.Completion?.ExitCode);
        Assert.IsNotNull(core.PrimaryFailure);
        Assert.IsNull(core.CleanupFailure);
    }

    /// <summary>成功reportでもDaily必須IDの不足を拒否し、標準filterがmodule集合を減らさず各起動引数へ渡ることを保証します。</summary>
    [TestMethod]
    public async Task RequiredDailyScenariosAndStandardFiltersReachTheGateAndProcesses()
    {
        using var fixture = new RunnerFixture { ScenarioIds = ["SC-01", "SC-02"] };
        const string filter = "FullyQualifiedName~日本語|TestCategory=Other";
        var plan = VerificationPlan.Parse(["dev", "--filter", filter], 1);
        VerificationRunResult result = await VerificationRunner.ExecuteAsync(plan, fixture.Run());
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsTrue(result.Modules.Single(module => module.Name == "Scenarios").Errors.Any(error => error.Contains("SC-09", StringComparison.Ordinal)));
        foreach ((string name, ProcessStartRequest request) in fixture.Requests.Where(item => item.Name is "Core" or "Windows" or "Scenarios"))
        {
            string[] arguments = request.Arguments.ToArray();
            string expected = name == "Scenarios" ? $"(TestCategory=Daily)&({filter})" : filter;
            Assert.AreEqual(expected, arguments[Array.IndexOf(arguments, "--filter") + 1]);
        }
        Assert.AreEqual(3, result.Modules.Count);
    }

    /// <summary>stdout保存だけが失敗してもstderrを保存し、成功processとreportだけでは入口全体を成功にしないことを保証します。</summary>
    [TestMethod]
    public async Task OutputFailureAloneIsUnsuccessfulAndDoesNotLoseStandardError()
    {
        using var fixture = new RunnerFixture();
        fixture.BeforeStart = (name, _) =>
        {
            if (name == "Core") { Directory.CreateDirectory(fixture.Path("run/Core/process.stdout.log")); }
        };
        VerificationRunResult result = await fixture.ExecuteAsync();
        VerificationCommandResult core = result.Commands.Single(command => command.Name == "Core");
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(0, core.Completion?.ExitCode);
        Assert.IsNull(core.PrimaryFailure);
        Assert.IsNotNull(core.OutputFailure);
        Assert.IsNull(core.CleanupFailure);
        Assert.AreEqual("stderr", File.ReadAllText(fixture.Path("run/Core/process.stderr.log")));
    }

    /// <summary>正常終了したprocessの停止処理だけが失敗した場合も、主失敗を捏造せず回収失敗として非成功にすることを保証します。</summary>
    [TestMethod]
    public async Task CleanupFailureAloneRemainsUnsuccessful()
    {
        using var fixture = new RunnerFixture();
        fixture.Configure = (name, command) => name == "Core" ? command with
        {
            Ready = fixture.Path("core-ready"),
            Release = fixture.Path("core-release"),
        } : command;
        var failure = new IOException("cleanup-only");
        fixture.AfterStart = (name, process) =>
        {
            if (name != "Core") { return; }
            // 同じOwnedProcessの既存正常終了要求を先に開始し、正常終端後の要求失敗だけを作る。
            _ = process.StopAsync(async _ =>
            {
                await WaitForFileAsync(fixture.Path("core-ready"));
                File.WriteAllText(fixture.Path("core-release"), "release");
                await process.Completion;
                throw failure;
            }, Deadline());
        };
        VerificationRunResult result = await fixture.ExecuteAsync();
        VerificationCommandResult core = result.Commands.Single(command => command.Name == "Core");
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(0, core.Completion?.ExitCode);
        Assert.IsNull(core.PrimaryFailure);
        Assert.IsNull(core.OutputFailure);
        Assert.AreSame(failure, core.CleanupFailure);
        Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Windows" or "Scenarios"));
    }

    /// <summary>root非成功・ログ保存失敗・正常終了要求失敗が重なってもすべてを保持し、Job内の生存childを回収することを保証します。</summary>
    [TestMethod]
    public async Task PrimaryOutputAndCleanupFailuresAreAllRetained()
    {
        using var fixture = new RunnerFixture();
        fixture.Configure = (name, command) => name == "Core" ? command with
        {
            ChildReady = fixture.Path("child-ready"),
            ChildRelease = fixture.Path("child-release"),
            ExitBeforeChild = true,
            ExitCode = 7,
        } : command;
        fixture.BeforeStart = (name, _) =>
        {
            if (name == "Core") { Directory.CreateDirectory(fixture.Path("run/Core/process.stdout.log")); }
        };
        VerificationRun run = fixture.Run() with
        {
            TestTimeout = TimeSpan.FromSeconds(5),
            RequestGracefulExit = (_, _) => Task.FromException(new IOException("graceful-failure")),
        };
        Task<VerificationRunResult> execution = VerificationRunner.ExecuteAsync(VerificationPlan.Parse(["dev"], 1), run);
        await WaitForFileAsync(fixture.Path("child-ready"));
        using var child = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.Path("child-ready"))));
        VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
        VerificationCommandResult core = result.Commands.Single(command => command.Name == "Core");
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(7, core.Completion?.ExitCode);
        Assert.IsNotNull(core.PrimaryFailure);
        StringAssert.Contains(core.PrimaryFailure.ToString(), "7");
        Assert.IsNotNull(core.OutputFailure);
        Assert.IsNotNull(core.CleanupFailure);
        StringAssert.Contains(core.CleanupFailure.ToString(), "graceful-failure");
        Assert.AreEqual("stderr", File.ReadAllText(fixture.Path("run/Core/process.stderr.log")));
        Assert.IsTrue(child.HasExited);
        Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Windows" or "Scenarios"));
    }

    /// <summary>起動段階で回収も失敗した場合、起動の主失敗と回収失敗を別々に記録して後段を開始しないことを保証します。</summary>
    [TestMethod]
    public async Task StartFailureKeepsItsCleanupFailure()
    {
        using var fixture = new RunnerFixture();
        var start = new IOException("start-primary");
        var cleanup = new IOException("start-cleanup");
        fixture.BeforeStart = (name, _) =>
        {
            if (name == "restore") { throw new OwnedProcessStartException(start, cleanup); }
        };
        VerificationRunResult result = await fixture.ExecuteAsync();
        VerificationCommandResult command = result.Commands.Single(item => item.Completion is null);
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsInstanceOfType<OwnedProcessStartException>(command.PrimaryFailure);
        Assert.IsNotNull(command.PrimaryFailure);
        Assert.AreSame(start, command.PrimaryFailure.InnerException);
        Assert.AreSame(cleanup, command.CleanupFailure);
        Assert.IsFalse(fixture.Requests.Any(item => item.Name == "build"));
    }

    /// <summary>summary保存だけの失敗も非成功とし、moduleの主失敗がある場合はそれも結果に残すことを保証します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SummaryFailureNeverTurnsTheRunIntoSuccessOrLosesTheOriginal(bool failModule)
    {
        using var fixture = new RunnerFixture
        {
            Configure = (name, command) => failModule && name == "Core" ? command with { ExitCode = 7 } : command,
        };
        Directory.CreateDirectory(fixture.Path("run/summary.json"));
        VerificationRunResult result = await fixture.ExecuteAsync();
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsNotNull(result.SummaryFailure);
        Assert.AreEqual(!failModule, result.Modules.All(module => module.Passed));
        Assert.AreEqual(failModule, result.Commands.Single(command => command.Name == "Core").PrimaryFailure is not null);
    }

    /// <summary>正常Core終了から回収猶予以上が経過しても、後続UIの致命失敗時点から回収猶予を確保し、先行正常終了で期限を消費しないことを保証します。</summary>
    [TestMethod]
    public async Task SuccessfulCoreDoesNotStartTheLaterUiCleanupBudget()
    {
        using var fixture = new RunnerFixture();
        using var canceled = new CancellationTokenSource();
        var graceful = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long graceStart = 0, graceEnd = 0;
        fixture.Configure = (name, command) => name == "Windows" ? command with
        {
            Ready = fixture.Path("windows-ready"),
            Release = fixture.Path("windows-release"),
        } : command;
        VerificationRun run = fixture.Run() with
        {
            CleanupGrace = TimeSpan.FromSeconds(1),
            RequestGracefulExit = async (_, token) =>
            {
                graceStart = Stopwatch.GetTimestamp();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally
                {
                    graceEnd = Stopwatch.GetTimestamp();
                    graceful.TrySetResult();
                }
            },
        };
        Task<VerificationRunResult> execution = VerificationRunner.ExecuteAsync(VerificationPlan.Parse(["dev"], 1), run, canceled.Token);
        try
        {
            await WaitForFileAsync(fixture.Path("windows-ready"));
            using var root = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.Path("windows-ready"))));
            // 正常Coreの後に、回収予算が先に始まっていたなら失効する時間まで意図的に進める。
            using var elapsed = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, elapsed.Token); }
            catch (OperationCanceledException) when (elapsed.IsCancellationRequested) { }
            canceled.Cancel();
            await graceful.Task.WaitAsync(TimeSpan.FromSeconds(5));
            VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, result.ExitCode);
            Assert.IsTrue(result.Modules.Single(module => module.Name == "Core").Passed);
            Assert.IsTrue(root.HasExited);
            Assert.IsTrue(Stopwatch.GetElapsedTime(graceStart, graceEnd).TotalMilliseconds > 250,
                "正常Coreの終了時点でUI用回収予算を確定しています。");
        }
        finally
        {
            canceled.Cancel();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>二つの実Jobをready後に取消または期限超過させ、遅れて回収へ入ったprocessも同じcutoffを使い、全root・childが終端することを保証します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FatalStageFailureSharesOneCutoffAndTerminatesAllOwnedProcesses(bool timeout)
    {
        using var fixture = new RunnerFixture();
        using var canceled = new CancellationTokenSource();
        var returnWindows = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGraceEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long firstStart = 0, firstEnd = 0, secondStart = 0, secondEnd = 0;
        fixture.Configure = (name, command) => name is "Core" or "Windows" ? command with
        {
            Ready = fixture.Path(name + "-ready"),
            Release = fixture.Path(name + "-release"),
            ChildReady = fixture.Path(name + "-child-ready"),
            ChildRelease = fixture.Path(name + "-child-release"),
        } : command;
        fixture.AfterStart = (name, _) =>
        {
            // start要求は実processを返す前で止める。先行回収のtoken取消が、後行回収の開始gateになる。
            if (name == "Windows") { returnWindows.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
        };
        VerificationRun run = fixture.Run() with
        {
            TestTimeout = TimeSpan.FromSeconds(timeout ? 5 : 20),
            CleanupGrace = TimeSpan.FromSeconds(2),
            RequestGracefulExit = async (process, token) =>
            {
                bool first = fixture.Names[process.ProcessId] == "Core";
                if (first) { firstStart = Stopwatch.GetTimestamp(); } else { secondStart = Stopwatch.GetTimestamp(); }
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally
                {
                    if (first)
                    {
                        firstEnd = Stopwatch.GetTimestamp();
                        firstGraceEnded.TrySetResult();
                    }
                    else { secondEnd = Stopwatch.GetTimestamp(); }
                }
            },
        };
        Task<VerificationRunResult> execution = VerificationRunner.ExecuteAsync(VerificationPlan.Parse(["dev"], 2), run, canceled.Token);
        var observed = new List<Process>();
        try
        {
            foreach (string name in new[] { "Core", "Windows" })
            {
                foreach (string suffix in new[] { "-ready", "-child-ready" })
                {
                    await WaitForFileAsync(fixture.Path(name + suffix));
                    observed.Add(Process.GetProcessById(int.Parse(File.ReadAllText(fixture.Path(name + suffix)))));
                }
            }
            if (!timeout) { canceled.Cancel(); }
            await firstGraceEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            returnWindows.TrySetResult();
            VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, result.ExitCode);
            Assert.IsTrue(observed.All(process => process.HasExited));
            Assert.IsFalse(fixture.Requests.Any(item => item.Name == "Scenarios"));
            Assert.AreEqual(2, result.Commands.Count(command => command.Name is "Core" or "Windows"));
            double firstMs = Stopwatch.GetElapsedTime(firstStart, firstEnd).TotalMilliseconds;
            double secondMs = Stopwatch.GetElapsedTime(secondStart, secondEnd).TotalMilliseconds;
            Assert.IsTrue(firstStart > 0 && secondStart > firstEnd);
            Assert.IsTrue(secondMs < firstMs * 0.85 + 100, $"後行回収が新しい猶予を受けています: first={firstMs}, second={secondMs}");
            Assert.IsTrue(Stopwatch.GetElapsedTime(firstStart).TotalSeconds < 2.8, "段階の共有回収cutoffを超えています。");
        }
        finally
        {
            canceled.Cancel();
            returnWindows.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (Process process in observed) { process.Dispose(); }
        }
    }

    private static ProcessDeadline Deadline() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));

    private static async Task WaitForFileAsync(string path)
    {
        ProcessDeadline deadline = Deadline();
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("fixtureのgateが開きません: " + path); }
            await Task.Delay(10);
        }
    }

    private sealed class RunnerFixture(DistributionFixture? distribution = null) : IDisposable
    {
        private readonly TestWorkspace? workspace = distribution is null ? TestWorkspace.Create(TestRunDirectory.RootDirectory) : null;
        private int commandIndex;
        internal bool FormatInput { get; init; }
        internal string[] ScenarioIds { get; init; } = ["SC-01", "SC-02", "SC-09"];
        internal Func<string, Command, Command>? Configure { get; set; }
        internal Action<string, ProcessStartRequest>? BeforeStart { get; set; }
        internal Action<string, OwnedProcess>? AfterStart { get; set; }
        internal ConcurrentQueue<(string Name, ProcessStartRequest Request)> Requests { get; } = new();
        internal ConcurrentDictionary<int, string> Names { get; } = new();
        private string Root => distribution?.Root ?? workspace?.RootDirectory ?? throw new InvalidOperationException();
        internal string Path(string relative) => distribution?.PathFor(relative) ?? workspace?.GetPath(relative) ?? throw new InvalidOperationException();
        internal VerificationRun Run()
        {
            File.WriteAllText(Path("changed.cs"), "original");
            Directory.CreateDirectory(Path("run"));
            return new VerificationRun(Root, Path("run"))
            {
                StartProcess = Start,
                MetadataTimeout = TimeSpan.FromSeconds(10),
                PreparationTimeout = TimeSpan.FromSeconds(10),
                FormatTimeout = TimeSpan.FromSeconds(10),
                TestTimeout = TimeSpan.FromSeconds(10),
                DistributionTimeout = TimeSpan.FromSeconds(30),
                CleanupGrace = TimeSpan.FromSeconds(2),
            };
        }
        internal Task<VerificationRunResult> ExecuteAsync(string profile = "dev") => VerificationRunner.ExecuteAsync(VerificationPlan.Parse([profile], 1), Run());
        private OwnedProcess Start(ProcessStartRequest request, Func<ProcessDeadline> beginFailureCleanup)
        {
            string name = request.FileName == "pwsh" ? request.Arguments[^1].Contains(" -SkipDocHtml -ArtifactRoot ", StringComparison.Ordinal)
                ? request.Arguments[^1].Contains(" -ApplicationVersion ", StringComparison.Ordinal) ? "publish-next" : "publish-v" : "layout"
                : request.FileName is "dotnet" or "git" ? request.Arguments[0] :
                System.IO.Path.GetFileName(request.FileName)["BeMusicSeeker.".Length..^".Tests.exe".Length];
            if (name == "format") { name = request.Arguments[1]; }
            Requests.Enqueue((name, request));
            BeforeStart?.Invoke(name, request);
            var command = new Command();
            if (request.FileName == "git")
            {
                command = command with
                {
                    StandardOutput = request.Arguments.Contains("--name-only") || name == "ls-files"
                    ? FormatInput ? "changed.cs\0" : "" : name == "rev-parse" ? "fixture-source" : ""
                };
            }
            if (name == "style") { command = command with { WritePath = Path("changed.cs"), WriteContent = "style" }; }
            if (name == "whitespace") { command = command with { RequirePath = Path("changed.cs"), RequireContent = "style", WritePath = Path("changed.cs"), WriteContent = "formatted" }; }
            if (name == "build" && FormatInput) { command = command with { RequirePath = Path("changed.cs"), RequireContent = "formatted" }; }
            if (name is "Core" or "Windows" or "Scenarios" or "Desktop.E2E")
            {
                string[] arguments = request.Arguments.ToArray();
                string reportDirectory = arguments[Array.IndexOf(arguments, "--results-directory") + 1];
                string report = Path(name + "-report-fixture.xml");
                IEnumerable<string> names = name switch
                {
                    "Scenarios" => ScenarioIds.Select(id => $"[{id}] report fixture"),
                    "Desktop.E2E" => new[] { "SC-15a", "SC-15b", "SC-16", "DT-01", "DT-02", "DT-03" }.Select(id => $"[{id}] report fixture"),
                    _ => ["report fixture"],
                };
                WriteReport(report, names);
                command = command with { ReportSource = report, ReportDestination = System.IO.Path.Combine(reportDirectory, "results.trx") };
            }
            command = Configure?.Invoke(name, command) ?? command;
            if (command.ExitCode == 0 && name is "publish-v" or "publish-next")
            {
                DistributionFixture input = distribution ?? throw new InvalidOperationException("配布入力fixtureがありません。");
                input.CreatePublishedVariant(name == "publish-next");
            }
            string specification = Path($"command-{Interlocked.Increment(ref commandIndex)}.json");
            File.WriteAllText(specification, JsonSerializer.Serialize(command));
            var process = OwnedProcess.Start(new ProcessStartRequest(System.IO.Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe"),
                ["runner-command", specification], request.WorkingDirectory, request.Environment, request.OwnershipPolicy), beginFailureCleanup);
            Names[process.ProcessId] = name;
            AfterStart?.Invoke(name, process);
            return process;
        }
        private static void WriteReport(string path, IEnumerable<string> names)
        {
            // report境界の入力であり、実Scenarioの成功caseやmoduleは追加しない。
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            string[] rows = names.ToArray();
            new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", rows.Select(name =>
                    new XElement(ns + "UnitTestResult", new XAttribute("testName", name), new XAttribute("outcome", "Passed")))),
                new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"), new XElement(ns + "Counters",
                    new XAttribute("total", rows.Length), new XAttribute("executed", rows.Length), new XAttribute("passed", rows.Length))))).Save(path);
        }
        public void Dispose() => workspace?.Dispose();
    }

    /// <summary>同じJSON command/I/O境界へ渡す入力です。実行順・期限・集約の判断は含めません。</summary>
    private sealed record Command
    {
        public string StandardOutput { get; init; } = "stdout";
        public string StandardError { get; init; } = "stderr";
        public int ExitCode { get; init; }
        public string? Ready { get; init; }
        public string? Release { get; init; }
        public string? WritePath { get; init; }
        public string? WriteContent { get; init; }
        public string? RequirePath { get; init; }
        public string? RequireContent { get; init; }
        public string? ReportSource { get; init; }
        public string? ReportDestination { get; init; }
        public string? ChildReady { get; init; }
        public string? ChildRelease { get; init; }
        public bool ExitBeforeChild { get; init; }
    }
}
