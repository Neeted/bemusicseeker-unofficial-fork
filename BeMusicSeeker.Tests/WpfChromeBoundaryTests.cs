using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfChromeBoundaryTests
{
    [TestMethod]
    public void MainWindowUsesNativeChromeAndPreservesTerminalCaptionRoutes()
    {
        string xamlPath = Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml");
        string source = File.ReadAllText(xamlPath);
        string codeBehind = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.cs"));
        XDocument document = XDocument.Load(xamlPath);
        XElement window = document.Root;
        XElement windowChrome = document.Descendants().Single(element => element.Name.LocalName == "WindowChrome");
        XElement captionButtonStyle = document.Descendants().Single(element =>
            element.Name.LocalName == "Style"
            && string.Equals(
                (string)element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "Key"),
                "CaptionButtonStyleKey",
                StringComparison.Ordinal));
        XElement windowChromeFrame = document.Descendants().Single(element =>
            element.Name.LocalName == "Border"
            && string.Equals((string)element.Attribute("Name"), "windowChromeFrame", StringComparison.Ordinal));
        XElement frameStyle = windowChromeFrame.Elements().Single(element => element.Name.LocalName == "Border.Style").Elements().Single();
        XElement inactiveFrameBrush = frameStyle.Elements()
            .Where(element => element.Name.LocalName == "Setter")
            .Single(element => string.Equals((string)element.Attribute("Property"), "BorderBrush", StringComparison.Ordinal));
        XElement activeFrameTrigger = frameStyle.Descendants().Single(element => element.Name.LocalName == "DataTrigger");
        XElement activeFrameBrush = activeFrameTrigger.Elements().Single(element => element.Name.LocalName == "Setter");

        Assert.AreEqual("23", (string)windowChrome.Attribute("CaptionHeight"));
        Assert.AreEqual("5", (string)windowChrome.Attribute("ResizeBorderThickness"));
        Assert.AreEqual("True", (string)window.Attribute("UseLayoutRounding"));
        Assert.AreEqual("True", (string)window.Attribute("SnapsToDevicePixels"));
        Assert.AreEqual(
            "{DynamicResource App.SubtleTextBrush}",
            (string)captionButtonStyle.Elements().Single(element =>
                element.Name.LocalName == "Setter"
                && string.Equals((string)element.Attribute("Property"), "Foreground", StringComparison.Ordinal)).Attribute("Value"));
        Assert.IsFalse(captionButtonStyle.Descendants().Any(element => element.Name.LocalName == "DataTrigger"));
        Assert.AreEqual("1", (string)windowChromeFrame.Attribute("BorderThickness"));
        Assert.AreEqual("False", (string)windowChromeFrame.Attribute("IsHitTestVisible"));
        Assert.AreEqual("True", (string)windowChromeFrame.Attribute("SnapsToDevicePixels"));
        Assert.AreEqual("5", (string)windowChromeFrame.Attribute("Panel.ZIndex"));
        Assert.AreEqual("{DynamicResource App.SubtleTextBrush}", (string)inactiveFrameBrush.Attribute("Value"));
        Assert.AreEqual("True", (string)activeFrameTrigger.Attribute("Value"));
        Assert.AreEqual("{Binding IsActive, RelativeSource={RelativeSource AncestorType={x:Type Window}}}", (string)activeFrameTrigger.Attribute("Binding"));
        Assert.AreEqual("BorderBrush", (string)activeFrameBrush.Attribute("Property"));
        Assert.AreEqual("{DynamicResource App.AccentBrush}", (string)activeFrameBrush.Attribute("Value"));
        Assert.AreEqual("Grid", windowChromeFrame.Parent.Name.LocalName);
        Assert.AreEqual("windowBorder", (string)windowChromeFrame.Parent.Parent.Attribute("Name"));
        Assert.IsTrue(source.Contains("WindowChrome.IsHitTestVisibleInChrome", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SystemCommands.CloseWindowCommand", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SystemCommands.MaximizeWindowCommand", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SystemCommands.MinimizeWindowCommand", StringComparison.Ordinal));
        Assert.IsTrue(codeBehind.Contains("windowBorder.Margin = new Thickness(8.0);", StringComparison.Ordinal));
        Assert.IsTrue(codeBehind.Contains("windowBorder.Margin = new Thickness(0.0);", StringComparison.Ordinal));
        foreach (string legacyToken in new[]
        {
            "MetroChromeBehavior",
            "Microsoft.Expression.Shapes",
            "System.Windows.Interactivity",
            "xmlns:i=",
            "xmlns:ei=",
            "xmlns:ee=",
            "xmlns:ed=",
            "xmlns:chrome="
        })
        {
            Assert.IsFalse(source.Contains(legacyToken, StringComparison.Ordinal), legacyToken);
        }
    }

    [TestMethod]
    public void DialogXamlDoesNotDeclareLegacyInteractionNamespace()
    {
        string repositoryRoot = FindRepositoryRoot();
        foreach (string fileName in new[] { "LoadPlaylistURIDialog.xaml", "PlaylistPropertyDialog.xaml" })
        {
            string source = File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker", "Views", fileName));
            Assert.IsFalse(source.Contains("xmlns:i=", StringComparison.Ordinal), fileName);
            Assert.IsFalse(source.Contains("System.Windows.Interactivity", StringComparison.Ordinal), fileName);
        }
    }

    [TestMethod]
    public void ProjectAndPortableLayoutRetireLegacyChromeAssemblies()
    {
        string repositoryRoot = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"));
        string layout = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "portable-package-layout.ps1"));
        int sdkNativeRootStart = layout.IndexOf("$script:RequiredSdkNativeRootFiles = @(", StringComparison.Ordinal);
        int sdkNativeRootEnd = layout.IndexOf(")", sdkNativeRootStart, StringComparison.Ordinal);
        string requiredSdkNativeRoot = layout.Substring(sdkNativeRootStart, sdkNativeRootEnd - sdkNativeRootStart);
        string notices = File.ReadAllText(Path.Combine(repositoryRoot, "ThirdPartyNotices.txt"));
        string japaneseNotices = File.ReadAllText(Path.Combine(repositoryRoot, "ThirdPartyNotices.ja.txt"));

        foreach (string assemblyName in new[]
        {
            "System.Windows.Interactivity.dll",
            "Microsoft.Expression.Interactions.dll",
            "Microsoft.Expression.Drawing.dll",
            "Microsoft.Expression.Effects.dll",
            "MetroRadiance.dll",
            "MetroRadiance.Core.dll",
            "MetroRadiance.Chrome.dll"
        })
        {
            Assert.IsFalse(project.Contains(assemblyName, StringComparison.Ordinal), assemblyName);
            Assert.IsFalse(requiredSdkNativeRoot.Contains("\"" + assemblyName + "\"", StringComparison.Ordinal), assemblyName);
            Assert.IsFalse(notices.Contains(assemblyName, StringComparison.Ordinal), assemblyName);
            Assert.IsFalse(japaneseNotices.Contains(assemblyName, StringComparison.Ordinal), assemblyName);
            Assert.IsFalse(File.Exists(Path.Combine(repositoryRoot, "libs", assemblyName)), assemblyName);
        }

        Assert.IsFalse(requiredSdkNativeRoot.Contains("\"Microsoft.Xaml.Behaviors.dll\"", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new AssertFailedException("Could not locate repository root.");
    }
}
