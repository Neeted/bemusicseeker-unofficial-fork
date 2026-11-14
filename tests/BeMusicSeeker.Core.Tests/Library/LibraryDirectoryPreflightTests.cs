using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using Newtonsoft.Json;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryDirectoryPreflightTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>ケース開始後だけ利用できる、可変ファイル領域の所有者です。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>constructorと既存field準備の成功後にケース専有領域を取得します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>既存finallyと接続・Task終端の後にケース専有領域を回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>
    /// 保証：登録済みの未接続ルートも設定の読み書きで保持し、存在しないという理由で利用者の登録を消さない。
    /// </summary>
    [TestMethod]
    public void StandaloneConfiguredAdapterPreservesMissingRootsOnRoundTrip()
    {
        string missing = Path.Combine(
            CaseWorkspace.RootDirectory,
            nameof(LibraryDirectoryPreflightTests),
            "missing-" + Guid.NewGuid().ToString("N"));

        IReadOnlyList<string> loaded = StandaloneBmsRootPathSettings.Deserialize(missing);
        string serialized = StandaloneBmsRootPathSettings.Serialize(loaded);
        IReadOnlyList<string> reloaded = StandaloneBmsRootPathSettings.Deserialize(serialized);

        CollectionAssert.AreEqual(new[] { Path.GetFullPath(missing) }, loaded.ToArray());
        CollectionAssert.AreEqual(new[] { Path.GetFullPath(missing) }, reloaded.ToArray());
    }

    /// <summary>
    /// 保証：LR2の検索先読取りでドライブ直下を保持し、相対ルートはLR2基準へ解決して変更追跡対象を取り違えない。
    /// </summary>
    [TestMethod]
    public void Lr2RawSearchRootGetterPreservesDriveRootAndResolvesRelativeRoot()
    {
        string root = CreateTemporaryDirectory();
        string configDirectory = Path.Combine(root, "LR2files", "Config");
        string configPath = Path.Combine(configDirectory, "config.xml");
        Directory.CreateDirectory(configDirectory);
        string driveRoot = Path.GetPathRoot(Path.GetFullPath(Environment.SystemDirectory))!;
        File.WriteAllText(
            configPath,
            "<config><jukebox><path>relative\\</path><path>"
            + driveRoot
            + "</path></jukebox></config>");
        try
        {
            LR2Config config = TestLr2ConfigFactory.Load(configPath);
            List<string> roots = config.GetBMSSearchDirectoriesForChangeTracking();

            CollectionAssert.Contains(roots, Path.Combine(root, "relative"));
            CollectionAssert.Contains(roots, driveRoot);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>
    /// 保証：追加出力先の保存文字列にあるドライブ直下を検査要求へ保持し、末尾区切り除去で別パスへ変えない。
    /// </summary>
    [TestMethod]
    public void RawAdditionalOutputDriveRootPreservesRootDuringRequestCapture()
    {
        string driveRoot = Path.GetPathRoot(Path.GetFullPath(Environment.SystemDirectory))!;
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2CustomFolderAdditionalOutputBaseDirsSerialized =
                JsonConvert.SerializeObject(new[] { driveRoot })
        };

        LibraryDirectoryPreflightRequest request = new LibraryDirectoryPreflightService()
            .CreateRequest([], [], options);

        LibraryDirectoryPreflightOutputBase target = request.OutputBaseTargets.Single();
        Assert.AreEqual(LibraryDirectoryPreflightOutputBaseKind.Additional, target.Kind);
        Assert.AreEqual(driveRoot, target.DirectoryPath);
    }

    /// <summary>
    /// 保証：LR2連携の事前検査は、空のBMSルートと、読取り専用属性・既存内容・空を含む通常・追加・ROOTの各出力基点を成功させ、
    /// いずれの基点と配下にも項目を作成・削除・書込みせず、更新日時を変えない（LR2 folder.date との整合）。
    /// </summary>
    /// <remarks>代替実装では System.IO を直接呼ぶ書込みを見逃すため、本番の既定の filesystem 境界と実ディレクトリで観測する。</remarks>
    [TestMethod]
    public void LinkedModePreflight_SucceedsWithoutChangingBmsRootOrAnyOutputBase()
    {
        string root = CreateTemporaryDirectory();
        string bmsRoot = Path.Combine(root, "bms");
        string normal = Path.Combine(root, "normal");
        string additional = Path.Combine(root, "additional");
        string rootType = Path.Combine(root, "root-type");
        string[] inspectedDirectories = [bmsRoot, normal, additional, rootType];
        foreach (string directory in inspectedDirectories)
        {
            Directory.CreateDirectory(directory);
        }
        foreach (string populatedBase in new[] { normal, rootType })
        {
            File.WriteAllBytes(Path.Combine(populatedBase, "existing-sentinel.txt"), [0x10, 0x20, 0x30]);
            string tableDirectory = Path.Combine(populatedBase, "table");
            Directory.CreateDirectory(tableDirectory);
            File.WriteAllBytes(Path.Combine(tableDirectory, "0000.lr2folder"), [0x41, 0x42]);
        }
        var fixedPastTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        try
        {
            List<string> allDirectories = [.. inspectedDirectories.SelectMany(directory =>
                Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).Prepend(directory))];
            foreach (string directory in allDirectories)
            {
                Directory.SetLastWriteTimeUtc(directory, fixedPastTime);
            }
            File.SetAttributes(normal, File.GetAttributes(normal) | FileAttributes.ReadOnly);
            foreach (string directory in allDirectories)
            {
                // 作成と削除で項目集合が元へ戻る書込みも検出できるよう、検査前の時刻が固定値であることを確かめます。
                Assert.AreEqual(fixedPastTime, Directory.GetLastWriteTimeUtc(directory), directory);
            }
            string[] before = CaptureTreeState(inspectedDirectories);
            LibraryDirectoryPreflightService service = new();
            LibraryDirectoryPreflightRequest request = service.CreateRequest(
                [bmsRoot],
                [bmsRoot],
                CreateLinkedOptions(normal, additional, rootType));

            service.EnsureAvailable(request);

            Assert.AreEqual(3, request.OutputBaseTargets.Count);
            CollectionAssert.AreEqual(before, CaptureTreeState(inspectedDirectories));
        }
        finally
        {
            if (Directory.Exists(normal))
            {
                File.SetAttributes(normal, File.GetAttributes(normal) & ~FileAttributes.ReadOnly);
            }
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>
    /// 保証：欠落ルートと通常ファイルを別原因の型付き停止として返し、対象用途と正規パスを診断へ保持する。
    /// </summary>
    [TestMethod]
    public void MissingAndFileRootsProduceTypedFailuresWithCause()
    {
        string root = CreateTemporaryDirectory();
        string missing = Path.Combine(root, "missing");
        string filePath = Path.Combine(root, "root-file");
        File.WriteAllText(filePath, "file");
        try
        {
            LibraryDirectoryPreflightService service = new();
            LibraryDirectoryPreflightException missingFailure = Assert.ThrowsExactly<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(
                    service.CreateRequest([missing], [missing], new BmsLibraryOptionsSnapshot())));
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.NotFound, missingFailure.Cause);
            Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, missingFailure.Use);
            Assert.AreEqual(Path.GetFullPath(missing), missingFailure.DirectoryPath);

            LibraryDirectoryPreflightException fileFailure = Assert.ThrowsExactly<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(
                    service.CreateRequest([filePath], [filePath], new BmsLibraryOptionsSnapshot())));
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.NotDirectory, fileFailure.Cause);
            Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, fileFailure.Use);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>
    /// 保証：BMSルート直下の列挙開始が権限不足で失敗した場合、BMSルート用途・AccessDenied の型付き失敗として返し、
    /// 読取り不能を空成功へ変換しない。
    /// </summary>
    [TestMethod]
    public void BmsRootEnumerationFailureIsTyped()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var fileSystem = new EnumerationFailurePreflightFileSystem
            {
                EnumerationFailure = new UnauthorizedAccessException("enumeration denied")
            };
            LibraryDirectoryPreflightService service = new(fileSystem);
            LibraryDirectoryPreflightException failure = Assert.ThrowsExactly<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(
                    service.CreateRequest([root], [root], new BmsLibraryOptionsSnapshot())));

            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.AccessDenied, failure.Cause);
            Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, failure.Use);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>
    /// 保証：壊れた追加出力先JSONは設定不備として返し、空出力先に読み替えて検査成功にしない。
    /// </summary>
    [TestMethod]
    public void MalformedAdditionalOutputConfigurationIsNotTreatedAsEmpty()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderAdditionalOutputBaseDirsSerialized = "{malformed-json"
            };

            LibraryDirectoryPreflightException failure = Assert.ThrowsExactly<LibraryDirectoryPreflightException>(
                () => new LibraryDirectoryPreflightService().CreateRequest([root], [root], options));

            Assert.AreEqual(LibraryDirectoryPreflightUse.Lr2OutputBase, failure.Use);
            Assert.AreEqual(LibraryDirectoryPreflightOutputBaseKind.Additional, failure.OutputBaseKind);
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.InvalidConfiguration, failure.Cause);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>
    /// 保証：LR2連携では通常・追加・ROOTの各出力基点を読取り検査し、欠落した基点はその種別・NotFound の型付き失敗として返し、
    /// 自動作成しない。
    /// </summary>
    /// <remarks>DataRow は欠落させる基点の種別。他の二つの基点とBMSルートは実在させ、検査から漏れた種別を区別する。</remarks>
    [TestMethod]
    [DataRow(nameof(LibraryDirectoryPreflightOutputBaseKind.Normal))]
    [DataRow(nameof(LibraryDirectoryPreflightOutputBaseKind.Additional))]
    [DataRow(nameof(LibraryDirectoryPreflightOutputBaseKind.RootType))]
    public void LinkedModeInspectsEveryOutputBaseAndDoesNotCreateMissingBase(string missingKindName)
    {
        LibraryDirectoryPreflightOutputBaseKind missingKind =
            Enum.Parse<LibraryDirectoryPreflightOutputBaseKind>(missingKindName);
        string root = CreateTemporaryDirectory();
        string bmsRoot = Path.Combine(root, "bms");
        var outputBases = new Dictionary<LibraryDirectoryPreflightOutputBaseKind, string>
        {
            [LibraryDirectoryPreflightOutputBaseKind.Normal] = Path.Combine(root, "normal"),
            [LibraryDirectoryPreflightOutputBaseKind.Additional] = Path.Combine(root, "additional"),
            [LibraryDirectoryPreflightOutputBaseKind.RootType] = Path.Combine(root, "root-type")
        };
        Directory.CreateDirectory(bmsRoot);
        foreach (KeyValuePair<LibraryDirectoryPreflightOutputBaseKind, string> outputBase in outputBases)
        {
            if (outputBase.Key != missingKind)
            {
                Directory.CreateDirectory(outputBase.Value);
            }
        }
        string missingBase = outputBases[missingKind];
        try
        {
            BmsLibraryOptionsSnapshot options = CreateLinkedOptions(
                outputBases[LibraryDirectoryPreflightOutputBaseKind.Normal],
                outputBases[LibraryDirectoryPreflightOutputBaseKind.Additional],
                outputBases[LibraryDirectoryPreflightOutputBaseKind.RootType]);
            LibraryDirectoryPreflightService service = new();
            LibraryDirectoryPreflightRequest request = service.CreateRequest(
                [bmsRoot],
                [bmsRoot],
                options);

            LibraryDirectoryPreflightException failure = Assert.ThrowsExactly<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(request));

            Assert.AreEqual(LibraryDirectoryPreflightUse.Lr2OutputBase, failure.Use);
            Assert.AreEqual(missingKind, failure.OutputBaseKind);
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.NotFound, failure.Cause);
            Assert.AreEqual(Path.GetFullPath(missingBase), failure.DirectoryPath);
            Assert.IsFalse(Directory.Exists(missingBase));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>
    /// 保証：単独動作では使わないLR2出力基点を検査・作成せず、BMSルートだけで利用を開始できる。
    /// </summary>
    [TestMethod]
    public void StandaloneModeIgnoresUnusedMissingOutputBases()
    {
        string root = CreateTemporaryDirectory();
        string bmsRoot = Path.Combine(root, "bms");
        Directory.CreateDirectory(bmsRoot);
        try
        {
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = false,
                LR2CustomFolderOutputBaseDir = Path.Combine(root, "unused")
            };
            LibraryDirectoryPreflightService service = new();
            LibraryDirectoryPreflightRequest request = service.CreateRequest(
                [bmsRoot],
                [bmsRoot],
                options);

            service.EnsureAvailable(request);

            Assert.AreEqual(0, request.OutputBaseTargets.Count);
            Assert.IsFalse(Directory.Exists(options.LR2CustomFolderOutputBaseDir));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>
    /// 保証：管理出力の未作成子フォルダは独立した必須検索ルートから除き、基点の検査成功を子の欠落で止めない。
    /// </summary>
    [TestMethod]
    public void ManagedOutputChildIsNotRequiredAsASeparateRoot()
    {
        string root = CreateTemporaryDirectory();
        string normal = Path.Combine(root, "normal");
        string managedRoot = Path.Combine(root, "managed");
        string uncreatedChild = Path.Combine(managedRoot, "playlist-child");
        Directory.CreateDirectory(normal);
        Directory.CreateDirectory(managedRoot);
        try
        {
            BmsLibraryOptionsSnapshot options = CreateLinkedOptions(normal, additional: null, rootType: managedRoot);
            LibraryDirectoryPreflightService service = new();
            LibraryDirectoryPreflightRequest request = service.CreateRequest(
                [normal, managedRoot, uncreatedChild],
                [normal],
                options);

            service.EnsureAvailable(request);

            Assert.IsFalse(Directory.Exists(uncreatedChild));
            CollectionAssert.AreEqual(
                new[] { Path.GetFullPath(normal) },
                request.ScanRootDirectories.ToArray());
            Assert.IsTrue(request.OutputBaseTargets.Any(target =>
                string.Equals(target.DirectoryPath, Path.GetFullPath(managedRoot), StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static BmsLibraryOptionsSnapshot CreateLinkedOptions(
        string normal,
        string? additional,
        string rootType)
    {
        return new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2CustomFolderOutputBaseDir = normal,
            LR2CustomFolderAdditionalOutputBaseDirs = additional == null ? [] : [additional],
            LR2CustomFolderOutputBaseDirRootType = rootType
        };
    }

    private string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            CaseWorkspace.RootDirectory,
            nameof(LibraryDirectoryPreflightTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>対象と配下の項目集合・ファイル内容・ディレクトリの更新日時を、比較できる行の列として捕捉します。</summary>
    private static string[] CaptureTreeState(IEnumerable<string> directories)
    {
        List<string> state = [];
        foreach (string directory in directories)
        {
            state.Add("D|" + directory + "|" + Directory.GetLastWriteTimeUtc(directory).Ticks);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            {
                state.Add(Directory.Exists(entry)
                    ? "D|" + entry + "|" + Directory.GetLastWriteTimeUtc(entry).Ticks
                    : "F|" + entry + "|" + Convert.ToHexString(File.ReadAllBytes(entry)));
            }
        }
        state.Sort(StringComparer.Ordinal);
        return [.. state];
    }

    /// <summary>実 filesystem の読取りへ、直下列挙の失敗だけを注入します。</summary>
    private sealed class EnumerationFailurePreflightFileSystem : ILibraryDirectoryPreflightFileSystem
    {
        internal Exception? EnumerationFailure { get; init; }

        public FileAttributes GetAttributes(string path) => LongPathFileSystem.GetAttributes(path);

        public IEnumerable<string> EnumerateDirectoryEntries(string path)
        {
            if (EnumerationFailure != null)
            {
                throw EnumerationFailure;
            }

            foreach (string entry in LongPathFileSystem.EnumerateFileSystemEntries(path))
            {
                yield return entry;
            }
        }
    }
}
