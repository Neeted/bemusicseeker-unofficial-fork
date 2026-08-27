using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DialogPresentationTests
{
    private const string CanonicalControlsSource =
        "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalControls.xaml";
    private const string CanonicalDialogStylesSource =
        "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalDialogStyles.xaml";
    private const string SettingsControlsSource =
        "/BeMusicSeeker;component/BeMusicSeeker/Views/Settings/SettingsControls.xaml";

    [DataTestMethod]
    [DataRow("BeMusicSeeker/Views/SettingsWindow.xaml", false, true)]
    [DataRow("BeMusicSeeker/Views/ReleaseNotesWindow.xaml", false, false)]
    [DataRow("BeMusicSeeker/Views/Settings/Lr2AdvancedPathsDialog.xaml", false, true)]
    [DataRow("BeMusicSeeker/Views/UpdateAvailableDialog.xaml", false, false)]
    [DataRow("BeMusicSeeker/Views/PendingDeleteConfirmDialog.xaml", false, false)]
    [DataRow("BeMusicSeeker/Views/PlayHistoryFolderDisplayPresetEditDialog.xaml", false, false)]
    [DataRow("BeMusicSeeker/Views/Lr2PlayHistorySchemaUninstallDialog.xaml", false, false)]
    [DataRow("Parago/Windows/ProgressDialog.xaml", false, false)]
    [DataRow("BeMusicSeeker/Views/ThemedMessageBox.cs", false, false)]
    [DataRow("BeMusicSeeker/Views/InitialSetupLanguageDialog.xaml", true, false)]
    [DataRow("BeMusicSeeker/Views/LoadPlaylistURIDialog.xaml", true, false)]
    [DataRow("BeMusicSeeker/Views/PlaylistPropertyDialog.xaml", true, false)]
    [DataRow("BeMusicSeeker/Views/PlaylistSummaryBulkEditDialog.xaml", true, false)]
    public void CustomDialogScope_AdoptsCanonicalVisualRoles(
        string relativePath,
        bool hasOverlay,
        bool allowsSettingsControlAliases)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            string source = File.ReadAllText(path);
            StringAssert.Contains(source, "App.Canonical.DialogContentStyle", relativePath);
            StringAssert.Contains(source, "App.Canonical.DialogPrimaryActionStyle", relativePath);
            StringAssert.Contains(source, "App.Canonical.DialogQuietActionStyle", relativePath);
            StringAssert.Contains(source, CanonicalDialogStylesSource, relativePath);
            Assert.IsFalse(
                source.Contains(CanonicalControlsSource, StringComparison.OrdinalIgnoreCase),
                relativePath + " must not depend on a sibling canonical control dictionary.");
            return;
        }

        XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
        XElement root = document.Root!;
        string[] resourceSources = root
            .Descendants()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .Select(element => (string?)element.Attribute("Source"))
            .Where(source => source != null)
            .Select(source => source!)
            .ToArray();
        string[] expectedFacadeSources = allowsSettingsControlAliases
            ? resourceSources.Where(IsSettingsControlsSource).ToArray()
            : resourceSources.Where(IsCanonicalDialogStylesSource).ToArray();
        Assert.AreEqual(
            1,
            expectedFacadeSources.Length,
            $"{relativePath} must import its canonical facade exactly once.");
        if (allowsSettingsControlAliases)
        {
            Assert.IsFalse(
                resourceSources.Any(IsCanonicalDialogStylesSource),
                $"{relativePath} must use SettingsControls as its only canonical facade.");
        }
        Assert.IsFalse(
            resourceSources.Any(IsCanonicalControlsSource),
            $"{relativePath} must not depend on sibling dictionary ordering for canonical controls.");

        if (hasOverlay)
        {
            XElement overlay = root
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Rectangle")!;
            Assert.IsNotNull(overlay, $"{relativePath} must define its overlay surface.");
            Assert.AreEqual(
                "{StaticResource App.Canonical.DialogOverlayStyle}",
                (string?)overlay.Attribute("Style"),
                $"{relativePath} must explicitly adopt the canonical overlay role.");
        }

        XElement? content = root
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Border"
                && (string?)element.Attribute("Style") == "{StaticResource App.Canonical.DialogContentStyle}");
        Assert.IsNotNull(content, $"{relativePath} must explicitly adopt the canonical dialog content role.");

        foreach (XElement button in root.Descendants().Where(element => element.Name.LocalName == "Button"))
        {
            string? style = (string?)button.Attribute("Style");
            bool adopted = style is "{StaticResource App.Canonical.DialogActionStyle}"
                or "{StaticResource App.Canonical.DialogPrimaryActionStyle}"
                or "{StaticResource App.Canonical.DialogQuietActionStyle}"
                or "{StaticResource App.Canonical.DialogDangerActionStyle}";
            if (allowsSettingsControlAliases && style?.StartsWith("{StaticResource Settings", StringComparison.Ordinal) == true)
            {
                adopted = true;
            }

            Assert.IsTrue(adopted, $"{relativePath} has a Button without a canonical dialog action role.");
        }

        var canonicalControlStyles = new[]
        {
            (Element: "TextBox", Key: "App.Canonical.TextBoxStyle"),
            (Element: "ComboBox", Key: "App.Canonical.ComboBoxStyle"),
            (Element: "CheckBox", Key: "App.Canonical.CheckBoxStyle"),
            (Element: "RadioButton", Key: "App.Canonical.RadioButtonStyle"),
            (Element: "ListBox", Key: "App.Canonical.ListBoxStyle"),
            (Element: "GroupBox", Key: "App.Canonical.GroupBoxStyle"),
            (Element: "Label", Key: "App.Canonical.LabelStyle"),
            (Element: "ScrollViewer", Key: "App.Canonical.ScrollViewerStyle"),
        };

        foreach ((string elementName, string key) in canonicalControlStyles)
        {
            foreach (XElement control in root.Descendants().Where(element => element.Name.LocalName == elementName))
            {
                string? style = (string?)control.Attribute("Style");
                bool adopted = style == $"{{StaticResource {key}}}";
                if (allowsSettingsControlAliases && style?.StartsWith("{StaticResource Settings", StringComparison.Ordinal) == true)
                {
                    adopted = true;
                }

                Assert.IsTrue(adopted, $"{relativePath} has a {elementName} without canonical control adoption.");
            }
        }
    }

    [TestMethod]
    public void CanonicalDependentDictionaries_MaterializeWithoutSiblingDictionaryOrder()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Assert.IsNotNull(application);

            ResourceDictionary[] applicationCanonicalResources = application.Resources.MergedDictionaries
                .Where(IsCanonicalResource)
                .ToArray();
            foreach (ResourceDictionary resource in applicationCanonicalResources)
            {
                application.Resources.MergedDictionaries.Remove(resource);
            }

            try
            {
                AssertStandaloneResourceDictionary(
                    CanonicalDialogStylesSource,
                    CanonicalControlsSource,
                    new object[]
                    {
                        "App.Canonical.DialogOverlayStyle",
                        "App.Canonical.DialogContentStyle",
                        "App.Canonical.DialogPrimaryActionStyle",
                        "App.Canonical.DialogQuietActionStyle",
                        "App.Canonical.ButtonStyle",
                        "App.Canonical.PrimaryButtonStyle",
                        "App.Canonical.ListBoxStyle"
                    });
                AssertStandaloneResourceDictionary(
                    SettingsControlsSource,
                    CanonicalDialogStylesSource,
                    new object[]
                    {
                        "SettingsButtonStyle",
                        "SettingsTextBoxStyle",
                        "SettingsListBoxStyle",
                        "App.Canonical.DialogContentStyle",
                        "App.Canonical.PrimaryButtonStyle",
                        typeof(Button),
                        typeof(TextBox),
                        typeof(ListBox)
                    });
            }
            finally
            {
                foreach (ResourceDictionary resource in applicationCanonicalResources)
                {
                    application.Resources.MergedDictionaries.Add(resource);
                }
            }
        });
    }

    [TestMethod]
    public void ApplicationResources_ExposeCanonicalControlsWithoutImplicitMainWindowAdoption()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Assert.IsNotNull(application);

            var canonicalDialogs = new ResourceDictionary
            {
                Source = new Uri(
                    CanonicalDialogStylesSource,
                    UriKind.RelativeOrAbsolute)
            };
            bool dialogsAdded = false;
            try
            {
                application.Resources.MergedDictionaries.Add(canonicalDialogs);
                dialogsAdded = true;

                Style canonicalButtonStyle = application.TryFindResource("App.Canonical.ButtonStyle") as Style;
                Assert.IsNotNull(canonicalButtonStyle,
                    "The application must own the canonical Button style explicitly.");

                Style canonicalTextBoxStyle = application.TryFindResource("App.Canonical.TextBoxStyle") as Style;
                Assert.IsNotNull(canonicalTextBoxStyle,
                    "The application must own the canonical TextBox style explicitly.");
                Assert.IsNotNull(application.TryFindResource("App.Canonical.DialogSurfaceStyle"),
                    "The application must own canonical dialog presentation resources explicitly.");

                Assert.IsFalse(application.Resources.Contains(typeof(Button)),
                    "Canonical controls must not become an application-wide implicit Button style.");
                Assert.IsFalse(application.Resources.Contains(typeof(TextBox)),
                    "Canonical controls must not become an application-wide implicit TextBox style.");

                var mainWindowButton = new Button();
                var mainWindowTextBox = new TextBox();
                Assert.IsNull(mainWindowButton.Style,
                    "An unadopted MainWindow Button must not resolve a canonical implicit style.");
                Assert.IsNull(mainWindowTextBox.Style,
                    "An unadopted MainWindow TextBox must not resolve a canonical implicit style.");

                var settingsHost = new Grid();
                settingsHost.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/BeMusicSeeker/Views/Settings/SettingsControls.xaml",
                        UriKind.RelativeOrAbsolute)
                });

                Style settingsButtonStyle = (Style)settingsHost.Resources["SettingsButtonStyle"];
                Assert.IsNotNull(settingsButtonStyle.BasedOn,
                    "Settings must derive its Button style from a canonical Button style.");
                Assert.AreEqual(typeof(Button), settingsButtonStyle.BasedOn.TargetType,
                    "Settings must derive its Button style from a Button style.");

                Style settingsTextBoxAlias = (Style)settingsHost.Resources["SettingsTextBoxStyle"];
                Style settingsTextBoxStyle = (Style)settingsHost.Resources[typeof(TextBox)];
                Assert.IsNotNull(settingsTextBoxAlias.BasedOn,
                    "Settings must derive its TextBox style from a canonical TextBox style.");
                Assert.AreEqual(typeof(TextBox), settingsTextBoxAlias.BasedOn.TargetType,
                    "Settings must derive its TextBox style from a TextBox style.");
                Assert.AreSame(settingsTextBoxAlias, settingsTextBoxStyle.BasedOn,
                    "Settings' implicit TextBox adoption must remain a local alias over the canonical style.");
            }
            finally
            {
                if (dialogsAdded)
                {
                    application.Resources.MergedDictionaries.Remove(canonicalDialogs);
                }

            }
        });
    }

    private static void AssertStandaloneResourceDictionary(
        string source,
        string dependencySource,
        object[] requiredKeys)
    {
        var resourceDictionary = new ResourceDictionary
        {
            Source = new Uri(source, UriKind.RelativeOrAbsolute)
        };
        Assert.IsTrue(
            resourceDictionary.MergedDictionaries.Any(resource =>
                string.Equals(resource.Source?.OriginalString, dependencySource, StringComparison.OrdinalIgnoreCase)),
            source + " must own its canonical dependency " + dependencySource + ".");

        var host = new Grid();
        host.Resources.MergedDictionaries.Add(resourceDictionary);
        foreach (object key in requiredKeys)
        {
            Assert.IsNotNull(
                host.TryFindResource(key),
                source + " must materialize resource '" + key + "' without an application sibling.");
        }
    }

    private static bool IsCanonicalResource(ResourceDictionary resource)
    {
        return string.Equals(
            resource.Source?.OriginalString,
            CanonicalControlsSource,
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                resource.Source?.OriginalString,
                CanonicalDialogStylesSource,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCanonicalDialogStylesSource(string source)
    {
        return string.Equals(
            source,
            CanonicalDialogStylesSource,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCanonicalControlsSource(string source)
    {
        return string.Equals(source, CanonicalControlsSource, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSettingsControlsSource(string source)
    {
        return source.EndsWith("SettingsControls.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
