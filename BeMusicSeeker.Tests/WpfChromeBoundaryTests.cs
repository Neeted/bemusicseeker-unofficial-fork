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
        XDocument document = XDocument.Load(xamlPath);
        XElement windowChrome = document.Descendants().Single(element => element.Name.LocalName == "WindowChrome");
        XElement captionButtonStyle = document.Descendants().Single(element =>
            element.Name.LocalName == "Style"
            && string.Equals(
                (string)element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "Key"),
                "CaptionButtonStyleKey",
                StringComparison.Ordinal));
        XElement activeTrigger = captionButtonStyle.Descendants().Single(element => element.Name.LocalName == "DataTrigger");

        Assert.AreEqual("23", (string)windowChrome.Attribute("CaptionHeight"));
        Assert.AreEqual("5", (string)windowChrome.Attribute("ResizeBorderThickness"));
        Assert.AreEqual("True", (string)activeTrigger.Attribute("Value"));
        Assert.AreEqual("{Binding IsActive, RelativeSource={RelativeSource AncestorType={x:Type Window}}}", (string)activeTrigger.Attribute("Binding"));
        Assert.IsTrue(source.Contains("WindowChrome.IsHitTestVisibleInChrome", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SystemCommands.CloseWindowCommand", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SystemCommands.MaximizeWindowCommand", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SystemCommands.MinimizeWindowCommand", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Binding IsActive, RelativeSource={RelativeSource AncestorType={x:Type Window}}", StringComparison.Ordinal));
        Assert.AreEqual(
            2,
            document.Descendants().Count(element =>
                element.Name.LocalName == "Ellipse"
                && string.Equals((string)element.Attribute("Stroke"), "#FF727272", StringComparison.Ordinal)
                && string.Equals((string)element.Attribute("StrokeThickness"), "8", StringComparison.Ordinal)));

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
        int managedRootStart = layout.IndexOf("$script:RequiredManagedRootFiles = @(", StringComparison.Ordinal);
        int managedRootEnd = layout.IndexOf(")", managedRootStart, StringComparison.Ordinal);
        string requiredManagedRoot = layout.Substring(managedRootStart, managedRootEnd - managedRootStart);
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
            Assert.IsFalse(requiredManagedRoot.Contains("\"" + assemblyName + "\"", StringComparison.Ordinal), assemblyName);
            Assert.IsFalse(notices.Contains(assemblyName, StringComparison.Ordinal), assemblyName);
            Assert.IsFalse(japaneseNotices.Contains(assemblyName, StringComparison.Ordinal), assemblyName);
            Assert.IsFalse(File.Exists(Path.Combine(repositoryRoot, "libs", assemblyName)), assemblyName);
        }

        Assert.IsFalse(requiredManagedRoot.Contains("\"Microsoft.Xaml.Behaviors.dll\"", StringComparison.Ordinal));
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
