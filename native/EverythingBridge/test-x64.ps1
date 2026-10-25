#requires -Version 7.0
# native変更時の追加検証。通常C#ビルドにはC++コンパイラーを要求しない。
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repositoryRoot 'scripts/verification-process-lifecycle.ps1')

$sdkPath = Join-Path $repositoryRoot 'native/Everything3_x64.dll'
if ((Get-FileHash -LiteralPath $sdkPath -Algorithm SHA256).Hash -ne 'BE25B01C73BBF359B50DDF30255133225F93B4BC40A8D208173319373BCDAA5C' -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($sdkPath).FileVersion -ne '3.0.0.9') {
    throw '固定SDK 3.0.0.9の版/hashが一致しません。結果fixtureの再点検が必要です。'
}
$vswhere = Join-Path "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer" 'vswhere.exe'
$msbuildCandidates = @(& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
if ($LASTEXITCODE -ne 0 -or $msbuildCandidates.Count -eq 0) { throw 'MSVC v145 / MSBuild が必要です。' }
$msbuild = $msbuildCandidates[0]

# 専用の小さい生成projectをobj内へ置き、ソリューション・配布入力へ加えない。
$output = Join-Path $PSScriptRoot 'obj/query-tests'
[void](New-Item -ItemType Directory -Path $output -Force)
$source = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'tests/EverythingBridgeQueryTests.cpp'))
$project = Join-Path $output 'QueryTests.vcxproj'
$projectText = @'
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations"><ProjectConfiguration Include="Release|x64"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration></ItemGroup>
  <PropertyGroup Label="Globals"><WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
  <PropertyGroup Label="Configuration"><ConfigurationType>Application</ConfigurationType><PlatformToolset>v145</PlatformToolset><UseDebugLibraries>false</UseDebugLibraries><CharacterSet>Unicode</CharacterSet></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
  <PropertyGroup><OutDir>$(ProjectDir)</OutDir><IntDir>$(ProjectDir)intermediate\</IntDir><TargetName>EverythingBridgeQueryTests</TargetName></PropertyGroup>
  <ItemDefinitionGroup>
    <ClCompile><WarningLevel>Level3</WarningLevel><SDLCheck>true</SDLCheck><ConformanceMode>true</ConformanceMode><LanguageStandard>stdcpp17</LanguageStandard><RuntimeLibrary>MultiThreaded</RuntimeLibrary><Optimization>MaxSpeed</Optimization><PreprocessorDefinitions>NDEBUG;UNICODE;_UNICODE;%(PreprocessorDefinitions)</PreprocessorDefinitions><AdditionalOptions>/utf-8 %(AdditionalOptions)</AdditionalOptions></ClCompile>
    <Link><SubSystem>Console</SubSystem></Link>
  </ItemDefinitionGroup>
  <ItemGroup><ClCompile Include="SOURCE_PATH" /></ItemGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
</Project>
'@
[IO.File]::WriteAllText($project, $projectText.Replace('SOURCE_PATH', $source).Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
$deadline = Resolve-VerificationDeadlinePair -TimeoutSeconds 180
$owned = [Collections.Generic.List[object]]::new()
$primaryFailure = $null
try {
    foreach ($step in @(
        @{ Name = 'compile'; File = $msbuild; Arguments = @($project, '/t:Rebuild', '/p:Configuration=Release', '/p:Platform=x64', '/nologo', '/verbosity:minimal') },
        @{ Name = 'test'; File = (Join-Path $output 'EverythingBridgeQueryTests.exe'); Arguments = @($sdkPath) }
    )) {
        $diagnostics = Join-Path $output $step.Name
        $started = Start-VerificationRedirectedProcess -FileName $step.File -Arguments $step.Arguments -WorkingDirectory $repositoryRoot -DeadlinePolicy $deadline -DiagnosticsDirectory $diagnostics -OwnedProcessRecords $owned
        $result = Complete-VerificationRedirectedProcess -Started $started -DiagnosticsDirectory $diagnostics -DeadlinePolicy $deadline -LifecycleName "native-query-$($step.Name)"
        Write-Host $result.StandardOutput
        if ($result.StandardError) { Write-Host $result.StandardError }
        [void](Assert-VerificationProcessResult -Result $result -Label $step.Name -RequireSuccess -TimeoutSeconds 180)
    }
}
catch {
    $primaryFailure = $_.Exception
    throw
}
finally {
    try {
        Stop-VerificationOwnedProcessRecords -StartedProcesses $owned -CleanupDeadlineUtc $deadline.CleanupDeadlineUtc
    }
    catch {
        if ($null -eq $primaryFailure) { throw }
        $primaryFailure.Data['NativeQueryCleanupFailure'] = $_.Exception.ToString()
        Write-Warning "native検証の後片付けにも失敗しました: $($_.Exception.Message)"
    }
}
