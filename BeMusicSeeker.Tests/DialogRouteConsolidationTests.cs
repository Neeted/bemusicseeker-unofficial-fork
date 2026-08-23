using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DialogRouteConsolidationTests
{
    private static readonly Regex[] LegacyRoutePatterns =
    [
        new Regex(@"DispatcherMessageBox\.Show", RegexOptions.Compiled),
        new Regex(@"internal static class DispatcherMessageBox", RegexOptions.Compiled),
        new Regex(@"internal static class UiDialogLegacyAdapter", RegexOptions.Compiled),
        new Regex(@"UiDialogLegacyAdapter\.ShowMessageBox", RegexOptions.Compiled),
        new Regex(@"System\.Windows\.MessageBox\.Show", RegexOptions.Compiled),
        new Regex(@"(?<!Themed)MessageBox\.Show\(", RegexOptions.Compiled),
        new Regex(@"new ConfirmationMessage", RegexOptions.Compiled),
        new Regex(@"InteractionMessageAction<FrameworkElement>", RegexOptions.Compiled),
        new Regex(@"RaiseInteractionMessageOnUiThread", RegexOptions.Compiled),
        new Regex(@"InteractionMessageTrigger", RegexOptions.Compiled),
        new Regex(@"MessageKey=", RegexOptions.Compiled),
        new Regex(@"CommonOpenFileDialog", RegexOptions.Compiled),
        new Regex(@"OpenFileDialog", RegexOptions.Compiled),
        new Regex(@"OpenFolderDialog", RegexOptions.Compiled),
        new Regex(@"SaveFileDialog", RegexOptions.Compiled),
        new Regex(@"FolderBrowserDialog", RegexOptions.Compiled),
        new Regex(@"\.ShowDialog\(", RegexOptions.Compiled),
        new Regex(@"ProgressDialog\.Execute", RegexOptions.Compiled),
        new Regex(@"ProgressDialog\.Current", RegexOptions.Compiled),
    ];

    [TestMethod]
    public void LegacyDialogRouteFiles_AreDocumentedInInventory()
    {
        string root = FindRepositoryRoot();
        string inventoryPath = Path.Combine(root, "devdocs", "spec", "dialog-route-inventory.md");
        string inventory = File.ReadAllText(inventoryPath);
        IReadOnlyList<string> documentedFiles = ReadDocumentedLegacySourceFiles(inventory);
        IReadOnlyList<string> actualFiles = FindLegacyDialogRouteFiles(root);

        CollectionAssert.AreEquivalent(
            documentedFiles.ToList(),
            actualFiles.ToList(),
            "Legacy dialog routes must be tracked in devdocs/spec/dialog-route-inventory.md before they are migrated.");
    }

    [TestMethod]
    public void UiDialogResult_SeparatesUserChoicesFromDisplayFailures()
    {
        Assert.AreEqual(UiDialogStatus.Accepted, UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK).Status);
        Assert.AreEqual(UiDialogStatus.Accepted, UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes).Status);
        Assert.AreEqual(UiDialogStatus.Rejected, UiDialogResult.FromMessageBoxResult(MessageBoxResult.No).Status);
        Assert.AreEqual(UiDialogStatus.CancelledByUser, UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel).Status);
        Assert.AreEqual(UiDialogStatus.ClosedByUser, UiDialogResult.FromMessageBoxResult(MessageBoxResult.None).Status);
        Assert.IsTrue(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes).IsPositive);
        Assert.IsTrue(UiDialogResult.ClosedByUser(MessageBoxResult.Yes).IsPositive);
        Assert.IsFalse(UiDialogResult.ClosedByUser(MessageBoxResult.No).IsPositive);
        Assert.AreEqual(UiDialogStatus.ClosedByUser, UiDialogResult.ClosedByUser(MessageBoxResult.Cancel).Status);
        Assert.AreEqual(MessageBoxResult.Cancel, UiDialogResult.ClosedByUser(MessageBoxResult.Cancel).MessageBoxResult);
        Assert.AreEqual(UiDialogStatus.OwnerUnavailable, UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable).Status);
        Assert.AreEqual(UiDialogStatus.DispatcherUnavailable, UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable).Status);
        Assert.IsFalse(UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable).IsAccepted);
    }

    [TestMethod]
    public void LegacyDialogEntryPoints_AreCoordinatorBackedAndDoNotUseStandardFallback()
    {
        string root = FindRepositoryRoot();
        string bmsLibraryDialogService = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryDialogService.cs"));
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string emergencyDialog = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "EmergencyDialog.cs"));

        Assert.IsFalse(File.Exists(Path.Combine(root, "BeMusicSeeker", "Models", "Utils", "DispatcherMessageBox.cs")), "DispatcherMessageBox should be removed after legacy call sites move to coordinator-backed routes.");
        Assert.IsFalse(File.Exists(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiDialogLegacyAdapter.cs")), "UiDialogLegacyAdapter should be removed after legacy entrypoints are retired.");
        StringAssert.Contains(bmsLibraryDialogService, "UiDialogCoordinator");
        Assert.IsFalse(bmsLibraryDialogService.Contains("DispatcherMessageBox"), "BmsLibraryDialogService must not route model dialogs through DispatcherMessageBox.");
        Assert.IsFalse(bmsLibraryDialogService.Contains("UiDialogLegacyAdapter"), "BmsLibraryDialogService must not route through the retired legacy adapter.");
        StringAssert.Contains(emergencyDialog, "MessageBox.Show(");
        Assert.IsFalse(File.Exists(Path.Combine(root, "BeMusicSeeker", "Views", "ThemedDialogInteractionMessageActions.cs")), "Livet message box actions should be removed after notification routes move to the coordinator.");
        Assert.IsFalse(mainWindowXaml.Contains("MessageKey=\"InformationDialog\""), "MainWindow must not keep unused Livet information dialog triggers.");
        Assert.IsFalse(mainWindowXaml.Contains("MessageKey=\"ConfirmationDialog\""), "MainWindow must not keep unused Livet confirmation dialog triggers.");
    }

    [TestMethod]
    public void ViewCodeBehindMessages_DoNotUseDispatcherMessageBox()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "BeMusicSeeker", "Views");
        List<string> offenders = Directory
            .EnumerateFiles(viewsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("DispatcherMessageBox.Show("))
            .Select(path => NormalizeRelativePath(new Uri(root + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString()))
            .ToList();

        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            offenders,
            "View code-behind must not use the retired DispatcherMessageBox route.");
    }

    [TestMethod]
    public void ViewModels_DoNotUseDispatcherMessageBox()
    {
        string root = FindRepositoryRoot();
        string viewModelsRoot = Path.Combine(root, "BeMusicSeeker", "ViewModels");
        List<string> offenders = Directory
            .EnumerateFiles(viewModelsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("DispatcherMessageBox.Show("))
            .Select(path => NormalizeRelativePath(new Uri(root + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString()))
            .ToList();

        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            offenders,
            "ViewModel messages must not use the retired DispatcherMessageBox route.");
    }

    [TestMethod]
    public void StandardMessageBox_IsLimitedToEmergencyDialog()
    {
        string root = FindRepositoryRoot();
        List<string> offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionAppSourceFile)
            .Where(path => File.ReadAllText(path).Contains("MessageBox.Show("))
            .Select(path => NormalizeRelativePath(new Uri(root + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString()))
            .ToList();
        List<string> emergencyCallers = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionAppSourceFile)
            .Where(path => File.ReadAllText(path).Contains("EmergencyDialog.Show("))
            .Select(path => NormalizeRelativePath(new Uri(root + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString()))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        CollectionAssert.AreEquivalent(
            new[] { "BeMusicSeeker/Views/Dialogs/EmergencyDialog.cs" },
            offenders,
            "Standard MessageBox.Show is allowed only in the explicit emergency dialog boundary.");
        CollectionAssert.AreEquivalent(
            new[] { "BeMusicSeeker/App.cs" },
            emergencyCallers,
            "EmergencyDialog.Show may be called only from App startup/shutdown/unhandled-exception emergency routes.");
    }

    private static IReadOnlyList<string> ReadDocumentedLegacySourceFiles(string inventory)
    {
        string section = ExtractBetween(inventory, "## Legacy Source Files", "## Route Classification Axes");
        MatchCollection matches = Regex.Matches(section, @"\| `([^`]+)` \|");
        return matches
            .Cast<Match>()
            .Select(match => NormalizeRelativePath(match.Groups[1].Value))
            .Where(path => path.StartsWith("BeMusicSeeker/", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> FindLegacyDialogRouteFiles(string root)
    {
        string sourceRoot = Path.Combine(root, "BeMusicSeeker");
        return Directory.GetFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                AbsolutePath = path,
                RelativePath = NormalizeRelativePath(GetRelativePath(root, path))
            })
            .Where(file => LegacyRoutePatterns.Any(pattern => pattern.IsMatch(File.ReadAllText(file.AbsolutePath))))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsProductionAppSourceFile(string path)
    {
        string relativePath = NormalizeRelativePath(GetRelativePath(FindRepositoryRoot(), path));
        return !relativePath.StartsWith("BeMusicSeeker.Tests/", StringComparison.Ordinal)
            && !relativePath.StartsWith("BeMusicSeeker.Updater/", StringComparison.Ordinal)
            && !relativePath.StartsWith("tools/", StringComparison.Ordinal)
            && !relativePath.StartsWith("obj/", StringComparison.Ordinal)
            && !relativePath.StartsWith("bin/", StringComparison.Ordinal)
            && relativePath.IndexOf("/obj/", StringComparison.Ordinal) < 0
            && relativePath.IndexOf("/bin/", StringComparison.Ordinal) < 0;
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string currentDirectory = directory!;
            if (File.Exists(Path.Combine(currentDirectory, "BeMusicSeeker.sln")))
            {
                return currentDirectory;
            }

            directory = Directory.GetParent(currentDirectory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find repository root from " + AppContext.BaseDirectory);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string GetRelativePath(string root, string path)
    {
        string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Uri.UnescapeDataString(new Uri(rootWithSeparator).MakeRelativeUri(new Uri(path)).ToString()).Replace('/', Path.DirectorySeparatorChar);
    }

    private static string ExtractBetween(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, "Start marker was not found: " + start);
        startIndex += start.Length;
        int endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.IsTrue(endIndex >= 0, "End marker was not found: " + end);
        return text.Substring(startIndex, endIndex - startIndex);
    }
}
