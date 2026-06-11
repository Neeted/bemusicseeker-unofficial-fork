using System;
using System.Globalization;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FullGenerationSignatureBuilder
{
    private const string Version = "lr2_full_generation_v2";
    private const int SongFolderGeneratorVersion = 1;
    private const int Lr2FolderFileParserVersion = 1;
    private const int Lr2CompatibilityFactsVersion = 1;

    internal static string Build(BmsLibraryOptionsSnapshot options)
    {
        return Version
            + "|appSchema=" + BmsLibraryDbGateway.CurrentAppSchemaVersion.ToString(CultureInfo.InvariantCulture)
            + "|chartInfoSchema=" + BmsLibraryDbGateway.CurrentChartInfoSchemaVersion.ToString(CultureInfo.InvariantCulture)
            + "|chartInfoParser=" + BmsLibraryDbGateway.CurrentChartInfoParserVersion.ToString(CultureInfo.InvariantCulture)
            + "|songFolderGenerator=" + SongFolderGeneratorVersion.ToString(CultureInfo.InvariantCulture)
            + "|lr2FolderFileParser=" + Lr2FolderFileParserVersion.ToString(CultureInfo.InvariantCulture)
            + "|lr2CompatibilityFacts=" + Lr2CompatibilityFactsVersion.ToString(CultureInfo.InvariantCulture)
            + "|operationModeLR2DB=" + ((options?.OperationModeLR2DB ?? false) ? "1" : "0");
    }
}
