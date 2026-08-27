using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parago.Windows;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DialogPresentationTests
{
    [DataTestMethod]
    [DataRow("SettingsWindow")]
    [DataRow("ReleaseNotesWindow")]
    [DataRow("Lr2AdvancedPathsDialog")]
    [DataRow("UpdateAvailableDialog")]
    [DataRow("PendingDeleteConfirmDialog")]
    [DataRow("PlayHistoryFolderDisplayPresetEditDialog")]
    [DataRow("Lr2PlayHistorySchemaUninstallDialog")]
    [DataRow("ProgressDialog")]
    [DataRow("ThemedMessageBox")]
    [DataRow("InitialSetupLanguageDialog")]
    [DataRow("LoadPlaylistURIDialog")]
    [DataRow("PlaylistPropertyDialog")]
    [DataRow("PlaylistSummaryBulkEditDialog")]
    public void CustomDialogScope_UsesAppliedCanonicalRolesAndKeepsOuterSentinelIsolated(string dialogName)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            PresentationFixture fixture = CreateFixture(dialogName);
            SentinelHost sentinelHost = CreateSentinelHost(fixture);
            try
            {
                AssertSentinelStyle(sentinelHost.SentinelButton, sentinelHost.SentinelStyle);
                windowTest.ShowAndWaitForContentRendered(fixture.HostWindow);
                fixture.HostWindow.UpdateLayout();
                AssertSentinelStyle(sentinelHost.SentinelButton, sentinelHost.SentinelStyle);
                AssertCanonicalPresentation(fixture);
                AssertApplicationResourcesDoNotImplicitlyAdoptCanonicalStyle(fixture, windowTest);
            }
            finally
            {
                if (fixture.HostWindow.IsVisible)
                {
                    if (fixture.HostWindow is SettingsWindow settingsWindow)
                    {
                        settingsWindow.CloseForOwnerShutdown();
                    }
                    else
                    {
                        fixture.HostWindow.Close();
                    }
                }
                if (sentinelHost.Window != fixture.HostWindow && sentinelHost.Window.IsVisible)
                {
                    sentinelHost.Window.Close();
                }
                fixture.Cleanup();
            }
        });
    }

    [TestMethod]
    public void ProgressDialog_WithSubLabelAndCancel_RendersContentInsideItsClientBounds()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var dialog = new ProgressDialog(new ProgressDialogSettings(
                showSubLabel: true,
                showCancelButton: true,
                showProgressBarIndeterminate: true))
            {
                Label = "Loading a long-running operation",
                SubLabel = string.Join(" ", Enumerable.Repeat(
                    "Preparing the selected files before the operation starts.",
                    8))
            };

            windowTest.ShowAndWaitForContentRendered(dialog);
            dialog.UpdateLayout();

            Border content = FindVisualDescendants<Border>(dialog)
                .Single(border => StyleChainContains(
                    border.Style,
                    (Style)dialog.FindResource("App.Canonical.DialogContentStyle")));
            var progressBar = (ProgressBar)dialog.FindName("ProgressBar")!;
            var cancelButton = (Button)dialog.FindName("CancelButton")!;
            var textLabel = (TextBlock)dialog.FindName("TextLabel")!;
            var subTextLabel = (TextBlock)dialog.FindName("SubTextLabel")!;

            Assert.IsTrue(content.ActualWidth > 0 && content.ActualHeight > 0);
            Assert.IsTrue(dialog.ActualHeight + 0.5 >= dialog.MinHeight);
            Assert.IsTrue(progressBar.ActualWidth > 0 && progressBar.ActualHeight > 0);
            Assert.IsTrue(cancelButton.ActualWidth > 0 && cancelButton.ActualHeight > 0);
            Assert.IsTrue(cancelButton.IsEnabled);
            Assert.IsTrue(cancelButton.IsHitTestVisible);
            Assert.IsTrue(textLabel.ActualWidth > 0 && textLabel.ActualHeight > 0);
            Assert.IsTrue(subTextLabel.ActualWidth > 0 && subTextLabel.ActualHeight > 0);

            AssertVisualBoundsInside(content, textLabel);
            AssertVisualBoundsInside(content, subTextLabel);
            AssertVisualBoundsInside(content, progressBar);
            AssertVisualBoundsInside(content, cancelButton);
            Assert.IsTrue(
                GetVisualBounds(content, progressBar).Top >= GetVisualBounds(content, subTextLabel).Bottom - 0.5,
                "The progress row must follow the rendered sub-label instead of overlapping it.");
        });
    }

    private static PresentationFixture CreateFixture(string dialogName)
    {
        return dialogName switch
        {
            "SettingsWindow" => CreateSettingsWindowFixture(),
            "ReleaseNotesWindow" => CreateNativeFixture(new ReleaseNotesWindow(), []),
            "Lr2AdvancedPathsDialog" => CreateLr2AdvancedPathsFixture(),
            "UpdateAvailableDialog" => CreateUpdateAvailableFixture(),
            "PendingDeleteConfirmDialog" => CreateNativeFixture(new PendingDeleteConfirmDialog(),
                [
                    ButtonWithAutomationId("PendingDeleteConfirm", DialogButtonRole.Primary, "pending-delete affirmative"),
                    ButtonWithAutomationId("PendingDeleteCancel", DialogButtonRole.Quiet, "pending-delete cancel"),
                ]),
            "PlayHistoryFolderDisplayPresetEditDialog" => CreatePlayHistoryPresetFixture(),
            "Lr2PlayHistorySchemaUninstallDialog" => CreateNativeFixture(
                new Lr2PlayHistorySchemaUninstallDialog("score.db"),
                [
                    ButtonWithAutomationId("Lr2PlayHistorySchemaUninstall", DialogButtonRole.Danger, "schema uninstall destructive action"),
                    ButtonWithAutomationId("Lr2PlayHistorySchemaCancel", DialogButtonRole.Quiet, "schema uninstall cancel"),
                ]),
            "ProgressDialog" => CreateNativeFixture(
                new ProgressDialog(new ProgressDialogSettings(
                    showSubLabel: true,
                    showCancelButton: true,
                    showProgressBarIndeterminate: true)),
                [ButtonWithAutomationId("ProgressDialogCancel", DialogButtonRole.Quiet, "progress cancel")]),
            "ThemedMessageBox" => CreateNativeFixture(
                ThemedMessageBox.BuildDialogForPresentation(
                    owner: null,
                    messageBoxText: "Presentation contract message",
                    caption: "Presentation",
                    button: MessageBoxButton.OKCancel,
                    icon: MessageBoxImage.Information,
                    initialResult: MessageBoxResult.Cancel,
                    warningMessageBoxText: null,
                    setResult: _ => { }),
                [
                    ButtonWithAutomationId("ThemedMessageBoxOK", DialogButtonRole.Primary, "message box affirmative"),
                    ButtonWithAutomationId("ThemedMessageBoxCancel", DialogButtonRole.Quiet, "message box cancel"),
                ]),
            "InitialSetupLanguageDialog" => CreateOverlayFixture(
                new InitialSetupLanguageDialog(),
                720,
                520,
                [ButtonWithAutomationId("InitialSetupLanguageContinue", DialogButtonRole.Primary, "initial setup continue")]),
            "LoadPlaylistURIDialog" => CreateOverlayFixture(
                new LoadPlaylistURIDialog(),
                720,
                520,
                [
                    ButtonWithAutomationId("LoadPlaylistUriOpenLocalFile", DialogButtonRole.Neutral, "playlist URI open local file"),
                    ButtonWithAutomationId("LoadPlaylistUriAccept", DialogButtonRole.Primary, "playlist URI affirmative"),
                    ButtonWithAutomationId("LoadPlaylistUriCancel", DialogButtonRole.Quiet, "playlist URI cancel"),
                ]),
            "PlaylistPropertyDialog" => CreateOverlayFixture(
                new PlaylistPropertyDialog(),
                720,
                520,
                [
                    ButtonWithAutomationId("PlaylistPropertyAccept", DialogButtonRole.Primary, "playlist property affirmative"),
                    ButtonWithAutomationId("PlaylistPropertyCancel", DialogButtonRole.Quiet, "playlist property cancel"),
                ]),
            "PlaylistSummaryBulkEditDialog" => CreateOverlayFixture(
                new PlaylistSummaryBulkEditDialog(),
                720,
                560,
                [
                    ButtonWithAutomationId("PlaylistSummaryClose", DialogButtonRole.Quiet, "playlist summary close"),
                    ButtonWithAutomationId("PlaylistSummaryApplyCustomFolderOutput", DialogButtonRole.Neutral, "playlist summary custom folder apply"),
                    ButtonWithAutomationId("PlaylistSummaryApplyOutputBase", DialogButtonRole.Neutral, "playlist summary output base apply"),
                    ButtonWithAutomationId("PlaylistSummaryApplyExternalPropertyInitialization", DialogButtonRole.Neutral, "playlist summary external property apply"),
                    ButtonWithAutomationId("PlaylistSummaryApplyRootFolder", DialogButtonRole.Neutral, "playlist summary root folder apply"),
                    ButtonWithAutomationId("PlaylistSummaryApplyExternalSync", DialogButtonRole.Neutral, "playlist summary external sync apply"),
                    ButtonWithAutomationId("PlaylistSummaryApplyBmtOutput", DialogButtonRole.Neutral, "playlist summary BMT apply"),
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(dialogName), dialogName, "Unknown dialog fixture."),
        };
    }

    private static PresentationFixture CreateNativeFixture(
        Window window,
        IReadOnlyList<ButtonExpectation> buttonExpectations,
        bool allowsSettingsControlAliases = false,
        Action cleanup = null)
    {
        FrameworkElement presentationRoot = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("Native dialog content must be a FrameworkElement.");
        return new PresentationFixture(
            window,
            presentationRoot,
            allowsSettingsControlAliases,
            requiresOverlayRole: false,
            buttonExpectations: buttonExpectations,
            cleanup: cleanup ?? (() => { }));
    }

    private static PresentationFixture CreateOverlayFixture(
        FrameworkElement presentationRoot,
        double width,
        double height,
        IReadOnlyList<ButtonExpectation> buttonExpectations)
    {
        var host = new Window
        {
            Width = width,
            Height = height,
            ShowInTaskbar = false,
            Content = presentationRoot,
        };
        return new PresentationFixture(
            host,
            presentationRoot,
            allowsSettingsControlAliases: false,
            requiresOverlayRole: true,
            buttonExpectations: buttonExpectations,
            cleanup: () => { });
    }

    private static PresentationFixture CreateLr2AdvancedPathsFixture()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        var dialog = new Lr2AdvancedPathsDialog(owner.SettingDialog);
        return CreateNativeFixture(
            dialog,
            [
                ButtonWithAutomationId("Lr2AdvancedPathsCancel", DialogButtonRole.Quiet, "LR2 paths cancel"),
                ButtonWithAutomationId("Lr2AdvancedPathsDone", DialogButtonRole.Primary, "LR2 paths affirmative"),
            ],
            allowsSettingsControlAliases: true,
            cleanup: owner.SettingDialog.Dispose);
    }

    private static PresentationFixture CreateSettingsWindowFixture()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        var window = new SettingsWindow { DataContext = owner.SettingDialog };
        return CreateNativeFixture(
            window,
            [
                ButtonWithAutomationId("SettingsSaveAndClose", DialogButtonRole.Primary, "settings save and close"),
                ButtonWithAutomationId("SettingsCancel", DialogButtonRole.Quiet, "settings cancel"),
                ButtonWithAutomationId("SettingsAddBmsSearchRoot", DialogButtonRole.Neutral, "settings add BMS root"),
                ButtonWithAutomationId("SettingsRemoveBmsSearchRoot", DialogButtonRole.Quiet, "settings remove BMS root"),
                ButtonWithAutomationId("SettingsEditCustomLr2Paths", DialogButtonRole.Quiet, "settings edit LR2 paths"),
                ButtonWithAutomationId("SettingsResyncLr2SongDb", DialogButtonRole.Neutral, "settings resync song database"),
                ButtonWithAutomationId("SettingsInstallOrRepairLr2PlayHistorySchema", DialogButtonRole.Neutral, "settings install or repair schema"),
                ButtonWithAutomationId("SettingsImportBeatorajaTableUrls", DialogButtonRole.Quiet, "settings import table URLs"),
            ],
            allowsSettingsControlAliases: true,
            cleanup: owner.SettingDialog.Dispose);
    }

    private static PresentationFixture CreateUpdateAvailableFixture()
    {
        var progressHub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        var dialog = new UpdateAvailableDialog(
            UpdateCheckResult.NoUpdate("1.0.0.0"),
            progressHub,
            ExternalShellGatewayPolicy.Current);
        return CreateNativeFixture(
            dialog,
            [
                ButtonWithAutomationId("UpdateApply", DialogButtonRole.Primary, "update affirmative"),
                ButtonWithAutomationId("UpdateOpenReleasePage", DialogButtonRole.Quiet, "release page secondary action"),
                ButtonWithAutomationId("UpdateClose", DialogButtonRole.Quiet, "update close"),
            ],
            cleanup: progressHub.Dispose);
    }

    private static PresentationFixture CreatePlayHistoryPresetFixture()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        PlayHistoryFolderDisplayPresetEditSession session = owner.SettingDialog
            .CreatePlayHistoryFolderDisplayPresetEditSession(null);
        var dialog = new PlayHistoryFolderDisplayPresetEditDialog(owner.SettingDialog, session);
        return CreateNativeFixture(
            dialog,
            [
                ButtonWithAutomationId("PlayHistoryPresetSave", DialogButtonRole.Primary, "play-history preset affirmative"),
                ButtonWithAutomationId("PlayHistoryPresetCancel", DialogButtonRole.Quiet, "play-history preset cancel"),
            ],
            cleanup: owner.SettingDialog.Dispose);
    }

    private static SentinelHost CreateSentinelHost(PresentationFixture fixture)
    {
        var sentinelStyle = new Style(typeof(Button));
        sentinelStyle.Setters.Add(new Setter(FrameworkElement.TagProperty, "outer-sentinel"));
        var outerRoot = new Grid();
        outerRoot.Resources.Add(typeof(Button), sentinelStyle);
        var sentinelButton = new Button { Content = "sentinel" };

        fixture.HostWindow.Content = null;
        outerRoot.Children.Add(fixture.PresentationRoot);
        outerRoot.Children.Add(sentinelButton);
        fixture.HostWindow.Content = outerRoot;
        return new SentinelHost(fixture.HostWindow, sentinelButton, sentinelStyle);
    }

    private static void AssertSentinelStyle(Button sentinelButton, Style sentinelStyle)
    {
        Assert.AreSame(sentinelStyle, sentinelButton.Style, "The outer sentinel style must remain local.");
        Assert.AreEqual("outer-sentinel", sentinelButton.Tag, "The outer sentinel must retain its style semantics.");
    }

    private static void AssertCanonicalPresentation(PresentationFixture fixture)
    {
        FrameworkElement presentationRoot = fixture.PresentationRoot;
        presentationRoot.ApplyTemplate();
        presentationRoot.UpdateLayout();

        IEnumerable<Border> contentBorderCandidates = presentationRoot is Border contentRoot
            ? new[] { contentRoot }.Concat(FindVisualDescendants<Border>(presentationRoot))
            : FindVisualDescendants<Border>(presentationRoot);
        Border[] contentBorders = contentBorderCandidates
            .Where(border =>
            {
                Style contentStyle = border.TryFindResource("App.Canonical.DialogContentStyle") as Style;
                return contentStyle != null && StyleChainContains(border.Style, contentStyle);
            })
            .ToArray();
        Assert.IsTrue(contentBorders.Length > 0, "The dialog must apply the canonical content role.");

        Button[] buttons = FindVisualDescendants<Button>(presentationRoot)
            .Where(item => item.TemplatedParent == null)
            .ToArray();
        ButtonExpectation[] expectations = fixture.ButtonExpectations.ToArray();
        string[] expectedAutomationIds = expectations
            .Select(expectation => expectation.AutomationId)
            .ToArray();
        Assert.IsFalse(
            expectedAutomationIds.Any(string.IsNullOrWhiteSpace),
            "Every dialog button expectation must declare a nonempty semantic identifier.");
        Assert.AreEqual(
            expectedAutomationIds.Length,
            expectedAutomationIds.Distinct(StringComparer.Ordinal).Count(),
            "Fixture metadata must assign a unique semantic identifier to every expected dialog button.");
        Assert.AreEqual(
            expectations.Length,
            buttons.Length,
            $"Fixture metadata must identify every production dialog button (expected={expectations.Length}, actual={buttons.Length}).");

        string[] actualAutomationIds = buttons
            .Select(button => AutomationProperties.GetAutomationId(button))
            .ToArray();
        Assert.IsFalse(
            actualAutomationIds.Any(string.IsNullOrWhiteSpace),
            "Every production dialog button must expose a nonempty stable semantic identifier.");
        Assert.AreEqual(
            actualAutomationIds.Length,
            actualAutomationIds.Distinct(StringComparer.Ordinal).Count(),
            "Production dialog buttons must expose unique semantic identifiers.");

        foreach (ButtonExpectation expectation in expectations)
        {
            Button[] matches = buttons
                .Where(button => string.Equals(
                    AutomationProperties.GetAutomationId(button),
                    expectation.AutomationId,
                    StringComparison.Ordinal))
                .ToArray();
            Assert.AreEqual(
                1,
                matches.Length,
                $"Fixture metadata '{expectation.Description}' must identify exactly one production dialog button with AutomationId '{expectation.AutomationId}'.");
        }

        foreach (Button button in buttons)
        {
            ButtonExpectation[] matches = expectations
                .Where(expectation => string.Equals(
                    AutomationProperties.GetAutomationId(button),
                    expectation.AutomationId,
                    StringComparison.Ordinal))
                .ToArray();
            Assert.AreEqual(
                1,
                matches.Length,
                $"Production dialog button with AutomationId '{AutomationProperties.GetAutomationId(button)}' must have exactly one authority-owned semantic expectation.");
            AssertCanonicalButtonRole(fixture, button, matches[0]);
        }

        AssertCanonicalControlRole<TextBox>(presentationRoot, "TextBox", "App.Canonical.TextBoxStyle");
        AssertCanonicalControlRole<ComboBox>(presentationRoot, "ComboBox", "App.Canonical.ComboBoxStyle");
        AssertCanonicalControlRole<CheckBox>(presentationRoot, "CheckBox", "App.Canonical.CheckBoxStyle");
        AssertCanonicalControlRole<RadioButton>(presentationRoot, "RadioButton", "App.Canonical.RadioButtonStyle");
        AssertCanonicalControlRole<ListBox>(presentationRoot, "ListBox", "App.Canonical.ListBoxStyle");
        AssertCanonicalControlRole<GroupBox>(presentationRoot, "GroupBox", "App.Canonical.GroupBoxStyle");
        AssertCanonicalControlRole<Label>(presentationRoot, "Label", "App.Canonical.LabelStyle");
        AssertCanonicalControlRole<ScrollViewer>(presentationRoot, "ScrollViewer", "App.Canonical.ScrollViewerStyle");

        if (fixture.RequiresOverlayRole)
        {
            AssertCanonicalOverlaySurface(presentationRoot);
        }
    }

    private static void AssertCanonicalOverlaySurface(FrameworkElement presentationRoot)
    {
        const string overlayStyleKey = "App.Canonical.DialogOverlayStyle";
        Rectangle[] overlaySurfaces = FindVisualDescendants<Rectangle>(presentationRoot)
            .Where(item => item.TemplatedParent == null)
            .ToArray();
        Assert.IsTrue(
            overlaySurfaces.Length > 0,
            "An overlay dialog fixture must contain at least one untemplated Rectangle surface.");
        Style overlayStyle = RequireStyle(overlaySurfaces[0], overlayStyleKey);

        Rectangle[] canonicalOverlays = overlaySurfaces
            .Where(overlay => overlay.Style != null && StyleChainContains(overlay.Style, overlayStyle))
            .ToArray();
        Assert.IsTrue(
            canonicalOverlays.Length > 0,
            "An overlay dialog fixture must contain at least one descendant with the same-host canonical overlay role.");

        foreach (Rectangle overlay in canonicalOverlays)
        {
            Assert.AreSame(
                overlayStyle,
                RequireStyle(overlay, overlayStyleKey),
                "The overlay surface must resolve the canonical role from its presentation host.");
        }
    }

    private static void AssertApplicationResourcesDoNotImplicitlyAdoptCanonicalStyle(
        PresentationFixture fixture,
        TestWindowPresentationScope windowTest)
    {
        var probeButton = new Button { Content = "unadopted application probe" };
        var probeWindow = new Window
        {
            Width = 240,
            Height = 80,
            ShowInTaskbar = false,
            Content = probeButton,
        };
        Style canonicalButtonStyle = RequireStyleFromPresentationHost(
            fixture.PresentationRoot,
            "App.Canonical.ButtonStyle");
        try
        {
            // This window has no local resource dictionary or sentinel. It proves that the
            // application resource boundary does not implicitly adopt the dialog control system.
            windowTest.ShowAndWaitForContentRendered(probeWindow);
            probeWindow.UpdateLayout();
            probeButton.ApplyTemplate();

            Assert.IsFalse(
                StyleChainContains(probeButton.Style, canonicalButtonStyle),
                "An unadopted application control must not inherit the canonical Button role implicitly.");
            Assert.IsNotNull(probeButton.Template, "The unadopted probe Button must still materialize a runtime template.");

            ControlTemplate canonicalTemplate = GetEffectiveStyleValue<ControlTemplate>(
                canonicalButtonStyle,
                Control.TemplateProperty);
            ControlTemplate actualTemplate = GetEffectiveStyleValue<ControlTemplate>(
                probeButton.Style,
                Control.TemplateProperty);
            Assert.IsNotNull(canonicalTemplate, "The canonical Button role must define an effective template.");
            Assert.AreNotSame(
                canonicalTemplate,
                actualTemplate,
                "An unadopted application control must not implicitly use the canonical Button template.");
        }
        finally
        {
            if (probeWindow.IsVisible)
            {
                probeWindow.Close();
            }
        }
    }

    private static void AssertCanonicalButtonRole(
        PresentationFixture fixture,
        Button button,
        ButtonExpectation expectation)
    {
        Assert.IsNotNull(button.Style, "Every dialog Button must have an explicit canonical action role.");
        DialogButtonRole actualRole = ResolveMostSpecificButtonRole(button);
        Assert.AreEqual(
            expectation.Role,
            actualRole,
            $"Dialog Button '{expectation.Description}' must resolve to its exact authority-owned role family (AutomationId='{expectation.AutomationId}').");
        string roleKey = GetDialogRoleStyleKey(expectation.Role);
        string baseRoleKey = GetCanonicalBaseRoleStyleKey(expectation.Role);
        Style roleStyle = RequireStyle(button, roleKey);
        Style baseRoleStyle = RequireStyle(button, baseRoleKey);
        bool directRole = StyleChainContains(button.Style, roleStyle);
        bool settingsAliasRole = fixture.AllowsSettingsControlAliases
            && StyleChainContains(button.Style, baseRoleStyle);
        Assert.IsTrue(
            directRole || settingsAliasRole,
            $"Dialog Button '{expectation.Description}' must resolve to its authority-owned '{roleKey}' role (name='{button.Name}', target='{button.Style?.TargetType}', basedOn='{button.Style?.BasedOn?.TargetType}').");

        ControlTemplate expectedTemplate = GetEffectiveStyleValue<ControlTemplate>(
            directRole ? roleStyle : baseRoleStyle,
            Control.TemplateProperty);
        ControlTemplate actualTemplate = GetEffectiveStyleValue<ControlTemplate>(button.Style, Control.TemplateProperty);
        Assert.IsNotNull(expectedTemplate, "Canonical Button action roles must define an effective template.");
        Assert.AreSame(expectedTemplate, actualTemplate, "The Button must use the canonical effective template.");
    }

    private static DialogButtonRole ResolveMostSpecificButtonRole(Button button)
    {
        if (StyleChainContains(button.Style, RequireStyle(button, "App.Canonical.DangerButtonStyle")))
        {
            return DialogButtonRole.Danger;
        }

        if (StyleChainContains(button.Style, RequireStyle(button, "App.Canonical.PrimaryButtonStyle")))
        {
            return DialogButtonRole.Primary;
        }

        if (StyleChainContains(button.Style, RequireStyle(button, "App.Canonical.QuietButtonStyle")))
        {
            return DialogButtonRole.Quiet;
        }

        if (StyleChainContains(button.Style, RequireStyle(button, "App.Canonical.ButtonStyle")))
        {
            return DialogButtonRole.Neutral;
        }

        Assert.Fail(
            $"Dialog Button with AutomationId '{AutomationProperties.GetAutomationId(button)}' does not resolve to a canonical semantic role family.");
        return DialogButtonRole.Neutral;
    }

    private static void AssertCanonicalControlRole<T>(
        FrameworkElement presentationRoot,
        string controlName,
        string canonicalStyleKey)
        where T : Control
    {
        foreach (T control in FindVisualDescendants<T>(presentationRoot).Where(item => item.TemplatedParent == null))
        {
            Style resolvedStyle = RequireStyle(control, canonicalStyleKey);
            Assert.IsNotNull(
                control.Style,
                $"Every {controlName} in the dialog must have an explicit canonical control role.");
            Assert.IsTrue(
                StyleChainContains(control.Style, resolvedStyle),
                $"{controlName} must derive from {canonicalStyleKey}.");
            ControlTemplate expectedTemplate = GetEffectiveStyleValue<ControlTemplate>(resolvedStyle, Control.TemplateProperty);
            ControlTemplate actualTemplate = GetEffectiveStyleValue<ControlTemplate>(control.Style, Control.TemplateProperty);
            Assert.IsNotNull(expectedTemplate, $"{canonicalStyleKey} must define an effective template.");
            Assert.AreSame(expectedTemplate, actualTemplate, $"{controlName} must use its canonical effective template.");
        }
    }

    private static Style RequireStyle(FrameworkElement resourceOwner, string key)
    {
        object resource = resourceOwner.TryFindResource(key);
        Assert.IsInstanceOfType(resource, typeof(Style), $"Missing canonical style resource '{key}'.");
        return (Style)resource;
    }

    private static Style RequireStyleFromPresentationHost(FrameworkElement presentationRoot, string key)
    {
        if (presentationRoot.TryFindResource(key) is Style rootStyle)
        {
            return rootStyle;
        }

        foreach (FrameworkElement descendant in FindVisualDescendants<FrameworkElement>(presentationRoot))
        {
            if (descendant.TryFindResource(key) is Style descendantStyle)
            {
                return descendantStyle;
            }
        }

        Assert.Fail($"Missing canonical style resource '{key}' in the presentation host.");
        return null;
    }

    private static bool StyleChainContains(Style actual, Style expected)
    {
        for (Style candidate = actual; candidate != null; candidate = candidate.BasedOn)
        {
            if (ReferenceEquals(candidate, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static T GetEffectiveStyleValue<T>(Style style, DependencyProperty property)
        where T : class
    {
        for (Style candidate = style; candidate != null; candidate = candidate.BasedOn)
        {
            Setter setter = candidate.Setters.OfType<Setter>().FirstOrDefault(item => item.Property == property);
            if (setter?.Value is T value)
            {
                return value;
            }
        }

        return null;
    }

    private static ButtonExpectation ButtonWithAutomationId(
        string automationId,
        DialogButtonRole role,
        string description)
        => new(description, automationId, role);

    private static string GetDialogRoleStyleKey(DialogButtonRole role)
        => role switch
        {
            DialogButtonRole.Primary => "App.Canonical.DialogPrimaryActionStyle",
            DialogButtonRole.Quiet => "App.Canonical.DialogQuietActionStyle",
            DialogButtonRole.Danger => "App.Canonical.DialogDangerActionStyle",
            _ => "App.Canonical.DialogActionStyle",
        };

    private static string GetCanonicalBaseRoleStyleKey(DialogButtonRole role)
        => role switch
        {
            DialogButtonRole.Primary => "App.Canonical.PrimaryButtonStyle",
            DialogButtonRole.Quiet => "App.Canonical.QuietButtonStyle",
            DialogButtonRole.Danger => "App.Canonical.DangerButtonStyle",
            _ => "App.Canonical.ButtonStyle",
        };

    private static void AssertVisualBoundsInside(Visual ancestor, FrameworkElement descendant)
    {
        Rect bounds = GetVisualBounds(ancestor, descendant);
        Rect ancestorBounds = new(0, 0, ((FrameworkElement)ancestor).ActualWidth, ((FrameworkElement)ancestor).ActualHeight);
        const double tolerance = 0.5;
        Assert.IsTrue(
            bounds.Left >= ancestorBounds.Left - tolerance
                && bounds.Top >= ancestorBounds.Top - tolerance
                && bounds.Right <= ancestorBounds.Right + tolerance
                && bounds.Bottom <= ancestorBounds.Bottom + tolerance,
            $"{descendant.Name} bounds {bounds} exceed content bounds {ancestorBounds}.");
    }

    private static Rect GetVisualBounds(Visual ancestor, FrameworkElement descendant)
        => descendant.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(0, 0, descendant.ActualWidth, descendant.ActualHeight));

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private enum DialogButtonRole
    {
        Neutral,
        Primary,
        Quiet,
        Danger,
    }

    private sealed class ButtonExpectation
    {
        internal ButtonExpectation(string description, string automationId, DialogButtonRole role)
        {
            Description = description;
            AutomationId = automationId;
            Role = role;
        }

        internal string Description { get; }

        internal string AutomationId { get; }

        internal DialogButtonRole Role { get; }
    }

    private sealed class PresentationFixture
    {
        internal PresentationFixture(
            Window hostWindow,
            FrameworkElement presentationRoot,
            bool allowsSettingsControlAliases,
            bool requiresOverlayRole,
            IReadOnlyList<ButtonExpectation> buttonExpectations,
            Action cleanup)
        {
            HostWindow = hostWindow;
            PresentationRoot = presentationRoot;
            AllowsSettingsControlAliases = allowsSettingsControlAliases;
            RequiresOverlayRole = requiresOverlayRole;
            ButtonExpectations = buttonExpectations;
            Cleanup = cleanup;
        }

        internal Window HostWindow { get; }

        internal FrameworkElement PresentationRoot { get; }

        internal bool AllowsSettingsControlAliases { get; }

        internal bool RequiresOverlayRole { get; }

        internal IReadOnlyList<ButtonExpectation> ButtonExpectations { get; }

        internal Action Cleanup { get; }
    }

    private sealed class SentinelHost
    {
        internal SentinelHost(Window window, Button sentinelButton, Style sentinelStyle)
        {
            Window = window;
            SentinelButton = sentinelButton;
            SentinelStyle = sentinelStyle;
        }

        internal Window Window { get; }

        internal Button SentinelButton { get; }

        internal Style SentinelStyle { get; }
    }
}
