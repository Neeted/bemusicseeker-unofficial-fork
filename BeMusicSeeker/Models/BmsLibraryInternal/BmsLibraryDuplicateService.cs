using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryDuplicateService
{
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
        DuplicateAnalysisResult result = new DuplicateAnalysisResult();
        List<IGrouping<string, BMSFile>> duplicateHashGroups = (snapshot ?? new List<BMSFile>())
            .Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.hash))
            .GroupBy((BMSFile file) => file.hash, StringComparer.OrdinalIgnoreCase)
            .Where((IGrouping<string, BMSFile> group) => group.Count() > 1)
            .ToList();
        foreach (IGrouping<string, BMSFile> duplicateHashGroup in duplicateHashGroups)
        {
            foreach (BMSFile item in duplicateHashGroup)
            {
                result.DuplicateFiles.Add(item);
            }
        }

        Dictionary<string, int> dirToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<string> idToDir = new List<string>();
        List<List<int>> groupIndices = new List<List<int>>();
        foreach (IGrouping<string, BMSFile> duplicateHashGroup2 in duplicateHashGroups)
        {
            List<int> currentGroup = new List<int>();
            foreach (BMSFile bmsInfo in duplicateHashGroup2)
            {
                string dir = DirectoryExt.GetDirectoryNameSimple(bmsInfo.path);
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
        foreach (BMSFile bms in snapshot ?? new List<BMSFile>())
        {
            string dir = DirectoryExt.GetDirectoryNameSimple(bms.path);
            if (!filesByDir.TryGetValue(dir, out List<BMSFile> list))
            {
                list = new List<BMSFile>();
                filesByDir[dir] = list;
            }
            list.Add(bms);
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
