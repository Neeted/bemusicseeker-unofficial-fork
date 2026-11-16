using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>固定Everything SDKを用いるnative query試験だけの準備・実行順を保持します。</summary>
internal static class NativeQueryVerification
{
    /// <summary>SDKを先に検査し、compile・query・任意fixtureを同一期限で実行します。製品の配布入力は変更しません。</summary>
    internal static async Task ExecuteAsync(string repository, bool buildPathIdentityFixture, ExecuteCommand execute, ProcessDeadline deadline)
    {
        string sdk = Path.Combine(repository, "native", "Everything3_x64.dll");
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sdk))) != "BE25B01C73BBF359B50DDF30255133225F93B4BC40A8D208173319373BCDAA5C"
            || FileVersionInfo.GetVersionInfo(sdk).FileVersion != "3.0.0.9")
        {
            throw new InvalidDataException("固定SDK 3.0.0.9の版/hashが一致しません。結果fixtureの再点検が必要です。");
        }
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        ProcessCompletion discovery = await ExecuteAsync(vswhere,
            ["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"]);
        string msbuild = discovery.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidOperationException("MSVC v145 / MSBuildが必要です。");
        string output = Path.Combine(repository, "native", "EverythingBridge", "obj", "query-tests");
        Directory.CreateDirectory(output);
        string source = SecurityElement.Escape(Path.Combine(repository, "native", "EverythingBridge", "tests", "EverythingBridgeQueryTests.cpp"))
            ?? throw new InvalidOperationException("native試験sourceのパスがありません。");
        string projectText = ProjectTemplate.Replace("SOURCE_PATH", source, StringComparison.Ordinal);
        string project = Path.Combine(output, "QueryTests.vcxproj");
        File.WriteAllText(project, projectText, new UTF8Encoding(false));
        await CompileAsync(project);
        await ExecuteAsync(Path.Combine(output, "EverythingBridgeQueryTests.exe"), [sdk]);
        if (buildPathIdentityFixture)
        {
            string fixtureBase = Path.Combine(output, "path-identity-fixture");
            string fixtureNative = Path.Combine(fixtureBase, "native");
            Directory.CreateDirectory(fixtureNative);
            string fixtureProject = Path.Combine(output, "PathIdentityFixture.vcxproj");
            string fixtureText = projectText
                .Replace("<ConfigurationType>Application</ConfigurationType>", "<ConfigurationType>DynamicLibrary</ConfigurationType>", StringComparison.Ordinal)
                .Replace("<OutDir>$(ProjectDir)</OutDir>", @"<OutDir>$(ProjectDir)path-identity-fixture\native\</OutDir>", StringComparison.Ordinal)
                .Replace(@"<IntDir>$(ProjectDir)intermediate\</IntDir>", @"<IntDir>$(ProjectDir)fixture-intermediate\</IntDir>", StringComparison.Ordinal)
                .Replace("<TargetName>EverythingBridgeQueryTests</TargetName>", "<TargetName>EverythingBridge_x64</TargetName>", StringComparison.Ordinal)
                .Replace("NDEBUG;UNICODE;", "BMS_PATH_IDENTITY_FIXTURE;NDEBUG;UNICODE;", StringComparison.Ordinal);
            File.WriteAllText(fixtureProject, fixtureText, new UTF8Encoding(false));
            File.Copy(sdk, Path.Combine(fixtureNative, "Everything3_x64.dll"), overwrite: true);
            await CompileAsync(fixtureProject);
            Console.WriteLine("BMS_EVERYTHING_PATH_IDENTITY_FIXTURE=" + fixtureBase);
        }

        async Task CompileAsync(string path)
        {
            await ExecuteAsync(msbuild, [path, "/t:Rebuild", "/p:Configuration=Release", "/p:Platform=x64", "/nologo", "/verbosity:minimal"]);
        }

        async Task<ProcessCompletion> ExecuteAsync(string executable, IReadOnlyList<string> arguments)
        {
            ProcessCompletion result = await execute(executable, arguments, deadline);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"native query検証が失敗しました。exit={result.ExitCode}\n{result.StandardError}");
            }
            return result;
        }
    }

    private const string ProjectTemplate = """
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
""";
}
