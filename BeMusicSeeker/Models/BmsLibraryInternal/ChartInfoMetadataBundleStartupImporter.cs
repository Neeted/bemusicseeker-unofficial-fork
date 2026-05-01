using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class ChartInfoMetadataBundleStartupImporter
{
    internal const string MetadataDbFileName = "chart-info-metadata.db";

    internal const string MetadataArchiveFileName = "chart-info-metadata.7z";

    internal const string ImportedMetadataDirectoryName = "imported_metadata";

    private const int ArchiveHashPrefixLength = 12;

    internal static void TryImportFromBaseDirectory(
        string baseDirectoryPath,
        BmsLibraryDbGateway dbGateway,
        Action<string> logInstallPerformance,
        Func<string, string, IReadOnlyList<ArchiveEntryMetadata>> extractArchiveEntries = null,
        Func<string> createTempDirectory = null)
    {
        if (dbGateway == null)
        {
            return;
        }

        string rootPath = string.IsNullOrWhiteSpace(baseDirectoryPath) ? AppDomain.CurrentDomain.BaseDirectory : baseDirectoryPath;
        string dbPath = Path.Combine(rootPath, MetadataDbFileName);
        if (File.Exists(dbPath))
        {
            try
            {
                string bundleSha256 = ImportDatabaseBundle(dbPath, dbGateway, logInstallPerformance);
                ArchiveImportedBundle(dbPath, "db", bundleSha256, rootPath, logInstallPerformance);
            }
            catch (Exception ex)
            {
                logInstallPerformance?.Invoke("chart_info_metadata_import failed bundleType=db path=\"" + dbPath + "\" message=" + ex.Message);
            }
            return;
        }

        string archivePath = Path.Combine(rootPath, MetadataArchiveFileName);
        if (File.Exists(archivePath))
        {
            try
            {
                ImportArchiveBundle(archivePath, dbGateway, logInstallPerformance, extractArchiveEntries, createTempDirectory);
            }
            catch (Exception ex)
            {
                logInstallPerformance?.Invoke("chart_info_metadata_import failed bundleType=7z archivePath=\"" + archivePath + "\" message=" + ex.Message);
            }
            return;
        }

        logInstallPerformance?.Invoke("chart_info_metadata_import skipped reason=missing_bundle");
    }

    private static string ImportDatabaseBundle(string bundlePath, BmsLibraryDbGateway dbGateway, Action<string> logInstallPerformance)
    {
        string bundleSha256 = BMSFile.GetSHA256Hash(bundlePath);
        logInstallPerformance?.Invoke("chart_info_metadata_import start bundleType=db path=\"" + bundlePath + "\" bundleSha256=" + bundleSha256);
        ChartInfoMetadataBundleImportResult result = dbGateway.ImportChartInfoMetadataBundle(bundlePath, bundleSha256);
        LogImportResult(logInstallPerformance, result, "db", bundlePath, bundleSha256, null, null, -1L);
        return bundleSha256;
    }

    private static void ImportArchiveBundle(
        string archivePath,
        BmsLibraryDbGateway dbGateway,
        Action<string> logInstallPerformance,
        Func<string, string, IReadOnlyList<ArchiveEntryMetadata>> extractArchiveEntries,
        Func<string> createTempDirectory)
    {
        string archiveSha256 = BMSFile.GetSHA256Hash(archivePath);
        logInstallPerformance?.Invoke("chart_info_metadata_import start bundleType=7z archivePath=\"" + archivePath + "\" bundleSha256=" + archiveSha256);
        Stopwatch importHistoryStopwatch = Stopwatch.StartNew();
        if (dbGateway.IsChartInfoMetadataBundleImportRecorded(archiveSha256))
        {
            importHistoryStopwatch.Stop();
            LogImportResult(
                logInstallPerformance,
                new ChartInfoMetadataBundleImportResult
                {
                    Skipped = true,
                    SkipReason = "already_imported",
                    BundleSha256 = archiveSha256,
                    ElapsedMs = importHistoryStopwatch.ElapsedMilliseconds
                },
                "7z",
                null,
                archiveSha256,
                archivePath,
                null,
                -1L);
            ArchiveImportedBundle(archivePath, "7z", archiveSha256, Path.GetDirectoryName(archivePath), logInstallPerformance);
            return;
        }

        string tempDirectoryPath = null;
        long extractMs = -1L;
        try
        {
            tempDirectoryPath = createTempDirectory != null ? createTempDirectory() : CreateDefaultTempDirectory();
            Stopwatch stopwatch = Stopwatch.StartNew();
            (extractArchiveEntries ?? DefaultExtractArchiveEntries)(archivePath, tempDirectoryPath);
            stopwatch.Stop();
            extractMs = stopwatch.ElapsedMilliseconds;

            string extractedDbPath = ResolveExtractedMetadataDbPath(tempDirectoryPath);
            ChartInfoMetadataBundleImportResult result = dbGateway.ImportChartInfoMetadataBundle(extractedDbPath, archiveSha256);
            LogImportResult(logInstallPerformance, result, "7z", extractedDbPath, archiveSha256, archivePath, extractedDbPath, extractMs);
            ArchiveImportedBundle(archivePath, "7z", archiveSha256, Path.GetDirectoryName(archivePath), logInstallPerformance);
        }
        finally
        {
            DeleteTemporaryDirectory(tempDirectoryPath, logInstallPerformance);
        }
    }

    private static IReadOnlyList<ArchiveEntryMetadata> DefaultExtractArchiveEntries(string archivePath, string destinationDirectoryPath)
    {
        return SevenZipArchiveExtractor.ExtractArchiveEntries(archivePath, destinationDirectoryPath);
    }

    private static string CreateDefaultTempDirectory()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoMetadata_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        return tempDirectoryPath;
    }

    private static string ResolveExtractedMetadataDbPath(string tempDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(tempDirectoryPath) || !Directory.Exists(tempDirectoryPath))
        {
            throw new DirectoryNotFoundException("chart_info metadata bundle extract directory was not found.");
        }

        string[] candidates = Directory.GetFiles(tempDirectoryPath, MetadataDbFileName, SearchOption.TopDirectoryOnly)
            .Where((string path) => string.Equals(Path.GetFileName(path), MetadataDbFileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 1)
        {
            return candidates[0];
        }
        if (candidates.Length == 0)
        {
            throw new InvalidDataException("chart_info metadata archive does not contain " + MetadataDbFileName + " at the archive root.");
        }

        throw new InvalidDataException("chart_info metadata archive contains multiple root " + MetadataDbFileName + " entries.");
    }

    private static void LogImportResult(
        Action<string> logInstallPerformance,
        ChartInfoMetadataBundleImportResult result,
        string bundleType,
        string path,
        string bundleSha256,
        string archivePath,
        string extractedDbPath,
        long extractMs)
    {
        if (result != null && result.Skipped)
        {
            logInstallPerformance?.Invoke("chart_info_metadata_import skipped reason=" + (result.SkipReason ?? "unknown")
                + " bundleType=" + bundleType
                + AppendPath(" path", path)
                + AppendPath(" archivePath", archivePath)
                + AppendPath(" extractedDbPath", extractedDbPath)
                + " bundleSha256=" + bundleSha256
                + AppendElapsed(" extractMs", extractMs)
                + " elapsedMs=" + result.ElapsedMs);
            return;
        }

        logInstallPerformance?.Invoke("chart_info_metadata_import done"
            + " bundleType=" + bundleType
            + AppendPath(" path", path)
            + AppendPath(" archivePath", archivePath)
            + AppendPath(" extractedDbPath", extractedDbPath)
            + " bundleId=" + (result?.BundleId ?? string.Empty)
            + " bundleSha256=" + bundleSha256
            + " sourceChartInfo=" + (result?.SourceChartInfoCount ?? 0)
            + " sourceDigest=" + (result?.SourceDigestCount ?? 0)
            + " chartInfoImported=" + (result?.ImportedChartInfoCount ?? 0)
            + " digestImported=" + (result?.ImportedDigestCount ?? 0)
            + " failureCleared=" + (result?.FailureClearedCount ?? 0)
            + AppendElapsed(" extractMs", extractMs)
            + " elapsedMs=" + (result?.ElapsedMs ?? 0));
    }

    private static string AppendPath(string label, string path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : label + "=\"" + path + "\"";
    }

    private static string AppendElapsed(string label, long elapsedMs)
    {
        return elapsedMs >= 0L ? label + "=" + elapsedMs : string.Empty;
    }

    private static void ArchiveImportedBundle(string sourcePath, string bundleType, string bundleSha256, string baseDirectoryPath, Action<string> logInstallPerformance)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            logInstallPerformance?.Invoke("chart_info_metadata_bundle_archive skipped reason=source_missing" + AppendPath(" source", sourcePath));
            return;
        }

        try
        {
            string archiveDirectoryPath = Path.Combine(
                string.IsNullOrWhiteSpace(baseDirectoryPath) ? Path.GetDirectoryName(sourcePath) : baseDirectoryPath,
                ImportedMetadataDirectoryName);
            Directory.CreateDirectory(archiveDirectoryPath);
            string destinationPath = BuildArchiveDestinationPath(archiveDirectoryPath, sourcePath, bundleSha256);
            File.Move(sourcePath, destinationPath);
            logInstallPerformance?.Invoke("chart_info_metadata_bundle_archive moved"
                + " bundleType=" + bundleType
                + AppendPath(" source", sourcePath)
                + AppendPath(" destination", destinationPath));
        }
        catch (Exception ex)
        {
            logInstallPerformance?.Invoke("chart_info_metadata_bundle_archive failed"
                + AppendPath(" source", sourcePath)
                + " message=" + ex.Message);
        }
    }

    private static string BuildArchiveDestinationPath(string archiveDirectoryPath, string sourcePath, string bundleSha256)
    {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        string primaryDestinationPath = Path.Combine(archiveDirectoryPath, Path.GetFileName(sourcePath));
        if (!File.Exists(primaryDestinationPath) && !Directory.Exists(primaryDestinationPath))
        {
            return primaryDestinationPath;
        }

        string hashPrefix = NormalizeHashPrefix(bundleSha256);
        string hashDestinationPath = Path.Combine(archiveDirectoryPath, fileNameWithoutExtension + "." + hashPrefix + extension);
        if (!File.Exists(hashDestinationPath) && !Directory.Exists(hashDestinationPath))
        {
            return hashDestinationPath;
        }

        for (int i = 1; i < int.MaxValue; i++)
        {
            string candidatePath = Path.Combine(archiveDirectoryPath, fileNameWithoutExtension + "." + hashPrefix + "." + i + extension);
            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new IOException("Unable to find available imported metadata bundle archive path.");
    }

    private static string NormalizeHashPrefix(string bundleSha256)
    {
        string normalized = string.IsNullOrWhiteSpace(bundleSha256) ? "unknown" : bundleSha256.Trim().ToLowerInvariant();
        if (normalized.Length <= ArchiveHashPrefixLength)
        {
            return normalized;
        }

        return normalized.Substring(0, ArchiveHashPrefixLength);
    }

    private static void DeleteTemporaryDirectory(string tempDirectoryPath, Action<string> logInstallPerformance)
    {
        if (string.IsNullOrWhiteSpace(tempDirectoryPath) || !Directory.Exists(tempDirectoryPath))
        {
            return;
        }

        try
        {
            Directory.Delete(tempDirectoryPath, recursive: true);
        }
        catch (Exception ex)
        {
            logInstallPerformance?.Invoke("chart_info_metadata_import cleanup_failed path=\"" + tempDirectoryPath + "\" message=" + ex.Message);
        }
    }
}
