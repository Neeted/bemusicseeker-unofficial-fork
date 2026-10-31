$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$sourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $sourceRoot 'scripts/verification-process-lifecycle.ps1')
$signal = [Threading.ManualResetEventSlim]::new($false)
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('verification-mtp-' + [Guid]::NewGuid().ToString('N'))
$primaryFailure = $null
[void][IO.Directory]::CreateDirectory($fixtureRoot)
try {
    $guard = [VerificationPostStartFaultGuard]::new($signal)
    . (Join-Path $sourceRoot 'scripts/verify-refactor.ps1') -InternalTestGuard $guard
    $sdk = (Get-Content -LiteralPath (Join-Path $sourceRoot 'global.json') -Raw | ConvertFrom-Json).'msbuild-sdks'.'MSTest.Sdk'
    # filterの故障でも通常テストや専用環境テストへ到達しない、使い捨ての小型MTPアプリを使う。
    $project = Join-Path $fixtureRoot 'Fixture.csproj'
    [IO.File]::WriteAllText($project, @"
<Project Sdk="MSTest.Sdk/$sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TestingExtensionsProfile>None</TestingExtensionsProfile>
    <EnableMicrosoftTestingExtensionsTrxReport>true</EnableMicrosoftTestingExtensionsTrxReport>
    <EnableMicrosoftTestingExtensionsCrashDump>true</EnableMicrosoftTestingExtensionsCrashDump>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
"@)
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'Tests.cs'), @'
using Microsoft.VisualStudio.TestTools.UnitTesting;
[assembly: Parallelize(Workers = 1, Scope = ExecutionScope.MethodLevel)]
namespace MtpFixture;
[TestClass]
public sealed class SelectionTests
{
    [TestMethod]
    [DataRow(3)]
    [DataRow(7)]
    public void Selected(int value) => Assert.IsTrue(value is 3 or 7);
    [TestMethod]
    public void Excluded() => Assert.Fail("選択外のメソッドが実行されました。");
}
'@)
    $deadline = Resolve-VerificationDeadlinePair -TimeoutSeconds 45
    Invoke-VerificationMonitoredCommand -Label 'MTP fixture build' -CommandPath 'dotnet' `
        -Arguments @('build', $project, '--disable-build-servers', '-p:UseSharedCompilation=false') `
        -WorkingDirectory $sourceRoot -DiagnosticsDirectory (Join-Path $fixtureRoot 'build') `
        -DeadlinePolicy $deadline 6>$null | Out-Null
    $functionalTestAssemblyPath = Join-Path $fixtureRoot 'bin/Debug/net10.0/Fixture.dll'
    $settings = Join-Path $fixtureRoot 'selected.runsettings'
    $selected = 'MtpFixture.SelectionTests.Selected'
    Write-MSTestParallelRunSettings -Path $settings -Workers 1 -TestCaseFilter "FullyQualifiedName=$selected"
    $results = Join-Path $fixtureRoot 'results'
    $arguments = Get-TestArguments -DiagnosticsDirectory $results -RunSettingsPath $settings
    Invoke-VerificationMonitoredCommand -Label 'MTP fixture test' -CommandPath ([IO.Path]::ChangeExtension($functionalTestAssemblyPath, '.exe')) `
        -Arguments $arguments -WorkingDirectory $sourceRoot -DiagnosticsDirectory $results `
        -DeadlinePolicy $deadline 6>$null | Out-Null
    $trx = Join-Path $results 'results.trx'
    [void](Assert-VerificationTestOutcomes -ResultPaths @($trx) -SelectedFqns @($selected))
    $rows = @(Read-VerificationTestOutcomes -ResultPaths @($trx))
    [pscustomobject]@{
        Names = @($rows | ForEach-Object FullyQualifiedName)
        Outcomes = @($rows | ForEach-Object Outcome)
        TestIds = @($rows | ForEach-Object TestId)
    } | ConvertTo-Json -Compress
}
catch { $primaryFailure = $_; throw }
finally {
    $signal.Dispose()
    try {
        $resolvedFixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
        $ownedPrefix = Join-Path ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) 'verification-mtp-'
        if (-not $resolvedFixtureRoot.StartsWith($ownedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw '所有するMTP fixture以外は削除できません。'
        }
        Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
    }
    catch {
        if ($null -eq $primaryFailure) { throw }
        Write-Warning "MTP fixture cleanup: $($_.Exception.Message)"
    }
}
