using System.Xml.Linq;
using static BeMusicSeeker.TestSupport.Components.UpdateDeploymentTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterDeploymentBoundaryTests
{
    /// <summary>
    /// 通常ビルドへツールを混在させず、指定する本体・ツールの公開プロファイルを分担して自己完結配布の成立条件を壊す誤りを検出する。
    /// </summary>
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
}
