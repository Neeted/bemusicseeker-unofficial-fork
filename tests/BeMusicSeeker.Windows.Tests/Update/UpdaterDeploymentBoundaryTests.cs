using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using static BeMusicSeeker.TestSupport.Components.UpdateDeploymentTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterDeploymentBoundaryTests
{
    /// <summary>MSTestが注入するcontextの取消を実process待機へ渡します。</summary>
    // MSTestのproperty injectionでテストメソッド実行前に非nullが保証されます。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 実公開関数が同じprofileと出力設定でlocked restoreからno-restore publishへ渡すことを保証する。
    /// </summary>
    /// <remarks>既定出力、明示rootの通常版、次版を区別する。実R2R生成と物理出力の分離はFullの実publishで検査する。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task PublishScriptRestoresReadyToRunDependenciesBeforeNoRestorePublish(bool dedicatedRoot, bool nextVersion)
    {
        string repositoryRoot = FindRepositoryRoot();
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $artifactRootPath = '{{EscapePowerShellLiteral(root)}}'
            $artifactRootWasProvided = ${{dedicatedRoot.ToString().ToLowerInvariant()}}
            $effectiveVersion = '{{(nextVersion ? "7.2.3.5" : "7.2.3.4")}}'
            $configuration = 'Release'
            $platform = 'x64'
            $appProject = Join-Path $artifactRootPath 'source/BeMusicSeeker.csproj'
            $updaterProject = Join-Path $artifactRootPath 'source/BeMusicSeeker.Updater.csproj'
            $appPublishOutput = Join-Path $artifactRootPath 'app'
            $updaterPublishOutput = Join-Path $artifactRootPath 'updater'
            $script:calls = [Collections.Generic.List[object]]::new()
            function dotnet { throw '捕捉境界を迂回したdotnet起動です。' }
            function Invoke-CheckedCommand {
                param([string]$Command, [Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
                if ($Command -cne 'dotnet') { throw "未知のcommand: $Command" }
                $script:calls.Add([pscustomobject]@{ Arguments = @($Arguments) })
            }
            function Assert-SelfContainedPublishLayout { }
            function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
            function Property([string[]]$arguments, [string]$name) {
                $prefix = '-p:' + $name + '='
                $values = @($arguments | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
                Require ($values.Count -eq 1) "propertyの欠落/重複: $name"
                return $values[0].Substring($prefix.Length)
            }
            $ast = [Management.Automation.Language.Parser]::ParseFile('{{EscapePowerShellLiteral(Path.Combine(repositoryRoot, "scripts", "publish.ps1"))}}', [ref]$null, [ref]$null)
            $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-SelfContainedPublish' }, $true)
            Require ($null -ne $function) '実公開関数がありません。'
            . ([scriptblock]::Create($function.Extent.Text))
            Invoke-SelfContainedPublish
            Require ($calls.Count -eq 4) 'app/updater各restore/publishの4呼出しが必要です。'
            $projects = @($appProject, $updaterProject)
            $names = @('BeMusicSeeker', 'BeMusicSeeker.Updater')
            $profiles = @('WinX64SelfContained', 'WinX64SelfContainedSingleFile')
            $outputs = @($appPublishOutput, $updaterPublishOutput)
            for ($projectIndex = 0; $projectIndex -lt 2; $projectIndex++) {
                $restore = $calls[$projectIndex * 2].Arguments
                $publish = $calls[$projectIndex * 2 + 1].Arguments
                Require ($restore[0] -ceq 'restore' -and $publish[0] -ceq 'publish') 'restore→publishの順序が異なります。'
                Require ($restore[1] -ceq $projects[$projectIndex] -and $publish[1] -ceq $projects[$projectIndex]) '対象projectが異なります。'
                Require ($restore -ccontains '--locked-mode') 'locked restoreではありません。'
                Require ($publish -ccontains '--no-restore') 'publishが再restoreします。'
                foreach ($arguments in @($restore, $publish)) {
                    $runtimeIndex = [Array]::IndexOf($arguments, '--runtime')
                    Require ($runtimeIndex -ge 0 -and $arguments[$runtimeIndex + 1] -ceq 'win-x64') 'runtimeが異なります。'
                    Require ((Property $arguments 'Configuration') -ceq 'Release') 'configurationが異なります。'
                    Require ((Property $arguments 'Platform') -ceq 'x64') 'platformが異なります。'
                    Require ((Property $arguments 'PublishProfile') -ceq $profiles[$projectIndex]) 'profileが異なります。'
                    if ($projectIndex -eq 0) {
                        Require ((Property $arguments 'BeMusicSeekerAppVersion') -ceq $effectiveVersion) 'app版指定が欠落/不一致です。'
                    }
                    foreach ($property in @('BaseIntermediateOutputPath', 'MSBuildProjectExtensionsPath', 'BaseOutputPath')) {
                        if ($artifactRootWasProvided) {
                            $value = Property $arguments $property
                            $leaf = if ($property -ceq 'BaseOutputPath') { 'bin' } else { 'obj' }
                            $expected = Join-Path $artifactRootPath "$leaf/$($names[$projectIndex])"
                            Require ([IO.Path]::IsPathFullyQualified($value)) '専有出力が絶対pathではありません。'
                            Require ([IO.Path]::GetFullPath($value).TrimEnd('\', '/') -ceq $expected.TrimEnd('\', '/')) '専有出力が別root/projectを指します。'
                        } else {
                            Require (@($arguments | Where-Object { $_.StartsWith("-p:$property=", [StringComparison]::Ordinal) }).Count -eq 0) '既定project出力が上書きされています。'
                        }
                    }
                }
                $restoreProperties = @($restore | Where-Object { $_.StartsWith('-p:', [StringComparison]::Ordinal) } | Sort-Object)
                $publishProperties = @($publish | Where-Object { $_.StartsWith('-p:', [StringComparison]::Ordinal) -and -not $_.StartsWith('-p:PublishDir=', [StringComparison]::Ordinal) } | Sort-Object)
                Require (@(Compare-Object $restoreProperties $publishProperties -CaseSensitive).Count -eq 0) 'restore/publishのpropertyが一致しません。'
                Require ((Property $publish 'PublishDir') -ceq $outputs[$projectIndex]) '公開先が異なります。'
            }
            """;
        OwnedProcess? process = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        ProcessDeadline? cleanupDeadline = null;
        var stopwatch = Stopwatch.StartNew();
        ProcessDeadline BeginCleanup() => cleanupDeadline ??= ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(5));
        try
        {
            // CreateProcessWのapplication指定はPATH探索しないため、元のpwshを同じ環境から絶対pathへ解決します。
            string powershell = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.GetFullPath(Path.Combine(directory, "pwsh.exe"))).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("公開引数検証に必要なpwsh.exeがPATHにありません。");
            var request = new ProcessStartRequest(powershell, ["-NoProfile", "-NonInteractive", "-Command", command], root);
            // 起動前からroot・両pipe・Jobの全終端まで一つの期限を共有します。
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(60));
            process = OwnedProcess.Start(request, BeginCleanup);
            ProcessCompletion completion = await process.WaitForExitAsync(deadline, TestContext.CancellationToken);
            Assert.AreEqual(0, completion.ExitCode,
                "publish restore/arguments contract" + Environment.NewLine + completion.StandardOutput + Environment.NewLine + completion.StandardError);
        }
        catch (Exception failure) { primary = failure; }
        finally
        {
            ProcessDeadline deadline = BeginCleanup();
            if (process is not null)
            {
                try
                {
                    ProcessStopResult stopped = await process.StopAsync(null, deadline);
                    if (stopped.CleanupFailure is not null) { cleanup.Add(stopped.CleanupFailure); }
                }
                catch (Exception failure) { cleanup.Add(failure); }
                finally
                {
                    try { await process.DisposeAsync(); }
                    catch (Exception failure) { cleanup.Add(failure); }
                }
            }
            try { workspace.Dispose(); }
            catch (Exception failure) { cleanup.Add(failure); }
        }
        if (primary is null && cleanup.Count > 0)
        {
            primary = new AggregateException("公開引数検証の回収を確認できません。", cleanup);
        }
        if (primary is not null)
        {
            // 故障したpipeを同期的に待たず、既に回収できた出力と主原因・回収診断を残します。
            string diagnostic = $"publish restore/arguments contract: PID {process?.ProcessId}, elapsed {stopwatch.Elapsed.TotalSeconds:F1}s"
                + Environment.NewLine + DescribeOutput("stdout", process?.StandardOutput)
                + Environment.NewLine + DescribeOutput("stderr", process?.StandardError);
            primary.Data["ValidatorProcessDiagnostics"] = diagnostic;
            if (cleanup.Count > 0) { primary.Data["PublishArgumentsCleanupFailures"] = new AggregateException(cleanup); }
            TestContext.WriteLine(diagnostic);
            foreach (Exception failure in cleanup) { TestContext.WriteLine(failure.ToString()); }
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }

    private static string DescribeOutput(string name, Task<string>? output)
        => output?.IsCompletedSuccessfully == true
            ? name + ": " + output.GetAwaiter().GetResult()
            : name + ": " + (output?.Status.ToString() ?? "not started") + " " + output?.Exception;

    private static string EscapePowerShellLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
