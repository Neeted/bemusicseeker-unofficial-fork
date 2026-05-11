using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Livet.Behaviors.Messaging;
using Livet.Messaging;
using Livet.Messaging.IO;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace BeMusicSeeker.Views;

internal class CommonOpenFileDialogInteractionMessageAction : InteractionMessageAction<FrameworkElement>
{
    protected override void InvokeAction(InteractionMessage message)
    {
        if (message is FolderSelectionMessage folderSelectionMessage)
        {
            ShowFolderSelectionDialog(folderSelectionMessage);
        }
        else if (message is OpeningFileSelectionMessage fileSelectionMessage)
        {
            ShowFileSelectionDialog(fileSelectionMessage);
        }
    }

    private void ShowFolderSelectionDialog(FolderSelectionMessage message)
    {
        using CommonOpenFileDialog dialog = new CommonOpenFileDialog
        {
            Title = message.Title,
            IsFolderPicker = true,
            EnsurePathExists = true,
            Multiselect = false
        };
        SetInitialDirectory(dialog, message.SelectedPath);

        if (dialog.ShowDialog(Window.GetWindow(AssociatedObject)) == CommonFileDialogResult.Ok)
        {
            message.Response = dialog.FileName;
        }
    }

    private void ShowFileSelectionDialog(OpeningFileSelectionMessage message)
    {
        using CommonOpenFileDialog dialog = new CommonOpenFileDialog
        {
            Title = message.Title,
            IsFolderPicker = false,
            EnsureFileExists = true,
            EnsurePathExists = true,
            Multiselect = message.MultiSelect,
            DefaultFileName = message.FileName
        };
        string defaultExtension = InferDefaultExtensionForTest(message.FileName, message.Filter);
        if (!string.IsNullOrWhiteSpace(defaultExtension))
        {
            dialog.DefaultExtension = defaultExtension;
        }
        SetInitialDirectory(dialog, message.InitialDirectory);
        foreach (Tuple<string, string> filter in ParseFilterPairsForTest(message.Filter))
        {
            dialog.Filters.Add(new CommonFileDialogFilter(filter.Item1, filter.Item2));
        }

        if (dialog.ShowDialog(Window.GetWindow(AssociatedObject)) == CommonFileDialogResult.Ok)
        {
            message.Response = dialog.FileNames.ToArray();
        }
    }

    internal static IReadOnlyList<Tuple<string, string>> ParseFilterPairsForTest(string filter)
    {
        List<Tuple<string, string>> filters = new List<Tuple<string, string>>();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            string[] parts = filter.Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                string displayName = parts[i];
                string extensionList = parts[i + 1];
                if (string.IsNullOrWhiteSpace(extensionList))
                {
                    continue;
                }
                filters.Add(Tuple.Create(
                    string.IsNullOrWhiteSpace(displayName) ? extensionList : displayName,
                    extensionList));
            }
        }
        if (filters.Count == 0)
        {
            filters.Add(Tuple.Create("*.*", "*.*"));
        }
        return filters;
    }

    internal static string InferDefaultExtensionForTest(string fileName, string filter)
    {
        string extension = GetExtensionWithoutDot(fileName);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension;
        }
        foreach (Tuple<string, string> filterPair in ParseFilterPairsForTest(filter))
        {
            foreach (string pattern in (filterPair.Item2 ?? string.Empty).Split(';'))
            {
                extension = GetExtensionWithoutDot(pattern);
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    return extension;
                }
            }
        }
        return null;
    }

    private static string GetExtensionWithoutDot(string pathOrPattern)
    {
        if (string.IsNullOrWhiteSpace(pathOrPattern) || pathOrPattern.Contains("?"))
        {
            return null;
        }
        string value = pathOrPattern.Trim();
        if (value == "*.*")
        {
            return null;
        }
        try
        {
            string extension = Path.GetExtension(value);
            if (string.IsNullOrWhiteSpace(extension) || extension == "." || extension.Contains("*") || extension.Contains("?"))
            {
                return null;
            }
            return extension.TrimStart('.');
        }
        catch
        {
            return null;
        }
    }

    internal static string ResolveInitialDirectoryForTest(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            if (Directory.Exists(path))
            {
                return path;
            }
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }
        catch
        {
        }
        return null;
    }

    internal static void SetInitialDirectory(CommonOpenFileDialog dialog, string path)
    {
        string initialDirectory = ResolveInitialDirectoryForTest(path);
        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
            dialog.DefaultDirectory = initialDirectory;
        }
    }
}
