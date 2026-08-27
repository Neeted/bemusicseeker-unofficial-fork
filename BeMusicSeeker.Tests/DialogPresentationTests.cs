using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DialogPresentationTests
{
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
}
