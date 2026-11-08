using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.TestSupport.Verification;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>実runnerのpipelineと所有processを動かし、commandとreportのI/Oだけをfixtureへ向けます。</summary>
[TestClass]
public sealed class VerificationRunnerTests
{
    /// <summary>固定SDKを使うnative工程は共通180秒を引継ぎ、query失敗時は任意fixtureを生成・起動しません。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task NativeQueryKeepsFixedSequenceAndDeadline(bool buildFixture, bool failQuery)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string sdk = workspace.GetPath("native/Everything3_x64.dll");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sdk) ?? throw new InvalidOperationException());
        File.Copy(System.IO.Path.Combine(BeMusicSeeker.TestSupport.Components.ThemeTestFiles.FindRepositoryRoot(), "native", "Everything3_x64.dll"), sdk);
        string output = workspace.GetPath("native/EverythingBridge/obj/query-tests");
        var calls = new List<(string Executable, string[] Arguments)>();
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(180));
        Task<ProcessCompletion> Execute(string executable, IReadOnlyList<string> arguments, ProcessDeadline actual)
        {
            Assert.AreSame(deadline, actual);
            calls.Add((System.IO.Path.GetFileName(executable), arguments.ToArray()));
            if (executable.EndsWith("vswhere.exe", StringComparison.Ordinal))
            { return Task.FromResult(new ProcessCompletion(0, workspace.GetPath("MSBuild.exe") + "\n", "")); }
            return Task.FromResult(new ProcessCompletion(failQuery && executable.EndsWith("EverythingBridgeQueryTests.exe", StringComparison.Ordinal) ? 17 : 0, "", "native-original-error"));
        }
        if (failQuery)
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeQueryVerification.ExecuteAsync(workspace.RootDirectory, buildFixture, Execute, deadline));
            StringAssert.Contains(error.Message, "native-original-error");
        }
        else { await NativeQueryVerification.ExecuteAsync(workspace.RootDirectory, buildFixture, Execute, deadline); }
        string[] expected = buildFixture && !failQuery ? ["vswhere.exe", "MSBuild.exe", "EverythingBridgeQueryTests.exe", "MSBuild.exe"]
            : ["vswhere.exe", "MSBuild.exe", "EverythingBridgeQueryTests.exe"];
        CollectionAssert.AreEqual(expected, calls.Select(call => call.Executable).ToArray());
        CollectionAssert.AreEqual(new[] { sdk }, calls[2].Arguments);
        foreach ((string Executable, string[] Arguments) compile in calls.Where(call => call.Executable == "MSBuild.exe"))
        {
            CollectionAssert.AreEqual(new[] { "/t:Rebuild", "/p:Configuration=Release", "/p:Platform=x64", "/nologo", "/verbosity:minimal" }, compile.Arguments.Skip(1).ToArray());
        }
        var query = XDocument.Load(System.IO.Path.Combine(output, "QueryTests.vcxproj"));
        XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        Assert.AreEqual("v145", query.Descendants(ns + "PlatformToolset").Single().Value);
        string fixtureProject = System.IO.Path.Combine(output, "PathIdentityFixture.vcxproj");
        Assert.AreEqual(buildFixture && !failQuery, File.Exists(fixtureProject));
        if (buildFixture && !failQuery)
        {
            var fixture = XDocument.Load(fixtureProject);
            Assert.AreEqual("DynamicLibrary", fixture.Descendants(ns + "ConfigurationType").Single().Value);
            StringAssert.StartsWith(fixture.Descendants(ns + "PreprocessorDefinitions").Single().Value, "BMS_PATH_IDENTITY_FIXTURE;");
            CollectionAssert.AreEqual(File.ReadAllBytes(sdk), File.ReadAllBytes(System.IO.Path.Combine(output, "path-identity-fixture", "native", "Everything3_x64.dll")));
        }
    }

    /// <summary>固定SDKに一致しない入力はcompiler探索前に拒否し、生成物や代替SDKを作りません。</summary>
    [TestMethod]
    public async Task NativeQueryRejectsChangedSdkBeforeStartingCommands()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string sdk = workspace.GetPath("native/Everything3_x64.dll");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sdk) ?? throw new InvalidOperationException());
        File.WriteAllText(sdk, "invalid SDK");
        int calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeQueryVerification.ExecuteAsync(workspace.RootDirectory, true,
            (_, _, _) => { calls++; return Task.FromResult(new ProcessCompletion(0, "", "")); }, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(180))));
        Assert.AreEqual(0, calls);
        Assert.IsFalse(Directory.Exists(workspace.GetPath("native/EverythingBridge/obj/query-tests")));
    }

    /// <summary>手動formatも既存準備process境界を使い、任意restoreと二段の整形を接続して前段失敗で停止します。</summary>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    public async Task ManualFormatUsesPreparationBoundaryAndStopsOnRestoreFailure(bool allSources, bool noRestore, bool failRestore)
    {
        using var fixture = new RunnerFixture
        {
            FormatInput = true,
            Configure = (name, command) => failRestore && name == "restore" ? command with { ExitCode = 17 } : command,
        };
        int result = await VerificationRunner.FormatAsync(fixture.Run(), allSources, noRestore);
        Assert.AreEqual(failRestore ? 1 : 0, result);
        string[] expected = allSources ? ["style", "whitespace"]
            : failRestore ? ["diff", "diff", "ls-files", "restore"] : ["diff", "diff", "ls-files", "restore", "style", "whitespace"];
        CollectionAssert.AreEqual(expected, fixture.Requests.Select(item => item.Name).ToArray());
        Assert.IsTrue(fixture.Requests.All(item => item.Request.OwnershipPolicy == ProcessOwnershipPolicy.Preparation));
        Assert.IsTrue(fixture.Requests.All(item => item.Request.Environment.ContainsKey(ProcessDeadline.SuiteDeadlineEnvironmentVariable)
            && item.Request.Environment[ProcessDeadline.SuiteDeadlineEnvironmentVariable] is null));
        if (failRestore) { StringAssert.Contains(File.ReadAllText(fixture.Path("run/format-004.stderr.log")), "stderr"); }
    }

    /// <summary>CPU予算によらず選択suiteを一回だけ起動し、標準filter・設定・reportと同じrunの証跡へ接続します。</summary>
    /// <remarks>固定参照・native・音声入力の継承を妨げる子環境上書きと親環境変更を検出します。実入力読取りは各正式suiteの受入で確認します。</remarks>
    [TestMethod]
    [DataRow("compatibility", 1, "chartstring-java")]
    [DataRow("compatibility", 8, "chartstring-java")]
    [DataRow("performance", 8, "copy-tracking")]
    [DataRow("compatibility", 8, "install-acl")]
    [DataRow("compatibility", 8, "everything-native-path-identity")]
    [DataRow("compatibility", 8, "audio-encoders")]
    [DataRow("compatibility", 8, "audio-encoder-child")]
    public async Task ExplicitSuiteRunsOnlySelectedModuleAndRecordsAppliedContract(string profile, int processors, string suite)
    {
        string[] inputVariables = ["BMS_TEST_CHARTSTRING_REFERENCE_JSON", "BMS_EVERYTHING_PATH_IDENTITY_FIXTURE",
            "BMS_TEST_AUDIO_ENCODERS", "BMS_TEST_AUDIO_ENCODER_DIR", "BMS_TEST_AUDIO_ENCODER_TYPES"];
        Dictionary<string, string?> originalInputs = inputVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        string? originalDeadline = Environment.GetEnvironmentVariable(ProcessDeadline.SuiteDeadlineEnvironmentVariable);
        const string filter = "FullyQualifiedName~Reference|TestCategory=OtherSuite";
        string moduleName = profile == "compatibility" ? "Compatibility" : "Performance";
        string expectedFilter = profile == "compatibility" ? $"(TestCategory=Suite.{suite})&({filter})" : $"TestCategory=Suite.{suite}";
        using var fixture = new RunnerFixture();
        var plan = VerificationPlan.Parse(profile == "compatibility"
            ? [profile, "--suite", suite, "--filter", filter] : [profile, "--suite", suite], processors);
        VerificationRunResult result = await VerificationRunner.ExecuteAsync(plan, fixture.Run() with { TestTimeout = null });
        Assert.AreEqual(0, result.ExitCode, result.PrimaryFailure?.ToString());
        CollectionAssert.AreEqual(new[] { "rev-parse", "status", "--version", "restore", "build", moduleName },
            fixture.Requests.Select(item => item.Name).ToArray());
        ProcessStartRequest restore = fixture.Requests.Single(item => item.Name == "restore").Request;
        Assert.IsTrue(restore.Arguments.Contains($"tests/verification-{profile}.slnf"));
        Assert.IsTrue(restore.Arguments.Contains("--locked-mode"));
        ProcessStartRequest build = fixture.Requests.Single(item => item.Name == "build").Request;
        Assert.IsTrue(build.Arguments.Contains($"tests/verification-{profile}.slnf") && build.Arguments.Contains("--no-restore"));
        Assert.IsTrue(build.Arguments.Contains("Release"));
        ProcessStartRequest request = fixture.Requests.Single(item => item.Name == moduleName).Request;
        StringAssert.Contains(request.FileName, "Release");
        string[] arguments = request.Arguments.ToArray();
        Assert.AreEqual(expectedFilter, arguments[Array.IndexOf(arguments, "--filter") + 1]);
        Assert.AreEqual("1", arguments[Array.IndexOf(arguments, "--minimum-expected-tests") + 1]);
        var settings = XDocument.Load(arguments[Array.IndexOf(arguments, "--settings") + 1]);
        Assert.AreEqual("1", settings.Descendants("Workers").Single().Value);
        Assert.AreEqual("MethodLevel", settings.Descendants("Scope").Single().Value);
        Assert.IsNotNull(request.Environment[ProcessDeadline.SuiteDeadlineEnvironmentVariable]);
        var imported = ProcessDeadline.Import(request.Environment[ProcessDeadline.SuiteDeadlineEnvironmentVariable] ?? throw new AssertFailedException());
        Assert.IsTrue(imported.Remaining > TimeSpan.Zero && imported.Remaining <= plan.TestTimeout);
        foreach ((string Name, ProcessStartRequest Request) preparation in fixture.Requests.Where(item => item.Name != moduleName))
        {
            Assert.IsTrue(preparation.Request.Environment.ContainsKey(ProcessDeadline.SuiteDeadlineEnvironmentVariable));
            Assert.IsNull(preparation.Request.Environment[ProcessDeadline.SuiteDeadlineEnvironmentVariable]);
        }
        Assert.AreEqual(originalDeadline, Environment.GetEnvironmentVariable(ProcessDeadline.SuiteDeadlineEnvironmentVariable));
        foreach (string inputVariable in inputVariables)
        {
            Assert.IsFalse(request.Environment.ContainsKey(inputVariable));
            Assert.AreEqual(originalInputs[inputVariable], Environment.GetEnvironmentVariable(inputVariable));
        }
        Assert.IsTrue(result.Commands.All(command => command.Passed));
        Assert.AreEqual(moduleName, result.Modules.Single().Name);
        Assert.AreEqual(1, result.Modules.Single().Cases);
        Assert.IsTrue(File.Exists(fixture.Path($"run/{moduleName}/results.trx")));
        Assert.AreEqual("stdout", File.ReadAllText(fixture.Path($"run/{moduleName}/process.stdout.log")));
        Assert.AreEqual("stderr", File.ReadAllText(fixture.Path($"run/{moduleName}/process.stderr.log")));
        using var summary = JsonDocument.Parse(File.ReadAllText(fixture.Path("run/summary.json")));
        string caseRoot = summary.RootElement.GetProperty("caseRoot").GetString() ?? throw new AssertFailedException();
        Assert.AreEqual(Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory) ?? throw new AssertFailedException(), "bms-tests"), Path.GetDirectoryName(caseRoot));
        Assert.AreEqual(Path.Combine(caseRoot, moduleName), request.Environment[TestRunDirectory.EnvironmentVariable]);
        Assert.AreEqual(suite, summary.RootElement.GetProperty("suite").GetString());
        Assert.AreEqual(60d, summary.RootElement.GetProperty("testTimeoutSeconds").GetDouble());
        JsonElement selection = summary.RootElement.GetProperty("filters").EnumerateArray().Single();
        Assert.AreEqual(moduleName, selection.GetProperty("Name").GetString());
        Assert.AreEqual(plan.Modules.Single().Filter, selection.GetProperty("Filter").GetString());
    }

    /// <summary>単一suiteの標準reportが空なら、process成功でも選択ゼロを成功へ変えません。</summary>
    [TestMethod]
    [DataRow("compatibility")]
    [DataRow("performance")]
    public async Task ExplicitSuiteRejectsEmptySelectedResults(string profile)
    {
        using var fixture = new RunnerFixture { CompatibilityNames = [], PerformanceNames = [] };
        VerificationRunResult result = await VerificationRunner.ExecuteAsync(
            VerificationPlan.Parse(profile == "compatibility"
                ? [profile, "--suite", "chart-info-edge", "--filter", "FullyQualifiedName~Absent"]
                : [profile, "--suite", "copy-tracking"], 8), fixture.Run());
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsTrue(result.Commands.Single(command => command.Name == (profile == "compatibility" ? "Compatibility" : "Performance")).Passed);
        Assert.IsFalse(result.Modules.Single().Passed);
        Assert.AreEqual(0, result.Modules.Single().Cases);
    }

    /// <summary>単一suiteの開始後の待機とroot終了後の所有child待機が、同じtest期限で失敗し回収されることを保証します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompatibilityDeadlineIncludesRootAndOwnedChild(bool exitBeforeChild)
    {
        var fixture = new RunnerFixture();
        using var canceled = new CancellationTokenSource();
        var owned = new ConcurrentDictionary<string, OwnedProcess>();
        var observed = new List<Process>();
        fixture.Configure = (name, command) => name == "Compatibility" ? command with
        {
            Ready = fixture.Path("ready"),
            Release = fixture.Path("release"),
            ChildReady = fixture.Path("child-ready"),
            ChildRelease = fixture.Path("child-release"),
            ExitBeforeChild = exitBeforeChild,
        } : command;
        fixture.AfterStart = (name, process) => { if (name == "Compatibility") { owned[name] = process; } };
        VerificationRun run = fixture.Run() with { TestTimeout = TimeSpan.FromSeconds(5), CleanupGrace = TimeSpan.FromSeconds(5) };
        Task<VerificationRunResult> execution = VerificationRunner.ExecuteAsync(
            VerificationPlan.Parse(["compatibility", "--suite", "chart-info-bms"], 8), run, canceled.Token);
        try
        {
            await WaitForFileAsync(fixture.Path("ready"));
            // root先行終了ではPIDの再取得を避け、元RootExitを観測する。
            if (exitBeforeChild) { Assert.AreEqual(0, await owned["Compatibility"].RootExit.WaitAsync(TimeSpan.FromSeconds(10))); }
            else { observed.Add(Process.GetProcessById(int.Parse(File.ReadAllText(fixture.Path("ready"))))); }
            observed.Add(Process.GetProcessById(int.Parse(File.ReadAllText(fixture.Path("child-ready")))));
            Assert.IsFalse(owned["Compatibility"].Completion.IsCompleted);
            VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, result.ExitCode);
            Assert.IsInstanceOfType<TimeoutException>(result.Commands.Single(command => command.Name == "Compatibility").PrimaryFailure);
            Assert.IsFalse(result.Modules.Single().Passed);
            using var summary = JsonDocument.Parse(File.ReadAllText(fixture.Path("run/summary.json")));
            Assert.AreEqual(5d, summary.RootElement.GetProperty("testTimeoutSeconds").GetDouble());
        }
        finally
        {
            canceled.Cancel();
            bool terminated = false;
            try
            {
                VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
                await ConfirmOwnedTerminationAsync(owned, observed, result);
                terminated = true;
            }
            finally
            {
                foreach (Process process in observed) { process.Dispose(); }
                if (terminated) { fixture.Dispose(); }
            }
        }
    }

    /// <summary>Fullは準備済候補を一回展開し、同じcatalog・installation・共有期限をReleaseの選択moduleへ渡して短IDと標準reportを集約します。</summary>
    /// <remarks>commandのI/Oだけをfixtureへ向けます。実publishと公開候補のFull成功を代用する検査ではありません。</remarks>
    [TestMethod]
    public async Task FullUsesPreparedArtifactsBeforeRunningEachSelectedModuleOnce()
    {
        string? originalDeadline = Environment.GetEnvironmentVariable(ProcessDeadline.SuiteDeadlineEnvironmentVariable);
        using var distribution = new DistributionFixture();
        (string Path, DistributionCatalog Catalog) prepared = await distribution.PrepareCatalogAsync();
        DistributionArtifact metadata = distribution.CreateMetadataVariant(prepared.Catalog.Current);
        File.WriteAllText(prepared.Path, JsonSerializer.Serialize(prepared.Catalog with { CurrentWithMetadata = metadata }));
        using var fixture = new RunnerFixture(distribution)
        {
            FormatInput = true,
            ScenarioIds = ["SC-01", "SC-02", "SC-05", "SC-09", "SC-10A", "SC-10B", "SC-11", "SC-12", "SC-13", "SC-14", "SC-ExistingData", "SC-CurrentUpdate"],
        };
        long firstModuleStart = 0;
        fixture.BeforeStart = (name, _) => { if (name == "Core") { firstModuleStart = Stopwatch.GetTimestamp(); } };
        VerificationRunResult result = await VerificationRunner.ExecuteAsync(VerificationPlan.Create("full", 1, distributionCatalog: prepared.Path),
            fixture.Run() with { TestTimeout = null });
        Assert.AreEqual(0, result.ExitCode, result.PrimaryFailure?.ToString());
        foreach ((string Name, ProcessStartRequest Request) observed in fixture.Requests)
        {
            Assert.IsTrue(observed.Request.Environment.ContainsKey(ProcessDeadline.SuiteDeadlineEnvironmentVariable));
            string? exported = observed.Request.Environment[ProcessDeadline.SuiteDeadlineEnvironmentVariable];
            if (observed.Name is "Scenarios" or "Desktop.E2E")
            {
                Assert.IsNotNull(exported);
                string[] parts = exported.Split(';');
                Assert.IsTrue(long.Parse(parts[0]["qpc:".Length..]) <= firstModuleStart,
                    "後発UI module開始時に共通test段階の起算点を作り直しません。");
                Assert.AreEqual(TimeSpan.FromSeconds(1380).Ticks, long.Parse(parts[1]["ticks:".Length..]));
                Assert.IsTrue(ProcessDeadline.Import(exported).Remaining > TimeSpan.Zero);
            }
            else { Assert.IsNull(exported); }
        }
        Assert.AreEqual(originalDeadline, Environment.GetEnvironmentVariable(ProcessDeadline.SuiteDeadlineEnvironmentVariable));
        CollectionAssert.AreEqual(new[] { "build", "style", "whitespace", "build", "layout", "layout", "layout", "Core", "Windows.Shell", "Windows.Remaining", "Windows.Settings", "Scenarios", "Desktop.E2E" }, fixture.Requests
            .Where(item => item.Name is "style" or "whitespace" or "build" or "layout" or "Core" or "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios" or "Desktop.E2E")
            .Select(item => item.Name).ToArray());
        ProcessStartRequest style = fixture.Requests.Single(item => item.Name == "style").Request;
        ProcessStartRequest whitespace = fixture.Requests.Single(item => item.Name == "whitespace").Request;
        Assert.AreEqual(fixture.Path("BeMusicSeeker.sln"), style.Arguments[2]);
        Assert.IsTrue(style.Arguments.Contains("--severity") && style.Arguments.Contains("error") && style.Arguments.Contains("--no-restore"));
        Assert.AreEqual(System.IO.Path.GetDirectoryName(fixture.Path("BeMusicSeeker.sln")), whitespace.Arguments[2]);
        Assert.IsTrue(whitespace.Arguments.Contains("--folder"));
        Assert.IsFalse(whitespace.Arguments.Contains("--no-restore"));
        Assert.IsFalse(style.Arguments.Contains("--include") || whitespace.Arguments.Contains("--include"));
        Assert.AreEqual(6, result.Modules.Count);
        string catalogPath = prepared.Path;
        string[] layoutCommands = fixture.Requests.Where(item => item.Name == "layout").Select(item => item.Request.Arguments[^1]).ToArray();
        string[] expectedPackages = [prepared.Catalog.Current.PackagePath, prepared.Catalog.Next.PackagePath, metadata.PackagePath];
        Assert.AreEqual(expectedPackages.Length, layoutCommands.Length);
        for (int index = 0; index < expectedPackages.Length; index++)
        {
            StringAssert.Contains(layoutCommands[index], "Assert-PortableReleasePackageLayout '" + expectedPackages[index].Replace("'", "''") + "'");
            StringAssert.Contains(layoutCommands[index], "portable-package-layout.ps1");
            Assert.IsFalse(layoutCommands[index].Contains("publish.ps1", StringComparison.Ordinal));
        }
        Assert.AreEqual("payload", File.ReadAllText(fixture.Path("run/application/payload.txt")));
        Assert.IsTrue(File.Exists(catalogPath));
        foreach ((string _, ProcessStartRequest request) in fixture.Requests.Where(item => item.Name is "Core" or "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios" or "Desktop.E2E"))
        {
            Assert.AreEqual(catalogPath, request.Environment[DistributionCatalog.EnvironmentVariable]);
            Assert.AreEqual(fixture.Path("run/application"), request.Environment[ScenarioApplicationPaths.EnvironmentVariable]);
            StringAssert.Contains(request.FileName, "Release");
        }
        using var summary = JsonDocument.Parse(File.ReadAllText(fixture.Path("run/summary.json")));
        Assert.IsTrue(summary.RootElement.GetProperty("passed").GetBoolean());
        Assert.AreEqual(catalogPath, summary.RootElement.GetProperty("distributionCatalog").GetString());
        JsonElement[] releases = summary.RootElement.GetProperty("releaseArtifacts").EnumerateArray().ToArray();
        Assert.AreEqual(2, releases.Length);
        JsonElement release = releases.Single(value => value.GetProperty("packagePath").GetString() == prepared.Catalog.Current.PackagePath);
        JsonElement bundled = releases.Single(value => value.GetProperty("packagePath").GetString() == metadata.PackagePath);
        Assert.AreEqual(metadata.Version, bundled.GetProperty("version").GetString());
        Assert.AreEqual(metadata.SizeBytes, bundled.GetProperty("sizeBytes").GetInt64());
        Assert.AreEqual(metadata.Sha256, bundled.GetProperty("sha256").GetString());
        Assert.AreEqual(prepared.Catalog.Current.PackagePath, release.GetProperty("packagePath").GetString());
        Assert.AreEqual(prepared.Catalog.Current.Sha256, release.GetProperty("sha256").GetString());
        Assert.AreEqual(prepared.Catalog.Current.SizeBytes, release.GetProperty("sizeBytes").GetInt64());
        Assert.AreEqual(prepared.Catalog.Current.Version, release.GetProperty("version").GetString());
        Assert.AreEqual(1380d, summary.RootElement.GetProperty("testTimeoutSeconds").GetDouble());
        Assert.IsTrue(summary.RootElement.GetProperty("testStageStartTimestamp").GetInt64() <= firstModuleStart);
        Assert.IsTrue(summary.RootElement.GetProperty("distributionMs").GetDouble() > 0);
        Assert.IsTrue(summary.RootElement.GetProperty("executionMs").GetDouble() > 0);
    }

    /// <summary>Fullの候補検査が失敗するとtestを起動せず、publishや再取得で補わず、summaryにも非成功を残します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidFullCandidateDoesNotStartAnyTestModule(bool missing)
    {
        using var distribution = new DistributionFixture();
        (string Path, DistributionCatalog Catalog) prepared = await distribution.PrepareCatalogAsync();
        if (missing) { File.Delete(prepared.Path); }
        else { File.AppendAllText(prepared.Catalog.Current.PackagePath, "changed"); }
        using var fixture = new RunnerFixture(distribution);
        VerificationRunResult result = await fixture.ExecuteAsync("full");
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsNotNull(result.PrimaryFailure);
        Assert.IsNull(result.SummaryFailure);
        Assert.IsFalse(fixture.Requests.Any(item => item.Request.FileName == "pwsh"));
        Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Core" or "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios"));
        Assert.IsFalse(Directory.Exists(fixture.Path("run/application")));
        using var summary = JsonDocument.Parse(File.ReadAllText(fixture.Path("run/summary.json")));
        Assert.IsFalse(summary.RootElement.GetProperty("passed").GetBoolean());
    }

    /// <summary>Nextのlayout検査が非成功なら、Fullは再publishせずinstallation展開・module開始を止め、合格summaryを作りません。</summary>
    /// <remarks>既存scriptの必須file判断はscript側の検査へ委ね、ここではその失敗がFullの成否へ接続されることをcommand境界で検査します。</remarks>
    [TestMethod]
    public async Task FailedNextLayoutStopsFullBeforeInstallationAndModules()
    {
        using var distribution = new DistributionFixture();
        (string Path, DistributionCatalog Catalog) prepared = await distribution.PrepareCatalogAsync();
        using var fixture = new RunnerFixture(distribution);
        int layouts = 0;
        fixture.Configure = (name, command) => name == "layout" && ++layouts == 2 ? command with { ExitCode = 17 } : command;
        VerificationRunResult result = await fixture.ExecuteAsync("full");
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsNotNull(result.PrimaryFailure);
        Assert.IsNull(result.SummaryFailure);
        ProcessStartRequest[] layoutRequests = fixture.Requests.Where(item => item.Name == "layout").Select(item => item.Request).ToArray();
        Assert.AreEqual(2, layoutRequests.Length);
        StringAssert.Contains(layoutRequests[1].Arguments[^1], "Assert-PortableReleasePackageLayout '" + prepared.Catalog.Next.PackagePath.Replace("'", "''") + "'");
        Assert.IsFalse(layoutRequests.Any(request => request.Arguments[^1].Contains("publish.ps1", StringComparison.Ordinal)));
        Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Core" or "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios" or "Desktop.E2E"));
        Assert.IsFalse(Directory.Exists(fixture.Path("run/application")));
        using var summary = JsonDocument.Parse(File.ReadAllText(fixture.Path("run/summary.json")));
        Assert.IsFalse(summary.RootElement.GetProperty("passed").GetBoolean());
    }

    /// <summary>
    /// 整形と同時に始めたbuildが整形前の内容で失敗しても、その結果を採らず、全style・whitespaceの変更を受け取る再buildで判定し、
    /// 同一pipeline内で準備からreport判定まで接続することを保証します。同時buildの失敗を成功へ数えたり、再buildを省いたりする誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task FormatterChangesReachTheBuildInTheActualPipeline()
    {
        int builds = 0;
        using var fixture = new RunnerFixture { FormatInput = true };
        // 1回目のbuildは整形の進行と無関係に必ず失敗させ、負荷による起動の遅れで判定が揺れないようにします。
        fixture.Configure = (name, command) => name == "build" && ++builds == 1 ? command with { ExitCode = 23 } : command;
        VerificationRunResult result = await fixture.ExecuteAsync("verify");
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("formatted", File.ReadAllText(fixture.Path("changed.cs")));
        CollectionAssert.AreEqual(new[] { "build", "style", "whitespace", "build" }, fixture.Requests
            .Where(item => item.Name is "style" or "whitespace" or "build").Select(item => item.Name).ToArray());
        VerificationCommandResult superseded = result.Commands.Single(command => command.Superseded);
        Assert.AreEqual(23, superseded.Completion?.ExitCode);
        Assert.IsFalse(superseded.Passed);
        Assert.IsTrue(superseded.PassedOrSuperseded);
        Assert.IsTrue(result.Commands.Where(command => !command.Superseded).All(command => command.Passed));
        Assert.AreEqual(5, result.Modules.Count);
    }

    /// <summary>
    /// 同時のbuildが整形の書込みより後に終わっても、再buildの開始時には整形したファイルの更新時刻がそのbuildの終了より新しいことを保証します。
    /// 更新時刻を進めずに再buildし、増分ビルドが整形前の内容から作った出力を最新と見なす誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task RebuildSeesFormattedSourcesAsNewerThanTheConcurrentBuild()
    {
        int builds = 0;
        DateTime? rebuildSourceTime = null;
        using var fixture = new RunnerFixture { FormatInput = true };
        string changed = fixture.Path("changed.cs");
        string ready = fixture.Path("build-ready");
        string release = fixture.Path("build-release");
        // 1回目のbuildは整形の完了まで保持し、整形の書込みがbuildの終了より前になる順序を作ります。
        fixture.Configure = (name, command) => name == "build" && ++builds == 1
            ? command with { Ready = ready, Release = release } : command;
        // whitespaceの後に始まる差分の空白検査の起動で整形の書込みの完了を知り、対象ファイルを読まずに順序を作ります。
        using var checkStarted = new ManualResetEventSlim();
        fixture.BeforeStart = (name, request) =>
        {
            if (name == "build" && File.Exists(release)) { rebuildSourceTime = File.GetLastWriteTimeUtc(changed); }
            if (name == "diff" && request.Arguments.Contains("--check")) { checkStarted.Set(); }
        };
        Task<VerificationRunResult> execution = fixture.ExecuteAsync("verify");
        DateTime released = default;
        try
        {
            await WaitForFileAsync(ready);
            Assert.IsTrue(await Task.Run(() => checkStarted.Wait(TimeSpan.FromSeconds(30))), "整形が完了しませんでした。");
            await Task.Delay(50);
            released = DateTime.UtcNow;
        }
        finally
        {
            File.WriteAllText(release, "");
        }
        VerificationRunResult result = await execution;
        Assert.AreEqual(0, result.ExitCode, result.PrimaryFailure?.ToString());
        Assert.AreEqual(2, builds);
        Assert.IsNotNull(rebuildSourceTime);
        Assert.IsTrue(File.GetLastWriteTimeUtc(changed) > released, "整形したファイルの更新時刻が、同時のbuildの解放より後へ進められていません。");
        Assert.IsTrue(rebuildSourceTime > released);
        Assert.IsTrue(result.Commands.Single(command => command.Superseded).Passed);
    }

    /// <summary>整形が内容を変えなければ、同時に始めたbuildの結果をそのまま採り、再buildしないことを保証します。</summary>
    [TestMethod]
    public async Task UnchangedFormatterKeepsTheConcurrentBuild()
    {
        using var fixture = new RunnerFixture { FormatInput = true, FormatterChangesContent = false };
        VerificationRunResult result = await fixture.ExecuteAsync("verify");
        Assert.AreEqual(0, result.ExitCode, result.PrimaryFailure?.ToString());
        CollectionAssert.AreEqual(new[] { "build", "style", "whitespace" }, fixture.Requests
            .Where(item => item.Name is "style" or "whitespace" or "build").Select(item => item.Name).ToArray());
        Assert.IsTrue(result.Commands.All(command => command.Passed && !command.Superseded));
    }

    /// <summary>
    /// 整形の失敗で、解放を待って実行中の同時のbuildを取り消して回収し、再buildやtestへ進まないことを保証します。
    /// 整形の失敗後も同時のbuildの終了を待ち続ける誤りや、取り消したbuildを成功として扱う誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task FailedFormatterCancelsTheConcurrentBuild()
    {
        using var fixture = new RunnerFixture { FormatInput = true, FormatterChangesContent = false };
        string ready = fixture.Path("build-ready");
        fixture.Configure = (name, command) => name switch
        {
            "build" => command with { Ready = ready, Release = fixture.Path("build-release") },
            "style" => command with { ExitCode = 17 },
            _ => command,
        };
        // buildが解放待ちに入ってから整形を失敗させ、取消だけがbuildを終わらせる状態を作ります。
        fixture.BeforeStart = (name, _) =>
        {
            if (name == "style") { WaitForFileAsync(ready).GetAwaiter().GetResult(); }
        };
        VerificationRunResult result = await fixture.ExecuteAsync("verify");
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsTrue(File.Exists(ready));
        Assert.IsFalse(File.Exists(fixture.Path("build-release")));
        Assert.AreEqual(1, fixture.Requests.Count(item => item.Name == "build"));
        Assert.IsFalse(fixture.Requests.Any(item => item.Name is "whitespace" or "Core" or "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios"));
        Assert.AreEqual(17, result.Commands.Single(command => command.Completion?.ExitCode == 17).Completion?.ExitCode);
        VerificationCommandResult canceled = result.Commands.Single(command => command.Superseded);
        Assert.IsFalse(canceled.Passed);
        // 準備期限の切れによる停止ではなく、整形の失敗による取消で終わったことを区別します。
        Assert.IsNotInstanceOfType<TimeoutException>(canceled.PrimaryFailure);
        Assert.IsTrue(canceled.ElapsedMs < TimeSpan.FromSeconds(30).TotalMilliseconds, $"同時のbuildが取消まで {canceled.ElapsedMs}ms かかりました。");
        Assert.IsNull(canceled.CleanupFailure);
        Assert.IsNull(canceled.OutputFailure);
    }

    /// <summary>restore・各整形・buildの非ゼロ終了で実pipelineが止まり、後段の起動で前段失敗を隠さないことを保証します。</summary>
    [TestMethod]
    [DataRow("restore", "style", "verify", true)]
    [DataRow("style", "whitespace", "verify", true)]
    [DataRow("whitespace", "Core", "verify", true)]
    [DataRow("build", "Core", "verify", true)]
    [DataRow("build", "Core", "verify", false)]
    [DataRow("style", "whitespace", "full", true)]
    public async Task FailedPreparationDoesNotStartTheNextStage(string fail, string next, string profile, bool formatterChangesContent)
    {
        using var fixture = new RunnerFixture
        {
            FormatInput = true,
            FormatterChangesContent = formatterChangesContent,
            Configure = (name, command) => name == fail ? command with { ExitCode = 17 } : command,
        };
        VerificationRunResult result = await fixture.ExecuteAsync(profile);
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsNotNull(result.PrimaryFailure);
        // 整形が内容を変えた場合の同時buildは置き換え対象で、成否の判定には最後の非置換commandだけを使います。
        Assert.AreEqual(17, result.Commands.Last(command => !command.Superseded && command.Completion?.ExitCode != 0).Completion?.ExitCode);
        Assert.IsFalse(fixture.Requests.Any(item => item.Name == next));
        // buildは整形と同時に一回だけ始まり、整形が内容を変えて同時buildを置き換える場合だけ再buildします。
        Assert.AreEqual(fail == "restore" ? 0 : fail == "build" && formatterChangesContent ? 2 : 1, fixture.Requests.Count(item => item.Name == "build"));
        Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Core" or "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios"));
    }

    /// <summary>
    /// 準備commandが実行期限内に非ゼロ終了しても、期限を跨ぐ停止処理の後に共有回収猶予で両ログとsummaryを保存し、元の失敗を保つことを保証します。
    /// 失敗確定時に回収期限を固定しない誤り、期限後にログを保存しない誤り、主失敗を期限超過へすり替える誤りを検出します。
    /// 終了の観測が実行期限内であることは、時間の余裕ではなく準備段階の直前に作った下限の期限の包含で確認し、外れた場合は前提不成立として失敗させます。
    /// 下限には直前の<c>dotnet --version</c>の起動とrestoreの起動・ready・releaseが含まれるため、それらが実行予算を超える極端な負荷では前提不成立になります。
    /// </summary>
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
        ProcessDeadline? lowerBound = null;
        fixture.Configure = (name, command) => name == "restore" ? command with
        {
            Ready = fixture.Path("restore-ready"),
            Release = fixture.Path("restore-release"),
            ExitCode = 17,
        } : command;
        fixture.BeforeStart = (name, _) =>
        {
            // 準備段階の構築直前の最後の起動で作るため、実際の実行期限はこれより後に始まり、この期限に含まれる終了は実行期限内の終了でもある。
            if (name == "--version") { lowerBound = ProcessDeadline.FromTimeout(executionBudget); }
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
        VerificationRun run = fixture.Run() with { PreparationTimeout = executionBudget };
        Task<VerificationRunResult> execution = VerificationRunner.ExecuteAsync(VerificationPlan.Parse(["dev"], 1), run);
        try
        {
            // 到達は局所期限なしで、通知か実行Taskの終端のどちらかまで待つ。
            await TestUiDispatcherHost.AwaitNotificationAsync(readyToExit.Task, execution, "準備processのready");
            ProcessDeadline boundary = observedExecutionDeadline ?? throw new InvalidOperationException("準備段階の起動を観測していません。");
            allowExit.TrySetResult();
            await TestUiDispatcherHost.AwaitNotificationAsync(terminated.Task, execution, "準備processの終了");
            Assert.IsTrue((lowerBound ?? throw new InvalidOperationException("準備段階の直前の起動を観測していません。")).Includes(failureObserved),
                "前提不成立: 準備processの終了が実行期限内に観測されなかったため、期限超過ではなく非ゼロ終了を主失敗とする検査になっていません。");
            // 回収猶予は失敗確定時に固定される。終了の観測は期限内なので、その後に実行期限を跨がせてから回収を許可する。
            // 時間経過自体を条件にする検証であり、順序はterminatedとallowCleanupのgateで確定する。
            while (boundary.Remaining > TimeSpan.Zero) { await Task.Delay(boundary.Remaining + TimeSpan.FromMilliseconds(10)); }
            allowCleanup.TrySetResult();
            VerificationRunResult result = await execution;
            // 回収猶予内の終端は、後続の表明の負荷を含めないよう実行の完了直後に測る。
            TimeSpan cleanupElapsed = Stopwatch.GetElapsedTime(failureObserved);
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
            Assert.IsTrue(cleanupElapsed < run.CleanupGrace);
            Assert.IsFalse(fixture.Requests.Any(item => item.Name is "build" or "Core" or "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios"));
        }
        finally
        {
            allowExit.TrySetResult();
            allowCleanup.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>B1では全体を計画順に逐次実行し、B8ではDailyを最初のUI枠で開始してからWindowsを計画順に枠へ入れ、各実行の全child終端だけで枠を解放します（root先行終了の検査はWindows.Shellで行います）。</summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(8)]
    public async Task UiExecutionsRespectBudgetAndStartDailyScenariosInTheFirstSlot(int budget)
    {
        using var fixture = new RunnerFixture();
        using var canceled = new CancellationTokenSource();
        string[] gated = ["Core", "Windows.Shell", "Windows.Remaining", "Windows.Settings"];
        var owned = new ConcurrentDictionary<string, OwnedProcess>();
        // DailyもWindowsと同じ方法で保持し、UI枠を占有している間に後続を開始しないことを観測する。
        fixture.Configure = (name, command) => gated.Contains(name) || name == "Scenarios" ? command with
        {
            Ready = fixture.Path(name + "-ready"),
            Release = fixture.Path(name + "-release"),
            ChildReady = fixture.Path(name + "-child-ready"),
            ChildRelease = fixture.Path(name + "-child-release"),
            ExitBeforeChild = name == "Windows.Shell",
        } : command;
        fixture.AfterStart = (name, process) => { if (gated.Contains(name)) { owned[name] = process; } };
        fixture.BeforeStart = (name, _) =>
        {
            if (name == "Scenarios")
            {
                // B1は計画順の逐次実行でWindows3実行の後、B8は同じUI枠の最初の要求としてWindowsより先に開始する。
                Assert.AreEqual(budget == 1 ? 3 : 0, owned.Count(item => item.Key.StartsWith("Windows.", StringComparison.Ordinal)));
                if (budget == 1) { Assert.IsTrue(owned.Values.All(process => process.Completion.IsCompletedSuccessfully)); }
            }
        };
        void Release(string name)
        {
            File.WriteAllText(fixture.Path(name + "-release"), "release");
            File.WriteAllText(fixture.Path(name + "-child-release"), "release");
        }
        Task<VerificationRunResult> execution = VerificationRunner.ExecuteAsync(VerificationPlan.Create("dev", budget),
            fixture.Run() with { TestTimeout = TimeSpan.FromSeconds(30) }, canceled.Token);
        try
        {
            if (budget == 1)
            {
                for (int index = 0; index < gated.Length; index++)
                {
                    string name = gated[index];
                    await WaitForFileAsync(fixture.Path(name + "-child-ready"));
                    CollectionAssert.AreEqual(gated.Take(index + 1).ToArray(), fixture.Requests.Where(item => gated.Contains(item.Name)).Select(item => item.Name).ToArray());
                    Assert.IsFalse(fixture.Requests.Any(item => item.Name == "Scenarios"));
                    Release(name);
                }
                await WaitForFileAsync(fixture.Path("Scenarios-child-ready"));
                Release("Scenarios");
            }
            else
            {
                // B8のUI枠は2。DailyとShellが先に入り、Dailyの全終端で空いた枠をRemainingが使う。
                foreach (string name in new[] { "Scenarios", "Core", "Windows.Shell" }) { await WaitForFileAsync(fixture.Path(name + "-child-ready")); }
                Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Windows.Remaining" or "Windows.Settings"), "DailyもUI枠を占有します。");
                Release("Scenarios");
                await WaitForFileAsync(fixture.Path("Windows.Remaining-child-ready"));
                Assert.IsFalse(owned["Core"].Completion.IsCompleted);
                Assert.IsFalse(fixture.Requests.Any(item => item.Name == "Windows.Settings"));
                File.WriteAllText(fixture.Path("Windows.Shell-release"), "release");
                await owned["Windows.Shell"].RootExit.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsFalse(fixture.Requests.Any(item => item.Name == "Windows.Settings"), "rootだけの終了でWindows枠を解放しません。");
                File.WriteAllText(fixture.Path("Windows.Shell-child-release"), "release");
                await WaitForFileAsync(fixture.Path("Windows.Settings-child-ready"));
                Assert.IsFalse(owned["Windows.Remaining"].Completion.IsCompleted);
                Assert.IsFalse(owned["Core"].Completion.IsCompleted);
                Release("Windows.Remaining");
                Release("Windows.Settings");
                Release("Core");
            }
            VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, result.ExitCode);
            Assert.AreEqual(5, result.Modules.Count);
            string[] uiStarts = fixture.Requests.Where(item => item.Name is "Scenarios" || item.Name.StartsWith("Windows.", StringComparison.Ordinal))
                .Select(item => item.Name).ToArray();
            CollectionAssert.AreEqual(budget == 1
                ? new[] { "Windows.Shell", "Windows.Remaining", "Windows.Settings", "Scenarios" }
                : new[] { "Scenarios", "Windows.Shell", "Windows.Remaining", "Windows.Settings" }, uiStarts);
            ProcessStartRequest[] windows = fixture.Requests.Where(item => item.Name.StartsWith("Windows.", StringComparison.Ordinal)).Select(item => item.Request).ToArray();
            Assert.AreEqual(1, windows.Select(request => request.FileName).Distinct().Count());
            Assert.AreEqual(3, windows.Select(request => request.Environment[TestRunDirectory.EnvironmentVariable]).Distinct().Count());
            Assert.AreEqual(3, windows.Select(request => request.Arguments[request.Arguments.ToList().IndexOf("--settings") + 1]).Distinct().Count());
            using var summary = JsonDocument.Parse(File.ReadAllText(fixture.Path("run/summary.json")));
            string caseRoot = summary.RootElement.GetProperty("caseRoot").GetString() ?? throw new AssertFailedException();
            Assert.AreEqual(Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory) ?? throw new AssertFailedException(), "bms-tests"), Path.GetDirectoryName(caseRoot));
            foreach ((string Name, ProcessStartRequest Request) observed in fixture.Requests.Where(item => item.Name.StartsWith("Windows.", StringComparison.Ordinal)))
            {
                Assert.AreEqual(Path.Combine(caseRoot, observed.Name), observed.Request.Environment[TestRunDirectory.EnvironmentVariable]);
            }
            long start = summary.RootElement.GetProperty("testStageStartTimestamp").GetInt64();
            Assert.IsTrue(start > 0 && start <= Stopwatch.GetTimestamp());
        }
        finally
        {
            foreach (string name in gated) { Release(name); }
            Release("Scenarios");
            canceled.Cancel();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>所有対象が全終端した非ゼロmoduleの失敗を保ちながら、他のmoduleの結果を回収することを保証します。</summary>
    [TestMethod]
    public async Task CompletedNonzeroModuleDoesNotPreventOtherModules()
    {
        using var fixture = new RunnerFixture { Configure = (name, command) => name == "Windows.Settings" ? command with { ExitCode = 7 } : command };
        VerificationRunResult result = await fixture.ExecuteAsync();
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual(5, result.Modules.Count);
        Assert.IsTrue(result.Modules.Where(module => module.Name != "Windows.Settings").All(module => module.Passed));
        VerificationCommandResult windows = result.Commands.Single(command => command.Name == "Windows.Settings");
        Assert.AreEqual(7, windows.Completion?.ExitCode);
        Assert.IsNotNull(windows.PrimaryFailure);
        Assert.IsNull(windows.CleanupFailure);
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
        foreach ((string name, ProcessStartRequest request) in fixture.Requests.Where(item => item.Name is "Core" or "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios"))
        {
            string[] arguments = request.Arguments.ToArray();
            string expected = plan.Modules.Single(module => module.ExecutionId == name).Filter ?? throw new InvalidOperationException();
            Assert.AreEqual(expected, arguments[Array.IndexOf(arguments, "--filter") + 1]);
        }
        Assert.AreEqual(5, result.Modules.Count);
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
        Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios"));
    }

    /// <summary>root非成功・ログ保存失敗・正常終了要求失敗が重なってもすべてを保持し、Job内の生存childを回収することを保証します。</summary>
    [TestMethod]
    public async Task PrimaryOutputAndCleanupFailuresAreAllRetained()
    {
        var fixture = new RunnerFixture();
        using var canceled = new CancellationTokenSource();
        var coreStarted = new TaskCompletionSource<OwnedProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new ConcurrentDictionary<string, OwnedProcess>();
        var observed = new List<Process>();
        fixture.AfterStart = (name, process) =>
        {
            if (name == "Core")
            {
                owned[name] = process;
                coreStarted.TrySetResult(process);
            }
        };
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
        Task<VerificationRunResult> execution = VerificationRunner.ExecuteAsync(VerificationPlan.Parse(["dev"], 1), run, canceled.Token);
        Process? child = null;
        try
        {
            OwnedProcess process = await coreStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForFileAsync(fixture.Path("child-ready"));
            child = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.Path("child-ready"))));
            observed.Add(child);
            Assert.AreEqual(7, await process.RootExit.WaitAsync(TimeSpan.FromSeconds(10)));
            // ここは三種類の失敗の併存を検査する。timeoutは専用caseへ分担し、主失敗の観測直後に回収を開始する。
            canceled.Cancel();
            VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            VerificationCommandResult core = result.Commands.Single(command => command.Name == "Core");
            Assert.AreEqual(1, result.ExitCode);
            Assert.AreEqual(7, core.Completion?.ExitCode);
            Assert.AreEqual(7, (await process.Completion.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
            Assert.AreEqual("stdout", await process.StandardOutput.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual("stderr", await process.StandardError.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsNotNull(core.PrimaryFailure);
            StringAssert.Contains(core.PrimaryFailure.ToString(), "7");
            Assert.IsNotNull(core.OutputFailure);
            Assert.IsNotNull(core.CleanupFailure);
            StringAssert.Contains(core.CleanupFailure.ToString(), "graceful-failure");
            Assert.AreEqual("stderr", File.ReadAllText(fixture.Path("run/Core/process.stderr.log")));
            Assert.IsTrue(child.HasExited);
            Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios"));
        }
        finally
        {
            canceled.Cancel();
            bool resourcesTerminated = false;
            try
            {
                VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
                await ConfirmOwnedTerminationAsync(owned, observed, result);
                resourcesTerminated = true;
            }
            finally
            {
                foreach (Process process in observed) { process.Dispose(); }
                if (resourcesTerminated) { fixture.Dispose(); }
            }
        }
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

    /// <summary>summaryの書込み・完成名への公開失敗を非成功とし、合格summaryを公開せず、moduleの主失敗も結果に残すことを保証します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task SummaryFailureNeverTurnsTheRunIntoSuccessOrLosesTheOriginal(bool failModule, bool failWrite)
    {
        using var fixture = new RunnerFixture
        {
            Configure = (name, command) => failModule && name == "Core" ? command with { ExitCode = 7 } : command,
        };
        Directory.CreateDirectory(fixture.Path(failWrite ? "run/summary.json.tmp" : "run/summary.json"));
        VerificationRunResult result = await fixture.ExecuteAsync();
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsNotNull(result.SummaryFailure);
        Assert.IsFalse(File.Exists(fixture.Path("run/summary.json")));
        Assert.AreEqual(!failModule, result.Modules.All(module => module.Passed));
        Assert.AreEqual(failModule, result.Commands.Single(command => command.Name == "Core").PrimaryFailure is not null);
    }

    /// <summary>
    /// 正常Coreの終了後に始めたUIの取消でも、回収期限が取消の時点から回収猶予を確保し、先行する正常終了で期限を消費しないことを保証します。
    /// 取消を伴わずに正常Coreの終了時点で回収期限を確定する誤りを、時間の経過ではなく取消直前の観測時刻と期限の包含で検出し、
    /// 停止への接続は、正常終了要求の入口でtokenが未取消であることで確認します。
    /// </summary>
    [TestMethod]
    public async Task SuccessfulCoreDoesNotStartTheLaterUiCleanupBudget()
    {
        using var fixture = new RunnerFixture();
        using var canceled = new CancellationTokenSource();
        var graceful = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? gracefulTokenCanceledAtEntry = null;
        fixture.Configure = (name, command) => name == "Windows.Settings" ? command with
        {
            Ready = fixture.Path("windows-ready"),
            Release = fixture.Path("windows-release"),
        } : command;
        VerificationRun run = fixture.Run() with
        {
            RequestGracefulExit = (process, token) =>
            {
                // 後続UIのprocessだけを対象にし、入口で回収tokenが未取消であることを記録してから正常終了させる。
                if (fixture.Names[process.ProcessId] != "Windows.Settings") { return Task.CompletedTask; }
                gracefulTokenCanceledAtEntry = token.IsCancellationRequested;
                File.WriteAllText(fixture.Path("windows-release"), "release");
                graceful.TrySetResult();
                return Task.CompletedTask;
            },
        };
        Task<VerificationRunResult> execution = VerificationRunner.ExecuteAsync(VerificationPlan.Parse(["dev"], 1), run, canceled.Token);
        try
        {
            await WaitForFileAsync(fixture.Path("windows-ready"));
            using var root = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.Path("windows-ready"))));
            // 取消の直前に観測する。回収期限は取消の時点以後に始まるため、この時刻から回収猶予までを含む。
            long beforeCancel = Stopwatch.GetTimestamp();
            canceled.Cancel();
            ProcessDeadline cutoff = fixture.FailureCleanup["Windows.Settings"]();
            long graceEnd = beforeCancel + (long)(run.CleanupGrace.TotalSeconds * Stopwatch.Frequency);
            Assert.IsTrue(cutoff.Includes(graceEnd), $"UI用の回収期限が取消の時点から回収猶予を確保していません（取消より前に確定した可能性があります。残り{cutoff.Remaining}）。");
            await TestUiDispatcherHost.AwaitNotificationAsync(graceful.Task, execution, "UIの正常終了要求");
            VerificationRunResult result = await execution;
            Assert.AreEqual(1, result.ExitCode);
            Assert.IsTrue(result.Modules.Single(module => module.Name == "Core").Passed);
            Assert.IsTrue(root.HasExited);
            Assert.AreEqual(false, gracefulTokenCanceledAtEntry, "正常終了要求の入口で、回収tokenが取消済みです。");
        }
        finally
        {
            canceled.Cancel();
            // 回収猶予（既定10秒）を使い切る停止でも元の失敗を隠さないよう、後始末の上限は猶予より長くする。
            await execution.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    /// <summary>二つの実Jobをready後に取消または期限超過させ、遅れて回収へ入ったprocessも同じcutoffを使い、全root・childが終端することを保証します。</summary>
    /// <remarks>後行のWindows.ShellのStartの返却は、先行のCoreが回収の猶予待ちへ入り、本体が両callbackの同一cutoffを確認した後（cutoff確定後）に行います。後行にも回収期限の残りの大半を使わせ、負荷で強制停止の時間が削られる組立てを避けます。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FatalStageFailureSharesOneCutoffAndTerminatesAllOwnedProcesses(bool timeout)
    {
        var fixture = new RunnerFixture();
        using var canceled = new CancellationTokenSource();
        var returnWindows = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGraceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new ConcurrentDictionary<string, OwnedProcess>();
        var gateFailures = new ConcurrentQueue<Exception>();
        fixture.Configure = (name, command) => name is "Core" or "Windows.Shell" ? command with
        {
            Ready = fixture.Path(name + "-ready"),
            Release = fixture.Path(name + "-release"),
            ChildReady = fixture.Path(name + "-child-ready"),
            ChildRelease = fixture.Path(name + "-child-release"),
        } : command;
        fixture.AfterStart = (name, process) =>
        {
            if (name is "Core" or "Windows.Shell") { owned[name] = process; }
            // start要求は実processを返す前で止める。先行回収が猶予待ちへ入ることが、後行回収の開始gateになる。
            // gateが開かなくても起動済みprocessはrunnerへ返して回収させ、失敗は本体で表明する。
            if (name == "Windows.Shell") { WaitStartGate(returnWindows.Task, gateFailures); }
        };
        VerificationRun run = fixture.Run() with
        {
            TestTimeout = TimeSpan.FromSeconds(timeout ? 20 : 60),
            CleanupGrace = TimeSpan.FromSeconds(10),
            RequestGracefulExit = async (process, token) =>
            {
                bool first = fixture.Names[process.ProcessId] == "Core";
                // runnerがcutoffを確定した後の先行回収の入口では開始だけを通知する。後行の開放は、本体がcutoffを読んだ後に行う。
                if (first) { firstGraceStarted.TrySetResult(); }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        // Start内の同期gateと解除側を分け、runner内のawaitが同期完了しても解除側へ到達する。
        Task<VerificationRunResult> execution = Task.Run(() => VerificationRunner.ExecuteAsync(VerificationPlan.Parse(["dev"], 2), run, canceled.Token));
        var observed = new List<Process>();
        try
        {
            foreach (string name in new[] { "Core", "Windows.Shell" })
            {
                foreach (string suffix in new[] { "-ready", "-child-ready" })
                {
                    int processId = int.Parse(await WaitForFileAsync(fixture.Path(name + suffix), TimeSpan.FromSeconds(30)));
                    if (suffix == "-ready")
                    {
                        Assert.AreEqual(owned[name].ProcessId, processId);
                    }
                    else
                    {
                        try { observed.Add(Process.GetProcessById(processId)); }
                        catch (ArgumentException)
                        {
                            // readyで出生済みのchildが回収済みでも、後段のJob・root・両pipe終端を必須にします。
                        }
                    }
                }
            }
            if (!timeout) { canceled.Cancel(); }
            await firstGraceStarted.Task.WaitAsync(TimeSpan.FromSeconds(45));
            // Abort済みの境界だけを読む。callbackを先に呼んで失敗時刻を作らない。後行の開放前に読み、後行が入口でcutoffを更新する誤りを検出する。
            ProcessDeadline cutoff = fixture.FailureCleanup["Core"]();
            Assert.AreSame(cutoff, fixture.FailureCleanup["Windows.Shell"](), "同じ段階に別の回収cutoffがあります。");
            // 後行にも回収期限の残りの大半を使わせるため、先行の猶予の終了を待たずに返却する。
            returnWindows.TrySetResult();
            VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsTrue(gateFailures.IsEmpty, "後行回収の開始gateが開かないまま観測期限を超えています。" + Environment.NewLine + string.Join(Environment.NewLine, gateFailures));
            Assert.AreEqual(1, result.ExitCode);
            Exception? primary = result.Commands.Single(command => command.Name == "Core").PrimaryFailure;
            if (timeout) { Assert.IsInstanceOfType<TimeoutException>(primary); }
            else { Assert.IsInstanceOfType<OperationCanceledException>(primary); }
            Assert.AreEqual(2, owned.Count);
            Assert.IsTrue(owned.Values.All(process => process.RootExit.IsCompletedSuccessfully
                && process.StandardOutput.IsCompletedSuccessfully && process.StandardError.IsCompletedSuccessfully
                && process.Completion.IsCompletedSuccessfully), "PID観測時の不在だけでなく、所有Job全体とroot・両pipeの実終端を確認します。");
            Assert.IsTrue(observed.All(process => process.HasExited));
            // UI枠1では最初のDailyが先に終わり、致命失敗後に後続のWindows実行を開始しない。
            Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Windows.Remaining" or "Windows.Settings"));
            Assert.AreEqual(2, result.Commands.Count(command => command.Name is "Core" or "Windows.Shell"));
            Assert.AreSame(cutoff, fixture.FailureCleanup["Windows.Shell"](), "後行回収がcutoffを更新しています。");
        }
        finally
        {
            canceled.Cancel();
            returnWindows.TrySetResult();
            bool resourcesTerminated = false;
            try
            {
                VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(30));
                await ConfirmOwnedTerminationAsync(owned, observed, result);
                resourcesTerminated = true;
            }
            finally
            {
                // 本体が先に失敗しても、記録したgateの失敗を診断として残す。成否は変えない。
                if (!gateFailures.IsEmpty) { Console.Error.WriteLine("後行回収の開始gateの失敗: " + string.Join(Environment.NewLine, gateFailures)); }
                foreach (Process process in observed) { process.Dispose(); }
                if (resourcesTerminated) { fixture.Dispose(); }
            }
        }
    }

    /// <summary>返却をcutoff後まで保留したprocessのログは書込み前に拒否し、既存stdoutと未作成stderrを保ち、summaryも非成功にします。</summary>
    /// <remarks>生存Jobの行では、期限後に全対象が停止しても回収失敗を消さないことを検査します。途中のファイル書込み取消は静的確認へ分担します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExpiredCleanupRejectsOutputAndRetainsLateProcessFailure(bool keepWindowsAlive)
    {
        var fixture = new RunnerFixture();
        using var canceled = new CancellationTokenSource();
        var returnWindows = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new ConcurrentDictionary<string, OwnedProcess>();
        var observed = new List<Process>();
        var gateFailures = new ConcurrentQueue<Exception>();
        fixture.Configure = (name, command) => name == "Core" || (name == "Windows.Shell" && keepWindowsAlive) ? command with
        {
            Ready = fixture.Path(name + "-ready"),
            Release = fixture.Path(name + "-release"),
            ChildReady = fixture.Path(name + "-child-ready"),
            ChildRelease = fixture.Path(name + "-child-release"),
        } : command;
        fixture.AfterStart = (name, process) =>
        {
            if (name is not ("Core" or "Windows.Shell")) { return; }
            owned[name] = process;
            // gateの待機は本体の観測と同じ長さの観測期限にし、本体のfinallyが必ず開放する。開かなくても起動済みprocessはrunnerへ返して回収させ、失敗は本体で表明する。
            if (name == "Windows.Shell") { WaitStartGate(returnWindows.Task, gateFailures); }
        };
        VerificationRun run = fixture.Run() with
        {
            TestTimeout = TimeSpan.FromSeconds(20),
            CleanupGrace = TimeSpan.FromSeconds(1),
            RequestGracefulExit = (process, _) =>
            {
                if (fixture.Names[process.ProcessId] == "Core") { firstStop.TrySetResult(); }
                return Task.CompletedTask;
            },
        };
        string rootDirectory = fixture.Path("");
        // 同期Startの保留中も、test側はcutoffの観測とgate解除を進められる。
        Task<VerificationRunResult> execution = Task.Run(() => VerificationRunner.ExecuteAsync(VerificationPlan.Parse(["dev"], 2), run, canceled.Token));
        try
        {
            await WaitForFileAsync(fixture.Path("Core-child-ready"));
            // WindowsのStart callbackに到達したことを、実所有対象で確認する。
            ProcessDeadline readyDeadline = Deadline();
            while (!owned.ContainsKey("Windows.Shell"))
            {
                if (readyDeadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("Windowsが起動しません。"); }
                await Task.Delay(10);
            }
            foreach (string name in keepWindowsAlive ? new[] { "Core", "Windows.Shell" } : new[] { "Core" })
            {
                foreach (string suffix in new[] { "-ready", "-child-ready" })
                {
                    await WaitForFileAsync(fixture.Path(name + suffix));
                    observed.Add(Process.GetProcessById(int.Parse(File.ReadAllText(fixture.Path(name + suffix)))));
                }
            }
            if (!keepWindowsAlive) { await owned["Windows.Shell"].Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
            string stdout = fixture.Path("run/Windows.Shell/process.stdout.log");
            string stderr = fixture.Path("run/Windows.Shell/process.stderr.log");
            File.WriteAllText(stdout, "sentinel");
            Assert.IsFalse(File.Exists(stderr));
            canceled.Cancel();
            await firstStop.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ProcessDeadline cutoff = fixture.FailureCleanup["Core"]();
            // タイマーの配送だけで失効とせず、Windowsの返却を元cutoffの実失効まで保留する。
            while (cutoff.Remaining > TimeSpan.Zero)
            {
                if (readyDeadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("回収cutoffの観測期限を超えています。"); }
                await Task.Delay(1).WaitAsync(readyDeadline.Remaining);
            }
            Assert.AreEqual(TimeSpan.Zero, cutoff.Remaining);
            Assert.AreEqual(TimeSpan.Zero, fixture.FailureCleanup["Windows.Shell"]().Remaining);
            if (keepWindowsAlive)
            {
                Assert.IsFalse(owned["Windows.Shell"].Completion.IsCompleted);
                Assert.IsTrue(observed.Skip(2).All(process => !process.HasExited));
            }
            returnWindows.TrySetResult();
            VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            VerificationCommandResult windows = result.Commands.Single(command => command.Name == "Windows.Shell");
            Assert.IsTrue(gateFailures.IsEmpty, "Windowsの返却gateが開かないまま観測期限を超えています。" + Environment.NewLine + string.Join(Environment.NewLine, gateFailures));
            Assert.AreEqual(1, result.ExitCode);
            // 生存Jobの行はpipeが回収期限内に終端する保証がないため、保存拒否は完了済みの行で確認する。
            if (!keepWindowsAlive) { Assert.IsNotNull(windows.OutputFailure); }
            Assert.IsNotNull(result.SummaryFailure);
            Assert.IsFalse(File.Exists(fixture.Path("run/summary.json")));
            if (keepWindowsAlive) { Assert.IsNotNull(windows.CleanupFailure); }
            Assert.AreEqual("sentinel", File.ReadAllText(stdout));
            Assert.IsFalse(File.Exists(stderr));
            // UI枠1では最初のDailyが先に終わり、致命失敗後に後続のWindows実行を開始しない。
            Assert.IsFalse(fixture.Requests.Any(item => item.Name is "Windows.Remaining" or "Windows.Settings"));
            Assert.AreEqual(2, result.Commands.Count(command => command.Name is "Core" or "Windows.Shell"));
        }
        finally
        {
            canceled.Cancel();
            returnWindows.TrySetResult();
            bool resourcesTerminated = false;
            try
            {
                VerificationRunResult result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
                await ConfirmOwnedTerminationAsync(owned, observed, result);
                resourcesTerminated = true;
            }
            finally
            {
                if (!gateFailures.IsEmpty) { Console.Error.WriteLine("Windowsの返却gateの失敗: " + string.Join(Environment.NewLine, gateFailures)); }
                foreach (Process process in observed) { process.Dispose(); }
                // 未終端の所有対象がある場合は領域を残し、削除で失敗を覆い隠さない。
                if (resourcesTerminated) { fixture.Dispose(); }
            }
        }
        Assert.IsFalse(Directory.Exists(rootDirectory));
    }

    /// <summary>領域削除前に元Taskと保持PIDを独立に観測し、終端時の故障もcommandの失敗と照合します。</summary>
    /// <remarks>全対象を一つの共有期限（60秒）で並行に待ち、未終端の対象が複数あっても失敗時の待ちを期限一回分に抑えます。</remarks>
    private static async Task ConfirmOwnedTerminationAsync(IEnumerable<KeyValuePair<string, OwnedProcess>> owned,
        IEnumerable<Process> observed, VerificationRunResult result)
    {
        using var stopWatchdog = new CancellationTokenSource();
        var watchdog = Task.Delay(ObservationTimeout, stopWatchdog.Token);
        var targets = new List<(string Name, string Part, Task Task)>();
        foreach ((string name, OwnedProcess process) in owned)
        {
            foreach ((string part, Task task) in new[] { ("RootExit", (Task)process.RootExit),
                ("stdout", process.StandardOutput), ("stderr", process.StandardError), ("Completion", process.Completion) })
            {
                targets.Add((name, part, task));
            }
        }
        // 一つのobserverがfaultしても、他の元Taskの終端確認を省略しない。全observerを同じ共有期限で並行に待つ。
        Task<(bool Terminated, Exception? Failure, Exception? WatchdogFailure)>[] taskObservers =
            [.. targets.Select(target => ObserveTerminationAsync(target.Task, target.Name + "/" + target.Part, watchdog))];
        // Jobのobserverと別に保持した全root/childの実終了を確認する。
        Process[] processes = [.. observed];
        Task<(bool Terminated, Exception? Failure, Exception? WatchdogFailure)>[] exitObservers =
            [.. processes.Select(process => ObserveTerminationAsync(process.WaitForExitAsync(), "PID " + process.Id, watchdog))];
        await Task.WhenAll(taskObservers.Concat(exitObservers));
        stopWatchdog.Cancel();
        var observations = new List<(string Name, string Part, bool Terminated, TaskStatus Status, Exception? Failure, Exception? WatchdogFailure)>();
        for (int index = 0; index < targets.Count; index++)
        {
            (bool Terminated, Exception? Failure, Exception? WatchdogFailure) observation = taskObservers[index].Result;
            observations.Add((targets[index].Name, targets[index].Part, observation.Terminated, targets[index].Task.Status, observation.Failure, observation.WatchdogFailure));
        }
        var exits = new List<bool>();
        for (int index = 0; index < processes.Length; index++)
        {
            (bool Terminated, Exception? Failure, Exception? WatchdogFailure) observation = exitObservers[index].Result;
            exits.Add(observation.Terminated && observation.Failure is null && observation.WatchdogFailure is null && processes[index].HasExited);
        }
        bool resourcesTerminated = observations.All(item => item.Terminated && item.WatchdogFailure is null) && exits.All(exited => exited);
        Assert.IsTrue(resourcesTerminated, "元Taskまたは保持したroot/childが未終端です。診断を確認してください。");
        foreach ((string Name, string Part, bool Terminated, TaskStatus Status, Exception? Failure, Exception? WatchdogFailure) observation in observations)
        {
            if (observation.Failure is null)
            {
                Assert.AreEqual(TaskStatus.RanToCompletion, observation.Status);
            }
            else
            {
                Assert.IsTrue(observation.Status is TaskStatus.Faulted or TaskStatus.Canceled);
                VerificationCommandResult command = result.Commands.Single(item => item.Name == observation.Name);
                Assert.IsFalse(command.Passed, observation.Name + "/" + observation.Part);
                Assert.IsNotNull(command.CleanupFailure, observation.Failure.ToString());
            }
        }
    }

    /// <summary>負例の元Taskのfault・取消を保持して終端を観測し、呼出し側が共有するwatchdogによる未終端とは区別します。</summary>
    private static async Task<(bool Terminated, Exception? Failure, Exception? WatchdogFailure)> ObserveTerminationAsync(Task task, string name, Task watchdog)
    {
        Task first = await Task.WhenAny(task, watchdog);
        Exception? watchdogFailure = first == watchdog ? new TimeoutException(name + "の終端観測期限を超えています。") : null;
        bool terminated = task.IsCompleted;
        Exception? failure = null;
        if (terminated)
        {
            // 元Task自身のTimeoutExceptionも元故障として扱い、観測用watchdogとは混同しない。
            try { await task; }
            catch (Exception originalFailure) { failure = originalFailure; }
        }
        if (failure is not null || watchdogFailure is not null)
        {
            Console.Error.WriteLine($"{name}: terminated={terminated}, status={task.Status}, failure={failure}, watchdogFailure={watchdogFailure}");
        }
        return (terminated, failure, watchdogFailure);
    }

    /// <summary>起動済みprocessを返す前の同期gateを観測期限（60秒）まで待ち、期限超過は例外にせず記録します。</summary>
    /// <remarks>Startの途中で例外にすると起動済みprocessをrunnerが所有できず回収されないため、gateの失敗は本体の表明へ移します。</remarks>
    private static void WaitStartGate(Task gate, ConcurrentQueue<Exception> failures)
    {
        try { gate.WaitAsync(Deadline().Remaining).GetAwaiter().GetResult(); }
        catch (TimeoutException error) { failures.Enqueue(error); }
    }

    // 実OSの起動・終端観測の期限です。Deadline()と終端観測の共有watchdogが同じ値を使い、検査対象の明示期限とは分けます。
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(60);

    private static ProcessDeadline Deadline() => ProcessDeadline.FromTimeout(ObservationTimeout);

    private static async Task<string> WaitForFileAsync(string path, TimeSpan? timeout = null)
    {
        ProcessDeadline deadline = timeout is { } budget ? ProcessDeadline.FromTimeout(budget) : Deadline();
        while (true)
        {
            if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("fixtureのgateが開きません: " + path); }
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return File.ReadAllText(path);
                }
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                // readyの公開後も読取りが共有違反になる間は、同じ観測期限内で待ちます。
            }
            await Task.Delay(10);
        }
    }

    private sealed class RunnerFixture(DistributionFixture? distribution = null) : IDisposable
    {
        private readonly TestWorkspace? workspace = distribution is null ? TestWorkspace.Create(TestRunDirectory.RootDirectory) : null;
        private int commandIndex;
        internal bool FormatInput { get; init; }
        /// <summary>偽のformatterが対象の内容を変えるかです。変える場合だけ、偽のbuildが整形後の内容を要求します。</summary>
        internal bool FormatterChangesContent { get; init; } = true;
        internal string[] ScenarioIds { get; init; } = ["SC-01", "SC-02", "SC-09"];
        internal string[] CompatibilityNames { get; init; } = ["report fixture"];
        internal string[] PerformanceNames { get; init; } = ["report fixture"];
        internal Func<string, Command, Command>? Configure { get; set; }
        internal Action<string, ProcessStartRequest>? BeforeStart { get; set; }
        internal Action<string, OwnedProcess>? AfterStart { get; set; }
        internal ConcurrentQueue<(string Name, ProcessStartRequest Request)> Requests { get; } = new();
        internal ConcurrentDictionary<int, string> Names { get; } = new();
        /// <summary>失敗確定後に同段階の絶対cutoffを観測する、起動時に受け取った境界です。</summary>
        internal ConcurrentDictionary<string, Func<ProcessDeadline>> FailureCleanup { get; } = new();
        private string Root => distribution?.Root ?? workspace?.RootDirectory ?? throw new InvalidOperationException();
        internal string Path(string relative) => distribution?.PathFor(relative) ?? workspace?.GetPath(relative) ?? throw new InvalidOperationException();
        internal VerificationRun Run()
        {
            File.WriteAllText(Path("changed.cs"), "original");
            Directory.CreateDirectory(Path("run"));
            return new VerificationRun(Root, Path("run"))
            {
                StartProcess = Start,
                // 非期限保証の検査では、実processの起動・回収に余裕を確保します。
                MetadataTimeout = TimeSpan.FromSeconds(60),
                PreparationTimeout = TimeSpan.FromSeconds(60),
                FormatTimeout = TimeSpan.FromSeconds(60),
                TestTimeout = TimeSpan.FromSeconds(60),
                DistributionTimeout = TimeSpan.FromSeconds(60),
                CleanupGrace = TimeSpan.FromSeconds(10),
            };
        }
        internal Task<VerificationRunResult> ExecuteAsync(string profile = "dev") => VerificationRunner.ExecuteAsync(VerificationPlan.Parse(profile == "full" ? [profile, "--distribution-catalog", Path("candidate/catalog.json")] : [profile], 1), Run());
        private OwnedProcess Start(ProcessStartRequest request, Func<ProcessDeadline> beginFailureCleanup)
        {
            string name = request.FileName == "pwsh" ? "layout" : request.FileName is "dotnet" or "git" ? request.Arguments[0] :
                System.IO.Path.GetFileName(request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1]);
            if (name == "format") { name = request.Arguments[1]; }
            Requests.Enqueue((name, request));
            FailureCleanup[name] = beginFailureCleanup;
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
            if (FormatterChangesContent)
            {
                if (name == "style") { command = command with { WritePath = Path("changed.cs"), WriteContent = "style" }; }
                if (name == "whitespace") { command = command with { RequirePath = Path("changed.cs"), RequireContent = "style", WritePath = Path("changed.cs"), WriteContent = "formatted" }; }
                // 整形と同時のbuildは書込み中のファイルを読まず、整形後に始まる再buildだけが整形後の内容を要求します。
                if (name == "build" && FormatInput && Requests.Any(item => item.Name == "whitespace"))
                {
                    command = command with { RequirePath = Path("changed.cs"), RequireContent = "formatted" };
                }
            }
            if (name is "Core" or "Windows.Settings" or "Windows.Shell" or "Windows.Remaining" or "Scenarios" or "Desktop.E2E" or "Compatibility" or "Performance")
            {
                string[] arguments = request.Arguments.ToArray();
                string reportDirectory = arguments[Array.IndexOf(arguments, "--results-directory") + 1];
                string report = Path(name + "-report-fixture.xml");
                IEnumerable<string> names = name switch
                {
                    "Scenarios" => ScenarioIds.Select(id => $"[{id}] report fixture"),
                    "Desktop.E2E" => new[] { "[DT-01] search", "[DT-02] drop", "[DT-02] schema", "[DT-03] virtual row", "[SC-16] normal startup", "[SC-15A] update", "[SC-15C] update restart failure" },
                    "Compatibility" => CompatibilityNames,
                    "Performance" => PerformanceNames,
                    _ => ["report fixture"],
                };
                WriteReport(report, names);
                command = command with { ReportSource = report, ReportDestination = System.IO.Path.Combine(reportDirectory, "results.trx") };
            }
            command = Configure?.Invoke(name, command) ?? command;
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
