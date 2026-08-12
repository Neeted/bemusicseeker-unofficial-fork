using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfTestApplicationHostTests
{
    [TestMethod]
    public void Invoke_ReusesSingleApplicationDispatcherAndStaThread()
    {
        Application? firstApplication = null;
        Dispatcher? firstDispatcher = null;
        Thread? firstThread = null;
        ApartmentState firstApartmentState = ApartmentState.Unknown;
        TestUiDispatcherHost.Invoke(() =>
        {
            firstApplication = Application.Current;
            firstDispatcher = Dispatcher.CurrentDispatcher;
            firstThread = Thread.CurrentThread;
            firstApartmentState = Thread.CurrentThread.GetApartmentState();
        });

        Application? secondApplication = null;
        Dispatcher? secondDispatcher = null;
        Thread? secondThread = null;
        TestUiDispatcherHost.Invoke(() =>
        {
            secondApplication = Application.Current;
            secondDispatcher = Dispatcher.CurrentDispatcher;
            secondThread = Thread.CurrentThread;
        });

        Assert.IsNotNull(firstApplication);
        Assert.AreSame(firstApplication, secondApplication);
        Assert.AreSame(firstDispatcher, secondDispatcher);
        Assert.AreSame(TestUiDispatcherHost.Dispatcher, firstDispatcher);
        Assert.AreSame(firstThread, secondThread);
        Assert.AreEqual(ApartmentState.STA, firstApartmentState);
    }

    [TestMethod]
    public void Startup_AppliesLightComponentThemeWithSemanticResources()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Assert.IsNotNull(application);
            ResourceDictionary theme = application.Resources.MergedDictionaries.Single(dictionary =>
                string.Equals(
                    dictionary.Source?.OriginalString,
                    "/BeMusicSeeker;component/Themes/Light.xaml",
                    StringComparison.OrdinalIgnoreCase));

            Assert.IsInstanceOfType<SolidColorBrush>(theme["App.BackgroundBrush"]);
            Assert.IsInstanceOfType<SolidColorBrush>(theme["App.TextBrush"]);
            Assert.AreEqual(AppThemeService.Light, BeMusicSeeker.Properties.Settings.Default.AppearanceTheme);
        });
    }

    [TestMethod]
    public void AssemblyInitialize_LeavesSharedWpfHostLazy()
    {
        string testProjectDirectory = FindTestProjectDirectory();
        string assemblySettingsSource = File.ReadAllText(Path.Combine(testProjectDirectory, "MSTestSettings.cs"));

        StringAssert.Contains(assemblySettingsSource, "RuntimeBootstrap.Initialize();");
        Assert.IsFalse(assemblySettingsSource.Contains("StartApplication", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TestAssembly_HasExactlyOneWpfApplicationConstructionInSharedHost()
    {
        string testProjectDirectory = FindTestProjectDirectory();
        var constructionPattern = new Regex(@"\bnew\s+Application\s*(?:\{|\()", RegexOptions.CultureInvariant);
        string[] constructionFiles = Directory.EnumerateFiles(testProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => constructionPattern.IsMatch(File.ReadAllText(path)))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { Path.Combine(testProjectDirectory, "TestUiScheduler.cs") },
            constructionFiles);
        Assert.AreEqual(1, constructionPattern.Matches(File.ReadAllText(constructionFiles[0])).Count);
    }

    private static string FindTestProjectDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, "BeMusicSeeker.Tests");
            if (File.Exists(Path.Combine(candidate, "BeMusicSeeker.Tests.csproj")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("BeMusicSeeker.Tests project directory was not found.");
    }
}
