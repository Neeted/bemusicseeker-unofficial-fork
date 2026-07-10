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
        string mainWindowViewModelSplitDirectory = Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindow");
        string[] nestedSourcePaths =
        [
            Path.Combine(mainWindowViewModelSplitDirectory, "MainWindowViewModel.SettingDialogViewModel.cs"),
            Path.Combine(mainWindowViewModelSplitDirectory, "MainWindowViewModel.PlaylistSummaryBulkEditDialogViewModel.cs"),
            Path.Combine(mainWindowViewModelSplitDirectory, "MainWindowViewModel.PlaylistPropertyDialogViewModel.cs")
        ];
        string logicalMainWindowViewModelSource = InsertSourceAfterMarker(
            File.ReadAllText(mainWindowViewModelPath),
            "    internal event EventHandler PlaybackStarted;",
            string.Join(
                Environment.NewLine,
                nestedSourcePaths.Select(ExtractMainWindowViewModelPartialBody)));

        string[] splitFiles = Directory.EnumerateFiles(Path.Combine(root, "BeMusicSeeker", "ViewModels"), "MainWindowViewModel*.cs", SearchOption.TopDirectoryOnly)
            .Concat(EnumerateDirectoryFiles(mainWindowViewModelSplitDirectory))
            .Where(path => !string.Equals(path, mainWindowViewModelPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => !nestedSourcePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return string.Join(
            Environment.NewLine,
            splitFiles.Select(path => File.ReadAllText(path)).Prepend(logicalMainWindowViewModelSource));
    }

    /// <summary>
    /// Reads one method body from the logical <c>MainWindowViewModel</c> source set.
    /// </summary>
    /// <param name="signature">A unique method signature fragment.</param>
    /// <returns>The method body including the outer braces.</returns>
    internal static string ReadMainWindowViewModelMethodBody(string signature)
    {
        return ExtractMethodBody(ReadMainWindowViewModelSourceText(), signature);
    }

    /// <summary>
    /// Reads the playlist workspace child ViewModel source directly.
    /// </summary>
    /// <returns>The source text for <c>PlaylistWorkspaceViewModel</c>.</returns>
    internal static string ReadPlaylistWorkspaceViewModelSourceText()
    {
        return ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistWorkspaceViewModel.cs");
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

    /// <summary>
    /// Reads all source files that are considered part of <c>BMSLibrary</c> after facade splitting.
    /// </summary>
    /// <returns>The concatenated source text for BMSLibrary-related files.</returns>
    internal static string ReadBmsLibrarySourceText()
    {
        string root = FindRepositoryRoot();
        string modelsDirectory = Path.Combine(root, "BeMusicSeeker", "Models");
        return ReadSourceFileSet(
            root,
            EnumerateExistingFiles(Path.Combine(modelsDirectory, "BMSLibrary.cs")),
            Directory.EnumerateFiles(modelsDirectory, "BMSLibrary*.cs", SearchOption.TopDirectoryOnly),
            EnumerateDirectoryFiles(Path.Combine(modelsDirectory, "BmsLibrary")),
            EnumerateDirectoryFiles(Path.Combine(modelsDirectory, "BmsLibraryInternal")));
    }

    /// <summary>
    /// Extracts a method body from source text using a unique signature fragment.
    /// </summary>
    /// <param name="text">Source text to search.</param>
    /// <param name="signature">A unique method signature fragment.</param>
    /// <returns>The method body including the outer braces.</returns>
    internal static string ExtractMethodBody(string text, string signature)
    {
        int signatureIndex = text.IndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
        {
            throw new InvalidOperationException("Method signature was not found.");
        }
        int braceIndex = text.IndexOf('{', signatureIndex);
        if (braceIndex < 0)
        {
            throw new InvalidOperationException("Method body start was not found.");
        }
        int depth = 0;
        for (int index = braceIndex; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(braceIndex, index - braceIndex + 1);
                }
            }
        }

        throw new InvalidOperationException("Method body end was not found.");
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

    private static string ExtractMainWindowViewModelPartialBody(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Nested MainWindowViewModel source file was not found.", path);
        }

        string source = File.ReadAllText(path);
        const string endMarker = "\n}";
        const string wrapperMarker = "public partial class MainWindowViewModel";
        int wrapperIndex = source.IndexOf(wrapperMarker, StringComparison.Ordinal);
        if (wrapperIndex < 0)
        {
            throw new InvalidOperationException("MainWindowViewModel partial wrapper marker was not found.");
        }

        int wrapperOpenBraceIndex = source.IndexOf('{', wrapperIndex);
        if (wrapperOpenBraceIndex < 0)
        {
            throw new InvalidOperationException("MainWindowViewModel partial wrapper opening brace was not found.");
        }

        int endIndex = source.LastIndexOf(endMarker, StringComparison.Ordinal);
        if (endIndex <= wrapperOpenBraceIndex)
        {
            throw new InvalidOperationException("MainWindowViewModel partial wrapper closing brace was not found.");
        }

        int bodyStartIndex = wrapperOpenBraceIndex + 1;
        if (bodyStartIndex < source.Length && source[bodyStartIndex] == '\r')
        {
            bodyStartIndex++;
        }

        if (bodyStartIndex < source.Length && source[bodyStartIndex] == '\n')
        {
            bodyStartIndex++;
        }

        return source.Substring(bodyStartIndex, endIndex - bodyStartIndex);
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
