using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using BeMusicSeeker;
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
    public void DirectDialogRouteFiles_MatchDocumentedBoundaries()
    {
        string root = FindRepositoryRoot();
        string inventoryPath = Path.Combine(root, "devdocs", "spec", "ui", "dialogs.md");
        string inventory = File.ReadAllText(inventoryPath);
        IReadOnlyList<string> documentedFiles = ReadDocumentedDirectSourceFiles(inventory);
        IReadOnlyList<string> actualFiles = FindLegacyDialogRouteFiles(root);

        CollectionAssert.AreEquivalent(
            documentedFiles.ToList(),
            actualFiles.ToList(),
            "直接表示を行う実装は devdocs/spec/ui/dialogs.md の許可範囲と一致する必要があります。");
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
        Assembly productionAssembly = typeof(App).Assembly;
        IReadOnlyList<(MethodBase Caller, MethodBase Callee)> nativeMessageBoxCalls =
            FindCallsTo(
                productionAssembly,
                method => method.DeclaringType == typeof(MessageBox)
                    && method.Name == nameof(MessageBox.Show));
        IReadOnlyList<(MethodBase Caller, MethodBase Callee)> emergencyDialogCalls =
            FindCallsTo(
                productionAssembly,
                method => method.DeclaringType == typeof(EmergencyDialog)
                    && method.Name == nameof(EmergencyDialog.Show));

        Assert.AreEqual(
            1,
            nativeMessageBoxCalls.Count,
            "The compiled production call graph must contain exactly one System.Windows.MessageBox.Show call.\n"
                + FormatCalls(nativeMessageBoxCalls));
        foreach (var call in nativeMessageBoxCalls)
        {
            Assert.AreEqual(
                typeof(EmergencyDialog),
                GetTopLevelDeclaringType(call.Caller),
                $"System.Windows.MessageBox.Show must be called by top-level EmergencyDialog, but was {DescribeMethod(call.Caller)}.\n"
                    + FormatCalls(nativeMessageBoxCalls));
        }

        Assert.IsTrue(
            emergencyDialogCalls.Count > 0,
            "The compiled production call graph must contain an EmergencyDialog.Show call from App.\n"
                + FormatCalls(emergencyDialogCalls));
        foreach (var call in emergencyDialogCalls)
        {
            Assert.AreEqual(
                typeof(App),
                GetTopLevelDeclaringType(call.Caller),
                $"EmergencyDialog.Show must be called by top-level App, but was {DescribeMethod(call.Caller)}.\n"
                    + FormatCalls(emergencyDialogCalls));
        }
    }

    private static IReadOnlyList<(MethodBase Caller, MethodBase Callee)> FindCallsTo(
        Assembly assembly,
        Func<MethodBase, bool> calleePredicate)
    {
        List<(MethodBase Caller, MethodBase Callee)> calls = [];
        foreach (Type type in assembly
            .GetTypes()
            .OrderBy(type => type.FullName ?? type.Name, StringComparer.Ordinal))
        {
            foreach (MethodBase caller in EnumerateDeclaredMembers(type)
                .OrderBy(method => DescribeMethod(method), StringComparer.Ordinal))
            {
                foreach (MethodBase callee in StartupLibraryConstructionTestSupport.EnumerateCalledMethods(caller))
                {
                    if (calleePredicate(callee))
                    {
                        calls.Add((caller, callee));
                    }
                }
            }
        }

        return calls;
    }

    private static IEnumerable<MethodBase> EnumerateDeclaredMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        HashSet<MethodBase> yielded = [];

        foreach (MethodBase method in type.GetMethods(flags).Cast<MethodBase>())
        {
            if (yielded.Add(method))
            {
                yield return method;
            }
        }

        foreach (ConstructorInfo constructor in type.GetConstructors(flags))
        {
            if (yielded.Add(constructor))
            {
                yield return constructor;
            }
        }

        if (type.TypeInitializer is ConstructorInfo typeInitializer && yielded.Add(typeInitializer))
        {
            yield return typeInitializer;
        }
    }

    private static Type? GetTopLevelDeclaringType(MethodBase method)
    {
        Type? declaringType = method.DeclaringType;
        while (declaringType?.DeclaringType != null)
        {
            declaringType = declaringType.DeclaringType;
        }

        return declaringType;
    }

    private static string FormatCalls(IEnumerable<(MethodBase Caller, MethodBase Callee)> calls)
    {
        string[] descriptions = calls
            .Select(call => $"{DescribeMethod(call.Caller)} -> {DescribeMethod(call.Callee)}")
            .ToArray();
        return descriptions.Length == 0
            ? "(no matching compiled call edges)"
            : string.Join(Environment.NewLine, descriptions);
    }

    private static string DescribeMethod(MethodBase method)
        => $"{method.DeclaringType?.FullName ?? "<global>"}.{method.Name}";

    private static IReadOnlyList<string> ReadDocumentedDirectSourceFiles(string inventory)
    {
        string section = ExtractBetween(inventory, "### 直接表示を許可する実装", "### 要求と失敗の扱い");
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
