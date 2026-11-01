namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数moduleで共用する既存の準備・観測境界を保持します。</summary>
internal static class ThemeTestFiles
{

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
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
}
