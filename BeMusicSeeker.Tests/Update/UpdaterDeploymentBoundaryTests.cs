using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterDeploymentBoundaryTests
{
    [TestMethod]
    public void ApplicationProjectUsesDedicatedUpdaterPublishBoundary()
    {
        string repositoryRoot = FindRepositoryRoot();
        var applicationProject = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker", "BeMusicSeeker.csproj"));
        XElement projectRoot = applicationProject.Root
            ?? throw new AssertFailedException("Application project XML has no root element.");

        Assert.IsFalse(
            projectRoot.Descendants("ProjectReference").Any(reference => string.Equals(
                (string?)reference.Attribute("Include"),
                @"BeMusicSeeker.Updater\BeMusicSeeker.Updater.csproj",
                StringComparison.OrdinalIgnoreCase)),
            "The main app publish must not inherit the updater build output.");
        Assert.IsFalse(
            projectRoot.Elements("Target").Any(target => string.Equals(
                (string?)target.Attribute("Name"),
                "CopyUpdaterToAppOutput",
                StringComparison.Ordinal)),
            "The build output must not be the updater deployment boundary.");
        Assert.IsNull(
            projectRoot
                .Descendants("Reference")
                .SingleOrDefault(reference => string.Equals(
                    (string?)reference.Attribute("Include"),
            "System.Deployment",
            StringComparison.OrdinalIgnoreCase)),
            "The unused System.Deployment reference must not remain in the application project.");

        var updaterProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "BeMusicSeeker.Updater",
            "BeMusicSeeker.Updater.csproj"));
        XElement updaterRoot = updaterProject.Root
            ?? throw new AssertFailedException("Updater project XML has no root element.");
        Assert.AreEqual("net10.0-windows", (string)updaterRoot.Descendants("TargetFramework").Single());
        Assert.AreEqual("x64", (string)updaterRoot.Descendants("PlatformTarget").Single());
        Assert.AreEqual("true", (string)updaterRoot.Descendants("PublishAot").Single());
        Assert.AreEqual("true", (string)updaterRoot.Descendants("IsAotCompatible").Single());
        Assert.AreEqual("Size", (string)updaterRoot.Descendants("OptimizationPreference").Single());

        string updaterProfilePath = Path.Combine(
            repositoryRoot,
            "BeMusicSeeker.Updater",
            "Properties",
            "PublishProfiles",
            "WinX64SelfContainedSingleFile.pubxml");
        var updaterProfile = XDocument.Load(updaterProfilePath);
        AssertProfileValue(updaterProfile, "RuntimeIdentifier", "win-x64");
        AssertProfileValue(updaterProfile, "SelfContained", "true");
        AssertProfileValue(updaterProfile, "PublishReadyToRun", "false");
        AssertProfileValueAbsent(updaterProfile, "PublishSingleFile");
        AssertProfileValueAbsent(updaterProfile, "IncludeNativeLibrariesForSelfExtract");
        AssertProfileValueAbsent(updaterProfile, "EnableCompressionInSingleFile");
        AssertProfileValueAbsent(updaterProfile, "PublishTrimmed");
        AssertProfileValueAbsent(updaterProfile, "StackTraceSupport");
        AssertProfileValueAbsent(updaterProfile, "InvariantGlobalization");
        AssertProfileValueAbsent(updaterProfile, "UseSystemResourceKeys");

        string selectedAppProfilePath = Path.Combine(
            repositoryRoot,
            "BeMusicSeeker",
            "Properties",
            "PublishProfiles",
            "WinX64SelfContained.pubxml");
        var selectedAppProfile = XDocument.Load(selectedAppProfilePath);
        AssertProfileValue(selectedAppProfile, "RuntimeIdentifier", "win-x64");
        AssertProfileValue(selectedAppProfile, "SelfContained", "true");
        AssertProfileValue(selectedAppProfile, "PublishSingleFile", "true");
        AssertProfileValue(selectedAppProfile, "IncludeNativeLibrariesForSelfExtract", "false");
        AssertProfileValue(selectedAppProfile, "IncludeAllContentForSelfExtract", "false");
        AssertProfileValue(selectedAppProfile, "PublishTrimmed", "false");
        AssertProfileValue(selectedAppProfile, "PublishReadyToRun", "true");
        AssertProfileValue(selectedAppProfile, "PublishReadyToRunComposite", "false");
        AssertProfileValue(selectedAppProfile, "EnableCompressionInSingleFile", "false");
        Assert.IsFalse(
            File.Exists(Path.Combine(
                repositoryRoot,
                "BeMusicSeeker",
                "Properties",
                "PublishProfiles",
                "WinX64SelfContainedSingleFile.pubxml")),
            "The superseded main-app profile must not remain.");
    }

    [TestMethod]
    public void PublishScriptRestoresReadyToRunDependenciesBeforeNoRestorePublish()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish.ps1"));
        int releasePackageStart = publishScript.IndexOf(
            "function New-ReleasePackage",
            StringComparison.Ordinal);
        Assert.IsTrue(releasePackageStart >= 0, "New-ReleasePackage must remain the package workflow owner.");
        int mainEntryPointStart = publishScript.IndexOf(
            "# ========== メイン ==========",
            releasePackageStart,
            StringComparison.Ordinal);
        Assert.IsTrue(mainEntryPointStart > releasePackageStart, "The publish script main entry point was not found.");

        string releasePackageFunction = publishScript[releasePackageStart..mainEntryPointStart];
        const string readyToRunRestore =
            "Invoke-CheckedCommand dotnet restore $solution '-r' 'win-x64' '--locked-mode' '-p:PublishReadyToRun=true'";
        int restoreIndex = releasePackageFunction.IndexOf(readyToRunRestore, StringComparison.Ordinal);
        int publishIndex = releasePackageFunction.IndexOf("Invoke-SelfContainedPublish", StringComparison.Ordinal);

        Assert.IsTrue(restoreIndex >= 0, "The locked win-x64 restore must restore the ReadyToRun dependency graph.");
        Assert.IsTrue(publishIndex > restoreIndex, "ReadyToRun dependencies must be restored before no-restore publish.");

        int publishFunctionStart = publishScript.IndexOf(
            "function Invoke-SelfContainedPublish",
            StringComparison.Ordinal);
        Assert.IsTrue(publishFunctionStart >= 0, "Invoke-SelfContainedPublish must remain the publish workflow owner.");
        int layoutFunctionStart = publishScript.IndexOf(
            "function Assert-SelfContainedPublishLayout",
            publishFunctionStart,
            StringComparison.Ordinal);
        Assert.IsTrue(layoutFunctionStart > publishFunctionStart, "The publish workflow boundary was not found.");
        string publishFunction = publishScript[publishFunctionStart..layoutFunctionStart];
        int appPublishStart = publishFunction.IndexOf("& dotnet publish $appProject", StringComparison.Ordinal);
        Assert.IsTrue(appPublishStart >= 0, "The main application publish command was not found.");
        int appPublishEnd = publishFunction.IndexOf(
            "if ($LASTEXITCODE -ne 0)",
            appPublishStart,
            StringComparison.Ordinal);
        Assert.IsTrue(appPublishEnd > appPublishStart, "The main application publish command boundary was not found.");
        string[] appPublishCommandLines = publishFunction[appPublishStart..appPublishEnd]
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(line => line.Trim())
            .ToArray();
        CollectionAssert.Contains(appPublishCommandLines, "--no-restore `");
        CollectionAssert.Contains(
            appPublishCommandLines,
            "--property:PublishProfile=WinX64SelfContained `");
    }

    [TestMethod]
    [TestCategory("ReleaseAcceptance")]
    public async Task PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput()
    {
        string repositoryRoot = FindRepositoryRoot();
        string validatorPath = Path.Combine(repositoryRoot, "scripts", "portable-package-layout.ps1");
        Assert.IsTrue(File.Exists(validatorPath), "The release layout validator must be present.");

        string appPublishDirectory = ResolveSelfContainedPublishDirectory("BMS_SCD_APP_PUBLISH_ROOT", "app");
        string updaterPublishDirectory = ResolveSelfContainedPublishDirectory("BMS_SCD_UPDATER_PUBLISH_ROOT", "updater");
        string stagingDirectory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ReleaseLayoutTests", Guid.NewGuid().ToString("N"));
        ExceptionDispatchInfo? primaryFailure = null;
        string? stagingCleanupFailure = null;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            foreach (string sourcePath in Directory.EnumerateFiles(appPublishDirectory, "*", SearchOption.AllDirectories))
            {
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

            ProcessStartInfo CreateValidatorStartInfo(string? command = null)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "pwsh",
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
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch (Exception exception)
            {
                stagingCleanupFailure =
                    $"staging directory cleanup failed for '{stagingDirectory}': "
                    + $"{exception.GetType().Name}: {exception.Message}";
            }
        }

        if (primaryFailure is not null)
        {
            if (!string.IsNullOrWhiteSpace(stagingCleanupFailure))
            {
                TryAttachSecondaryDiagnostic(
                    primaryFailure.SourceException,
                    "Secondary staging cleanup failure",
                    stagingCleanupFailure);
            }

            primaryFailure.Throw();
            return;
        }

        if (!string.IsNullOrWhiteSpace(stagingCleanupFailure))
        {
            Assert.Fail(stagingCleanupFailure);
        }
    }

    private static async Task RunValidatorProcess(ProcessStartInfo startInfo, string operation)
    {
        var processTimeout = TimeSpan.FromSeconds(60);
        var streamTimeout = TimeSpan.FromSeconds(5);
        var cleanupTimeout = TimeSpan.FromSeconds(5);
        var process = new Process { StartInfo = startInfo };
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        bool processStarted = false;
        int? processId = null;
        DateTime? processStartTime = null;
        var streamDeadline = new Stopwatch();
        ExceptionDispatchInfo? primaryFailure = null;
        TimeSpan failureElapsed = TimeSpan.Zero;
        bool stdoutCompletedAtFailure = false;
        bool stderrCompletedAtFailure = false;
        string cleanupResult = "not attempted because the process did not start";
        var secondaryFailures = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                throw new AssertFailedException($"{operation} process setup failed: Process.Start returned false.");
            }

            // The retained Process handle owns cleanup even when metadata access
            // fails immediately after a successful start.
            processStarted = true;
            streamDeadline.Restart();
            stdoutTask = process.StandardOutput.ReadToEndAsync();
            stderrTask = process.StandardError.ReadToEndAsync();
            ObserveLateStreamFault(stdoutTask);
            ObserveLateStreamFault(stderrTask);

            Exception? metadataFailure = null;
            try
            {
                processId = process.Id;
            }
            catch (Exception exception)
            {
                metadataFailure = new AssertFailedException(
                    $"{operation} process setup failed: PID capture failed: {exception.Message}",
                    exception);
            }

            try
            {
                processStartTime = process.StartTime;
            }
            catch (Exception exception)
            {
                AssertFailedException startTimeFailure = new(
                    $"{operation} process setup failed: StartTime capture failed: {exception.Message}",
                    exception);
                if (metadataFailure is null)
                {
                    metadataFailure = startTimeFailure;
                }
                else
                {
                    secondaryFailures.Add(
                        $"Process metadata capture failure: {startTimeFailure.Message}");
                }
            }

            if (metadataFailure is not null)
            {
                throw metadataFailure;
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(processTimeout);
            }
            catch (TimeoutException exception)
            {
                throw new AssertFailedException(
                    $"{operation} exceeded its {processTimeout.TotalSeconds:F0}-second process bound "
                    + $"(PID {processId}, elapsed {stopwatch.Elapsed.TotalSeconds:F1}s).",
                    exception);
            }

            int exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                throw new AssertFailedException(
                    $"{operation} exited with code {exitCode} "
                    + $"(PID {processId}, elapsed {stopwatch.Elapsed.TotalSeconds:F1}s).");
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(
                    Remaining(streamDeadline!, streamTimeout));
            }
            catch (TimeoutException exception)
            {
                throw new AssertFailedException(
                    $"{operation} exceeded its {streamTimeout.TotalSeconds:F0}-second redirected-stream bound "
                    + $"after exit (PID {processId}, elapsed {stopwatch.Elapsed.TotalSeconds:F1}s).",
                    exception);
            }
            catch (Exception exception)
            {
                throw new AssertFailedException(
                    $"{operation} redirected stream failed for PID {processId}: {exception.Message}",
                    exception);
            }
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
            failureElapsed = stopwatch.Elapsed;
            stdoutCompletedAtFailure = stdoutTask?.IsCompletedSuccessfully == true;
            stderrCompletedAtFailure = stderrTask?.IsCompletedSuccessfully == true;
        }
        finally
        {
            if (processStarted)
            {
                try
                {
                    (bool succeeded, string diagnostic) cleanup = await CleanupOwnedProcess(
                        process,
                        processId,
                        processStartTime,
                        cleanupTimeout);
                    cleanupResult = cleanup.diagnostic;
                    if (!cleanup.succeeded)
                    {
                        secondaryFailures.Add("Process cleanup failure: " + cleanup.diagnostic);
                    }
                }
                catch (Exception exception)
                {
                    cleanupResult =
                        $"root PID {processId?.ToString() ?? "<unknown>"} cleanup owner threw "
                        + $"{exception.GetType().Name}: {exception.Message}";
                    secondaryFailures.Add("Process cleanup failure: " + cleanupResult);
                }
            }

            Task[] streams = new Task?[] { stdoutTask, stderrTask }.OfType<Task>().ToArray();
            try
            {
                if (streams.Length > 0)
                {
                    await Task.WhenAll(streams).WaitAsync(
                        Remaining(streamDeadline, streamTimeout));
                }
            }
            catch (TimeoutException)
            {
                secondaryFailures.Add(
                    $"Redirected streams remained incomplete at the absolute {streamTimeout.TotalSeconds:F0}-second stream cutoff.");
            }
            catch (Exception exception)
            {
                secondaryFailures.Add(
                    $"Redirected stream failure observed after cleanup: {exception.GetType().Name}: {exception.Message}");
            }

            try
            {
                process.Dispose();
            }
            catch (Exception exception)
            {
                secondaryFailures.Add("Process handle cleanup failure: " + exception.Message);
            }
        }

        if (primaryFailure is null && secondaryFailures.Count == 0)
        {
            return;
        }

        if (primaryFailure is null)
        {
            failureElapsed = stopwatch.Elapsed;
            stdoutCompletedAtFailure = stdoutTask?.IsCompletedSuccessfully == true;
            stderrCompletedAtFailure = stderrTask?.IsCompletedSuccessfully == true;
        }

        string diagnostic;
        try
        {
            diagnostic = string.Join(
                Environment.NewLine,
                new[]
                {
                    "Process cleanup: " + cleanupResult,
                    DescribeValidatorStream(
                        "stdout", stdoutTask, stdoutCompletedAtFailure, processId, failureElapsed,
                        streamTimeout, cleanupResult),
                    DescribeValidatorStream(
                        "stderr", stderrTask, stderrCompletedAtFailure, processId, failureElapsed,
                        streamTimeout, cleanupResult)
                }.Concat(secondaryFailures));
        }
        catch (Exception exception)
        {
            diagnostic =
                $"Validator diagnostics construction failed: {exception.GetType().Name}: {exception.Message}";
        }

        if (primaryFailure is not null)
        {
            TryAttachSecondaryDiagnostic(
                primaryFailure.SourceException,
                "Secondary validator diagnostics",
                diagnostic,
                secondaryFailures);
            primaryFailure.Throw();
            return;
        }

        Assert.Fail(diagnostic);
    }

    /// <summary>
    /// Cleans up the validator root under its childless-command contract. The
    /// validator commands dot-source the PowerShell script and use only in-process
    /// PowerShell/.NET filesystem operations; they must not launch child processes.
    /// Therefore a root exit is the complete owned lifecycle, and this helper
    /// intentionally performs no descendant discovery or process-name fallback.
    /// </summary>
    private static async Task<(bool Succeeded, string Diagnostic)> CleanupOwnedProcess(
        Process process,
        int? processId,
        DateTime? processStartTime,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        (bool exited, string diagnostic) initial = ObserveRootProcess(
            process,
            processId,
            processStartTime);
        if (initial.exited)
        {
            return (
                true,
                $"root PID {processId?.ToString() ?? "<unknown>"} had already exited; "
                + "childless validator contract makes root exit sufficient");
        }

        string? terminationFailure = null;
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (Exception exception)
        {
            terminationFailure =
                $"root-only termination for PID {processId?.ToString() ?? "<unknown>"} failed: "
                + $"{exception.GetType().Name}: {exception.Message}";
        }

        TimeSpan remaining = Remaining(stopwatch, timeout);
        if (remaining <= TimeSpan.Zero)
        {
            terminationFailure ??=
                $"root-only termination for PID {processId?.ToString() ?? "<unknown>"} "
                + $"consumed the {timeout.TotalSeconds:F0}-second cleanup bound";
        }
        else
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(remaining);
            }
            catch (Exception exception)
            {
                string waitFailure =
                    $"root-only termination observation for PID {processId?.ToString() ?? "<unknown>"} failed: "
                    + $"{exception.GetType().Name}: {exception.Message}";
                terminationFailure = terminationFailure is null
                    ? waitFailure
                    : terminationFailure + "; " + waitFailure;
            }
        }

        (bool exited, string diagnostic) residual = ObserveRootProcess(
            process,
            processId,
            processStartTime);
        if (residual.exited)
        {
            return (
                true,
                terminationFailure is null
                    ? $"root PID {processId?.ToString() ?? "<unknown>"} exited after root-only termination"
                    : terminationFailure + $"; residual confirmation found root PID {processId?.ToString() ?? "<unknown>"} exited");
        }

        string failure = terminationFailure
            ?? $"root PID {processId?.ToString() ?? "<unknown>"} remained active after root-only termination";
        return (
            false,
            initial.diagnostic + "; " + failure + "; " + residual.diagnostic);
    }

    private static (bool Exited, string Diagnostic) ObserveRootProcess(
        Process process,
        int? processId,
        DateTime? processStartTime)
    {
        var diagnostics = new List<string>();
        bool hasExited;
        try
        {
            hasExited = process.HasExited;
        }
        catch (Exception exception)
        {
            hasExited = false;
            diagnostics.Add(
                $"retained handle HasExited could not be observed: {exception.GetType().Name}: {exception.Message}");
        }

        if (hasExited)
        {
            return (true, $"root PID {processId?.ToString() ?? "<unknown>"} has exited");
        }

        if (processId is int capturedProcessId)
        {
            try
            {
                int observedProcessId = process.Id;
                if (observedProcessId != capturedProcessId)
                {
                    diagnostics.Add(
                        $"root process identity changed from PID {capturedProcessId} to PID {observedProcessId}");
                }
                else
                {
                    diagnostics.Add($"PID identity matched {capturedProcessId}");
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"PID identity evidence could not be observed: {exception.GetType().Name}: {exception.Message}");
            }
        }
        else
        {
            diagnostics.Add("PID was not captured");
        }

        if (processStartTime is DateTime capturedStartTime)
        {
            try
            {
                DateTime observedStartTime = process.StartTime;
                if (observedStartTime != capturedStartTime)
                {
                    diagnostics.Add(
                        $"root start identity changed from {capturedStartTime:O} to {observedStartTime:O}");
                }
                else
                {
                    diagnostics.Add($"StartTime identity matched {capturedStartTime:O}");
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"StartTime identity evidence could not be observed: "
                    + $"{exception.GetType().Name}: {exception.Message}");
            }
        }
        else
        {
            diagnostics.Add("StartTime was not captured");
        }

        string rootLabel = $"root PID {processId?.ToString() ?? "<unknown>"}";
        string diagnostic = rootLabel + " remains active";
        if (diagnostics.Count > 0)
        {
            diagnostic += "; identity evidence: " + string.Join("; ", diagnostics);
        }

        return (false, diagnostic);
    }

    private static void ObserveLateStreamFault(Task streamTask)
    {
        // A deadline may expire before a redirected reader faults. Observe that
        // late fault explicitly without changing the primary lifecycle failure.
        _ = streamTask.ContinueWith(
            static task => _ = task.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    private static void TryAttachSecondaryDiagnostic(
        Exception primaryException,
        string key,
        string diagnostic,
        ICollection<string>? fallbackDiagnostics = null)
    {
        try
        {
            primaryException.Data[key] = diagnostic;
        }
        catch (Exception exception)
        {
            fallbackDiagnostics?.Add(
                $"Secondary diagnostic attachment failed for '{key}': "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string DescribeValidatorStream(
        string name,
        Task<string>? streamTask,
        bool completedAtFailure,
        int? processId,
        TimeSpan elapsed,
        TimeSpan bound,
        string cleanupResult)
    {
        string context = $"PID {processId?.ToString() ?? "<unknown>"}, elapsed {elapsed.TotalSeconds:F1}s, "
            + $"{bound.TotalSeconds:F0}-second bound; cleanup: {cleanupResult}";
        if (streamTask?.IsCompletedSuccessfully == true)
        {
            string timing = completedAtFailure ? "completed" : "completed after cleanup";
            return $"{name} {timing} ({context}): "
                + (string.IsNullOrEmpty(streamTask.Result) ? "<empty>" : streamTask.Result);
        }

        string state = streamTask is null
            ? "was not started"
            : streamTask.IsFaulted
                ? "failed: " + streamTask.Exception!.GetBaseException().Message
                : streamTask.IsCanceled ? "was canceled" : "remained incomplete";
        return $"{name} stream {state} ({context}).";
    }

    private static TimeSpan Remaining(Stopwatch stopwatch, TimeSpan timeout)
    {
        TimeSpan remaining = timeout - stopwatch.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static void AssertProfileValue(XDocument profile, string propertyName, string expectedValue)
    {
        XElement property = profile
            .Descendants()
            .SingleOrDefault(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.Ordinal))
            ?? throw new AssertFailedException($"Publish profile property is missing: {propertyName}");
        Assert.AreEqual(expectedValue, property.Value, propertyName);
    }

    private static void AssertProfileValueAbsent(XDocument profile, string propertyName)
    {
        Assert.IsFalse(
            profile.Descendants().Any(element => string.Equals(
                element.Name.LocalName,
                propertyName,
                StringComparison.Ordinal)),
            $"Publish profile property must remain absent: {propertyName}");
    }

    private static string FindRepositoryRoot()
    {
        string? directoryPath = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (File.Exists(Path.Combine(directoryPath, "BeMusicSeeker.sln")))
            {
                return directoryPath!;
            }

            directoryPath = Directory.GetParent(directoryPath)?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string ResolveReleaseOutputDirectory()
    {
        var testOutputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        string targetFramework = testOutputDirectory.Name;
        string? configuration = testOutputDirectory.Parent?.Name;
        string? platform = testOutputDirectory.Parent?.Parent?.Name;
        if (string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(platform))
        {
            throw new AssertFailedException("The test output path does not contain configuration and platform segments.");
        }

        return Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "bin", platform, configuration, targetFramework);
    }

    private static string ResolveSelfContainedPublishDirectory(string environmentVariableName, string childDirectoryName)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            Assert.Inconclusive($"Self-contained publish verification requires {environmentVariableName} for {childDirectoryName}.");
        }

        string path = configuredPath!;
        if (!Directory.Exists(path))
        {
            Assert.Inconclusive("Self-contained publish output is missing: " + path);
        }
        return path;
    }
}
