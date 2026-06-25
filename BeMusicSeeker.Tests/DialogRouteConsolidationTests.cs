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
        new Regex(@"System\.Windows\.MessageBox\.Show", RegexOptions.Compiled),
        new Regex(@"(?<!Themed)MessageBox\.Show\(", RegexOptions.Compiled),
        new Regex(@"new ConfirmationMessage", RegexOptions.Compiled),
        new Regex(@"InteractionMessageAction<FrameworkElement>", RegexOptions.Compiled),
        new Regex(@"RaiseInteractionMessageOnUiThread", RegexOptions.Compiled),
        new Regex(@"CommonOpenFileDialog", RegexOptions.Compiled),
        new Regex(@"OpenFileDialog", RegexOptions.Compiled),
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
        string dispatcherMessageBox = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "Utils", "DispatcherMessageBox.cs"));
        string themedDialogActions = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "ThemedDialogInteractionMessageActions.cs"));

        StringAssert.Contains(dispatcherMessageBox, "UiDialogCoordinator");
        Assert.IsFalse(dispatcherMessageBox.Contains("MessageBox.Show("), "DispatcherMessageBox must not fall back to the standard WPF MessageBox route.");
        StringAssert.Contains(themedDialogActions, "UiDialogCoordinator");
        Assert.IsFalse(themedDialogActions.Contains("ThemedMessageBox.Show("), "Livet dialog actions should go through UiDialogCoordinator.");
        StringAssert.Contains(themedDialogActions, "UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser => ThemedMessageBox.ToConfirmationResponse(result.MessageBoxResult)");
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
