using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>所有プロセスの実行境界です。引数をshell文字列へ連結しません。</summary>
internal delegate Task<ProcessCompletion> ExecuteCommand(string executable, IReadOnlyList<string> arguments, ProcessDeadline deadline);

/// <summary>全体またはGitの変更集合を対象として、style修正からwhitespace修正へ進めます。</summary>
internal static class RepositoryFormatter
{
    /// <summary>全体適用は各formatterを一回、変更範囲はGitで集合を確定して適用します。手動入口の復元も同じ期限を使い、変更C#ゼロなら復元・整形を省きます。</summary>
    internal static async Task FormatAsync(string repository, ExecuteCommand execute, ProcessDeadline deadline, bool allSources = false, bool restore = false)
    {
        if (allSources)
        {
            await RestoreAsync();
            await FormatCategoryAsync("style", []);
            await FormatCategoryAsync("whitespace", []);
            return;
        }

        IReadOnlyList<string> paths = await GetChangedPathsAsync(repository, execute, deadline);
        if (paths.Count == 0)
        {
            return;
        }

        await RestoreAsync();
        IReadOnlyList<IReadOnlyList<string>> batches = CreateBatches(paths);
        foreach (string category in new[] { "style", "whitespace" })
        {
            foreach (IReadOnlyList<string> batch in batches)
            {
                await FormatCategoryAsync(category, ["--include", .. batch]);
            }
        }

        async Task RestoreAsync()
        {
            if (!restore) { return; }
            ProcessCompletion result = await execute("dotnet", ["restore", Path.Combine(repository, "BeMusicSeeker.sln"),
                "--locked-mode", "--disable-build-servers", "-p:Platform=x64"], deadline);
            RequireSuccess(result, "整形前の復元");
        }

        async Task FormatCategoryAsync(string category, IReadOnlyList<string> includes)
        {
            string[] prefix = category == "style"
                ? ["format", "style", Path.Combine(repository, "BeMusicSeeker.sln"), "--severity", "error", "--no-restore"]
                : ["format", "whitespace", repository, "--folder"];
            // folder適用でも、root直下とネストした生成先の通常C#を変更しません。
            ProcessCompletion result = await execute("dotnet", [.. prefix, "--verbosity", "minimal", "--exclude",
                "**/bin/**", "**/obj/**", "**/.tmp/**", "**/artifacts/verification/**", .. includes], deadline);
            RequireSuccess(result, "自動整形");
        }
    }

    /// <summary>NUL区切りの未stage・stage済み・未追跡の和集合から、存在する変更C#を返します。</summary>
    internal static async Task<IReadOnlyList<string>> GetChangedPathsAsync(string repository, ExecuteCommand execute, ProcessDeadline deadline)
    {
        string[][] queries = [["diff", "--name-only", "--diff-filter=d", "-z", "--"],
            ["diff", "--cached", "--name-only", "--diff-filter=d", "-z", "--"],
            ["ls-files", "--others", "--exclude-standard", "-z", "--"]];
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string[] query in queries)
        {
            ProcessCompletion result = await execute("git", query, deadline);
            RequireSuccess(result, "変更ファイルの取得");
            foreach (string path in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                string normalized = path.Replace('\\', '/');
                string[] segments = normalized.Split('/');
                bool generated = segments.Any(segment => segment is "bin" or "obj" or ".tmp")
                    || ("/" + normalized).Contains("/artifacts/verification/", StringComparison.Ordinal);
                if (!generated && normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(repository, path)))
                {
                    paths.Add("./" + normalized);
                }
            }
        }
        return paths.ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<string>> CreateBatches(IReadOnlyList<string> paths)
    {
        var batches = new List<IReadOnlyList<string>>();
        var batch = new List<string>();
        int length = 0;
        // prefixとquote時の伸長にも余裕を残し、Windowsの32,767文字制限を超えません。
        foreach (string path in paths)
        {
            int escapedLength = path.Length * 2 + 3;
            if (escapedLength > 24000)
            {
                throw new InvalidOperationException("整形対象のpathがcommand line上限を超えます。");
            }

            if (length + escapedLength > 24000)
            {
                batches.Add(batch.ToArray());
                batch.Clear();
                length = 0;
            }
            batch.Add(path);
            length += escapedLength;
        }
        if (batch.Count > 0)
        {
            batches.Add(batch.ToArray());
        }

        return batches;
    }

    private static void RequireSuccess(ProcessCompletion result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{operation}に失敗しました。exit={result.ExitCode}\n{result.StandardError}");
        }
    }
}
