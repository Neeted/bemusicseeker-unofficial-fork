using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Xml.Linq;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.TestSupport.Components.ThemeTestFiles;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class NativeWindowThemeContractTests
{
    /// <summary>本番のXAML rootを実型と照合し、共通Window継承の抜けとprogressのClose抑制・Closing接続の欠落を検出します。</summary>
    [TestMethod]
    public void AuthoredWindowXaml_UsesSharedRootExceptCustomChromeMainWindow()
    {
        string repositoryRoot = FindRepositoryRoot();
        Assembly productionAssembly = typeof(ThemedWindow).Assembly;
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string path in EnumerateProductionXaml(repositoryRoot))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            XElement root = document.Root!;
            string? className = (string?)root.Attribute(xamlNamespace + "Class");
            if (className == null)
            {
                continue;
            }

            Type? viewType = productionAssembly.GetType(className, throwOnError: false);
            if (viewType == null || !typeof(Window).IsAssignableFrom(viewType))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(repositoryRoot, path);
            if (viewType == typeof(MainWindow))
            {
                Assert.AreEqual("Window", root.Name.LocalName, relativePath);
                Assert.IsTrue(
                    root.Descendants().Any(element => element.Name.LocalName == "WindowChrome.WindowChrome"),
                    $"{relativePath} must retain its custom WindowChrome contract.");
                continue;
            }

            Assert.AreEqual("ThemedWindow", root.Name.LocalName, $"{relativePath} must use ThemedWindow as its XAML root.");
            StringAssert.StartsWith(
                root.Name.NamespaceName,
                "clr-namespace:BeMusicSeeker.Views",
                $"{relativePath} must resolve its root to BeMusicSeeker.Views.ThemedWindow.");

            if (string.Equals(className, "Parago.Windows.ProgressDialog", StringComparison.Ordinal))
            {
                Assert.AreEqual("True", root.Attributes().Single(attribute => attribute.Name.LocalName == "WindowSettings.HideCloseButton").Value);
                Assert.AreEqual("OnClosing", (string?)root.Attribute("Closing"));
            }
        }
    }

    private static IEnumerable<string> EnumerateProductionXaml(string repositoryRoot)
    {
        return new[] { "BeMusicSeeker" }
            .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(repositoryRoot, directory), "*.xaml", SearchOption.AllDirectories))
            .Where(path => !HasDirectorySegment(path, "bin") && !HasDirectorySegment(path, "obj"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasDirectorySegment(string path, string segment)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
    }
}
