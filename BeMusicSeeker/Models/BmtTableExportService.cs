using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models.LR2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

internal static class BmtTableExportService
{
    internal const string ManifestFileName = ".bemusicseeker-bmt-manifest";

    private static readonly object ManifestLock = new();

    private sealed class ManifestState
    {
        public HashSet<string> Files { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ManifestPlaylistEntry> Playlists { get; } = new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal);
    }

    internal class ManagedTableUrlEntry
    {
        public string PlaylistIdentity { get; set; }

        public string FileName { get; set; }

        public string Url { get; set; }

        public string Name { get; set; }

        public string ContentHash { get; set; }
    }

    private sealed class ManifestPlaylistEntry : ManagedTableUrlEntry
    {
    }

    internal sealed class ExportResult
    {
        public List<ManagedTableUrlEntry> PreviousManagedTables { get; } = [];

        public List<ManagedTableUrlEntry> CurrentManagedTables { get; } = [];

        public int WrittenCount { get; internal set; }

        public int SkippedWriteCount { get; internal set; }

        public int RemovedCount { get; internal set; }
    }

    internal sealed class SongHashResolution(string md5, string sha256)
    {
        public string Md5 { get; } = md5;

        public string Sha256 { get; } = sha256;
    }

    internal interface ISongHashResolver
    {
        SongHashResolution Resolve(BMSTableEntry entry);
    }

    internal static ExportResult ExportTables(string tablePath, IEnumerable<BMSTable> tables, bool cleanupStaleManagedFiles)
    {
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            return new ExportResult();
        }
        Directory.CreateDirectory(tablePath);
        var exportedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTable table in tables ?? [])
        {
            string fileName = ExportTable(tablePath, table);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                exportedFiles.Add(fileName);
            }
        }
        return UpdateManifest(tablePath, exportedFiles, new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal), cleanupStaleManagedFiles);
    }

    internal static ExportResult ExportTableDataSet(string tablePath, IEnumerable<JObject> tableDataSet, bool cleanupStaleManagedFiles)
    {
        return ExportTableDataSet(tablePath, (tableDataSet ?? []).Select(tableData => Tuple.Create<string, JObject>(null, tableData)), cleanupStaleManagedFiles, null);
    }

    internal static ExportResult ExportTableDataSet(string tablePath, IEnumerable<Tuple<string, JObject>> tableDataSet, bool cleanupStaleManagedFiles)
    {
        return ExportTableDataSet(tablePath, tableDataSet, cleanupStaleManagedFiles, null);
    }

    internal static ExportResult ExportTableDataSet(string tablePath, IEnumerable<Tuple<string, JObject>> tableDataSet, bool cleanupStaleManagedFiles, Action<int, int, string> progressReporter)
    {
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            return new ExportResult();
        }
        Directory.CreateDirectory(tablePath);
        ManifestState previousManifest;
        lock (ManifestLock)
        {
            previousManifest = ReadManifest(tablePath);
        }
        List<ManagedTableUrlEntry> previousManagedTables = [.. previousManifest.Playlists.Values.Select(CloneManagedTableUrlEntry)];
        var result = new ExportResult();
        var exportedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var playlists = new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal);
        List<Tuple<string, JObject>> items = (tableDataSet ?? []).ToList();
        int processedCount = 0;
        foreach (Tuple<string, JObject> item in items)
        {
            string playlistIdentity = item?.Item1;
            JObject tableData = item?.Item2;
            string fileName = GetOutputFileName(tableData);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                string contentHash = ComputeContentHash(tableData);
                if (!ShouldSkipWrite(tablePath, previousManifest, playlistIdentity, fileName, contentHash))
                {
                    WriteTableDataFile(tablePath, tableData, fileName);
                    result.WrittenCount++;
                }
                else
                {
                    result.SkippedWriteCount++;
                }
                exportedFiles.Add(fileName);
                if (!string.IsNullOrWhiteSpace(playlistIdentity))
                {
                    playlists[playlistIdentity] = CreateManifestPlaylistEntry(playlistIdentity, fileName, tableData, contentHash);
                }
            }
            processedCount++;
            progressReporter?.Invoke(processedCount, items.Count, tableData?.Value<string>("name") ?? playlistIdentity ?? string.Empty);
        }
        ExportResult manifestResult = UpdateManifest(tablePath, exportedFiles, playlists, cleanupStaleManagedFiles);
        result.PreviousManagedTables.Clear();
        result.PreviousManagedTables.AddRange(previousManagedTables);
        result.CurrentManagedTables.Clear();
        result.CurrentManagedTables.AddRange(manifestResult.CurrentManagedTables);
        result.RemovedCount = manifestResult.RemovedCount;
        return result;
    }

    internal static string ExportTable(string tablePath, BMSTable table)
    {
        if (string.IsNullOrWhiteSpace(tablePath) || table == null)
        {
            return null;
        }
        JObject tableData = BuildTableData(table);
        if (tableData == null)
        {
            return null;
        }
        return ExportTableData(tablePath, tableData);
    }

    internal static string ExportTableData(string tablePath, JObject tableData)
    {
        return ExportTableData(tablePath, tableData, null);
    }

    internal static string ExportTableData(string tablePath, JObject tableData, string playlistIdentity)
    {
        if (string.IsNullOrWhiteSpace(tablePath) || tableData == null)
        {
            return null;
        }
        string fileName = GetOutputFileName(tableData);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }
        Directory.CreateDirectory(tablePath);
        string contentHash = ComputeContentHash(tableData);
        bool skipWrite;
        lock (ManifestLock)
        {
            skipWrite = ShouldSkipWrite(tablePath, ReadManifest(tablePath), playlistIdentity, fileName, contentHash);
        }
        if (!skipWrite)
        {
            WriteTableDataFile(tablePath, tableData, fileName);
        }
        AddManagedFile(tablePath, fileName, playlistIdentity, tableData, contentHash);
        return fileName;
    }

    internal static void CleanupManagedFiles(string tablePath)
    {
        if (string.IsNullOrWhiteSpace(tablePath) || !Directory.Exists(tablePath))
        {
            return;
        }
        lock (ManifestLock)
        {
            foreach (string fileName in ReadManifest(tablePath).Files)
            {
                TryDeleteFile(Path.Combine(tablePath, fileName));
            }
            TryDeleteFile(Path.Combine(tablePath, ManifestFileName));
        }
    }

    internal static ExportResult RemoveManagedPlaylist(string tablePath, string playlistIdentity)
    {
        var result = new ExportResult();
        if (string.IsNullOrWhiteSpace(tablePath) || string.IsNullOrWhiteSpace(playlistIdentity))
        {
            return result;
        }
        lock (ManifestLock)
        {
            ManifestState manifest = ReadManifest(tablePath);
            result.PreviousManagedTables.AddRange(manifest.Playlists.Values.Select(CloneManagedTableUrlEntry));
            if (manifest.Playlists.TryGetValue(playlistIdentity, out ManifestPlaylistEntry oldEntry))
            {
                manifest.Files.Remove(oldEntry.FileName);
                TryDeleteFile(Path.Combine(tablePath, oldEntry.FileName));
                manifest.Playlists.Remove(playlistIdentity);
                WriteManifest(tablePath, manifest.Files, manifest.Playlists);
            }
            result.CurrentManagedTables.AddRange(manifest.Playlists.Values.Select(CloneManagedTableUrlEntry));
        }
        return result;
    }

    internal static List<ManagedTableUrlEntry> ReadManagedTableUrls(string tablePath)
    {
        if (string.IsNullOrWhiteSpace(tablePath) || !Directory.Exists(tablePath))
        {
            return [];
        }
        lock (ManifestLock)
        {
            return [.. ReadManifest(tablePath).Playlists.Values.Select(CloneManagedTableUrlEntry)];
        }
    }

    internal static JObject BuildTableData(BMSTable table)
    {
        return BuildTableData(table, null);
    }

    internal static JObject BuildTableData(BMSTable table, ISongHashResolver hashResolver)
    {
        if (table == null)
        {
            return null;
        }
        string url = ResolveTableUrl(table);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(table.name))
        {
            return null;
        }
        JArray folders = BuildFolders(table, hashResolver);
        JArray courses = BuildCourses(table);
        if (folders.Count == 0 && courses.Count == 0)
        {
            return null;
        }
        var root = new JObject
        {
            ["url"] = url,
            ["name"] = table.name ?? string.Empty,
            ["tag"] = ResolveTag(table),
            ["folder"] = folders,
            ["course"] = courses
        };
        return root;
    }

    private static string ResolveTableUrl(BMSTable table)
    {
        Uri uri = table.Page_url ?? table.GetAbsoluteHeaderUrl();
        if (uri != null && uri.IsAbsoluteUri)
        {
            return uri.AbsoluteUri;
        }
        if (table.playlist_id.HasValue)
        {
            return "bemusicseeker://playlist/" + table.playlist_id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static string ResolveTag(BMSTable table)
    {
        if (!string.IsNullOrWhiteSpace(table.tag))
        {
            return table.tag;
        }
        if (!string.IsNullOrWhiteSpace(table.symbol))
        {
            return table.symbol;
        }
        return (table.compat_prefix ?? string.Empty).Trim();
    }

    private static JArray BuildFolders(BMSTable table, ISongHashResolver hashResolver)
    {
        JArray folders = [];
        var entriesByFolder = new Dictionary<string, List<BMSTableEntry>>(StringComparer.Ordinal);
        foreach (BMSTableEntry entry in table.entries ?? [])
        {
            if (entry == null || entry.is_removed)
            {
                continue;
            }
            string folderKey = entry.folder ?? string.Empty;
            if (!entriesByFolder.TryGetValue(folderKey, out List<BMSTableEntry> folderEntries))
            {
                folderEntries = [];
                entriesByFolder[folderKey] = folderEntries;
            }
            folderEntries.Add(entry);
        }
        List<string> folderNames = table.folder_list ?? [];
        foreach (string folderName in folderNames)
        {
            JArray songs = [];
            if (!entriesByFolder.TryGetValue(folderName ?? string.Empty, out List<BMSTableEntry> entries))
            {
                continue;
            }
            foreach (BMSTableEntry entry in entries)
            {
                JObject song = BuildSong(entry, null, hashResolver);
                if (song != null)
                {
                    songs.Add(song);
                }
            }
            if (songs.Count == 0)
            {
                continue;
            }
            folders.Add(new JObject
            {
                ["name"] = ResolveFolderName(table, folderName),
                ["songs"] = songs
            });
        }
        return folders;
    }

    private static string ResolveFolderName(BMSTable table, string folderName)
    {
        if (!table.is_external_sync)
        {
            return folderName ?? string.Empty;
        }
        string level = table.ConvertBackFolderNameToCompatibleLevelName(folderName ?? string.Empty);
        string tag = ResolveTag(table);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return folderName ?? string.Empty;
        }
        return tag + level;
    }

    private static JObject BuildSong(BMSTableEntry entry, JObject sourceChart, ISongHashResolver hashResolver)
    {
        SongHashResolution resolvedHashes = hashResolver?.Resolve(entry);
        string md5 = FirstNonEmpty(
            entry?.md5,
            string.IsNullOrWhiteSpace(entry?.md5) ? resolvedHashes?.Md5 : null,
            sourceChart?.Value<string>("md5"));
        string sha256 = FirstNonEmpty(
            entry?.sha256,
            string.IsNullOrWhiteSpace(entry?.sha256) ? resolvedHashes?.Sha256 : null,
            sourceChart?.Value<string>("sha256"));
        string title = FirstNonEmpty(entry?.title, sourceChart?.Value<string>("title"));
        if (string.IsNullOrWhiteSpace(title) || (string.IsNullOrWhiteSpace(md5) && string.IsNullOrWhiteSpace(sha256)))
        {
            return null;
        }
        var song = new JObject
        {
            ["title"] = title
        };
        AddIfNotEmpty(song, "artist", FirstNonEmpty(entry?.artist, sourceChart?.Value<string>("artist")));
        AddIfNotEmpty(song, "md5", md5?.ToLowerInvariant());
        AddIfNotEmpty(song, "sha256", sha256?.ToLowerInvariant());
        AddIfNotEmpty(song, "url", FirstNonEmpty(entry?.url, sourceChart?.Value<string>("url")));
        AddIfNotEmpty(song, "appendurl", FirstNonEmpty(entry?.url_diff, sourceChart?.Value<string>("url_diff"), sourceChart?.Value<string>("appendurl")));
        AddIfNotEmpty(song, "ipfs", sourceChart?.Value<string>("ipfs"));
        AddIfNotEmpty(song, "appendipfs", FirstNonEmpty(sourceChart?.Value<string>("ipfs_diff"), sourceChart?.Value<string>("appendipfs")));
        JArray orgMd5 = BuildOrgMd5(entry, sourceChart);
        if (orgMd5.Count > 0)
        {
            song["org_md5"] = orgMd5;
        }
        return song;
    }

    private static JArray BuildOrgMd5(BMSTableEntry entry, JObject sourceChart)
    {
        JArray values = [];
        foreach (string value in entry?.Org_md5 ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.ToLowerInvariant());
            }
        }
        JToken sourceOrg = sourceChart?["org_md5"];
        if (sourceOrg is JArray array)
        {
            foreach (JToken item in array)
            {
                string value = item?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value.ToLowerInvariant());
                }
            }
        }
        else
        {
            string value = sourceOrg?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.ToLowerInvariant());
            }
        }
        return new JArray(values.Select(item => item.ToString()).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static JArray BuildCourses(BMSTable table)
    {
        JArray courses = [];
        foreach (LR2SongDBExtended.playlist_course courseRow in table.Courses ?? [])
        {
            JObject course = ConvertCourse(courseRow?.course_json);
            if (course != null)
            {
                courses.Add(course);
            }
        }
        return courses;
    }

    private static JObject ConvertCourse(string courseJson)
    {
        if (string.IsNullOrWhiteSpace(courseJson))
        {
            return null;
        }
        JObject source;
        try
        {
            source = JObject.Parse(courseJson);
        }
        catch
        {
            return null;
        }
        JArray songs = [];
        int index = 1;
        foreach (JToken chartToken in EnumerateCourseChartTokens(source))
        {
            JObject song = BuildCourseSong(chartToken, index++);
            if (song != null)
            {
                songs.Add(song);
            }
        }
        if (songs.Count == 0)
        {
            return null;
        }
        var course = new JObject
        {
            ["name"] = FirstNonEmpty(source.Value<string>("name"), "No Course Title"),
            ["hash"] = songs
        };
        JArray constraints = BuildCourseConstraints(source["constraint"] as JArray);
        if (constraints.Count > 0)
        {
            course["constraint"] = constraints;
        }
        JArray trophies = BuildCourseTrophies(source["trophy"] as JArray);
        if (trophies.Count > 0)
        {
            course["trophy"] = trophies;
        }
        if (source["release"] != null && bool.TryParse(source["release"].ToString(), out bool release))
        {
            course["release"] = release;
        }
        return course;
    }

    private static IEnumerable<JToken> EnumerateCourseChartTokens(JObject source)
    {
        JArray charts = source["charts"] as JArray ?? source["hash"] as JArray;
        if (charts != null)
        {
            foreach (JToken chart in charts)
            {
                yield return chart;
            }
            yield break;
        }
        foreach (JToken md5 in source["md5"] as JArray ?? [])
        {
            if (!string.IsNullOrWhiteSpace(md5?.ToString()))
            {
                yield return new JObject
                {
                    ["md5"] = md5.ToString()
                };
            }
        }
        foreach (JToken sha256 in source["sha256"] as JArray ?? [])
        {
            if (!string.IsNullOrWhiteSpace(sha256?.ToString()))
            {
                yield return new JObject
                {
                    ["sha256"] = sha256.ToString()
                };
            }
        }
    }

    private static JObject BuildCourseSong(JToken chartToken, int index)
    {
        JObject chart = ConvertCourseChartToken(chartToken);
        if (chart == null)
        {
            return null;
        }
        string md5 = chart.Value<string>("md5");
        string sha256 = chart.Value<string>("sha256");
        string title = FirstNonEmpty(chart.Value<string>("title"), "course " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(md5) && string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }
        var song = new JObject
        {
            ["title"] = title
        };
        AddIfNotEmpty(song, "artist", chart.Value<string>("artist"));
        AddIfNotEmpty(song, "md5", md5?.ToLowerInvariant());
        AddIfNotEmpty(song, "sha256", sha256?.ToLowerInvariant());
        AddIfNotEmpty(song, "url", chart.Value<string>("url"));
        AddIfNotEmpty(song, "appendurl", FirstNonEmpty(chart.Value<string>("url_diff"), chart.Value<string>("appendurl")));
        AddIfNotEmpty(song, "ipfs", chart.Value<string>("ipfs"));
        AddIfNotEmpty(song, "appendipfs", FirstNonEmpty(chart.Value<string>("ipfs_diff"), chart.Value<string>("appendipfs")));
        return song;
    }

    private static JObject ConvertCourseChartToken(JToken chartToken)
    {
        if (chartToken is JObject chart)
        {
            return chart;
        }
        string hash = chartToken?.ToString();
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }
        JObject chartObject = [];
        if (hash.Length == 64)
        {
            chartObject["sha256"] = hash;
        }
        else
        {
            chartObject["md5"] = hash;
        }
        return chartObject;
    }

    private static JArray BuildCourseConstraints(JArray source)
    {
        JArray constraints = [];
        if (source == null)
        {
            return constraints;
        }
        foreach (JToken item in source)
        {
            string value = ConvertConstraint(item?.ToString());
            if (!string.IsNullOrWhiteSpace(value) && !constraints.Any(token => string.Equals(token.ToString(), value, StringComparison.Ordinal)))
            {
                constraints.Add(value);
            }
        }
        return constraints;
    }

    private static string ConvertConstraint(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "class" or "grade" => "CLASS",
            "mirror" or "grade_mirror" => "MIRROR",
            "random" or "grade_random" => "RANDOM",
            "no_speed" => "NO_SPEED",
            "no_good" => "NO_GOOD",
            "no_great" => "NO_GREAT",
            "gauge_lr2" => "GAUGE_LR2",
            "gauge_5k" => "GAUGE_5KEYS",
            "gauge_7k" => "GAUGE_7KEYS",
            "gauge_9k" => "GAUGE_9KEYS",
            "gauge_24k" => "GAUGE_24KEYS",
            "ln" => "LN",
            "cn" => "CN",
            "hcn" => "HCN",
            _ => null,
        };
    }

    private static JArray BuildCourseTrophies(JArray source)
    {
        JArray trophies = [];
        if (source == null)
        {
            return trophies;
        }
        foreach (JToken item in source)
        {
            if (item is not JObject trophy)
            {
                continue;
            }
            string name = trophy.Value<string>("name");
            double missrate = trophy.Value<double?>("missrate") ?? 0d;
            double scorerate = trophy.Value<double?>("scorerate") ?? 100d;
            if (string.IsNullOrWhiteSpace(name) || missrate <= 0d || scorerate >= 100d)
            {
                continue;
            }
            trophies.Add(new JObject
            {
                ["name"] = name,
                ["missrate"] = missrate,
                ["scorerate"] = scorerate
            });
        }
        return trophies;
    }

    private static ExportResult UpdateManifest(string tablePath, HashSet<string> currentFiles, Dictionary<string, ManifestPlaylistEntry> playlists, bool cleanupStaleManagedFiles)
    {
        var result = new ExportResult();
        lock (ManifestLock)
        {
            ManifestState previous = ReadManifest(tablePath);
            result.PreviousManagedTables.AddRange(previous.Playlists.Values.Select(CloneManagedTableUrlEntry));
            if (cleanupStaleManagedFiles)
            {
                foreach (string fileName in previous.Files.Where(fileName => !currentFiles.Contains(fileName)))
                {
                    TryDeleteFile(Path.Combine(tablePath, fileName));
                    result.RemovedCount++;
                }
            }
            result.CurrentManagedTables.AddRange((playlists ?? new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal)).Values.Select(CloneManagedTableUrlEntry));
            WriteManifest(tablePath, currentFiles, playlists);
        }
        return result;
    }

    private static string GetOutputFileName(JObject tableData)
    {
        string url = tableData?.Value<string>("url");
        return string.IsNullOrWhiteSpace(url) ? null : BMSTable.ComputeSha256Hex(url) + ".bmt";
    }

    private static void WriteTableDataFile(string tablePath, JObject tableData, string fileName)
    {
        string outputPath = Path.Combine(tablePath, fileName);
        string tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Compress))
            using (var writer = new StreamWriter(gzipStream, new UTF8Encoding(false)))
            {
                writer.Write(tableData.ToString(Formatting.Indented));
            }
            ReplaceFile(tempPath, outputPath);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static bool ShouldSkipWrite(string tablePath, ManifestState previousManifest, string playlistIdentity, string fileName, string contentHash)
    {
        return !string.IsNullOrWhiteSpace(playlistIdentity)
            && !string.IsNullOrWhiteSpace(fileName)
            && !string.IsNullOrWhiteSpace(contentHash)
            && previousManifest?.Playlists.TryGetValue(playlistIdentity, out ManifestPlaylistEntry previousEntry) == true
            && string.Equals(previousEntry.FileName, fileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previousEntry.ContentHash, contentHash, StringComparison.Ordinal)
            && File.Exists(Path.Combine(tablePath, fileName));
    }

    private static string ComputeContentHash(JObject tableData)
    {
        if (tableData == null)
        {
            return string.Empty;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(tableData.ToString(Formatting.None));
        using SHA256 sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void AddManagedFile(string tablePath, string fileName, string playlistIdentity, JObject tableData, string contentHash)
    {
        lock (ManifestLock)
        {
            ManifestState manifest = ReadManifest(tablePath);
            if (!string.IsNullOrWhiteSpace(playlistIdentity)
                && manifest.Playlists.TryGetValue(playlistIdentity, out ManifestPlaylistEntry oldEntry)
                && !string.Equals(oldEntry.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                manifest.Files.Remove(oldEntry.FileName);
                TryDeleteFile(Path.Combine(tablePath, oldEntry.FileName));
            }
            manifest.Files.Add(fileName);
            if (!string.IsNullOrWhiteSpace(playlistIdentity))
            {
                manifest.Playlists[playlistIdentity] = CreateManifestPlaylistEntry(playlistIdentity, fileName, tableData, contentHash);
            }
            WriteManifest(tablePath, manifest.Files, manifest.Playlists);
        }
    }

    private static ManifestState ReadManifest(string tablePath)
    {
        var state = new ManifestState();
        string manifestPath = Path.Combine(tablePath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return state;
        }
        try
        {
            var manifest = JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            foreach (JToken item in manifest["files"] as JArray ?? [])
            {
                string fileName = Path.GetFileName(item.ToString());
                if (!string.IsNullOrWhiteSpace(fileName) && fileName.EndsWith(".bmt", StringComparison.OrdinalIgnoreCase))
                {
                    state.Files.Add(fileName);
                }
            }
            if (manifest["playlists"] is JObject playlists)
            {
                foreach (JProperty property in playlists.Properties())
                {
                    var value = property.Value as JObject;
                    string fileName = Path.GetFileName(value?.Value<string>("file"));
                    string url = value?.Value<string>("url");
                    if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(fileName) && fileName.EndsWith(".bmt", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
                    {
                        state.Playlists[property.Name] = new ManifestPlaylistEntry
                        {
                            PlaylistIdentity = property.Name,
                            FileName = fileName,
                            Url = url,
                            Name = value.Value<string>("name") ?? string.Empty,
                            ContentHash = value.Value<string>("contentHash") ?? string.Empty
                        };
                        state.Files.Add(fileName);
                    }
                }
            }
        }
        catch
        {
        }
        return state;
    }

    private static void WriteManifest(string tablePath, IEnumerable<string> fileNames)
    {
        WriteManifest(tablePath, fileNames, null);
    }

    private static void WriteManifest(string tablePath, IEnumerable<string> fileNames, IDictionary<string, ManifestPlaylistEntry> playlists)
    {
        var manifest = new JObject
        {
            ["files"] = new JArray((fileNames ?? []).Where(fileName => !string.IsNullOrWhiteSpace(fileName)).OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase))
        };
        if (playlists != null && playlists.Count > 0)
        {
            JObject playlistJson = [];
            foreach (KeyValuePair<string, ManifestPlaylistEntry> item in playlists.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(item.Key) && item.Value != null && !string.IsNullOrWhiteSpace(item.Value.FileName) && !string.IsNullOrWhiteSpace(item.Value.Url))
                {
                    playlistJson[item.Key] = new JObject
                    {
                        ["file"] = item.Value.FileName,
                        ["url"] = item.Value.Url,
                        ["name"] = item.Value.Name ?? string.Empty,
                        ["contentHash"] = item.Value.ContentHash ?? string.Empty
                    };
                }
            }
            manifest["playlists"] = playlistJson;
        }
        File.WriteAllText(Path.Combine(tablePath, ManifestFileName), manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
    }

    private static ManifestPlaylistEntry CreateManifestPlaylistEntry(string playlistIdentity, string fileName, JObject tableData, string contentHash)
    {
        return new ManifestPlaylistEntry
        {
            PlaylistIdentity = playlistIdentity,
            FileName = fileName,
            Url = tableData?.Value<string>("url") ?? string.Empty,
            Name = tableData?.Value<string>("name") ?? string.Empty,
            ContentHash = contentHash ?? string.Empty
        };
    }

    private static ManagedTableUrlEntry CloneManagedTableUrlEntry(ManagedTableUrlEntry entry)
    {
        return new ManagedTableUrlEntry
        {
            PlaylistIdentity = entry?.PlaylistIdentity,
            FileName = entry?.FileName,
            Url = entry?.Url,
            Name = entry?.Name,
            ContentHash = entry?.ContentHash
        };
    }

    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }
        File.Move(sourcePath, destinationPath);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void AddIfNotEmpty(JObject obj, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            obj[name] = value;
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
