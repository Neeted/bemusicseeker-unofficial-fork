using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;
using static BeMusicSeeker.TestSupport.Components.UpdateDeploymentTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterDeploymentBoundaryTests
{
    /// <summary>実公開layoutの受理と全禁止配置の実混入・理由付き拒否を確認します。</summary>
    [TestMethod]
    [TestCategory("ReleaseAcceptance")]
    public async Task PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput()
    {
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(180));
        // 共通10秒上限の内側で、元の公開受入の回収5秒も一度だけ確定します。
        var cleanupDeadline = new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)).LimitRemaining(TimeSpan.FromSeconds(5)));
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string stagingDirectory = workspace.GetPath("staging");
        var processes = new List<OwnedProcess>();
        var cleanupFailures = new List<Exception>();
        Exception? primaryFailure = null;
        try
        {
            string repositoryRoot = FindRepositoryRoot();
            string validatorPath = Path.Combine(repositoryRoot, "scripts", "portable-package-layout.ps1");
            Assert.IsTrue(File.Exists(validatorPath), "The release layout validator must be present.");
            DistributionCatalog catalog = await DistributionCatalog.ReadFromEnvironmentAsync(repositoryRoot, deadline);
            string appPublishDirectory = catalog.Current.AppPublishRoot ?? throw new InvalidDataException("Current app rootがありません。");
            string updaterPublishDirectory = catalog.Current.UpdaterPublishRoot ?? throw new InvalidDataException("Current updater rootがありません。");
            Directory.CreateDirectory(stagingDirectory);
            foreach (string sourcePath in Directory.EnumerateFiles(appPublishDirectory, "*", SearchOption.AllDirectories))
            {
                DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
                string relativePath = Path.GetRelativePath(appPublishDirectory, sourcePath);
                string topLevelName = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (topLevelName.Equals("config", StringComparison.OrdinalIgnoreCase)
                    || topLevelName.Equals("data", StringComparison.OrdinalIgnoreCase)
                    || topLevelName.Equals("log", StringComparison.OrdinalIgnoreCase)
                    || topLevelName.Equals("logs", StringComparison.OrdinalIgnoreCase)
                    || topLevelName.Equals("update_backup", StringComparison.OrdinalIgnoreCase)
                    || topLevelName.Equals("update_work", StringComparison.OrdinalIgnoreCase)
                    || topLevelName.Equals("imported_metadata", StringComparison.OrdinalIgnoreCase)
                    || relativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                    || relativePath.StartsWith("BeMusicSeeker.Updater.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destinationPath = Path.Combine(stagingDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath);
            }
            string updaterPath = Path.Combine(updaterPublishDirectory, "BeMusicSeeker.Updater.exe");
            Assert.IsTrue(File.Exists(updaterPath), "The self-contained updater publish output must exist.");
            File.Copy(updaterPath, Path.Combine(stagingDirectory, "BeMusicSeeker.Updater.exe"));

            // CreateProcessWのapplication指定はPATH探索しないため、元のpwshを同じ環境から絶対pathへ解決します。
            string powershell = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.GetFullPath(Path.Combine(directory, "pwsh.exe"))).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("公開layout検証に必要なpwsh.exeがPATHにありません。");

            ProcessStartInfo CreateValidatorStartInfo(string? command = null)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = powershell,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(command
                    ?? $". '{EscapePowerShellLiteral(validatorPath)}'; "
                        + $"Assert-PortableStagingLayout -targetStagingDirectory '{EscapePowerShellLiteral(stagingDirectory)}' -requiresMetadataArchive:$false");
                return startInfo;
            }

            await RunValidatorProcess(CreateValidatorStartInfo(), "positive package layout validation");

            string[] forbiddenPaths =
            {
                "config",
                "log",
                "libs/SevenZipExtractor.dll",
                "libs/OggVorbis.NET64.dll",
                "libs/x64/sqlite3.dll",
                "BeMusicSeeker.exe.config",
                "libs/Bass.Net.dll",
                "libs/DynamicJson.dll",
                "libs/IniLibrary.dll",
                "libs/Livet.dll",
                "libs/Livet.Extensions.dll",
                "libs/MetroRadiance.Chrome.dll",
                "libs/MetroRadiance.Core.dll",
                "libs/MetroRadiance.dll",
                "libs/Microsoft.Expression.Drawing.dll",
                "libs/Microsoft.Expression.Effects.dll",
                "libs/Microsoft.Expression.Interactions.dll",
                "libs/Microsoft.WindowsAPICodePack.dll",
                "libs/Microsoft.WindowsAPICodePack.Shell.dll",
                "libs/Newtonsoft.Json.dll",
                "libs/NLog.Database.dll",
                "libs/NLog.dll",
                "libs/NLog.WindowsEventLog.dll",
                "libs/QuickConverter.dll",
                "libs/SgmlReaderDll.dll",
                "libs/System.Collections.Immutable.dll",
                "libs/System.Resources.Extensions.dll",
                "libs/System.Memory.dll",
                "libs/System.Buffers.dll",
                "libs/System.Numerics.Vectors.dll",
                "libs/System.Runtime.CompilerServices.Unsafe.dll",
                "libs/System.Windows.Interactivity.dll",
                "libs/sqlite.net.dll",
                "x86/sqlite3.dll",
                "x86/7z.dll",
                "x86/bass.dll",
                "x86/bass_fx.dll",
                "x86/bassasio.dll",
                "x86/bassenc.dll",
                "x86/bassmix.dll",
                "x86/basswasapi.dll",
                "libs/x86/sqlite3.dll",
                "libs/x86/7z.dll",
                "libs/x86/bass.dll",
                "libs/x86/bass_fx.dll",
                "libs/x86/bassasio.dll",
                "libs/x86/bassenc.dll",
                "libs/x86/bassmix.dll",
                "libs/x86/basswasapi.dll",
                "Bass.Net.dll",
                "x64/OggVorbis.NET64.dll",
                "x64/7z.dll",
                "x64/bass.dll",
                "x64/bass_fx.dll",
                "x64/bassasio.dll",
                "x64/bassenc.dll",
                "x64/bassmix.dll",
                "x64/basswasapi.dll",
                "OggVorbis.NET.dll"
            };
            (string Path, bool IsDirectory, string MembershipPath, string FailurePath)[] forbiddenCases = forbiddenPaths
                .Select(path => (
                    Path: path,
                    IsDirectory: path is "config" or "log",
                    MembershipPath: path,
                    FailurePath: path.StartsWith("libs/x86/", StringComparison.OrdinalIgnoreCase)
                        ? "libs/x86"
                        : path.StartsWith("x86/", StringComparison.OrdinalIgnoreCase)
                            ? "x86"
                            : path))
                .Concat(new[]
                {
                    (Path: "x86/user-added.dll", IsDirectory: false, MembershipPath: "x86", FailurePath: "x86"),
                    (Path: "libs/x86/user-added.dll", IsDirectory: false, MembershipPath: "libs/x86", FailurePath: "libs/x86")
                })
                .ToArray();
            string forbiddenCaseExpression = "@("
                + string.Join(
                    ",",
                    forbiddenCases.Select(testCase =>
                        "[pscustomobject]@{"
                        + "Path='" + EscapePowerShellLiteral(testCase.Path) + "';"
                        + "IsDirectory=$" + testCase.IsDirectory.ToString().ToLowerInvariant() + ";"
                        + "MembershipPath='" + EscapePowerShellLiteral(testCase.MembershipPath) + "';"
                        + "FailurePath='" + EscapePowerShellLiteral(testCase.FailurePath) + "'"
                        + "}"))
                + ")";
            string negativeValidatorCommand =
                $". '{EscapePowerShellLiteral(validatorPath)}'; "
                + $"$root = '{EscapePowerShellLiteral(stagingDirectory)}'; "
                + $"$forbiddenCases = {forbiddenCaseExpression}; "
                + "$policyPaths = @(Get-PortableForbiddenPaths); "
                + "foreach ($case in $forbiddenCases) { "
                + "if ($policyPaths -notcontains $case.MembershipPath) { throw \"Forbidden path is missing from policy: $($case.MembershipPath)\" }; "
                + "$fullPath = Join-PortablePackageRelativePath $root $case.Path; "
                + "$parentPath = Split-Path -Parent $fullPath; "
                + "if ($case.IsDirectory) { [IO.Directory]::CreateDirectory($fullPath) | Out-Null } "
                + "else { [IO.Directory]::CreateDirectory($parentPath) | Out-Null; [IO.File]::WriteAllText($fullPath, 'forbidden-layout-entry') }; "
                + "$failureMessage = $null; "
                + "try { Assert-PortableStagingLayout -targetStagingDirectory $root -requiresMetadataArchive:$false } catch { $failureMessage = $_.Exception.Message }; "
                + "if ([string]::IsNullOrWhiteSpace($failureMessage)) { throw \"Validator accepted forbidden path: $($case.Path)\" }; "
                + "if ($failureMessage.IndexOf($case.FailurePath, [StringComparison]::OrdinalIgnoreCase) -lt 0) { "
                + "throw \"Validator rejected $($case.Path) for an unexpected reason: $failureMessage\" "
                + "}; "
                + "if ($case.IsDirectory) { [IO.Directory]::Delete($fullPath, $true) } else { [IO.File]::Delete($fullPath) }; "
                + "while ($parentPath -ne $root -and [IO.Directory]::Exists($parentPath) -and [IO.Directory]::GetFileSystemEntries($parentPath).Length -eq 0) { "
                + "[IO.Directory]::Delete($parentPath); "
                + "$parentPath = Split-Path -Parent $parentPath "
                + "} "
                + "}";

            await RunValidatorProcess(
                CreateValidatorStartInfo(negativeValidatorCommand),
                "negative package layout validation");
            DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (exception is OwnedProcessStartException { CleanupFailure: not null } failed) { cleanupFailures.Add(failed.CleanupFailure); }
        }
        finally
        {
            foreach (OwnedProcess owned in processes.AsEnumerable().Reverse())
            {
                try
                {
                    ProcessStopResult stopped = await owned.StopAsync(null, cleanupDeadline.Value);
                    if (stopped.CleanupFailure is not null) { cleanupFailures.Add(stopped.CleanupFailure); }
                    await owned.DisposeAsync();
                }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            if (primaryFailure is not null || cleanupFailures.Count > 0) { workspace.PreserveArtifacts(); }
            try { workspace.Dispose(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count > 0) { primaryFailure.Data["UpdaterCleanupSecondaryDiagnostics"] = new AggregateException(cleanupFailures); }
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (cleanupFailures.Count > 0) { throw new AggregateException(cleanupFailures); }

        async Task RunValidatorProcess(ProcessStartInfo info, string operation)
        {
            DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
            ProcessDeadline localDeadline = deadline.LimitRemaining(TimeSpan.FromSeconds(60));
            ProcessDeadline streamDeadline = deadline.LimitRemaining(TimeSpan.FromSeconds(5));
            var process = OwnedProcess.Start(new ProcessStartRequest(info.FileName, info.ArgumentList.ToArray(), stagingDirectory), () => cleanupDeadline.Value);
            processes.Add(process);
            int exitCode = await process.WaitForRootExitAsync(localDeadline);
            Assert.AreEqual(0, exitCode, operation + " failed: " + await process.StandardError.WaitAsync(streamDeadline.Remaining));
            await Task.WhenAll(process.StandardOutput, process.StandardError).WaitAsync(streamDeadline.Remaining);
            await process.WaitForExitAsync(deadline);
        }
    }
    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
