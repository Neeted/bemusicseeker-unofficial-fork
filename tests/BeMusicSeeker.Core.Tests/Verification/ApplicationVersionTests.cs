using System.Text.Json;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>通常版の正本と新しいpublish引数の拒否を、実スクリプトの小さい入力境界で検査します。</summary>
[TestClass]
public sealed class ApplicationVersionTests
{
    /// <summary>canonical propsだけを正本とし、旧literalが異なっても採用せず、欠落・不正時にも旧値へ戻らないことを保証します。</summary>
    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing")]
    [DataRow("invalid")]
    [DataRow("duplicate")]
    public async Task CanonicalReaderNeverFallsBackToTheOldLiteral(string condition)
    {
        using var fixture = new VersionFixture();
        if (condition == "missing") { File.Delete(fixture.Path("ApplicationVersion.props")); }
        if (condition == "invalid") { fixture.WriteVersion("9.8.7"); }
        if (condition == "duplicate")
        {
            File.WriteAllText(fixture.Path("ApplicationVersion.props"), "<Project><PropertyGroup><BeMusicSeekerAppVersion>9.8.7.6</BeMusicSeekerAppVersion><BeMusicSeekerAppVersion>1.2.3.4</BeMusicSeekerAppVersion></PropertyGroup></Project>");
        }
        ProcessCompletion result = await fixture.CommandAsync($". {Quote(fixture.Path("scripts/application-version.ps1"))}; Get-ApplicationVersion -RepositoryRoot {Quote(fixture.Root)}");
        Assert.AreEqual(condition == "valid", result.ExitCode == 0, result.StandardError);
        if (condition == "valid") { Assert.AreEqual("9.8.7.6", result.StandardOutput.Trim()); }
    }

    /// <summary>不正override・専有root不足・build省略との併用を、dotnetや出力先への副作用より前に拒否します。</summary>
    [TestMethod]
    [DataRow("9.8.7", true, false)]
    [DataRow("9.8.x.6", true, false)]
    [DataRow("9.8.7.2147483648", true, false)]
    [DataRow("9.8.7.6", false, false)]
    [DataRow("9.8.7.6", true, true)]
    public async Task PublishRejectsInvalidOverridesBeforeAnyBuild(string version, bool root, bool skip)
    {
        using var fixture = new VersionFixture();
        string[] arguments = ["-File", fixture.Path("scripts/publish.ps1"), "-ApplicationVersion", version,
            .. root ? new[] { "-ArtifactRoot", fixture.Path("variant") } : [], .. skip ? new[] { "-SkipBuild" } : []];
        ProcessCompletion result = await fixture.RunAsync(arguments);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsFalse(File.Exists(fixture.Path("fakebin/dotnet-called")), result.StandardError);
        Assert.IsFalse(Directory.Exists(fixture.Path("variant")));
        Assert.IsFalse(Directory.Exists(fixture.Path("artifacts/publish")));
    }

    /// <summary>配布lock欠落時は通常restoreによる自動生成へ進まず、既存配布出力も変更せずに診断付きで拒否します。</summary>
    [TestMethod]
    public async Task PublishRejectsMissingPublishLockBeforeChangingOutputs()
    {
        using var fixture = new VersionFixture();
        File.Delete(fixture.Path("BeMusicSeeker/packages.publish.lock.json"));
        Directory.CreateDirectory(fixture.Path("variant/app"));
        File.WriteAllText(fixture.Path("variant/app/sentinel"), "existing output");
        ProcessCompletion result = await fixture.RunAsync(["-File", fixture.Path("scripts/publish.ps1"), "-ArtifactRoot", fixture.Path("variant")]);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardError, "App publish lock is missing");
        Assert.IsFalse(File.Exists(fixture.Path("fakebin/dotnet-called")));
        Assert.IsFalse(File.Exists(fixture.Path("BeMusicSeeker/packages.publish.lock.json")));
        Assert.AreEqual("existing output", File.ReadAllText(fixture.Path("variant/app/sentinel")));
        Assert.IsFalse(Directory.Exists(fixture.Path("variant/updater")));
        Assert.IsFalse(Directory.Exists(fixture.Path("variant/dist")));
    }

    /// <summary>旧Fullの版取得関数も同じ正本を使い、literal readerを残して配布準備だけ旧値になる誤りを検出します。</summary>
    [TestMethod]
    public async Task LegacyVerificationReaderUsesTheCanonicalSource()
    {
        using var fixture = new VersionFixture();
        string source = Quote(System.IO.Path.Combine(VersionFixture.Repository, "scripts/verify-refactor.ps1"));
        string command = $". {Quote(fixture.Path("scripts/application-version.ps1"))}; "
            + $"$ast=[System.Management.Automation.Language.Parser]::ParseFile({source},[ref]$null,[ref]$null); "
            + "$function=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-AssemblyInformationalVersion'},$true); "
            + "if($null -eq $function){throw '旧入口の版取得関数がありません。'}; . ([scriptblock]::Create($function.Extent.Text)); "
            + $"Get-AssemblyInformationalVersion -Root {Quote(fixture.Root)}";
        ProcessCompletion result = await fixture.CommandAsync(command);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual("9.8.7.6", result.StandardOutput.Trim());
    }

    /// <summary>旧local-update準備は通常packageをcanonical版で選び、既存の検証feed版・copy・hash生成の意味を維持します。</summary>
    [TestMethod]
    public async Task LegacyLocalUpdatePreparationSelectsTheCanonicalPackage()
    {
        using var fixture = new VersionFixture();
        Directory.CreateDirectory(fixture.Path("dist"));
        File.WriteAllText(fixture.Path("dist/bemusicseeker-unofficial-fork-v9.8.7.6.zip"), "package input");
        ProcessCompletion result = await fixture.RunAsync(["-File", fixture.Path("scripts/prepare-local-update-test.ps1"), "-OutputDir", fixture.Path("feed")]);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual("package input", File.ReadAllText(fixture.Path("feed/bemusicseeker-local-update-test.zip")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(fixture.Path("feed/update.json")));
        Assert.AreEqual("999.0.0.0", manifest.RootElement.GetProperty("version").GetString());
    }

    /// <summary>不正な版や末尾上限を丸めたり繰り上げたりせず、次版の生成前に失敗させます。</summary>
    [TestMethod]
    [DataRow("3.1.0")]
    [DataRow("3.1.0.invalid")]
    [DataRow("3.1.0.2147483647")]
    public void NextVersionDoesNotNormalizeInvalidOrOverflowedInput(string value)
        => Assert.Throws<Exception>(() => DistributionValidation.IncrementVersion(value));

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>package戻り値を捨ててもnativeの両診断を別streamへ保持し、非zero終了を成功へ置き換えないことを保証します。</summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(17)]
    public async Task NativeCommandRetainsDiagnosticsWhenItsReturnValueIsDiscarded(int exitCode)
    {
        using var fixture = new VersionFixture();
        string native = fixture.Path("native.cmd");
        File.WriteAllText(native, $"@echo off\r\necho native-stdout-marker\r\necho native-stderr-marker 1>&2\r\nexit /b {exitCode}\r\n");
        string source = Quote(fixture.Path("scripts/publish.ps1"));
        string command = $"$ast=[System.Management.Automation.Language.Parser]::ParseFile({source},[ref]$null,[ref]$null); "
            + "$function=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-CheckedCommand'},$true); "
            + "if($null -eq $function){throw 'native呼出関数がありません。'}; . ([scriptblock]::Create($function.Extent.Text)); "
            + $"[void](Invoke-CheckedCommand -Command {Quote(native)})";
        ProcessCompletion result = await fixture.CommandAsync(command);
        Assert.AreEqual(exitCode == 0, result.ExitCode == 0, result.StandardError);
        StringAssert.Contains(result.StandardOutput, "native-stdout-marker");
        StringAssert.Contains(result.StandardError, "native-stderr-marker");
        Assert.IsFalse(result.StandardOutput.Contains("native-stderr-marker", StringComparison.Ordinal));
        if (exitCode != 0) { StringAssert.Contains(result.StandardError, "exit code 17"); }
    }

    private sealed class VersionFixture : IDisposable
    {
        private readonly TestWorkspace workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        internal string Root => workspace.RootDirectory;
        internal string Path(string relative) => workspace.GetPath(relative);
        internal static string Repository
        {
            get
            {
                DirectoryInfo? directory = new(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "BeMusicSeeker.sln"))) { directory = directory.Parent; }
                return directory?.FullName ?? throw new DirectoryNotFoundException("fixture元のrepositoryがありません。");
            }
        }
        internal VersionFixture()
        {
            Directory.CreateDirectory(Path("scripts"));
            foreach (string script in new[] { "application-version.ps1", "publish.ps1", "portable-package-layout.ps1", "prepare-local-update-test.ps1" })
            {
                File.Copy(System.IO.Path.Combine(Repository, "scripts", script), Path("scripts/" + script));
            }
            Directory.CreateDirectory(Path("BeMusicSeeker/Properties"));
            // このfixtureはrestore以前の拒否だけを観測するため、lockの内容は使用しません。
            File.WriteAllText(Path("BeMusicSeeker/packages.publish.lock.json"), "{}");
            File.WriteAllText(Path("BeMusicSeeker/Properties/AssemblyInfo.cs"), "[assembly: AssemblyInformationalVersion(\"7.7.7.7\")]");
            Directory.CreateDirectory(Path("fakebin"));
            File.WriteAllText(Path("fakebin/dotnet.cmd"), "@echo off\r\n>\"%~dp0dotnet-called\" echo called\r\nexit /b 77\r\n");
            WriteVersion("9.8.7.6");
        }
        internal void WriteVersion(string version) => File.WriteAllText(Path("ApplicationVersion.props"),
            $"<Project><PropertyGroup><BeMusicSeekerAppVersion>{version}</BeMusicSeekerAppVersion></PropertyGroup></Project>");
        internal Task<ProcessCompletion> CommandAsync(string command) => RunAsync(["-Command", "$ErrorActionPreference='Stop'; " + command]);
        internal async Task<ProcessCompletion> RunAsync(IReadOnlyList<string> arguments)
        {
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
            // Jobへの停止起動はshellの探索を使わないため、親PATH上の実行体を明示します。
            string powershell = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => System.IO.Path.Combine(directory, "pwsh.exe")).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("PowerShell 7の実行体がPATHにありません。");
            await using var process = OwnedProcess.Start(new ProcessStartRequest(powershell, ["-NoProfile", "-NonInteractive", .. arguments], Root,
                new Dictionary<string, string?> { ["PATH"] = Path("fakebin") + ";" + Environment.GetEnvironmentVariable("PATH") }));
            return await process.WaitForExitAsync(deadline);
        }
        public void Dispose() => workspace.Dispose();
    }
}
