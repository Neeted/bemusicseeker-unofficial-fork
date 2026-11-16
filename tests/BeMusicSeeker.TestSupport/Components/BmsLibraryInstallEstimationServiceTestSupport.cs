using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class BmsLibraryInstallEstimationServiceTestSupport
{
    /// <summary>ケース所有の設定から、同じ条件の導入先推定サービスを構成します。</summary>
    internal static BmsLibraryInstallEstimationService CreateService(BeMusicSeeker.Properties.Settings testSettings)
    {
        return new BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot.CreateCurrent(testSettings));
    }

    /// <summary>指定された識別・配置・資源条件から、各ケースが観測する譜面値を作ります。</summary>
    internal static ChartFile CreateFile(string? hash, string path, params string[] wavFiles)
    {
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = path,
            Resources = TestChartResources.Create(wavFiles, null)
        };
        file = file with { Md5 = hash };
        return file;
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static void DeleteDirectoryIfExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }
        foreach (string childFilePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(childFilePath, FileAttributes.Normal);
        }
        foreach (string childDirectoryPath in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(childDirectoryPath, FileAttributes.Normal);
        }
        File.SetAttributes(directoryPath, FileAttributes.Normal);
        Directory.Delete(directoryPath, recursive: true);
    }

}
