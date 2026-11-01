using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryParentFolderCacheServiceTests
{
    /// <summary>
    /// LR2フォルダが存在するrootでもbmsonの所持パスがあれば親候補へ残し、譜面なしの除外をそのまま適用しない。
    /// </summary>
    [TestMethod]
    [DataRow(".bms", false)]
    [DataRow(".bmson", true)]
    public void BuildParentFolderCandidates_BmsonChartKeepsRootEvenWhenLr2FolderExists(string extension, bool trailingSeparator)
    {
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace1.RootDirectory, "BeMusicSeeker_ParentFolderCache_" + Guid.NewGuid().ToString("N"));
        string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
        string chartDirectoryPath = Path.Combine(libraryRootPath, "Package");
        string chartPath = Path.Combine(chartDirectoryPath, "chart" + extension);
        string customFolderPath = Path.Combine(tempRootPath, "CustomFolder");
        Directory.CreateDirectory(chartDirectoryPath);
        Directory.CreateDirectory(customFolderPath);
        File.WriteAllText(Path.Combine(chartDirectoryPath, "table.lr2folder"), string.Empty);
        try
        {
            var service = new BmsLibraryParentFolderCacheService();
            BmsLibraryOptionsSnapshot options = CreateLr2Options(customFolderPath);
            if (trailingSeparator) { libraryRootPath += Path.DirectorySeparatorChar; }
            List<string> emptyResult = service.BuildParentFolderCandidates([libraryRootPath], [], options);
            List<string> bmsonResult = service.BuildParentFolderCandidates([libraryRootPath], [chartPath], options);

            CollectionAssert.DoesNotContain(emptyResult, libraryRootPath);
            CollectionAssert.Contains(bmsonResult, libraryRootPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// LR2出力先を通常・追加・root型の設定から除外し、所持譜面があっても出力先の子を親候補に混入させない。
    /// </summary>
    [TestMethod]
    public void BuildParentFolderCandidates_ExcludesCustomFolderOutputBasesEvenWhenChartsExist()
    {
        using var caseWorkspace2 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(caseWorkspace2.RootDirectory, "BeMusicSeeker_ParentFolderCache_" + Guid.NewGuid().ToString("N"));
        try
        {
            string libraryRootPath = Path.Combine(tempRootPath, "LibraryRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string additionalOutputBase = Path.Combine(tempRootPath, "AdditionalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "PlaylistOutput");
            Directory.CreateDirectory(libraryRootPath);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(additionalOutputBase);
            Directory.CreateDirectory(rootOutputChild);

            var service = new BmsLibraryParentFolderCacheService();
            BmsLibraryOptionsSnapshot options = CreateLr2Options(
                normalOutputBase + Path.DirectorySeparatorChar,
                [additionalOutputBase + Path.DirectorySeparatorChar],
                rootOutputBase + Path.DirectorySeparatorChar);
            List<string> result = service.BuildParentFolderCandidates(
                [libraryRootPath, normalOutputBase, additionalOutputBase, rootOutputChild],
                [
                    Path.Combine(libraryRootPath, "chart.bms"),
                    Path.Combine(normalOutputBase, "chart.bms"),
                    Path.Combine(additionalOutputBase, "chart.bms"),
                    Path.Combine(rootOutputChild, "chart.bms")
                ],
                options);

            CollectionAssert.Contains(result, libraryRootPath);
            CollectionAssert.DoesNotContain(result, normalOutputBase);
            CollectionAssert.DoesNotContain(result, additionalOutputBase);
            CollectionAssert.DoesNotContain(result, rootOutputChild);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static BmsLibraryOptionsSnapshot CreateLr2Options(string customFolderPath)
    {
        return CreateLr2Options(customFolderPath, [], Path.Combine(customFolderPath, "RootType"));
    }

    private static BmsLibraryOptionsSnapshot CreateLr2Options(
        string customFolderPath,
        IReadOnlyList<string> additionalOutputBaseDirectories,
        string rootOutputBaseDirectory)
    {
        return new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2CustomFolderOutputBaseDir = customFolderPath,
            LR2CustomFolderAdditionalOutputBaseDirs = additionalOutputBaseDirectories,
            LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDirectory
        };
    }

}
