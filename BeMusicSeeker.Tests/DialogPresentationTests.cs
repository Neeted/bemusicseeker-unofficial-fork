using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
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
                AssertCanonicalPresentation(fixture.PresentationRoot, fixture.AllowsSettingsControlAliases);
            }
            finally
            {
                if (fixture.HostWindow.IsVisible)
                {
                    fixture.HostWindow.Close();
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
            "ReleaseNotesWindow" => CreateNativeFixture(new ReleaseNotesWindow()),
            "Lr2AdvancedPathsDialog" => CreateLr2AdvancedPathsFixture(),
            "UpdateAvailableDialog" => CreateUpdateAvailableFixture(),
            "PendingDeleteConfirmDialog" => CreateNativeFixture(new PendingDeleteConfirmDialog()),
            "PlayHistoryFolderDisplayPresetEditDialog" => CreatePlayHistoryPresetFixture(),
            "Lr2PlayHistorySchemaUninstallDialog" => CreateNativeFixture(
                new Lr2PlayHistorySchemaUninstallDialog("score.db")),
            "ProgressDialog" => CreateNativeFixture(new ProgressDialog(new ProgressDialogSettings(
                showSubLabel: true,
                showCancelButton: true,
                showProgressBarIndeterminate: true))),
            "ThemedMessageBox" => CreateNativeFixture(ThemedMessageBox.BuildDialogForPresentation(
                owner: null,
                messageBoxText: "Presentation contract message",
                caption: "Presentation",
                button: MessageBoxButton.OKCancel,
                icon: MessageBoxImage.Information,
                initialResult: MessageBoxResult.Cancel,
                warningMessageBoxText: null,
                setResult: _ => { })),
            "InitialSetupLanguageDialog" => CreateOverlayFixture(new InitialSetupLanguageDialog(), 720, 520),
            "LoadPlaylistURIDialog" => CreateOverlayFixture(new LoadPlaylistURIDialog(), 720, 520),
            "PlaylistPropertyDialog" => CreateOverlayFixture(new PlaylistPropertyDialog(), 720, 520),
            "PlaylistSummaryBulkEditDialog" => CreateOverlayFixture(new PlaylistSummaryBulkEditDialog(), 720, 560),
            _ => throw new ArgumentOutOfRangeException(nameof(dialogName), dialogName, "Unknown dialog fixture."),
        };
    }

    private static PresentationFixture CreateNativeFixture(
        Window window,
        bool allowsSettingsControlAliases = false,
        Action cleanup = null)
    {
        FrameworkElement presentationRoot = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("Native dialog content must be a FrameworkElement.");
        return new PresentationFixture(
            window,
            presentationRoot,
            allowsSettingsControlAliases,
            cleanup ?? (() => { }));
    }

    private static PresentationFixture CreateOverlayFixture(
        FrameworkElement presentationRoot,
        double width,
        double height)
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
            cleanup: () => { });
    }

    private static PresentationFixture CreateLr2AdvancedPathsFixture()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        var dialog = new Lr2AdvancedPathsDialog(owner.SettingDialog);
        return CreateNativeFixture(
            dialog,
            allowsSettingsControlAliases: true,
            cleanup: owner.SettingDialog.Dispose);
    }

    private static PresentationFixture CreateSettingsWindowFixture()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        var window = new SettingsWindow { DataContext = owner.SettingDialog };
        return CreateNativeFixture(
            window,
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
        return CreateNativeFixture(dialog, cleanup: progressHub.Dispose);
    }

    private static PresentationFixture CreatePlayHistoryPresetFixture()
    {
        MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
        PlayHistoryFolderDisplayPresetEditSession session = owner.SettingDialog
            .CreatePlayHistoryFolderDisplayPresetEditSession(null);
        var dialog = new PlayHistoryFolderDisplayPresetEditDialog(owner.SettingDialog, session);
        return CreateNativeFixture(dialog, cleanup: owner.SettingDialog.Dispose);
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

    private static void AssertCanonicalPresentation(
        FrameworkElement presentationRoot,
        bool allowsSettingsControlAliases)
    {
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

        foreach (Button button in FindVisualDescendants<Button>(presentationRoot))
        {
            if (button.TemplatedParent == null)
            {
                AssertCanonicalButtonRole(button, allowsSettingsControlAliases);
            }
        }

        AssertCanonicalControlRole<TextBox>(presentationRoot, "TextBox", "App.Canonical.TextBoxStyle");
        AssertCanonicalControlRole<ComboBox>(presentationRoot, "ComboBox", "App.Canonical.ComboBoxStyle");
        AssertCanonicalControlRole<CheckBox>(presentationRoot, "CheckBox", "App.Canonical.CheckBoxStyle");
        AssertCanonicalControlRole<RadioButton>(presentationRoot, "RadioButton", "App.Canonical.RadioButtonStyle");
        AssertCanonicalControlRole<ListBox>(presentationRoot, "ListBox", "App.Canonical.ListBoxStyle");
        AssertCanonicalControlRole<GroupBox>(presentationRoot, "GroupBox", "App.Canonical.GroupBoxStyle");
        AssertCanonicalControlRole<Label>(presentationRoot, "Label", "App.Canonical.LabelStyle");
        AssertCanonicalControlRole<ScrollViewer>(presentationRoot, "ScrollViewer", "App.Canonical.ScrollViewerStyle");

        foreach (Rectangle overlay in FindVisualDescendants<Rectangle>(presentationRoot)
            .Where(item => ReferenceEquals(VisualTreeHelper.GetParent(item), presentationRoot)))
        {
            Style overlayStyle = RequireStyle(overlay, "App.Canonical.DialogOverlayStyle");
            Assert.IsNotNull(overlay.Style, "Every dialog overlay must have an explicit canonical overlay role.");
            Assert.IsTrue(
                StyleChainContains(overlay.Style, overlayStyle),
                "Dialog overlays must use the canonical overlay role.");
        }
    }

    private static void AssertCanonicalButtonRole(
        Button button,
        bool allowsSettingsControlAliases)
    {
        Assert.IsNotNull(button.Style, "Every dialog Button must have an explicit canonical action role.");
        string[] roleKeys = allowsSettingsControlAliases
            ?
            [
                "App.Canonical.DialogDangerActionStyle",
                "App.Canonical.DialogPrimaryActionStyle",
                "App.Canonical.DialogQuietActionStyle",
                "App.Canonical.DialogActionStyle",
                "App.Canonical.DangerButtonStyle",
                "App.Canonical.PrimaryButtonStyle",
                "App.Canonical.IconButtonStyle",
                "App.Canonical.QuietButtonStyle",
                "App.Canonical.ButtonStyle",
            ]
            :
            [
                "App.Canonical.DialogDangerActionStyle",
                "App.Canonical.DialogPrimaryActionStyle",
                "App.Canonical.DialogQuietActionStyle",
                "App.Canonical.DialogActionStyle",
            ];

        string roleKey = roleKeys.FirstOrDefault(key =>
            StyleChainContains(button.Style, RequireStyle(button, key)));
        Assert.IsNotNull(
            roleKey,
            $"Dialog Button style must resolve to a canonical action role (name='{button.Name}', target='{button.Style?.TargetType}', basedOn='{button.Style?.BasedOn?.TargetType}').");

        Style roleStyle = RequireStyle(button, roleKey);
        ControlTemplate expectedTemplate = GetEffectiveStyleValue<ControlTemplate>(roleStyle, Control.TemplateProperty);
        ControlTemplate actualTemplate = GetEffectiveStyleValue<ControlTemplate>(button.Style, Control.TemplateProperty);
        Assert.IsNotNull(expectedTemplate, "Canonical Button action roles must define an effective template.");
        Assert.AreSame(expectedTemplate, actualTemplate, "The Button must use the canonical effective template.");
    }

    private static void AssertCanonicalControlRole<T>(
        FrameworkElement presentationRoot,
        string controlName,
        string canonicalStyleKey)
        where T : Control
    {
        foreach (T control in FindVisualDescendants<T>(presentationRoot).Where(item => item.TemplatedParent == null))
        {
            Style canonicalStyle = RequireStyle(control, canonicalStyleKey);
            Assert.IsNotNull(
                control.Style,
                $"Every {controlName} in the dialog must have an explicit canonical control role.");
            Assert.IsTrue(
                StyleChainContains(control.Style, canonicalStyle),
                $"{controlName} must derive from {canonicalStyleKey}.");
            ControlTemplate expectedTemplate = GetEffectiveStyleValue<ControlTemplate>(canonicalStyle, Control.TemplateProperty);
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

    private sealed class PresentationFixture
    {
        internal PresentationFixture(
            Window hostWindow,
            FrameworkElement presentationRoot,
            bool allowsSettingsControlAliases,
            Action cleanup)
        {
            HostWindow = hostWindow;
            PresentationRoot = presentationRoot;
            AllowsSettingsControlAliases = allowsSettingsControlAliases;
            Cleanup = cleanup;
        }

        internal Window HostWindow { get; }

        internal FrameworkElement PresentationRoot { get; }

        internal bool AllowsSettingsControlAliases { get; }

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
