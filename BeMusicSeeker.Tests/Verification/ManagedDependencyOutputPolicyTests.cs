using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ManagedDependencyOutputPolicyTests
{
    /// <summary>
    /// 実アプリの構築設定と出力を照合し、ホスト管理の依存関係を旧framework資産や競合する隣接managed DLLへ戻す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ApplicationProjectUsesHostManagedDependencyLayout()
    {
        string repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker", "BeMusicSeeker.csproj"));
        XElement projectRoot = project.Root ?? throw new AssertFailedException("Application project XML has no root element.");

        Assert.AreEqual(
            "net10.0-windows",
            (string)projectRoot.Elements("PropertyGroup").Elements("TargetFramework").Single(),
            "The application must target the Windows .NET 10 runtime.");
        Assert.IsFalse(
            projectRoot.Elements("Target").Any(target => string.Equals(
                (string?)target.Attribute("Name"),
                "ApplyManagedDependencyOutputPolicy",
                StringComparison.Ordinal)),
            "The legacy libs relocation target must not remain in the project boundary.");
        Assert.IsFalse(
            projectRoot.Elements("Target").Any(target => string.Equals(
                (string?)target.Attribute("Name"),
                "RemoveLegacyManagedDependencyRootOutput",
                StringComparison.Ordinal)),
            "The legacy managed DLL deletion target must not remain in the project boundary.");

        var config = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker", "app.config"));
        Assert.IsFalse(
            config.Descendants(XName.Get("probing", "urn:schemas-microsoft-com:asm.v1")).Any(),
            "The runtime must not depend on Framework private probing.");
        Assert.IsFalse(config.Descendants("startup").Any(), "Framework supportedRuntime selection must not remain in the .NET 10 app config.");
        Assert.IsFalse(config.Descendants("runtime").Any(), "Framework runtime switches must not remain in the .NET 10 app config.");

        string releaseOutputDirectory = ResolveReleaseOutputDirectory();
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.exe")),
            "The Release application output must exist before this layout behavior test runs.");
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.deps.json")),
            "The host dependency graph must be emitted beside the application.");
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.runtimeconfig.json")),
            "The host runtime configuration must be emitted beside the application.");
        using var appLockDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker", "packages.lock.json")));
        JsonElement appLockTarget = appLockDocument.RootElement.GetProperty("dependencies").GetProperty("net10.0-windows7.0");
        using var depsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.deps.json")));
        string runtimeTargetName = depsDocument.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()
            ?? throw new AssertFailedException("The application deps runtime target has no name.");
        JsonElement depsTarget = depsDocument.RootElement.GetProperty("targets").GetProperty(runtimeTargetName);
        foreach (string packageId in new[]
        {
            "Newtonsoft.Json", "ManagedBass", "ManagedBass.Mix", "ManagedBass.Fx",
            "ManagedBass.Enc", "ManagedBass.Asio", "ManagedBass.Wasapi"
        })
        {
            string resolved = appLockTarget.GetProperty(packageId).GetProperty("resolved").GetString()
                ?? throw new AssertFailedException($"The locked package has no resolved version: {packageId}");
            Assert.IsTrue(depsTarget.TryGetProperty(packageId + "/" + resolved, out _), packageId);
        }
        Assert.IsFalse(depsTarget.EnumerateObject().Any(package =>
            package.Name.StartsWith("Un4seen.Bass/", StringComparison.Ordinal)));

        foreach (string dependencyName in new[]
        {
            "Livet.Core.dll",
            "Livet.EventListeners.dll",
            "Livet.Messaging.dll",
            "Livet.Mvvm.dll",
            "Microsoft.Xaml.Behaviors.dll",
            "Newtonsoft.Json.dll",
            "NLog.dll",
            "SevenZipExtractor.dll",
            "SgmlReaderDll.dll",
            "ManagedBass.dll",
            "ManagedBass.Mix.dll",
            "ManagedBass.Fx.dll",
            "ManagedBass.Enc.dll",
            "ManagedBass.Asio.dll",
            "ManagedBass.Wasapi.dll"
        })
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(releaseOutputDirectory, dependencyName)),
                $"The host layout must place {dependencyName} beside the application.");
            Assert.IsTrue(depsTarget.EnumerateObject().Any(package =>
                package.Value.TryGetProperty("runtime", out JsonElement runtime) &&
                runtime.EnumerateObject().Any(asset => string.Equals(Path.GetFileName(asset.Name), dependencyName, StringComparison.Ordinal))),
                $"The host graph must select a runtime asset for {dependencyName}.");
        }
        Assert.IsFalse(
            File.Exists(Path.Combine(releaseOutputDirectory, "Bass.Net.dll")),
            "The final host layout must not deploy the retired BASS.NET assembly.");
        Assert.IsFalse(
            File.Exists(Path.Combine(releaseOutputDirectory, "NVorbis.dll")),
            "The native Vorbis bridge replaces the retired managed decoder package.");

        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "libs", "x64", "7z.dll")),
            "The package-provided x64 7z native asset must be staged under the existing library native owner path.");
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "libs", "x64", "bms_vorbis.dll")),
            "The process-owned Vorbis bridge must be staged beside the other x64 native assets.");
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "native", "Everything3_x64.dll")),
            "The Everything SDK x64 native asset must be staged under the application native owner path.");
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "native", "EverythingBridge_x64.dll")),
            "The Everything bridge x64 native asset must be staged beside its SDK sibling.");
        Assert.AreEqual(
            2,
            Directory.GetFiles(Path.Combine(releaseOutputDirectory, "native"), "Everything*_x64.dll", SearchOption.TopDirectoryOnly).Length,
            "The application native owner must contain exactly one copy of each Everything x64 asset.");
        XElement everythingBridgeAsset = projectRoot
            .Elements("ItemGroup")
            .Elements("None")
            .Single(item => string.Equals((string?)item.Attribute("Include"), "..\\native\\EverythingBridge_x64.dll", StringComparison.Ordinal));
        Assert.IsNull(
            everythingBridgeAsset.Attribute("Condition"),
            "The bridge and SDK must be a mandatory, deterministic native ship set.");
        Assert.IsFalse(
            File.Exists(Path.Combine(releaseOutputDirectory, "x64", "7z.dll")),
            "The package's root x64 native copy must not remain beside the deterministic library path.");

        foreach (string removedAddonName in new[] { "NLog.Database.dll", "NLog.WindowsEventLog.dll" })
        {
            Assert.IsFalse(
                File.Exists(Path.Combine(releaseOutputDirectory, removedAddonName)),
                $"The NLog 6 host layout must not deploy removed target package {removedAddonName}.");
        }

        XElement nlogReference = projectRoot
            .Elements("ItemGroup")
            .Elements("PackageReference")
            .Single(reference => string.Equals((string?)reference.Attribute("Include"), "NLog", StringComparison.Ordinal));
        Assert.IsNull(nlogReference.Attribute("Version"));
        Assert.IsFalse(
            projectRoot.Elements("ItemGroup").Elements("PackageReference").Any(reference =>
                string.Equals((string?)reference.Attribute("Include"), "NLog.Database", StringComparison.Ordinal) ||
                string.Equals((string?)reference.Attribute("Include"), "NLog.WindowsEventLog", StringComparison.Ordinal)),
            "NLog 6 core must be the only NLog package reference.");
        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "libs", "NLog.dll")),
            "The tracked legacy NLog binary must not remain beside the SDK project.");

        XElement newtonsoftReference = projectRoot
            .Elements("ItemGroup")
            .Elements("PackageReference")
            .Single(reference => string.Equals((string?)reference.Attribute("Include"), "Newtonsoft.Json", StringComparison.Ordinal));
        Assert.IsNull(newtonsoftReference.Attribute("Version"));
        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "libs", "Newtonsoft.Json.dll")),
            "Newtonsoft.Json must be supplied by the SDK package output, not a tracked HintPath binary.");
        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "libs", "IniLibrary.dll")) ||
            File.Exists(Path.Combine(repositoryRoot, "libs", "SgmlReaderDll.dll")) ||
            File.Exists(Path.Combine(repositoryRoot, "libs", "System.Collections.Immutable.dll")),
            "Legacy helper binaries must be supplied by the package/runtime graph or removed, not tracked beside the SDK project.");

        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "libs", "System.Resources.Extensions.dll")),
            "System.Resources.Extensions must not be supplied by a tracked HintPath binary; the .NET 10 WindowsDesktop runtime pack supplies the publish asset.");
    }

    /// <summary>
    /// テストプロジェクトが自分のlocked restoreとテストhost依存を持ち、アプリ出力への依存で検証環境を成立させる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void TestProjectOwnsLockedTestHostDependencyGraph()
    {
        string repositoryRoot = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "BeMusicSeeker.Tests.csproj"));
        XElement projectRoot = project.Root ?? throw new AssertFailedException("Test project XML has no root element.");

        Assert.AreEqual(
            "true",
            (string)projectRoot.Elements("PropertyGroup").Elements("RestorePackagesWithLockFile").Single(),
            "The test project must own a packages.lock.json for deterministic testhost restore.");
        XElement testSdkReference = projectRoot
            .Elements("ItemGroup")
            .Elements("PackageReference")
            .Single(reference => string.Equals((string?)reference.Attribute("Include"), "Microsoft.NET.Test.Sdk", StringComparison.Ordinal));
        Assert.IsNull((object?)testSdkReference.Attribute("Version"));

        string lockPath = Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "packages.lock.json");
        Assert.IsTrue(File.Exists(lockPath), "The test project lock file must be tracked beside its project.");
        using var lockDocument = JsonDocument.Parse(File.ReadAllText(lockPath));
        JsonElement dependencies = lockDocument.RootElement.GetProperty("dependencies");
        JsonElement targetDependencies = dependencies.GetProperty("net10.0-windows7.0");
        JsonElement sdk = targetDependencies.GetProperty("Microsoft.NET.Test.Sdk");
        string sdkVersion = sdk.GetProperty("resolved").GetString()
            ?? throw new AssertFailedException("The test SDK lock entry has no resolved version.");
        using var assetsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "obj", "project.assets.json")));
        Assert.IsTrue(assetsDocument.RootElement.GetProperty("targets").GetProperty("net10.0-windows/win-x64")
            .TryGetProperty("Microsoft.NET.Test.Sdk/" + sdkVersion, out _));
        Assert.AreEqual("Direct", sdk.GetProperty("type").GetString());
    }

    /// <summary>
    /// lockを持つ各プロジェクトでwin-x64の復元入力とグラフを揃え、別RIDや旧依存グラフを消費する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void LockOwningProjectsDeclareCanonicalWinX64RestoreGraph()
    {
        string repositoryRoot = FindRepositoryRoot();
        var lockOwners = new[]
        {
            new
            {
                ProjectPath = Path.Combine("BeMusicSeeker", "BeMusicSeeker.csproj"),
                LockPath = Path.Combine("BeMusicSeeker", "packages.lock.json"),
                BaseTarget = "net10.0-windows7.0",
                RidTarget = "net10.0-windows7.0/win-x64"
            },
            new
            {
                ProjectPath = Path.Combine("BeMusicSeeker.Tests", "BeMusicSeeker.Tests.csproj"),
                LockPath = Path.Combine("BeMusicSeeker.Tests", "packages.lock.json"),
                BaseTarget = "net10.0-windows7.0",
                RidTarget = "net10.0-windows7.0/win-x64"
            },
            new
            {
                ProjectPath = Path.Combine("tools", "chart-info-compare", "ChartInfoCompare.csproj"),
                LockPath = Path.Combine("tools", "chart-info-compare", "packages.lock.json"),
                BaseTarget = "net10.0",
                RidTarget = "net10.0/win-x64"
            },
            new
            {
                ProjectPath = Path.Combine("tools", "chart-info-export", "ChartInfoExport.csproj"),
                LockPath = Path.Combine("tools", "chart-info-export", "packages.lock.json"),
                BaseTarget = "net10.0",
                RidTarget = "net10.0/win-x64"
            }
        };

        foreach (var lockOwner in lockOwners)
        {
            string projectPath = Path.Combine(repositoryRoot, lockOwner.ProjectPath);
            var project = XDocument.Load(projectPath);
            XElement projectRoot = project.Root ?? throw new AssertFailedException($"Project XML has no root element: {projectPath}");
            Assert.AreEqual(
                "true",
                (string)projectRoot.Elements("PropertyGroup").Elements("RestorePackagesWithLockFile").Single(),
                $"The lock-owning project must enable lock file restore: {lockOwner.ProjectPath}");
            Assert.AreEqual(
                "win-x64",
                (string)projectRoot.Elements("PropertyGroup").Elements("RuntimeIdentifiers").Single(),
                $"The lock-owning project must keep ordinary IDE restore on the canonical win-x64 graph: {lockOwner.ProjectPath}");
            Assert.IsFalse(
                projectRoot.Elements("PropertyGroup").Elements("RuntimeIdentifier").Any(),
                $"The project must not use singular RuntimeIdentifier because it also changes build output semantics: {lockOwner.ProjectPath}");

            string lockPath = Path.Combine(repositoryRoot, lockOwner.LockPath);
            Assert.IsTrue(File.Exists(lockPath), $"Lock file is missing: {lockPath}");
            using var lockDocument = JsonDocument.Parse(File.ReadAllText(lockPath));
            JsonElement dependencies = lockDocument.RootElement.GetProperty("dependencies");
            Assert.IsTrue(
                dependencies.TryGetProperty(lockOwner.BaseTarget, out _),
                $"The lock file must retain its base target graph: {lockOwner.LockPath}");
            Assert.IsTrue(
                dependencies.TryGetProperty(lockOwner.RidTarget, out JsonElement ridDependencies),
                $"The lock file must retain its win-x64 target graph: {lockOwner.LockPath}");
            JsonElement sqliteNativePackage = ridDependencies.GetProperty("SourceGear.sqlite3");
            Assert.AreEqual("Transitive", sqliteNativePackage.GetProperty("type").GetString());
            string targetFramework = (string)projectRoot.Elements("PropertyGroup").Elements("TargetFramework").Single();
            string assetsPath = Path.Combine(Path.GetDirectoryName(projectPath)
                ?? throw new AssertFailedException("Project directory is missing."), "obj", "project.assets.json");
            using var assetsDocument = JsonDocument.Parse(File.ReadAllText(assetsPath));
            JsonElement assetsTargets = assetsDocument.RootElement.GetProperty("targets");
            JsonElement baseSqlitePackage = dependencies.GetProperty(lockOwner.BaseTarget).GetProperty("SourceGear.sqlite3");
            Assert.AreEqual("Transitive", baseSqlitePackage.GetProperty("type").GetString());
            foreach ((string AssetsTarget, JsonElement LockedPackage) target in new[]
            {
                (targetFramework, baseSqlitePackage),
                (targetFramework + "/win-x64", sqliteNativePackage)
            })
            {
                string resolved = target.LockedPackage.GetProperty("resolved").GetString()
                    ?? throw new AssertFailedException("SourceGear lock entry has no resolved version.");
                JsonElement selected = assetsTargets.GetProperty(target.AssetsTarget).GetProperty("SourceGear.sqlite3/" + resolved);
                Assert.AreEqual("package", selected.GetProperty("type").GetString());
            }
        }
    }

    /// <summary>
    /// 中央指定からlock・正規assets・depsの依存集合を照合し、局所version指定、選択資産と実出力の不一致、退役済み音声資産の残留を検出する。
    /// </summary>
    [TestMethod]
    public void PackageVersionsAreCentrallyOwnedAndRetiredAssembliesStayOutOfRuntimeOutput()
    {
        string repositoryRoot = FindRepositoryRoot();
        var centralPackages = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"));
        XElement centralRoot = centralPackages.Root ?? throw new AssertFailedException("Central package props has no root element.");
        Assert.AreEqual(
            "true",
            (string)centralRoot.Elements("PropertyGroup").Elements("ManagePackageVersionsCentrally").Single());
        var centralVersions = centralRoot.Elements("ItemGroup").Elements("PackageVersion")
            .ToDictionary(
                package => (string?)package.Attribute("Include") ?? throw new AssertFailedException("Central package ID is missing."),
                package => (string?)package.Attribute("Version") ?? throw new AssertFailedException("Central package version is missing."),
                StringComparer.Ordinal);
        // 音声ABI正本のpinは通常依存のversion期待と分け、必要六categoryを独立して固定する。
        string[] managedBassPackages =
        [
            "ManagedBass", "ManagedBass.Mix", "ManagedBass.Fx",
            "ManagedBass.Enc", "ManagedBass.Asio", "ManagedBass.Wasapi"
        ];
        foreach (string packageId in managedBassPackages)
        {
            Assert.AreEqual("[4.0.2]", centralVersions[packageId], packageId);
        }
        foreach (string projectDirectory in new[] { "BeMusicSeeker", "BeMusicSeeker.Tests" })
        {
            string projectPath = Path.Combine(repositoryRoot, projectDirectory, projectDirectory + ".csproj");
            XElement projectRoot = XDocument.Load(projectPath).Root
                ?? throw new AssertFailedException("Package project has no root element.");
            string targetFramework = (string)projectRoot.Elements("PropertyGroup").Elements("TargetFramework").Single();
            using var lockDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, projectDirectory, "packages.lock.json")));
            JsonElement lockedTarget = lockDocument.RootElement.GetProperty("dependencies").GetProperty("net10.0-windows7.0");
            using var assetsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, projectDirectory, "obj", "project.assets.json")));
            JsonElement selectedTarget = assetsDocument.RootElement.GetProperty("targets").GetProperty(targetFramework + "/win-x64");
            Assert.IsTrue(assetsDocument.RootElement.GetProperty("project").GetProperty("restore")
                .GetProperty("centralPackageVersionsManagementEnabled").GetBoolean(), projectDirectory);
            JsonElement evaluatedFramework = assetsDocument.RootElement.GetProperty("project").GetProperty("frameworks").GetProperty(targetFramework);
            string outputDirectory = projectDirectory == "BeMusicSeeker" ? ResolveReleaseOutputDirectory() : AppContext.BaseDirectory;
            using var depsDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDirectory, projectDirectory + ".deps.json")));
            string runtimeTargetName = depsDocument.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()
                ?? throw new AssertFailedException("The deps runtime target has no name.");
            JsonElement depsTarget = depsDocument.RootElement.GetProperty("targets").GetProperty(runtimeTargetName);
            foreach (XElement packageReference in projectRoot.Elements("ItemGroup").Elements("PackageReference"))
            {
                string packageId = (string?)packageReference.Attribute("Include")
                    ?? throw new AssertFailedException("Package reference ID is missing.");
                Assert.IsTrue(centralVersions.TryGetValue(packageId, out string? declaration), packageId);
                Assert.IsNull(packageReference.Attribute("Version"), packageId);
                Assert.IsNull(packageReference.Attribute("VersionOverride"), packageId);
                Assert.IsFalse(packageReference.Elements("Version").Any(), packageId);
                Assert.IsFalse(packageReference.Elements("VersionOverride").Any(), packageId);
                JsonElement dependency = lockedTarget.GetProperty(packageId);
                Assert.AreEqual("Direct", dependency.GetProperty("type").GetString(), packageId);
                string resolved = dependency.GetProperty("resolved").GetString()
                    ?? throw new AssertFailedException($"The locked package has no resolved version: {packageId}");
                AssertCentralDeclarationMatchesLock(declaration ?? throw new AssertFailedException("Central declaration is missing."), dependency, resolved, packageId);
                JsonElement evaluatedDependency = evaluatedFramework.GetProperty("dependencies").GetProperty(packageId);
                Assert.IsTrue(evaluatedDependency.GetProperty("versionCentrallyManaged").GetBoolean(), packageId);
                Assert.AreEqual(declaration, evaluatedFramework.GetProperty("centralPackageVersions").GetProperty(packageId).GetString(), packageId);
                Assert.AreEqual(dependency.GetProperty("requested").GetString(), evaluatedDependency.GetProperty("version").GetString(), packageId);
                if (managedBassPackages.Contains(packageId, StringComparer.Ordinal))
                {
                    Assert.AreEqual("[4.0.2, 4.0.2]", dependency.GetProperty("requested").GetString(), packageId);
                    Assert.AreEqual("4.0.2", resolved, packageId);
                }
                Assert.IsTrue(selectedTarget.TryGetProperty(packageId + "/" + resolved, out _), packageId);
                Assert.IsTrue(depsTarget.TryGetProperty(packageId + "/" + resolved, out _), packageId);
            }
            // 期待集合は消費結果の自己列挙ではなく、復元のlocked package graphから定める。
            foreach (JsonProperty lockedPackage in lockedTarget.EnumerateObject())
            {
                if (lockedPackage.Value.GetProperty("type").GetString() == "Project")
                {
                    continue;
                }
                string resolved = lockedPackage.Value.GetProperty("resolved").GetString()
                    ?? throw new AssertFailedException($"The locked package has no resolved version: {lockedPackage.Name}");
                string packageKey = lockedPackage.Name + "/" + resolved;
                Assert.IsTrue(selectedTarget.TryGetProperty(packageKey, out JsonElement selectedPackage), packageKey);
                Assert.AreEqual("package", selectedPackage.GetProperty("type").GetString(), packageKey);
                Assert.IsTrue(depsTarget.TryGetProperty(packageKey, out _), packageKey);
            }
            AssertRuntimeOutputsMatchSelectedAssets(assetsDocument.RootElement, selectedTarget, depsTarget, outputDirectory);
        }

        string releaseOutputDirectory = ResolveReleaseOutputDirectory();
        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "libs", "Livet.dll")) ||
            File.Exists(Path.Combine(repositoryRoot, "libs", "Livet.Extensions.dll")),
            "The tracked legacy Livet binaries must not remain beside the package-owned WPF project.");
        Assert.IsFalse(
            File.Exists(Path.Combine(releaseOutputDirectory, "libs", "x64", "OggVorbis.NET64.dll")) ||
            File.Exists(Path.Combine(releaseOutputDirectory, "OggVorbis.NET64.dll")),
            "The retired OggVorbis native and managed assets must not remain in the release output.");

    }

    /// <summary>
    /// 配布するnative音声資産ごとの権利者・notice・実licenseの区別を両言語で確認し、異なる権利条件の混同や配布記載の欠落を検出する。
    /// </summary>
    [TestMethod]
    public void NativeComplianceNoticeEntriesAreGreenAndSeparated()
    {
        string repositoryRoot = FindRepositoryRoot();
        string english = NormalizeLineEndings(File.ReadAllText(Path.Combine(repositoryRoot, "ThirdPartyNotices.txt")));
        string japanese = NormalizeLineEndings(File.ReadAllText(Path.Combine(repositoryRoot, "ThirdPartyNotices.ja.txt")));
        string englishCore = ExtractSection(english, "1) BASS core and official add-ons", "1a) BASSASIO");
        string englishAsio = ExtractSection(english, "1a) BASSASIO", "1b) BASS_FX");
        string englishFx = ExtractSection(english, "1b) BASS_FX", "2) ManagedBass");
        string englishVorbis = ExtractSection(english, "19) Xiph.org libogg and libvorbis", "20) Fonts");
        string japaneseCore = ExtractSection(japanese, "1) BASS core と公式 add-on", "1a) BASSASIO");
        string japaneseAsio = ExtractSection(japanese, "1a) BASSASIO", "1b) BASS_FX");
        string japaneseFx = ExtractSection(japanese, "1b) BASS_FX", "2) ManagedBass");
        string japaneseVorbis = ExtractSection(japanese, "19) Xiph.org libogg と libvorbis", "20) フォントファイル");

        (string Text, string Component, string Notice, string Copyright)[] englishSections = new[]
        {
            (Text: englishCore, Component: "Component: bass.dll, bassmix.dll, bassenc.dll, basswasapi.dll (x64)", Notice: "Notice Summary: third_party/licenses/01-BASS-NOTICE.txt", Copyright: "Copyright: Un4seen Developments Ltd."),
            (Text: englishAsio, Component: "Component: bassasio.dll (x64)", Notice: "Notice Summary: third_party/licenses/01a-BASSASIO-NOTICE.txt", Copyright: "Copyright: Un4seen Developments Ltd."),
            (Text: englishFx, Component: "Component: bass_fx.dll (x64)", Notice: "Notice Summary: third_party/licenses/01b-BASS_FX-NOTICE.txt", Copyright: "Copyright: (: JOBnik! :) [Arthur Aminov, ISRAEL]")
        };
        foreach ((string Text, string Component, string Notice, string Copyright) section in englishSections)
        {
            StringAssert.Contains(section.Text, "Status: GREEN");
            StringAssert.Contains(section.Text, section.Component);
            StringAssert.Contains(section.Text, section.Notice);
            StringAssert.Contains(section.Text, section.Copyright);
        }

        (string Text, string Component, string Notice, string Copyright)[] japaneseSections = new[]
        {
            (Text: japaneseCore, Component: "コンポーネント: bass.dll, bassmix.dll, bassenc.dll, basswasapi.dll (x64)", Notice: "Notice Summary: third_party/licenses/01-BASS-NOTICE.txt", Copyright: "著作権所有者: Un4seen Developments Ltd."),
            (Text: japaneseAsio, Component: "コンポーネント: bassasio.dll (x64)", Notice: "Notice Summary: third_party/licenses/01a-BASSASIO-NOTICE.txt", Copyright: "著作権所有者: Un4seen Developments Ltd."),
            (Text: japaneseFx, Component: "コンポーネント: bass_fx.dll (x64)", Notice: "Notice Summary: third_party/licenses/01b-BASS_FX-NOTICE.txt", Copyright: "著作権所有者: (: JOBnik! :) [Arthur Aminov, ISRAEL]")
        };
        foreach ((string Text, string Component, string Notice, string Copyright) section in japaneseSections)
        {
            StringAssert.Contains(section.Text, "ステータス: GREEN");
            StringAssert.Contains(section.Text, section.Component);
            StringAssert.Contains(section.Text, section.Notice);
            StringAssert.Contains(section.Text, section.Copyright);
        }

        Assert.IsFalse(englishCore.Contains("bassasio.dll", StringComparison.Ordinal));
        Assert.IsFalse(englishCore.Contains("bass_fx.dll", StringComparison.Ordinal));
        Assert.IsFalse(japaneseCore.Contains("bassasio.dll", StringComparison.Ordinal));
        Assert.IsFalse(japaneseCore.Contains("bass_fx.dll", StringComparison.Ordinal));
        StringAssert.Contains(englishAsio, "bassasio.dll");
        StringAssert.Contains(japaneseAsio, "bassasio.dll");
        Assert.IsFalse(englishFx.Contains("Copyright: Un4seen", StringComparison.Ordinal));
        Assert.IsFalse(japaneseFx.Contains("著作権所有者: Un4seen", StringComparison.Ordinal));
        StringAssert.Contains(englishVorbis, "Status: GREEN");
        StringAssert.Contains(englishVorbis, "Component: bms_vorbis.dll (x64; statically linked libogg, libvorbis and libvorbisfile)");
        StringAssert.Contains(englishVorbis, "third_party/licenses/19a-libogg-BSD-3-Clause.txt");
        StringAssert.Contains(englishVorbis, "third_party/licenses/19b-libvorbis-BSD-3-Clause.txt");
        StringAssert.Contains(japaneseVorbis, "ステータス: GREEN");
        StringAssert.Contains(japaneseVorbis, "コンポーネント: bms_vorbis.dll (x64、libogg・libvorbis・libvorbisfileを静的リンク)");
        StringAssert.Contains(japaneseVorbis, "third_party/licenses/19a-libogg-BSD-3-Clause.txt");
        StringAssert.Contains(japaneseVorbis, "third_party/licenses/19b-libvorbis-BSD-3-Clause.txt");

        StringAssert.Contains(english, "Conditionally releasable with minor remediation (YELLOW):\n(None)\n\nReady for release (GREEN):");
        StringAssert.Contains(japanese, "追加対応を行うことにより条件付きでリリース可能（YELLOW）:\n(なし)\n\n準備完了（GREEN）:");
        StringAssert.Contains(english, "BASS core and official add-ons (bass.dll, bassmix.dll, bassenc.dll, basswasapi.dll),\n  BASSASIO, BASS_FX, ManagedBass 4.0.2,");
        StringAssert.Contains(japanese, "BASS core と公式 add-on（bass.dll, bassmix.dll, bassenc.dll, basswasapi.dll）、\n  BASSASIO、BASS_FX、ManagedBass 4.0.2、");
        StringAssert.Contains(english, "future commercial or monetized release requires a new upstream license");
        StringAssert.Contains(japanese, "将来、商用または収益化する場合はアップストリーム条項を再確認します。");

        foreach (string noticeName in new[]
        {
            "01-BASS-NOTICE.txt",
            "01a-BASSASIO-NOTICE.txt",
            "01b-BASS_FX-NOTICE.txt"
        })
        {
            string noticePath = Path.Combine(repositoryRoot, "third_party", "licenses", noticeName);
            Assert.IsTrue(File.Exists(noticePath), noticePath);
            string notice = NormalizeLineEndings(File.ReadAllText(noticePath));
            StringAssert.Contains(notice, "Notice Summary (not authoritative full license text)");
            Assert.IsFalse(notice.Contains("License Text:", StringComparison.Ordinal));
        }

        foreach (string licenseName in new[]
        {
            "19a-libogg-BSD-3-Clause.txt",
            "19b-libvorbis-BSD-3-Clause.txt"
        })
        {
            string licensePath = Path.Combine(repositoryRoot, "third_party", "licenses", licenseName);
            Assert.IsTrue(File.Exists(licensePath), licensePath);
            StringAssert.Contains(NormalizeLineEndings(File.ReadAllText(licensePath)), "Xiph.org Foundation");
        }
    }

    /// <summary>
    /// 正本で固定したnative BASS群とVorbis bridgeの実バイト・版・ABIを照合し、未検証の差替えや異なるCPU資産の混入を検出する。
    /// </summary>
    [TestMethod]
    public void NativeBassVersionAndHashSetRemainsFixed()
    {
        string repositoryRoot = FindRepositoryRoot();
        string dependencySpec = NormalizeLineEndings(File.ReadAllText(Path.Combine(
            repositoryRoot,
            "devdocs",
            "spec",
            "runtime",
            "audio-dependencies.md")));
        var expected = new[]
        {
            new { Name = "bass.dll", Version = "2.4.18.3", Api = "0x02041203", Hash = "FEBB2CF1882D554C3A958280777DA0B69F07DE6E262DF271DE11C56E4A54AFD4", FileVersionPrefix = "2.4.18" },
            new { Name = "bassmix.dll", Version = "2.4.13.0", Api = "0x02040D00", Hash = "3A1777CD14C0FC6E2D6F879CE9B66CB60CD1C6607F344D70D2434164526063AB", FileVersionPrefix = "2.4.13" },
            new { Name = "bassenc.dll", Version = "2.4.17.0", Api = "0x02041100", Hash = "9D8EE8D750DEF93E927E62E35D02A4CC8457C509CFA561C47AED3381691F51F8", FileVersionPrefix = "2.4.17" },
            new { Name = "basswasapi.dll", Version = "2.4.4.1", Api = "0x02040401", Hash = "6F0869C11431E01F759FBE1CD6080299C833C519EB8AB1FEAE12106907B1FBD1", FileVersionPrefix = "2.4.4" },
            new { Name = "bass_fx.dll", Version = "2.4.12.6", Api = "0x02040C06", Hash = "A6E1847EEF52D882B4137AF514D834C2E220DACEB417C821D1E502FB7A34C84A", FileVersionPrefix = "2.4" },
            new { Name = "bassasio.dll", Version = "1.4.3.0", Api = "0x01040300", Hash = "73BF79C8ECCD63DEA8EB3E3E9B5FFE6F9406DEB9BBCCCC7557CA54F5013B4B96", FileVersionPrefix = "1.4.3" }
        };

        foreach (var component in expected)
        {
            string dllPath = Path.Combine(repositoryRoot, "vendor", "native", "x64", component.Name);
            Assert.IsTrue(File.Exists(dllPath), dllPath);
            StringAssert.Contains(dependencySpec, $"| `{component.Name}` | {component.Version} / `{component.Api}` |");
            Assert.AreEqual(component.Hash, (GetFileHash(dllPath)), component.Name);
            string? fileVersion = FileVersionInfo.GetVersionInfo(dllPath).FileVersion;
            Assert.IsTrue(
                fileVersion?.StartsWith(component.FileVersionPrefix, StringComparison.Ordinal) == true,
                $"{component.Name} file version was {fileVersion}.");
        }

        string vorbisBridgePath = Path.Combine(repositoryRoot, "vendor", "native", "x64", "bms_vorbis.dll");
        Assert.IsTrue(File.Exists(vorbisBridgePath), vorbisBridgePath);
        Assert.AreEqual(
            "403287F5E79580C4987F5018AF7FFE5B180F63819B10D09EA744AF5465D360C9",
            GetFileHash(vorbisBridgePath));
        using (FileStream stream = File.OpenRead(vorbisBridgePath))
        using (var peReader = new PEReader(stream))
        {
            Assert.AreEqual(Machine.Amd64, peReader.PEHeaders.CoffHeader.Machine, vorbisBridgePath);
        }
        StringAssert.Contains(dependencySpec, "`bms_vorbis.dll` | C ABI 1");
    }

    /// <summary>
    /// ManagedBassの原文MIT licenseを固定内容で保持し、native BASSのnotice要約や退役したwrapperの権利表記と混同する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void FinalManagedBassLicenseAndNativeNoticeAreTrackedSeparately()
    {
        string repositoryRoot = FindRepositoryRoot();
        string managedBassLicensePath = Path.Combine(repositoryRoot, "third_party", "licenses", "02-ManagedBass-MIT.txt");
        Assert.IsTrue(File.Exists(managedBassLicensePath), managedBassLicensePath);
        string managedBassLicense = File.ReadAllText(managedBassLicensePath);
        string canonicalManagedBassLicense = managedBassLicense
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        Assert.AreEqual(
            "41810CB2403489DB4FB5B2F961B78DC3629CE5B9DF06251D939A89ED3FB05063",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                canonicalManagedBassLicense))));
        StringAssert.Contains(managedBassLicense, "The MIT License");
        StringAssert.Contains(managedBassLicense, "Mathew Sachin");

        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "third_party", "licenses", "02-BASS.NET-NOTICE.txt")),
            "The retired managed wrapper notice must not remain in the current license inventory.");
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(repositoryRoot, "third_party", "licenses", "01-BASS-NOTICE.txt")),
            "Notice Summary (not authoritative full license text)");

        foreach (string noticeName in new[] { "ThirdPartyNotices.txt", "ThirdPartyNotices.ja.txt" })
        {
            string notice = File.ReadAllText(Path.Combine(repositoryRoot, noticeName));
            StringAssert.Contains(notice, "ManagedBass");
            Assert.IsFalse(notice.Contains("Bass.Net.dll", StringComparison.Ordinal));
            Assert.IsFalse(notice.Contains("BASS.NET", StringComparison.Ordinal));
        }
    }

    /// <summary>現在使う裸versionと単一exact指定だけをlockへ照合し、未対応の範囲を別の指定へ読み替えません。</summary>
    private static void AssertCentralDeclarationMatchesLock(string declaration, JsonElement dependency, string resolved, string packageId)
    {
        bool exact = declaration.StartsWith("[", StringComparison.Ordinal) && declaration.EndsWith("]", StringComparison.Ordinal);
        string version = exact ? declaration[1..^1] : declaration;
        Assert.IsTrue(Version.TryParse(version, out Version? requestedVersion), $"Unsupported central version: {packageId}={declaration}");
        Assert.IsTrue(Version.TryParse(resolved, out Version? resolvedVersion), $"Unsupported locked version: {packageId}={resolved}");
        Assert.AreEqual(exact ? $"[{version}, {version}]" : $"[{version}, )", dependency.GetProperty("requested").GetString(), packageId);
        if (exact)
        {
            Assert.AreEqual(version, resolved, packageId);
        }
        else
        {
            Assert.IsTrue((resolvedVersion ?? throw new AssertFailedException("Resolved version is missing."))
                >= (requestedVersion ?? throw new AssertFailedException("Requested version is missing.")), packageId);
        }
    }

    /// <summary>正規win-x64 graphの実managed runtime資産を一意に特定し、depsの選択と通常build出力のバイトを照合します。</summary>
    private static void AssertRuntimeOutputsMatchSelectedAssets(
        JsonElement assetsRoot,
        JsonElement selectedTarget,
        JsonElement depsTarget,
        string outputDirectory)
    {
        foreach (JsonProperty package in selectedTarget.EnumerateObject())
        {
            if (package.Value.GetProperty("type").GetString() != "package")
            {
                continue;
            }
            Assert.IsTrue(depsTarget.TryGetProperty(package.Name, out JsonElement consumed), package.Name);
            if (!consumed.TryGetProperty("runtime", out JsonElement runtime))
            {
                // meta packageと、競合解消後にこのpackageから選択されなかった候補に出力を要求しない。
                continue;
            }
            foreach (JsonProperty asset in runtime.EnumerateObject())
            {
                if (Path.GetFileName(asset.Name) == "_._")
                {
                    continue;
                }
                Assert.AreEqual(".dll", Path.GetExtension(asset.Name), $"Unsupported managed runtime asset: {asset.Name}");
                Assert.IsTrue(package.Value.TryGetProperty("runtime", out JsonElement selectedRuntime) &&
                    selectedRuntime.TryGetProperty(asset.Name, out _), $"The selected deps asset is absent from the canonical assets graph: {package.Name}/{asset.Name}");
                string libraryPath = assetsRoot.GetProperty("libraries").GetProperty(package.Name).GetProperty("path").GetString()
                    ?? throw new AssertFailedException($"Package library path is missing: {package.Name}");
                string[] sourcePaths = assetsRoot.GetProperty("packageFolders").EnumerateObject()
                    .Select(folder => Path.Combine(folder.Name, libraryPath.Replace('/', Path.DirectorySeparatorChar), asset.Name.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(File.Exists)
                    .ToArray();
                Assert.AreEqual(1, sourcePaths.Length, $"The selected runtime asset must resolve uniquely: {package.Name}/{asset.Name}");
                string outputPath = Path.Combine(outputDirectory, Path.GetFileName(asset.Name));
                Assert.IsTrue(File.Exists(outputPath), outputPath);
                Assert.AreEqual(GetFileHash(sourcePaths[0]), GetFileHash(outputPath), outputPath);
            }
        }
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

    private static string ExtractSection(string text, string startMarker, string endMarker)
    {
        int start = text.IndexOf(startMarker, StringComparison.Ordinal);
        int end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Section marker was not found: {startMarker}");
        Assert.IsTrue(end > start, $"Section end marker was not found: {endMarker}");
        return text[start..end];
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string GetFileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

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
}
