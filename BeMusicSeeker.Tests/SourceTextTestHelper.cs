using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Source-text tests use this helper so production file splits do not invalidate architecture checks.
/// </summary>
internal static class SourceTextTestHelper
{
    /// <summary>
    /// Reads a production source file from the repository root.
    /// </summary>
    /// <param name="relativePathParts">Path parts relative to the repository root.</param>
    /// <returns>The source text for the requested file.</returns>
    internal static string ReadProductionSourceText(params string[] relativePathParts)
    {
        return File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. relativePathParts]));
    }

    /// <summary>
    /// Reads production source files under a base directory in deterministic order.
    /// </summary>
    /// <param name="baseDirectory">Directory relative to the repository root.</param>
    /// <param name="includePattern">File name pattern such as <c>MainWindow*.cs</c>.</param>
    /// <returns>The concatenated source text for matching files.</returns>
    internal static string ReadProductionSourceTextGlob(string baseDirectory, string includePattern)
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(root, baseDirectory);
        if (!Directory.Exists(directory))
        {
            return string.Empty;
        }

        string[] files = Directory
            .EnumerateFiles(directory, includePattern, SearchOption.AllDirectories)
            .OrderBy(path => GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReadSourceFiles(files);
    }

    /// <summary>
    /// Reads all source files that are considered part of <c>MainWindowViewModel</c> after partial splitting.
    /// </summary>
    /// <returns>The concatenated source text for MainWindowViewModel-related files.</returns>
    internal static string ReadMainWindowViewModelSourceText()
    {
        string root = FindRepositoryRoot();
        string mainWindowViewModelPath = Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string settingDialogViewModelPath = Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindow", "MainWindowViewModel.SettingDialogViewModel.cs");
        string logicalMainWindowViewModelSource = InsertSourceAfterMarker(
            File.ReadAllText(mainWindowViewModelPath),
            "    internal event EventHandler MainTableDisplayRefreshRequested;",
            ExtractNestedMainWindowViewModelSource(
                settingDialogViewModelPath));

        string[] splitFiles = Directory.EnumerateFiles(Path.Combine(root, "BeMusicSeeker", "ViewModels"), "MainWindowViewModel*.cs", SearchOption.TopDirectoryOnly)
            .Concat(EnumerateDirectoryFiles(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindow")))
            .Where(path => !string.Equals(path, mainWindowViewModelPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(path, settingDialogViewModelPath, StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return string.Join(
            Environment.NewLine,
            splitFiles.Select(path => File.ReadAllText(path)).Prepend(logicalMainWindowViewModelSource));
    }

    /// <summary>
    /// Reads all source files that are considered part of <c>MainWindow</c> after partial splitting.
    /// </summary>
    /// <returns>The concatenated source text for MainWindow-related files.</returns>
    internal static string ReadMainWindowSourceText()
    {
        string root = FindRepositoryRoot();
        return ReadSourceFileSet(
            root,
            EnumerateExistingFiles(
                Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs")),
            Directory.EnumerateFiles(Path.Combine(root, "BeMusicSeeker", "Views"), "MainWindow*.cs", SearchOption.TopDirectoryOnly),
            EnumerateDirectoryFiles(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow")));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
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

    private static string ReadSourceFileSet(string root, params IEnumerable<string>[] fileGroups)
    {
        string[] files = fileGroups
            .SelectMany(group => group)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ReadSourceFiles(files);
    }

    private static string ReadSourceFiles(IEnumerable<string> files)
    {
        return string.Join(
            Environment.NewLine,
            files.Select(path => File.ReadAllText(path)));
    }

    private static string InsertSourceAfterMarker(string source, string marker, string insertedSource)
    {
        if (string.IsNullOrEmpty(insertedSource))
        {
            return source;
        }

        int markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidOperationException("MainWindowViewModel source insertion marker was not found.");
        }

        int insertIndex = markerIndex + marker.Length;
        return source.Substring(0, insertIndex)
            + Environment.NewLine
            + Environment.NewLine
            + insertedSource
            + source.Substring(insertIndex);
    }

    private static string ExtractNestedMainWindowViewModelSource(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Nested MainWindowViewModel source file was not found.", path);
        }

        string source = File.ReadAllText(path);
        const string startMarker = "    public partial class SettingDialogViewModel : ViewModel";
        const string endMarker = "\n}";
        int startIndex = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            throw new InvalidOperationException("Nested MainWindowViewModel start marker was not found.");
        }

        int endIndex = source.LastIndexOf(endMarker, StringComparison.Ordinal);
        if (endIndex <= startIndex)
        {
            throw new InvalidOperationException("Nested MainWindowViewModel end marker was not found.");
        }

        return source.Substring(startIndex, endIndex - startIndex);
    }

    private static IEnumerable<string> EnumerateExistingFiles(params string[] paths)
    {
        return paths.Where(File.Exists);
    }

    private static IEnumerable<string> EnumerateDirectoryFiles(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            : [];
    }

    private static string GetRelativePath(string root, string path)
    {
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        Uri rootUri = new Uri(rootWithSeparator);
        Uri pathUri = new Uri(path);
        return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
    }
}
