using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Windows.Tests.Runtime;

/// <summary>別プロセス間の配置・排他・一時領域の所有と、実製品入口での入力拒否を検査します。</summary>
[TestClass]
public sealed class RuntimeIsolationProcessTests
{
    private static string ProbePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "windows-process-probe", "WindowsProcessProbe.exe");
    private static string ApplicationPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "application", "BeMusicSeeker.exe");

    /// <summary>通常全体排他と共通profile排他を別プロセスで競合させ、途中取得の解放・パス正規化・強制停止後の再取得を保証します。</summary>
    /// <remarks>通常mutexはケース専有名を使います。製品の固定GUIDとの接続は静的レビューへ分担します。</remarks>
    [TestMethod]
    public Task NormalAndTestProcessesRespectGlobalAndCanonicalProfileLeases()
        => RunCaseAsync(async test =>
        {
            string profileA = test.Workspace.GetPath("Profile A");
            string profileB = test.Workspace.GetPath("Profile B");
            string normalMutex = "Local\\BeMusicSeeker.RuntimeTests." + Guid.NewGuid().ToString("N");

            (OwnedProcess Process, string Ready, string Release) normalA = test.StartProfile(profileA, false, normalMutex);
            await AssertAcquiredAsync(normalA, true, test.Deadline);
            await CheckProfileAsync(test, profileB, false, normalMutex, false);
            await CheckProfileAsync(test, profileB, true, normalMutex, true);
            foreach (string alias in new[] { profileA, profileA.ToUpperInvariant(), profileA + Path.DirectorySeparatorChar,
                Path.Combine(profileA, "unused", "..") })
            {
                await CheckProfileAsync(test, alias, true, normalMutex, false);
            }
            await ReleaseProfileAsync(normalA, true, test.Deadline);

            (OwnedProcess Process, string Ready, string Release) testA = test.StartProfile(profileA, true, normalMutex);
            await AssertAcquiredAsync(testA, true, test.Deadline);
            await CheckProfileAsync(test, profileA, false, normalMutex, false);
            // 直前の通常Aがglobalを取ったままprofile取得に失敗しても、通常Bを妨げません。
            await CheckProfileAsync(test, profileB, false, normalMutex, true);
            await CheckProfileAsync(test, profileB, true, normalMutex, true);

            ProcessStopResult stopped = await testA.Process.StopAsync(null, test.Deadline);
            Assert.IsNull(stopped.CleanupFailure);
            Assert.AreNotEqual(0, (await testA.Process.WaitForExitAsync(test.Deadline)).ExitCode,
                "releaseを作らずholderを強制停止します。");
            await CheckProfileAsync(test, profileA, true, normalMutex, true);
        });

    /// <summary>別processの生存leaseが実leafと内容を保護し、所有holder強制停止後だけ回収され、別scopeの残骸は保持されることを保証します。</summary>
    /// <remarks>未知・旧markerと不明所有の条件網羅はTempDirectoryPublisherTestsへ分担します。</remarks>
    [TestMethod]
    public Task TemporaryCleanerPreservesLiveOwnerAndOtherScopeThenRemovesStoppedOwner()
        => RunCaseAsync(async test =>
        {
            string scope = test.Workspace.GetPath("temporary");
            string otherScope = test.Workspace.GetPath("other-temporary");
            string ready = test.Workspace.GetPath("temp-ready.json");
            OwnedProcess holder = test.StartProbe("hold-temp", scope, ready, test.Workspace.GetPath("temp-release"));
            using JsonDocument live = await ReadReadyAsync(holder, ready, test.Deadline);
            string leaf = RequiredString(live.RootElement, "LeafPath");
            Assert.IsTrue(leaf.StartsWith(scope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            string ownedFile = Path.Combine(leaf, "owned.txt");
            string ownedContent = File.ReadAllText(ownedFile);
            Assert.AreEqual("live temporary payload", ownedContent);

            // 別scopeも実所有者が作った既知sessionにし、誤った広域回収を検出できる残骸を用意します。
            string otherReady = test.Workspace.GetPath("other-ready.json");
            OwnedProcess other = test.StartProbe("hold-temp", otherScope, otherReady, test.Workspace.GetPath("other-release"));
            using JsonDocument otherLive = await ReadReadyAsync(other, otherReady, test.Deadline);
            string otherLeaf = RequiredString(otherLive.RootElement, "LeafPath");
            Assert.IsTrue(otherLeaf.StartsWith(otherScope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.IsNull((await other.StopAsync(null, test.Deadline)).CleanupFailure);
            Assert.AreNotEqual(0, (await other.WaitForExitAsync(test.Deadline)).ExitCode);

            await CleanTemporaryAsync(test, scope);
            Assert.IsFalse(holder.Completion.IsCompleted, "生存holderをcleanerが終わらせません。");
            Assert.AreEqual(ownedContent, File.ReadAllText(ownedFile));
            Assert.AreEqual("live temporary payload", File.ReadAllText(Path.Combine(otherLeaf, "owned.txt")));

            Assert.IsNull((await holder.StopAsync(null, test.Deadline)).CleanupFailure);
            Assert.AreNotEqual(0, (await holder.WaitForExitAsync(test.Deadline)).ExitCode);
            Assert.IsTrue(Directory.Exists(leaf), "停止だけではholderのfinallyを実行せず、残骸を残します。");
            await CleanTemporaryAsync(test, scope);
            Assert.IsFalse(Directory.Exists(leaf));
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(leaf)), "leafを含む所有sessionを回収します。");
            Assert.AreEqual("live temporary payload", File.ReadAllText(Path.Combine(otherLeaf, "owned.txt")));
        });

    /// <summary>fresh processでargv・配置・子環境を保ち、明示Assetsの7z解決とログ4getterへの接続を確認して、親CWD・環境・ログ条件の不変を保証します。</summary>
    /// <remarks>不正ログ値は両行ともWarnとし、空白だけは警告対象にせず、非空の不正値は元文字列と警告条件を保持します。
    /// 配置・ログ解析の条件網羅はParserTests・OptionsTests・ApplicationPathPolicyTests、実nativeの展開はInstallの回帰テストへ分担します。</remarks>
    [TestMethod]
    [DataRow("sqlite_net", "raw", "SQLITE_NET", false, true, false, "  ", false)]
    [DataRow("raw", "sqlite_net", "raw", true, false, true, "unknown Raw", true)]
    public Task RuntimeCaptureKeepsArgumentsProfileAndChildOnlyEnvironment(string songMode, string maintenanceMode,
        string hydrationMode, bool rawSong, bool rawMaintenance, bool rawHydration, string logLevelValue, bool hasInvalidLogLevelValue)
        => RunCaseAsync(async test =>
        {
            string profile = test.Workspace.GetPath("専有 profile");
            string assets = test.Workspace.GetPath("専有 assets");
            string nativeDirectory = Path.Combine(assets, "libs", "x64");
            Directory.CreateDirectory(nativeDirectory);
            string expectedSevenZipPath = Path.Combine(nativeDirectory, "7z.dll");
            // resolverだけを動かし、誤ってnativeをロードすれば成功できない専有入力を使います。
            byte[] sevenZipSentinel = "runtime resolver sentinel: not a native library"u8.ToArray();
            File.WriteAllBytes(expectedSevenZipPath, sevenZipSentinel);
            string manifest = test.Workspace.GetPath("session 日本語.json");
            JsonObject session = CreateManifest(profile);
            session["assetsDirectory"] = assets;
            File.WriteAllText(manifest, session.ToJsonString());
            string relativeManifest = Path.Combine("inputs", "..", Path.GetFileName(manifest));
            Assert.IsFalse(Path.IsPathFullyQualified(relativeManifest));
            string[] arguments = ["日本語 🙂 with space", "quote\"and\\slash", "", "trailing \\",
                "--test-session", relativeManifest, "--update-manifest-url=https://example.invalid/capture.json", "--log-level=" + logLevelValue];
            string[] environmentNames = ["BMS_SONG_TABLE_LOAD_MODE", "BMS_MAINTENANCE_TABLE_LOAD_MODE", "BMS_CHART_INFO_HYDRATION_LOAD_MODE"];
            string?[] before = environmentNames.Select(Environment.GetEnvironmentVariable).ToArray();
            string cwd = Environment.CurrentDirectory;
            (NormalLogLevel Level, bool InfoEnabled, bool HasInvalid, string InvalidValue) loggingBefore =
                (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
                    CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue);
            var environment = new Dictionary<string, string?>
            {
                [environmentNames[0]] = songMode,
                [environmentNames[1]] = maintenanceMode,
                [environmentNames[2]] = hydrationMode
            };
            OwnedProcess process = test.StartProbe(["capture-runtime", ApplicationPath, .. arguments], environment);
            ProcessCompletion result = await process.WaitForExitAsync(test.Deadline);
            AssertSuccess(result);
            using var capture = JsonDocument.Parse(result.StandardOutput);
            JsonElement value = capture.RootElement;
            Assert.AreEqual(ApplicationPath, RequiredString(value, "ExecutablePath"));
            Assert.AreEqual(assets, RequiredString(value, "AssetsDirectory"));
            string actualSevenZipPath = RequiredString(value, "BundledSevenZipLibraryPath");
            Assert.AreEqual(expectedSevenZipPath, actualSevenZipPath);
            Assert.IsTrue(File.Exists(actualSevenZipPath));
            CollectionAssert.AreEqual(sevenZipSentinel, File.ReadAllBytes(actualSevenZipPath));
            Assert.AreEqual(profile, RequiredString(value, "ProfileDirectory"));
            Assert.AreEqual(Path.Combine(profile, "config", "user.config"), RequiredString(value, "UserConfigPath"));
            Assert.AreEqual(Path.Combine(profile, "data"), RequiredString(value, "DataDirectoryPath"));
            Assert.AreEqual(Path.Combine(profile, "lang"), RequiredString(value, "LanguageDirectory"));
            Assert.AreEqual(Path.Combine(profile, "log"), RequiredString(value, "LogDirectory"));
            Assert.AreEqual(Path.Combine(profile, "temp"), RequiredString(value, "TempDirectory"));
            Assert.AreEqual(Path.Combine(profile, "legacy"), RequiredString(value, "LegacySettingsDirectory"));
            Assert.AreEqual("https://example.invalid/capture.json", RequiredString(value, "UpdateManifestUrl"));
            Assert.AreEqual(rawSong, value.GetProperty("UseRawSongCatalog").GetBoolean());
            Assert.AreEqual(rawMaintenance, value.GetProperty("UseRawMaintenanceTable").GetBoolean());
            Assert.AreEqual(rawHydration, value.GetProperty("UseRawChartInfoHydration").GetBoolean());
            Assert.AreEqual(NormalLogLevel.Warn, (NormalLogLevel)value.GetProperty("LogLevel").GetInt32());
            Assert.IsFalse(value.GetProperty("IsInfoLoggingEnabled").GetBoolean());
            Assert.AreEqual(hasInvalidLogLevelValue, value.GetProperty("HasInvalidLogLevelValue").GetBoolean());
            Assert.AreEqual(logLevelValue, RequiredString(value, "InvalidLogLevelValue"));
            string[] expectedArguments = [.. arguments];
            expectedArguments[5] = manifest;
            CollectionAssert.AreEqual(expectedArguments,
                value.GetProperty("RestartArguments").EnumerateArray().Select(item => item.GetString()).ToArray());
            Assert.AreEqual(relativeManifest, arguments[5]);
            Assert.IsFalse(Directory.Exists(profile), "捕捉だけではprofileを作成しません。");
            Assert.AreEqual(cwd, Environment.CurrentDirectory);
            CollectionAssert.AreEqual(before, environmentNames.Select(Environment.GetEnvironmentVariable).ToArray());
            Assert.AreEqual(loggingBefore, (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
                CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue));
        });

    /// <summary>不正schemaと未知providerを実製品のMainへ渡し、理由付きの非zero終端とprofile未作成・通常配置の不変を保証します。</summary>
    /// <remarks>有効manifestによる実アプリの起動成功はE2Eへ分担し、probeの成功で代用しません。</remarks>
    [TestMethod]
    [DataRow("schema", "schemaVersion")]
    [DataRow("provider", "fileEnumeration")]
    public Task ProductEntryPointRejectsInvalidSessionWithoutWritingProfileOrNormalLayout(string invalidField, string reason)
        => RunCaseAsync(async test =>
        {
            string profile = test.Workspace.GetPath("unvalidated-profile");
            JsonObject document = CreateManifest(profile);
            if (invalidField == "schema") { document["schemaVersion"] = 2; }
            else { document["providers"] = new JsonObject { ["fileEnumeration"] = "unknown-provider" }; }
            string manifest = test.Workspace.GetPath("invalid-session.json");
            File.WriteAllText(manifest, document.ToJsonString());
            Dictionary<string, string> before = CaptureNormalMutableLayout();

            OwnedProcess application = test.StartApplication("--test-session", manifest);
            ProcessCompletion result = await application.WaitForExitAsync(test.Deadline);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.StandardError, "Application startup options failed:");
            StringAssert.Contains(result.StandardError, reason);
            Assert.IsFalse(Directory.Exists(profile));
            Dictionary<string, string> after = CaptureNormalMutableLayout();
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray(), "通常配置のパス集合を変えません。");
            foreach ((string path, string content) in before)
            {
                Assert.AreEqual(content, after[path], "通常配置の内容を変えません: " + path);
            }
        });

    private static async Task CheckProfileAsync(ProcessCase test, string profile, bool isTest, string normalMutex, bool expected)
    {
        (OwnedProcess Process, string Ready, string Release) process = test.StartProfile(profile, isTest, normalMutex);
        await AssertAcquiredAsync(process, expected, test.Deadline);
        await ReleaseProfileAsync(process, expected, test.Deadline);
    }

    private static async Task AssertAcquiredAsync((OwnedProcess Process, string Ready, string Release) process, bool expected, ProcessDeadline deadline)
    {
        using JsonDocument ready = await ReadReadyAsync(process.Process, process.Ready, deadline);
        Assert.AreEqual(expected, ready.RootElement.GetProperty("Acquired").GetBoolean());
        if (expected) { Assert.IsFalse(process.Process.Completion.IsCompleted, "取得したleaseをreleaseまで保持します。"); }
    }

    private static async Task ReleaseProfileAsync((OwnedProcess Process, string Ready, string Release) process, bool expected, ProcessDeadline deadline)
    {
        if (expected) { File.WriteAllText(process.Release, "release"); }
        ProcessCompletion result = await process.Process.WaitForExitAsync(deadline);
        AssertSuccess(result);
        using var observation = JsonDocument.Parse(result.StandardOutput);
        Assert.AreEqual(expected, observation.RootElement.GetProperty("Acquired").GetBoolean());
    }

    private static async Task CleanTemporaryAsync(ProcessCase test, string scope)
    {
        OwnedProcess cleaner = test.StartProbe("cleanup-temp", scope);
        ProcessCompletion result = await cleaner.WaitForExitAsync(test.Deadline);
        AssertSuccess(result);
        using var observation = JsonDocument.Parse(result.StandardOutput);
        Assert.IsTrue(observation.RootElement.GetProperty("Completed").GetBoolean());
    }

    private static async Task<JsonDocument> ReadReadyAsync(OwnedProcess process, string path, ProcessDeadline deadline)
    {
        while (!File.Exists(path))
        {
            if (process.Completion.IsCompleted)
            {
                ProcessCompletion result = await process.WaitForExitAsync(deadline);
                // 終端観測とreadyの配送順だけで失敗にしません。公開はtmpからのrenameです。
                if (File.Exists(path)) { break; }
                Assert.Fail($"ready公開前に終了しました。exit={result.ExitCode}, stderr={result.StandardError}");
            }
            TimeSpan remaining = deadline.Remaining;
            if (remaining == TimeSpan.Zero) { throw new TimeoutException("プローブのready公開が期限を超えました: " + path); }
            await Task.WhenAny(process.Completion, Task.Delay(remaining < TimeSpan.FromMilliseconds(20) ? remaining : TimeSpan.FromMilliseconds(20)));
        }
        if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("プローブのready観測が期限を超えました: " + path); }
        // 完成JSONのrename公開直後はDELETE handleが残り得るため、読取りをrenameと共有します。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonDocument.Parse(stream);
    }

    private static void AssertSuccess(ProcessCompletion result)
    {
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual(string.Empty, result.StandardError);
    }

    private static string RequiredString(JsonElement value, string property)
        => value.GetProperty(property).GetString() ?? throw new AssertFailedException("JSON文字列がnullです: " + property);

    private static JsonObject CreateManifest(string profile) => new()
    {
        ["schemaVersion"] = 1,
        ["profileDirectory"] = profile,
        ["runId"] = "runtime-process-run",
        ["caseId"] = Guid.NewGuid().ToString("N"),
        ["controlPipeName"] = "runtime-process-" + Guid.NewGuid().ToString("N")
    };

    private static Dictionary<string, string> CaptureNormalMutableLayout()
    {
        string directory = Path.GetDirectoryName(ApplicationPath) ?? throw new InvalidOperationException("製品fixtureの所在が不明です。");
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in new[] { "config", "data", "lang", "log", "update_work", "imported_metadata", "test.mp3" })
        {
            string path = Path.Combine(directory, name);
            if (Directory.Exists(path))
            {
                entries.Add(name, "directory");
                foreach (string child in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                {
                    entries.Add(Path.GetRelativePath(directory, child), "directory");
                }
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) { AddFile(file); }
            }
            else if (File.Exists(path)) { AddFile(path); }
        }
        return entries;

        void AddFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            entries.Add(Path.GetRelativePath(directory, path), Convert.ToHexString(SHA256.HashData(stream)));
        }
    }

    private static async Task RunCaseAsync(Func<ProcessCase, Task> scenario)
    {
        var test = new ProcessCase();
        Exception? primary = null;
        try { await scenario(test); }
        catch (Exception failure) { primary = failure; throw; }
        finally { await test.CleanupAsync(primary); }
    }

    /// <summary>このケースで起動した参照だけを保持し、主失敗と回収失敗を別々に残します。</summary>
    private sealed class ProcessCase
    {
        private readonly List<OwnedProcess> processes = [];

        /// <summary>入力・ready・releaseと製品の可変領域をこのケースに閉じ込めます。</summary>
        internal TestWorkspace Workspace { get; } = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        /// <summary>readyから全プロセスの終端まで共用し、個別待機では延長しない期限です。</summary>
        internal ProcessDeadline Deadline { get; } = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(45));

        /// <summary>既定環境を継承し、固定配置のprobeをケース内CWDで起動します。</summary>
        internal OwnedProcess StartProbe(params string[] arguments) => StartProbe(arguments, null);

        /// <summary>子だけへ環境差分を渡し、stdoutの回収をOwnedProcessへ一任します。</summary>
        internal OwnedProcess StartProbe(string[] arguments, IReadOnlyDictionary<string, string?>? environment)
            => Start(ProbePath, ["runtime-isolation", .. arguments], environment);

        /// <summary>fallbackなしのbuild出力を使い、実製品のMainへargvを渡します。</summary>
        internal OwnedProcess StartApplication(params string[] arguments) => Start(ApplicationPath, arguments, null);

        /// <summary>個別のreadyとreleaseを割り当て、ケース専有の通常mutex名でprofileを保持します。</summary>
        internal (OwnedProcess Process, string Ready, string Release) StartProfile(string profile, bool isTest, string normalMutex)
        {
            string ready = Workspace.GetPath("profile-" + processes.Count + "-ready.json");
            string release = Workspace.GetPath("profile-" + processes.Count + "-release");
            return (StartProbe("hold-profile", profile, isTest.ToString(), normalMutex, ready, release), ready, release);
        }

        private OwnedProcess Start(string executable, string[] arguments, IReadOnlyDictionary<string, string?>? environment)
        {
            Assert.IsTrue(File.Exists(executable), "指定のbuild fixtureが必要です: " + executable);
            var process = OwnedProcess.Start(new ProcessStartRequest(executable, arguments, Workspace.RootDirectory, environment));
            processes.Add(process);
            return process;
        }

        /// <summary>全起動参照とケース領域を回収し、既存主失敗があれば回収失敗を付記して維持します。</summary>
        internal async Task CleanupAsync(Exception? primary)
        {
            var failures = new List<Exception>();
            var cleanupDeadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(15));
            for (int index = processes.Count - 1; index >= 0; index--)
            {
                OwnedProcess process = processes[index];
                try
                {
                    ProcessStopResult stopped = await process.StopAsync(null, cleanupDeadline);
                    if (stopped.CleanupFailure is not null) { failures.Add(stopped.CleanupFailure); }
                }
                catch (Exception failure) { failures.Add(failure); }
                try { await process.DisposeAsync(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            try { Workspace.Dispose(); }
            catch (Exception failure) { failures.Add(failure); }
            if (failures.Count == 0) { return; }
            var cleanup = new AggregateException("実プロセス検査の回収に失敗しました。", failures);
            if (primary is null) { throw cleanup; }
            primary.Data["RuntimeIsolationProcessTests.CleanupFailure"] = cleanup;
            Console.Error.WriteLine(cleanup);
        }
    }
}
