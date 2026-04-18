using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryDuplicateService
{
    public List<DuplicateChartRow> BuildSnapshot(IEnumerable<BMSFile> bmsFiles, IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<DuplicateChartRow> rows = new List<DuplicateChartRow>();
        rows.AddRange((bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Select(DuplicateChartRow.CreateFromBmsFile)
            .Where((DuplicateChartRow row) => row != null));
        rows.AddRange((bmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            .Select(DuplicateChartRow.CreateFromBmsonSong)
            .Where((DuplicateChartRow row) => row != null));
        return rows;
    }

    public void ClearDuplicateState(IEnumerable<BMSFile> files, string duplicateWarningMessage)
    {
        foreach (BMSFile file in files ?? Enumerable.Empty<BMSFile>())
        {
            file.IsHashDuplicated = false;
            file.warning = RemoveDuplicateWarning(file.warning, duplicateWarningMessage);
        }
    }

    public void ApplyDuplicateWarnings(IEnumerable<BMSFile> files, string duplicateWarningMessage)
    {
        foreach (BMSFile file in files ?? Enumerable.Empty<BMSFile>())
        {
            if (file == null)
            {
                continue;
            }
            file.IsHashDuplicated = true;
            if (HasDuplicateWarning(file.warning, duplicateWarningMessage))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(file.warning))
            {
                file.warning = duplicateWarningMessage;
            }
            else
            {
                file.warning = file.warning + Environment.NewLine + duplicateWarningMessage;
            }
        }
    }

    public DuplicateAnalysisResult Analyze(List<BMSFile> snapshot)
    {
        return Analyze((snapshot ?? new List<BMSFile>())
            .Select(DuplicateChartRow.CreateFromBmsFile)
            .Where((DuplicateChartRow row) => row != null)
            .ToList());
    }

    public DuplicateAnalysisResult Analyze(IEnumerable<DuplicateChartRow> snapshot)
    {
        DuplicateAnalysisResult result = new DuplicateAnalysisResult();
        List<DuplicateChartRow> snapshotRows = (snapshot ?? Enumerable.Empty<DuplicateChartRow>())
            .Where((DuplicateChartRow row) => row != null && row.DisplayRow != null && !string.IsNullOrWhiteSpace(row.LookupHash))
            .ToList();
        List<IGrouping<string, DuplicateChartRow>> duplicateHashGroups = snapshotRows
            .GroupBy((DuplicateChartRow row) => row.LookupHash, StringComparer.OrdinalIgnoreCase)
            .Where((IGrouping<string, DuplicateChartRow> group) => group.Count() > 1)
            .ToList();
        foreach (IGrouping<string, DuplicateChartRow> duplicateHashGroup in duplicateHashGroups)
        {
            foreach (DuplicateChartRow item in duplicateHashGroup)
            {
                result.DuplicateFiles.Add(item.DisplayRow);
            }
        }

        Dictionary<string, int> dirToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<string> idToDir = new List<string>();
        List<List<int>> groupIndices = new List<List<int>>();
        foreach (IGrouping<string, DuplicateChartRow> duplicateHashGroup2 in duplicateHashGroups)
        {
            List<int> currentGroup = new List<int>();
            foreach (DuplicateChartRow duplicateRow in duplicateHashGroup2)
            {
                string dir = duplicateRow.DirectoryPath;
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }
                if (!dirToId.TryGetValue(dir, out int id))
                {
                    id = idToDir.Count;
                    dirToId[dir] = id;
                    idToDir.Add(dir);
                }
                currentGroup.Add(id);
            }
            if (currentGroup.Count > 0)
            {
                groupIndices.Add(currentGroup);
            }
        }

        int[] parent = Enumerable.Range(0, idToDir.Count).ToArray();
        foreach (List<int> group in groupIndices)
        {
            if (group.Count <= 1)
            {
                continue;
            }
            int first = group[0];
            for (int i = 1; i < group.Count; i++)
            {
                int root1 = FindRoot(parent, first);
                int root2 = FindRoot(parent, group[i]);
                if (root1 != root2)
                {
                    parent[root1] = root2;
                }
            }
        }

        Dictionary<int, HashSet<string>> rootViewToDirs = new Dictionary<int, HashSet<string>>();
        for (int i = 0; i < idToDir.Count; i++)
        {
            int root = FindRoot(parent, i);
            if (!rootViewToDirs.TryGetValue(root, out HashSet<string> set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                rootViewToDirs[root] = set;
            }
            set.Add(idToDir[i]);
        }

        Dictionary<string, List<BMSFile>> filesByDir = new Dictionary<string, List<BMSFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateChartRow row in snapshotRows)
        {
            string dir = row.DirectoryPath;
            if (!filesByDir.TryGetValue(dir, out List<BMSFile> list))
            {
                list = new List<BMSFile>();
                filesByDir[dir] = list;
            }
            list.Add(row.DisplayRow);
        }

        foreach (HashSet<string> dirs in rootViewToDirs.Values)
        {
            List<BMSFile> groupFiles = new List<BMSFile>();
            foreach (string directoryPath in dirs)
            {
                if (filesByDir.TryGetValue(directoryPath, out List<BMSFile> list))
                {
                    groupFiles.AddRange(list);
                }
            }
            if (groupFiles.Count > 0)
            {
                result.DuplicateGroups.Add(new DuplicateGroup(groupFiles, dirs.ToList()));
            }
        }
        result.DuplicateGroups.Sort((x, y) => string.Compare(x.Header, y.Header, StringComparison.Ordinal));
        return result;
    }

    private static int FindRoot(int[] parent, int node)
    {
        int root = node;
        while (parent[root] != root)
        {
            parent[root] = parent[parent[root]];
            root = parent[root];
        }
        return root;
    }

    private static bool HasDuplicateWarning(string warning, string duplicateWarningMessage)
    {
        return warning?.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any((string line) => string.Equals(line.Trim(), duplicateWarningMessage, StringComparison.Ordinal)) ?? false;
    }

    private static string RemoveDuplicateWarning(string warning, string duplicateWarningMessage)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return string.Empty;
        }
        return string.Join(Environment.NewLine, warning.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select((string line) => line.Trim())
            .Where((string line) => !string.Equals(line, duplicateWarningMessage, StringComparison.Ordinal))
            .ToArray());
    }
}
