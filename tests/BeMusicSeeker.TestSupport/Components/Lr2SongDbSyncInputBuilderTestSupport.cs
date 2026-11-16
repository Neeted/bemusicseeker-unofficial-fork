using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class Lr2SongDbSyncInputBuilderTestSupport
{
    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal enum EnumerationCategory
    {
        Lr2Folder,
        TextMetadata,
        Directories
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class DeterministicRootFileEnumerator : IRootFileEnumerator
    {
        private readonly IReadOnlyDictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>> entriesByCategory;

        internal DeterministicRootFileEnumerator(
            IReadOnlyDictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>> entriesByCategory)
        {
            var snapshot = new Dictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>>();
            foreach (KeyValuePair<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>> pair in entriesByCategory
                ?? new Dictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>>())
            {
                snapshot[pair.Key] = [.. (pair.Value ?? [])];
            }

            this.entriesByCategory = snapshot;
        }

        internal List<EnumerationCategory> RequestedCategories { get; } = [];

        public RootFileEnumerationResult EnumerateFiles(
            IEnumerable<string> rootDirectories,
            IEnumerable<RootFileEnumerationGroup> groups,
            bool verboseLog = false)
        {
            var result = new RootFileEnumerationResult
            {
                Success = true,
                IsComplete = true,
                BackendName = "deterministic-test"
            };
            foreach (RootFileEnumerationGroup group in groups ?? [])
            {
                EnumerationCategory category = Classify(group);
                RequestedCategories.Add(category);
                result.InitializeGroup(group.Name);
                if (!entriesByCategory.TryGetValue(category, out IReadOnlyList<RootFileEnumerationEntry>? entries))
                {
                    continue;
                }

                foreach (RootFileEnumerationEntry entry in entries)
                {
                    result.AddEntry(group.Name, entry);
                }
            }

            return result;
        }

        private static EnumerationCategory Classify(RootFileEnumerationGroup group)
        {
            if (group.IncludeDirectories)
            {
                return EnumerationCategory.Directories;
            }

            if (group.Extensions.Any(extension => string.Equals(extension, ".lr2folder", StringComparison.OrdinalIgnoreCase)))
            {
                return EnumerationCategory.Lr2Folder;
            }

            if (group.Extensions.Length > 0)
            {
                return EnumerationCategory.TextMetadata;
            }

            throw new InvalidOperationException("Unsupported deterministic enumeration group.");
        }
    }

}
