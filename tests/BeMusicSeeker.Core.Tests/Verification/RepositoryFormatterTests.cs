using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>実Gitの変更集合と外部formatterへの受渡しを検査します。</summary>
[TestClass]
public sealed class RepositoryFormatterTests
{
    /// <summary>stage・未stage・未追跡の和集合から生成先と削除を除き、日本語・空白・renameのpathをそのまま受け渡します。</summary>
    [TestMethod]
    public async Task ChangedFilesAreSelectedOnceWithLiteralPaths()
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        File.WriteAllText(repository.Path("base.cs"), "changed");
        File.Move(repository.Path("removed.cs"), repository.Path("名前 [変更].cs"));
        await repository.GitAsync("add", "--all");
        File.AppendAllText(repository.Path("base.cs"), "unstaged");
        foreach (string path in new[] { "未追跡 空白.cs", "obj/generated.cs", "bin/generated.cs", ".tmp/generated.cs", "artifacts/verification/generated.cs", "note.md" })
        {
            repository.Write(path);
        }

        await RepositoryFormatter.FormatAsync(repository.Root, repository.ExecuteAsync, Deadline());
        Assert.AreEqual(2, repository.Formats.Count);
        CollectionAssert.AreEqual(new[] { "./base.cs", "./名前 [変更].cs", "./未追跡 空白.cs" }, Includes(repository.Formats[0]));
        CollectionAssert.AreEqual(Includes(repository.Formats[0]), Includes(repository.Formats[1]));
        Assert.AreEqual("style", repository.Formats[0][1]);
        Assert.AreEqual("whitespace", repository.Formats[1][1]);
        Assert.IsTrue(repository.Formats[0].Contains("--no-restore"));
        Assert.IsTrue(repository.Formats[1].Contains("--folder"));
        Assert.IsFalse(repository.Formats[1].Contains("--no-restore"));
    }

    /// <summary>変更C#ゼロではformatterを起動せず、空includeによる全体整形への拡大を検出します。</summary>
    [TestMethod]
    public async Task NoChangedCSharpDoesNotStartFormatter()
    {
        using var repository = new Repository();
        await repository.InitializeAsync();
        repository.Write("notes.md");
        await RepositoryFormatter.FormatAsync(repository.Root, repository.ExecuteAsync, Deadline());
        Assert.AreEqual(0, repository.Formats.Count);
    }

    /// <summary>長いpath集合を分割しても全styleが先に完了し、途中失敗ではwhitespaceへ進まないことを保証します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AllStyleBatchesPrecedeWhitespaceAndFailureStops(bool fail)
    {
        using var repository = new Repository { FailStyle = fail };
        await repository.InitializeAsync();
        for (int index = 0; index < 180; index++)
        {
            repository.Write($"long-{index:D3}-{new string('x', 90)}.cs");
        }

        if (fail)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryFormatter.FormatAsync(repository.Root, repository.ExecuteAsync, Deadline()));
            Assert.AreEqual(1, repository.Formats.Count);
            Assert.AreEqual("style", repository.Formats[0][1]);
        }
        else
        {
            await RepositoryFormatter.FormatAsync(repository.Root, repository.ExecuteAsync, Deadline());
            int styleCount = repository.Formats.TakeWhile(arguments => arguments[1] == "style").Count();
            Assert.IsTrue(styleCount > 1);
            Assert.AreEqual(styleCount * 2, repository.Formats.Count);
            Assert.IsTrue(repository.Formats.Skip(styleCount).All(arguments => arguments[1] == "whitespace"));
            Assert.AreEqual(180, repository.Formats.Take(styleCount).Sum(arguments => Includes(arguments).Length));
        }
    }

    private static string[] Includes(string[] arguments) => arguments.Skip(Array.IndexOf(arguments, "--include") + 1).ToArray();
    private static ProcessDeadline Deadline() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));

    private sealed class Repository : IDisposable
    {
        private readonly TestWorkspace workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        internal string Root => workspace.RootDirectory;
        internal List<string[]> Formats { get; } = [];
        internal bool FailStyle { get; init; }
        internal string Path(string relative) => workspace.GetPath(relative);
        internal void Write(string relative)
        {
            string path = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
            File.WriteAllText(path, "fixture");
        }
        internal async Task InitializeAsync()
        {
            await GitAsync("init");
            Write("base.cs"); Write("removed.cs");
            await GitAsync("add", "--all");
            await GitAsync("-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path("hooks"), "commit", "-m", "fixture");
        }
        internal Task<ProcessCompletion> GitAsync(params string[] arguments) => ExecuteAsync("git", arguments, Deadline());
        internal async Task<ProcessCompletion> ExecuteAsync(string executable, IReadOnlyList<string> arguments, ProcessDeadline deadline)
        {
            if (executable == "dotnet")
            {
                Formats.Add(arguments.ToArray());
                return new ProcessCompletion(FailStyle && arguments[1] == "style" ? 1 : 0, "", "");
            }
            await using var process = OwnedProcess.Start(new ProcessStartRequest(executable, arguments, Root, ownershipPolicy: ProcessOwnershipPolicy.Preparation));
            ProcessCompletion result = await process.WaitForExitAsync(deadline);
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            return result;
        }
        public void Dispose()
        {
            // Gitが自分で作ったobjectを読取り専用にするため、このfixtureのobjectだけを削除可能へ戻します。
            string objects = Path(".git/objects");
            if (Directory.Exists(objects))
            {
                foreach (string file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                }
            }
            workspace.Dispose();
        }
    }
}
