using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal sealed class DroppedInstallBatchRequest
{
    public string[] Paths { get; }

    public int PathCount => Paths.Length;

    public string DisplayName { get; }

    public DroppedInstallBatchRequest(IEnumerable<string> paths)
    {
        Paths = (paths ?? Enumerable.Empty<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        DisplayName = GetDisplayName(Paths.FirstOrDefault());
    }

    private static string GetDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }
}
