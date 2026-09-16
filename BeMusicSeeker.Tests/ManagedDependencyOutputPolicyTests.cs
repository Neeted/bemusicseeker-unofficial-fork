using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ManagedDependencyOutputPolicyTests
{
    [TestMethod]
    public void ApplicationProjectUsesHostManagedDependencyLayout()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"));
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

        XDocument config = XDocument.Load(Path.Combine(repositoryRoot, "app.config"));
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
        string dependencyGraph = File.ReadAllText(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.deps.json"));
        StringAssert.Contains(dependencyGraph, "\"Newtonsoft.Json/13.0.4\"");
        StringAssert.Contains(dependencyGraph, "\"ManagedBass/4.0.2\"");
        StringAssert.Contains(dependencyGraph, "\"ManagedBass.Mix/4.0.2\"");
        StringAssert.Contains(dependencyGraph, "\"ManagedBass.Fx/4.0.2\"");
        StringAssert.Contains(dependencyGraph, "\"ManagedBass.Enc/4.0.2\"");
        StringAssert.Contains(dependencyGraph, "\"ManagedBass.Asio/4.0.2\"");
        StringAssert.Contains(dependencyGraph, "\"ManagedBass.Wasapi/4.0.2\"");
        Assert.IsFalse(dependencyGraph.Contains("Un4seen.Bass/", StringComparison.Ordinal));

        foreach (string dependencyName in new[]
        {
            "Livet.Core.dll",
            "Livet.EventListeners.dll",
            "Livet.Messaging.dll",
            "Livet.Mvvm.dll",
            "Microsoft.Xaml.Behaviors.dll",
            "Newtonsoft.Json.dll",
            "NLog.dll",
            "NVorbis.dll",
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
        }
        Assert.IsFalse(
            File.Exists(Path.Combine(releaseOutputDirectory, "Bass.Net.dll")),
            "The final host layout must not deploy the retired BASS.NET assembly.");

        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "libs", "x64", "7z.dll")),
            "The package-provided x64 7z native asset must be staged under the existing library native owner path.");
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
            .Single(item => string.Equals((string?)item.Attribute("Include"), "native\\EverythingBridge_x64.dll", StringComparison.Ordinal));
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

    [TestMethod]
    public void TestProjectOwnsLockedTestHostDependencyGraph()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "BeMusicSeeker.Tests.csproj"));
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
        using JsonDocument lockDocument = JsonDocument.Parse(File.ReadAllText(lockPath));
        JsonElement dependencies = lockDocument.RootElement.GetProperty("dependencies");
        JsonElement targetDependencies = dependencies.GetProperty("net10.0-windows7.0");
        JsonElement sdk = targetDependencies.GetProperty("Microsoft.NET.Test.Sdk");
        Assert.AreEqual("18.8.1", sdk.GetProperty("resolved").GetString());
        Assert.AreEqual("Direct", sdk.GetProperty("type").GetString());
    }

    [TestMethod]
    public void LockOwningProjectsDeclareCanonicalWinX64RestoreGraph()
    {
        string repositoryRoot = FindRepositoryRoot();
        var lockOwners = new[]
        {
            new
            {
                ProjectPath = "BeMusicSeeker.csproj",
                LockPath = "packages.lock.json",
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
            XDocument project = XDocument.Load(projectPath);
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
            using JsonDocument lockDocument = JsonDocument.Parse(File.ReadAllText(lockPath));
            JsonElement dependencies = lockDocument.RootElement.GetProperty("dependencies");
            Assert.IsTrue(
                dependencies.TryGetProperty(lockOwner.BaseTarget, out _),
                $"The lock file must retain its base target graph: {lockOwner.LockPath}");
            Assert.IsTrue(
                dependencies.TryGetProperty(lockOwner.RidTarget, out JsonElement ridDependencies),
                $"The lock file must retain its win-x64 target graph: {lockOwner.LockPath}");
            JsonElement sqliteNativePackage = ridDependencies.GetProperty("SourceGear.sqlite3");
            Assert.AreEqual("Transitive", sqliteNativePackage.GetProperty("type").GetString());
            Assert.AreEqual("3.53.3", sqliteNativePackage.GetProperty("resolved").GetString());
        }
    }

    [TestMethod]
    public void PackageVersionsAreCentrallyOwnedAndAnalyzersStayOutOfRuntimeOutput()
    {
        string repositoryRoot = FindRepositoryRoot();
        var expectedVersions = new[]
        {
            new { Id = "Microsoft.NET.Test.Sdk", Version = "18.8.1" },
            new { Id = "MSTest.TestAdapter", Version = "3.6.4" },
            new { Id = "MSTest.TestFramework", Version = "3.6.4" },
            new { Id = "LivetCask.Core", Version = "4.0.2" },
            new { Id = "LivetCask.EventListeners", Version = "4.0.2" },
            new { Id = "LivetCask.Mvvm", Version = "4.0.2" },
            new { Id = "Microsoft.Xml.SgmlReader", Version = "1.8.30" },
            new { Id = "NLog", Version = "6.1.4" },
            new { Id = "Newtonsoft.Json", Version = "13.0.4" },
            new { Id = "ManagedBass", Version = "4.0.2" },
            new { Id = "ManagedBass.Mix", Version = "4.0.2" },
            new { Id = "ManagedBass.Fx", Version = "4.0.2" },
            new { Id = "ManagedBass.Enc", Version = "4.0.2" },
            new { Id = "ManagedBass.Asio", Version = "4.0.2" },
            new { Id = "ManagedBass.Wasapi", Version = "4.0.2" },
            new { Id = "Roslynator.Analyzers", Version = "4.15.0" },
            new { Id = "Roslynator.CodeAnalysis.Analyzers", Version = "4.15.0" },
            new { Id = "Roslynator.Formatting.Analyzers", Version = "4.15.0" },
            new { Id = "sqlite-net-pcl", Version = "1.11.285" },
            new { Id = "SQLitePCLRaw.bundle_e_sqlite3", Version = "3.0.4" },
            new { Id = "SevenZipExtractor", Version = "1.0.19" },
            new { Id = "NVorbis", Version = "0.10.5" },
        }.ToDictionary(item => item.Id, item => item.Version, StringComparer.Ordinal);

        XDocument centralPackages = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"));
        XElement centralRoot = centralPackages.Root ?? throw new AssertFailedException("Central package props has no root element.");
        Assert.AreEqual(
            "true",
            (string)centralRoot.Elements("PropertyGroup").Elements("ManagePackageVersionsCentrally").Single(),
            "Central package management must be enabled for the migration dependency graph.");
        var centralVersions = centralRoot
            .Elements("ItemGroup")
            .Elements("PackageVersion")
            .ToDictionary(
                package => (string)package.Attribute("Include")!,
                package => (string)package.Attribute("Version")!,
                StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(expectedVersions.Keys.ToArray(), centralVersions.Keys.ToArray());
        foreach (var expected in expectedVersions)
        {
            Assert.AreEqual(
                expected.Key.StartsWith("ManagedBass", StringComparison.Ordinal)
                    ? "[4.0.2]"
                    : expected.Value,
                centralVersions[expected.Key]);
        }

        var projectPackages = new[]
        {
            new
            {
                ProjectPath = Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"),
                LockPath = Path.Combine(repositoryRoot, "packages.lock.json"),
                PackageIds = new[]
                {
                    "LivetCask.Core",
                    "LivetCask.EventListeners",
                    "LivetCask.Mvvm",
                    "Microsoft.Xml.SgmlReader",
                    "Newtonsoft.Json",
                    "NLog",
                    "Roslynator.Analyzers",
                    "Roslynator.CodeAnalysis.Analyzers",
                    "Roslynator.Formatting.Analyzers",
                    "sqlite-net-pcl",
                    "SQLitePCLRaw.bundle_e_sqlite3",
                    "SevenZipExtractor",
                    "NVorbis",
                    "ManagedBass",
                    "ManagedBass.Mix",
                    "ManagedBass.Fx",
                    "ManagedBass.Enc",
                    "ManagedBass.Asio",
                    "ManagedBass.Wasapi"
                }
            },
            new
            {
                ProjectPath = Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "BeMusicSeeker.Tests.csproj"),
                LockPath = Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "packages.lock.json"),
                PackageIds = new[]
                {
                    "Microsoft.NET.Test.Sdk",
                    "MSTest.TestAdapter",
                    "MSTest.TestFramework",
                    "sqlite-net-pcl",
                    "SQLitePCLRaw.bundle_e_sqlite3",
                    "SevenZipExtractor",
                    "NVorbis",
                    "ManagedBass",
                    "ManagedBass.Mix",
                    "ManagedBass.Fx",
                    "ManagedBass.Enc",
                    "ManagedBass.Asio",
                    "ManagedBass.Wasapi"
                }
            }
        };

        foreach (var projectPackage in projectPackages)
        {
            XDocument project = XDocument.Load(projectPackage.ProjectPath);
            XElement projectRoot = project.Root ?? throw new AssertFailedException("Package project has no root element.");
            foreach (XElement packageReference in projectRoot.Elements("ItemGroup").Elements("PackageReference"))
            {
                string packageId = (string)packageReference.Attribute("Include")!;
                Assert.IsTrue(
                    expectedVersions.ContainsKey(packageId),
                    $"Package {packageId} must be declared by Directory.Packages.props.");
                Assert.IsNull(
                    packageReference.Attribute("Version"),
                    $"Package {packageId} must not carry a project-local version.");
            }

            Assert.IsTrue(File.Exists(projectPackage.LockPath), $"Lock file is missing: {projectPackage.LockPath}");
            using JsonDocument lockDocument = JsonDocument.Parse(File.ReadAllText(projectPackage.LockPath));
            JsonElement target = lockDocument.RootElement.GetProperty("dependencies").GetProperty("net10.0-windows7.0");
            foreach (string packageId in projectPackage.PackageIds)
            {
                Assert.IsTrue(target.TryGetProperty(packageId, out JsonElement dependency), $"Lock entry is missing: {packageId}");
                Assert.AreEqual("Direct", dependency.GetProperty("type").GetString());
                Assert.AreEqual(expectedVersions[packageId], dependency.GetProperty("resolved").GetString());
                if (packageId.StartsWith("ManagedBass", StringComparison.Ordinal))
                {
                    Assert.AreEqual("[4.0.2, 4.0.2]", dependency.GetProperty("requested").GetString());
                }
            }
        }

        XDocument appProject = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"));
        foreach (string analyzerId in new[]
        {
            "Roslynator.Analyzers",
            "Roslynator.CodeAnalysis.Analyzers",
            "Roslynator.Formatting.Analyzers"
        })
        {
            XElement analyzerReference = appProject
                .Root!
                .Elements("ItemGroup")
                .Elements("PackageReference")
                .Single(reference => string.Equals((string?)reference.Attribute("Include"), analyzerId, StringComparison.Ordinal));
            Assert.AreEqual("all", (string?)analyzerReference.Element("PrivateAssets"));
            Assert.AreEqual(
                "runtime; build; native; contentfiles; analyzers; buildtransitive",
                (string?)analyzerReference.Element("IncludeAssets"));
        }

        string releaseOutputDirectory = ResolveReleaseOutputDirectory();
        Assert.IsFalse(
            Directory.EnumerateFiles(releaseOutputDirectory, "Roslynator*.dll", SearchOption.AllDirectories).Any(),
            "Analyzer assemblies must not be copied to the application runtime output.");
        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "libs", "Livet.dll")) ||
            File.Exists(Path.Combine(repositoryRoot, "libs", "Livet.Extensions.dll")),
            "The tracked legacy Livet binaries must not remain beside the package-owned WPF project.");
        Assert.IsFalse(
            File.Exists(Path.Combine(releaseOutputDirectory, "libs", "x64", "OggVorbis.NET64.dll")) ||
            File.Exists(Path.Combine(releaseOutputDirectory, "OggVorbis.NET64.dll")),
            "The retired OggVorbis native and managed assets must not remain in the release output.");

    }

    [TestMethod]
    public void NativeComplianceNoticeEntriesAreGreenAndSeparated()
    {
        string repositoryRoot = FindRepositoryRoot();
        string english = NormalizeLineEndings(File.ReadAllText(Path.Combine(repositoryRoot, "ThirdPartyNotices.txt")));
        string japanese = NormalizeLineEndings(File.ReadAllText(Path.Combine(repositoryRoot, "ThirdPartyNotices.ja.txt")));
        string englishCore = ExtractSection(english, "1) BASS core and official add-ons", "1a) BASSASIO");
        string englishAsio = ExtractSection(english, "1a) BASSASIO", "1b) BASS_FX");
        string englishFx = ExtractSection(english, "1b) BASS_FX", "2) ManagedBass");
        string japaneseCore = ExtractSection(japanese, "1) BASS core と公式 add-on", "1a) BASSASIO");
        string japaneseAsio = ExtractSection(japanese, "1a) BASSASIO", "1b) BASS_FX");
        string japaneseFx = ExtractSection(japanese, "1b) BASS_FX", "2) ManagedBass");

        var englishSections = new[]
        {
            (Text: englishCore, Component: "Component: bass.dll, bassmix.dll, bassenc.dll, basswasapi.dll (x64)", Notice: "Notice Summary: third_party/licenses/01-BASS-NOTICE.txt", Copyright: "Copyright: Un4seen Developments Ltd."),
            (Text: englishAsio, Component: "Component: bassasio.dll (x64)", Notice: "Notice Summary: third_party/licenses/01a-BASSASIO-NOTICE.txt", Copyright: "Copyright: Un4seen Developments Ltd."),
            (Text: englishFx, Component: "Component: bass_fx.dll (x64)", Notice: "Notice Summary: third_party/licenses/01b-BASS_FX-NOTICE.txt", Copyright: "Copyright: (: JOBnik! :) [Arthur Aminov, ISRAEL]")
        };
        foreach (var section in englishSections)
        {
            StringAssert.Contains(section.Text, "Status: GREEN");
            StringAssert.Contains(section.Text, section.Component);
            StringAssert.Contains(section.Text, section.Notice);
            StringAssert.Contains(section.Text, section.Copyright);
        }

        var japaneseSections = new[]
        {
            (Text: japaneseCore, Component: "コンポーネント: bass.dll, bassmix.dll, bassenc.dll, basswasapi.dll (x64)", Notice: "Notice Summary: third_party/licenses/01-BASS-NOTICE.txt", Copyright: "著作権所有者: Un4seen Developments Ltd."),
            (Text: japaneseAsio, Component: "コンポーネント: bassasio.dll (x64)", Notice: "Notice Summary: third_party/licenses/01a-BASSASIO-NOTICE.txt", Copyright: "著作権所有者: Un4seen Developments Ltd."),
            (Text: japaneseFx, Component: "コンポーネント: bass_fx.dll (x64)", Notice: "Notice Summary: third_party/licenses/01b-BASS_FX-NOTICE.txt", Copyright: "著作権所有者: (: JOBnik! :) [Arthur Aminov, ISRAEL]")
        };
        foreach (var section in japaneseSections)
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
    }

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
            new { Name = "bassmix.dll", Version = "2.4.12.0", Api = "0x02040C00", Hash = "F782CAE8090700A456C9E7AEAA7770C3B90CB60A1E765C4B3CBAE739D3B4D58D", FileVersionPrefix = "2.4.12" },
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
    }

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

        return Path.Combine(FindRepositoryRoot(), "bin", platform, configuration, targetFramework);
    }
}
