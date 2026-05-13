using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

internal static class BmtTableExportService
{
    internal const string ManifestFileName = ".bemusicseeker-bmt-manifest";

    private static readonly object ManifestLock = new object();

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
    }

    private sealed class ManifestPlaylistEntry : ManagedTableUrlEntry
    {
    }

    internal sealed class ExportResult
    {
        public List<ManagedTableUrlEntry> PreviousManagedTables { get; } = new List<ManagedTableUrlEntry>();

        public List<ManagedTableUrlEntry> CurrentManagedTables { get; } = new List<ManagedTableUrlEntry>();
    }

    internal static ExportResult ExportTables(string tablePath, IEnumerable<BMSTable> tables, bool cleanupStaleManagedFiles)
    {
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            return new ExportResult();
        }
        Directory.CreateDirectory(tablePath);
        HashSet<string> exportedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTable table in tables ?? Enumerable.Empty<BMSTable>())
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
        return ExportTableDataSet(tablePath, (tableDataSet ?? Enumerable.Empty<JObject>()).Select((JObject tableData) => Tuple.Create<string, JObject>(null, tableData)), cleanupStaleManagedFiles);
    }

    internal static ExportResult ExportTableDataSet(string tablePath, IEnumerable<Tuple<string, JObject>> tableDataSet, bool cleanupStaleManagedFiles)
    {
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            return new ExportResult();
        }
        Directory.CreateDirectory(tablePath);
        List<ManagedTableUrlEntry> previousManagedTables = ReadManagedTableUrls(tablePath);
        HashSet<string> exportedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ManifestPlaylistEntry> playlists = new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal);
        foreach (Tuple<string, JObject> item in tableDataSet ?? Enumerable.Empty<Tuple<string, JObject>>())
        {
            string fileName = ExportTableData(tablePath, item?.Item2, item?.Item1);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                exportedFiles.Add(fileName);
                if (!string.IsNullOrWhiteSpace(item?.Item1))
                {
                    playlists[item.Item1] = CreateManifestPlaylistEntry(item.Item1, fileName, item.Item2);
                }
            }
        }
        ExportResult result = UpdateManifest(tablePath, exportedFiles, playlists, cleanupStaleManagedFiles);
        result.PreviousManagedTables.Clear();
        result.PreviousManagedTables.AddRange(previousManagedTables);
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
        string url = tableData.Value<string>("url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        Directory.CreateDirectory(tablePath);
        string fileName = BMSTable.ComputeSha256Hex(url) + ".bmt";
        string outputPath = Path.Combine(tablePath, fileName);
        string tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (GZipStream gzipStream = new GZipStream(fileStream, CompressionMode.Compress))
            using (StreamWriter writer = new StreamWriter(gzipStream, new UTF8Encoding(false)))
            {
                writer.Write(tableData.ToString(Formatting.Indented));
            }
            ReplaceFile(tempPath, outputPath);
            AddManagedFile(tablePath, fileName, playlistIdentity, tableData);
            return fileName;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
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
        ExportResult result = new ExportResult();
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
            return new List<ManagedTableUrlEntry>();
        }
        lock (ManifestLock)
        {
            return ReadManifest(tablePath).Playlists.Values.Select(CloneManagedTableUrlEntry).ToList();
        }
    }

    internal static JObject BuildTableData(BMSTable table)
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
        JArray folders = BuildFolders(table);
        JArray courses = BuildCourses(table);
        if (folders.Count == 0 && courses.Count == 0)
        {
            return null;
        }
        JObject root = new JObject
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

    private static JArray BuildFolders(BMSTable table)
    {
        JArray folders = new JArray();
        List<string> folderNames = table.folder_list ?? new List<string>();
        foreach (string folderName in folderNames)
        {
            JArray songs = new JArray();
            IEnumerable<BMSTableEntry> entries = (table.entries ?? new List<BMSTableEntry>())
                .Where((BMSTableEntry entry) => entry != null && !entry.is_removed && string.Equals(entry.folder ?? string.Empty, folderName ?? string.Empty, StringComparison.Ordinal));
            foreach (BMSTableEntry entry in entries)
            {
                JObject song = BuildSong(entry, null);
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

    private static JObject BuildSong(BMSTableEntry entry, JObject sourceChart)
    {
        string md5 = FirstNonEmpty(entry?.md5, sourceChart?.Value<string>("md5"));
        string sha256 = FirstNonEmpty(entry?.sha256, sourceChart?.Value<string>("sha256"));
        string title = FirstNonEmpty(entry?.title, sourceChart?.Value<string>("title"));
        if (string.IsNullOrWhiteSpace(title) || (string.IsNullOrWhiteSpace(md5) && string.IsNullOrWhiteSpace(sha256)))
        {
            return null;
        }
        JObject song = new JObject
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
        JArray values = new JArray();
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
        return new JArray(values.Select((JToken item) => item.ToString()).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static JArray BuildCourses(BMSTable table)
    {
        JArray courses = new JArray();
        foreach (LR2SongDBExtended.playlist_course courseRow in table.Courses ?? Array.Empty<LR2SongDBExtended.playlist_course>())
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
        JArray songs = new JArray();
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
        JObject course = new JObject
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
        foreach (JToken md5 in source["md5"] as JArray ?? new JArray())
        {
            if (!string.IsNullOrWhiteSpace(md5?.ToString()))
            {
                yield return new JObject
                {
                    ["md5"] = md5.ToString()
                };
            }
        }
        foreach (JToken sha256 in source["sha256"] as JArray ?? new JArray())
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
        JObject song = new JObject
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
        JObject chartObject = new JObject();
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
        JArray constraints = new JArray();
        if (source == null)
        {
            return constraints;
        }
        foreach (JToken item in source)
        {
            string value = ConvertConstraint(item?.ToString());
            if (!string.IsNullOrWhiteSpace(value) && !constraints.Any((JToken token) => string.Equals(token.ToString(), value, StringComparison.Ordinal)))
            {
                constraints.Add(value);
            }
        }
        return constraints;
    }

    private static string ConvertConstraint(string value)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "class":
            case "grade":
                return "CLASS";
            case "mirror":
            case "grade_mirror":
                return "MIRROR";
            case "random":
            case "grade_random":
                return "RANDOM";
            case "no_speed":
                return "NO_SPEED";
            case "no_good":
                return "NO_GOOD";
            case "no_great":
                return "NO_GREAT";
            case "gauge_lr2":
                return "GAUGE_LR2";
            case "gauge_5k":
                return "GAUGE_5KEYS";
            case "gauge_7k":
                return "GAUGE_7KEYS";
            case "gauge_9k":
                return "GAUGE_9KEYS";
            case "gauge_24k":
                return "GAUGE_24KEYS";
            case "ln":
                return "LN";
            case "cn":
                return "CN";
            case "hcn":
                return "HCN";
            default:
                return null;
        }
    }

    private static JArray BuildCourseTrophies(JArray source)
    {
        JArray trophies = new JArray();
        if (source == null)
        {
            return trophies;
        }
        foreach (JToken item in source)
        {
            if (!(item is JObject trophy))
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
        ExportResult result = new ExportResult();
        lock (ManifestLock)
        {
            ManifestState previous = ReadManifest(tablePath);
            result.PreviousManagedTables.AddRange(previous.Playlists.Values.Select(CloneManagedTableUrlEntry));
            if (cleanupStaleManagedFiles)
            {
                foreach (string fileName in previous.Files.Where((string fileName) => !currentFiles.Contains(fileName)))
                {
                    TryDeleteFile(Path.Combine(tablePath, fileName));
                }
            }
            result.CurrentManagedTables.AddRange((playlists ?? new Dictionary<string, ManifestPlaylistEntry>(StringComparer.Ordinal)).Values.Select(CloneManagedTableUrlEntry));
            WriteManifest(tablePath, currentFiles, playlists);
        }
        return result;
    }

    private static void AddManagedFile(string tablePath, string fileName, string playlistIdentity, JObject tableData)
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
                manifest.Playlists[playlistIdentity] = CreateManifestPlaylistEntry(playlistIdentity, fileName, tableData);
            }
            WriteManifest(tablePath, manifest.Files, manifest.Playlists);
        }
    }

    private static ManifestState ReadManifest(string tablePath)
    {
        ManifestState state = new ManifestState();
        string manifestPath = Path.Combine(tablePath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return state;
        }
        try
        {
            JObject manifest = JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            foreach (JToken item in manifest["files"] as JArray ?? new JArray())
            {
                string fileName = Path.GetFileName(item.ToString());
                if (!string.IsNullOrWhiteSpace(fileName) && fileName.EndsWith(".bmt", StringComparison.OrdinalIgnoreCase))
                {
                    state.Files.Add(fileName);
                }
            }
            JObject playlists = manifest["playlists"] as JObject;
            if (playlists != null)
            {
                foreach (JProperty property in playlists.Properties())
                {
                    JObject value = property.Value as JObject;
                    string fileName = Path.GetFileName(value?.Value<string>("file"));
                    string url = value?.Value<string>("url");
                    if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(fileName) && fileName.EndsWith(".bmt", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(url))
                    {
                        state.Playlists[property.Name] = new ManifestPlaylistEntry
                        {
                            PlaylistIdentity = property.Name,
                            FileName = fileName,
                            Url = url,
                            Name = value.Value<string>("name") ?? string.Empty
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
        JObject manifest = new JObject
        {
            ["files"] = new JArray((fileNames ?? Enumerable.Empty<string>()).Where((string fileName) => !string.IsNullOrWhiteSpace(fileName)).OrderBy((string fileName) => fileName, StringComparer.OrdinalIgnoreCase))
        };
        if (playlists != null && playlists.Count > 0)
        {
            JObject playlistJson = new JObject();
            foreach (KeyValuePair<string, ManifestPlaylistEntry> item in playlists.OrderBy((KeyValuePair<string, ManifestPlaylistEntry> item) => item.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(item.Key) && item.Value != null && !string.IsNullOrWhiteSpace(item.Value.FileName) && !string.IsNullOrWhiteSpace(item.Value.Url))
                {
                    playlistJson[item.Key] = new JObject
                    {
                        ["file"] = item.Value.FileName,
                        ["url"] = item.Value.Url,
                        ["name"] = item.Value.Name ?? string.Empty
                    };
                }
            }
            manifest["playlists"] = playlistJson;
        }
        File.WriteAllText(Path.Combine(tablePath, ManifestFileName), manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
    }

    private static ManifestPlaylistEntry CreateManifestPlaylistEntry(string playlistIdentity, string fileName, JObject tableData)
    {
        return new ManifestPlaylistEntry
        {
            PlaylistIdentity = playlistIdentity,
            FileName = fileName,
            Url = tableData?.Value<string>("url") ?? string.Empty,
            Name = tableData?.Value<string>("name") ?? string.Empty
        };
    }

    private static ManagedTableUrlEntry CloneManagedTableUrlEntry(ManagedTableUrlEntry entry)
    {
        return new ManagedTableUrlEntry
        {
            PlaylistIdentity = entry?.PlaylistIdentity,
            FileName = entry?.FileName,
            Url = entry?.Url,
            Name = entry?.Name
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
        return values?.FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value));
    }
}
