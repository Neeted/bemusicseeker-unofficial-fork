using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DialogPresentationTests
{
    [DataTestMethod]
    [DataRow("BeMusicSeeker/Views/SettingsWindow.xaml", false, true)]
    [DataRow("BeMusicSeeker/Views/ReleaseNotesWindow.xaml", false, false)]
    [DataRow("BeMusicSeeker/Views/Settings/Lr2AdvancedPathsDialog.xaml", false, false)]
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
            return;
        }

        XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
        XElement root = document.Root!;

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
    public void ApplicationResources_ExposeCanonicalControlsWithoutImplicitMainWindowAdoption()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Assert.IsNotNull(application);

            var canonicalControls = new ResourceDictionary
            {
                Source = new Uri(
                    "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalControls.xaml",
                    UriKind.RelativeOrAbsolute)
            };
            var canonicalDialogs = new ResourceDictionary
            {
                Source = new Uri(
                    "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalDialogStyles.xaml",
                    UriKind.RelativeOrAbsolute)
            };
            bool controlsAdded = false;
            bool dialogsAdded = false;
            try
            {
                application.Resources.MergedDictionaries.Add(canonicalControls);
                controlsAdded = true;
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
                Assert.AreSame(canonicalButtonStyle, settingsButtonStyle.BasedOn,
                    "Settings must adopt the application canonical Button style.");

                Style settingsTextBoxAlias = (Style)settingsHost.Resources["SettingsTextBoxStyle"];
                Style settingsTextBoxStyle = (Style)settingsHost.Resources[typeof(TextBox)];
                Assert.AreSame(canonicalTextBoxStyle, settingsTextBoxAlias.BasedOn,
                    "Settings must adopt the application canonical TextBox style.");
                Assert.AreSame(settingsTextBoxAlias, settingsTextBoxStyle.BasedOn,
                    "Settings' implicit TextBox adoption must remain a local alias over the canonical style.");
            }
            finally
            {
                if (dialogsAdded)
                {
                    application.Resources.MergedDictionaries.Remove(canonicalDialogs);
                }

                if (controlsAdded)
                {
                    application.Resources.MergedDictionaries.Remove(canonicalControls);
                }
            }
        });
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
