using System.Text.Json;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>通常版の正本、新しいpublish引数の拒否、vswhereのPATH補完を、publish.ps1の小さい入力境界で検査します。</summary>
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

    /// <summary>
    /// 補完の判断を、PATH・標準の導入先・vswhere.exeの有無の組合せで検査します。親プロセスと利用者の環境は変えず、子プロセスの中だけで環境を所有します。
    /// J1は、PATHで解決できず標準の導入先にあるとき、PATHの先頭だけへ加えて残りの順を保ち、出力がちょうど1行で、解決先がそのファイルになることを保証します。
    /// 末尾への追加・置換・別のパス・出力の欠落や重複・Get-Commandの失敗による例外を検出します。
    /// J2は、PATHの別の場所で解決できるとき、標準の導入先にもあってもPATHの文字列が完全に同じで出力もないことを保証します。無条件の追加や文字列だけの判定を検出します。
    /// J3は、導入先のディレクトリはあるがvswhere.exeがないとき、J4は、導入先のディレクトリもないとき、PATHを変えず例外も出さないことを保証します（出力は固定しません）。ディレクトリの有無だけの判定や存在しないパスでの例外を検出します。
    /// </summary>
    [TestMethod]
    [DataRow("J1")]
    [DataRow("J2")]
    [DataRow("J3")]
    [DataRow("J4")]
    public async Task VswhereCompletionAddsTheStandardInstallerOnlyWhenItIsMissingFromPath(string condition)
    {
        using var fixture = new VersionFixture();
        string installer = fixture.Path("Program Files (x86)/Microsoft Visual Studio/Installer");
        string other = fixture.Path("other");
        string empty = fixture.Path("empty");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(other);
        if (condition is "J1" or "J2") { Directory.CreateDirectory(installer); File.WriteAllText(System.IO.Path.Combine(installer, "vswhere.exe"), ""); }
        if (condition == "J3") { Directory.CreateDirectory(installer); }
        if (condition == "J2") { File.WriteAllText(System.IO.Path.Combine(other, "vswhere.exe"), ""); }
        string path = condition == "J2" ? other + ";" + empty : empty;
        string source = Quote(fixture.Path("scripts/publish.ps1"));
        string command = $"$ast=[System.Management.Automation.Language.Parser]::ParseFile({source},[ref]$null,[ref]$null); "
            + "$function=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Add-VswhereToPathIfMissing'},$true); "
            + "if($null -eq $function){throw 'vswhere補完関数がありません。'}; . ([scriptblock]::Create($function.Extent.Text)); "
            + $"${{env:ProgramFiles(x86)}}={Quote(fixture.Path("Program Files (x86)"))}; $env:PATH={Quote(path)}; "
            + $"Set-Content -LiteralPath {Quote(fixture.Path("before.txt"))} -Value $env:PATH -NoNewline; "
            + $"Set-Content -LiteralPath {Quote(fixture.Path("premise.txt"))} -Value \"$((Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue).Source)\" -NoNewline; "
            + "[void](Add-VswhereToPathIfMissing); "
            + $"Set-Content -LiteralPath {Quote(fixture.Path("after.txt"))} -Value $env:PATH -NoNewline; "
            + $"Set-Content -LiteralPath {Quote(fixture.Path("resolved.txt"))} -Value \"$((Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue).Source)\" -NoNewline";
        ProcessCompletion result = await fixture.CommandAsync(command);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        string before = File.ReadAllText(fixture.Path("before.txt"));
        string after = File.ReadAllText(fixture.Path("after.txt"));
        string premise = File.ReadAllText(fixture.Path("premise.txt"));
        string resolved = File.ReadAllText(fixture.Path("resolved.txt"));
        string[] lines = (result.StandardOutput + "\n" + result.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (condition == "J2") { Assert.AreEqual(System.IO.Path.GetFullPath(System.IO.Path.Combine(other, "vswhere.exe")), System.IO.Path.GetFullPath(premise), "前提: PATHの別の場所で解決できません。"); }
        else { Assert.AreEqual("", premise, "前提不足: テストが所有するPATHでvswhere.exeが解決されています。"); }
        if (condition == "J1")
        {
            string[] elements = after.Split(';');
            Assert.AreEqual(NormalizeDirectory(installer), NormalizeDirectory(elements[0]));
            Assert.AreEqual(before, string.Join(';', elements.Skip(1)));
            Assert.AreEqual(System.IO.Path.GetFullPath(System.IO.Path.Combine(installer, "vswhere.exe")), System.IO.Path.GetFullPath(resolved));
            Assert.HasCount(1, lines, result.StandardOutput + result.StandardError);
        }
        else
        {
            Assert.AreEqual(before, after);
            if (condition == "J2") { Assert.IsEmpty(lines, result.StandardOutput + result.StandardError); }
        }
    }

    /// <summary>
    /// publish.ps1全体の実行で、最初のnativeコマンドへ補完したPATHが渡り、nativeが失敗しても呼んだプロセスのPATHが実行前と完全に同じへ戻ることを保証します。
    /// 補完の呼出しの欠落、nativeより後での補完、復元の欠落、成功時だけの復元、publish前の復元を検出します。失敗はnativeの終了コード77によるものであることも確認します。
    /// </summary>
    [TestMethod]
    public async Task PublishPassesTheCompletedPathToNativeCommandsAndRestoresItAfterFailure()
    {
        using var fixture = new VersionFixture();
        string installer = fixture.Path("Program Files (x86)/Microsoft Visual Studio/Installer");
        Directory.CreateDirectory(installer);
        File.WriteAllText(System.IO.Path.Combine(installer, "vswhere.exe"), "");
        string owned = fixture.Path("fakebin") + ";" + Environment.SystemDirectory;
        string vswhereLookup = "$((Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue).Source)";
        string command = $"${{env:ProgramFiles(x86)}}={Quote(fixture.Path("Program Files (x86)"))}; $env:PATH={Quote(owned)}; $before=$env:PATH; "
            + $"Set-Content -LiteralPath {Quote(fixture.Path("premise.txt"))} -Value \"{vswhereLookup}\" -NoNewline; "
            + $"$caught=''; try {{ & {Quote(fixture.Path("scripts/publish.ps1"))} -ArtifactRoot {Quote(fixture.Path("variant"))} }} catch {{ $caught=$_.Exception.Message }}; "
            + $"Set-Content -LiteralPath {Quote(fixture.Path("before.txt"))} -Value $before -NoNewline; "
            + $"Set-Content -LiteralPath {Quote(fixture.Path("after.txt"))} -Value $env:PATH -NoNewline; "
            + $"Set-Content -LiteralPath {Quote(fixture.Path("caught.txt"))} -Value $caught -NoNewline";
        ProcessCompletion result = await fixture.CommandAsync(command);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual("", File.ReadAllText(fixture.Path("premise.txt")), "前提不足: テストが所有するPATHでvswhere.exeが解決されています。");
        StringAssert.Contains(File.ReadAllText(fixture.Path("caught.txt")), "exit code 77");
        string nativePath = File.ReadAllText(fixture.Path("fakebin/dotnet-path")).Trim();
        Assert.AreEqual(NormalizeDirectory(installer), NormalizeDirectory(nativePath.Split(';')[0]));
        Assert.AreEqual(File.ReadAllText(fixture.Path("before.txt")), File.ReadAllText(fixture.Path("after.txt")));
    }

    /// <summary>末尾の区切りと大文字小文字の差を、加えるディレクトリの表記として固定しないための正規化です。</summary>
    private static string NormalizeDirectory(string value)
        => System.IO.Path.GetFullPath(value).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).ToUpperInvariant();

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
            File.WriteAllText(Path("fakebin/dotnet.cmd"), "@echo off\r\n>\"%~dp0dotnet-called\" echo called\r\n>\"%~dp0dotnet-path\" echo %PATH%\r\nexit /b 77\r\n");
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
